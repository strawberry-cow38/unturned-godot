using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // A gas-station pump (the Gas_Pump_0 map object) given a POWER INPUT (master): one 750w consumer port you can wire
    // a generator to. `IsPowered` is the on/off flag it flips while it's receiving its 750w -- NO behaviour hangs off it
    // yet. It's a world FIXTURE, not a player deployable: no HP/fuel/pickup, and the pump MESH is already drawn by the
    // world builder; this node just hosts the connection port + joins the power net (the "deployables" group PowerNet
    // reads, which now keys on IPowerDevice rather than the concrete Deployable).
    public partial class GasPump : Node3D, IPowerDevice
    {
        public const float Watts = 750f;
        // where the orange input cube sits in the pump's LOCAL (flat-authored) frame: raw Z is the pump's height, so
        // Z=1.2 is ~waist up when the map stands it upright; Y=-0.4 puts it just off one of the thin faces.
        public static readonly Vector3 PortLocal = new Vector3(0f, -0.4f, 1.2f);
        readonly List<ConnectionPort> _ports = new();
        ConnectionPort _input;

        // IPowerDevice
        public bool PowerProducing => false;   // a pure consumer, never a source
        public bool PowerOnFire => false;      // a map fixture doesn't burn
        public uint PowerNetId => 0;           // not replicated -> SP/local wiring only
        public IReadOnlyList<ConnectionPort> PowerPorts => _ports;

        public bool IsPowered => _input != null && GodotObject.IsInstanceValid(_input) && _input.Powered;   // on/off flag: getting its 750w

        // Attach a power input to a placed gas pump. `pos`/`basis` = the pump's world transform (matches the drawn mesh);
        // `portLocal` = where the orange input cube sits on the pump, in the pump's local frame.
        public static GasPump Attach(Node parent, Vector3 pos, Basis basis, Vector3 portLocal)
        {
            var gp = new GasPump { Transform = new Transform3D(basis, pos) };
            parent.AddChild(gp);
            var port = ConnectionPort.Create(gp, new DeployableDef.Port { Kind = DeployableDef.PortKind.Consumer, Pos = portLocal, Watts = Watts }, "Gas Pump");
            gp.AddChild(port);
            gp._ports.Add(port);
            gp._input = port;
            gp.AddToGroup("deployables");   // PowerNet reads this group (keyed on IPowerDevice now)
            if (gp.GetTree() is SceneTree t && t.GetNodesInGroup("powermgr").Count == 0)   // one PowerManager ticks the whole net (else a placed generator makes it)
            { var pm = new PowerManager(); pm.AddToGroup("powermgr"); parent.AddChild(pm); }
            PowerNet.MarkDirty();
            return gp;
        }
    }
}
