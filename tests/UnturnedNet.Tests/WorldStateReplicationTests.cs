using NUnit.Framework;
using SDG.Unturned;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // Phase 8 world state (MP_PLAN §3.7) at the L0 layer, on the full NetWorldServer/NetWorldClient stack
    // over deterministic MemTransport: the tick-derived world clock, server-owned crops (Plant/Harvest
    // commands, the tick growth clock, the server-side AGRICULTURE second-yield roll), and the resource
    // alive-bitmap (join bitmap + delta flips + events).
    [TestFixture]
    public class WorldStateReplicationTests
    {
        const ushort SeedId = 950;
        const ushort YieldId = 951;
        const uint GrowthSeconds = 2;   // 100 ticks -- fast enough to mature inside a test

        [SetUp]
        public void SetUp()
        {
            TransactionalFixtures.RegisterAssets();
            Assets.add(new ItemAsset { id = SeedId, itemName = "Carrot Seed", size_x = 1, size_y = 1 });
            Assets.add(new ItemAsset { id = YieldId, itemName = "Carrot", size_x = 1, size_y = 1 });
        }

        static void RegisterCropSchema(CropSchema schema)
            => schema.Register(new CropNetDef { SeedId = SeedId, GrowthSeconds = GrowthSeconds, YieldItemId = YieldId });

        static TransactionalHarness Harness(int seed)
        {
            var h = new TransactionalHarness(seed);
            RegisterCropSchema(h.Server.Crops.Schema);
            return h;
        }

        // ---- world clock (§3.7 day-night from server tick) ----

        [Test]
        public void WorldClock_ReplicatesOnce_AndBothSidesDeriveTheSameTimeOfDay()
        {
            var h = Harness(80801).Connected("a");
            var a = h.Clients[0];
            Assert.That(a.Clock.HasClock, Is.False, "no clock configured yet -> replicas carry none");

            h.Server.Clock.ServerConfigure(0.35f, 300f, h.Server.Session.CurrentTick);
            h.StepUntil(() => a.Clock.HasClock);
            Assert.That(a.Clock.HasClock, Is.True, "the configured clock reached the replica");
            Assert.That(a.Clock.StateHash(), Is.EqualTo(h.Server.Clock.StateHash()), "clock parity (quantized base + day length)");

            // time of day is DERIVED from the snapshot tick -- never streamed. Same tick -> exact same time.
            long t = a.Applier.LastAppliedServerTick;
            Assert.That(a.Clock.TimeOfDayAt(t), Is.EqualTo(h.Server.Clock.TimeOfDayAt(t)), "identical derivation on both sides");
            float now = a.Clock.TimeOfDayAt(t);
            float later = a.Clock.TimeOfDayAt(t + 750);   // 15 s of ticks on a 300 s day = +0.05 of a day
            Assert.That(Mathf.Abs(later - now - 0.05f), Is.LessThan(0.001f), "time advances at tick x 0.02 / dayLength");

            // an admin set-time re-anchors the base -> the delta block carries it (steady state costs one bit)
            h.Server.Clock.ServerConfigure(0.75f, 300f, h.Server.Session.CurrentTick);
            h.StepUntil(() => a.Clock.StateHash() == h.Server.Clock.StateHash());
            Assert.That(a.Clock.BaseTime01, Is.EqualTo(h.Server.Clock.BaseTime01), "re-anchored base reached the replica");

            // re-configuring the SAME values must not dirty the block (drift republishes are free)
            long changed = h.Server.Clock.LastChangedTick;
            h.Server.Clock.ServerConfigure(0.75f, 300f, h.Server.Session.CurrentTick);
            Assert.That(h.Server.Clock.LastChangedTick, Is.EqualTo(changed), "quantized-equal reconfigure is a no-op");
        }

        // ---- crops (§3.7: Plant/Harvest commands, tick growth clock, server-side yield roll) ----

        [Test]
        public void PlantCommand_SpendsTheSeed_GrowsOnTheTickClock_AndHarvestYields()
        {
            var h = Harness(80802).Connected("planter", "observer");
            var a = h.Clients[0];
            var b = h.Clients[1];
            RegisterCropSchema(b.Crops.Schema);
            h.Server.Transactions.Rand = () => 0.99f;   // deterministic: never a second yield in this test

            // no seed -> the choke point refuses (inventory is the validator, like deployable placement)
            long rejected0 = h.Server.Commands.Diag.ValidationRejected;
            a.SendPlantCrop(SeedId, new Vector3(1f, 0f, 1f));
            h.Step(20);
            Assert.That(h.Server.Crops.Count, Is.EqualTo(0), "seedless plant refused");
            Assert.That(h.Server.Commands.Diag.ValidationRejected, Is.GreaterThan(rejected0));

            h.Grant(a.PlayerId, new Item(SeedId));
            // out of reach (avatar sits near origin; CropReach = 6) -> refused, seed kept
            a.SendPlantCrop(SeedId, new Vector3(100f, 0f, 0f));
            h.Step(20);
            Assert.That(h.Server.Crops.Count, Is.EqualTo(0), "out-of-reach plant refused");

            a.SendPlantCrop(SeedId, new Vector3(1f, 0f, 1f));
            h.StepUntil(() => h.Server.Crops.Count == 1);
            Assert.That(h.Server.Crops.Count, Is.EqualTo(1), "planted server-side");
            Assert.That(h.Server.Inventories.TryGet(a.PlayerId, out var inv) && inv.Inventory.getItemCount(SeedId) == 0,
                        Is.True, "planting spent the seed");
            h.StepUntil(() => b.Crops.Count == 1);
            Assert.That(b.Crops.Count, Is.EqualTo(1), "the observer mirrors the crop (event + snap)");
            Assert.That(b.Crops.StateHash(), Is.EqualTo(h.Server.Crops.StateHash()), "crop parity");

            uint cropId = 0;
            foreach (var e in h.Server.Crops.All) cropId = e.NetIdValue;

            // harvest before maturity -> refused at the choke point; the crop stays
            long rejected1 = h.Server.Commands.Diag.ValidationRejected;
            a.SendHarvestCrop(cropId);
            h.Step(20);
            Assert.That(h.Server.Crops.Count, Is.EqualTo(1), "immature harvest refused");
            Assert.That(h.Server.Commands.Diag.ValidationRejected, Is.GreaterThan(rejected1));

            // the growth clock is the server tick: both sides agree the moment it matures (same math)
            h.StepUntil(() =>
            {
                h.Server.Crops.TryGet(cropId, out var e);
                return e != null && h.Server.Crops.IsGrown(e, h.Server.Session.CurrentTick);
            }, (int)(GrowthSeconds * 50) + 50);
            // the replica derives maturity from the SAME tick clock -- its known tick trails the server by
            // one snapshot interval, so give it a couple of snapshots to observe a tick past maturity
            Assert.That(h.StepUntil(() => b.Crops.TryGet(cropId, out var r)
                                       && b.Crops.IsGrown(r, b.Applier.LastAppliedServerTick), 50), Is.True,
                        "the replica derives maturity from the tick clock -- no stage ever crossed the wire");

            uint xpBefore = h.Server.Skills.TryGet(a.PlayerId, out var sk) ? sk.Skills.experience : 0;
            a.SendHarvestCrop(cropId);
            h.StepUntil(() => h.Server.Crops.Count == 0);
            Assert.That(h.Server.Crops.Count, Is.EqualTo(0), "harvest consumed the crop");
            Assert.That(h.Server.WorldItems.Count, Is.EqualTo(1), "one yield item dropped (no AGRICULTURE mastery)");
            foreach (var wi in h.Server.WorldItems.All)
                Assert.That(wi.ItemId, Is.EqualTo(YieldId), "the yield is the def's Grow item");
            Assert.That(h.Server.Skills.TryGet(a.PlayerId, out var sk2) && sk2.Skills.experience == xpBefore + 1,
                        Is.True, "harvest awarded Harvest_Reward_Experience (1)");
            h.StepUntil(() => b.Crops.Count == 0);
            Assert.That(b.Crops.Count, Is.EqualTo(0), "the observer saw the harvest");
        }

        [Test]
        public void AgricultureMastery_RollsTheSecondYield_ServerSide()
        {
            var h = Harness(80803).Connected("farmer");
            var a = h.Clients[0];
            h.Server.Skills.ServerSetSkillLevel(a.PlayerId, "agriculture", int.MaxValue,
                                                h.Server.Session.CurrentTick, out _, out byte applied);
            Assert.That(applied, Is.GreaterThan(0), "agriculture maxed (mastery 1.0)");
            h.Server.Transactions.Rand = () => 0f;   // roll always under mastery -> guaranteed double yield

            h.Grant(a.PlayerId, new Item(SeedId));
            a.SendPlantCrop(SeedId, new Vector3(1f, 0f, 1f));
            h.StepUntil(() => h.Server.Crops.Count == 1);
            uint cropId = 0;
            foreach (var e in h.Server.Crops.All) cropId = e.NetIdValue;
            h.StepUntil(() =>
            {
                h.Server.Crops.TryGet(cropId, out var e);
                return e != null && h.Server.Crops.IsGrown(e, h.Server.Session.CurrentTick);
            }, (int)(GrowthSeconds * 50) + 50);

            a.SendHarvestCrop(cropId);
            h.StepUntil(() => h.Server.WorldItems.Count == 2);
            Assert.That(h.Server.WorldItems.Count, Is.EqualTo(2),
                        "max AGRICULTURE + a sub-mastery roll = the second yield item (rolled by the SERVER)");
        }

        [Test]
        public void Crops_SurviveJoin_ThroughTheFullSnapshot()
        {
            var h = Harness(80804).Connected("farmer");
            var a = h.Clients[0];
            h.Grant(a.PlayerId, new Item(SeedId));
            a.SendPlantCrop(SeedId, new Vector3(1f, 0f, 1f));
            h.StepUntil(() => h.Server.Crops.Count == 1);

            var late = h.AddClient("late");
            RegisterCropSchema(late.Crops.Schema);
            h.StepUntil(() => late.State == NetSessionState.Connected && late.Crops.Count == 1);
            Assert.That(late.Crops.Count, Is.EqualTo(1), "the join-time FULL snapshot carried the crop");
            Assert.That(late.Crops.StateHash(), Is.EqualTo(h.Server.Crops.StateHash()),
                        "join parity incl. PlantedAtTick -- the joiner derives the same growth stage");
        }

        // ---- resources (§3.7: alive-bitmap + events) ----

        [Test]
        public void Resources_JoinBitmap_DeltaFlips_AndEvents()
        {
            var h = Harness(80805);
            h.Server.Resources.ServerInit(500, h.Server.Session.CurrentTick);
            h.Connected("a");
            var a = h.Clients[0];
            int harvested = 0, respawned = 0;
            a.ResourceHarvested += _ => harvested++;
            a.ResourceRespawned += _ => respawned++;

            h.StepUntil(() => a.Resources.Count == 500);
            Assert.That(a.Resources.Count, Is.EqualTo(500), "the join full carried the whole bitmap");
            Assert.That(a.Resources.AliveCount, Is.EqualTo(500), "everything alive at boot");

            Assert.That(h.Server.Transactions.SetResourceAlive(123, false), Is.True);
            h.StepUntil(() => !a.Resources.IsAlive(123));
            Assert.That(a.Resources.IsAlive(123), Is.False, "the felled tree's bit flipped on the replica");
            Assert.That(harvested, Is.EqualTo(1), "the ResourceHarvested fact fired once");
            Assert.That(a.Resources.StateHash(), Is.EqualTo(h.Server.Resources.StateHash()), "bitmap parity");

            Assert.That(h.Server.Transactions.SetResourceAlive(123, false), Is.False, "idempotent -- no double fact");

            Assert.That(h.Server.Transactions.SetResourceAlive(123, true), Is.True);
            h.StepUntil(() => a.Resources.IsAlive(123));
            Assert.That(respawned, Is.EqualTo(1), "the ResourceRespawned fact fired");

            // a late joiner's bitmap already reflects history
            h.Server.Transactions.SetResourceAlive(7, false);
            h.Server.Transactions.SetResourceAlive(499, false);
            var late = h.AddClient("late");
            h.StepUntil(() => late.State == NetSessionState.Connected && late.Resources.Count == 500);
            Assert.That(late.Resources.IsAlive(7), Is.False, "join bitmap: dead index 7");
            Assert.That(late.Resources.IsAlive(499), Is.False, "join bitmap: dead index 499");
            Assert.That(late.Resources.AliveCount, Is.EqualTo(498));
            Assert.That(late.Resources.StateHash(), Is.EqualTo(h.Server.Resources.StateHash()), "join bitmap parity");
        }

        [Test]
        public void WorldState_ConvergesUnderLossAndReorder()
        {
            // the §6 adverse-network shape: 20% loss + jitter both ways; crops/clock/resources still converge
            var loss = new SDG.NetTransport.Mem.FaultyLinkConfig { LossProbability = 0.2, LatencyTicks = 2, ReorderJitterTicks = 3 };
            var h = new TransactionalHarness(80806, loss, loss);
            RegisterCropSchema(h.Server.Crops.Schema);
            h.Server.Resources.ServerInit(64, h.Server.Session.CurrentTick);
            h.Connected("a");
            var a = h.Clients[0];
            RegisterCropSchema(a.Crops.Schema);

            h.Server.Clock.ServerConfigure(0.5f, 120f, h.Server.Session.CurrentTick);
            h.Grant(a.PlayerId, new Item(SeedId));
            a.SendPlantCrop(SeedId, new Vector3(1f, 0f, 1f));
            h.Server.Transactions.SetResourceAlive(33, false);

            bool converged = h.StepUntil(() =>
                a.Clock.HasClock
                && a.Crops.Count == 1
                && a.Resources.Count == 64 && !a.Resources.IsAlive(33)
                && a.Crops.StateHash() == h.Server.Crops.StateHash()
                && a.Resources.StateHash() == h.Server.Resources.StateHash(), 800);
            Assert.That(converged, Is.True, $"world state converged under 20% loss (seed={h.Net.Seed})");
            Assert.That(a.Clock.StateHash(), Is.EqualTo(h.Server.Clock.StateHash()), "clock parity under loss");
        }
    }
}
