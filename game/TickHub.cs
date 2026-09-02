using Godot;

namespace UnturnedGodot
{
    // ONE engine->C# callback per frame for the swarm classes.
    //
    // Every C# node with a `_Process`/`_PhysicsProcess` override costs a native->managed transition plus a StringName
    // walk of its ENTIRE class chain (X -> ... -> Node3D -> Node -> GodotObject, comparing method names one by one) on
    // every call, whether or not the body does anything. PerfView/ETW on PEI idle (2026-09-02): ~46% of the main
    // thread was that dispatch, game logic itself ~3%. Swarm classes (containers, vehicles) register with their static
    // `_live` lists and get ticked from this single node instead. Created lazily by the first registrant; lives under
    // the scene root; pauses with the tree (default ProcessMode), and TickAll skips nodes whose own CanProcess() is
    // false, so pause/FreezeMode behave as before.
    public partial class TickHub : Node
    {
        static TickHub _inst;
        public static void Ensure(Node any)
        {
            if (_inst != null && GodotObject.IsInstanceValid(_inst)) return;
            var tree = any.GetTree();
            if (tree == null) return;
            _inst = new TickHub { Name = "TickHub" };
            tree.Root.CallDeferred(Node.MethodName.AddChild, _inst);
        }
        // Generic registrations: any node hands the hub a tick delegate + a rate. Ticked from _Process at most `hz`
        // times a second with the accumulated delta, skipped while the node's own CanProcess() is false (pause /
        // ProcessMode), dropped automatically once the node is freed.
        struct Entry { public Node Node; public System.Action<double> Tick; public double Period, Acc; }
        static readonly System.Collections.Generic.List<Entry> _ticks = new();
        public static void Add(Node node, System.Action<double> tick, float hz)
        {
            Ensure(node);
            _ticks.Add(new Entry { Node = node, Tick = tick, Period = hz > 0f ? 1.0 / hz : 0.0 });
        }
        public static void Remove(Node node)
        {
            for (int i = _ticks.Count - 1; i >= 0; i--) if (_ticks[i].Node == node) _ticks.RemoveAt(i);
        }
        public override void _Process(double delta)
        {
            StorageCrate.TickAll(delta);
            for (int i = _ticks.Count - 1; i >= 0; i--)
            {
                var e = _ticks[i];
                if (!GodotObject.IsInstanceValid(e.Node)) { _ticks.RemoveAt(i); continue; }
                e.Acc += delta;
                if (e.Acc < e.Period) { _ticks[i] = e; continue; }
                double dt = e.Acc; e.Acc = 0.0; _ticks[i] = e;
                if (!e.Node.CanProcess()) continue;
                e.Tick(dt);
            }
        }
        public override void _PhysicsProcess(double delta) => Vehicle.PhysicsTickAll(delta);
    }
}
