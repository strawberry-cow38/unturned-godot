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
}
