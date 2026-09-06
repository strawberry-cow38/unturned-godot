using NUnit.Framework;
using SDG.Unturned;

namespace UnturnedSim.Tests
{
    /// <summary>The frozen state (strawberry 2026-09-06). These cover the RULES; ServerFreezingTests covers the
    /// sweep that applies them and the wire tests cover the field surviving replication.</summary>
    [TestFixture]
    public class FreezingTests
    {
        static ItemAsset Food(ushort id = 13, string name = "Canned Beans")
            => new ItemAsset { id = id, itemName = name, size_x = 1, size_y = 1, type = EItemType.FOOD, useFood = 30 };

        [Test]
        public void only_food_freezes()
        {
            // A frozen rifle is not a mechanic. Both directions, because a one-sided check passes by freezing
            // everything OR nothing.
            Assert.That(Freezing.Freezable(Food()), Is.True);
            Assert.That(Freezing.Freezable(new ItemAsset { id = 4, itemName = "Eaglefire", type = EItemType.GUN }), Is.False);
            Assert.That(Freezing.Freezable(new ItemAsset { id = 37, itemName = "Birch Log", type = EItemType.SUPPLY }), Is.False);
            Assert.That(Freezing.Freezable(null), Is.False);
        }

        [Test]
        public void any_amount_of_frozen_blocks_eating()
        {
            // "frozen food cannot be eaten until thawed" -- the gate is ANY frozen, not solid-frozen. A steak at
            // 1 % is still a steak with ice in it, and a >= 100 test here would let 99 % through.
            Assert.That(Freezing.CanEat(new Item(13) { frozen = 0 }), Is.True);
            Assert.That(Freezing.CanEat(new Item(13) { frozen = 1 }), Is.False, "1% is still frozen");
            Assert.That(Freezing.CanEat(new Item(13) { frozen = 99 }), Is.False);
            Assert.That(Freezing.CanEat(new Item(13) { frozen = 100 }), Is.False);
            // ...and cooking is gated on exactly the same line, because "cooking starts after the food is thawed".
            for (byte f = 0; f <= 100; f += 25)
                Assert.That(Freezing.CanCook(new Item(13) { frozen = f }), Is.EqualTo(f == 0), $"at {f}%");
        }

        [Test]
        public void freezing_fills_and_thawing_empties_and_neither_wraps()
        {
            // The byte arithmetic is the whole risk here: an unclamped Advance turns 100 + 1 into 101 and, worse,
            // 0 - 1 into 255 -- a fully thawed steak would come back frozen solid.
            byte f = 0;
            for (int i = 0; i < 200; i++) f = Freezing.Freeze(f, 1f);
            Assert.That(f, Is.EqualTo(Freezing.Max), "freezing saturates at 100, it does not roll over");

            for (int i = 0; i < 500; i++) f = Freezing.Thaw(f, 1f);
            Assert.That(f, Is.Zero, "thawing bottoms out at 0 rather than wrapping to 255");

            Assert.That(Freezing.Advance(0, -5f, 1f), Is.Zero, "a single step below zero clamps too");
            Assert.That(Freezing.Advance(100, 5f, 1f), Is.EqualTo(Freezing.Max));
        }

        [Test]
        public void cooking_thaws_faster_than_a_shelf_does()
        {
            // "drops faster when being cooked". Asserted as an ordering rather than against the constants, so
            // retuning the rates cannot silently invert the rule the request actually stated.
            byte shelf = Freezing.Thaw(100, 5f);
            byte oven = Freezing.ThawWhileCooking(100, 5f);
            Assert.That(oven, Is.LessThan(shelf), $"an oven should beat a shelf ({oven} vs {shelf})");
            Assert.That(Freezing.Freeze(0, 5f), Is.GreaterThan(0), "and a freezer actually freezes");
        }

        [Test]
        public void spoilage_stops_dead_at_solid_and_merely_slows_in_a_fridge()
        {
            // The three cases she named, as one ordering: frozen solid NEVER spoils, a fridge is much slower
            // than a shelf, and a shelf is the baseline.
            float shelf = Freezing.SpoilMultiplier(0, refrigerated: false);
            float fridge = Freezing.SpoilMultiplier(0, refrigerated: true);
            float solid = Freezing.SpoilMultiplier(100, refrigerated: false);

            Assert.That(shelf, Is.EqualTo(1f).Within(0.001f), "an item on a shelf spoils at its own rate");
            Assert.That(solid, Is.Zero, "\"at 100% they NEVER spoil\" -- exactly zero, not merely small");
            Assert.That(fridge, Is.LessThan(shelf * 0.5f), $"\"much slower\", not slightly ({fridge})");
            Assert.That(fridge, Is.GreaterThan(0f), "...but a fridge is NOT a freezer: it still spoils");

            // Partial freezing has no cliff at 99 -- it scales, so an item does not lurch when it tops out.
            Assert.That(Freezing.SpoilMultiplier(50, false), Is.LessThan(shelf));
            Assert.That(Freezing.SpoilMultiplier(99, false), Is.GreaterThan(0f), "99% is not 100%");
        }

        [Test]
        public void the_label_is_silent_until_there_is_something_to_say()
        {
            Assert.That(Freezing.Label(0), Is.Null, "unfrozen food must not carry a '0% frozen' tag");
            Assert.That(Freezing.Label(50), Is.EqualTo("Partly Frozen"));
            Assert.That(Freezing.Label(100), Is.EqualTo("Frozen"));
        }
    }

    /// <summary>The page-bound refactor that made room for the freezer page. Its failure mode is silent and
    /// nasty, so it gets its own fixture.</summary>
    [TestFixture]
    public class OwnPagesBoundTests
    {
        [Test]
        public void auto_add_never_lands_an_item_in_a_container_you_are_looking_at()
        {
            // THE BUG THIS EXISTS TO PREVENT. Forty call sites spelled "the player's own pages" as `PAGES - 2`,
            // which silently meant "all but the last two". Appending FREEZER as a tenth page would have shifted
            // that window onto STORAGE, and the first symptom in a real session would be picked-up loot
            // vanishing into whatever crate happened to be open.
            Assert.That(PlayerInventory.OWNPAGES, Is.LessThanOrEqualTo(PlayerInventory.STORAGE),
                        "the player's own pages must stop before the external container views begin");

            var inv = new PlayerInventory();
            Assets.add(new ItemAsset { id = 900, itemName = "Test Brick", size_x = 1, size_y = 1, type = EItemType.SUPPLY });

            // Give the external pages plenty of room and fill the player's own pockets completely.
            inv.items[PlayerInventory.STORAGE].loadSize(8, 8);
            inv.items[PlayerInventory.AREA].loadSize(8, 8);
            inv.items[PlayerInventory.FREEZER].loadSize(8, 8);
            int packed = 0;
            while (inv.tryAddItem(new Item(900)) && packed < 500) packed++;

            Assert.That(packed, Is.GreaterThan(0), "the fixture has to actually store something to be a test");
            foreach (byte external in new[] { PlayerInventory.STORAGE, PlayerInventory.AREA, PlayerInventory.FREEZER })
                Assert.That(inv.items[external].getItemCount(), Is.Zero,
                            $"auto-add leaked into page {external} -- it must only ever fill the player's own pages");
        }

        [Test]
        public void the_ground_is_not_a_drag_target_but_the_freezer_is()
        {
            // TryDrag used to reject `page >= PAGES - 1`, which pointed at AREA only while AREA happened to be
            // last. Appending FREEZER moved that arithmetic one page along and would have inverted it exactly:
            // the freezer banned, the ground permitted. Both directions asserted for that reason.
            var inv = new PlayerInventory();
            Assets.add(new ItemAsset { id = 901, itemName = "Test Steak", size_x = 1, size_y = 1, type = EItemType.FOOD });
            inv.items[PlayerInventory.FREEZER].loadSize(4, 2);
            inv.items[PlayerInventory.STORAGE].loadSize(4, 2);
            inv.items[PlayerInventory.AREA].loadSize(4, 2);
            inv.items[PlayerInventory.STORAGE].addItem(0, 0, 0, new Item(901));

            Assert.That(inv.TryDrag(PlayerInventory.STORAGE, 0, 0, PlayerInventory.FREEZER, 0, 0, 0), Is.True,
                        "dragging a steak from the fridge into its freezer is the whole interaction");
            Assert.That(inv.items[PlayerInventory.FREEZER].getItemCount(), Is.EqualTo(1));

            Assert.That(inv.TryDrag(PlayerInventory.FREEZER, 0, 0, PlayerInventory.AREA, 0, 0, 0), Is.False,
                        "the ground is still not a drag endpoint");
        }
    }
}
