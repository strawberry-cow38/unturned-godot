using Godot;

namespace UnturnedGodot
{
    // OIL PUMP / DERRICK -- source InteractableOil + ItemOilPumpAsset. A placed pump that holds a fuel reservoir
    // (Oil.dat id1219: Fuel_Capacity 2500, Health 450) and slowly EXTRACTS more over time -- a RENEWABLE fuel source
    // for the fluid economy (vs the static gas-station tanks). You siphon fuel out of it (source askBurn) into a
    // vehicle / gas can.
    //
    // SERVER-AUTHORITATIVE: the server owns Fuel; nothing here mutates shared state on a client. MP replication (a
    // FixtureKind + a view-only Materialize mirroring entity.Fuel + running-from-entity.ToggledOn) is wired in the
    // batch once tinyclaw lands the schema DefIds -- until then this is the sim half (recipe step 2), verified by the
    // direct-construct --oiltest harness (the allowed test seam, CLAUDE.md line 124). NOT "done" until a joined client
    // sees it (the SP+MP DONE gate).
    //
    // Port abstraction: retail ties the extraction rate to an oil DEPOSIT node under the pump (a worldgen resource the
    // port doesn't have yet), so the pump self-regens at RegenPerSec to represent the deposit.
    public partial class OilPump : Node3D
    {
        public float FuelCapacity = 2500f;   // Oil.dat Fuel_Capacity
        public float Health = 450f;          // Oil.dat Health
        public float Fuel;                   // current reservoir (server-owned)
        public float RegenPerSec = 5f;       // extraction rate (deposit abstraction) -- a slow renewable, ~8 min empty->full
        public uint NetId;                    // MP: the server entity this view mirrors (0 = the authoritative host node)
        public bool IsReplica;                // MP: a client-side VIEW-ONLY replica -- renders + mirrors Fuel; the SERVER runs regen/siphon

        MeshInstance3D _beam;                 // the rocking walking-beam (pump-jack "horse head")
        float _phase;

        public bool IsRunning => Fuel < FuelCapacity;                       // pumping while there's headroom (-> entity.ToggledOn in MP)
        public float FuelNorm => FuelCapacity > 0f ? Fuel / FuelCapacity : 0f;

        public static OilPump Spawn(Node parent, Vector3 pos, float yawDeg = 0f, float fuel = 0f)
        {
            var p = new OilPump { Position = pos, RotationDegrees = new Vector3(0f, yawDeg, 0f), Fuel = fuel };
            parent.AddChild(p);
            return p;
        }

        public override void _Ready() { AddToGroup("oilpumps"); BuildVisual(); }

        // MP: the client's DeployableReplicaView calls this for a FixtureKind.OilPump entity -> a VIEW-ONLY pump-jack that
        // renders + rocks its beam from the mirrored Fuel. The dispatch sets Fuel = entity.Fuel each tick; NO regen/siphon
        // runs here (the server owns that). Mirrors GasPump.Materialize.
        public static OilPump Materialize(Node parent, Vector3 pos, float yawDegrees, uint netId)
        {
            var p = new OilPump { Position = pos, RotationDegrees = new Vector3(0f, yawDegrees, 0f), NetId = netId, IsReplica = true };
            parent.AddChild(p);
            return p;
        }

        // server-auth extraction: pump fuel up toward capacity (the renewable source) + rock the beam while running. On a
        // client REPLICA the regen is skipped -- the server owns Fuel and the dispatch mirrors it -- but the beam still
        // rocks off the mirrored Fuel (a pure view-only derivation, no shared-state mutation).
        public override void _PhysicsProcess(double delta)
        {
            float dt = (float)delta;
            if (!IsReplica && Fuel < FuelCapacity) Fuel = Mathf.Min(FuelCapacity, Fuel + RegenPerSec * dt);
            if (_beam != null) { _phase += dt * (IsRunning ? 2.2f : 0f); _beam.RotationDegrees = new Vector3(Mathf.Sin(_phase) * 14f, 0f, 0f); }
        }

        // draw up to `requested` fuel out of the reservoir (source askBurn); returns the amount actually siphoned.
        public float Siphon(float requested)
        {
            float drawn = Mathf.Clamp(requested, 0f, Fuel);
            Fuel -= drawn;
            return drawn;
        }

        void BuildVisual()
        {
            var metal = new StandardMaterial3D { AlbedoColor = new Color(0.2f, 0.22f, 0.26f), Metallic = 0.5f, Roughness = 0.6f };
            var rust = new StandardMaterial3D { AlbedoColor = new Color(0.5f, 0.32f, 0.18f), Roughness = 0.9f };
            AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(1.1f, 0.25f, 1.1f) }, Position = new Vector3(0f, 0.12f, 0f), MaterialOverride = metal });   // concrete/steel base pad
            AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.14f, 1.4f, 0.14f) }, Position = new Vector3(-0.2f, 0.9f, 0f), RotationDegrees = new Vector3(0f, 0f, 10f), MaterialOverride = rust });    // A-frame leg
            AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.14f, 1.4f, 0.14f) }, Position = new Vector3(0.2f, 0.9f, 0f), RotationDegrees = new Vector3(0f, 0f, -10f), MaterialOverride = rust });    // A-frame leg
            _beam = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.16f, 0.16f, 1.7f) }, Position = new Vector3(0f, 1.55f, 0f), MaterialOverride = rust };   // the nodding walking-beam
            AddChild(_beam);
        }
    }
}
