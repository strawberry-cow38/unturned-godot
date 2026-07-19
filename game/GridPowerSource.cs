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

        // look-at outline (glow duplicate on the overlay layer) + the generator-style info billboard
        MeshInstance3D _glow;
        InfoBillboard _info;
        bool _focused;
        static readonly Color GridColor = new Color(0.40f, 0.85f, 1f);   // electric blue for the grid outline / name

        // IPowerDevice: a mains SOURCE -- it produces while the global grid flag is on, and a map fixture never burns.
        public bool PowerProducing => PowerNet.GlobalPower;
        public bool PowerOnFire => false;
        public uint PowerNetId => 0;   // SP/local wiring only -- not replicated (MP is a later task)
        public IReadOnlyList<ConnectionPort> PowerPorts => _ports;
        public bool IsPowered => PowerNet.GlobalPower;   // convenience: the source is live only while the grid is on

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
            _info.SetPrompt(PowerNet.GlobalPower ? $"{draw:0} / {Watts:0}W" : "mains OFF", GridColor);
        }
    }
}
