using Godot;
using System.Collections.Generic;
using SDG.NetTransport.Mem;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot.Testing
{
    /// <summary>
    /// strawberry 2026-09-16: "food in the cooking containers is not getting cooked."
    ///
    /// ⭐ THE PATH THE EXISTING REPRO CANNOT SEE. CookerPowerRepro already asserts every symptom of that
    /// report -- an oven cooks, a bbq cooks, the switch survives closing the panel -- and it PASSES, because
    /// it runs on MpLoopback. MpLoopback is the one place in the entire codebase that ever wrote
    /// `ToggledOn = true` on a grid source, so it is the one place where the mains are up. A dedicated
    /// server placed the same fixtures and left them off, and its own field comment said "mains OFF"
    /// without anyone reading that as the defect.
    ///
    /// So the green test and the broken game were both telling the truth about different worlds. This test
    /// exists to cover the world nobody was testing, and it is deliberately about the POWER rather than the
    /// cooking: the cooking logic is already proven on the loopback, and re-proving it here would pass for
    /// reasons that have nothing to do with why it was broken.
    ///
    /// ⚠ Electric appliances only. A barbecue burns charcoal and never consults the mains, which is exactly
    /// why the report read as "cooking is broken" instead of "the power is off" -- the half that still
    /// worked was the half with fuel in it.
    /// </summary>
    public class DedicatedMainsTests : GameTest
    {
        public override string Name => "net.dedicated_mains";
        public override double TimeoutSimSeconds => 60;

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                syncLoad: true, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready", world.Ready);
            ItemCatalog.RegisterAll();

            var net = new MemNetwork(20260916);
            world.Sim.Sim.Add(new DelegateSimStep((t, dt) => net.Tick(), "l1.netpump"));

            // ONE grid source, handed in exactly as WorldBuilder hands the real map's fixtures over.
            var fixtures = new List<FixtureRecord>
            {
                new FixtureRecord { DefId = 9200, Pos = new Vector3(6f, 0f, 0f), YawDegrees = 0f,
                                                 Basis = Basis.Identity, StationId = 0 },
            };
            var ded = new DedicatedServer
            {
                Driver = world.Sim, TransportOverride = new MemServerTransport(net),
                RemoteAvatars = true, Fixtures = fixtures,
            };
            World.AddChild(ded);
            yield return Ticks(4);

            // Did the fixture get placed at all? If this fails the test is measuring its own rig, not the bug.
            int sources = 0, on = 0;
            foreach (var e in ded.Server.Deployables.All)
                if (ded.Server.Deployables.Schema.TryGet(e.DefId, out var d) && d.FixtureKind == FixtureKind.GridSource)
                { sources++; if (e.ToggledOn) on++; }

            T.Check($"the dedicated server placed the grid source ({sources})", sources > 0);
            if (sources == 0) yield break;

            // ⭐ THE ONE. Before the fix this was 0 of 1 -- placed, and left switched off, forever, because the
            // "grid mains ON by DEFAULT" decision (master 2026-07-20) reached MpLoopback and stopped there.
            T.Check($"...and switched it ON, the way the loopback path always has ({on}/{sources})", on == sources);

            // And the consequence, stated in the terms the bug was reported in: this is the exact predicate
            // ServerCooking asks before it will let an oven, a toaster or a microwave cook anything.
            bool mains = false;
            foreach (var e in ded.Server.Deployables.All)
                if (ded.Server.Deployables.Schema.TryGet(e.DefId, out var d)
                    && d.FixtureKind == FixtureKind.GridSource && e.ToggledOn) { mains = true; break; }
            T.Check($"so the mains are UP on a dedicated server ({mains}) -- which is what an oven asks", mains);
            yield break;
        }
    }
}
