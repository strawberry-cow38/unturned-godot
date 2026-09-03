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
            // ORDER: the swarms used to take their own callbacks in tree order, which put every vehicle (spawned by the
            // world build) BEFORE the player. Ticked from a hub that sits after Main's subtree they ran after the player's
            // step, so anything reading a vehicle transform in the player's tick (the ride cam, seated puppets) saw last
            // step's value for a frame -- net.ride_freelook caught it (tinyclaw, 2026-09-03). A negative priority runs the
            // hub ahead of every default-priority node, restoring "vehicles first".
            _inst = new TickHub { Name = "TickHub", ProcessPhysicsPriority = -10, ProcessPriority = -10 };
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
        // Per-FRAME registrations (strawberry 2026-09-03 "dig the next biggest frame swallower"): the ETW profile of the pinned
        // PEI spot showed the C# bridge (CSharpInstanceBridge.Call) at ~16% of the main thread -- ~20 singleton nodes still took
        // their own _Process/_PhysicsProcess at ~30 us of StringName chain-walk EACH, per frame. They register here instead:
        // one engine callback, then plain delegate calls, in REGISTRATION order (player before its viewmodel/HUD, as the tree
        // order used to give). Each class keeps its override as a forwarder for direct callers (tests drive p._Process(dt))
        // and turns the engine's callback off with SetProcess(false) in _Ready -- the PlayerController/TickProxy pattern.
        struct Frame { public Node Node; public System.Action<double> Tick; }
        static readonly System.Collections.Generic.List<Frame> _procs = new(), _phys = new();
        public static void AddProcess(Node node, System.Action<double> tick) { Ensure(node); _procs.Add(new Frame { Node = node, Tick = tick }); }
        public static void AddPhysics(Node node, System.Action<double> tick) { Ensure(node); _phys.Add(new Frame { Node = node, Tick = tick }); }
        public static void RemoveProcess(Node node) { for (int i = _procs.Count - 1; i >= 0; i--) if (_procs[i].Node == node) _procs.RemoveAt(i); }
        public static void RemovePhysics(Node node) { for (int i = _phys.Count - 1; i >= 0; i--) if (_phys[i].Node == node) _phys.RemoveAt(i); }
        public static int ProcessCount => _procs.Count; public static int PhysicsCount => _phys.Count;
        static void RunFrames(System.Collections.Generic.List<Frame> list, double delta)
        {
            for (int i = 0; i < list.Count; i++)   // forward = registration order
            {
                var f = list[i];
                if (!GodotObject.IsInstanceValid(f.Node)) { list.RemoveAt(i--); continue; }
                if (!f.Node.IsInsideTree() || !f.Node.CanProcess()) continue;   // removed-but-alive (UI panels) / paused: skipped exactly like the engine would
                f.Tick(delta);
            }
        }
        public override void _Process(double delta)
        {
            StorageCrate.TickAll(delta);
            RunFrames(_procs, delta);
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
        public override void _PhysicsProcess(double delta) { Vehicle.PhysicsTickAll(delta); RunFrames(_phys, delta); }   // vehicles first (see ORDER above), then the registered physics ticks
    }
}
