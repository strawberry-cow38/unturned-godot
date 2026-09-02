using Godot;

namespace UnturnedGodot
{
    // A 2-method node that takes the engine's per-frame callbacks on behalf of a BIG class.
    //
    // Every engine->C# call walks the target class's generated dispatch table (all its methods, then every base
    // class) comparing StringNames; a class like PlayerController (~360 methods) pays that walk four times a frame
    // (_process + its notification, _physics_process + its notification). Handing the callbacks to this proxy child
    // makes the walk trivially short; the proxy sits at child index 0 so the owner's logic still runs before the
    // owner's other children, exactly where its own callback used to. ProcessMode/pause are inherited from the owner.
    public partial class TickProxy : Node
    {
        public System.Action<double> OnProcess, OnPhysics;
        public override void _Process(double delta) => OnProcess?.Invoke(delta);
        public override void _PhysicsProcess(double delta) => OnPhysics?.Invoke(delta);
        public static TickProxy Attach(Node owner, System.Action<double> process, System.Action<double> physics)
        {
            var p = new TickProxy { Name = "TickProxy", OnProcess = process, OnPhysics = physics };
            owner.AddChild(p);
            owner.MoveChild(p, 0);
            return p;
        }
    }
}
