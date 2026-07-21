using Godot;

namespace UnturnedGodot
{
    // C4 / REMOTE CHARGE -- source InteractableCharge + ItemChargeAsset. A planted explosive with NO proximity trigger:
    // a DETONATOR fires EVERY one of the owner's charges at once (source Charge.Detonate -> DamageTool.explode, then the
    // charge self-destructs). Built for RAIDING -- the standard Charge (id1241) does 200 to players/zombies but 1000 to
    // barricades/structures and 500 to vehicles, so a stack of them blows through a base wall. AoE over Range2 with the
    // port's falloff convention (linear for zombies/vehicles/deployables, squared + explosion armor for players, matching
    // PlayerController.Explode). Deployables (generators/traps/etc.) take the Barricade_Damage -- that's the raid.
    public partial class Charge : Node3D
    {
        public string Label = "C4 Charge";
        public float Range2 = 8f;                                    // ItemChargeAsset.range2 (Charge id1241)
        public float PlayerDamage = 200f, ZombieDamage = 200f, AnimalDamage = 200f;
        public float BarricadeDamage = 1000f, StructureDamage = 1000f, VehicleDamage = 500f, ResourceDamage = 2000f, ObjectDamage = 1000f;
        public uint NetId;              // MP: the server entity this view mirrors (0 = the SP/host authoritative node)
        public bool IsReplica;          // MP: a client-side VIEW-ONLY replica -- renders the C4 only; the SERVER (ServerCharge) owns Detonate (the detonator is a server-routed command)

        // per-variant config (item id: Charge 1241 / Charge_Precision 1393) -- the archetype node (unspawned), the SINGLE
        // source used by the spawners AND the server-side ServerCharge. 1241 = the field defaults (Range2 8, 200 flesh);
        // 1393 = the precision override (smaller Range2 3 but heavier -- 250 flesh, 2000 to barricades/structures).
        public static Charge ForDefId(ushort defId)
        {
            var c = new Charge();
            if (defId == 1393)
            {
                c.Label = "Precision Charge"; c.Range2 = 3f; c.PlayerDamage = c.ZombieDamage = c.AnimalDamage = 250f;
                c.BarricadeDamage = c.StructureDamage = 2000f; c.VehicleDamage = 1000f; c.ResourceDamage = 4000f; c.ObjectDamage = 2000f;
            }
            return c;
        }
        public static Charge Spawn(Node parent, Vector3 pos, float yawDeg = 0f) => SpawnDef(parent, pos, yawDeg, 1241);
        public static Charge SpawnPrecision(Node parent, Vector3 pos, float yawDeg = 0f) => SpawnDef(parent, pos, yawDeg, 1393);
        static Charge SpawnDef(Node parent, Vector3 pos, float yawDeg, ushort defId)
        {
            var c = ForDefId(defId);
            c.Position = pos; c.RotationDegrees = new Vector3(0f, yawDeg, 0f);
            parent.AddChild(c);
            return c;
        }

        public override void _Ready() { AddToGroup("charges"); BuildVisual(); }

        // MP: the client's DeployableReplicaView calls this for a FixtureKind.Charge entity -> a VIEW-ONLY C4 brick
        // (both the standard + precision variants render the same, and a replica never detonates, so no variant needed).
        // The SERVER (ServerCharge) owns Detonate over a server-routed detonator command. Mirrors OilPump.Materialize.
        public static Charge Materialize(Node parent, Vector3 pos, float yawDegrees, uint netId)
        {
            var c = new Charge { Position = pos, RotationDegrees = new Vector3(0f, yawDegrees, 0f), NetId = netId, IsReplica = true };
            parent.AddChild(c);
            return c;
        }

        // Fire EVERY planted charge at once -- source: a detonator triggers all of the owner's charges. SP = one owner.
        public static int DetonateAll(SceneTree tree)
        {
            int n = 0;
            foreach (var node in tree.GetNodesInGroup("charges"))
                if (node is Charge c && GodotObject.IsInstanceValid(c)) { c.Detonate(); n++; }
            return n;
        }

        // source Charge.Detonate: DamageTool.explode over Range2 with the per-type damages, then the charge self-destructs.
        public void Detonate()
        {
            if (IsReplica) return;   // a client REPLICA never applies blast damage or self-removes -- ServerCharge owns Detonate; the ReplicaView retires this node when the server retires the entity
            Vector3 p = GlobalPosition;
            foreach (var node in GetTree().GetNodesInGroup("zombies"))
                if (node is ZombieController z && !z.Dead)
                { float d = z.GlobalPosition.DistanceTo(p); if (d <= Range2) z.DamageHit(SDG.Unturned.ExplosionMath.Linear(ZombieDamage, d, Range2), z.GlobalPosition, (z.GlobalPosition - p).Normalized()); }
            foreach (var node in GetTree().GetNodesInGroup("vehicles"))
                if (node is Vehicle v && !v.Exploded)
                { float d = v.GlobalPosition.DistanceTo(p); if (d <= Range2) v.TakeDamage(SDG.Unturned.ExplosionMath.Linear(VehicleDamage, d, Range2)); }
            foreach (var node in GetTree().GetNodesInGroup("deployables"))   // THE RAID: blow up placed barricades / generators / etc. (Barricade_Damage)
                if (node is Deployable dep && GodotObject.IsInstanceValid(dep))
                { float d = dep.GlobalPosition.DistanceTo(p); if (d <= Range2) dep.TakeDamage(SDG.Unturned.ExplosionMath.Linear(BarricadeDamage, d, Range2)); }
            foreach (var node in GetTree().GetNodesInGroup("players"))
                if (node is PlayerController pc)
                { float d = pc.GlobalPosition.DistanceTo(p); if (d <= Range2) pc.TakeDamage(SDG.Unturned.ExplosionMath.Squared(PlayerDamage, d, Range2) * (pc.Inventory?.ExplosionArmor ?? 1f), p); }   // players: squared falloff + worn explosion armor
            GD.Print($"[charge] {Label} detonated at {p} (r={Range2})");
            QueueFree();
        }

        void BuildVisual()
        {
            // a tan brick of plastic explosive with a small glowing-red arming light
            var putty = new StandardMaterial3D { AlbedoColor = new Color(0.72f, 0.6f, 0.32f), Roughness = 0.9f };
            AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.28f, 0.12f, 0.18f) }, Position = new Vector3(0f, 0.06f, 0f), MaterialOverride = putty });
            var led = new StandardMaterial3D { AlbedoColor = new Color(0.9f, 0.1f, 0.1f), EmissionEnabled = true, Emission = new Color(1f, 0.15f, 0.1f), EmissionEnergyMultiplier = 2.5f, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
            AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.045f, 0.045f, 0.045f) }, Position = new Vector3(0.1f, 0.14f, 0f), MaterialOverride = led });
        }
    }
}
