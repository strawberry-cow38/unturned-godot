using NUnit.Framework;
using SDG.Unturned;

namespace UnturnedSim.Tests
{
    // WHICH HAND SLOT AN ITEM GOES TO -- the rule that existed three times and was right twice.
    //
    // strawberry 2026-09-13: "when dragging a weapon that could go in either 1/2 slot, always try to fill an
    // empty slot rather than replacing". Pickup and the right-click equip already did that; the paperdoll DRAG
    // called PreferredSlot() flat, so a sidearm always took the secondary and threw out whatever was in it even
    // with the primary standing empty. Same item, same inventory, two different answers depending on the
    // gesture. All three now go through PlayerInventory, and these tests are written against that.
    //
    // ⚠ Every case here pairs the interesting state with the state next to it. "The sidearm went to slot 0"
    // proves nothing on its own -- a function that always returned 0 would pass it. What has to hold is that
    // the answer MOVES with which slot is occupied, so each test also pins where the same item goes when the
    // occupancy is different.
    [TestFixture]
    public class HandSlotChoiceTests
    {
        const ushort RifleId = 9001, SidearmId = 9002, AnyId = 9003;

        [SetUp]
        public void RegisterAssets()
        {
            // Additive -- this fixture shares a process with every other test here, so never Assets.clear().
            if (Assets.find(RifleId) == null)
                Assets.add(new ItemAsset { id = RifleId, itemName = "Test Rifle", size_x = 1, size_y = 1,
                                           type = EItemType.GUN, slot = ESlotType.PRIMARY });
            if (Assets.find(SidearmId) == null)
                Assets.add(new ItemAsset { id = SidearmId, itemName = "Test Sidearm", size_x = 1, size_y = 1,
                                           type = EItemType.GUN, slot = ESlotType.SECONDARY });
            if (Assets.find(AnyId) == null)
                Assets.add(new ItemAsset { id = AnyId, itemName = "Test Anything", size_x = 1, size_y = 1,
                                           type = EItemType.MELEE, slot = ESlotType.ANY });
            Assume.That(Assets.find(SidearmId), Is.Not.Null, "fixture: the sidearm asset did not register");
        }

        static ItemAsset A(ushort id) => Assets.find(id);

        static PlayerInventory Inv(params (byte slot, ushort id)[] occupied)
        {
            var inv = new PlayerInventory();
            foreach (var (slot, id) in occupied) inv.equipToSlot(slot, new Item(id));
            return inv;
        }

        // THE BUG. A sidearm prefers the secondary; with the secondary full and the primary free it must take
        // the primary rather than evict. The second half is what makes the first mean something: with BOTH free
        // the same call still answers 1, so this is a rule about occupancy and not a function that returns 0.
        [Test]
        public void ASidearmTakesTheFreePrimaryRatherThanEvictingTheSecondary()
        {
            Assert.That(Inv((1, SidearmId)).EquipHandSlotFor(A(SidearmId)), Is.EqualTo(0),
                        "secondary occupied, primary free -> holster in the primary");
            Assert.That(Inv().EquipHandSlotFor(A(SidearmId)), Is.EqualTo(1),
                        "...but with both free it still PREFERS the secondary -- the hip, not the back");
        }

        [Test]
        public void AnAnySlotItemDoesTheSame()
        {
            Assert.That(Inv((1, AnyId)).EquipHandSlotFor(A(AnyId)), Is.EqualTo(0));
            Assert.That(Inv().EquipHandSlotFor(A(AnyId)), Is.EqualTo(1));
        }

        // Both full -> there is nothing to fill, so the equip gesture falls back to the preferred slot and the
        // caller displaces its occupant. "Fill an empty slot rather than replacing" is a preference, not a ban.
        [Test]
        public void WithBothSlotsFullTheEquipGestureStillDisplacesThePreferredOne()
        {
            var inv = Inv((0, RifleId), (1, SidearmId));
            Assert.That(inv.EquipHandSlotFor(A(SidearmId)), Is.EqualTo(1), "nothing free -> displace the preferred slot");
            Assert.That(inv.EmptyHandSlotFor(A(SidearmId)), Is.EqualTo(-1), "...and there genuinely is no free slot");
        }

        // ⚠ THE RULE MUST NOT PUT A RIFLE IN THE HIP. "Fill an empty slot" is bounded by what the item may
        // occupy at all -- a PRIMARY-only weapon with a full primary and an empty secondary has NO free slot,
        // and answering 1 here would be the fix creating a worse bug than the one it fixed.
        [Test]
        public void APrimaryOnlyWeaponNeverFallsIntoTheEmptySecondary()
        {
            var inv = Inv((0, RifleId));
            Assert.That(inv.EmptyHandSlotFor(A(RifleId)), Is.EqualTo(-1),
                        "the secondary is empty but a rifle cannot go there");
            Assert.That(inv.EquipHandSlotFor(A(RifleId)), Is.EqualTo(0), "so it displaces the other rifle");
            Assert.That(Inv().EmptyHandSlotFor(A(RifleId)), Is.EqualTo(0), "with the primary free it takes it");
        }

        // PICKUP has different semantics from an equip gesture and must keep them: no free slot means BAG IT,
        // not evict something. Folding both into one answer is the mistake this pair of methods exists to avoid.
        [Test]
        public void PickupBagsTheItemRatherThanDisplacingAnything()
        {
            var inv = Inv((0, RifleId), (1, SidearmId));
            var place = inv.tryAddItemAuto(new Item(SidearmId), out byte slot);
            Assert.That(place, Is.EqualTo(PlayerInventory.AutoPlace.Grid), "both hands full -> it goes in the bag");
            Assert.That(slot, Is.EqualTo(byte.MaxValue), "and no slot is reported");
            Assert.That(inv.items[1].getItemCount(), Is.EqualTo(1), "the holstered sidearm was NOT thrown out");
        }

        [Test]
        public void PickupStillFillsTheOtherHandWhenThePreferredOneIsTaken()
        {
            var inv = Inv((1, SidearmId));
            var place = inv.tryAddItemAuto(new Item(SidearmId), out byte slot);
            Assert.That(place, Is.EqualTo(PlayerInventory.AutoPlace.Slot));
            Assert.That(slot, Is.EqualTo(0), "the free primary takes it");
        }

        // A non-holster item has no hand slot at all, and must not be handed slot 0 by a helper that forgot to
        // say so -- that would let a bandage be dragged into a holster.
        [Test]
        public void ANonHolsterItemReportsNoSlot()
        {
            var inv = Inv();
            var bandage = new ItemAsset { id = 9004, itemName = "Test Bandage", size_x = 1, size_y = 1,
                                          type = EItemType.MEDICAL, slot = ESlotType.NONE };
            Assert.That(inv.EmptyHandSlotFor(bandage), Is.EqualTo(-1));
            Assert.That(inv.EquipHandSlotFor(bandage), Is.EqualTo(-1));
            Assert.That(inv.EquipHandSlotFor(null), Is.EqualTo(-1), "and a null asset must not throw");
        }
    }
}
