using System.Linq;
using NUnit.Framework;
using SDG.Unturned;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    /// <summary>Three server-authority holes found by an astra code review on 2026-09-07 and verified against
    /// the source before being fixed. Each test fails on the pre-fix server.
    ///
    /// All three are the same species: a validate step that checks everything EXCEPT the one thing that makes
    /// the request legitimate. None of them is reachable by a normal client -- they are what a hand-written
    /// packet gets to do, which is the only kind of bug worth having a server-side test for at all.</summary>
    [TestFixture]
    public class AstraReviewFixTests
    {
        const ushort PackId = 8801;

        [SetUp]
        public void SetUp()
        {
            TransactionalFixtures.RegisterAssets();   // clears the catalog -- the suite-local pack goes in after
            Assets.add(new ItemAsset { id = PackId, itemName = "Fixture Pack", size_x = 2, size_y = 2, type = EItemType.BACKPACK, width = 4, height = 3 });
        }

        static ItemJar Find(NetWorldServer s, ushort pid, ushort id)
        {
            var inv = s.Transactions.InventoryForTest(pid);
            for (byte p = 0; p < PlayerInventory.OWNPAGES; p++)
                for (byte i = 0; i < inv.items[p].getItemCount(); i++)
                {
                    var j = inv.items[p].getItem(i);
                    if (j?.item != null && j.item.id == id) return j;
                }
            return null;
        }

        static int CountOf(NetWorldServer s, ushort pid, ushort id)
        {
            var inv = s.Transactions.InventoryForTest(pid);
            int n = 0;
            for (byte p = 0; p < PlayerInventory.OWNPAGES; p++)
                for (byte i = 0; i < inv.items[p].getItemCount(); i++)
                {
                    var j = inv.items[p].getItem(i);
                    if (j?.item != null && j.item.id == id) n++;
                }
            return n;
        }

        // ---------------------------------------------------------------- 1. the magazine printer

        /// <summary>Unloading must produce LOOSE AMMUNITION. The guard used to be "the output's magRound matches
        /// the cartridge in the magazine", and a magazine declares the cartridge it ACCEPTS in that same field --
        /// so naming the magazine itself as the output passed, and the server built one. One round spent, one
        /// magazine body created, repeat: an item printer wearing an unload's clothes.</summary>
        [Test]
        public void unloading_a_magazine_into_a_magazine_is_refused()
        {
            var h = new TransactionalHarness(9701).Connected("a");
            var a = h.Clients[0];
            h.Grant(a.PlayerId, new Item(TransactionalFixtures.StanagId) { amount = 5, magLoadedRound = "556" });
            h.Step(10);

            var mag = Find(h.Server, a.PlayerId, TransactionalFixtures.StanagId);
            Assert.That(mag, Is.Not.Null, "fixture: the loaded magazine is on the server");
            Assert.That(CountOf(h.Server, a.PlayerId, TransactionalFixtures.StanagId), Is.EqualTo(1), "fixture: exactly one magazine to start");
            long rejectedBefore = h.Server.Transactions.Diag.MagLoadsRejected;

            // Unload, naming the MAGAZINE as the thing to receive the round. Its magRound is "556", the same
            // cartridge the magazine holds, so the old caliber check waved this through.
            a.SendMagLoad(2, mag.x, mag.y, TransactionalFixtures.StanagId,
                          0, 0, 0, TransactionalFixtures.StanagId, true);
            h.Step(12);

            Assert.That(h.Server.Transactions.Diag.MagLoadsApplied, Is.EqualTo(0), "the server applied no unload");
            Assert.That(h.Server.Transactions.Diag.MagLoadsRejected, Is.GreaterThan(rejectedBefore), "...and counted it as rejected");
            Assert.That(CountOf(h.Server, a.PlayerId, TransactionalFixtures.StanagId), Is.EqualTo(1),
                        "NO second magazine was minted");
            Assert.That(mag.item.amount, Is.EqualTo(5), "and the original kept all five rounds");
        }

        /// <summary>The control: a REAL unload, into loose ammunition, still works. Without this the test above
        /// passes just as well against a server that refuses every unload, which would be a different bug.</summary>
        [Test]
        public void unloading_into_loose_ammunition_still_works()
        {
            var h = new TransactionalHarness(9702).Connected("a");
            var a = h.Clients[0];
            h.Grant(a.PlayerId, new Item(TransactionalFixtures.StanagId) { amount = 5, magLoadedRound = "556" });
            h.Step(10);
            var mag = Find(h.Server, a.PlayerId, TransactionalFixtures.StanagId);

            a.SendMagLoad(2, mag.x, mag.y, TransactionalFixtures.StanagId,
                          0, 0, 0, TransactionalFixtures.Round556Id, true);
            Assert.That(h.StepUntil(() => h.Server.Transactions.Diag.MagLoadsApplied == 1), Is.True,
                        $"the server applied the legitimate unload (seed={h.Net.Seed})");
            Assert.That(mag.item.amount, Is.EqualTo(4), "the magazine lost a round");
            Assert.That(Find(h.Server, a.PlayerId, TransactionalFixtures.Round556Id), Is.Not.Null, "and a loose round came back");
        }

        // ---------------------------------------------------------------- 2. looting your own corpse

        /// <summary>A dead player is still a peer with an inventory for the whole respawn delay, and his death
        /// drops land close enough to the body that the facing check is skipped. Pickup validated position,
        /// inventory, reach and facing -- never liveness -- so a corpse could pick its own kit back up before
        /// respawning, voiding the death penalty and taking the loot away from whoever earned it.</summary>
        [Test]
        public void a_dead_player_cannot_pick_his_death_drops_back_up()
        {
            var h = new TransactionalHarness(9703).Connected("victim");
            var victim = h.Clients[0];
            h.Grant(victim.PlayerId, new Item(TransactionalFixtures.BeansId));
            h.Step(10);
            Assert.That(CountOf(h.Server, victim.PlayerId, TransactionalFixtures.BeansId), Is.EqualTo(1), "fixture: carrying the beans");

            h.Server.Combat.DamagePlayerExternal(victim.PlayerId, 1000f);
            Assert.That(h.StepUntil(() => !h.Server.CombatState.IsAlive(victim.PlayerId)), Is.True, "the victim died");
            Assert.That(h.StepUntil(() => h.Server.WorldItems.All.Any()), Is.True, "the death drop reached the world");

            var drop = h.Server.WorldItems.All.First();
            Assert.That(CountOf(h.Server, victim.PlayerId, TransactionalFixtures.BeansId), Is.EqualTo(0),
                        "fixture: death emptied the bag, so a successful pickup is visible as a 1");

            victim.SendPickupItem(drop.NetIdValue);
            h.Step(15);

            Assert.That(h.Server.CombatState.IsAlive(victim.PlayerId), Is.False, "still dead (the respawn delay outlasts this)");
            Assert.That(CountOf(h.Server, victim.PlayerId, TransactionalFixtures.BeansId), Is.EqualTo(0),
                        "the corpse did NOT get its beans back");
            Assert.That(h.Server.WorldItems.All.Any(w => w.NetIdValue == drop.NetIdValue), Is.True,
                        "and the drop is still on the ground for whoever earned it");
        }

        // ---------------------------------------------------------------- 3. the bag that eats its contents

        /// <summary>Taking a bag off resizes its page to 0x0 and Items.loadSize drops every jar that no longer
        /// fits, so the server destroyed a backpack's contents on unwear. Singleplayer has spilled them out
        /// first since 2026-09-03 (InventoryUI.UnwearTo); the network path returned before that logic, so the
        /// bug existed only in multiplayer -- the same SP/MP seam this codebase keeps closing.</summary>
        [Test]
        public void unwearing_a_bag_keeps_what_was_inside_it()
        {
            var h = new TransactionalHarness(9704).Connected("a");
            var a = h.Clients[0];
            h.Grant(a.PlayerId, new Item(PackId));
            h.Step(10);

            var packJar = Find(h.Server, a.PlayerId, PackId);
            Assert.That(packJar, Is.Not.Null, "fixture: the pack is in the grid");
            a.SendWearClothing(2, packJar.x, packJar.y, (byte)EItemType.BACKPACK);
            Assert.That(h.StepUntil(() => h.Server.Transactions.InventoryForTest(a.PlayerId).wornBackpack != null), Is.True,
                        $"the pack went on (seed={h.Net.Seed})");

            // Two items INSIDE the bag. Placed on the BACKPACK page explicitly rather than granted: Grant walks
            // the pages and drops them in the first pocket that fits, which is not the bag -- a first pass at
            // this test did that and passed against the unfixed server, because nothing was ever in the bag to
            // lose. The whole bug is about the contents of THAT page.
            var srvInv = h.Server.Transactions.InventoryForTest(a.PlayerId);
            var bagPage = srvInv.items[PlayerInventory.BACKPACK];
            Assert.That(bagPage.width * bagPage.height, Is.GreaterThan(0), "fixture: wearing the pack sized its page");
            Assert.That(bagPage.tryAddItem(new Item(TransactionalFixtures.ScrapId, 3)), Is.True, "fixture: scrap went into the bag");
            Assert.That(bagPage.tryAddItem(new Item(TransactionalFixtures.BeansId)), Is.True, "fixture: beans went into the bag");
            Assert.That(bagPage.getItemCount(), Is.EqualTo(2), "fixture: two items are IN THE BAG, not in a pocket");
            h.Step(10);
            int scrapBefore = CountOf(h.Server, a.PlayerId, TransactionalFixtures.ScrapId);
            int beansBefore = CountOf(h.Server, a.PlayerId, TransactionalFixtures.BeansId);
            Assert.That(scrapBefore + beansBefore, Is.EqualTo(2), "fixture: both items are carried before the unwear");

            a.SendUnwearClothing((byte)EItemType.BACKPACK);
            Assert.That(h.StepUntil(() => h.Server.Transactions.InventoryForTest(a.PlayerId).wornBackpack == null), Is.True,
                        $"the pack came off (seed={h.Net.Seed})");
            h.Step(10);

            // The contents survive: in the grid if they fit, on the ground if they do not. What must NOT happen
            // is that they cease to exist, which is what the pre-fix server did to them.
            int scrapAfter = CountOf(h.Server, a.PlayerId, TransactionalFixtures.ScrapId)
                           + h.Server.WorldItems.All.Count(w => w.ItemId == TransactionalFixtures.ScrapId);
            int beansAfter = CountOf(h.Server, a.PlayerId, TransactionalFixtures.BeansId)
                           + h.Server.WorldItems.All.Count(w => w.ItemId == TransactionalFixtures.BeansId);
            Assert.That(scrapAfter, Is.EqualTo(scrapBefore), "the scrap still exists somewhere (grid or ground)");
            Assert.That(beansAfter, Is.EqualTo(beansBefore), "and so do the beans");
            Assert.That(CountOf(h.Server, a.PlayerId, PackId), Is.EqualTo(1), "and the pack itself came back to the grid");
        }
    }
}
