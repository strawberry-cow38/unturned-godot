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
        public const float Watts = 10000f;   // 10kW mains feed (strawberry)
        // where the green OUTPUT cube sits in the box's LOCAL (as-loaded) frame. Circuit_0 AABB (CONV=1 raw obj):
        // X -0.73..0.27 (width 1.0), Y -0.10..0.48 (depth 0.58), Z 0.00..1.87 (height). The broad front panel faces
        // +Y, so the port sits centered in X (-0.226), mid-height (Z 0.933), pushed out just past the +Y face.
        public static readonly Vector3 PortLocal = new Vector3(-0.226f, 0.60f, 0.933f);

        readonly List<ConnectionPort> _ports = new();
        ConnectionPort _output;

        // IPowerDevice: a mains SOURCE -- it produces while the global grid flag is on, and a map fixture never burns.
        public bool PowerProducing => PowerNet.GlobalPower;
        public bool PowerOnFire => false;
        public uint PowerNetId => 0;   // SP/local wiring only -- not replicated (MP is a later task)
        public IReadOnlyList<ConnectionPort> PowerPorts => _ports;
        public bool IsPowered => PowerNet.GlobalPower;   // convenience: the source is live only while the grid is on

        // Attach a grid source at a placed Circuit_0's transform (pos + basis from WorldBuilder.PlaceObject), owning
        // one wire-able Output port. Mirrors GasPump.Attach: adds to the "deployables" group PowerNet reads, and lazily
        // spawns the single PowerManager if this is the first powered thing in the world.
        public static GridPowerSource Attach(Node parent, Vector3 pos, Basis basis, Vector3 portLocal)
        {
            var g = new GridPowerSource { Transform = new Transform3D(basis, pos) };
            parent.AddChild(g);
            var port = ConnectionPort.Create(g, new DeployableDef.Port { Kind = DeployableDef.PortKind.Output, Pos = portLocal, Watts = Watts }, "Grid Power");
            g.AddChild(port);
            g._ports.Add(port);
            g._output = port;
            g.AddToGroup("deployables");   // PowerNet reads this group (keyed on IPowerDevice, not Deployable)
            if (g.GetTree() is SceneTree t && t.GetNodesInGroup("powermgr").Count == 0)   // one PowerManager ticks the whole net (else a placed generator/pump makes it)
            { var pm = new PowerManager(); pm.AddToGroup("powermgr"); parent.AddChild(pm); }
            PowerNet.MarkDirty();
            return g;
        }
    }
}
