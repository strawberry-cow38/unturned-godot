using Godot;
using SDG.Unturned;

namespace UnturnedGodot
{
    // First-person player: ported PlayerMovementSim on Godot's 50 Hz physics tick + mouse look + a hitscan
    // gun (raycast from the camera vs the zombie collision layer). Movement CONSTANTS are exact; feel goes
    // through Jolt. Builds its own camera + capsule collider so it can be spawned from code.
    // WASD move / Shift sprint / Ctrl crouch / Z prone / Space jump / LMB fire / G melee / H grenade / R reload / Esc release mouse.
    public partial class PlayerController : CharacterBody3D
    {
        readonly PlayerMovementSim _move = new PlayerMovementSim();
        bool _xHeld, _zHeld; EPlayerStance _baseStance = EPlayerStance.STAND;   // intertwined stance state machine: X = crouch key, Z = prone key (master)
        CapsuleShape3D _capsule; CollisionShape3D _hitbox; float _capStance = -1f;   // hitbox capsule, resized per stance (source HeightForStance)
        Camera3D _cam;
        Vector3 _interpPrev, _interpCurr; bool _interpReady;   // render interpolation: smooth the VISUAL position between the 50Hz physics ticks (master); rotation stays per-frame so the mouse is instant
        Viewmodel _viewmodel;
        public PlayerInventory Inventory;   // the ported 9-page inventory model
        InventoryUI _invUI;                 // the dashboard (Tab to open)
        CraftingUI _craftUI;                // the crafting menu (K to open)
        BuildTool _build;                   // B = build mode (grid-snapped structures)
        string _gunName = "eaglefire";   // gun folder name (eaglefire | maplestrike), derived from the .dat path
        float _pitchDeg;
        Vehicle _driving; bool _fp;   // vehicle being driven + camera mode: _fp false = 3rd person (default), true = 1st; H toggles (on foot + driving)
        RiggedCharacter _body;        // live 3rd-person player model (RiggedCharacter), visible when !_fp
        // Damage feedback, both source-exact and fired from TakeDamage: the red hurt flash (PlayerUI.painAlpha) and the
        // camera flinch (PlayerLook.flinchLocalRotation, an angular kick perpendicular to the hit that decays to level).
        public float PainAlpha;                     // PlayerUI.pain: red overlay alpha, set on hit, fades at 1/s
        Quaternion _flinch = Quaternion.Identity;   // PlayerLook.flinchLocalRotation: camera kick, recovers at 4/s

        [Export] public float MouseSensitivity = 0.12f;
        public int Ammo = 30;
        public int Kills { get; private set; }

        public float Health = 100f;
        public float MaxHealth = 100f;
        public int Deaths;
        public bool Bleeding;      // HUD status indicator: set briefly after taking a hit (PlayerLifeUI's bleedingBox)
        double _bleedTimer;
        public bool Broken;        // PlayerLife.isBroken: broken legs (from a hard fall) -- blocks sprint + jump until mended
        // Survival vitals (0..1), shown live on the HUD. Rates are config-driven in Unturned (modeConfigData); these
        // are sensible stand-ins: stamina drains while sprinting + regens otherwise; food/water slowly decay; health
        // regenerates while fed + hydrated (PlayerLife gates regen on food/water) or bleeds while starved/dehydrated.
        public float Stamina = 1f, Food = 1f, Water = 1f;
        float _staminaRegenDelay;   // seconds to wait after releasing sprint before stamina regenerates
        public float Infection;   // 0..1 virus; zombie bites raise it (Zombie.askDamage's player.life.askInfect(b/3))
        public void Infect(float amount) => Infection = Mathf.Clamp(Infection + amount, 0f, 1f);

        // Use a consumable (ItemConsumeableAsset): apply its Health/Food/Water/bleeding effects to the vitals.
        public void Consume(ItemAsset a)
        {
            if (a == null) return;
            if (a.useHealth > 0) Health = Mathf.Min(MaxHealth, Health + a.useHealth);
            if (a.useFood  > 0) Food  = Mathf.Min(1f, Food  + a.useFood  / 100f);
            if (a.useWater > 0) Water = Mathf.Min(1f, Water + a.useWater / 100f);
            if (a.useStopsBleeding) { Bleeding = false; _bleedTimer = 0; }
            if (a.useHealBroken) Broken = false;   // Bones_Modifier Heal (Medkit/Splint) mends broken legs
        }

        // Drop an item into the world at pos, grounded by a downward cast (ItemManager.dropItem: snap to ground +
        // a small +-0.125 spread). Spawns a WorldItem you can walk back over and pick up.
        // aim point for the F1 dev console -- the look-orb: camera ray forward to the first hit (world/vehicles/props) or max reach.
        public Vector3 LookPoint()
        {
            if (_cam == null) return GlobalPosition - GlobalTransform.Basis.Z * 3f;
            var space = GetWorld3D().DirectSpaceState;
            Vector3 from = _cam.GlobalPosition, fwd = -_cam.GlobalTransform.Basis.Z;
            var rq = PhysicsRayQueryParameters3D.Create(from, from + fwd * LookReach);
            rq.CollisionMask = (1u << 0) | (1u << 5) | (1u << 6);   // world + vehicles + props
            rq.Exclude = new Godot.Collections.Array<Rid> { GetRid() };
            var hit = space.IntersectRay(rq);
            return hit.Count > 0 ? (Vector3)hit["position"] : from + fwd * LookReach;
        }

        // teleport (F1 console): move the VEHICLE if driving (the player rides attached to it), else the player. Zero velocity so it doesn't launch.
        public void TeleportTo(Vector3 pos)
        {
            if (_driving != null) { _driving.GlobalPosition = pos; _driving.LinearVelocity = Vector3.Zero; _driving.AngularVelocity = Vector3.Zero; }
            else
            {
                GlobalPosition = pos; Velocity = Vector3.Zero;
                _interpPrev = _interpCurr = pos;   // MUST reset the render-interp snapshots too — otherwise the next 50Hz tick does `GlobalPosition = _interpCurr` and snaps us right back to the old spot (the "gave feedback but didn't tp" bug; master was on foot, not driving)
            }
        }

        // Map arrow (M map): radians for a 2D arrow that points up=north at 0, turning clockwise. Source sets
        // localPlayerImage.RotationAngle = player yaw; we take the look/camera forward on the XZ plane. Godot 2D
        // rotation is clockwise-positive, so an up-pointing arrow rotates by atan2(fx, -fz).
        public float MapFacingAngle()
        {
            Vector3 f = _cam != null ? -_cam.GlobalTransform.Basis.Z : -GlobalTransform.Basis.Z;
            return Mathf.Atan2(f.X, -f.Z);
        }

        public void DropWorldItem(Item item, Vector3 pos)
        {
            var space = GetWorld3D().DirectSpaceState;
            var q = PhysicsRayQueryParameters3D.Create(pos + Vector3.Up, pos + Vector3.Down * 2048f);
            q.CollisionMask = 1u << 0;   // ground
            q.Exclude = new Godot.Collections.Array<Rid> { GetRid() };
            var hit = space.IntersectRay(q);
            if (hit.Count > 0) pos = (Vector3)hit["position"];
            pos += new Vector3(_rng.RandfRange(-0.125f, 0.125f), 0f, _rng.RandfRange(-0.125f, 0.125f));
            WorldItem.Spawn(GetParent(), item, pos);
        }

        WorldItem _focusItem;   // the dropped item the player is currently LOOKING AT (glowing + named), pickup target for E
        Vehicle _focusVehicle;  // the vehicle the player is LOOKING AT (outlined + info panel), enter target for E
        Vector3 _lookEnd;       // where the eye-ray ends (the look sphere sits here)
        MeshInstance3D _lookViz; // O-toggle visualizer of that ONE look sphere

        // Look-at interaction (master): cast the eye-ray from the camera forward, up to ~3.5 m, against item interaction
        // spheres (bit 8) AND world geometry (bit 0). The CLOSEST hit wins -> a wall between you and the item blocks it
        // (LOS-correct). The hit item gets a rarity glow outline + name billboard; a different/no item clears the old.
        const float LookReach = 2.6f, LookSphereR = 0.16f;   // the eye-ray reaches this far, ending in a sphere of this radius (master shrank it by half)

        void UpdateLookFocus()
        {
            WorldItem hitItem = null; Vehicle hitVeh = null;
            if (!_dead && _driving == null && _cam != null && Input.MouseMode == Input.MouseModeEnum.Captured)
            {
                var space = GetWorld3D().DirectSpaceState;
                Vector3 from = _cam.GlobalPosition;
                Vector3 fwd = -_cam.GlobalTransform.Basis.Z;
                // 1) ray forward -> the sphere sits where the ray STOPS (on world/props/items/vehicles, or max reach)
                var rq = PhysicsRayQueryParameters3D.Create(from, from + fwd * LookReach);
                rq.CollisionMask = (1u << 0) | (1u << 5) | (1u << 6) | (1u << 7);   // world + vehicles + props + items
                rq.Exclude = new Godot.Collections.Array<Rid> { GetRid() };
                var rhit = space.IntersectRay(rq);
                _lookEnd = rhit.Count > 0 ? (Vector3)rhit["position"] : from + fwd * LookReach;
                // 2) sphere at the ray end -> nearest ITEM (bit 7) or VEHICLE (bit 5) it overlaps is focusable
                var sq = new PhysicsShapeQueryParameters3D
                {
                    Shape = new SphereShape3D { Radius = LookSphereR },
                    Transform = new Transform3D(Basis.Identity, _lookEnd),
                    CollisionMask = WorldItem.ItemHitLayer | (1u << 5),
                    Exclude = new Godot.Collections.Array<Rid> { GetRid() },
                };
                float bestI = float.MaxValue, bestV = float.MaxValue;
                foreach (var h in space.IntersectShape(sq, 8))
                {
                    var c = h["collider"].As<GodotObject>();
                    if (c is WorldItem wi && IsInstanceValid(wi))
                    {
                        float d = wi.GlobalPosition.DistanceSquaredTo(_lookEnd);
                        if (d < bestI) { bestI = d; hitItem = wi; }
                    }
                    else if (c is Vehicle v && IsInstanceValid(v) && !v.Exploded)
                    {
                        float d = v.GlobalPosition.DistanceSquaredTo(_lookEnd);
                        if (d < bestV) { bestV = d; hitVeh = v; }
                    }
                }
                if (hitItem != null && hitVeh != null) { if (bestV < bestI) hitItem = null; else hitVeh = null; }   // focus the nearer of the two
            }
            if (_lookViz != null) { _lookViz.Visible = WorldItem.ShowLookSphere && !_dead && _driving == null; if (_lookViz.Visible) _lookViz.GlobalPosition = _lookEnd; }
            if (hitItem != _focusItem)
            {
                if (IsInstanceValid(_focusItem)) _focusItem.SetFocused(false);
                _focusItem = hitItem;
                _focusItem?.SetFocused(true);
            }
            if (hitVeh != _focusVehicle)
            {
                if (IsInstanceValid(_focusVehicle)) _focusVehicle.SetLookFocused(false);
                _focusVehicle = hitVeh;
                _focusVehicle?.SetLookFocused(true);
            }
        }

        // E: pick up the item you're LOOKING AT (the focused one), adding it to the inventory.
        public void TryPickup()
        {
            var wi = _focusItem;
            if (wi != null && IsInstanceValid(wi) && Inventory.tryAddItem(wi.Item))
            {
                GD.Print($"[pickup] {wi.Item.GetAsset()?.itemName}");
                wi.QueueFree();
                _focusItem = null;
                _invUI?.Refresh();
            }
        }

        float _meleeCd;
        MeleeDef _melee;   // the equipped melee weapon (null = bare fists)

        // Equip a melee weapon: load its real ItemMeleeAsset .dat (Range + per-target damage) so a swing is
        // weapon-specific. Holsters any gun viewmodel (the in-hand melee VIEWMODEL is the next melee-system increment).
        public void EquipHeldMelee(string meleeName)
        {
            string p = ProjectSettings.GlobalizePath($"res://content/{meleeName}.dat");
            _melee = System.IO.File.Exists(p) ? MeleeDef.FromDatText(meleeName, System.IO.File.ReadAllText(p)) : new MeleeDef { Name = meleeName };
            _viewmodel?.QueueFree();
            _viewmodel = new Viewmodel { MeleeMesh = $"{meleeName}.txt", MeleeAlbedo = $"{meleeName}_albedo.png" };   // show the melee weapon in-hand (arms + model, no gun FX)
            AddChild(_viewmodel);
            GD.Print($"[melee] equipped {_melee.Name} (range {_melee.Range}, zombie dmg {_melee.ZombieDamage}, stamina {_melee.Stamina})");
        }

        // G: melee swing -- hit the nearest zombie in front within the weapon's reach (proximity, not a raycast). Reuses
        // the zombie damage path. Rounds out combat (Unturned lets you swing/punch when out of ammo or up close).
        public void MeleeAttack()
        {
            if (_meleeCd > 0f || _cam == null) return;
            _meleeCd = 0.45f;   // ~half-second between swings
            _viewmodel?.SwingMelee();   // play the source melee swing animation (Weak)
            float range = _melee?.Range ?? 2.2f;      // the weapon's .dat Range (fists ~2.2 m)
            float dmg = _melee?.ZombieDamage ?? 45f;   // the weapon's .dat Zombie_Damage (fists 45)
            Vector3 origin = GlobalPosition + Vector3.Up * 1.2f, fwd = -_cam.GlobalTransform.Basis.Z;   // proximity from the player torso (robust); aimed by the look direction
            foreach (var n in GetTree().GetNodesInGroup("zombies"))
                if (n is ZombieController z && !z.Dead)
                {
                    Vector3 to = z.GlobalPosition + Vector3.Up - origin;   // aim at the torso
                    if (to.Length() < range + 0.5f && to.Normalized().Dot(fwd) > 0.3f)   // in front, in reach (+0.5 torso-vs-center slack)
                    {
                        bool wd = z.Dead;
                        z.DamageHit(dmg, z.GlobalPosition + Vector3.Up, fwd);
                        if (!wd && z.Dead) Kills++;
                        GD.Print($"[melee] hit a zombie ({_melee?.Name ?? "fists"} {dmg} dmg, range {range})");
                        break;   // one target per swing
                    }
                }
        }

        // PlayerLife.onLanded: landing faster than the fall-damage threshold (map default 22 m/s, and the port has
        // normal gravity so totalGravityMultiplier > 0.67 always holds) deals damage = min(101, |verticalVelocity|),
        // rounded. Source multiplies by the DEFENSE/STRENGTH skill (still 1.0 -- no skill system) then the WHOLE-BODY
        // clothing fallingDamageMultiplier (PlayerLife:2430 `damage *= clothing.fallingDamageMultiplier`) -- now WIRED.
        // Leg-breaking (source breakLegs, gated by FallingBoneBreakingProof) is still a separate un-modelled mechanic.
        void CheckFallDamage(float verticalVel)
        {
            const float threshold = 22.0f;
            if (verticalVel >= -threshold) return;             // a normal jump lands at ~7 m/s -> no damage
            Broken = true;                                     // any fall past the threshold breaks legs (shouldBreakLegs defaults true)
            float armored = Mathf.Abs(verticalVel) * (Inventory?.FallingDamageMultiplier ?? 1f);   // worn clothing cuts fall damage (source: whole-body product)
            int dmg = Mathf.RoundToInt(Mathf.Min(101f, armored));   // RoundAndClampToByte; damage <= 101
            if (dmg > 0) { GD.Print($"[fall] landed at {verticalVel:F1} m/s -> {dmg} damage, legs broken"); TakeDamage(dmg); }
        }

        float _grenadeCd;

        // DamageTool.explode (bounded): every zombie within radius takes zombieDamage * (1 - range/radius) -- LINEAR
        // falloff (Zombie.cs:270); the thrower (player) within radius takes playerDamage * (1 - (range/radius)^2) --
        // SQUARED falloff (Player.cs:1975). Out of radius = nothing. No LoS/armor/limb/buildable/vehicle damage yet.
        public void Explode(Vector3 point, float radius, float zombieDamage, float playerDamage, float vehicleDamage)
        {
            foreach (var n in GetTree().GetNodesInGroup("zombies"))
                if (n is ZombieController z && !z.Dead)
                {
                    float range = z.GlobalPosition.DistanceTo(point);
                    if (range > radius) continue;
                    float times = 1f - range / radius;
                    bool wd = z.Dead;
                    z.DamageHit(zombieDamage * times, z.GlobalPosition, (z.GlobalPosition - point).Normalized());
                    if (!wd && z.Dead) Kills++;
                }
            foreach (var n in GetTree().GetNodesInGroup("vehicles"))   // source DamageTool.explode also damages vehicles (Grenade.dat Vehicle_Damage 100)
                if (n is Vehicle v && !v.Exploded)
                {
                    float range = v.GlobalPosition.DistanceTo(point);
                    if (range > radius) continue;
                    v.TakeDamage(vehicleDamage * (1f - range / radius));   // linear falloff (port's simplified explosion model)
                }
            float pr = GlobalPosition.DistanceTo(point);
            if (pr <= radius) { float t = 1f - (pr / radius) * (pr / radius); if (t > 0f) TakeDamage(playerDamage * t * (Inventory?.ExplosionArmor ?? 1f)); }   // worn clothing cuts blast damage (source Player.cs:1981 getPlayerExplosionArmor)
            Local?.FlinchFromExplosion(point, Mathf.Max(radius * 2f, 12f), 30f);   // camera shake toward the blast (real Bomb effects ~16r/30mag)
            GD.Print($"[explode] r={radius} at {point}");
        }

        // Explosion camera shake -- src: EffectManager.cs:1615 -> PlayerLook.FlinchFromExplosion. A flinch rotation toward the
        // blast (axis = Cross(up, dir-from-blast-to-cam), in cam-local space) with EXPONENTIAL distance falloff 1-(dist/radius)^2;
        // magnitude in degrees from the explosion EffectAsset's CameraShake (real Bomb_* values: radius 6-32, mag 2-45).
        public static PlayerController Local;   // the interactive player (set in _Ready); explosions shake THIS camera
        public void FlinchFromExplosion(Vector3 point, float radius, float magnitudeDegrees)
        {
            if (_cam == null) return;
            Vector3 rel = _cam.GlobalPosition - point;
            float dist = rel.Length();
            if (dist <= 0f || dist >= radius) return;                                   // outside the shake radius -> nothing
            Vector3 worldAxis = Vector3.Up.Cross(rel / dist).Normalized();
            Vector3 localAxis = (_cam.GlobalTransform.Basis.Inverse() * worldAxis).Normalized();
            float deg = magnitudeDegrees * (1f - (dist / radius) * (dist / radius));     // src exponential falloff
            if (localAxis.IsFinite() && Mathf.Abs(deg) > 0.01f)
                _flinch = (_flinch * new Quaternion(localAxis, Mathf.DegToRad(deg))).Normalized();   // rides the existing _flinch spring
        }

        // Throw a grenade from the muzzle (ItemThrowableAsset). Bounded first pass: a fixed throw arc, ~1 s between
        // throws, no inventory consumption yet (like the generic melee).
        public void ThrowGrenade()
        {
            if (_grenadeCd > 0f) return;
            _grenadeCd = 1.0f;
            Vector3 fwd = _cam != null ? -_cam.GlobalTransform.Basis.Z : -GlobalTransform.Basis.Z;
            var g = new Grenade { Thrower = this, Vel = fwd * 16f + Vector3.Up * 5f + Velocity };   // arc forward + inherit motion
            GetParent().AddChild(g);
            g.GlobalPosition = (_cam?.GlobalPosition ?? GlobalPosition) + fwd * 0.5f;
            GD.Print("[grenade] thrown");
        }

        StorageCrate _openCrate;

        // F: open the nearest storage crate within ~2.5 m -- loads its grid into the STORAGE page (7) so the existing
        // dashboard + TryDrag handle it, and opens the dashboard.
        public bool OpenNearestCrate()
        {
            StorageCrate near = null; float best = 6.25f;   // 2.5 m, squared
            foreach (var n in GetTree().GetNodesInGroup("crates"))
                if (n is StorageCrate c)
                {
                    float d = GlobalPosition.DistanceSquaredTo(c.GlobalPosition);
                    if (d < best) { best = d; near = c; }
                }
            if (near == null) return false;
            _openCrate = near;
            CopyPage(near.Storage, Inventory.items[PlayerInventory.STORAGE], near.Width, near.Height);
            GD.Print($"[crate] opened ({near.Storage.getItemCount()} items)");
            _invUI?.Open();
            Input.MouseMode = Input.MouseModeEnum.Visible;
            return true;
        }

        // save the open crate's contents back and clear the STORAGE view (called when the dashboard closes)
        void CloseCrate()
        {
            if (_openCrate == null) return;
            CopyPage(Inventory.items[PlayerInventory.STORAGE], _openCrate.Storage, _openCrate.Width, _openCrate.Height);
            var s = Inventory.items[PlayerInventory.STORAGE];
            s.clear(); s.loadSize(0, 0);
            _openCrate = null;
        }

        static void CopyPage(SDG.Unturned.Items from, SDG.Unturned.Items to, byte w, byte h)
        {
            to.clear(); to.loadSize(w, h);
            for (byte i = 0; i < from.getItemCount(); i++)
            {
                var j = from.getItem(i);
                to.addItem(j.x, j.y, j.rot, j.item);
            }
        }
        public Vector3 Spawn = new Vector3(0, 1f, 0);

        // Zombie sensing (AlertTool/PlayerStance): Agro increments once per zombie that starts hunting this
        // player -- it drives their approach path (every 3rd zombie RUSHes, the rest split left/right, so a
        // horde fans out to surround). Moving/Stance feed the stealth detection radius below.
        public int Agro;
        public bool Moving { get; private set; }
        public EPlayerStance Stance => _move.Stance;
        float _footNoiseT;   // Phase 3 hearing: throttle the continuous footstep-noise emit (~2.5x/s while moving)

        // Port of PlayerStance.GetStealthDetectionRadius: the radius (m) within which a zombie can sense this
        // player, by stance -- standing 12, crouched 6, sprinting 20, prone 3, x1.1 while moving. AlertTool
        // clamps it to [1, 64]. Crouch-walking (or crawling prone) is how you sneak past a horde.
        public float GetStealthDetectionRadius()
        {
            if (IsDriving) return Mathf.Clamp(48f * _driving.ForwardSpeedPct(), 1f, 64f);   // source DRIVING: DETECT_FORWARD(48) * fwd-speed% -> loud at speed, ~silent when parked
            float move = Moving ? 1.1f : 1f;                       // DETECT_MOVE
            float r = _move.Stance switch
            {
                EPlayerStance.SPRINT => 20f * move,                // DETECT_SPRINT
                EPlayerStance.CROUCH => 6f * move,                 // DETECT_CROUCH
                EPlayerStance.PRONE  => 3f * move,                 // DETECT_PRONE
                _ => 12f * move,                                   // DETECT_STAND
            };
            return Mathf.Clamp(r, 1f, 64f);
        }

        // When set (e.g. by a recorded demo or a net-driven bot), overrides keyboard input: x=strafe, y=forward.
        public UnityEngine.Vector2? ScriptedInput;
        // Likewise forces the stance (bypassing the Shift/Ctrl/Z keys) for demos, bots, and self-tests.
        public EPlayerStance? ScriptedStance;

        void UpdateHitbox(EPlayerStance stance)   // collision capsule per stance (STAND 2 / CROUCH 1.2 / PRONE 0.8), bottom pinned to the feet
        {
            float h = PlayerMovementDef.HeightForStance(stance);
            if (Mathf.Abs(h - _capStance) < 0.001f) return;
            _capStance = h; _capsule.Height = h; _hitbox.Position = new Vector3(0f, h / 2f, 0f);
        }

        const float StepHeight = 0.4f;   // curbs/thresholds up to this high are stepped over (master: stop snagging on sidewalks)
        // If the horizontal motion is blocked at foot level but clear a step higher, raise onto the step; FloorSnapLength then
        // pulls us back down onto it. Reused by both the player and zombies (source has stair/ledge handling in PlayerMovement).
        void StepUp(float delta)
        {
            if (!IsOnFloor()) return;
            Vector3 motion = new Vector3(Velocity.X, 0f, Velocity.Z) * delta;
            if (motion.LengthSquared() < 1e-6f) return;
            var raised = new Transform3D(GlobalTransform.Basis, GlobalPosition + Vector3.Up * StepHeight);
            if (TestMove(GlobalTransform, motion) && !TestMove(raised, motion))
                GlobalPosition += Vector3.Up * StepHeight;
        }

        bool HeadroomFor(float height)   // is there space to occupy a taller capsule? (blocks standing up under a ceiling -- master)
        {
            var q = new PhysicsShapeQueryParameters3D
            {
                Shape = new CapsuleShape3D { Height = height, Radius = 0.34f },
                Transform = new Transform3D(Basis.Identity, GlobalPosition + Vector3.Up * (height / 2f)),
                CollisionMask = CollisionMask,
                Exclude = new Godot.Collections.Array<Rid> { GetRid() },
            };
            return GetWorld3D().DirectSpaceState.IntersectShape(q, 1).Count == 0;
        }
        public bool CaptureMouse = true;

        public GunDef Gun;          // real ItemGunAsset stats (damage/range/firerate/mag) when loaded
        float _fireCd;              // seconds until the gun can fire again
        const float GunshotRadius = 48f;   // earshot of an unsuppressed shot (AlertTool noise); suppressors would cut it
        bool _reloading;            // reloading -> can't fire; magazine refills when the timer elapses
        double _reloadTimer;
        const double ReloadTime = 1.633; // Eaglefire Gun_Reload clip length (no reload-time key in the .dat)
        float _recoilPending, _recoilYawPending;  // un-applied recoil kick (deg); drains additively into the real aim and STAYS -- never auto-returns (master: additive, no recover-to-origin)
        readonly RandomNumberGenerator _rng = new();
        enum FireMode { Safety, Semi, Auto, Burst }   // EFiremode; the gun's available set comes from its .dat flags
        FireMode _firemode = FireMode.Semi;
        public string FiremodeName => _firemode.ToString().ToUpper();   // for the HUD
        // let the FP viewmodel take the world's lighting (day/night sun + ambient)
        public void LinkWorldLighting(DirectionalLight3D sun, Godot.Environment env)
        {
            if (_viewmodel != null) { _viewmodel.WorldSun = sun; _viewmodel.WorldEnv = env; }
        }
        int _burstLeft;                               // rounds remaining in the current burst
        float _burstCd;                               // NON-source anti-spam-click cooldown between bursts (master's call)

        bool _dead;
        double _deathTimer;
        RiggedCharacter _corpse;

        // Zombie melee lands here; on death, drop a ragdoll corpse + third-person death-cam, then respawn.
        // fromPos = the attacker's world position, used only to aim the camera flinch; null for sourceless damage
        // (starvation/infection) which flashes but doesn't kick the camera.
        public void TakeDamage(float amount, Vector3? fromPos = null)
        {
            if (_dead || Health <= 0f) return;
            Health -= amount;
            if (amount > 1f) { Bleeding = true; _bleedTimer = 5.0; }   // show the bleeding status icon after a real hit

            // Hurt flash — PlayerLifeUI.onDamaged -> PlayerUI.pain: a red full-screen overlay whose alpha is
            // Clamp(damage/40, 0, 1) * 0.75, but only for a real hit (source gates it on damage > 5).
            if (amount > 5f) PainAlpha = Mathf.Clamp(amount / 40f, 0f, 1f) * 0.75f;

            // Camera flinch — PlayerLook.FlinchFromDamage: rotate the view by Min(damage, 25) * 0.5 degrees around the
            // axis Cross(up, hitDir) (perpendicular to where the hit came from), converted into camera-local space so a
            // frontal hit pitches the view and a side hit rolls it. The kick accumulates and later recovers to level.
            if (fromPos.HasValue && _cam != null)
            {
                Vector3 dir = GlobalPosition - fromPos.Value; dir.Y = 0f;   // horizontal hit direction (attacker -> me)
                if (dir.LengthSquared() > 0.0001f)
                {
                    Vector3 worldAxis = Vector3.Up.Cross(dir.Normalized()).Normalized();
                    Vector3 localAxis = (_cam.GlobalTransform.Basis.Inverse() * worldAxis).Normalized();   // InverseTransformDirection
                    float deg = Mathf.Min(amount, 25f) * 0.5f;
                    if (localAxis.IsFinite())   // a degenerate cam basis could NaN the axis -> skip rather than poison _flinch
                        _flinch = (_flinch * new Quaternion(localAxis, Mathf.DegToRad(deg))).Normalized();
                }
            }

            if (Health <= 0f) { Deaths++; Die(); }
        }

        void Die()
        {
            _dead = true;
            _deathTimer = 3.5;
            _burstLeft = 0;   // death cancels any in-progress burst (no resume after respawn)
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
            Stamina = Food = Water = 1f; Infection = 0f; Bleeding = false; Broken = false;   // fresh vitals on respawn
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
                PainAlpha = 0f; _flinch = Quaternion.Identity;   // clear any lingering hurt feedback
            }
        }

        // Survival sim driving the live HUD vitals. The mechanism is source-accurate (PlayerLife: stamina burns while
        // sprinting + regens otherwise; health regenerates only while fed AND hydrated; you take damage when food or
        // water bottoms out). The RATES are stand-ins -- Unturned's real ones live in server modeConfigData, not the
        // binary -- so they're tuned to be visible, not eyeballed from the game.
        void UpdateVitals(bool moving, float dt)
        {
            if (_dead) return;
            bool sprinting = moving && _move.Stance == EPlayerStance.SPRINT;
            if (sprinting) { Stamina = Mathf.Max(0f, Stamina - 0.22f * dt); _staminaRegenDelay = 1f; }   // hold regen 1s after releasing sprint
            else { _staminaRegenDelay = Mathf.Max(0f, _staminaRegenDelay - dt); if (_staminaRegenDelay <= 0f) Stamina = Mathf.Min(1f, Stamina + 0.33f * dt); }
            Food  = Mathf.Max(0f, Food  - 0.0050f * dt);
            Water = Mathf.Max(0f, Water - 0.0070f * dt);
            Infection = Mathf.Max(0f, Infection - 0.01f * dt);       // virus slowly clears if you stop getting bitten
            bool sick = Infection > 0.75f;                           // heavy infection makes you ill (loses health)
            if (Food > 0.30f && Water > 0.30f && Health < MaxHealth && !sick)
                Health = Mathf.Min(MaxHealth, Health + 2f * dt);     // regen while fed + hydrated (blocked while sick)
            else if (Food <= 0f || Water <= 0f || sick)
                Health = Mathf.Max(0f, Health - (sick ? 2f : 1.5f) * dt);   // starve / dehydrate / infection sickness
            if (Health <= 0f) { Deaths++; Die(); }
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
            GD.Print($"[gun] {Gun.Id}: zombieDmg={Gun.ZombieDamage} vehicleDmg={Gun.VehicleDamage} range={Gun.Range} firerate={Gun.Firerate} mag={Gun.AmmoMax} pellets={Gun.Pellets} mode={_firemode}");
        }

        public string HeldGunName => _gunName;

        // Hold a specific gun by its content name: reload the GunDef + rebuild the per-gun viewmodel. Used by Q-switch
        // and by the inventory's Equip action (equipping a gun makes it the held weapon).
        public void EquipHeldGun(string gunName)
        {
            LoadGun($"res://content/{gunName}.dat");   // sets Gun + _gunName + Ammo + firemode
            _melee = null;   // holding a gun now, not a melee weapon (re-enables ADS)
            _viewmodel?.QueueFree();
            _viewmodel = new Viewmodel { GunName = _gunName };
            AddChild(_viewmodel);
            GD.Print($"[gun] holding {_gunName}");
        }

        // Q toggles between the three ported guns.
        void SwitchWeapon()
        {
            EquipHeldGun(_gunName switch { "eaglefire" => "maplestrike", "maplestrike" => "masterkey", _ => "eaglefire" });
        }

        public override void _Ready()
        {
            AddToGroup("players");     // so vehicle explosions (+ future area effects) can find nearby players
            CollisionLayer = 1 << 3;   // player bit
            CollisionMask = (1 << 0) | (1 << 6);    // walk on ground (bit 0) + collide with transparent props on bit 6 (see-through to the item LOS raycast but still solid for the player -- master)

            _capsule = new CapsuleShape3D { Height = PlayerMovementDef.HEIGHT_STAND, Radius = 0.35f };
            _hitbox = new CollisionShape3D { Shape = _capsule, Position = new Vector3(0, PlayerMovementDef.HEIGHT_STAND / 2f, 0) };
            AddChild(_hitbox);
            FloorMaxAngle = Mathf.DegToRad(55f);   // climb steeper slopes than Godot's 45 default (master)
            FloorSnapLength = 0.5f;                 // stay glued to the ground over small steps / undulations

            PhysicsInterpolationMode = Node.PhysicsInterpolationModeEnum.Off;   // opt the PLAYER out of Godot's global physics interp -- on-foot uses MANUAL position-only interp so the mouse stays instant (master)
            _cam = new Camera3D { Position = new Vector3(0, 1.6f, 0), Current = true, PhysicsInterpolationMode = Node.PhysicsInterpolationModeEnum.Off };
            _cam.CullMask &= ~OutlineOverlay.OutlineLayer;   // don't render the items' silhouette meshes in the main view (only the offscreen mask cam does)
            AddChild(_cam);
            CallDeferred(Node.MethodName.AddChild, new OutlineOverlay());   // screen-space look-at outline (deferred so the viewport/camera exist)
            _lookViz = new MeshInstance3D   // the ONE look-END sphere (O toggles it); TopLevel so it sits in world space at the ray end
            {
                Mesh = new SphereMesh { Radius = LookSphereR, Height = LookSphereR * 2f, RadialSegments = 16, Rings = 10 },
                TopLevel = true, Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(0.3f, 0.8f, 1f, 0.25f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, CullMode = BaseMaterial3D.CullModeEnum.Disabled },
            };
            AddChild(_lookViz);
            if (CaptureMouse) Local = this;   // the interactive (mouse-captured) player -> explosions shake this camera

            _body = RiggedCharacter.Build("res://content/rig.json", new Color(0.82f, 0.66f, 0.52f));   // live 3rd-person body
            if (_body != null) { _body.Visible = false; CallDeferred(Node.MethodName.AddSibling, _body); }
            _viewmodel = new Viewmodel { GunName = _gunName };   // per-gun visuals
            AddChild(_viewmodel);
            _rng.Randomize();

            // the ported inventory + its dashboard. Demo-populate it (real items) so there's something to show.
            ItemCatalog.RegisterAll();
            Inventory = new PlayerInventory();
            PopulateDemoInventory();
            _invUI = new InventoryUI { Inv = Inventory, Player = this };
            AddChild(_invUI);
            _craftUI = new CraftingUI { Inv = Inventory, Player = this };
            AddChild(_craftUI);
            _build = new BuildTool { Cam = _cam };
            GetParent().AddChild(_build);   // structures live in the scene, not under the player

            if (CaptureMouse) Input.MouseMode = Input.MouseModeEnum.Captured;
            foreach (var a in OS.GetCmdlineUserArgs()) if (a == "--pdie") _pdieTest = 2.0; // render-test: die at 2s
        }
        double _pdieTest = -1;

        public PauseMenu PauseMenu;   // ESC viewmodel-tuning menu (set by BuildPlayable); null in demos
        public AttachmentMenu AttachMenu;   // T weapon-attachment menu (set by BuildPlayable); null in demos

        public override void _UnhandledInput(InputEvent @event)
        {
            // while driving, only E (exit) / V (cam) / L (lights) / Escape + LMB (horn) / RMB (lights) are live -- no look, fire, aim, reload, etc.
            if (_driving != null)
            {
                bool allowedKey = @event is InputEventKey { Pressed: true } dk && (dk.Keycode == Key.E || dk.Keycode == Key.H || dk.Keycode == Key.L || dk.Keycode == Key.Ctrl || dk.Keycode == Key.Escape);
                bool allowedMouse = @event is InputEventMouseButton { ButtonIndex: MouseButton.Left or MouseButton.Right };
                if (!allowedKey && !allowedMouse) return;
            }
            // clicks belong to an open UI (inventory / crate / dashboard) when the cursor's visible -- don't fire / honk / aim THROUGH them (master)
            if (@event is InputEventMouseButton && Input.MouseMode != Input.MouseModeEnum.Captured) return;
            if (@event is InputEventMouseMotion mm && Input.MouseMode == Input.MouseModeEnum.Captured)
            {
                RotateY(Mathf.DegToRad(-mm.Relative.X * MouseSensitivity));
                _pitchDeg = Mathf.Clamp(_pitchDeg - mm.Relative.Y * MouseSensitivity, -89f, 89f);
                _cam.RotationDegrees = new Vector3(_pitchDeg, 0f, 0f);
            }
            else if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
            {
                if (_driving != null) _driving.Honk();                 // LMB while driving: horn
                else if (_build != null && _build.Active) _build.Place();   // build mode: place a structure
                else StartFire();
            }
            else if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Right } rmb)
            {
                if (_driving != null) { if (rmb.Pressed) _driving.ToggleHeadlights(); }   // RMB while driving: toggle lights
                else if (_melee == null) _viewmodel?.SetAiming(rmb.Pressed);   // hold RMB to ADS -- GUNS only (a melee weapon has no sights)
            }
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.R })
                StartReload();
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.V })
            {
                if (_driving == null) CycleFiremode();   // V on foot: cycle firemode (cam toggle moved to H)
            }
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.H })
                _fp = !_fp;   // H: toggle 3rd / 1st person camera (on foot + driving)
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.Q })
                SwitchWeapon();   // toggle Eaglefire <-> Maplestrike
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.E })
            {
                if (_driving != null) ExitVehicle();                       // E while driving: hop out
                else if (_focusItem != null) TryPickup();                                                  // looking at an item: pick it up
                else if (_focusVehicle != null && IsInstanceValid(_focusVehicle)) EnterVehicle(_focusVehicle); // looking at a vehicle: get in (master: look-at, not proximity)
            }
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.L })
            {
                if (_driving != null) _driving.ToggleHeadlights();         // L while driving: toggle headlights
            }
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.Ctrl })
            {
                if (_driving != null && _driving.HasSiren) _driving.ToggleSiren();   // Ctrl while driving an emergency vehicle: toggle siren/lightbar (master)
            }
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.F })
                { if (!OpenNearestCrate()) _viewmodel?.PlayInspect(); }   // F: open a nearby crate, else inspect the gun
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.B })
                _build?.Toggle();     // toggle build mode
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.C })
                _build?.CycleType();  // cycle the structure type (floor/wall)
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.G })
                MeleeAttack();        // melee swing at a zombie in reach
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.H })
                ThrowGrenade();       // throw a grenade
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.P, Echo: false })
            {
                WorldItem.ShowLabels = !WorldItem.ShowLabels;                       // P: toggle ALL item ESP name tags
                GetTree().CallGroup("esp_labels", "set_visible", WorldItem.ShowLabels);
            }
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.O, Echo: false })
                WorldItem.ShowLookSphere = !WorldItem.ShowLookSphere;               // O: toggle the look-END sphere visualizer (master)
            else if (@event is InputEventKey { Keycode: Key.T, Echo: false } tKey)
            {
                if (AttachMenu != null)   // T (hold): show the weapon-attachment menu while held, release to close
                {
                    if (tKey.Pressed && !AttachMenu.IsOpen)
                    {
                        AttachMenu.VM = _viewmodel;
                        AttachMenu.Open();
                        Input.MouseMode = Input.MouseModeEnum.Visible;
                    }
                    else if (!tKey.Pressed && AttachMenu.IsOpen)
                    {
                        AttachMenu.Close();
                        Input.MouseMode = Input.MouseModeEnum.Captured;
                    }
                }
            }
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.Tab })
            {
                if (_viewmodel != null && _viewmodel.InAttachView) return;   // no inventory while the T attachment menu is up
                if (_invUI != null && _invUI.IsOpen) CloseCrate();   // closing the dashboard saves an open crate
                _invUI?.Toggle();   // open/close the inventory dashboard, freeing the mouse while it's open
                Input.MouseMode = (_invUI != null && _invUI.IsOpen) ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
            }
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.K })
            {
                if (_viewmodel != null && _viewmodel.InAttachView) return;   // no crafting while the T attachment menu is up
                _craftUI?.Toggle();   // K: open/close the crafting menu (lists what you can make from your supplies)
                Input.MouseMode = (_craftUI != null && _craftUI.IsOpen) ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
            }
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape })
            {
                if (PauseMenu != null)   // ESC opens the viewmodel-tuning pause menu (frees the mouse for the sliders)
                {
                    PauseMenu.Toggle();
                    Input.MouseMode = PauseMenu.IsOpen ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
                }
                else
                    Input.MouseMode = Input.MouseMode == Input.MouseModeEnum.Captured
                        ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
            }
        }

        public void OpenInventory() { _invUI?.Open(); Input.MouseMode = Input.MouseModeEnum.Visible; }
        public void DemoSelect(byte page, byte x, byte y) { _invUI?.DebugSelect(page, x, y); Input.MouseMode = Input.MouseModeEnum.Visible; }
        public void DemoEquip(byte page, byte x, byte y) => _invUI?.DebugEquip(page, x, y);

        // seed the inventory with real items: wear the Alicepack (8x7) + Cargo Pants (6x3) so those pages open up,
        // put both guns in the hand slots, and scatter medical/food/water across pockets + backpack to show packing
        void PopulateDemoInventory()
        {
            Inventory.wearBackpack(new Item(253));   // Alicepack -> backpack slot + 8x7 storage
            Inventory.wearPants(new Item(209));      // Cargo Pants -> pants slot + 6x3 storage
            Inventory.equipToSlot(0, new Item(4));     // Eaglefire -> primary
            Inventory.equipToSlot(1, new Item(363));   // Maplestrike -> secondary
            // items DON'T stack (Unturned is grid-based): each is its own single (amount-1) grid item.
            Inventory.items[2].tryAddItem(new Item(15));            // Medkit in pockets
            Inventory.items[2].tryAddItem(new Item(95));            // Bandage
            Inventory.items[2].tryAddItem(new Item(95));            // Bandage (separate slot -- no stacking)
            Inventory.items[2].tryAddItem(new Item(14));            // Bottled Water
            var bag = Inventory.items[PlayerInventory.BACKPACK];
            bag.tryAddItem(new Item(15));                           // Medkit
            bag.tryAddItem(new Item(13));                           // Canned Beans
            bag.tryAddItem(new Item(13));                           // Canned Beans (separate)
            bag.tryAddItem(new Item(14));                           // Bottled Water
            bag.tryAddItem(new Item(14));                           // Bottled Water (separate)
            bag.tryAddItem(new Item(95));                           // Bandage
            Inventory.items[PlayerInventory.PANTS].tryAddItem(new Item(13));  // Canned Beans in pants
        }

        // R to reload: block firing, then refill the magazine after the reload's duration. The reload takes the
        // Gun_Reload clip's length (the Eaglefire .dat has no separate reload-time key), so ReloadTime = that.
        void StartReload()
        {
            if (_reloading || _dead) return;
            int max = Gun?.AmmoMax ?? 30;
            if (Ammo >= max) return;
            _burstLeft = 0;   // reloading cancels any in-progress burst -> it won't resume after the reload (master)
            _reloading = true;
            _viewmodel?.SetReloading(true);
            double full = _viewmodel?.ReloadLength ?? ReloadTime;   // per-gun reload duration (masterkey 2.467s vs rifles 1.633s)
            _reloadTimer = Gun?.ShellReload == true ? full / System.Math.Max(1, max) : full;   // shell-fed shotguns (Pump/Break) load ONE shell per interval (see the reload tick + StartFire cancel)
        }

        // LMB press -> fire per the current mode (safety = nothing, semi = one, burst = queue BurstCount, auto = start).
        void StartFire()
        {
            if (_dead) return;   // ignore fire commands on the death screen (master)
            if (_reloading) { if (Gun?.ShellReload == true && Ammo > 0) { _reloading = false; _viewmodel?.SetReloading(false); } else return; }   // shell-fed shotgun: firing CANCELS the shell-by-shell reload (shoot what's loaded); other guns ignore fire mid-reload (master)
            if (_viewmodel != null && _viewmodel.InAttachView) return;   // no firing while the T attachment menu is up
            if (_viewmodel != null && _viewmodel.IsInspecting) { _viewmodel.CancelInspect(); return; }   // firing mid-inspect cancels it + snaps the gun to the shoot pose; no shot this click
            if (_firemode == FireMode.Safety) return;
            // dry-fire: trigger pulled on an empty chamber -> hammer click, no shot
            if (Ammo <= 0 && !_reloading && _fireCd <= 0f) { _viewmodel?.PlayDryFire(); return; }
            switch (_firemode)
            {
                case FireMode.Semi: Fire(); break;
                case FireMode.Auto: Fire(); break;   // held-fire continues in _PhysicsProcess
                case FireMode.Burst: if (_burstCd <= 0f && _burstLeft <= 0) _burstLeft = Gun?.BurstCount ?? 3; break;   // cooldown gate + can't start a new burst mid-burst (master)
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
            if (_fireCd > 0f || Ammo <= 0 || _reloading || _cam == null || _dead || _driving != null) return false;   // never fire while dead -- kills a queued burst the frame we die (the tick calls Fire()) + ignores death-screen clicks (master). _driving guard fixes the "stray tracer flies straight south" bug: the auto/burst tick (_PhysicsProcess) calls Fire() on held-LMB WITHOUT a driving check, and while driving _cam is TopLevel (detached chase cam) -> aim = the chase cam's fixed heading, not the player's look. LMB honks while driving anyway.
            if (_viewmodel != null && (!_viewmodel.IsEquipComplete || _viewmodel.IsInspecting || _viewmodel.InAttachView)) return false;   // no firing until equip finishes, or during inspect / attachment menu (source canFire gates)
            float damage = Gun?.ZombieDamage ?? 34f;   // range/travel are encoded in the bullet's steps + velocity
            float vehDamage = Gun?.VehicleDamage ?? 40f;   // bullets hurt vehicles less than zombies (source Vehicle_Damage)
            _fireCd = Gun != null ? (Gun.Firerate + 1) / 50f : 0.1f;   // interval = firerate+1 ticks: source fires when clock-lastFire > firerate (STRICT >, UseableGun.tockShoot), so the real gap is firerate+1. Off-by-one made fast guns (zube firerate 4: 750rpm vs correct 600) fire ~25% too hot -- master's "very high ROF"
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
            if (Gun != null)   // additive recoil: each shot kicks the AIM up + random-sign yaw (scaled by Recover); it accumulates and STAYS -- player pulls back down (master)
            {
                _recoilPending += _rng.RandfRange(Gun.RecoilMinY, Gun.RecoilMaxY) * Gun.RecoverY;
                _recoilYawPending += _rng.RandfRange(Gun.RecoilMinX, Gun.RecoilMaxX) * Gun.RecoverX * (_rng.Randf() < 0.5f ? -1f : 1f);
            }

            Vector3 from = _cam.GlobalPosition;
            // Aim from the player's AUTHORITATIVE look (body yaw + camera pitch), NOT the camera's live GLOBAL basis.
            // Reading _cam.GlobalTransform.Basis meant a shot could inherit a transiently-bad camera axis -- flinch/
            // hit-shake (line 1223 sets _cam.Basis = flinch*look) or a frame where the cam basis wasn't the player's
            // -- firing the bullet off in a FIXED world direction regardless of where you aimed (the "stray tracer
            // flies straight south, any gun, any time" bug). Recoil is preserved (it drains into Rotation.Y/_pitchDeg).
            Basis cb = new Basis(Vector3.Up, Rotation.Y) * new Basis(Vector3.Right, Mathf.DegToRad(_pitchDeg));  // X=right, Y=up, -Z=forward
            Vector3 aim = -cb.Z;                                            // undeviated shot axis, from the real look angles
            float aimA = _viewmodel?.AimAlpha ?? 0f;
            // muzzle: hip sits lower-right (where the barrel is); ADS pulls the gun onto the camera axis, so the
            // muzzle centres (X offset -> 0) as you aim -> the bullet + tracer keep originating from the barrel.
            Vector3 muzzle = from + cb.X * (0.12f * (1f - aimA)) - cb.Y * 0.035f + aim * 0.4f;
            SpawnMuzzleLight(muzzle);   // once per shot — the Muzzle_0 flash lights the world

            // Ballistics: each pellet is a SIMULATED PROJECTILE (travel + drop), not an instant ray. Velocity =
            // dir * MuzzleVelocity; it steps every physics tick (0.02s) in StepBullets, dropping under gravity, its
            // tracer flying with it, hits/damage landing when it arrives. (source: BulletInfo + UseableGun.cs:1539.)
            float spread = Gun != null && Gun.SpreadAngleDegrees > 0f
                ? Mathf.DegToRad(Gun.SpreadAngleDegrees) * Mathf.Lerp(1f, Gun.SpreadAim, aimA) : 0f;
            int pellets = Mathf.Max(1, Gun?.Pellets ?? 1);
            float muzzleVel = Gun?.MuzzleVelocity ?? 500f;
            int steps = Gun?.BallisticSteps ?? 20;
            float gravity = -9.81f * (Gun?.GravityMultiplier ?? 4f);
            for (int i = 0; i < pellets; i++)
            {
                Vector3 dir = spread > 0.0001f ? DeviateInCone(aim, spread) : aim;
                SpawnBullet(muzzle, dir * muzzleVel, steps, gravity, damage, vehDamage);
            }
            // AlertTool point-noise: an unsuppressed gunshot pulls zombies within earshot over to investigate. A silenced
            // barrel skips the alert ENTIRELY (source UseableGun ~936: only alert if barrel==null || !isSilenced) -> stealth.
            if (!(_viewmodel?.IsSuppressed ?? false)) SoundBus.Emit(GetTree(), GlobalPosition, SoundBus.Gunshot);   // Phase 3 sound bus: unsuppressed gunshot loudness (suppressed = silent)
            return true;   // shot fired; the actual hits/kills land later in StepBullets
        }

        // A simulated bullet (Unturned's BulletInfo): flies from the muzzle with a velocity, dropping under gravity,
        // stepped every physics tick; its tracer travels with it; it hits/despawns on contact or after its steps.
        sealed class Bullet { public Vector3 Pos, Vel, Origin; public int StepsLeft; public float Gravity, Damage, VehicleDamage; public MeshInstance3D Tracer; public Node3D RocketVis; }
        readonly System.Collections.Generic.List<Bullet> _bullets = new();

        void SpawnBullet(Vector3 pos, Vector3 vel, int steps, float gravity, float damage, float vehicleDamage)
        {
            var b = new Bullet { Pos = pos, Origin = pos, Vel = vel, StepsLeft = Mathf.Max(1, steps), Gravity = gravity, Damage = damage, VehicleDamage = vehicleDamage, Tracer = MakeTracer() };
            if (b.Tracer != null) { GetTree().CurrentScene?.AddChild(b.Tracer); UpdateTracer(b); }
            if (Gun?.Action == "Rocket") b.RocketVis = SpawnRocketVis(pos);   // launcher: the rocket is a VISIBLE flying projectile, not an invisible bullet
            _bullets.Add(b);
        }

        // Step every live bullet exactly like the source (UseableGun.cs:1539-1542): raycast this tick's segment for a
        // hit, else advance pos += vel*0.02 and apply gravity vel.y += g*0.02. Called once per 50 Hz physics tick.
        void StepBullets()
        {
            if (_bullets.Count == 0) return;
            var space = GetWorld3D().DirectSpaceState;
            for (int i = _bullets.Count - 1; i >= 0; i--)
            {
                var b = _bullets[i];
                Vector3 next = b.Pos + b.Vel * 0.02f;
                var query = PhysicsRayQueryParameters3D.Create(b.Pos, next, (1u << 0) | (1u << 1) | (1u << 4) | (1u << 5) | (1u << 6) | (1u << 9)); // world + enemy + ragdoll + vehicle + props + water surface
                var hit = space.IntersectRay(query);
                if (hit.Count > 0)
                {
                    Vector3 point = hit["position"].AsVector3();
                    Vector3 hdir = b.Vel.Normalized();
                    var collider = hit["collider"].As<GodotObject>();
                    if (collider is ZombieController z) { bool head = z.IsHeadshot(point); SpawnFleshImpact(point, hdir); bool wd = z.Dead; z.DamageHit(b.Damage, point, hdir); if (!wd && z.Dead) Kills++; HitmarkerHUD.Instance?.Show(head); }   // hitmarker: white body / red headshot (source EPlayerHit)
                    else if (collider is PhysicalBone3D pb) { SpawnFleshImpact(point, hdir); pb.ApplyImpulse(hdir * 7f, point - pb.GlobalPosition); }
                    else if (collider is Vehicle veh) { veh.TakeDamage(b.VehicleDamage); SpawnSurfaceImpact(point, hit["normal"].AsVector3(), Surf.Metal, veh); }   // source Vehicle_Damage (35) + metal sparks, hole follows the car
                    else   // world/prop/terrain -> material impact; terrain samples its splatmap PER-POINT (sand/road/dirt/grass) for the real ground material
                    {
                        Surf sf = Surf.Concrete;
                        if (collider is Node n)
                        {
                            if (Terrain.Active != null && n.IsInGroup("terrain")) sf = Terrain.Active.SurfAt(point.X, point.Z);
                            else if (n.HasMeta(SurfMeta)) sf = (Surf)(int)n.GetMeta(SurfMeta);
                        }
                        SpawnSurfaceImpact(point, hit["normal"].AsVector3(), sf);
                    }
                    if (Gun?.Action == "Rocket") { Explode(point, 9f, 250f, 200f, 300f); GD.Print("[rocket] launcher warhead detonated"); }   // rocket launcher: AoE blast on impact (vehicles hit hardest), reusing the grenade explode
                    RemoveBullet(i);
                    continue;
                }
                b.Pos = next;
                b.Vel += new Vector3(0f, b.Gravity * 0.02f, 0f);
                UpdateTracer(b);
                if (b.RocketVis != null && IsInstanceValid(b.RocketVis)) { b.RocketVis.GlobalPosition = b.Pos; var _vd = b.Vel.Normalized(); if (Mathf.Abs(_vd.Y) < 0.98f) b.RocketVis.LookAt(b.Pos + b.Vel, Vector3.Up); }   // fly the rocket model along the ballistic, nose along velocity
                if (--b.StepsLeft <= 0) RemoveBullet(i);
            }
        }

        void RemoveBullet(int i) { _bullets[i].Tracer?.QueueFree(); _bullets[i].RocketVis?.QueueFree(); _bullets.RemoveAt(i); }

        // The rocket launcher's projectile is a VISIBLE flying rocket (projectile.prefab Model_0; no _MainTex -> flat dark body).
        ArrayMesh _rocketMesh; bool _rocketTried;
        Node3D SpawnRocketVis(Vector3 pos)
        {
            if (!_rocketTried) { _rocketTried = true; try { _rocketMesh = ContentProvider.ParseObj("res://content/rocket_projectile.txt"); } catch { } }
            if (_rocketMesh == null) return null;
            var rv = new MeshInstance3D { Mesh = _rocketMesh, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.324f, 0.397f, 0.331f), Roughness = 0.75f, Metallic = 0f } };   // projectile.prefab material _Color (olive body) + _Glossiness 0.25 -> roughness 0.75
            GetTree().CurrentScene?.AddChild(rv);
            rv.GlobalPosition = pos;
            return rv;
        }

        Texture2D _bulletHoleTex; bool _bhTried;
        Texture2D BulletHoleTex()
        {
            if (!_bhTried) { _bhTried = true; string p = ProjectSettings.GlobalizePath("res://content/bullet_hole.png"); if (System.IO.File.Exists(p)) { var img = Image.LoadFromFile(p); if (img != null) _bulletHoleTex = ImageTexture.CreateFromImage(img); } }
            return _bulletHoleTex;
        }

        // Real per-material bullet-hole decal (Effects/Impacts/<mat>_WithDecal, MeshRenderer _MainTex extracted), cached.
        // Only hard surfaces (concrete/metal/wood) leave a hole; falls back to the generated bullet_hole if a texture's missing.
        readonly System.Collections.Generic.Dictionary<Surf, Texture2D> _decalTex = new System.Collections.Generic.Dictionary<Surf, Texture2D>();
        Texture2D DecalTex(Surf surf)
        {
            if (_decalTex.TryGetValue(surf, out var cached)) return cached;
            string name = surf switch { Surf.Metal => "metal", Surf.Wood => "wood", _ => "concrete" };
            string p = ProjectSettings.GlobalizePath($"res://content/decal_{name}.png");
            Texture2D t = null;
            if (System.IO.File.Exists(p)) { var img = Image.LoadFromFile(p); if (img != null) { img.GenerateMipmaps(); t = ImageTexture.CreateFromImage(img); } }
            _decalTex[surf] = t ??= BulletHoleTex();
            return t;
        }

        // surface materials for bullet impacts (a slice of the source EPhysicsMaterial set). Tagged on colliders via
        // SetMeta("surf", (int)Surf) -- terrain = Grass, vehicles = Metal, untagged (buildings/props) = Concrete.
        public enum Surf { Concrete, Grass, Dirt, Metal, Wood, Sand, Water }
        public const string SurfMeta = "surf";
        public static Color SurfDust(Surf s) => s switch
        {
            Surf.Grass => new Color(0.40f, 0.50f, 0.28f),
            Surf.Dirt  => new Color(0.45f, 0.35f, 0.25f),
            Surf.Metal => new Color(1f, 0.82f, 0.35f),
            Surf.Wood  => new Color(0.50f, 0.38f, 0.24f),
            Surf.Sand  => new Color(0.78f, 0.70f, 0.52f),
            Surf.Water => new Color(0.62f, 0.72f, 0.85f),   // pale blue-white splash
            _          => new Color(0.58f, 0.56f, 0.52f),   // concrete
        };

        // Bullet impact: a projected bullet-hole DECAL (hard surfaces only) + the REAL source impact effect debris burst
        // at the hit, oriented to the surface normal (Effects/Impacts/<mat>_static, extracted textures + params). Metal =
        // additive sparks; soft ground (grass/dirt/sand) = no decal.
        void SpawnSurfaceImpact(Vector3 point, Vector3 normal, Surf surf, Node3D attachTo = null)
        {
            if (System.Environment.GetEnvironmentVariable("UG_IMPACTDEBUG") == "1") GD.Print($"[impact] surf={surf} @ {point.Round()} tex={(ImpactTex(surf) != null)}");
            var scene = GetTree().CurrentScene;
            if (scene == null) { GD.PrintErr("[impact] CurrentScene NULL -> no impact spawned"); return; }
            Vector3 up = normal.Normalized();
            bool hard = surf is Surf.Concrete or Surf.Metal or Surf.Wood;
            bool metal = surf == Surf.Metal;
            var tex = DecalTex(surf);
            if (hard && tex != null)
            {
                var dec = new Decal { TextureAlbedo = tex, Size = new Vector3(0.16f, 0.3f, 0.16f), AlbedoMix = 1f, Modulate = Colors.White };   // real per-material decal carries its own colour
                (attachTo ?? (Node)scene).AddChild(dec);   // vehicle hits: parent to the car so the hole FOLLOWS it (master); world hits: static in the scene
                Vector3 t = Mathf.Abs(up.Dot(Vector3.Up)) < 0.95f ? Vector3.Up : Vector3.Right;
                Vector3 right = up.Cross(t).Normalized();
                dec.GlobalTransform = new Transform3D(new Basis(right, up, right.Cross(up)), point + up * 0.06f);   // +Y = normal -> projects DOWN into the surface (local-to-parent once attached)
                var t1 = GetTree().CreateTimer(18.0); t1.Timeout += () => { if (IsInstanceValid(dec)) dec.QueueFree(); };
            }
            // Source ParticleSystem (Effects/Impacts/<mat>_static): a one-shot BURST of debris -- concrete/wood/gravel/foliage
            // = 8 @ 0.25-0.5m, 2-4 m/s; metal = 16 @ 0.125-0.25m, 4-8 m/s; all gravityModifier 1 (fall), ~1s life. The debris
            // sheets are 4 frames (a random chip per particle); metal is one spark sprite.
            var itex = ImpactTex(surf);
            bool sheet = itex != null && itex.GetWidth() >= itex.GetHeight() * 3;   // 32x8 debris strip = 4 chips; 16x16 metal = single
            var mat = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                AlbedoColor = Colors.White, BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
                BlendMode = metal ? BaseMaterial3D.BlendModeEnum.Add : BaseMaterial3D.BlendModeEnum.Mix,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
            };
            if (itex != null) mat.AlbedoTexture = itex;
            if (sheet) { mat.ParticlesAnimHFrames = 4; mat.ParticlesAnimVFrames = 1; mat.ParticlesAnimLoop = false; }
            if (metal) { mat.EmissionEnabled = true; if (itex != null) mat.EmissionTexture = itex; mat.Emission = new Color(1f, 0.7f, 0.2f); mat.EmissionEnergyMultiplier = 2.5f; }
            var dust = new CpuParticles3D
            {
                Emitting = true, OneShot = true, Amount = metal ? 16 : 8, Lifetime = 1.0f, Explosiveness = 1f,
                Direction = up, Spread = 70f, InitialVelocityMin = metal ? 4f : 2f, InitialVelocityMax = metal ? 8f : 4f,
                Gravity = new Vector3(0f, -9.8f, 0f), ScaleAmountMin = metal ? 0.125f : 0.25f, ScaleAmountMax = metal ? 0.25f : 0.5f,
                Mesh = new QuadMesh { Size = Vector2.One, Material = mat },
            };
            if (sheet) { dust.AnimOffsetMin = 0f; dust.AnimOffsetMax = 1f; }   // random static chip frame per particle
            scene.AddChild(dust);
            dust.GlobalPosition = point + up * 0.03f;
            var t2 = GetTree().CreateTimer(1.4); t2.Timeout += () => { if (IsInstanceValid(dust)) dust.QueueFree(); };
            PlayImpactSound(ImpactSnd(surf), point);   // source impact effects carry per-material audio
        }

        // Real impact-effect debris texture per surface (Effects/Impacts/<mat>_static extracted PNG), cached. Surf->effect:
        // grass=foliage, dirt/sand=gravel, metal/wood/concrete same-named.
        readonly System.Collections.Generic.Dictionary<Surf, ImageTexture> _impactTex = new System.Collections.Generic.Dictionary<Surf, ImageTexture>();
        ImageTexture ImpactTex(Surf surf)
        {
            if (_impactTex.TryGetValue(surf, out var cached)) return cached;
            string name = surf switch
            {
                Surf.Metal => "metal", Surf.Wood => "wood", Surf.Sand => "gravel",
                Surf.Grass => "foliage", Surf.Dirt => "gravel", Surf.Water => "water", _ => "concrete",
            };
            string p = ProjectSettings.GlobalizePath($"res://content/impact_{name}_static_0.png");
            ImageTexture tex = null;
            if (System.IO.File.Exists(p)) { var img = Image.LoadFromFile(p); if (img != null) tex = ImageTexture.CreateFromImage(img); }
            _impactTex[surf] = tex;
            return tex;
        }

        // Impact SOUND — each source impact effect carries its own audio (Effects/Impacts/<mat>/<mat>.mp3), extracted to WAV.
        // A 3D one-shot at the hit point, cached per surface. grass=foliage, dirt/sand=gravel, else same-named.
        static readonly System.Collections.Generic.Dictionary<Surf, AudioStream> _impactSnd = new System.Collections.Generic.Dictionary<Surf, AudioStream>();
        static AudioStream _fleshSnd; static bool _fleshSndTried;
        static AudioStream LoadWav(string rel)
        {
            string p = ProjectSettings.GlobalizePath(rel);
            return System.IO.File.Exists(p) ? AudioStreamWav.LoadFromFile(p) : null;
        }
        AudioStream ImpactSnd(Surf surf)
        {
            if (_impactSnd.TryGetValue(surf, out var cached)) return cached;
            string name = surf switch
            {
                Surf.Metal => "metal", Surf.Wood => "wood", Surf.Sand => "gravel",
                Surf.Grass => "foliage", Surf.Dirt => "gravel", Surf.Water => "water", _ => "concrete",
            };
            var a = LoadWav($"res://content/impact_{name}.wav");
            _impactSnd[surf] = a;
            return a;
        }
        void PlayImpactSound(AudioStream a, Vector3 pos)
        {
            if (a == null) return;
            var scene = GetTree().CurrentScene;
            if (scene == null) return;
            var pl = new AudioStreamPlayer3D { Stream = a, UnitSize = 5f, MaxDistance = 70f, VolumeDb = -3f };
            scene.AddChild(pl);
            pl.GlobalPosition = pos;
            pl.Play();
            pl.Finished += () => { if (IsInstanceValid(pl)) pl.QueueFree(); };
            if (System.Environment.GetEnvironmentVariable("UG_IMPACTDEBUG") == "1") GD.Print($"[impactaudio] played @ {pos.Round()}");
        }

        // The traveling tracer: a thin additive "Bullet"-textured streak that rides with the bullet, oriented along
        // its velocity (the Military_30's Trail_0). Made once per bullet; UpdateTracer re-places it each step.
        MeshInstance3D MakeTracer()
        {
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
            return new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.05f, 0.05f, 5f) }, MaterialOverride = mat };
        }

        void UpdateTracer(Bullet b)
        {
            if (b.Tracer == null) return;
            Vector3 axis = b.Vel.LengthSquared() > 1e-6f ? b.Vel.Normalized() : Vector3.Forward;
            // the streak trails from the MUZZLE (Origin) up to the bullet, capped at 5 m -- so it never extends behind
            // the barrel toward the camera (master: tracer should come from the barrel, not the eye).
            float len = Mathf.Min(5f, b.Pos.DistanceTo(b.Origin));
            if (len < 0.02f) { b.Tracer.Visible = false; return; }
            b.Tracer.Visible = true;
            Vector3 back = b.Pos - axis * len;
            Vector3 up = Mathf.Abs(axis.Dot(Vector3.Up)) > 0.99f ? Vector3.Right : Vector3.Up;
            b.Tracer.LookAtFromPosition((back + b.Pos) * 0.5f, b.Pos, up);   // centred between muzzle-side + head
            b.Tracer.Scale = new Vector3(1f, 1f, len / 5f);                   // shrink the 5 m box to the trail length
        }

        // Flesh impact — the REAL source Flesh_Dynamic effect (impact ID 5), extracted texture + params: a 16-particle
        // burst of the 4-frame blood sprite, size 0.5-1.0m, 3-6 m/s, gravityModifier 1, ~1s life, sprayed back out of the
        // wound (-dir). One-shot GpuParticles3D at the world hit point, auto-freed. (Was a flat-red placeholder quad @ 24
        // particles / 0.1m — now the real blood texture at source counts/sizes.)
        void SpawnFleshImpact(Vector3 point, Vector3 dir)
        {
            if (!_fleshTexTried)
            {
                _fleshTexTried = true;
                string fp = ProjectSettings.GlobalizePath("res://content/impact_flesh_dynamic_0.png");
                if (System.IO.File.Exists(fp)) { var fi = Image.LoadFromFile(fp); if (fi != null) _fleshTex = ImageTexture.CreateFromImage(fi); }
            }
            var pm = new ParticleProcessMaterial
            {
                Direction = -dir, Spread = 60f,
                InitialVelocityMin = 3f, InitialVelocityMax = 6f,       // source startSpeed 3-6
                Gravity = new Vector3(0f, -9.8f, 0f),                   // gravityModifier 1
                ScaleMin = 0.5f, ScaleMax = 1.0f,                       // source startSize 0.5-1.0m (QuadMesh Size 1 -> metres)
                Color = Colors.White,                                   // texture supplies the blood red
                AnimOffsetMin = 0f, AnimOffsetMax = 1f,                 // random static blood frame per particle (4-frame sheet)
            };
            var mat = new StandardMaterial3D
            {
                AlbedoColor = Colors.White, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
            };
            if (_fleshTex != null) { mat.AlbedoTexture = _fleshTex; mat.ParticlesAnimHFrames = 4; mat.ParticlesAnimVFrames = 1; mat.ParticlesAnimLoop = false; }
            else mat.AlbedoColor = new Color(0.5f, 0.02f, 0.02f);       // fallback: red if the texture is missing
            var ps = new GpuParticles3D
            {
                Amount = 16, Lifetime = 1.0, OneShot = true, Explosiveness = 1f,   // source burst of 16
                ProcessMaterial = pm,
                DrawPass1 = new QuadMesh { Size = Vector2.One, Material = mat },
                Emitting = true,
            };
            GetTree().CurrentScene?.AddChild(ps);
            ps.GlobalPosition = point;
            var timer = GetTree().CreateTimer(1.4);
            timer.Timeout += () => { if (IsInstanceValid(ps)) ps.QueueFree(); };
            if (!_fleshSndTried) { _fleshSndTried = true; _fleshSnd = LoadWav("res://content/impact_flesh.wav"); }
            PlayImpactSound(_fleshSnd, point);   // source flesh impact carries blood-splat audio
        }

        static Texture2D _tracerTex;      // the "Bullet" sprite, loaded once (shared by MakeTracer)
        static bool _tracerTexTried;
        static Texture2D _fleshTex; static bool _fleshTexTried;   // the real Flesh_Dynamic blood sprite (loaded once)

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
            if (_interpReady && !_dead && _driving == null)   // RENDER INTERPOLATION (master): lerp the visual position between the last two 50Hz ticks so it doesn't step at 50Hz while rendering at 60+
                GlobalPosition = _interpPrev.Lerp(_interpCurr, (float)Engine.GetPhysicsInterpolationFraction());
            if (_driving != null && !_dead)   // driving: position the cam from the vehicle's Godot-INTERPOLATED visual transform, so cam + car mesh are both smooth + IN SYNC (master: godot smoothing for the car)
                PositionDriveCam(_driving.GetGlobalTransformInterpolated());
            UpdateLookFocus();   // look-at item interaction (master): eye-ray -> focus the item you're aiming at (rarity glow + name)
            // Additive recoil (master): drain the pending kick INTO the real aim over a couple frames (a smooth climb),
            // then leave it there -- the view stays kicked up and the player pulls the mouse back down. Never recovers on its own.
            if (_recoilPending != 0f || _recoilYawPending != 0f)
            {
                float step = Mathf.Min(1f, 18f * (float)delta);
                float dp = _recoilPending * step;
                _pitchDeg = Mathf.Clamp(_pitchDeg + dp, -89f, 89f);   // pitch folds into the actual aim -- stays put
                _recoilPending -= dp;
                float dy = _recoilYawPending * step;
                RotateY(Mathf.DegToRad(dy));                          // yaw folds into the body -- stays put
                _recoilYawPending -= dy;
            }
            PainAlpha = Mathf.Max(0f, PainAlpha - (float)delta);                 // hurt flash fades at 1/s (PlayerUI line 1835)
            // flinch recovers to level at 4/s (PlayerLook line 1330). GUARD: a degenerate hit can leave _flinch NaN or
            // denormalized, and Godot's Slerp/Basis assert IsNormalized -> that was the "Quaternion is not normalized" spam.
            if (!_flinch.IsFinite() || _flinch.LengthSquared() < 1e-6f) _flinch = Quaternion.Identity;
            _flinch = _flinch.Normalized().Slerp(Quaternion.Identity, 4f * (float)delta);
            if (_cam != null && !_dead && _driving == null)   // while driving, DriveVehicle (in _PhysicsProcess) owns the cam
            {
                if (_fp)
                {
                    // FP: eye height follows the stance (PlayerLook.heightLook 1.75/1.2/0.35, lerped 4/s), pitched by the mouse
                    float targetEye = Stance switch { EPlayerStance.CROUCH => 1.2f, EPlayerStance.PRONE => 0.35f, _ => 1.75f };
                    var cp = _cam.Position; cp.X = 0f; cp.Z = 0f; cp.Y = Mathf.Lerp(cp.Y, targetEye, 4f * (float)delta); _cam.Position = cp;
                    var look = Basis.FromEuler(new Vector3(Mathf.DegToRad(_pitchDeg), 0f, 0f), EulerOrder.Yxz);   // flinch left-multiplies the look
                    _cam.Basis = new Basis(_flinch) * look;
                }
                else
                {
                    // 3rd person on foot: chase behind + above (child of the player, so it follows the body yaw); mouse Y orbits a bit
                    _cam.Position = new Vector3(0f, 1.9f, 3.4f);
                    _cam.Rotation = new Vector3(Mathf.DegToRad(Mathf.Clamp(_pitchDeg * 0.5f, -40f, 25f) - 6f), 0f, 0f);
                }
            }
            UpdateBody(delta);
        }

        // live 3rd-person body: shown when !_fp; stands at the player (facing the body yaw, animated by ground speed) or sits in the driver seat
        void UpdateBody(double delta)
        {
            if (_viewmodel != null) _viewmodel.SetShown(_fp && _driving == null && !_dead);   // FP gun arms: first-person on foot only
            if (_body == null) return;
            _body.Visible = !_fp && !_dead;   // dead -> the corpse ragdoll handles the body
            if (_fp || _dead) { return; }
            if (_driving != null)   // in the driver seat (best-effort idle pose)
            {
                _body.GlobalTransform = _driving.GlobalTransform * new Transform3D(Basis.Identity, _driving.SeatOffset);   // per-vehicle driver seat (prefab Seat_0)
                _body.SetLocomotion(0f);
            }
            else   // on foot: at the player's feet, facing the body yaw, locomotion by horizontal speed
            {
                _body.GlobalPosition = GlobalPosition;
                _body.Rotation = new Vector3(0f, Rotation.Y, 0f);
                _body.SetLocomotion(new Vector2(Velocity.X, Velocity.Z).Length(), Stance);   // crouch/prone anims by stance (master)
            }
            _body.Tick(delta);
        }

        // --- Vehicle enter/exit (source: InteractableVehicle). E enters the nearest vehicle's driver seat / exits. ---
        public bool IsDriving => _driving != null;
        public Vehicle Driving => _driving;   // the vehicle being driven (for zombies to swipe at, source targetPassengerVehicle)
        public void SetSuppressor(bool on) => _viewmodel?.SetSlotAttached("Barrel", on);   // test hook: toggle the silenced barrel

        Vehicle NearestVehicle()
        {
            Vehicle best = null; float bestD = 4.0f * 4.0f;   // within ~4 m
            foreach (var n in GetTree().GetNodesInGroup("vehicles"))
                if (n is Vehicle v && !v.Exploded)   // a wrecked car can't be entered (master); E near only a wreck falls through to pickup
                {
                    float d = GlobalPosition.DistanceSquaredTo(v.GlobalPosition);
                    if (d < bestD) { bestD = d; best = v; }
                }
            return best;
        }

        public HUD Hud;   // set by the scene builder; the vehicle status box binds to the driven vehicle on enter/exit

        void EnterVehicle(Vehicle v)
        {
            _driving = v;
            _burstLeft = 0;                                    // entering a vehicle cancels an in-progress burst (no resume on exit)
            v.EngineOn = true;                                 // start burning fuel (source: engine on)
            if (Hud != null) Hud.Vehicle = v;                  // show the vehicle status box (fuel/health/battery)
            _viewmodel?.SetShown(false);                       // no gun while driving
            if (_cam != null) _cam.TopLevel = true;            // free the camera into world space
            foreach (var c in FindChildren("*", "CollisionShape3D", true, false))
                if (c is CollisionShape3D cs) cs.Disabled = true;   // stop the player body fighting the vehicle
            Visible = false;
            Velocity = Vector3.Zero;
        }

        void ExitVehicle()
        {
            var v = _driving; _driving = null;
            if (v != null) { v.EngineOn = false; v.Park(); }   // stop burning fuel + brake so it doesn't roll away
            if (Hud != null) Hud.Vehicle = null;               // hide the vehicle status box
            if (v != null) GlobalPosition = v.GlobalPosition + v.GlobalTransform.Basis.X * 2.4f + Vector3.Up * 1.0f;
            foreach (var c in FindChildren("*", "CollisionShape3D", true, false))
                if (c is CollisionShape3D cs) cs.Disabled = false;
            Visible = true;
            _viewmodel?.SetShown(true);
            if (_cam != null) { _cam.TopLevel = false; _cam.Position = new Vector3(0f, 1.6f, 0f); _cam.Rotation = Vector3.Zero; }
            _pitchDeg = 0f;
        }

        public Vector2? ScriptedDrive;   // test hook: (steer, throttle) instead of keys
        public bool DriveFP { set => _fp = value; }   // test hook: force first-person cam
        public void EnterNearestVehicle() { var v = NearestVehicle(); if (v != null) EnterVehicle(v); }

        void DriveVehicle(float delta)
        {
            if (_driving.Exploded) { ExitVehicle(); TakeDamage(150f); return; }   // caught in the blast -> ejected + killed (source explode kills passengers)
            float throttle, steer;
            if (ScriptedDrive.HasValue) { steer = ScriptedDrive.Value.X; throttle = ScriptedDrive.Value.Y; }
            else
            {
                throttle = (Input.IsPhysicalKeyPressed(Key.W) ? 1f : 0f) - (Input.IsPhysicalKeyPressed(Key.S) ? 1f : 0f);
                steer = (Input.IsPhysicalKeyPressed(Key.D) ? 1f : 0f) - (Input.IsPhysicalKeyPressed(Key.A) ? 1f : 0f);
            }
            _driving.Drive(throttle, steer, Input.IsPhysicalKeyPressed(Key.Space));
            GlobalPosition = _driving.GlobalPosition;   // ride along so exit + FP cam land at the vehicle (the cam is positioned in _Process from the vehicle's INTERPOLATED transform)
        }

        void PositionDriveCam(Transform3D vt)   // FP / chase cam from the (interpolated) vehicle transform. Full global transform atomically
        {                                        // (position + orientation): a LookAt updated pos but not rotation through turns -> car slid out of frame.
            if (_cam == null) return;
            var fwd = -vt.Basis.Z; fwd.Y = 0f;
            fwd = fwd.LengthSquared() > 0.001f ? fwd.Normalized() : Vector3.Forward;
            if (_fp)   // first-person from the driver's head, looking forward over the hood
            {
                var eye = vt * new Vector3(-0.4f, 1.85f, 0.4f);
                _cam.GlobalTransform = new Transform3D(Basis.Identity, eye).LookingAt(vt * new Vector3(-0.4f, 1.25f, -3.5f), Vector3.Up);
            }
            else            // third-person chase (Unturned default): behind + above the car's heading, looking at it
            {
                var eye = vt.Origin - fwd * 7.5f + Vector3.Up * 3.2f;
                _cam.GlobalTransform = new Transform3D(Basis.Identity, eye).LookingAt(vt.Origin + Vector3.Up * 0.7f, Vector3.Up);
            }
        }

        public override void _PhysicsProcess(double delta)
        {
            if (_pdieTest > 0) { _pdieTest -= delta; if (_pdieTest <= 0) { _pdieTest = -1; TakeDamage(9999f); } }
            // below-map kill: Unturned Level.isPointWithinValidHeight = y in [-1024,1024]; fall past the map floor -> die + respawn (covers driving too)
            if (!_dead && GlobalPosition.Y < -1030f) { GD.Print("[oob] fell below the map -> killed"); TakeDamage(9999f); }
            if (_driving != null) { _interpReady = false; DriveVehicle((float)delta); return; }   // driving: skip on-foot movement (+ pause the render-interp so exiting doesn't smear)
            if (_interpReady && !_dead) GlobalPosition = _interpCurr;   // render-interp (master): restore the TRUE physics position before moving (undoes the _Process visual smoothing)
            StepBullets();   // advance in-flight bullets (travel + drop) each 50 Hz tick — matches the source 0.02s step
            if (_bleedTimer > 0) { _bleedTimer -= delta; if (_bleedTimer <= 0) Bleeding = false; }
            if (_dead)
            {
                _deathTimer -= delta;
                Velocity = Vector3.Zero;
                if (_deathTimer <= 0) Respawn();
                return;
            }
            if (_fireCd > 0f) _fireCd -= (float)delta;
            if (_meleeCd > 0f) _meleeCd -= (float)delta;
            if (_burstCd > 0f) _burstCd -= (float)delta;
            if (_grenadeCd > 0f) _grenadeCd -= (float)delta;
            if (_reloading)
            {
                _reloadTimer -= delta;
                if (_reloadTimer <= 0)
                {
                    int max = Gun?.AmmoMax ?? 30;
                    if (Gun?.ShellReload == true)   // shotgun: load ONE shell, then queue the next shell (or finish when full)
                    {
                        Ammo = System.Math.Min(Ammo + 1, max);
                        if (Ammo >= max) { _reloading = false; _viewmodel?.SetReloading(false); }
                        else { _reloadTimer = (_viewmodel?.ReloadLength ?? ReloadTime) / System.Math.Max(1, max); _viewmodel?.SetReloading(true); }
                    }
                    else { Ammo = max; _reloading = false; _viewmodel?.SetReloading(false); }   // magazine swap: whole mag at once
                }
            }
            // burst rounds + full-auto hold fire on cooldown (Fire() still enforces ammo/reload/cd)
            if (_fireCd <= 0f && !_reloading)
            {
                if (_burstLeft > 0) { if (Fire()) { _burstLeft--; if (_burstLeft == 0) _burstCd = 0.2f; } else _burstLeft = 0; }
                else if (_firemode == FireMode.Auto && Input.IsMouseButtonPressed(MouseButton.Left)) Fire();
            }

            // Intertwined stance STATE MACHINE (master): X = crouch key, Z = prone key, moving between STAND/CROUCH/PRONE from ANY state.
            bool xNow = Input.IsPhysicalKeyPressed(Key.X);
            if (xNow && !_xHeld) _baseStance = (_baseStance == EPlayerStance.CROUCH) ? EPlayerStance.STAND : EPlayerStance.CROUCH;   // X: stand<->crouch, and prone->crouch
            _xHeld = xNow;
            bool zNow = Input.IsPhysicalKeyPressed(Key.Z);
            if (zNow && !_zHeld) _baseStance = (_baseStance == EPlayerStance.PRONE) ? EPlayerStance.STAND : EPlayerStance.PRONE;    // Z: stand<->prone, and crouch->prone
            _zHeld = zNow;
            var wantStance = ScriptedStance ?? _baseStance;
            if (wantStance == EPlayerStance.STAND && Input.IsPhysicalKeyPressed(Key.Shift) && Stamina > 0.05f) wantStance = EPlayerStance.SPRINT;   // sprint overlays standing
            if (Broken && wantStance == EPlayerStance.SPRINT) wantStance = EPlayerStance.STAND;   // broken legs can't sprint (PlayerStance.cs:703)
            // can't rise into a ceiling: if the target stance is TALLER than the current capsule and there's no headroom, stay low (master)
            float wantH = PlayerMovementDef.HeightForStance(wantStance);
            if (wantH > _capStance + 0.01f && _capStance > 0f && !HeadroomFor(wantH))
                wantStance = _baseStance = (_capStance <= PlayerMovementDef.HEIGHT_PRONE + 0.01f) ? EPlayerStance.PRONE : EPlayerStance.CROUCH;   // blocked overhead -> stay in the stance that fits
            _move.Stance = wantStance;
            UpdateHitbox(_move.Stance);   // resize the collision capsule to match the stance (source HeightForStance)

            float forward, strafe;
            if (ScriptedInput.HasValue) { strafe = ScriptedInput.Value.x; forward = ScriptedInput.Value.y; }
            else
            {
                forward = (Input.IsPhysicalKeyPressed(Key.W) ? 1f : 0f) - (Input.IsPhysicalKeyPressed(Key.S) ? 1f : 0f);
                strafe  = (Input.IsPhysicalKeyPressed(Key.D) ? 1f : 0f) - (Input.IsPhysicalKeyPressed(Key.A) ? 1f : 0f);
            }
            bool jump = Input.IsPhysicalKeyPressed(Key.Space) && !Broken;   // broken legs can't jump (PlayerMovement.cs:1310)

            // feed the viewmodel its locomotion so the walk bob picks the right SPEED_*/BOB_* + gates on movement
            bool moving = Mathf.Abs(forward) > 0.01f || Mathf.Abs(strafe) > 0.01f;
            Moving = moving;                                  // exposed for zombie stealth detection
            _viewmodel?.SetLocomotion(moving, _move.Stance);
            UpdateVitals(moving, (float)delta);

            // Phase 3 hearing: moving on foot makes FOOTSTEP noise the zombies can hear, loudness = the source stealth
            // detection radius by stance/speed (sprint 20 loud .. prone 3 near-silent). Throttled; a motionless player
            // makes no sound (must be SEEN instead). Zombies within earshot path to it via SoundBus.Hear.
            _footNoiseT -= (float)delta;
            if (moving && _footNoiseT <= 0f)
            {
                _footNoiseT = 0.4f;
                float loud = GetStealthDetectionRadius();
                if (loud > 2f) SoundBus.Emit(GetTree(), GlobalPosition, loud);
            }

            var v = _move.Step(new UnityEngine.Vector2(strafe, forward), jump, IsOnFloor(), (float)delta);
            Vector3 world = GlobalTransform.Basis * new Vector3(v.x, 0f, -v.z);
            bool wasAirborne = !IsOnFloor();                  // ground state going into this step
            Velocity = new Vector3(world.X, v.y, world.Z);
            StepUp((float)delta);   // climb small curbs/thresholds so we don't snag (master)
            MoveAndSlide();
            _interpPrev = _interpReady ? _interpCurr : GlobalPosition; _interpCurr = GlobalPosition; _interpReady = true;   // snapshot this tick's start/end for render interpolation (master)
            if (wasAirborne && IsOnFloor()) CheckFallDamage(v.y);   // just touched down -> fall damage on a hard landing
        }
    }
}
