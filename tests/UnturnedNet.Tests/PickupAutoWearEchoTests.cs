using NUnit.Framework;
using SDG.Unturned;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    /// <summary>Picking a hat up off the ground auto-wears it, and the OWNER HAS TO BE TOLD without having to
    /// poke their bag first.
    ///
    /// master 2026-10-05: "see if we can finally fix the hat equip bug... it doesnt show or apply on my character
    /// until i update my inv. (by moving an item in my inv)", and on how it happens: "picking up from ground
    /// which auto equips it".
    ///
    /// ⭐⭐ THE DEFECT IS A MISSING DIRTY FLAG, NOT A MISSING REPAINT. PlayerInventory.tryAddItemAuto can land a
    /// pickup three ways. Grid calls tryAddItem and Slot calls equipToSlot, and both touch a page, which raises
    /// Items.onStateUpdated, which is the ONLY thing that marks the owner's inventory dirty. Worn -- a clothing
    /// item whose slot is empty -- is `wornHat = item` and nothing else. So the server dressed the player and
    /// then wrote an EMPTY owner block on every tick afterwards, and the hat stayed invisible until an unrelated
    /// grid edit dirtied the entry. "Moving an item in my inventory" was never a fix; it was the next thing that
    /// happened to set the flag.
    ///
    /// ⚠ AND IT IS WHY TWO EARLIER FIXES DID NOT HOLD. 2026-09-07 added PlayerClothingController.ReconcileTick
    /// and 2026-09-13 added the worn slots to InventoryUI's poll hash -- both correct, both still working, both
    /// watching the client's worn slots for a change the server was not sending. A client-side poll cannot see
    /// data that never arrives, so the third attempt had to be a test on the WIRE rather than a better poll.
    ///
    /// The teeth are in step 2: step 1 (the server wore it) passes with the bug present and on its own would
    /// have shipped the same green-while-broken result as last time.</summary>
    [TestFixture]
    public class PickupAutoWearEchoTests
    {
        const ushort HatId = 27;        // a Tophat: HAT, so an empty head makes this AutoPlace.Worn
        const ushort GenericId = TransactionalFixtures.BeansId;   // FOOD -> AutoPlace.Grid
        const ushort GunId = TransactionalFixtures.RifleId;       // GUN, PRIMARY -> AutoPlace.Slot

        [SetUp]
        public void SetUp()
        {
            TransactionalFixtures.RegisterAssets();
            Assets.add(new ItemAsset { id = HatId, itemName = "Tophat", size_x = 2, size_y = 2, type = EItemType.HAT });
        }

        static bool ClientWornHat(NetWorldClient client, ushort pid, out ushort id)
        {
            id = 0;
            if (!client.Inventories.TryGet(pid, out var ce)) return false;
            id = ce.Inventory.wornHat?.id ?? 0;
            return id != 0;
        }

        [Test]
        public void picking_a_hat_off_the_ground_reaches_the_owner_with_no_inventory_poke()
        {
            var h = new TransactionalHarness(4913).Connected("a");
            var a = h.Clients[0];
            h.Step(10);

            Assert.That(h.Server.Inventories.TryGet(a.PlayerId, out var se), Is.True);
            Assert.That(se.Inventory.wornHat, Is.Null, "bare-headed to start, or AutoPlace.Worn is not the branch under test");

            var drop = h.Server.Transactions.SpawnWorldItem(new Item(HatId), new Vector3(0.5f, 0f, 0.5f), Vector3.zero);
            h.Step(5);
            a.SendPickupItem(drop.NetIdValue);

            // STEP 1 -- passes WITH the bug. The server always wore it correctly; that was never the problem,
            // and asserting only this is how the previous two attempts stayed green over a broken game.
            Assert.That(h.StepUntil(() => se.Inventory.wornHat != null), Is.True,
                        $"the SERVER auto-wore the hat (seed={h.Net.Seed})");
            Assert.That(se.Inventory.wornHat.id, Is.EqualTo(HatId));

            // STEP 2 -- ⭐ THE TEETH. Nothing below touches a grid, so the only way the owner can learn about
            // the hat is the dirty flag the Worn branch does not raise by itself. Remove ServerMarkDirty from
            // OnPickupItem and this is the assert that goes red.
            Assert.That(h.StepUntil(() => ClientWornHat(a, a.PlayerId, out var got) && got == HatId), Is.True,
                        $"...and the OWNER REPLICA was told, with no grid edit anywhere (seed={h.Net.Seed})");

            // ...and the hat is worn, not duplicated into the bag as well.
            Assert.That(a.Inventories.TryGet(a.PlayerId, out var ce), Is.True);
            int loose = 0;
            foreach (var pg in ce.Inventory.items)
                for (byte i = 0; i < pg.getItemCount(); i++)
                    if (pg.getItem(i)?.item?.id == HatId) loose++;
            Assert.That(loose, Is.Zero, "the hat is on your head, not on your head AND in your bag");
        }

        /// <summary>CONTROLS. These two pickups land on a PAGE, so they dirtied the entry long before this fix
        /// and must pass either way. They are what makes the test above specific: if the harness, the echo or
        /// the pickup command were simply broken, these would fail too and the Worn case would prove nothing.</summary>
        [Test]
        public void control_pickups_that_land_on_a_page_already_echoed()
        {
            var h = new TransactionalHarness(4914).Connected("a");
            var a = h.Clients[0];
            h.Step(10);

            var beans = h.Server.Transactions.SpawnWorldItem(new Item(GenericId), new Vector3(0.5f, 0f, 0.5f), Vector3.zero);
            h.Step(5);
            a.SendPickupItem(beans.NetIdValue);
            Assert.That(h.StepUntil(() => Holds(a, a.PlayerId, GenericId)), Is.True,
                        $"control: a bagged pickup (AutoPlace.Grid) reaches the owner (seed={h.Net.Seed})");

            var rifle = h.Server.Transactions.SpawnWorldItem(new Item(GunId), new Vector3(0.5f, 0f, 0.5f), Vector3.zero);
            h.Step(5);
            a.SendPickupItem(rifle.NetIdValue);
            Assert.That(h.StepUntil(() => Holds(a, a.PlayerId, GunId)), Is.True,
                        $"control: a holstered pickup (AutoPlace.Slot) reaches the owner (seed={h.Net.Seed})");
        }

        static bool Holds(NetWorldClient client, ushort pid, ushort id)
        {
            if (!client.Inventories.TryGet(pid, out var ce)) return false;
            foreach (var pg in ce.Inventory.items)
                for (byte i = 0; i < pg.getItemCount(); i++)
                    if (pg.getItem(i)?.item?.id == id) return true;
            return false;
        }

        /// <summary>All SEVEN garment slots, because the bug was never about hats -- HAT is simply what master
        /// happened to pick up. One parameterised test rather than seven copies, and it fails for any slot whose
        /// auto-wear stops being published.
        ///
        /// ⭐⭐ RUNNING THIS WITH THE FIX REMOVED IS WHAT NAMED THE REAL BLAST RADIUS, and it is not all seven:
        /// HAT, GLASSES and MASK go red, while VEST, BACKPACK, SHIRT and PANTS stay GREEN. Those four carry a
        /// STORAGE PAGE, and wearing one resizes it (Items.loadSize) -- which raises onStateUpdated and dirties
        /// the entry as a SIDE EFFECT. They were never correct, they were accidentally covered.
        ///
        /// Which is exactly master's own words a month earlier (2026-09-13): "hats and face coverings arent being
        /// applied onto the player until the inventory is updated by moving something". Hat and face coverings --
        /// the three slots in the game with nothing to put in them. The report described the blast radius
        /// precisely and it still took a third pass to read it that way, so the four passing cases stay in: if a
        /// future change stops a bag resizing on wear, those slots lose their accidental cover and this says so.</summary>
        [TestCase(EItemType.HAT, (ushort)27)]
        [TestCase(EItemType.GLASSES, (ushort)334)]
        [TestCase(EItemType.MASK, (ushort)9301)]
        [TestCase(EItemType.VEST, (ushort)9302)]
        [TestCase(EItemType.BACKPACK, (ushort)9303)]
        [TestCase(EItemType.SHIRT, (ushort)9304)]
        [TestCase(EItemType.PANTS, (ushort)9305)]
        public void every_garment_slot_publishes_its_auto_wear(EItemType slot, ushort id)
        {
            Assets.add(new ItemAsset { id = id, itemName = "Test " + slot, size_x = 2, size_y = 2, type = slot });
            var h = new TransactionalHarness((ushort)(5000 + (int)slot)).Connected("a");
            var a = h.Clients[0];
            h.Step(10);

            var drop = h.Server.Transactions.SpawnWorldItem(new Item(id), new Vector3(0.5f, 0f, 0.5f), Vector3.zero);
            h.Step(5);
            a.SendPickupItem(drop.NetIdValue);

            Assert.That(h.StepUntil(() => h.Server.Inventories.TryGet(a.PlayerId, out var se)
                                          && se.Inventory.wornByType(slot)?.id == id), Is.True,
                        $"the server auto-wore the {slot} (seed={h.Net.Seed})");
            Assert.That(h.StepUntil(() => a.Inventories.TryGet(a.PlayerId, out var ce)
                                          && ce.Inventory.wornByType(slot)?.id == id), Is.True,
                        $"...and the owner replica was told about the {slot} (seed={h.Net.Seed})");
        }
    }
}
