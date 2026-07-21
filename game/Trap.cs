using Godot;

namespace UnturnedGodot
{
    public enum ETrapKind { Spike, Barbedwire, Caltrop, Landmine }

    // TRAP -- source InteractableTrap + ItemTrapAsset. A placed barricade with a trigger volume: when a solid entity
    // ENTERS (edge-triggered, after the 0.25 s Trap_Setup_Delay arm time -- so the placer isn't caught by their own trap),
    // it fires. Two families, both modelled here from the real .dat values:
    //   * non-explosive (Spikes id383 / Barbedwire id386 / Caltrop id382): direct damage to the entity that entered
    //     (Zombie/Player/Animal_Damage). A "Broken" trap (the Snare bear-trap) breaks the victim's legs. Each trigger
    //     wears the trap's OWN Health down by 5; at 0 the trap breaks and is removed (so a spike wall degrades with use).
    //   * explosive (Landmine id1101): ONE-SHOT -- it self-destructs and detonates an AoE blast of radius Range2, hitting
    //     zombies / players / vehicles with the respective damages and a linear falloff (source DamageTool.explode).
    //
    // Adapted to our sim: the trigger is an edge-detected proximity scan of the zombies/players/vehicles groups within
    // TriggerRadius -- behaviourally identical to InteractableTrapTrigger.OnTriggerEnter (fires on the outside->inside
    // transition, re-arms when the entity leaves, per-trap Cooldown between fires). Animals are skipped for now (the port
    // has no animal-damage API yet); none of these three traps require power, so requiresPower isn't wired here.
    public partial class Trap : Node3D
    {
        public ETrapKind Kind = ETrapKind.Spike;
        public float PlayerDamage, ZombieDamage = 60f, AnimalDamage;
        public float VehicleDamage, BarricadeDamage, StructureDamage, ResourceDamage, ObjectDamage;
        public float Range2 = 8f;              // explosion blast radius (explosive only)
        public float Health = 35f;             // the trap's OWN hp -- worn down 5 per trigger; <=0 -> the trap breaks
        public bool IsExplosive;
        public bool IsBroken;                  // "Broken" (the Snare) -> break the victim's legs on a hit
        public float SetupDelay = 0.25f;       // Trap_Setup_Delay: arm time after placement
        public float Cooldown = 0f;            // Trap_Cooldown between triggers (0 = fires on every fresh enter)
        public float TriggerRadius = 0.85f;    // footprint the trigger volume covers (adapted from the barricade collider)

        double _age, _lastTriggered = -999.0;
        bool _spent;                           // a landmine (or a worn-out trap) is done -- stop scanning
        bool _armed;                           // seeded _inside on the first armed frame (pre-existing overlaps don't trigger)
        readonly System.Collections.Generic.HashSet<Node> _inside = new();

        public static Trap Spawn(Node parent, Vector3 pos, float yawDeg, Trap t)
        {
            t.Position = pos; t.RotationDegrees = new Vector3(0f, yawDeg, 0f);
            parent.AddChild(t);
            return t;
        }
        // per-archetype factories from the retail .dat values (Bundles/Items/Barricades/*)
        public static Trap SpawnSpike(Node p, Vector3 pos, float yaw) => Spawn(p, pos, yaw, new Trap {
            Kind = ETrapKind.Spike, ZombieDamage = 60f, PlayerDamage = 30f, AnimalDamage = 60f, Health = 35f });
        public static Trap SpawnBarbedwire(Node p, Vector3 pos, float yaw) => Spawn(p, pos, yaw, new Trap {
            Kind = ETrapKind.Barbedwire, ZombieDamage = 80f, PlayerDamage = 40f, AnimalDamage = 80f, Health = 70f });
        public static Trap SpawnCaltrop(Node p, Vector3 pos, float yaw) => Spawn(p, pos, yaw, new Trap {
            Kind = ETrapKind.Caltrop, ZombieDamage = 40f, PlayerDamage = 20f, AnimalDamage = 0f, Health = 15f });   // Caltrop.dat: also Damage_Tires (no wheel-HP model here)
        public static Trap SpawnLandmine(Node p, Vector3 pos, float yaw) => Spawn(p, pos, yaw, new Trap {
            Kind = ETrapKind.Landmine, IsExplosive = true, Range2 = 8f, ZombieDamage = 175f, PlayerDamage = 91f,
            AnimalDamage = 175f, VehicleDamage = 175f, BarricadeDamage = 75f, StructureDamage = 75f,
            ResourceDamage = 625f, ObjectDamage = 100f, Health = 1f, TriggerRadius = 0.5f });

        public override void _Ready() => BuildVisual();

        public override void _Process(double delta)
        {
            if (_spent) return;
            _age += delta;
            if (_age < SetupDelay) return;   // still arming -- inert
            if (!_armed)   // first armed frame: seed _inside with anything ALREADY standing in the footprint so it isn't hit
            {              // until it leaves + re-enters -- source OnTriggerEnter fires on ENTER only, not on pre-existing overlaps
                _armed = true;
                ScanGroup("zombies", _inside, trigger: false); ScanGroup("players", _inside, trigger: false); ScanGroup("vehicles", _inside, trigger: false);
                return;
            }

            // edge-triggered: fire for any solid entity NEWLY inside the trigger footprint (== OnTriggerEnter). We rebuild
            // the "currently inside" set each frame and trigger only on the entrants (not already in last frame's set).
            var seen = new System.Collections.Generic.HashSet<Node>();
            ScanGroup("zombies", seen, trigger: true);
            ScanGroup("players", seen, trigger: true);
            ScanGroup("vehicles", seen, trigger: true);
            if (_spent) return;
            _inside.Clear(); _inside.UnionWith(seen);
        }

        void ScanGroup(string group, System.Collections.Generic.HashSet<Node> seen, bool trigger)
        {
            if (_spent) return;
            foreach (var n in GetTree().GetNodesInGroup(group))
            {
                if (n is not Node3D e || !GodotObject.IsInstanceValid(e)) continue;
                if (e.GlobalPosition.DistanceTo(GlobalPosition) > TriggerRadius) continue;
                seen.Add(e);
                if (trigger && !_inside.Contains(e)) { Trigger(e); if (_spent) return; }   // trigger:false = seed-only (arming pass)
            }
        }

        void Trigger(Node3D entity)
        {
            if (_age - _lastTriggered < Cooldown) return;   // per-trap cooldown (source lastTriggered)
            _lastTriggered = _age;

            if (IsExplosive) { Explode(); return; }

            Vector3 dir = -GlobalTransform.Basis.Z;   // source uses transform.forward for the hit direction
            if (entity is ZombieController z && !z.Dead)
            {
                z.DamageHit(ZombieDamage, z.GlobalPosition, dir);
                Wear(5f);   // source: a hyper zombie wears the trap 10; the port has no HYPER speciality, so it's always 5
            }
            else if (entity is PlayerController pc)
            {
                pc.TakeDamage(PlayerDamage, GlobalPosition);
                if (IsBroken) pc.Broken = true;   // the Snare bear-trap: SHRED + break legs (source player.life.breakLegs)
                Wear(5f);
            }
            // vehicles rolling over a non-explosive trap: source only chips TIRES (damageTires); the port has no
            // per-wheel HP model, so a spike/wire does nothing to a car here (the landmine's AoE does, below).
        }

        void Wear(float amount)
        {
            Health -= amount;
            if (Health <= 0f) { _spent = true; GD.Print($"[trap] {Kind} wore out -> removed"); QueueFree(); }
        }

        // source DamageTool.explode: an AoE over Range2 hitting zombies / players / vehicles with a linear falloff, then
        // the landmine destroys itself (one-shot). Mirrors PlayerController.Explode's loops but needs no player instance.
        void Explode()
        {
            _spent = true;
            Vector3 p = GlobalPosition;
            foreach (var n in GetTree().GetNodesInGroup("zombies"))
                if (n is ZombieController z && !z.Dead)
                {
                    float d = z.GlobalPosition.DistanceTo(p);
                    if (d <= Range2 && !ExplosionBlocked(p, z.GlobalPosition)) z.DamageHit(SDG.Unturned.ExplosionMath.Linear(ZombieDamage, d, Range2), z.GlobalPosition, (z.GlobalPosition - p).Normalized());
                }
            foreach (var n in GetTree().GetNodesInGroup("vehicles"))
                if (n is Vehicle v && !v.Exploded)
                {
                    float d = v.GlobalPosition.DistanceTo(p);
                    if (d <= Range2 && !ExplosionBlocked(p, v.GlobalPosition)) v.TakeDamage(SDG.Unturned.ExplosionMath.Linear(VehicleDamage, d, Range2));
                }
            foreach (var n in GetTree().GetNodesInGroup("players"))
                if (n is PlayerController pc)
                {
                    float d = pc.GlobalPosition.DistanceTo(p);
                    if (d <= Range2 && !ExplosionBlocked(p, pc.GlobalPosition)) pc.TakeDamage(SDG.Unturned.ExplosionMath.Squared(PlayerDamage, d, Range2) * (pc.Inventory?.ExplosionArmor ?? 1f), p);   // players take SQUARED falloff + worn explosion armor (matches PlayerController.Explode + source getPlayerExplosionArmor); zombies/vehicles use linear per the port convention
                }
            GD.Print($"[trap] LANDMINE detonated at {p} (r={Range2})");
            QueueFree();
        }

        // A wall/terrain between the blast and a target stops the damage (source Explode -> LineOfSightTest; mirrors
        // PlayerController.ExplosionBlocked). Ray on the world layer only, at +0.8 m on both ends so it clears the ground.
        bool ExplosionBlocked(Vector3 point, Vector3 target)
        {
            Vector3 a = point + Vector3.Up * 0.8f, b = target + Vector3.Up * 0.8f;
            var q = PhysicsRayQueryParameters3D.Create(a, b, ZombieNav.WorldLayer);
            q.CollideWithAreas = false;
            return GetWorld3D().DirectSpaceState.IntersectRay(q).Count > 0;
        }

        void BuildVisual()
        {
            if (Kind == ETrapKind.Landmine)
            {
                var body = new StandardMaterial3D { AlbedoColor = new Color(0.18f, 0.19f, 0.16f), Metallic = 0.3f, Roughness = 0.7f };
                AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.16f, BottomRadius = 0.2f, Height = 0.08f }, Position = new Vector3(0f, 0.04f, 0f), MaterialOverride = body });
                var btn = new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.1f, 0.1f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
                AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.05f, BottomRadius = 0.05f, Height = 0.05f }, Position = new Vector3(0f, 0.09f, 0f), MaterialOverride = btn });
                return;
            }
            // spikes / barbedwire / caltrop: a cluster of upward spikes (metal-grey for wire, wood-brown otherwise)
            bool wire = Kind == ETrapKind.Barbedwire;
            var mat = new StandardMaterial3D { AlbedoColor = wire ? new Color(0.36f, 0.37f, 0.39f) : new Color(0.45f, 0.33f, 0.2f), Metallic = wire ? 0.6f : 0f, Roughness = 0.85f };
            float h = Kind == ETrapKind.Caltrop ? 0.18f : 0.5f;
            var rnd = new RandomNumberGenerator { Seed = 1234 };
            for (int i = 0; i < 7; i++)
            {
                float x = rnd.RandfRange(-0.35f, 0.35f), z = rnd.RandfRange(-0.18f, 0.18f);
                AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.05f, Height = h }, Position = new Vector3(x, h * 0.5f, z), MaterialOverride = mat });
            }
        }
    }
}
