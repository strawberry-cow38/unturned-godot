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

            _corpse = RiggedCharacter.Build("res://content/rig.json", new Color(0.95f, 0.92f, 0.88f));
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
            Ammo = Gun.AmmoMax;
            GD.Print($"[gun] {Gun.Id}: zombieDmg={Gun.ZombieDamage} range={Gun.Range} firerate={Gun.Firerate} mag={Gun.AmmoMax}");
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
                Fire();
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape })
                Input.MouseMode = Input.MouseMode == Input.MouseModeEnum.Captured
                    ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
        }

        // Hitscan: ray from the camera along its forward, masked to the zombie layer. Damage/range/firerate
        // come from the equipped gun's real ItemGunAsset .dat when loaded.
        public bool Fire()
        {
            if (_fireCd > 0f || Ammo <= 0 || _cam == null) return false;
            float range = Gun?.Range ?? 200f;
            float damage = Gun?.ZombieDamage ?? 34f;
            _fireCd = Gun != null ? Gun.Firerate / 50f : 0.1f;   // Firerate = sim ticks between shots
            Ammo--;

            var space = GetWorld3D().DirectSpaceState;
            Vector3 from = _cam.GlobalPosition;
            Vector3 dir = -_cam.GlobalTransform.Basis.Z;
            Vector3 to = from + dir * range;
            var query = PhysicsRayQueryParameters3D.Create(from, to, (1u << 1) | (1u << 4)); // enemy + ragdoll bones
            var hit = space.IntersectRay(query);
            if (hit.Count == 0) return false;
            var collider = hit["collider"].As<GodotObject>();
            Vector3 point = hit["position"].AsVector3();
            if (collider is ZombieController z)
            {
                bool wasDead = z.Dead;
                z.DamageHit(damage, point, dir);         // impact point -> death ragdoll shoved where hit
                if (!wasDead && z.Dead) Kills++;
                return true;
            }
            if (collider is PhysicalBone3D pb)            // shooting a corpse -> tumble it
            {
                pb.ApplyImpulse(dir * 7f, point - pb.GlobalPosition);
                return true;
            }
            return false;
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

            var v = _move.Step(new UnityEngine.Vector2(strafe, forward), jump, IsOnFloor(), (float)delta);
            Vector3 world = GlobalTransform.Basis * new Vector3(v.x, 0f, -v.z);
            Velocity = new Vector3(world.X, v.y, world.Z);
            MoveAndSlide();
        }
    }
}
