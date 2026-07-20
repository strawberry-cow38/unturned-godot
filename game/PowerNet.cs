using Godot;
using SDG.Unturned;

namespace UnturnedGodot
{
    // Power propagation over the wire graph. The ALGORITHM lives engine-free in core/UnturnedSim/PowerSolver.cs
    // (tested under tests/UnturnedSim.Tests); this is the thin adapter: walk the "deployables"/"wires" groups into
    // plain PowerDevice/PowerPort/PowerWire records, Solve, and write Live/Powered/Draw back to the port nodes.
    public static class PowerNet
    {
        // The graph only changes on discrete events (wire built/cleared, deployable placed/removed, generator toggled,
        // something catches fire) -- nothing per-frame -- so recompute is event-driven, not every frame. MarkDirty()
        // flags state changes; the wire/deployable COUNT is a backstop that catches any structural add/remove for free.
        static bool _dirty = true;
        static int _lastWires = -1, _lastDeployables = -1;
        public static void MarkDirty() => _dirty = true;

        // Global grid-power flag (SP): while ON, every GridPowerSource (a Circuit_0 breaker box) feeds its 10kW into
        // the wire graph; while OFF the mains are dead. Default OFF -- the grid starts unpowered and the F1 console
        // `toggleGlobalPower` energizes it. A flag flip is NOT a structural change (the wire/deployable counts don't
        // move), so the toggle path MUST MarkDirty() or RecomputeIfDirty's count-backstop would skip the recompute.
        static bool _globalPower = false;
        public static bool GlobalPower => _globalPower;
        public static bool ToggleGlobalPower() { _globalPower = !_globalPower; MarkDirty(); return _globalPower; }
        public static void SetGlobalPower(bool on) { if (_globalPower != on) { _globalPower = on; MarkDirty(); } }

        public static void ResetForTests() { _dirty = true; _lastWires = -1; _lastDeployables = -1; _globalPower = false; }   // L1 test isolation between sandboxes

        public static void RecomputeIfDirty(SceneTree tree)
        {
            int w = tree.GetNodeCountInGroup("wires"), d = tree.GetNodeCountInGroup("deployables");
            if (!_dirty && w == _lastWires && d == _lastDeployables) return;   // idle: nothing changed -> skip the whole O(W*(W+D)) pass
            _dirty = false; _lastWires = w; _lastDeployables = d;
            Recompute(tree);
        }

        public static void Recompute(SceneTree tree)
        {
            var devices = new System.Collections.Generic.List<PowerDevice>();
            var portMap = new System.Collections.Generic.Dictionary<ConnectionPort, PowerPort>();
            foreach (var n in tree.GetNodesInGroup("deployables"))
                if (n is IPowerDevice d)   // a Deployable OR a powered world fixture (gas pump)
                {
                    var dev = new PowerDevice { Producing = d.PowerProducing, OnFire = d.PowerOnFire, Conducting = d.PowerConducting };
                    foreach (var p in d.PowerPorts)
                    {
                        if (p == null || !GodotObject.IsInstanceValid(p)) continue;
                        p.Occupied = false;   // reset; the wire loop below re-marks the wired ports
                        portMap[p] = dev.AddPort(Kind(p.Kind), p.Watts);
                    }
                    devices.Add(dev);
                }

            var wires = new System.Collections.Generic.List<PowerWire>();
            foreach (var n in tree.GetNodesInGroup("wires"))
                if (n is Wire w && GodotObject.IsInstanceValid(w.Source) && GodotObject.IsInstanceValid(w.Consumer))
                {
                    w.Source.Occupied = true; w.Consumer.Occupied = true;   // a wired port shades darker
                    if (portMap.TryGetValue(w.Source, out var src) && portMap.TryGetValue(w.Consumer, out var cons))
                        wires.Add(new PowerWire(src, cons));
                }

            PowerSolver.Solve(devices, wires);

            foreach (var kv in portMap)   // write the solved state back onto the port nodes
            {
                kv.Key.Live = kv.Value.Live;
                kv.Key.Powered = kv.Value.Powered;
                kv.Key.Draw = kv.Value.Draw;
                kv.Key.UpdateCubeColor();   // reflect the new occupancy shade
            }
        }

        static PowerPortKind Kind(DeployableDef.PortKind k) => k switch
        {
            DeployableDef.PortKind.Output => PowerPortKind.Output,
            DeployableDef.PortKind.Consumer => PowerPortKind.Consumer,
            _ => PowerPortKind.Passthrough,
        };
    }

    // Ticks the power net once a frame. One instance is created lazily by the first placed deployable.
    public partial class PowerManager : Node
    {
        public override void _Process(double delta) => PowerNet.RecomputeIfDirty(GetTree());
    }
}
