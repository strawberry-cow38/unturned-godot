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
        public override void _Process(double delta) => StorageCrate.TickAll(delta);
        public override void _PhysicsProcess(double delta) => Vehicle.PhysicsTickAll(delta);
    }
}
