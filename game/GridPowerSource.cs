using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // A grid-power mains SOURCE bolted onto the Circuit_0 breaker-box world object (master/strawberry): a wire-able
    // 10kW OUTPUT port that produces power while the global grid flag (PowerNet.GlobalPower) is ON. The mirror of a
    // running generator's output -- same green output cube, same wire tool, same PowerSolver treatment -- except the
    // "engine" is the global mains switch instead of fuel + the F-toggle. It's a world FIXTURE (no HP/pickup/fire);
    // the world builder still draws the breaker-box mesh + collider, this just hangs the port off it.
    //
    // Not a Deployable -- an IPowerDevice, exactly like GasPump. PowerNet already gathers the "deployables" group by
    // that interface (not the concrete Deployable), and ConnectionPort.Owner is an IPowerDevice whose Usable only
    // needs a live, non-on-fire GodotObject owner -- so a grid source wires up with ZERO change to either the power
    // graph or the port, and the Deployable path stays byte-identical.
    public partial class GridPowerSource : Node3D, IPowerDevice
    {
        public const float DefaultWatts = 10000f;   // 10kW mains feed (strawberry)
        public float Watts = DefaultWatts;           // per-instance now -- set in the map editor (custom value OR a preset)
        public string GridName = "";                 // editor label; the mouseover reads "Grid Power - <name>: <watts>"

        // Editor presets: approximate real BC/NA electrical SERVICE capacities (master). Residential = single-phase
        // 240V (100A / 200A); Commercial = 3-phase 208V (~200-300A / ~1000A+); Industrial = 3-phase 480V (~1MW / multi-service MW).
        public static readonly (string name, float watts)[] Presets =
        {
            ("Residential Small", 24000f),    // 100A @ 240V  ~= 24 kW
            ("Residential Large", 48000f),    // 200A @ 240V  ~= 48 kW
            ("Commercial Small",  100000f),   // ~200-300A 3-phase 208V (a commercial service "exceeds 100kW")
            ("Commercial Large",  500000f),   // ~1000A+ 3-phase
            ("Industrial Small",  1000000f),  // ~1 MW  (~1000A @ 480V 3-phase)
            ("Industrial Large",  5000000f),  // ~5 MW  (multiple 4000A @ 480V services)
        };
        public string Tooltip => $"Grid Power - {(string.IsNullOrEmpty(GridName) ? "Unnamed" : GridName)}: {Watts:0}W";
        // where the green OUTPUT cube sits in the box's LOCAL (as-loaded) frame. Circuit_0 AABB (CONV=1 raw obj):
        // X -0.73..0.27 (width 1.0), Y -0.10..0.48 (depth 0.58), Z 0.00..1.87 (height). Port hangs off the +X (right)
        // face, mid-height (Z 0.933) -- render-verified sitting ON the box face, not floating (Y=0.60 floated above it).
        public static readonly Vector3 PortLocal = new Vector3(0.32f, 0.18f, 0.933f);

        readonly List<ConnectionPort> _ports = new();
        ConnectionPort _output;

        // A3 (SP/MP-unify): the replicated entity this node mirrors (0 = a direct SP/local source from Attach).
        // A joined client / consuming loopback materializes the source via Materialize and stamps the server
        // NetId, so an interactive wire request addresses it over the wire (PlayerController.RequestWire).
        public uint NetId;

        // A3: on a client replica (Materialize) producing derives from the replicated entity.ToggledOn -- set
        // each tick by DeployableReplicaView -- NEVER the process-global PowerNet.GlobalPower (a local flip
        // would diverge the mains bit -> StateHash desync). Null on a direct SP source: it falls back to the
        // global flag exactly as before. A CHANGE marks the net dirty so the count-backstop recompute fires
        // (a mains flip is not a structural change; mirrors Deployable.NetSetPowered).
        bool? _netProducing;
        public bool? NetProducingOverride
        {
            get => _netProducing;
            set { if (_netProducing != value) { _netProducing = value; PowerNet.MarkDirty(); } }
        }
        bool Producing => _netProducing ?? PowerNet.GlobalPower;   // replica: the replicated mains bit; direct SP: the global flag

        // look-at outline (glow duplicate on the overlay layer) + the generator-style info billboard
        MeshInstance3D _glow;
        InfoBillboard _info;
        bool _focused;
        static readonly Color GridColor = new Color(0.40f, 0.85f, 1f);   // electric blue for the grid outline / name

        // IPowerDevice: a mains SOURCE -- it produces while the mains are on (replica: the replicated
        // entity.ToggledOn via NetProducingOverride; direct SP: PowerNet.GlobalPower), and a map fixture never burns.
        public bool PowerProducing => Producing;
        public bool PowerOnFire => false;
        public uint PowerNetId => NetId;   // 0 = direct SP/local wire; a replica's server NetId routes wire requests over the wire (A3)
        public IReadOnlyList<ConnectionPort> PowerPorts => _ports;
        public bool IsPowered => Producing;   // convenience: the source is live only while the mains are on

        // Attach a grid source at a placed Circuit_0's transform (pos + basis from WorldBuilder.PlaceObject), owning
        // one wire-able Output port. Mirrors GasPump.Attach: adds to the "deployables" group PowerNet reads, and lazily
        // spawns the single PowerManager if this is the first powered thing in the world.
        public static GridPowerSource Attach(Node parent, Vector3 pos, Basis basis, Vector3 portLocal, float watts = DefaultWatts, string gridName = "", Mesh boxMesh = null)
        {
            var g = new GridPowerSource { Transform = new Transform3D(basis, pos), Watts = watts, GridName = gridName };
            parent.AddChild(g);
            var port = ConnectionPort.Create(g, new DeployableDef.Port { Kind = DeployableDef.PortKind.Output, Pos = portLocal, Watts = watts }, "Grid Power");
            g.AddChild(port);
            g._ports.Add(port);
            g._output = port;
            g.AddToGroup("deployables");   // PowerNet reads this group (keyed on IPowerDevice, not Deployable)
            if (boxMesh != null)   // look-at outline: a duplicate of the box mesh on the overlay layer, hidden until looked at
                g.AddChild(g._glow = new MeshInstance3D
                {
                    Mesh = boxMesh, Visible = false, Layers = OutlineOverlay.OutlineLayer, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = Colors.White, CullMode = BaseMaterial3D.CullModeEnum.Disabled },
                });
            g.AddChild(g._info = new InfoBillboard { TopLevel = true });
            if (g.GetTree() is SceneTree t && t.GetNodesInGroup("powermgr").Count == 0)   // one PowerManager ticks the whole net (else a placed generator/pump makes it)
            { var pm = new PowerManager(); pm.AddToGroup("powermgr"); parent.AddChild(pm); }
            PowerNet.MarkDirty();
            return g;
        }

        // A3 (SP/MP-unify): materialize a grid source from its REPLICATED entity (DeployableReplicaView) --
        // a self-contained node stamped with the server NetId, its one Output port hung off PortLocal, in the
        // "deployables" group the local PowerNet reads. NetProducingOverride is seeded OFF (mains default off)
        // and re-derived from entity.ToggledOn each tick by the view. Positioned at the quantized entity pos with
        // the SAME flat->upright basis the world-drawn Circuit_0 mesh + GasPump.Materialize + SpawnEditorGridPower
        // use: yaw THEN a -90deg stand-up (raw Z-height -> world Y). WITHOUT the stand-up (the old plain-yaw basis)
        // the port cube stayed in the box's as-loaded FLAT frame while the mesh stood up -> the output node floated
        // off the box face = "no visual node on breaker box" (master 2026-07-20). PortLocal is authored in the
        // STOOD-UP frame -- the same one Attach's full placement basis produces (render-verified on the +X face).
        public static GridPowerSource Materialize(Node parent, Vector3 pos, float yawDegrees, float watts, uint netId)
        {
            var basis = new Basis(Vector3.Up, Mathf.DegToRad(yawDegrees)) * new Basis(Vector3.Right, Mathf.DegToRad(-90f));   // yaw + stand the flat-authored box upright (matches GasPump.Materialize + SpawnEditorGridPower)
            var g = new GridPowerSource { Transform = new Transform3D(basis, pos), Watts = watts, NetId = netId, NetProducingOverride = false };
            parent.AddChild(g);
            var port = ConnectionPort.Create(g, new DeployableDef.Port { Kind = DeployableDef.PortKind.Output, Pos = PortLocal, Watts = watts }, "Grid Power");
            g.AddChild(port);
            g._ports.Add(port);
            g._output = port;
            g.AddToGroup("deployables");   // PowerNet reads this group (keyed on IPowerDevice, not Deployable)
            g.AddChild(g._info = new InfoBillboard { TopLevel = true });
            g.AddInteractionCollider();
            if (g.GetTree() is SceneTree t && t.GetNodesInGroup("powermgr").Count == 0)   // one PowerManager ticks the whole net
            { var pm = new PowerManager(); pm.AddToGroup("powermgr"); parent.AddChild(pm); }
            PowerNet.MarkDirty();
            return g;
        }

        // A3 (SP/MP-unify): a self-contained gridpower-meta collider so the look-ray focuses the box (outline +
        // tooltip + wire-tool endpoint) WITHOUT relying on WorldBuilder tagging the world mesh's collider (it
        // never does -- only the dead SpawnEditorGridPower path did, so the placements.txt Circuit_0 boxes were
        // un-focusable/un-wireable = "grid power doesn't exist"). Mirrors GasPump.AddInteractionCollider: a
        // world-space box wrapping the standing breaker box, on the small-prop look layer (1<<6, in
        // PlayerController's look-ray mask), slightly oversized so it wins the coincident world collider along
        // the ray. Used by Materialize (replica/consume) and the pure-direct SpawnFixturesDirect.
        StaticBody3D _hitBody;
        public void AddInteractionCollider()
        {
            // Circuit_0 stands ~1.87 m tall from its placement origin (base); wrap it world-axis-aligned (TopLevel
            // so the plain-yaw / stand-up node basis doesn't tilt the box), centered mid-height, slightly oversized.
            _hitBody = new StaticBody3D { TopLevel = true, Transform = new Transform3D(Basis.Identity, GlobalPosition + Vector3.Up * 0.95f), CollisionLayer = 1u << 6, CollisionMask = 0 };
            _hitBody.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(1.3f, 2.1f, 1.3f) } });
            _hitBody.SetMeta("gridpower", this);   // look-ray hits this -> resolve the GridPowerSource (PlayerController:176)
            AddChild(_hitBody);
        }

        // Look-at outline + the generator-style info billboard (name + output/load bar). Mirrors GasPump.
        public void SetLookFocused(bool on)
        {
            if (_focused == on) return;
            _focused = on;
            if (_glow != null) _glow.Visible = on;
            if (on) WorldItem.FocusColor = GridColor;
            _info?.SetActive(on);
        }

        public override void _Process(double delta)
        {
            if (!_focused || _info == null) return;   // only the looked-at box keeps its tooltip live
            _info.GlobalPosition = GlobalPosition + Vector3.Up * 2.4f;   // float above the box (Circuit_0 ~1.87m tall)
            _info.SetName(string.IsNullOrEmpty(GridName) ? "Grid Power" : $"Grid Power - {GridName}", GridColor);
            float draw = (_output != null && GodotObject.IsInstanceValid(_output)) ? _output.Draw : 0f;
            _info.SetBar(0, Watts > 0f ? Mathf.Clamp(draw / Watts, 0f, 1f) : 0f, GridColor);   // load / capacity (generator's usage bar)
            _info.SetPrompt(Producing ? $"{draw:0} / {Watts:0}W" : "mains OFF", GridColor);
        }
    }
}
