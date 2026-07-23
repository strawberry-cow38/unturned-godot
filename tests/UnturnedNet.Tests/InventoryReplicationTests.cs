using NUnit.Framework;
using SDG.Unturned;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // MP_PLAN §3.3 -- the inventory command battery (§4 Phase 6: "L0 inventory command validation: illegal
    // moves rejected, both grids converge by StateHash"). The server's PlayerInventory runs the same ported
    // TryDrag/tryFindSpace cell math SP plays on -- that IS the validator; the owner-only block re-states
    // the whole grid whenever the model's own onStateUpdated dirtiness fires.
    [TestFixture]
    public class InventoryReplicationTests
    {
        [SetUp]
        public void SetUp() => TransactionalFixtures.RegisterAssets();

        static ulong OwnerParity(TransactionalHarness h, NetWorldClient c)
        {
            Assert.That(c.Inventories.StateHash(), Is.EqualTo(h.Server.Inventories.StateHashFor(c.PlayerId)),
                        "owner-block parity: client grid == server grid");
            return c.Inventories.StateHash();
        }

        // The pen-test regression (strawberry: "pen test potential fluid exploits"): a fluid CONTAINER's contents
        // (type + mL + quality) MUST ride the inventory wire. The SP game runs under a consuming loopback that re-adopts
        // the owner inventory through this schema; pre-fix WriteJar dropped the fluid fields, so a bottle drunk to EMPTY
        // round-tripped to fluidAmount -1 (fresh) and lazily REFILLED to full every sync -> infinite drinking, and a
        // TAINTED container reset its quality -> a free-clean-water vector. Teeth: pre-fix the client reads -1 / clean.
        static float ClientFluid(NetWorldClient c, ushort id, out byte type, out byte qual)
        {
            type = 0; qual = 0;
            if (!c.Inventories.TryGet(c.PlayerId, out var e)) return -999f;
            foreach (var page in e.Inventory.items)
                foreach (var jar in page.items)
                    if (jar.item != null && jar.item.id == id) { type = jar.item.fluidType; qual = jar.item.fluidQuality; return jar.item.fluidAmount; }
            return -999f;
        }

        [Test]
        public void fluid_container_contents_survive_replication()
        {
            var h = new TransactionalHarness(9099).Connected("a");
            var a = h.Clients[0];
            // an EMPTIED water bottle (drunk to 0) + a partially-full TAINTED canteen (type 2 = Water, quality 1 = Tainted)
            h.Grant(a.PlayerId, new Item(TransactionalFixtures.WaterBottleId) { fluidType = 2, fluidAmount = 0f, fluidQuality = 0 });
            h.Grant(a.PlayerId, new Item(TransactionalFixtures.CanteenId) { fluidType = 2, fluidAmount = 300f, fluidQuality = 1 });
            Assert.That(h.StepUntil(() => a.Inventories.TryGet(a.PlayerId, out var e)
                                       && e.Inventory.getItemCount(TransactionalFixtures.WaterBottleId) == 1
                                       && e.Inventory.getItemCount(TransactionalFixtures.CanteenId) == 1), Is.True,
                        $"both containers replicated to the owner (seed={h.Net.Seed})");

            // TEETH 1: the emptied bottle stays EMPTY on the client -- pre-fix it round-tripped to -1 (fresh) and refilled
            float bAmt = ClientFluid(a, TransactionalFixtures.WaterBottleId, out byte bType, out _);
            Assert.That(bAmt, Is.EqualTo(0f).Within(0.6f), "the emptied bottle stays empty over the wire (no refill exploit)");
            Assert.That(bType, Is.EqualTo(2), "the bottle's fluid type survived the wire");

            // TEETH 2: the tainted canteen keeps its AMOUNT and its QUALITY -- pre-fix quality reset to 0 (Clean) = free clean water
            float cAmt = ClientFluid(a, TransactionalFixtures.CanteenId, out _, out byte cQual);
            Assert.That(cAmt, Is.EqualTo(300f).Within(0.6f), "the tainted canteen's amount survived");
            Assert.That(cQual, Is.EqualTo(1), "the tainted canteen's QUALITY survived (Tainted stays Tainted)");

            OwnerParity(h, a);
        }

        [Test]
        public void fresh_fluid_container_keeps_its_uninitialized_sentinel()
        {
            var h = new TransactionalHarness(9100).Connected("a");
            var a = h.Clients[0];
            h.Grant(a.PlayerId, new Item(TransactionalFixtures.WaterBottleId));   // fresh -> fluidAmount -1 (lazy-init on first read)
            Assert.That(h.StepUntil(() => a.Inventories.TryGet(a.PlayerId, out var e)
                                       && e.Inventory.getItemCount(TransactionalFixtures.WaterBottleId) == 1), Is.True,
                        $"fresh bottle replicated (seed={h.Net.Seed})");
            // the -1 fresh sentinel must ROUND-TRIP faithfully (WriteClampedFloat is signed) -- else a fresh bottle would
            // read 0 = empty and never lazy-init to its full default. < 0 confirms the sentinel survived.
            float amt = ClientFluid(a, TransactionalFixtures.WaterBottleId, out _, out _);
            Assert.That(amt, Is.LessThan(-0.5f), "a fresh container's -1 sentinel survives the wire (still lazy-inits to full)");
            OwnerParity(h, a);
        }

        [Test]
        public void console_give_lands_in_the_server_grid_and_replicates()
        {
            var h = new TransactionalHarness(9081).Connected("a", "b");
            var a = h.Clients[0];
            var b = h.Clients[1];
            string result = null;
            a.ConsoleResult += e => result = e.Text;

            a.SendConsole("give 4");   // Eaglefire, 4x2 -> fits the 5x3 pockets
            Assert.That(h.StepUntil(() => a.Inventories.TryGet(a.PlayerId, out var mine)
                                       && mine.Inventory.getItemCount(TransactionalFixtures.RifleId) == 1), Is.True,
                        $"give applied on the server + replicated to the owner (seed={h.Net.Seed})");
            Assert.That(result, Does.Contain("gave"), "the console verdict came back as an event");
            Assert.That(b.Inventories.TryGet(a.PlayerId, out _), Is.False, "owner-only: A's grid never entered B's replica");
            OwnerParity(h, a);
        }

        [Test]
        public void legal_move_applies_illegal_move_rejected_grids_converge()
        {
            var h = new TransactionalHarness(9082).Connected("a");
            var a = h.Clients[0];
            h.Grant(a.PlayerId, new Item(TransactionalFixtures.BeansId));    // lands at pockets (0,0)
            h.Grant(a.PlayerId, new Item(TransactionalFixtures.ScrapId));    // lands at pockets (1,0)
            h.Step(10);

            a.SendMoveItem(2, 0, 0, 2, 3, 2, 0);   // legal: empty cell
            Assert.That(h.StepUntil(() => h.Server.Transactions.Diag.GridMovesApplied == 1), Is.True,
                        $"legal move applied (seed={h.Net.Seed})");
            h.Step(10);
            ulong afterLegal = OwnerParity(h, a);

            a.SendMoveItem(2, 3, 2, 5, 0, 0, 0);   // illegal: clothing page 5 is 0x0 (nothing worn)
            a.SendMoveItem(2, 4, 4, 2, 0, 0, 0);   // illegal: no item at (4,4)
            h.Step(20);
            Assert.That(h.Server.Transactions.Diag.GridMovesRejected, Is.EqualTo(2), "the grid math refused both");
            Assert.That(OwnerParity(h, a), Is.EqualTo(afterLegal), "rejected moves changed nothing");
        }

        [Test]
        public void equip_to_hand_slot()
        {
            var h = new TransactionalHarness(9083).Connected("a");
            var a = h.Clients[0];
            h.Grant(a.PlayerId, new Item(TransactionalFixtures.RifleId));
            h.Step(10);

            a.SendEquipItem(2, 0, 0, 0);   // pockets (0,0) -> PRIMARY slot
            Assert.That(h.StepUntil(() =>
            {
                if (!a.Inventories.TryGet(a.PlayerId, out var mine)) return false;
                return mine.Inventory.items[0].getItemCount() == 1;
            }), Is.True, $"equip replicated into the slot page (seed={h.Net.Seed})");
            OwnerParity(h, a);

            long rejected = h.Server.Commands.Diag.ValidationRejected;
            a.SendEquipItem(0, 0, 0, 5);   // slot 5 is not a hand slot
            h.Step(15);
            Assert.That(h.Server.Commands.Diag.ValidationRejected, Is.GreaterThan(rejected));
        }

        [Test]
        public void drop_then_pickup_roundtrips_through_the_world()
        {
            var h = new TransactionalHarness(9084).Connected("a", "b");
            var a = h.Clients[0];
            var b = h.Clients[1];
            h.Grant(a.PlayerId, new Item(TransactionalFixtures.BeansId));
            h.Step(10);

            a.SendDropItem(2, 0, 0);
            Assert.That(h.StepUntil(() => b.WorldItems.Count == 1), Is.True,
                        $"drop became a replicated world item on the OTHER client too (seed={h.Net.Seed})");
            uint dropId = 0;
            foreach (var wi in b.WorldItems.All) dropId = wi.NetIdValue;
            Assert.That(b.WorldItems.StateHash(), Is.EqualTo(h.Server.WorldItems.StateHash()), "world parity");
            h.Server.Inventories.TryGet(a.PlayerId, out var sInv);
            Assert.That(sInv.Inventory.getItemCount(TransactionalFixtures.BeansId), Is.EqualTo(0), "left the grid");

            a.SendPickupItem(dropId);
            Assert.That(h.StepUntil(() => a.WorldItems.Count == 0
                                       && a.Inventories.TryGet(a.PlayerId, out var mine)
                                       && mine.Inventory.getItemCount(TransactionalFixtures.BeansId) == 1), Is.True,
                        $"pickup removed the world item + returned the item to the grid (seed={h.Net.Seed})");
            OwnerParity(h, a);
        }

        [Test]
        public void pickup_out_of_reach_rejected_at_choke_point()
        {
            var h = new TransactionalHarness(9085).Connected("a");
            var a = h.Clients[0];
            var far = h.Server.Transactions.SpawnWorldItem(new Item(TransactionalFixtures.BeansId), new Vector3(50f, 0f, 50f), Vector3.zero);
            h.Step(5);
            long rejected = h.Server.Commands.Diag.ValidationRejected;

            a.SendPickupItem(far.NetIdValue);
            h.Step(20);

            Assert.That(h.Server.Commands.Diag.ValidationRejected, Is.GreaterThan(rejected), "50 m grab refused");
            Assert.That(h.Server.WorldItems.Count, Is.EqualTo(1), "the item stayed in the world");
        }

        [Test]
        public void pickup_behind_the_back_rejected()
        {
            var h = new TransactionalHarness(9090).Connected("a");
            var a = h.Clients[0];
            // player 1 spawns at (0,0,0) with yaw 0 -- facing -Z in the wire-yaw convention the REAL
            // server stamps onto entities (ServerDrive writes the avatar's Godot RotationDegrees.Y, and
            // Godot forward at yaw 0 is -Z). An item 3 m at +Z is squarely BEHIND: well inside the 6 m
            // PickupReach, far outside the facing cone -- reach alone would hoover it.
            var behind = h.Server.Transactions.SpawnWorldItem(new Item(TransactionalFixtures.BeansId), new Vector3(0f, 0f, 3f), Vector3.zero);
            h.Step(5);
            long rejected = h.Server.Commands.Diag.ValidationRejected;

            a.SendPickupItem(behind.NetIdValue);
            h.Step(20);

            Assert.That(h.Server.Commands.Diag.ValidationRejected, Is.GreaterThan(rejected),
                        "behind-the-back grab refused at the choke point (the facing cone -- reach alone allows it)");
            Assert.That(h.Server.WorldItems.Count, Is.EqualTo(1), "the item stayed in the world");
        }

        [Test]
        public void pickup_at_feet_allowed_regardless_of_yaw()
        {
            var h = new TransactionalHarness(9091).Connected("a");
            var a = h.Clients[0];
            // 0.5 m at +Z = directly behind a yaw-0 player, but INSIDE the at-feet skip range: the bearing
            // of an item at your feet is unstable (and SP picks it up via the eye ray), so the cone must
            // not apply -- the pickup lands.
            var atFeet = h.Server.Transactions.SpawnWorldItem(new Item(TransactionalFixtures.BeansId), new Vector3(0f, 0f, 0.5f), Vector3.zero);
            h.Step(5);

            a.SendPickupItem(atFeet.NetIdValue);
            Assert.That(h.StepUntil(() => h.Server.WorldItems.Count == 0
                                       && a.Inventories.TryGet(a.PlayerId, out var mine)
                                       && mine.Inventory.getItemCount(TransactionalFixtures.BeansId) == 1), Is.True,
                        $"at-feet pickup landed regardless of facing (seed={h.Net.Seed})");
        }

        [Test]
        public void pickup_into_a_full_grid_is_denied_but_stays()
        {
            var h = new TransactionalHarness(9086).Connected("a");
            var a = h.Clients[0];
            h.Server.Inventories.TryGet(a.PlayerId, out var sInv);
            for (int i = 0; i < 15; i++)   // pockets are 5x3 and nothing is worn -> 15 cells, all filled
                Assert.That(sInv.Inventory.tryAddItem(new Item(TransactionalFixtures.ScrapId)), Is.True);
            var drop = h.Server.Transactions.SpawnWorldItem(new Item(TransactionalFixtures.BeansId), new Vector3(0.5f, 0f, 0.5f), Vector3.zero);
            h.Step(10);

            bool denied = false;
            a.ItemPickupDenied += e => denied = e.NetId == drop.NetIdValue;
            a.SendPickupItem(drop.NetIdValue);
            Assert.That(h.StepUntil(() => denied), Is.True, $"ItemPickupDenied reached the requester (seed={h.Net.Seed})");
            Assert.That(h.Server.WorldItems.Count, Is.EqualTo(1), "the item stayed in the world");
            Assert.That(h.Server.Transactions.Diag.PickupsDenied, Is.EqualTo(1));
        }

        [Test]
        public void craft_applies_and_the_skill_gate_holds()
        {
            var h = new TransactionalHarness(9087).Connected("a");
            var a = h.Clients[0];
            h.Grant(a.PlayerId, new Item(TransactionalFixtures.LogId));
            h.Grant(a.PlayerId, new Item(TransactionalFixtures.LogId));
            h.Step(10);

            a.SendCraft(1);   // the CRAFTING-level-1 gated blueprint -- level 0 -> refused
            h.Step(20);
            Assert.That(h.Server.Transactions.Diag.CraftsRejected, Is.EqualTo(1), "skill gate held");

            a.SendConsole("skill crafting 1");
            h.Step(20);
            a.SendCraft(1);
            Assert.That(h.StepUntil(() => a.Inventories.TryGet(a.PlayerId, out var mine)
                                       && mine.Inventory.getItemCount(TransactionalFixtures.PlankId) == 1), Is.True,
                        $"gated craft applied once the skill leveled (seed={h.Net.Seed})");
            h.Server.Inventories.TryGet(a.PlayerId, out var sInv);
            Assert.That(sInv.Inventory.getItemCount(TransactionalFixtures.LogId), Is.EqualTo(0), "supplies consumed");
            OwnerParity(h, a);

            a.SendCraft(0);   // supplies now gone -> the open blueprint refuses too
            h.Step(20);
            Assert.That(h.Server.Transactions.Diag.CraftsRejected, Is.EqualTo(2), "no supplies, no craft");
        }

        [Test]
        public void consume_heals_the_server_combat_state()
        {
            var h = new TransactionalHarness(9088).Connected("a");
            var a = h.Clients[0];
            h.Grant(a.PlayerId, new Item(TransactionalFixtures.BeansId));
            h.Server.CombatState.TryGet(a.PlayerId, out var ce);
            ce.HealthExact = 50f;
            ce.Health = 50;
            h.Server.CombatState.MarkDirty(ce, h.Server.Session.CurrentTick);
            h.Step(10);

            a.SendConsume(2, 0, 0);
            Assert.That(h.StepUntil(() => ce.HealthExact == 60f), Is.True,
                        $"beans healed 10 on the server (seed={h.Net.Seed})");
            h.Server.Inventories.TryGet(a.PlayerId, out var sInv);
            Assert.That(sInv.Inventory.getItemCount(TransactionalFixtures.BeansId), Is.EqualTo(0), "eaten");
            Assert.That(h.Server.Transactions.Diag.ConsumesApplied, Is.EqualTo(1));

            a.SendConsume(2, 0, 0);   // nothing there anymore
            h.Step(15);
            Assert.That(h.Server.Transactions.Diag.ConsumesRejected, Is.EqualTo(1));
        }

        [Test]
        public void storage_crate_arbitration_and_transfer()
        {
            var h = new TransactionalHarness(9089).Connected("a", "b");
            var a = h.Clients[0];
            var b = h.Clients[1];
            // a crate near both spawns (player 1 at x 0, player 2 at x 2), seeded with one scrap
            var crate = h.Server.Inventories.ServerRegisterCrate(h.Server.Ids.Mint(), 5, 4, new Vector3(1f, 0f, 0f));
            crate.Storage.tryAddItem(new Item(TransactionalFixtures.ScrapId));
            uint crateId = crate.NetIdValue;
            h.Step(5);

            bool aOpened = false;
            a.StorageOpened += e => aOpened = e.NetId == crateId;
            a.SendOpenStorage(crateId);
            Assert.That(h.StepUntil(() => aOpened
                                       && a.Inventories.TryGet(a.PlayerId, out var mine)
                                       && mine.Inventory.items[PlayerInventory.STORAGE].getItemCount() == 1), Is.True,
                        $"open loaded the crate into the owner's STORAGE page (seed={h.Net.Seed})");
            OwnerParity(h, a);

            bool bOpened = false;
            b.StorageOpened += e => bOpened = true;
            b.SendOpenStorage(crateId);
            h.Step(20);
            Assert.That(bOpened, Is.False, "one opener at a time -- B was refused while A holds it open");
            Assert.That(crate.OpenBy, Is.EqualTo(a.PlayerId));

            a.SendMoveItem(PlayerInventory.STORAGE, 0, 0, 2, 0, 0, 0);   // crate -> pockets
            bool aClosed = false;
            a.StorageClosed += e => aClosed = true;
            a.SendCloseStorage();
            Assert.That(h.StepUntil(() => aClosed), Is.True, $"close acked (seed={h.Net.Seed})");
            Assert.That(crate.Storage.getItemCount(), Is.EqualTo(0), "the taken item saved OUT of the crate");
            Assert.That(crate.OpenBy, Is.EqualTo(0), "arbitration released");
            h.Server.Inventories.TryGet(a.PlayerId, out var sInv);
            Assert.That(sInv.Inventory.getItemCount(TransactionalFixtures.ScrapId), Is.EqualTo(1), "item kept");
            Assert.That(sInv.Inventory.items[PlayerInventory.STORAGE].width, Is.EqualTo((byte)0), "view cleared");
            h.Step(10);
            OwnerParity(h, a);

            b.SendOpenStorage(crateId);
            Assert.That(h.StepUntil(() => bOpened), Is.True, $"B opens once A released it (seed={h.Net.Seed})");
        }

        [Test]
        public void owner_block_survives_lossy_links()
        {
            var loss = new SDG.NetTransport.Mem.FaultyLinkConfig { LossProbability = 0.25, DuplicateProbability = 0.05, LatencyTicks = 1, ReorderJitterTicks = 4 };
            var h = new TransactionalHarness(778899, loss, loss).Connected("a");
            var a = h.Clients[0];
            h.Grant(a.PlayerId, new Item(TransactionalFixtures.BeansId));
            h.Grant(a.PlayerId, new Item(TransactionalFixtures.RifleId));

            a.SendMoveItem(2, 0, 0, 2, 4, 2, 0);   // reliable command under 25% loss
            Assert.That(h.StepUntil(() => h.Server.Transactions.Diag.GridMovesApplied == 1, 800), Is.True,
                        $"the move landed through the loss (seed={h.Net.Seed})");
            h.Step(120);   // let the owner block win through the lossy snap stream
            OwnerParity(h, a);
        }
    }
}
