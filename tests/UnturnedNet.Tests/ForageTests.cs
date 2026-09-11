using NUnit.Framework;
using SDG.Unturned;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // Picking berry bushes and mushrooms (retail InteractableForage + ResourceManager.ReceiveForageRequest)
    // at the L0 layer, over the real client/server stack on deterministic MemTransport.
    //
    // Master's requirement was specific -- "make sure the harvest path goes through the server" -- so these
    // are aimed at the ways a harvest quietly stops being server-owned rather than at the happy path alone:
    // a client foraging something that is not forageable, foraging from across the map, foraging the same
    // plant twice, or the alive bit gaining a second writer.
    [TestFixture]
    public class ForageTests
    {
        // A reward the fixtures actually register, so a full bag/failed-add cannot be mistaken for a refusal.
        // The REAL retail table (Bush_Amber -> 963 and friends) is asserted game-side, where it is read.
        const ushort BerryId = TransactionalFixtures.BeansId;

        [SetUp]
        public void SetUp() => TransactionalFixtures.RegisterAssets();

        /// <summary>Seed a world of `count` resources with one forageable plant at `at`.</summary>
        static void Seed(TransactionalHarness h, int count, int index, Vector3 at, long resetTicks = 20)
        {
            h.Server.Resources.ServerInit(count, h.Server.Session.CurrentTick);
            h.Server.ForageHost.ServerInit(count);
            h.Server.ForageHost.SetMeta(index, BerryId, at, resetTicks);
        }

        [Test]
        public void Forage_GivesTheItem_KillsThePlant_AndReplicates()
        {
            var h = new TransactionalHarness(4301);
            Seed(h, 30, index: 7, at: Vector3.zero);
            h.Connected("a", "b");
            var a = h.Clients[0]; var b = h.Clients[1];
            h.StepUntil(() => a.Resources.Count == 30 && b.Resources.Count == 30);

            var inv = h.Server.Transactions.InventoryForTest(a.PlayerId);
            int before = inv.getItemCount(BerryId);
            h.Server.Skills.TryGet(a.PlayerId, out var sk0);
            uint xpBefore = sk0.Skills.experience;

            a.SendForageResource(7);
            h.StepUntil(() => !h.Server.Resources.IsAlive(7));

            Assert.That(h.Server.Resources.IsAlive(7), Is.False, "the picked plant is gone on the server");
            Assert.That(inv.getItemCount(BerryId), Is.EqualTo(before + 1),
                        "the reward landed in the SERVER's inventory -- the client grants nothing");
            Assert.That(h.Server.Skills.TryGet(a.PlayerId, out var sk1) && sk1.Skills.experience == xpBefore + 1,
                        Is.True, "Forage_Reward_Experience (1) awarded server-side");
            Assert.That(h.StepUntil(() => !b.Resources.IsAlive(7)), Is.True,
                        "the OTHER player saw the bush vanish -- a forage is a broadcast fact, not a local hide");
            Assert.That(a.Resources.StateHash(), Is.EqualTo(h.Server.Resources.StateHash()), "bitmap parity");
        }

        [Test]
        public void Forage_IsRefused_FromAcrossTheMap()
        {
            var h = new TransactionalHarness(4302);
            // 25 m away: past retail's 400 sq.m gate, and far enough that no interact ray could reach it.
            Seed(h, 30, index: 7, at: new Vector3(0f, 0f, 25f));
            h.Connected("a");
            var a = h.Clients[0];
            h.StepUntil(() => a.Resources.Count == 30);
            var inv = h.Server.Transactions.InventoryForTest(a.PlayerId);
            int before = inv.getItemCount(BerryId);

            a.SendForageResource(7);
            h.Step(30);

            Assert.That(h.Server.Resources.IsAlive(7), Is.True, "a plant out of reach is still standing");
            Assert.That(inv.getItemCount(BerryId), Is.EqualTo(before), "and handed over nothing");
        }

        [Test]
        public void Forage_IsRefused_OnAnythingNotRegisteredForageable()
        {
            var h = new TransactionalHarness(4303);
            Seed(h, 30, index: 7, at: Vector3.zero);
            h.Connected("a");
            var a = h.Clients[0];
            h.StepUntil(() => a.Resources.Count == 30);
            var inv = h.Server.Transactions.InventoryForTest(a.PlayerId);
            int before = inv.getItemCount(BerryId);

            // index 8 is a TREE as far as the server is concerned: it exists in the bitmap and was never
            // registered forageable. Nothing about the wire stops a client naming it, so the server must.
            a.SendForageResource(8);
            h.Step(30);
            Assert.That(h.Server.Resources.IsAlive(8), Is.True, "an unregistered index cannot be foraged");
            Assert.That(inv.getItemCount(BerryId), Is.EqualTo(before), "and pays out nothing");

            // ...and an index past the end of the world is not a crash either.
            a.SendForageResource(9999);
            h.Step(10);
            Assert.That(inv.getItemCount(BerryId), Is.EqualTo(before), "an out-of-range index pays out nothing");
        }

        [Test]
        public void Forage_Twice_GivesOne()
        {
            var h = new TransactionalHarness(4304);
            Seed(h, 30, index: 7, at: Vector3.zero, resetTicks: 100000);   // will not regrow inside the test
            h.Connected("a");
            var a = h.Clients[0];
            h.StepUntil(() => a.Resources.Count == 30);
            var inv = h.Server.Transactions.InventoryForTest(a.PlayerId);
            int before = inv.getItemCount(BerryId);

            a.SendForageResource(7);
            h.StepUntil(() => !h.Server.Resources.IsAlive(7));
            a.SendForageResource(7);
            a.SendForageResource(7);
            h.Step(30);

            Assert.That(inv.getItemCount(BerryId), Is.EqualTo(before + 1),
                        "a picked bush pays out ONCE however many times it is asked -- spamming the key is not a berry printer");
        }

        [Test]
        public void Take_DoesNotFlipTheAliveBit_SoOneWriterOwnsIt()
        {
            // The bit belongs to ServerTransactions.SetResourceAlive, which broadcasts as it flips. If
            // ServerForage flipped it too, a pick would go out with no event behind it -- dead on the
            // server, standing on every client -- which is exactly the failure that has no symptom until
            // somebody walks through a bush that is not there.
            var h = new TransactionalHarness(4305);
            Seed(h, 10, index: 3, at: Vector3.zero);
            long tick = h.Server.Session.CurrentTick;

            Assert.That(h.Server.ForageHost.Take(3, Vector3.zero, tick), Is.EqualTo(BerryId), "Take reports the reward");
            Assert.That(h.Server.Resources.IsAlive(3), Is.True, "...and leaves the alive bit to its one writer");
        }

        [Test]
        public void PickedPlant_RegrowsAfterItsReset()
        {
            var h = new TransactionalHarness(4306);
            Seed(h, 10, index: 3, at: Vector3.zero, resetTicks: 20);
            long tick = h.Server.Session.CurrentTick;
            var regrown = new System.Collections.Generic.List<int>();

            Assert.That(h.Server.ForageHost.Take(3, Vector3.zero, tick), Is.EqualTo(BerryId));
            Assert.That(h.Server.ForageHost.CollectRegrown(tick + 19, regrown), Is.EqualTo(0),
                        "nothing comes back one tick early");
            Assert.That(h.Server.ForageHost.CollectRegrown(tick + 20, regrown), Is.EqualTo(1), "and comes back on time");
            Assert.That(regrown[0], Is.EqualTo(3));
            Assert.That(h.Server.ForageHost.CollectRegrown(tick + 200, regrown), Is.EqualTo(0),
                        "collected once, not every tick after -- a repeating regrow would rebroadcast forever");
        }

        [Test]
        public void CanForage_IsTheSameGateTheWireRuns()
        {
            var h = new TransactionalHarness(4307);
            Seed(h, 10, index: 3, at: Vector3.zero);
            Assert.That(h.Server.ForageHost.CanForage(3, Vector3.zero), Is.True, "in reach, registered, alive");
            Assert.That(h.Server.ForageHost.CanForage(3, new Vector3(0f, 0f, 19.9f)), Is.True, "just inside 20 m");
            Assert.That(h.Server.ForageHost.CanForage(3, new Vector3(0f, 0f, 20.1f)), Is.False, "just outside it");
            Assert.That(h.Server.ForageHost.CanForage(4, Vector3.zero), Is.False, "an unregistered index");
            Assert.That(h.Server.ForageHost.CanForage(-1, Vector3.zero), Is.False, "a negative index");
            Assert.That(h.Server.ForageHost.IsForageable(3), Is.True);
            Assert.That(h.Server.ForageHost.IsForageable(4), Is.False);
        }
    }
}
