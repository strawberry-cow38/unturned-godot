using Godot;
using System.Collections.Generic;
using System.Linq;

namespace UnturnedGodot.Testing
{
    // TickHub turned ~500 independent per-node engine callbacks into ONE. That trades 500 independent failures for
    // one total one: if the hub is missing, freed, or never reaches a registrant, every container and vehicle
    // silently stops ticking and nothing errors -- a drive test still passes while fridges never update. So this
    // pins the WIRING itself (register -> the hub steps -> the registrant's tick count went up), on both sides of
    // the hub, rather than one visible consequence of it (tinyclaw's review, 2026-09-02).
    public partial class TickHubProbeCrate : StorageCrate
    {
        public int Ticks;
        protected override void Tick(double delta) => Ticks++;
    }

    public class TickHubWiring : GameTest
    {
        public override string Name => "tickhub.wiring";
        public override double TimeoutSimSeconds => 30;

        public override IEnumerable<Step> Run()
        {
            var p = new TickHubProbeCrate();
            World.AddChild(p);
            yield return Until(() => p.Ticks >= 2, 5);
            T.Check($"a registered container is ticked by the hub ({p.Ticks} ticks within 5 s)", p.Ticks >= 2);

            int hubs = World.GetTree().Root.GetChildren().Count(c => c is TickHub);
            T.Check($"exactly one TickHub lives under the scene root ({hubs})", hubs == 1);

            long v0 = Vehicle.PhysicsTickAllCalls;
            yield return Ticks(10);
            long dv = Vehicle.PhysicsTickAllCalls - v0;
            T.Check($"the hub drives Vehicle.PhysicsTickAll every physics tick (+{dv} over 10 ticks)", dv >= 10);

            World.RemoveChild(p);   // _ExitTree unregisters -> no more ticks, exactly like a freed per-node _Process
            int frozen = p.Ticks;
            yield return Ticks(60);   // 1 s of sim: at least four 4 Hz hub ticks would have landed if it were still registered
            T.Check($"a container that left the tree is no longer ticked ({p.Ticks} == {frozen})", p.Ticks == frozen);
            p.QueueFree();
        }
    }

    // Dropped items went the same way as the containers above (2026-09-12). ETW on the pinned PEI spot: the DISPATCH
    // -- a StringName walk of WorldItem -> RigidBody3D -> PhysicsBody3D -> CollisionObject3D -> Node3D -> Node, one
    // method name at a time, before the body runs at all -- cost 2,385 ms against 3,587 ms of actual work, and on all
    // but ~4 frames a second the body it finally reached was a timer decrement.
    //
    // There are TWO ways that change can go wrong, and only one of them is visible:
    //   * the hub never reaches the items -> they stop hiding behind walls, and nothing errors; or
    //   * SetProcess(false) never happened -> the engine STILL dispatches by name, the saving is entirely imaginary,
    //     and every "do items still tick?" check stays green because they do -- twice.
    // The second is the one a test normally misses, so it is pinned here explicitly rather than assumed.
    public partial class TickHubProbeItem : WorldItem
    {
        public int Ticks;
        public override void _Process(double delta) => Ticks++;   // wiring probe only; the body is unchanged and covered elsewhere
    }

    public class WorldItemHubWiring : GameTest
    {
        public override string Name => "tickhub.worlditem";
        public override double TimeoutSimSeconds => 30;

        public override IEnumerable<Step> Run()
        {
            int before = WorldItem.LiveCount;
            var it = new TickHubProbeItem { FallbackColor = Colors.White, FallbackName = "probe" };
            World.AddChild(it);
            it.GlobalPosition = new Vector3(0, 40, 0);
            yield return Ticks(2);
            T.Check($"a spawned item registers with the hub ({WorldItem.LiveCount} == {before} + 1)", WorldItem.LiveCount == before + 1);

            // THE PERF ASSERTION, and the reason this test exists. If the engine is still dispatching _Process by
            // name then the change bought nothing -- and every other check in this method would still pass.
            T.Check("the engine's per-node _Process callback is OFF (SetProcess(false) took effect)", !it.IsProcessing());

            int t0 = it.Ticks;
            yield return Ticks(30);
            T.Check($"...and the hub ticks it regardless (+{it.Ticks - t0} over 30 physics ticks)", it.Ticks > t0);

            World.RemoveChild(it);   // _ExitTree deregisters -- exactly like a freed per-node _Process
            int frozen = it.Ticks;
            yield return Ticks(30);
            T.Check($"an item that left the tree is no longer ticked ({it.Ticks} == {frozen})", it.Ticks == frozen);
            T.Check($"...and it deregistered ({WorldItem.LiveCount} == {before})", WorldItem.LiveCount == before);
            it.QueueFree();
        }
    }
}
