using Godot;
using SDG.Unturned;

namespace UnturnedGodot
{
    // First-person player: ported PlayerMovementSim on Godot's 50 Hz physics tick + mouse look + a hitscan
    // gun (raycast from the camera vs the zombie collision layer). Movement CONSTANTS are exact; feel goes
    // through Jolt. Builds its own camera + capsule collider so it can be spawned from code.
    // WASD move / Shift sprint / Ctrl crouch / Space jump / LMB fire / Esc release mouse.
    public partial class PlayerController : CharacterBody3D
    {
        readonly PlayerMovementSim _move = new PlayerMovementSim();
        Camera3D _cam;
        Viewmodel _viewmodel;
        string _gunName = "eaglefire";   // gun folder name (eaglefire | maplestrike), derived from the .dat path
        float _pitchDeg;

        [Export] public float MouseSensitivity = 0.12f;
        public int Ammo = 30;
        public int Kills { get; private set; }

        public float Health = 100f;
        public float MaxHealth = 100f;
        public int Deaths;
        public Vector3 Spawn = new Vector3(0, 1f, 0);

        // When set (e.g. by a recorded demo or a net-driven bot), overrides keyboard input: x=strafe, y=forward.
        public UnityEngine.Vector2? ScriptedInput;
        public bool CaptureMouse = true;

        public GunDef Gun;          // real ItemGunAsset stats (damage/range/firerate/mag) when loaded
        float _fireCd;              // seconds until the gun can fire again
        bool _reloading;            // reloading -> can't fire; magazine refills when the timer elapses
        double _reloadTimer;
        const double ReloadTime = 1.633; // Eaglefire Gun_Reload clip length (no reload-time key in the .dat)
        float _recoilPitch, _recoilYaw;  // camera recoil offset (deg), decays back toward 0 (PlayerLook Lerp rate 4)
        readonly RandomNumberGenerator _rng = new();
        enum FireMode { Safety, Semi, Auto, Burst }   // EFiremode; the gun's available set comes from its .dat flags
        FireMode _firemode = FireMode.Semi;
        int _burstLeft;                               // rounds remaining in the current burst

        bool _dead;
        double _deathTimer;
        RiggedCharacter _corpse;

        // Zombie melee lands here; on death, drop a ragdoll corpse + third-person death-cam, then respawn.
        public void TakeDamage(float amount)
        {
            if (_dead || Health <= 0f) return;
            Health -= amount;
            if (Health <= 0f) { Deaths++; Die(); }
        }

        void Die()
        {
            _dead = true;
            _deathTimer = 3.5;
            Velocity = Vector3.Zero;

            _corpse = RiggedCharacter.Build("res://content/rig.json", new Color(0.82f, 0.66f, 0.52f));
            if (_corpse != null)
            {
                GetParent().AddChild(_corpse);
                _corpse.GlobalPosition = GlobalPosition - new Vector3(0f, 0.9f, 0f);
                _corpse.Rotation = new Vector3(0f, Rotation.Y, 0f);
                var r = new RandomNumberGenerator(); r.Randomize();
                // Unturned RagdollTool force: (dir + up*8 + randXZ +-16) * 32, applied as one physics step (~*0.02).
                Vector3 f = (-GlobalTransform.Basis.Z * 5f + Vector3.Up * 8f + new Vector3(r.RandfRange(-16f, 16f), 0f, r.RandfRange(-16f, 16f))) * 0.64f;
                _corpse.RagdollStart(f);
            }
            _viewmodel?.SetAiming(false);
            _viewmodel?.SetShown(false);   // no gun in the death-cam
            if (_cam != null)
            {
                _cam.TopLevel = true;   // hold the death-cam still in world space while the body flops
                _cam.LookAtFromPosition(GlobalPosition + new Vector3(2.2f, 2.2f, 2.8f), GlobalPosition - new Vector3(0f, 0.6f, 0f), Vector3.Up);
            }
        }

        void Respawn()
        {
            _dead = false;
            Health = MaxHealth;
            GlobalPosition = Spawn;
            Velocity = Vector3.Zero;
            _corpse?.QueueFree(); _corpse = null;
            _viewmodel?.SetShown(true);
            if (_cam != null)
            {
                _cam.TopLevel = false;
                _cam.Position = new Vector3(0f, 1.6f, 0f);
                _cam.Rotation = Vector3.Zero;
                _pitchDeg = 0f;
            }
        }

        public Camera3D Camera => _cam;

        // Load a real gun .dat (e.g. Eaglefire) through the ported UnturnedDat layer and equip it.
        public void LoadGun(string datPath)
        {
            string text;
            if (datPath.StartsWith("res://") || datPath.StartsWith("user://"))
            {
                using var f = Godot.FileAccess.Open(datPath, Godot.FileAccess.ModeFlags.Read);
                text = f?.GetAsText();
            }
            else text = System.IO.File.Exists(datPath) ? System.IO.File.ReadAllText(datPath) : null;
            if (string.IsNullOrEmpty(text)) { GD.PushError($"[gun] .dat not found: {datPath}"); return; }
            Gun = GunDef.FromDatText(text);
            _gunName = System.IO.Path.GetFileNameWithoutExtension(datPath);
            Ammo = Gun.AmmoMax;
            // reset to a valid firemode for THIS gun — don't inherit the previous one (e.g. Auto carried onto the
            // semi-only shotgun would let it hold-fire full-auto). Prefer Semi, then Auto/Burst, else Safety.
            var modes = AvailableModes();
            _firemode = System.Array.IndexOf(modes, FireMode.Semi) >= 0 ? FireMode.Semi
                      : System.Array.IndexOf(modes, FireMode.Auto) >= 0 ? FireMode.Auto
                      : modes[0];
            _burstLeft = 0;
            GD.Print($"[gun] {Gun.Id}: zombieDmg={Gun.ZombieDamage} range={Gun.Range} firerate={Gun.Firerate} mag={Gun.AmmoMax} pellets={Gun.Pellets} mode={_firemode}");
        }

        // Q toggles between the two ported guns: reload the GunDef + rebuild the per-gun viewmodel.
        void SwitchWeapon()
        {
            string next = _gunName switch { "eaglefire" => "maplestrike", "maplestrike" => "masterkey", _ => "eaglefire" };
            LoadGun($"res://content/{next}.dat");   // sets Gun + _gunName + Ammo (eaglefire -> maplestrike -> masterkey)
            _viewmodel?.QueueFree();
            _viewmodel = new Viewmodel { GunName = _gunName };
            AddChild(_viewmodel);
            GD.Print($"[gun] switched to {_gunName}");
        }

        public override void _Ready()
        {
            CollisionLayer = 1 << 3;   // player bit
            CollisionMask = 1 << 0;    // walk on ground (bit 0)

            var shape = new CollisionShape3D { Shape = new CapsuleShape3D { Height = 1.8f, Radius = 0.35f } };
            shape.Position = new Vector3(0, 0.9f, 0);
            AddChild(shape);

            _cam = new Camera3D { Position = new Vector3(0, 1.6f, 0), Current = true };
            AddChild(_cam);
            _viewmodel = new Viewmodel { GunName = _gunName };   // per-gun visuals
            AddChild(_viewmodel);
            _rng.Randomize();

            if (CaptureMouse) Input.MouseMode = Input.MouseModeEnum.Captured;
            foreach (var a in OS.GetCmdlineUserArgs()) if (a == "--pdie") _pdieTest = 2.0; // render-test: die at 2s
        }
        double _pdieTest = -1;

        public override void _UnhandledInput(InputEvent @event)
        {
            if (@event is InputEventMouseMotion mm && Input.MouseMode == Input.MouseModeEnum.Captured)
            {
                RotateY(Mathf.DegToRad(-mm.Relative.X * MouseSensitivity));
                _pitchDeg = Mathf.Clamp(_pitchDeg - mm.Relative.Y * MouseSensitivity, -89f, 89f);
                _cam.RotationDegrees = new Vector3(_pitchDeg, 0f, 0f);
            }
            else if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
                StartFire();
            else if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Right } rmb)
                _viewmodel?.SetAiming(rmb.Pressed);   // hold RMB to aim down sights (Unturned default mode)
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.R })
                StartReload();
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.V })
                CycleFiremode();
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.Q })
                SwitchWeapon();   // toggle Eaglefire <-> Maplestrike
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape })
                Input.MouseMode = Input.MouseMode == Input.MouseModeEnum.Captured
                    ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
        }

        // R to reload: block firing, then refill the magazine after the reload's duration. The reload takes the
        // Gun_Reload clip's length (the Eaglefire .dat has no separate reload-time key), so ReloadTime = that.
        void StartReload()
        {
            if (_reloading || _dead) return;
            int max = Gun?.AmmoMax ?? 30;
            if (Ammo >= max) return;
            _reloading = true;
            _reloadTimer = ReloadTime;
            _viewmodel?.SetReloading(true);
        }

        // LMB press -> fire per the current mode (safety = nothing, semi = one, burst = queue BurstCount, auto = start).
        void StartFire()
        {
            if (_firemode == FireMode.Safety) return;
            // dry-fire: trigger pulled on an empty chamber -> hammer click, no shot
            if (Ammo <= 0 && !_reloading && _fireCd <= 0f) { _viewmodel?.PlayDryFire(); return; }
            switch (_firemode)
            {
                case FireMode.Semi: Fire(); break;
                case FireMode.Auto: Fire(); break;   // held-fire continues in _PhysicsProcess
                case FireMode.Burst: _burstLeft = Gun?.BurstCount ?? 3; break;
            }
        }

        // V cycles through the modes the gun's .dat actually offers (Eaglefire: Safety -> Semi -> Burst).
        void CycleFiremode()
        {
            var modes = AvailableModes();
            int i = System.Array.IndexOf(modes, _firemode);
            _firemode = modes[(i + 1) % modes.Length];
            _burstLeft = 0;
        }

        FireMode[] AvailableModes()
        {
            var list = new System.Collections.Generic.List<FireMode>();
            if (Gun != null)
            {
                if (Gun.HasSafety) list.Add(FireMode.Safety);
                if (Gun.HasSemi) list.Add(FireMode.Semi);
                if (Gun.HasAuto) list.Add(FireMode.Auto);
                if (Gun.BurstCount > 0) list.Add(FireMode.Burst);
            }
            if (list.Count == 0) list.Add(FireMode.Semi);
            return list.ToArray();
        }

        // Random unit vector within a cone of half-angle `spread` (radians) around `dir` — the port of
        // RandomEx.GetRandomForwardVectorInCone the source applies to each bullet's direction.
        Vector3 DeviateInCone(Vector3 dir, float spread)
        {
            float ang = _rng.RandfRange(0f, spread);
            float az = _rng.RandfRange(0f, Mathf.Tau);
            Vector3 up = Mathf.Abs(dir.Dot(Vector3.Up)) < 0.99f ? Vector3.Up : Vector3.Right;
            Vector3 right = dir.Cross(up).Normalized();
            Vector3 realUp = right.Cross(dir).Normalized();
            Vector3 offset = (right * Mathf.Cos(az) + realUp * Mathf.Sin(az)) * Mathf.Sin(ang);
            return (dir * Mathf.Cos(ang) + offset).Normalized();
        }

        // Hitscan: ray from the camera along its forward, masked to the zombie layer. Damage/range/firerate
        // come from the equipped gun's real ItemGunAsset .dat when loaded.
        public bool Fire()
        {
            if (_fireCd > 0f || Ammo <= 0 || _reloading || _cam == null) return false;
            float range = Gun?.Range ?? 200f;
            float damage = Gun?.ZombieDamage ?? 34f;
            _fireCd = Gun != null ? Gun.Firerate / 50f : 0.1f;   // Firerate = sim ticks between shots
            Ammo--;
            // fire feedback + the gun's real per-shot viewmodel shake (Shake_Min/Max_*); zero if no gun loaded
            if (Gun != null)
            {
                float rvPitch = _rng.RandfRange(Gun.RecoilMinY, Gun.RecoilMaxY);   // vertical recoil -> muzzle climb
                float rvYaw = _rng.RandfRange(Gun.RecoilMinX, Gun.RecoilMaxX);     // horizontal recoil -> gun yaw
                _viewmodel?.Kick(new Vector3(Gun.ShakeMinX, Gun.ShakeMinY, Gun.ShakeMinZ),
                                 new Vector3(Gun.ShakeMaxX, Gun.ShakeMaxY, Gun.ShakeMaxZ), rvPitch, rvYaw);
            }
            else _viewmodel?.Kick(Vector3.Zero, Vector3.Zero, 0f, 0f);
            if (Gun != null)   // camera recoil: pitch up + random-sign yaw, scaled by Recover (source: aim gets kick*Recover)
            {
                _recoilPitch += _rng.RandfRange(Gun.RecoilMinY, Gun.RecoilMaxY) * Gun.RecoverY;
                _recoilYaw += _rng.RandfRange(Gun.RecoilMinX, Gun.RecoilMaxX) * Gun.RecoverX * (_rng.Randf() < 0.5f ? -1f : 1f);
            }

            var space = GetWorld3D().DirectSpaceState;
            Vector3 from = _cam.GlobalPosition;
            Vector3 aim = -_cam.GlobalTransform.Basis.Z;                    // undeviated shot axis (shared muzzle)
            Basis cb = _cam.GlobalTransform.Basis;                          // camera: X=right, Y=up, -Z=forward
            Vector3 muzzle = from + cb.X * 0.12f - cb.Y * 0.04f + aim * 0.4f; // approx the muzzle (right, just below eye, fwd)
            SpawnMuzzleLight(muzzle);   // once per shot — the Muzzle_0 flash lights the world (our viewmodel flash can't)

            // Pellets: fire `Pellets` rays per shot, each deviated within the spread cone. Source: the magazine's
            // Pellets count (rifles = 1; shotgun shells Shells_2 = 8). Each pellet gets its own spread + tracer +
            // impact -> the shotgun's spread pattern. Recoil/flash/ammo above are per-shot, not per-pellet.
            float aimA = _viewmodel?.AimAlpha ?? 0f;
            float spread = Gun != null && Gun.SpreadAngleDegrees > 0f
                ? Mathf.DegToRad(Gun.SpreadAngleDegrees) * Mathf.Lerp(1f, Gun.SpreadAim, aimA) : 0f;
            int pellets = Mathf.Max(1, Gun?.Pellets ?? 1);
            bool killed = false;
            for (int i = 0; i < pellets; i++)
            {
                // source: dir = aim * RandomForwardVectorInCone(spread), spread = base * Lerp(1, spreadAim, aimAlpha)
                Vector3 dir = spread > 0.0001f ? DeviateInCone(aim, spread) : aim;
                Vector3 to = from + dir * range;
                var query = PhysicsRayQueryParameters3D.Create(from, to, (1u << 1) | (1u << 4)); // enemy + ragdoll bones
                var hit = space.IntersectRay(query);
                SpawnTracer(muzzle, hit.Count > 0 ? hit["position"].AsVector3() : to);   // one tracer per pellet
                if (hit.Count == 0) continue;
                var collider = hit["collider"].As<GodotObject>();
                Vector3 point = hit["position"].AsVector3();
                SpawnFleshImpact(point, dir);   // blood burst — every hit here is flesh (enemy/ragdoll-bone layers)
                if (collider is ZombieController z)
                {
                    bool wasDead = z.Dead;
                    z.DamageHit(damage, point, dir);         // impact point -> death ragdoll shoved where hit
                    if (!wasDead && z.Dead) Kills++;
                    killed = true;
                }
                else if (collider is PhysicalBone3D pb)       // shooting a corpse -> tumble it
                    pb.ApplyImpulse(dir * 7f, point - pb.GlobalPosition);
            }
            return killed;
        }

        // Flesh impact — the source Flesh_Dynamic effect (impact ID 5: a ~25-particle billboard blood spray,
        // size 0.5-1, ~1s life). Ported as a one-shot GPUParticles3D blood burst at the world hit point, sprayed
        // back out of the wound (-dir) under gravity, auto-freed. (Per-material impacts — concrete/metal/wood —
        // are a follow-up; flesh is what you hit in the demo.)
        void SpawnFleshImpact(Vector3 point, Vector3 dir)
        {
            var pm = new ParticleProcessMaterial
            {
                Direction = -dir,
                Spread = 60f,
                InitialVelocityMin = 1.5f,
                InitialVelocityMax = 4.5f,
                Gravity = new Vector3(0f, -9.8f, 0f),
                ScaleMin = 0.5f,
                ScaleMax = 1.0f,
                Color = new Color(0.5f, 0.02f, 0.02f),
            };
            var mat = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.5f, 0.02f, 0.02f),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            };
            var ps = new GpuParticles3D
            {
                Amount = 24,
                Lifetime = 1.0,
                OneShot = true,
                Explosiveness = 0.95f,
                ProcessMaterial = pm,
                DrawPass1 = new QuadMesh { Size = new Vector2(0.1f, 0.1f), Material = mat },
                Emitting = true,
            };
            GetTree().CurrentScene?.AddChild(ps);
            ps.GlobalPosition = point;
            var timer = GetTree().CreateTimer(1.4);
            timer.Timeout += () => { if (IsInstanceValid(ps)) ps.QueueFree(); };
        }

        static Texture2D _tracerTex;
        static bool _tracerTexTried;
        // Brief world-space tracer (the Military_30's Trail_0): a thin additive "Bullet"-textured box from ~muzzle
        // to the impact point, shown for a couple of frames then freed. Source renders it as a stretch-billboard
        // particle emitted at the muzzle down the shot direction (UseableGun.cs:645); a stretched box reads the same
        // from the third-person demo cam. Both ported guns carry the tracer mag, so every shot leaves one.
        void SpawnTracer(Vector3 from, Vector3 to)
        {
            float len = from.DistanceTo(to);
            if (len < 0.5f) return;
            if (!_tracerTexTried)
            {
                _tracerTexTried = true;
                string p = ProjectSettings.GlobalizePath("res://content/bullet.png");
                if (System.IO.File.Exists(p)) { var img = Image.LoadFromFile(p); if (img != null) _tracerTex = ImageTexture.CreateFromImage(img); }
            }
            var mat = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                AlbedoColor = new Color(1f, 0.9f, 0.55f),
            };
            if (_tracerTex != null) mat.AlbedoTexture = _tracerTex;
            var mi = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.05f, 0.05f, len) }, MaterialOverride = mat };
            GetTree().CurrentScene?.AddChild(mi);
            Vector3 axis = (to - from).Normalized();
            Vector3 up = Mathf.Abs(axis.Dot(Vector3.Up)) > 0.99f ? Vector3.Right : Vector3.Up;
            mi.LookAtFromPosition((from + to) * 0.5f, to, up);   // box length (local Z) spans from->to
            var timer = GetTree().CreateTimer(0.05);   // very brief — a static line held longer lags behind you when moving
            timer.Timeout += () => { if (IsInstanceValid(mi)) mi.QueueFree(); };
        }

        // Brief world-space muzzle flash light. The source Muzzle_0 effect illuminates the environment on each shot;
        // our viewmodel flash lives in an isolated SubViewport world, so it can't light the main scene. Warm Muzzle_0
        // colour (Unity (0.941,0.756,0.152)), flashed a couple of frames at the muzzle so nearby surfaces/zombies pop.
        void SpawnMuzzleLight(Vector3 pos)
        {
            var light = new OmniLight3D
            {
                OmniRange = 6f,
                LightColor = new Color(0.941f, 0.756f, 0.152f),
                LightEnergy = 3.5f,
            };
            GetTree().CurrentScene?.AddChild(light);
            light.GlobalPosition = pos;
            var timer = GetTree().CreateTimer(0.05);   // brief flash, in step with the muzzle sprite
            timer.Timeout += () => { if (IsInstanceValid(light)) light.QueueFree(); };
        }

        public override void _Process(double delta)
        {
            // Camera recoil recovers toward 0 (PlayerLook decays it with Lerp rate 4) and rides on top of the
            // mouse pitch each frame; while dead the death-cam owns the camera, so leave it alone.
            _recoilPitch = Mathf.Lerp(_recoilPitch, 0f, 4f * (float)delta);
            _recoilYaw = Mathf.Lerp(_recoilYaw, 0f, 4f * (float)delta);
            if (_cam != null && !_dead)
                _cam.RotationDegrees = new Vector3(_pitchDeg + _recoilPitch, _recoilYaw, 0f);
        }

        public override void _PhysicsProcess(double delta)
        {
            if (_pdieTest > 0) { _pdieTest -= delta; if (_pdieTest <= 0) { _pdieTest = -1; TakeDamage(9999f); } }
            if (_dead)
            {
                _deathTimer -= delta;
                Velocity = Vector3.Zero;
                if (_deathTimer <= 0) Respawn();
                return;
            }
            if (_fireCd > 0f) _fireCd -= (float)delta;
            if (_reloading)
            {
                _reloadTimer -= delta;
                if (_reloadTimer <= 0) { Ammo = Gun?.AmmoMax ?? 30; _reloading = false; _viewmodel?.SetReloading(false); }
            }
            // burst rounds + full-auto hold fire on cooldown (Fire() still enforces ammo/reload/cd)
            if (_fireCd <= 0f && !_reloading)
            {
                if (_burstLeft > 0) { if (Fire()) _burstLeft--; else _burstLeft = 0; }
                else if (_firemode == FireMode.Auto && Input.IsMouseButtonPressed(MouseButton.Left)) Fire();
            }

            _move.Stance = Input.IsPhysicalKeyPressed(Key.Shift) ? EPlayerStance.SPRINT
                         : Input.IsPhysicalKeyPressed(Key.Ctrl) ? EPlayerStance.CROUCH
                         : EPlayerStance.STAND;

            float forward, strafe;
            if (ScriptedInput.HasValue) { strafe = ScriptedInput.Value.x; forward = ScriptedInput.Value.y; }
            else
            {
                forward = (Input.IsPhysicalKeyPressed(Key.W) ? 1f : 0f) - (Input.IsPhysicalKeyPressed(Key.S) ? 1f : 0f);
                strafe  = (Input.IsPhysicalKeyPressed(Key.D) ? 1f : 0f) - (Input.IsPhysicalKeyPressed(Key.A) ? 1f : 0f);
            }
            bool jump = Input.IsPhysicalKeyPressed(Key.Space);

            // feed the viewmodel its locomotion so the walk bob picks the right SPEED_*/BOB_* + gates on movement
            bool moving = Mathf.Abs(forward) > 0.01f || Mathf.Abs(strafe) > 0.01f;
            _viewmodel?.SetLocomotion(moving, _move.Stance);

            var v = _move.Step(new UnityEngine.Vector2(strafe, forward), jump, IsOnFloor(), (float)delta);
            Vector3 world = GlobalTransform.Basis * new Vector3(v.x, 0f, -v.z);
            Velocity = new Vector3(world.X, v.y, world.Z);
            MoveAndSlide();
        }
    }
}
