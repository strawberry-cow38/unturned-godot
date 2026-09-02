using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SDG.Unturned;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // DEATH DROPS EVERYTHING (strawberry 2026-09-02: "your items are kept after death instead of dropping on
    // the ground"). Until this landed, ServerCombat's one death path set Health 0 / Deaths++ / RespawnAtTick and
    // broadcast PlayerDied -- and never touched the inventory, so a corpse stood back up with its bag.
    //
    // THE TRAP THIS SUITE AVOIDS: "the inventory is empty after death" passes if the items were DELETED. Every
    // test here asserts the items EXIST ON THE GROUND -- as WorldItemReplication entities on the server AND as
    // replicas on ANOTHER client's WorldItems -- with their identity and state intact, and that the count
    // conserved: what left the bag is exactly what hit the ground.
    [TestFixture]
    public class DeathDropTests
    {
        [SetUp]
        public void SetUp()
        {
            TransactionalFixtures.RegisterAssets();   // clears the catalog first -- so the suite-local clothing goes in AFTER it
            Assets.add(new ItemAsset { id = PackId, itemName = "Fixture Pack", size_x = 2, size_y = 2, type = EItemType.BACKPACK, width = 4, height = 3 });
            Assets.add(new ItemAsset { id = ShirtId, itemName = "Fixture Shirt", size_x = 2, size_y = 2, type = EItemType.SHIRT, width = 0, height = 0 });
        }

        static int Carried(PlayerInventory inv)
        {
            int n = 0;
            for (byte p = 0; p < PlayerInventory.STORAGE; p++) n += inv.items[p].getItemCount();
            foreach (var w in new[] { inv.wornHat, inv.wornGlasses, inv.wornMask, inv.wornShirt, inv.wornVest, inv.wornBackpack, inv.wornPants })
                if (w != null) n++;
            return n;
        }

        static void Kill(TransactionalHarness h, NetWorldClient victim)
        {
            h.Server.Combat.DamagePlayerExternal(victim.PlayerId, 1000f);
            Assert.That(h.StepUntil(() => !h.Server.CombatState.IsAlive(victim.PlayerId)), Is.True, "the victim died");
        }

        [Test]
        public void death_puts_every_carried_item_on_the_ground_where_another_client_can_see_it()
        {
            var h = new TransactionalHarness(seed: 9601).Connected("victim", "witness");
            var victim = h.Clients[0];
            var witness = h.Clients[1];

            // a rifle with STATE (ammo, quality) in the pockets, plus a stack and a single -- three items whose
            // identity + state must come out the other side unchanged
            var rifle = new Item(TransactionalFixtures.RifleId) { quality = 37, gunAmmo = 12 };
            h.Grant(victim.PlayerId, rifle);
            h.Grant(victim.PlayerId, new Item(TransactionalFixtures.ScrapId, 5));
            h.Grant(victim.PlayerId, new Item(TransactionalFixtures.BeansId));
            var serverInv = h.Server.Transactions.InventoryForTest(victim.PlayerId);
            int carried = Carried(serverInv);
            Assert.That(carried, Is.EqualTo(3), "fixture: three items in the bag before death");
            Assert.That(h.StepUntil(() => victim.Inventories.TryGet(victim.PlayerId, out var e) && Carried(e.Inventory) == 3), Is.True,
                        "the owner saw its bag fill before dying (so the post-death echo is a real change)");

            // die somewhere specific so "landed at the death spot" is falsifiable
            var deathSpot = new Vector3(120f, 3f, -80f);
            h.Server.Players.ServerTeleport(victim.PlayerId, deathSpot, h.Server.Session.CurrentTick);
            int groundBefore = h.Server.WorldItems.Count;
            Kill(h, victim);

            // (1) the SERVER: the bag is empty AND the ground holds exactly what was in it
            Assert.That(Carried(serverInv), Is.EqualTo(0), "the server grid is empty after death");
            Assert.That(h.Server.WorldItems.Count - groundBefore, Is.EqualTo(carried),
                        "every carried item became a world item -- no more, no fewer (nothing deleted, nothing duplicated)");
            var dropped = h.Server.WorldItems.All.Where(e => (e.Pos - deathSpot).magnitude < 2f).ToList();
            Assert.That(dropped.Count, Is.EqualTo(carried), $"all {carried} landed within 2 m of the death spot");
            var ids = dropped.Select(e => e.ItemId).OrderBy(i => i).ToArray();
            Assert.That(ids, Is.EqualTo(new ushort[] { TransactionalFixtures.RifleId, TransactionalFixtures.BeansId, TransactionalFixtures.ScrapId }.OrderBy(i => i).ToArray()),
                        "the ground holds the rifle, the beans and the scrap");
            var groundRifle = dropped.First(e => e.ItemId == TransactionalFixtures.RifleId);
            Assert.That(groundRifle.Quality, Is.EqualTo(37), "the rifle kept its quality on the way down");
            Assert.That(ReferenceEquals(groundRifle.ServerItem, rifle), Is.True, "the SAME Item object moved from the grid to the ground (ammo/attachments ride with it)");
            Assert.That(groundRifle.ServerItem.gunAmmo, Is.EqualTo(12), "...with its 12 loaded rounds");
            Assert.That(dropped.First(e => e.ItemId == TransactionalFixtures.ScrapId).Amount, Is.EqualTo(5), "the scrap stack kept its 5");
            Assert.That(h.Server.Transactions.Diag.DeathDrops, Is.EqualTo(1));
            Assert.That(h.Server.Transactions.Diag.DeathDropItems, Is.EqualTo(carried));

            // (2) the WITNESS: another client sees the same three items on the ground -- the broadcast facts
            // reached everyone, not just the victim
            Assert.That(h.StepUntil(() => witness.WorldItems.Count == groundBefore + carried), Is.True,
                        $"the witness's world-item replica grew by {carried} (has {witness.WorldItems.Count}, seed={h.Net.Seed})");
            foreach (var e in dropped)
            {
                Assert.That(witness.WorldItems.TryGet(e.NetIdValue, out var w), Is.True, $"the witness has world item {e.NetIdValue}");
                Assert.That(w.ItemId, Is.EqualTo(e.ItemId));
                Assert.That(w.Amount, Is.EqualTo(e.Amount));
                Assert.That(w.Quality, Is.EqualTo(e.Quality));
                Assert.That((w.Pos - e.Pos).magnitude, Is.LessThan(0.05f), "at the server's spot");
            }

            // (3) the VICTIM: its own replica shows the empty bag (the owner echo carried the loss)
            Assert.That(h.StepUntil(() => victim.Inventories.TryGet(victim.PlayerId, out var e) && Carried(e.Inventory) == 0), Is.True,
                        "the victim's owner-block replica emptied");
            Assert.That(victim.Inventories.StateHash(), Is.EqualTo(h.Server.Inventories.StateHashFor(victim.PlayerId)), "owner-block parity after the drop");

            // (4) and the witness can PICK ONE UP -- the drop is a real, ordinary world item
            h.Server.Players.ServerTeleport(witness.PlayerId, deathSpot, h.Server.Session.CurrentTick);
            var beans = dropped.First(e => e.ItemId == TransactionalFixtures.BeansId);
            h.Step(2);
            witness.SendPickupItem(beans.NetIdValue);
            Assert.That(h.StepUntil(() => h.Server.Transactions.InventoryForTest(witness.PlayerId).getItemCount(TransactionalFixtures.BeansId) == 1), Is.True,
                        "the witness picked the dead player's beans up off the ground");
            Assert.That(h.Server.WorldItems.TryGet(beans.NetIdValue, out _), Is.False, "...and the ground entity retired");
        }

        [Test]
        public void death_drops_the_worn_clothing_too_but_only_after_emptying_its_pockets()
        {
            // The ordering trap: unwearing a bag resizes its page to 0x0 and DISCARDS its contents. The drop must
            // empty the backpack page BEFORE taking the backpack off, or the backpack lands and what was in it
            // vanishes. Assert BOTH the pack and its contents hit the ground.
            var h = new TransactionalHarness(seed: 9602).Connected("victim", "witness");
            var victim = h.Clients[0];
            var witness = h.Clients[1];
            var serverInv = h.Server.Transactions.InventoryForTest(victim.PlayerId);
            serverInv.wearBackpack(new Item(PackId));
            Assert.That(serverInv.items[PlayerInventory.BACKPACK].tryAddItem(new Item(TransactionalFixtures.ScrapId, 3)), Is.True, "fixture: scrap in the pack");
            Assert.That(serverInv.items[PlayerInventory.BACKPACK].tryAddItem(new Item(TransactionalFixtures.BeansId)), Is.True, "fixture: beans in the pack");
            serverInv.wearShirt(new Item(ShirtId));
            h.Server.Inventories.ServerMarkDirty(victim.PlayerId);
            int carried = Carried(serverInv);
            Assert.That(carried, Is.EqualTo(4), "fixture: pack + shirt worn, two items inside the pack");

            var deathSpot = new Vector3(-40f, 0f, 55f);
            h.Server.Players.ServerTeleport(victim.PlayerId, deathSpot, h.Server.Session.CurrentTick);
            int groundBefore = h.Server.WorldItems.Count;
            Kill(h, victim);

            Assert.That(serverInv.wornBackpack, Is.Null, "the pack came off");
            Assert.That(serverInv.wornShirt, Is.Null, "the shirt came off");
            Assert.That(Carried(serverInv), Is.EqualTo(0));
            var dropped = h.Server.WorldItems.All.Where(e => (e.Pos - deathSpot).magnitude < 2f).Select(e => e.ItemId).OrderBy(i => i).ToArray();
            Assert.That(dropped, Is.EqualTo(new ushort[] { PackId, ShirtId, TransactionalFixtures.ScrapId, TransactionalFixtures.BeansId }.OrderBy(i => i).ToArray()),
                        "the pack, the shirt, AND the pack's contents are all on the ground (nothing discarded by the resize)");
            Assert.That(h.Server.WorldItems.Count - groundBefore, Is.EqualTo(carried), "count conserved");
            Assert.That(h.StepUntil(() => witness.WorldItems.Count == groundBefore + carried), Is.True, "the witness sees all four");
            Assert.That(h.StepUntil(() => victim.Inventories.TryGet(victim.PlayerId, out var e) && e.Inventory.wornBackpack == null && e.Inventory.wornShirt == null), Is.True,
                        "the victim's replica shows the clothes gone (worn slots ride the owner block)");
        }

        [Test]
        public void death_does_not_drop_the_contents_of_a_crate_the_victim_had_open()
        {
            // The STORAGE page (7) is a VIEW of an open crate, not the player's property. Dropping it would
            // teleport the crate's contents onto the corpse; and a dead opener would hold the one-opener lock
            // until they disconnected. Death closes the crate (saving the page back) and leaves it alone.
            var h = new TransactionalHarness(seed: 9603).Connected("victim", "witness");
            var victim = h.Clients[0];
            var witness = h.Clients[1];
            var crateAt = new Vector3(10f, 0f, 10f);
            var crate = h.Server.Inventories.ServerRegisterCrate(h.Server.Ids.Mint(), 4, 3, crateAt);
            Assert.That(crate.Storage.tryAddItem(new Item(TransactionalFixtures.LogId)), Is.True);
            Assert.That(crate.Storage.tryAddItem(new Item(TransactionalFixtures.PlankId)), Is.True);
            h.Server.Players.ServerTeleport(victim.PlayerId, crateAt, h.Server.Session.CurrentTick);
            h.Step(2);
            victim.SendOpenStorage(crate.NetIdValue);
            Assert.That(h.StepUntil(() => h.Server.Inventories.TryGet(victim.PlayerId, out var e) && e.OpenCrateId == crate.NetIdValue), Is.True, "the victim opened the crate");
            var serverInv = h.Server.Transactions.InventoryForTest(victim.PlayerId);
            Assert.That(serverInv.items[PlayerInventory.STORAGE].getItemCount(), Is.EqualTo(2), "fixture: the crate's two items are in the STORAGE view");
            h.Grant(victim.PlayerId, new Item(TransactionalFixtures.BeansId));   // one thing that IS theirs

            int groundBefore = h.Server.WorldItems.Count;
            Kill(h, victim);

            Assert.That(h.Server.WorldItems.Count - groundBefore, Is.EqualTo(1), "only the beans dropped -- the crate's log and plank did not");
            Assert.That(h.Server.WorldItems.All.Any(e => e.ItemId == TransactionalFixtures.LogId || e.ItemId == TransactionalFixtures.PlankId), Is.False);
            Assert.That(crate.Storage.getItemCount(), Is.EqualTo(2), "the crate still holds both (the view was saved back on close)");
            Assert.That(crate.OpenBy, Is.EqualTo(0), "the corpse released the crate lock");
            Assert.That(h.Server.Inventories.TryGet(victim.PlayerId, out var after) && after.OpenCrateId == 0, Is.True, "the victim no longer has it open");
            // ...so the witness can open it -- the lock really was released, not just the field cleared
            h.Server.Players.ServerTeleport(witness.PlayerId, crateAt, h.Server.Session.CurrentTick);
            h.Step(2);
            witness.SendOpenStorage(crate.NetIdValue);
            Assert.That(h.StepUntil(() => h.Server.Inventories.TryGet(witness.PlayerId, out var w) && w.OpenCrateId == crate.NetIdValue), Is.True,
                        "the witness could open the crate the dead player had been using");
        }

        [Test]
        public void a_player_with_nothing_drops_nothing_and_still_dies_and_respawns()
        {
            // The seam must be inert for an empty bag: no phantom entities, no broken respawn clock.
            var h = new TransactionalHarness(seed: 9604).Connected("victim");
            var victim = h.Clients[0];
            int groundBefore = h.Server.WorldItems.Count;
            Kill(h, victim);
            Assert.That(h.Server.WorldItems.Count, Is.EqualTo(groundBefore), "nothing appeared on the ground");
            Assert.That(h.Server.Transactions.Diag.DeathDrops, Is.EqualTo(1), "the drop ran (and found nothing)");
            Assert.That(h.Server.Transactions.Diag.DeathDropItems, Is.EqualTo(0));
            Assert.That(h.StepUntil(() => h.Server.CombatState.IsAlive(victim.PlayerId), maxTicks: 400), Is.True, "and the respawn clock still fired");
        }

        // clothing fixtures local to this suite: a 4x3 backpack (a real bag page) and a 0x0 shirt
        const ushort PackId = 9301;
        const ushort ShirtId = 9302;
    }
}
