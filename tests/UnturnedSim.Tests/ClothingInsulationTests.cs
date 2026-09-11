using NUnit.Framework;
using SDG.Unturned;

namespace UnturnedSim.Tests
{
    // Two insulation values per garment (strawberry 2026-09-10: "each clothing piece has two separate
    // insulation values, heat:cold"). Keyed off the garment word in the item name, which is what makes one
    // rule cover all 8 parkas and all 19 hoodies in the real catalog.
    [TestFixture]
    public class ClothingInsulationTests
    {
        static (float cold, float heat) Shirt(string name) => ClothingInsulation.For(name, EItemType.SHIRT);

        [Test]
        public void ColourPrefix_DoesNotChangeTheGarment()
        {
            // The whole reason this is name-keyed rather than an id table: every colour variant must agree,
            // including ones nobody has added yet.
            Assert.That(Shirt("Orange Parka"), Is.EqualTo(Shirt("Green Parka")));
            Assert.That(Shirt("Purple Parka"), Is.EqualTo(Shirt("Red Parka")));
        }

        [Test]
        public void AParkaIsGreatInTheColdAndWorthlessInTheHeat()
        {
            var parka = Shirt("Red Parka");
            var tee = Shirt("Red T-Shirt");
            Assert.That(parka.cold, Is.GreaterThan(tee.cold));
            Assert.That(parka.heat, Is.LessThan(tee.heat), "a parka must not help in a desert -- this is why there are two values");
        }

        [Test]
        public void AHelmetIsTheOnePieceThatHurtsInTheHeat()
        {
            Assert.That(ClothingInsulation.For("Military Helmet", EItemType.HAT).heat, Is.LessThan(0f));
        }

        [Test]
        public void ACapBeatsAHelmetInTheSun()
        {
            var cap = ClothingInsulation.For("Tan Cap", EItemType.HAT);
            var helmet = ClothingInsulation.For("Military Helmet", EItemType.HAT);
            Assert.That(cap.heat, Is.GreaterThan(helmet.heat));
        }

        [Test]
        public void NoGarmentHasNegativeColdInsulation()
        {
            // A t-shirt must not make you COLDER than being bare -- heat relief is a separate axis, and a
            // negative cold value here would mean undressing beats dressing in a blizzard.
            foreach (var n in new[] { "Red T-Shirt", "Blue Shorts", "Tan Cap", "Swim Trunks", "Military Helmet" })
                Assert.That(Shirt(n).cold, Is.GreaterThanOrEqualTo(0f), n);
        }

        [Test]
        public void AnUnknownGarment_FallsBackToItsSlot_NotToNothing()
        {
            // Returning 0 for an unrecognised name fails silently as "this coat does nothing"; a plausible
            // slot default is wrong by a little and visible.
            var unknown = ClothingInsulation.For("Emerald Spacesuit", EItemType.SHIRT);
            Assert.That(unknown, Is.EqualTo(ClothingInsulation.SlotDefault(EItemType.SHIRT)));
            Assert.That(unknown.cold, Is.GreaterThan(0f));
        }

        [Test]
        public void EmptyOrNullName_DoesNotThrow()
        {
            Assert.That(ClothingInsulation.For(null, EItemType.HAT), Is.EqualTo(ClothingInsulation.SlotDefault(EItemType.HAT)));
            Assert.That(ClothingInsulation.For("", EItemType.HAT), Is.EqualTo(ClothingInsulation.SlotDefault(EItemType.HAT)));
        }

        [Test]
        public void GlassesAndTiesCarryNothingThermally()
        {
            Assert.That(ClothingInsulation.For("Gold Monocle", EItemType.GLASSES), Is.EqualTo((0f, 0f)));
            Assert.That(ClothingInsulation.For("Gold Bowtie", EItemType.VEST), Is.EqualTo((0f, 0f)));
        }
    }
}
