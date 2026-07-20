using NUnit.Framework;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // A3 (SP/MP-unify) -- the grid-power mains SOURCE as a server-placed deployable-graph FIXTURE. Instead of an
    // SP-local IPowerDevice built only under Playable, every Circuit_0 becomes a DeployableEntity (FixtureKind.
    // GridSource) the server places at world-build; it rides the EXISTING SystemDeployables replication, and the
    // F1/toggleGlobalPower mains switch is a server-gated MECHANIC (RunConsole, before the AllowCheats gate) that
    // flips every source's ToggledOn bit + broadcasts. Both sides then run the same pure PowerSolver so a wired
    // consumer lights identically on the authority and the replica -- no solver output ever crosses the wire (§3.1).
    [TestFixture]
    public class DeployableGridSourceTests
    {
        const ushort GRID = TransactionalFixtures.GridSourceId;   // 9200: FixtureKind.GridSource, one 10kW Output
        const ushort SPOT = TransactionalFixtures.SpotlightId;    // 250W Consumer (+ Passthrough)

        [SetUp]
        public void SetUp() => TransactionalFixtures.RegisterAssets();

        [Test]
        public void source_replicates_and_energizes()
        {
            var h = new TransactionalHarness(9200).Connected("a");
            var a = h.Clients[0];

            // server-PLACE the grid source (a world fixture, mains OFF) + a spotlight consumer, and wire them
            // (Output port 0 -> Consumer port 0) -- exactly what the dedicated/consuming-loopback server does at
            // world-build. Pre-fix the GridSource def is absent from the schema, so ServerPlace returns null here.
            var grid = h.Server.Deployables.ServerPlace(h.Server.Ids.Mint(), GRID, 0, new Vector3(-2f, 0f, 0f), 0f, h.Server.Session.CurrentTick);
            Assert.That(grid, Is.Not.Null, "GridSource def registered -> the fixture places (pre-fix: def absent -> null, no source)");
            var spot = h.Server.Deployables.ServerPlace(h.Server.Ids.Mint(), SPOT, 0, new Vector3(2f, 0f, 0f), 0f, h.Server.Session.CurrentTick);
            h.Server.Deployables.ServerConnectWire(h.Server.Ids.Mint(), grid.NetIdValue, 0, spot.NetIdValue, 0, h.Server.Session.CurrentTick);

            Assert.That(h.StepUntil(() => a.Deployables.Count == 2 && a.Deployables.WireCount == 1), Is.True,
                        $"grid source + consumer + wire replicated to the client (seed={h.Net.Seed})");
            Assert.That(a.Deployables.TryGet(grid.NetIdValue, out var cGrid) && cGrid.DefId == GRID, Is.True,
                        "the client mirrors the grid-source entity (DefId 9200)");

            // OFF (default, mains never toggled) -> the wired consumer is dark on BOTH sides
            h.Server.Deployables.Solve();
            a.Deployables.Solve();
            h.Server.Deployables.TryGet(spot.NetIdValue, out var sSpotOff);
            a.Deployables.TryGet(spot.NetIdValue, out var cSpotOff);
            Assert.That(sSpotOff.Solved[0].Powered, Is.False, "OFF: server consumer dark (mains never toggled)");
            Assert.That(cSpotOff.Solved[0].Powered, Is.False, "OFF: client consumer dark");

            // toggle the MAINS ON through the server-gated console MECHANIC (the F1 toggleGlobalPower wire path).
            // Pre-fix RunConsole has no toggleglobalpower verb -> the source never toggles -> the consumer stays dark.
            h.Server.Transactions.RunConsole(a.PlayerId, "toggleglobalpower on");
            Assert.That(h.StepUntil(() => a.Deployables.TryGet(grid.NetIdValue, out var g) && g.ToggledOn), Is.True,
                        $"mains-on toggled every GridSource + replicated the ToggledOn bit (seed={h.Net.Seed})");

            // ON -> the 10kW mains energize the wired consumer, identically on both sides (same solver, same inputs)
            h.Server.Deployables.Solve();
            a.Deployables.Solve();
            h.Server.Deployables.TryGet(grid.NetIdValue, out var sGrid);
            h.Server.Deployables.TryGet(spot.NetIdValue, out var sSpot);
            a.Deployables.TryGet(spot.NetIdValue, out var cSpot);
            Assert.That(sGrid.Solved[0].Live, Is.EqualTo(10000f), "ON: the grid output produces its 10kW");
            Assert.That(sGrid.Solved[0].Draw, Is.EqualTo(250f), "ON: mains load = the spotlight's 250W");
            Assert.That(sSpot.Solved[0].Powered, Is.True, "ON: server consumer powered off the mains");
            Assert.That(cSpot.Solved[0].Powered, Is.True, "ON: client consumer powered (replicated ToggledOn, same solve)");
            Assert.That(a.Deployables.StateHash(), Is.EqualTo(h.Server.Deployables.StateHash()), "graph parity after the mains toggle");

            // toggle OFF -> dark again: the SAME consumer assert flips with the mains bit -> it energizes BY the
            // toggle, not by luck (teeth). Bare `grid` here also exercises the flip-current-state path.
            h.Server.Transactions.RunConsole(a.PlayerId, "grid");
            Assert.That(h.StepUntil(() => a.Deployables.TryGet(grid.NetIdValue, out var g) && !g.ToggledOn), Is.True,
                        $"bare toggle flipped the mains back OFF + replicated (seed={h.Net.Seed})");
            h.Server.Deployables.Solve();
            a.Deployables.Solve();
            a.Deployables.TryGet(spot.NetIdValue, out var cSpotOff2);
            Assert.That(cSpotOff2.Solved[0].Powered, Is.False, "OFF again: client consumer dark once the mains toggle off");
        }
    }
}
