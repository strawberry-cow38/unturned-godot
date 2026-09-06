using System.Collections.Generic;
using NUnit.Framework;
using SDG.Unturned;
using UnityEngine;
using UnturnedGodot;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    /// <summary>Timed crafting on the server (master 2026-09-06). The property worth the most here is that
    /// ingredients leave the bag at ENQUEUE -- a queue that only checks at the start and takes at the end is a
    /// duplication bug that looks like patience.</summary>
    [TestFixture]
    public class ServerCraftingTests
    {
        const string LogGuid = "aaaa0000000000000000000000000001";
        const string PlankGuid = "aaaa0000000000000000000000000002";
        const string SawGuid = "aaaa0000000000000000000000000003";

        [SetUp]
        public void SetUp()
        {
            TransactionalFixtures.RegisterAssets();
            Assets.add(new ItemAsset { id = 8001, itemName = "Test Log", size_x = 2, size_y = 1, type = EItemType.SUPPLY, guid = LogGuid });
            Assets.add(new ItemAsset { id = 8002, itemName = "Test Plank", size_x = 1, size_y = 2, type = EItemType.SUPPLY, guid = PlankGuid });
            Assets.add(new ItemAsset { id = 8003, itemName = "Test Saw", size_x = 2, size_y = 1, type = EItemType.MELEE, guid = SawGuid });
        }

        static BlueprintDef Recipe(float secs)
        {
            var bp = new BlueprintDef { Operation = "Craft", OwnerItemId = "8002", Seconds = secs };
            bp.Inputs.Add(new BlueprintDef.Ingredient { Guid = LogGuid, Amount = 1, Consume = true });
            bp.Inputs.Add(new BlueprintDef.Ingredient { Guid = SawGuid, Amount = 1, Consume = false });   // a TOOL
            bp.Outputs.Add(new BlueprintDef.Ingredient { Guid = PlankGuid, Amount = 2, Consume = true });
            return bp;
        }

        static (ServerCrafting c, InventoryReplication inv, PlayerInventory bag) Rig(float secs, int logs)
        {
            var inv = new InventoryReplication();
            inv.ServerAdd(1, 0L);
            inv.TryGet(1, out var e);
            for (int i = 0; i < logs; i++) e.Inventory.tryAddItem(new Item(8001));
            e.Inventory.tryAddItem(new Item(8003));   // the saw
            var list = new List<BlueprintDef> { Recipe(secs) };
            var c = new ServerCrafting(inv) { BlueprintsSource = () => list };
            return (c, inv, e.Inventory);
        }

        [Test]
        public void ingredients_leave_the_bag_the_moment_the_job_is_queued()
        {
            // THE DUPLICATION GUARD. If the log were only taken at payout, both of these queue successfully
            // against the same single log and both pay out -- two planks' worth of product for one log's worth
            // of input, arrived at by waiting.
            var (c, _, bag) = Rig(5f, logs: 1);
            Assert.That(c.Enqueue(1, 0), Is.True, "the first job is affordable");
            Assert.That(bag.getItemCount(8001), Is.Zero, "the log is spent NOW, not at the end");
            Assert.That(c.Enqueue(1, 0), Is.False, "...so a second job cannot be funded by the same log");
            Assert.That(c.JobCount(1), Is.EqualTo(1));
        }

        [Test]
        public void a_tool_is_required_but_never_consumed()
        {
            var (c, _, bag) = Rig(1f, logs: 1);
            c.Enqueue(1, 0);
            Assert.That(bag.getItemCount(8003), Is.EqualTo(1), "a saw survives sawing");
        }

        [Test]
        public void nothing_is_produced_before_the_clock_runs_out()
        {
            var (c, _, bag) = Rig(8f, logs: 1);
            c.Enqueue(1, 0);
            for (int i = 0; i < 100; i++) c.Step(0.02f);      // 2.0 s of an 8 s job
            Assert.That(bag.getItemCount(8002), Is.Zero, "a quarter of the way through is not done");
            Assert.That(c.JobCount(1), Is.EqualTo(1), "...and the job is still pending");

            for (int i = 0; i < 320; i++) c.Step(0.02f);      // past 8 s
            Assert.That(bag.getItemCount(8002), Is.EqualTo(2), "the recipe's two planks arrive at the end");
            Assert.That(c.JobCount(1), Is.Zero, "and the queue empties");
        }

        [Test]
        public void the_recipes_own_time_is_what_is_enforced()
        {
            // Not a fixed base: an 8 s recipe must still be running at 4 s, and a 1 s one must be done.
            var (slow, _, slowBag) = Rig(8f, logs: 1);
            slow.Enqueue(1, 0);
            var (fast, _, fastBag) = Rig(1f, logs: 1);
            fast.Enqueue(1, 0);
            for (int i = 0; i < 200; i++) { slow.Step(0.02f); fast.Step(0.02f); }   // 4 s
            Assert.That(fastBag.getItemCount(8002), Is.EqualTo(2), "the 1 s recipe finished");
            Assert.That(slowBag.getItemCount(8002), Is.Zero, "the 8 s recipe has not");
        }

        [Test]
        public void jobs_run_one_at_a_time_not_all_at_once()
        {
            // Parallel jobs would make the times meaningless the moment you queued a few: three 4 s crafts
            // would all land at 4 s. Serial is what the SP queue does and what the number implies.
            var (c, _, bag) = Rig(4f, logs: 3);
            for (int i = 0; i < 3; i++) Assert.That(c.Enqueue(1, 0), Is.True);
            for (int i = 0; i < 250; i++) c.Step(0.02f);   // 5 s -- past ONE job only
            Assert.That(bag.getItemCount(8002), Is.EqualTo(2), "exactly one job has paid out");
            Assert.That(c.JobCount(1), Is.EqualTo(2));
        }

        [Test]
        public void leaving_mid_craft_gives_the_materials_back()
        {
            // Ingredients are spent up front, so a disconnect without a refund is a player paying for a product
            // that can never be delivered.
            var (c, _, bag) = Rig(30f, logs: 2);
            c.Enqueue(1, 0); c.Enqueue(1, 0);
            Assert.That(bag.getItemCount(8001), Is.Zero);
            Assert.That(c.RefundAll(1), Is.EqualTo(2));
            Assert.That(bag.getItemCount(8001), Is.EqualTo(2), "both logs come back");
            Assert.That(c.JobCount(1), Is.Zero);
        }

        [Test]
        public void the_owner_is_told_whenever_their_queue_changes()
        {
            // The client cannot see a server-side timer any other way -- it skips its own queue in MP.
            var (c, _, _) = Rig(2f, logs: 1);
            int fired = 0;
            c.QueueChanged = _ => fired++;
            c.Enqueue(1, 0);
            Assert.That(fired, Is.EqualTo(1), "queued");
            for (int i = 0; i < 200; i++) c.Step(0.02f);
            Assert.That(fired, Is.EqualTo(2), "...and finished");
        }
    }
}
