using NUnit.Framework;
using SDG.Unturned;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // MP_PLAN §3.1 -- the showcase: the server owns the deployable/wire GRAPH; topology changes are reliable
    // events; scalars ride the snap block; and BOTH sides run the same pure PowerSolver on their own copy,
    // so the lamp states agree without a single solver output crossing the wire. These are the §4 Phase 6
    // "power-graph replication" battery, reusing the PowerSolverTests device fixtures as net defs.
    [TestFixture]
    public class DeployableReplicationTests
    {
        const ushort GEN = TransactionalFixtures.GeneratorId;
        const ushort SPOT = TransactionalFixtures.SpotlightId;

        [SetUp]
        public void SetUp() => TransactionalFixtures.RegisterAssets();

        // client a builds gen(-2) -> wire -> spot(+2) and toggles the generator on; every replica converges
        static (TransactionalHarness h, uint genId, uint spotId) BuildRig(int seed, params string[] clients)
        {
            var h = new TransactionalHarness(seed).Connected(clients);
            var a = h.Clients[0];
            h.Grant(a.PlayerId, new Item(GEN));
            h.Grant(a.PlayerId, new Item(SPOT));

            a.SendPlaceDeployable(GEN, new Vector3(-2f, 0f, 0f), 0f);
            a.SendPlaceDeployable(SPOT, new Vector3(2f, 0f, 0f), 0f);
            Assert.That(h.StepUntil(() => a.Deployables.Count == 2), Is.True, $"placements replicated (seed={seed})");
            uint genId = h.FindDeployable(a, GEN);
            uint spotId = h.FindDeployable(a, SPOT);
            Assert.That(genId, Is.Not.EqualTo(0u));
            Assert.That(spotId, Is.Not.EqualTo(0u));

            a.SendConnectWire(genId, 0, spotId, 0);   // output port 0 -> consumer port 0
            a.SendToggleDeployable(genId, true);
            Assert.That(h.StepUntil(() => a.Deployables.WireCount == 1
                                       && a.Deployables.TryGet(genId, out var g) && g.ToggledOn), Is.True,
                        $"wire + toggle replicated (seed={seed})");
            return (h, genId, spotId);
        }

        [Test]
        public void placement_consumes_the_deployable_item()
        {
            var h = new TransactionalHarness(9071).Connected("a");
            var a = h.Clients[0];
            h.Grant(a.PlayerId, new Item(GEN));

            a.SendPlaceDeployable(GEN, new Vector3(-2f, 0f, 0f), 0f);
            Assert.That(h.StepUntil(() => h.Server.Deployables.Count == 1), Is.True, $"placed (seed={h.Net.Seed})");
            h.Server.Inventories.TryGet(a.PlayerId, out var inv);
            Assert.That(inv.Inventory.getItemCount(GEN), Is.EqualTo(0), "the placed item was spent");

            // no item left -> a second placement is refused at the choke point
            long rejected = h.Server.Commands.Diag.ValidationRejected;
            a.SendPlaceDeployable(GEN, new Vector3(-2f, 0f, 2f), 0f);
            h.Step(20);
            Assert.That(h.Server.Commands.Diag.ValidationRejected, Is.GreaterThan(rejected), "no item, no placement");
            Assert.That(h.Server.Deployables.Count, Is.EqualTo(1));
        }

        [Test]
        public void both_sides_solve_the_replicated_graph_identically()
        {
            var (h, genId, spotId) = BuildRig(9072, "a", "b");
            var a = h.Clients[0];
            var b = h.Clients[1];
            h.Step(10);   // scalar snap settles

            Assert.That(a.Deployables.StateHash(), Is.EqualTo(h.Server.Deployables.StateHash()), "A graph parity");
            Assert.That(b.Deployables.StateHash(), Is.EqualTo(h.Server.Deployables.StateHash()), "B graph parity");

            h.Server.Deployables.Solve();
            b.Deployables.Solve();
            foreach (var side in new[] { h.Server.Deployables, b.Deployables })
            {
                side.TryGet(genId, out var gen);
                side.TryGet(spotId, out var spot);
                Assert.That(gen.Solved[0].Live, Is.EqualTo(4000f), "output produces 4000 W");
                Assert.That(gen.Solved[0].Draw, Is.EqualTo(250f), "generator load = the spotlight's 250 W");
                Assert.That(spot.Solved[0].Powered, Is.True, "consumer powered");
                Assert.That(spot.Solved[0].Live, Is.EqualTo(4000f), "consumer receives the full export");
                Assert.That(spot.Solved[1].Live, Is.EqualTo(3750f), "passthrough re-exports the leftover");
            }
        }

        [Test]
        public void chain_through_passthrough_powers_the_second_spotlight()
        {
            var (h, genId, spotAId) = BuildRig(9073, "a", "b");
            var a = h.Clients[0];
            var b = h.Clients[1];
            h.Grant(a.PlayerId, new Item(SPOT));
            a.SendPlaceDeployable(SPOT, new Vector3(2f, 0f, 3f), 0f);
            Assert.That(h.StepUntil(() => a.Deployables.Count == 3), Is.True, $"second spotlight (seed={h.Net.Seed})");
            uint spotBId = 0;
            foreach (var e in a.Deployables.All)
                if (e.DefId == SPOT && e.NetIdValue != spotAId) spotBId = e.NetIdValue;

            a.SendConnectWire(spotAId, 1, spotBId, 0);   // A's passthrough -> B's consumer
            Assert.That(h.StepUntil(() => b.Deployables.WireCount == 2), Is.True, $"chain wire (seed={h.Net.Seed})");

            h.Server.Deployables.Solve();
            b.Deployables.Solve();
            foreach (var side in new[] { h.Server.Deployables, b.Deployables })
            {
                side.TryGet(spotBId, out var spotB);
                side.TryGet(genId, out var gen);
                Assert.That(spotB.Solved[0].Powered, Is.True, "chained consumer powered through the passthrough");
                Assert.That(gen.Solved[0].Draw, Is.EqualTo(500f), "generator load sums both powered spotlights");
            }
        }

        [Test]
        public void toggle_off_unpowers_every_replica()
        {
            var (h, genId, spotId) = BuildRig(9074, "a", "b");
            var a = h.Clients[0];
            var b = h.Clients[1];

            a.SendToggleDeployable(genId, false);
            Assert.That(h.StepUntil(() => b.Deployables.TryGet(genId, out var g) && !g.ToggledOn), Is.True,
                        $"toggle-off replicated (seed={h.Net.Seed})");

            h.Server.Deployables.Solve();
            b.Deployables.Solve();
            foreach (var side in new[] { h.Server.Deployables, b.Deployables })
            {
                side.TryGet(spotId, out var spot);
                Assert.That(spot.Solved[0].Powered, Is.False, "consumer dark with the source off");
            }
        }

        [Test]
        public void wire_rules_reject_illegal_topology()
        {
            var (h, genId, spotId) = BuildRig(9075, "a");
            var a = h.Clients[0];
            h.Step(5);
            long rejected = h.Server.Commands.Diag.ValidationRejected;
            int wires = h.Server.Deployables.WireCount;

            a.SendConnectWire(genId, 0, spotId, 0);    // one-wire-per-port: both endpoints already wired
            a.SendConnectWire(spotId, 0, genId, 0);    // consumer as source / output as destination
            a.SendConnectWire(genId, 0, genId, 0);     // src == dst deployable
            a.SendConnectWire(genId, 5, spotId, 0);    // port index out of range
            a.SendConnectWire(999u, 0, spotId, 0);     // nonexistent source entity
            a.SendToggleDeployable(spotId, true);      // no tank -> not toggleable
            h.Step(25);

            Assert.That(h.Server.Commands.Diag.ValidationRejected - rejected, Is.EqualTo(6), "every illegal command refused");
            Assert.That(h.Server.Deployables.WireCount, Is.EqualTo(wires), "topology untouched");
        }

        [Test]
        public void out_of_range_placement_rejected()
        {
            var h = new TransactionalHarness(9076).Connected("a");
            var a = h.Clients[0];
            h.Grant(a.PlayerId, new Item(GEN));
            long rejected = h.Server.Commands.Diag.ValidationRejected;

            a.SendPlaceDeployable(GEN, new Vector3(100f, 0f, 100f), 0f);   // player stands at ~origin
            h.Step(20);

            Assert.That(h.Server.Commands.Diag.ValidationRejected, Is.GreaterThan(rejected), "beyond Range + slack");
            Assert.That(h.Server.Deployables.Count, Is.EqualTo(0));
        }

        [Test]
        public void salvage_cascades_wires_and_drops_scrap_everywhere()
        {
            var (h, genId, spotId) = BuildRig(9077, "a", "b");
            var a = h.Clients[0];
            var b = h.Clients[1];

            // only a dead/burning wreck salvages: publish the scalar state first (the node layer's job in game)
            h.Server.Deployables.ServerSetScalars(genId, 0f, 0f, onFire: true, h.Server.Session.CurrentTick);
            Assert.That(h.StepUntil(() => b.Deployables.TryGet(genId, out var g) && g.OnFire), Is.True,
                        $"scalar snap carried the fire state (seed={h.Net.Seed})");

            a.SendSalvageDeployable(genId);
            Assert.That(h.StepUntil(() => b.Deployables.Count == 1 && b.Deployables.WireCount == 0
                                       && b.WorldItems.Count == 2), Is.True,
                        $"remove + wire cascade + 2 scrap drops replicated (seed={h.Net.Seed})");

            foreach (var wi in b.WorldItems.All)
                Assert.That(wi.ItemId, Is.EqualTo(TransactionalFixtures.ScrapId), "the drops are Metal Scrap");
            Assert.That(b.Deployables.StateHash(), Is.EqualTo(h.Server.Deployables.StateHash()), "graph parity");
            Assert.That(b.WorldItems.StateHash(), Is.EqualTo(h.Server.WorldItems.StateHash()), "world-item parity");

            h.Server.Deployables.Solve();
            b.Deployables.Solve();
            foreach (var side in new[] { h.Server.Deployables, b.Deployables })
            {
                side.TryGet(spotId, out var spot);
                Assert.That(spot.Solved[0].Powered, Is.False, "orphaned consumer went dark on both sides");
            }
        }

        [Test]
        public void deploy_pickup_returns_item()
        {
            // B2: hold-F pickup returns the LIVE deployable (with its HP quality + fuel) to the bag and cascades
            // its wires -- distinct from salvage, which scraps a burning wreck. Pre-fix the round trip can't even
            // be expressed (no command), and the SP @677 early-return left a placed generator un-retrievable
            // under consume. Here: place gen->wire->spot, damage/drain the gen server-side, then pick it up.
            var (h, genId, spotId) = BuildRig(9101, "a");
            var a = h.Clients[0];

            // 225/450 HP -> quality 50; 30 fuel -> fuelLevel 30 (both integers survive the scalar quantization)
            h.Server.Deployables.ServerSetScalars(genId, 225f, 30f, onFire: false, h.Server.Session.CurrentTick);

            Assert.That(h.Server.Inventories.TryGet(a.PlayerId, out var inv0), Is.True);
            Assert.That(inv0.Inventory.getItemCount(GEN), Is.EqualTo(0), "both granted deployables were placed (bag empty of generators)");

            a.SendPickupDeployable(genId);
            Assert.That(h.StepUntil(() => !h.Server.Deployables.TryGet(genId, out _)
                                       && h.Server.Deployables.WireCount == 0
                                       && h.Server.Inventories.TryGet(a.PlayerId, out var i)
                                       && i.Inventory.getItemCount(GEN) == 1), Is.True,
                        $"generator removed + wire cascade-removed + item back in the bag (seed={h.Net.Seed})");

            Assert.That(h.Server.Deployables.Count, Is.EqualTo(1), "only the spotlight remains");
            Assert.That(h.Server.Deployables.TryGet(spotId, out _), Is.True, "the spotlight survives the pickup");

            // the returned item is the ACTUAL generator with its stamped HP quality + fuel (not scrap)
            var item = FindItem(inv0.Inventory, GEN);
            Assert.That(item, Is.Not.Null, "the generator item is in the server bag");
            Assert.That((int)item.quality, Is.EqualTo(50), "HP 225/450 -> quality 50%");
            Assert.That(item.fuelLevel, Is.EqualTo(30f), "the tank (30 fuel) rode back on the item");
        }

        [Test]
        public void deploy_pickup_full_bag_drops_world_item()
        {
            // B2 full-bag branch: with no room in the grid the returned deployable drops as a world item at the
            // entity's position (SP "drop where it stood" @691), instead of vanishing.
            var h = new TransactionalHarness(9102).Connected("a");
            var a = h.Clients[0];

            // place a generator over the wire (spends the one granted item -> bag empty, a real NetId placed)
            h.Grant(a.PlayerId, new Item(GEN));
            a.SendPlaceDeployable(GEN, new Vector3(1f, 0f, 1f), 0f);
            Assert.That(h.StepUntil(() => a.Deployables.Count == 1), Is.True, $"placement replicated (seed={h.Net.Seed})");
            uint genId = h.FindDeployable(a, GEN);
            Assert.That(genId, Is.Not.EqualTo(0u));

            // now STUFF the 5x3 pocket grid so a 2x2 generator can no longer fit (two 2x2 gens leave no 2x2 gap)
            h.Grant(a.PlayerId, new Item(GEN));
            h.Grant(a.PlayerId, new Item(GEN));
            Assert.That(h.Server.Inventories.TryGet(a.PlayerId, out var inv), Is.True);
            Assert.That(inv.Inventory.getItemCount(GEN), Is.EqualTo(2));

            int worldItemsBefore = h.Server.WorldItems.Count;
            a.SendPickupDeployable(genId);
            Assert.That(h.StepUntil(() => !h.Server.Deployables.TryGet(genId, out _)
                                       && h.Server.WorldItems.Count == worldItemsBefore + 1), Is.True,
                        $"full bag -> the generator dropped as a world item (seed={h.Net.Seed})");
            Assert.That(inv.Inventory.getItemCount(GEN), Is.EqualTo(2), "the bag was full -> pickup did NOT add a third generator");

            bool found = false;
            foreach (var wi in h.Server.WorldItems.All)
                if (wi.ItemId == GEN) { found = true; break; }
            Assert.That(found, Is.True, "the spawned world item is the generator (458)");
        }

        // scan every page for the first jar holding the given item id (server-grid inspection)
        static Item FindItem(PlayerInventory inv, ushort id)
        {
            foreach (var page in inv.items)
                foreach (var jar in page.items)
                    if (jar.item != null && jar.item.id == id) return jar.item;
            return null;
        }

        [Test]
        public void late_joiner_gets_the_graph_from_the_join_snapshot()
        {
            var (h, genId, spotId) = BuildRig(9078, "a");
            h.Step(20);

            var c = h.AddClient("late");
            Assert.That(h.StepUntil(() => c.State == NetSessionState.Connected && c.Deployables.Count == 2
                                       && c.Deployables.WireCount == 1), Is.True,
                        $"join snapshot carried the whole graph (seed={h.Net.Seed})");
            Assert.That(c.Deployables.StateHash(), Is.EqualTo(h.Server.Deployables.StateHash()), "late-join parity");

            c.Deployables.Solve();
            c.Deployables.TryGet(spotId, out var spot);
            Assert.That(spot.Solved[0].Powered, Is.True, "the late joiner's own solve lights the lamp");
        }

        [Test]
        public void survives_lossy_reordered_links()
        {
            // the whole build sequence over adverse links: the reliable event plane must carry the topology
            var loss = new SDG.NetTransport.Mem.FaultyLinkConfig { LossProbability = 0.2, DuplicateProbability = 0.05, LatencyTicks = 2, ReorderJitterTicks = 3 };
            var h2 = new TransactionalHarness(424242, loss, loss).Connected("a", "b");
            var a = h2.Clients[0];
            var b = h2.Clients[1];
            h2.Grant(a.PlayerId, new Item(GEN));
            h2.Grant(a.PlayerId, new Item(SPOT));
            a.SendPlaceDeployable(GEN, new Vector3(-2f, 0f, 0f), 0f);
            a.SendPlaceDeployable(SPOT, new Vector3(2f, 0f, 0f), 0f);
            Assert.That(h2.StepUntil(() => a.Deployables.Count == 2, 800), Is.True, $"placements under loss (seed={h2.Net.Seed})");
            uint gen2 = h2.FindDeployable(a, GEN);
            uint spot2 = h2.FindDeployable(a, SPOT);
            a.SendConnectWire(gen2, 0, spot2, 0);
            a.SendToggleDeployable(gen2, true);
            Assert.That(h2.StepUntil(() => b.Deployables.WireCount == 1
                                        && b.Deployables.TryGet(gen2, out var g) && g.ToggledOn, 800), Is.True,
                        $"topology facts survived 20% loss + reorder (seed={h2.Net.Seed})");
            Assert.That(b.Deployables.StateHash(), Is.EqualTo(h2.Server.Deployables.StateHash()), "parity under loss");

            b.Deployables.Solve();
            b.Deployables.TryGet(spot2, out var spot);
            Assert.That(spot.Solved[0].Powered, Is.True);
        }
    }
}
