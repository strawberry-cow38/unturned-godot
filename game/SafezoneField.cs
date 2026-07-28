using Godot;
using System.Collections.Generic;
using SDG.Unturned;
using UVector3 = UnityEngine.Vector3;

namespace UnturnedGodot
{
    /// <summary>
    /// The Godot half of safezones: owns a SafezoneSim, keeps each zone's Active flag in step with the
    /// power grid, and draws the bubble.
    ///
    /// One node for the whole level rather than an Area3D per zone, for the same reason the zombie
    /// rewrite has one director: a zone must behave identically on a dedicated server that never
    /// renders and never runs physics. Membership is a distance test the sim already owns, so nothing
    /// here needs a collider — which also means no "the trigger did not fire because the body was
    /// teleported" class of bug.
    /// </summary>
    public partial class SafezoneField : Node3D
    {
        /// <summary>The live field, for the damage/building/zombie rules to consult. Null on worlds
        /// with no zones, and every call site null-checks rather than assuming one exists.</summary>
        public static SafezoneField Instance;

        public SafezoneSim Sim { get; } = new SafezoneSim();

        /// <summary>Per-zone bookkeeping the sim deliberately does not carry: the sim answers "is this
        /// point safe", it does not know what a generator or a mesh is.</summary>
        sealed class Zone
        {
            public int Index;                 // into the sim
            public Deployable Generator;      // the powered thing that keeps it alive; null = always-on map zone
            public MeshInstance3D Bubble;
            public bool LastActive;
        }

        readonly List<Zone> _zones = new();

        [Export] public float DefaultRadius = 32f;

        public override void _Ready() => Instance = this;
        public override void _ExitTree() { if (Instance == this) Instance = null; }

        /// <summary>Add a zone. `generator` may be null for a map-authored zone that is always live
        /// (a spawn town); otherwise the zone follows that deployable's power state.</summary>
        public int AddZone(Vector3 center, float radius, Deployable generator = null, bool drawBubble = true)
        {
            var uc = new UVector3(center.X, center.Y, center.Z);
            int idx = Sim.Add(uc, radius, active: generator == null);
            var z = new Zone { Index = idx, Generator = generator, LastActive = generator == null };
            if (drawBubble) { z.Bubble = BuildBubble(center, radius); AddChild(z.Bubble); }
            _zones.Add(z);
            ApplyVisual(z, z.LastActive);
            return idx;
        }

        public override void _PhysicsProcess(double delta)
        {
            // Follow the grid. A safezone is a consequence of power, so a cut generator has to drop the
            // protection within a tick rather than at the next time something happens to rebuild it.
            for (int i = 0; i < _zones.Count; i++)
            {
                var z = _zones[i];
                if (z.Generator == null) continue;                     // map zone: always live
                bool live = GodotObject.IsInstanceValid(z.Generator) && IsPowered(z.Generator);
                if (live == z.LastActive) continue;
                z.LastActive = live;
                Sim.SetActive(z.Index, live);
                ApplyVisual(z, live);
            }
        }

        static bool IsPowered(Deployable d) => d.IsPowered && !d.IsWreck;

        // --- presentation ---------------------------------------------------------------------------

        MeshInstance3D BuildBubble(Vector3 center, float radius)
        {
            var mesh = new SphereMesh { Radius = radius, Height = radius * 2f, RadialSegments = 24, Rings = 12 };
            return new MeshInstance3D
            {
                Mesh = mesh,
                Position = center,
                // Front-face culled + unshaded + additive: seen from INSIDE the bubble, which is where a
                // protected player stands. Culling the near hemisphere stops the dome washing out the
                // whole screen when you are stood in the middle of it.
                MaterialOverride = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                    CullMode = BaseMaterial3D.CullModeEnum.Front,
                    AlbedoColor = new Color(0.25f, 0.75f, 1f, 0.10f),
                },
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
        }

        static void ApplyVisual(Zone z, bool active)
        {
            if (z.Bubble == null) return;
            z.Bubble.Visible = true;   // a dead bubble still renders -- players need to see WHERE the zone is
            if (z.Bubble.MaterialOverride is StandardMaterial3D m)
                m.AlbedoColor = active
                    ? new Color(0.25f, 0.75f, 1f, 0.10f)    // live: cool blue
                    : new Color(0.55f, 0.55f, 0.55f, 0.05f); // unpowered: grey and fainter
        }
    }
}
