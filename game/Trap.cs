using Godot;
using SDG.Unturned;

namespace UnturnedGodot
{
    // The Godot half of a trap barricade (src InteractableTrap + InteractableTrapTrigger). The parent Deployable owns
    // placement / health / salvage / the damage lifecycle exactly like any other barricade; this node is only the
    // trigger volume and the damage hookup. Every GATE (setup delay, cooldown, power, self-collider, the PvP and
    // riding-a-vehicle rules) lives engine-free in TrapRule so it is unit-testable without a scene -- this class just
    // reduces the live contact to booleans, asks TrapRule, and performs the verdict.
    //
    // src: Assets/Runtime/Assembly-CSharp/Unturned/Interactable/InteractableTrap.cs (U3-SDK).
    public partial class Trap : Area3D
    {
        Deployable _owner;
        DeployableDef _def;
        float _lastActive;      // src `lastActive`: when the trap went live (OnEnable) -- the setup delay counts from here
        float _lastTriggered;   // src `lastTriggered`: last contact that spent the cooldown

        // Trap triggers must see BODIES (players/zombies/vehicles are CharacterBody3D/VehicleBody3D), not areas.
        // The volume is a slab the size of the barricade footprint, lifted so it sits ON the pad rather than inside it.
        public static Trap Make(Deployable owner, DeployableDef def, Aabb meshAabb)
        {
            var t = new Trap
            {
                _owner = owner,
                _def = def,
                Monitoring = true,
                Monitorable = false,                      // nothing needs to detect the trigger ITSELF
                CollisionLayer = 0,                       // the trigger is not solid and is never hit by rays
                CollisionMask = TriggerMask,
            };
            Vector3 foot = meshAabb.Size == Vector3.Zero ? def.Size : meshAabb.Size;
            // widen a little so a walker actually clips it (the src trigger colliders are hand-authored per model and
            // are generously bigger than the visible pad) and give it real height so a running capsule can't tunnel.
            var box = new BoxShape3D { Size = new Vector3(Mathf.Max(foot.X, 0.5f) + 0.25f, Mathf.Max(foot.Y, 0.5f) + 0.25f, TriggerHeight) };
            t.AddChild(new CollisionShape3D { Shape = box, Position = meshAabb.GetCenter() });
            return t;
        }

        // Players/zombies/vehicles all sit on the character/vehicle physics layers; see reference collision-layer map.
        // Bit 0 (world/static) is deliberately excluded so terrain and props never trip a trap.
        const uint TriggerMask = (1u << 1) | (1u << 2) | (1u << 5) | (1u << 6);
        const float TriggerHeight = 0.9f;

        // Seconds of SIM time since the trap went live. The src uses Time.realtimeSinceStartup, but this port has a
        // sim-speed console command (and a headless test host that runs ticks as fast as it can), so wall-clock would
        // make a trap's arming window drift against the world it lives in -- a 4x sim would arm it in 4x the game time.
        // Accumulating _Process delta is the same thing the neighbouring Deployable timers (_deadTimer/_burnTime) do.
        float _age;

        public override void _Ready()
        {
            _lastActive = 0f;          // src OnEnable: the setup delay is measured from the moment the trap goes live
            _lastTriggered = float.NegativeInfinity;
            BodyEntered += OnBodyEntered;
        }

        public override void _Process(double delta) => _age += (float)delta;

        float Now() => _age;

        void OnBodyEntered(Node3D body)
        {
            if (_owner == null || _def == null || !IsInstanceValid(_owner)) return;
            if (_owner.OnFire) return;   // a burning/dead barricade is not an armed trap

            TrapTarget target = TrapTarget.Other;
            bool riding = false, hyper = false;
            var player = body as PlayerController;
            var zombie = body as ZombieController;

            if (player != null)
            {
                target = TrapTarget.Player;
                riding = player.IsRiding || player.Driving != null;   // src: other.transform.parent CompareTag("Vehicle")
            }
            else if (zombie != null)
            {
                if (zombie.Dead) return;
                target = TrapTarget.Zombie;
            }
            else if (body.IsInGroup("animals")) target = TrapTarget.Animal;

            var d = TrapRule.Evaluate(
                target,
                otherIsTrigger: false,                       // Godot BodyEntered only reports real bodies (Area3D contacts arrive on AreaEntered)
                isSelfOrChild: body == _owner || body.IsAncestorOf(_owner) || _owner.IsAncestorOf(body),
                now: Now(), lastActive: _lastActive, setupDelay: _def.TrapSetupDelay,
                lastTriggered: _lastTriggered, cooldown: _def.TrapCooldown,
                requiresPower: _def.TrapRequiresPower, isWired: _owner.IsPowered,
                isExplosive: _def.TrapExplosive, isBroken: _def.TrapBroken, explosionLaunchSpeed: _def.TrapLaunchSpeed,
                isPvP: PvP, targetRidingVehicle: riding, zombieIsHyper: hyper);

            if (d.Consumed) _lastTriggered = Now();
            if (d.Action == TrapAction.None && d.SelfWear <= 0f) return;

            switch (d.Action)
            {
                case TrapAction.Explode:
                    // src damages the barricade BEFORE the blast so the trap dies even when a server's barricade-armor
                    // multiplier would zero out the blast's self-damage (Nelson 2025-08-25, public issue #5188).
                    _owner.TakeDamage(d.SelfWear);
                    Detonate(_owner.GlobalPosition, _def);
                    GD.Print($"[trap] {_def.Name} detonated at {_owner.GlobalPosition}");
                    return;

                case TrapAction.ShredPlayer:
                    player?.TakeDamage(_def.TrapPlayerDamage, GlobalPosition);
                    if (d.BreakLegs && player != null) player.Broken = true;   // src life.breakLegs()
                    _owner.TakeDamage(d.SelfWear);
                    GD.Print($"[trap] {_def.Name} shredded a player for {_def.TrapPlayerDamage}{(d.BreakLegs ? " (legs broken)" : "")}");
                    return;

                case TrapAction.DamageZombie:
                    zombie?.DamageHit(_def.TrapZombieDamage, zombie.GlobalPosition, Vector3.Up);
                    _owner.TakeDamage(d.SelfWear);
                    GD.Print($"[trap] {_def.Name} hit a zombie for {_def.TrapZombieDamage}");
                    return;

                case TrapAction.DamageAnimal:
                    // GAP (honest): AnimalAgent has no damage/health surface in the port yet, so there is nothing to
                    // damage. The trap still takes its wear (src does) and the rule is already correct + tested -- wiring
                    // the victim is a one-liner the day animals become damageable.
                    _owner.TakeDamage(d.SelfWear);
                    GD.Print($"[trap] {_def.Name} caught an animal (no animal damage model yet -- wear only)");
                    return;
            }
        }

        // Server PvP flag (src Provider.isPvP). The port has no server settings object yet; PvP is the Unturned default
        // and every PEI trap is a PvP-server item, so it defaults ON and is overridable for testing.
        public static bool PvP = System.Environment.GetEnvironmentVariable("UG_PVE") != "1";

        // The trap blast (src DamageTool.explode via ExplosionParameters). PlayerController.Explode is the grenade's
        // thrower-centric version -- it only damages the ONE player that threw it -- so a world-owned explosion needs
        // its own pass over every registered player. Zombie/vehicle falloff and the line-of-sight rule match it exactly.
        public static void Detonate(Vector3 point, DeployableDef def)
        {
            var tree = (Engine.GetMainLoop() as SceneTree);
            if (tree == null) return;
            float radius = def.TrapRange2;

            foreach (var n in tree.GetNodesInGroup("zombies"))
                if (n is ZombieController z && !z.Dead)
                {
                    float range = z.GlobalPosition.DistanceTo(point);
                    if (range > radius || Blocked(z, point, z.GlobalPosition)) continue;
                    z.DamageHit(ExplosionMath.Linear(def.TrapZombieDamage, range, radius), z.GlobalPosition, (z.GlobalPosition - point).Normalized());
                }

            foreach (var n in tree.GetNodesInGroup("vehicles"))
                if (n is Vehicle v && !v.Exploded)
                {
                    float range = v.GlobalPosition.DistanceTo(point);
                    if (range > radius || Blocked(v, point, v.GlobalPosition)) continue;
                    v.TakeDamage(ExplosionMath.Linear(def.TrapVehicleDamage, range, radius));
                }

            foreach (var p in PlayerRegistry.All)
            {
                if (p == null || !IsInstanceValid(p)) continue;
                float range = p.GlobalPosition.DistanceTo(point);
                if (range > radius || Blocked(p, point, p.GlobalPosition)) continue;
                float t = ExplosionMath.Squared(def.TrapPlayerDamage, range, radius);   // src: players use SQUARED falloff
                if (t > 0f) p.TakeDamage(t * (p.Inventory?.ExplosionArmor ?? 1f));
            }

            PlayerRegistry.FlinchAllFromExplosion(point, Mathf.Max(radius * 2f, 12f), 30f);
        }

        // src ExplosionDamageParameters.LineOfSightTest -- a wall between the blast and the target shields it. Both ends
        // are raised to chest height so the ray doesn't graze the ground (mirrors PlayerController.ExplosionBlocked).
        static bool Blocked(Node3D probe, Vector3 point, Vector3 target)
        {
            if (probe.GetWorld3D() == null) return false;
            var q = PhysicsRayQueryParameters3D.Create(point + Vector3.Up * 0.8f, target + Vector3.Up * 0.8f, ZombieNav.WorldLayer);
            return probe.GetWorld3D().DirectSpaceState.IntersectRay(q).Count > 0;
        }
    }
}
