using System.Linq;
using NUnit.Framework;
using SDG.Unturned;

namespace UnturnedSim.Tests
{
    // COOKING (strawberry 2026-09-05). Engine-free, so every rule is a value asserted here rather than
    // something you have to boot Godot and stand in a kitchen to observe.
    //
    // The checks that carry weight are the two ACCEPT sets. Both were written as explicit id lists instead
    // of name matching, and these tests pin the specific catalog entries that make name matching wrong --
    // so a later "simplify this to a Contains" is a red test rather than a discovery in play.
    [TestFixture]
    public class CookingTests
    {
        [Test]
        public void the_bands_are_where_strawberry_put_them()
        {
            // "90-100% cooked is Cooked. above 100% is burnt"
            Assert.That(Cooking.IsRaw(0), Is.True);
            Assert.That(Cooking.IsRaw(89), Is.True);
            Assert.That(Cooking.IsCooked(89), Is.False, "89 is still raw");
            Assert.That(Cooking.IsCooked(90), Is.True, "90 is the first cooked value");
            Assert.That(Cooking.IsCooked(100), Is.True, "100 is still cooked, not yet burnt");
            Assert.That(Cooking.IsBurnt(100), Is.False);
            Assert.That(Cooking.IsBurnt(101), Is.True, "past 100 is burnt");
        }

        [Test]
        public void a_forgotten_roast_stops_counting_instead_of_wrapping_to_raw()
        {
            // cooked is a byte. Without the MaxCooked clamp, an oven left on overnight rolls 255 -> 0 and a
            // cremated roast reads as raw meat. That is the one arithmetic bug this type can have.
            byte c = 250;
            for (int i = 0; i < 500; i++) c = Cooking.Advance(c, ECookerKind.Oven, 1f);
            Assert.That(c, Is.EqualTo(Cooking.MaxCooked));
            Assert.That(Cooking.IsBurnt(c), Is.True, "still burnt after a very long time, never raw again");
        }

        [Test]
        public void the_toaster_takes_bread_and_refuses_clothing()
        {
            // THE TRAP THIS PINS: the catalog contains "Gingerbread Top" (a SHIRT), "Gingerbread Bottom"
            // (PANTS), "Gingerbread Mask" and "Mime's Baguette" (a BACKPACK). Any rule shaped like
            // name.Contains("bread") or Contains("baguette") lets you toast an outfit.
            Assert.That(Cooking.IsBread(460), Is.True, "Bread");
            Assert.That(Cooking.IsBread(467), Is.True, "BLT Sandwich -- bread with something in it");
            Assert.That(Cooking.IsBread(743), Is.False, "Gingerbread Top is a SHIRT");
            Assert.That(Cooking.IsBread(883), Is.False, "Mime's Baguette is a BACKPACK");
            Assert.That(Cooking.IsBread(13), Is.False, "Canned Beans is not bread");
        }

        [Test]
        public void the_microwave_explodes_on_metal_and_not_on_a_chocolate_bar()
        {
            // THE MIRROR TRAP: Chocolate Bar (83), Candy Bar (84), Granola Bar (85) and Energy Bar (86) all
            // end in "Bar", so name.EndsWith("Bar") detonates the microwave on confectionery.
            Assert.That(Cooking.IsMetal(13), Is.True, "Canned Beans -- a tin");
            Assert.That(Cooking.IsMetal(67), Is.True, "Metal Scrap");
            Assert.That(Cooking.IsMetal(285), Is.True, "Metal Bar");
            foreach (ushort sweet in new ushort[] { 83, 84, 85, 86 })
                Assert.That(Cooking.IsMetal(sweet), Is.False, $"item {sweet} is confectionery, not metal");
            Assert.That(Cooking.IsMetal(460), Is.False, "Bread");
        }

        [Test]
        public void speed_matches_what_was_asked_for()
        {
            // "ovens are medium speed", "toasters are fast", "microwaves are fast", "bbqs ... same speed as an oven"
            Assert.That(Cooking.RatePerSecond(ECookerKind.Barbecue),
                        Is.EqualTo(Cooking.RatePerSecond(ECookerKind.Oven)), "bbq == oven, explicitly asked for");
            Assert.That(Cooking.RatePerSecond(ECookerKind.Toaster),
                        Is.GreaterThan(Cooking.RatePerSecond(ECookerKind.Oven)), "toaster is the fast pair");
            Assert.That(Cooking.RatePerSecond(ECookerKind.Microwave),
                        Is.GreaterThan(Cooking.RatePerSecond(ECookerKind.Oven)), "microwave is the fast pair");
        }

        [Test]
        public void the_style_is_the_appliance_and_plain_carries_no_label()
        {
            Assert.That(Cooking.StyleOf(ECookerKind.Microwave), Is.EqualTo(ECookStyle.Microwaved));
            Assert.That(Cooking.StyleOf(ECookerKind.Barbecue), Is.EqualTo(ECookStyle.CharcoalGrilled));
            Assert.That(Cooking.StyleOf(ECookerKind.Oven), Is.EqualTo(ECookStyle.Plain));
            Assert.That(Cooking.StyleOf(ECookerKind.Toaster), Is.EqualTo(ECookStyle.Plain));

            // "average (no label)" -- raw food and plainly-cooked food both show nothing. A label on
            // everything is a label on nothing.
            Assert.That(Cooking.Label(0, ECookStyle.Plain), Is.Empty, "raw shows nothing");
            Assert.That(Cooking.Label(95, ECookStyle.Plain), Is.EqualTo("Cooked"));
            Assert.That(Cooking.Label(95, ECookStyle.Microwaved), Is.EqualTo("Microwaved"));
            Assert.That(Cooking.Label(95, ECookStyle.CharcoalGrilled), Is.EqualTo("Charcoal Grilled"));
            Assert.That(Cooking.Label(140, ECookStyle.CharcoalGrilled), Is.EqualTo("Burnt"),
                        "burnt outranks the style -- charcoal grilled charcoal is still charcoal");
        }

        [Test]
        public void cooking_is_a_reward_and_raw_is_not_a_punishment()
        {
            // Raw stays EXACTLY as valuable as it is today, so this feature adds a reason to cook rather than
            // quietly nerfing every food item in the game the day it lands.
            Assert.That(Cooking.Nutrition(0, ECookStyle.Plain), Is.EqualTo(1f));

            float plain = Cooking.Nutrition(95, ECookStyle.Plain);
            float micro = Cooking.Nutrition(95, ECookStyle.Microwaved);
            float coal = Cooking.Nutrition(95, ECookStyle.CharcoalGrilled);
            Assert.That(micro, Is.LessThan(plain), "microwave is a DEBUFF against a normal cook");
            Assert.That(micro, Is.GreaterThan(1f), "...but still better than eating it raw");
            Assert.That(coal, Is.GreaterThan(plain), "charcoal grilled is the BUFF");
            Assert.That(Cooking.Nutrition(150, ECookStyle.CharcoalGrilled), Is.LessThan(1f),
                        "burnt is worse than raw whatever cooked it");
        }

        [Test]
        public void a_toaster_only_ever_accepts_bread_but_a_microwave_accepts_the_can_it_dies_on()
        {
            var bread = new ItemAsset { id = 460, type = EItemType.FOOD };
            var beans = new ItemAsset { id = 13, type = EItemType.FOOD };
            var scrap = new ItemAsset { id = 67, type = EItemType.SUPPLY };

            Assert.That(Cooking.Accepts(ECookerKind.Toaster, bread), Is.True);
            Assert.That(Cooking.Accepts(ECookerKind.Toaster, beans), Is.False, "not bread");
            Assert.That(Cooking.Accepts(ECookerKind.Oven, beans), Is.True, "an oven cooks food");
            Assert.That(Cooking.Accepts(ECookerKind.Oven, scrap), Is.False, "scrap metal is not food");

            // Accepts and Detonates are separate questions and this is why: a microwave ACCEPTS a tin of
            // beans (it is food) and that is exactly how you get to blow it up. Folding the two together
            // would make the can inert instead of lethal.
            Assert.That(Cooking.Accepts(ECookerKind.Microwave, beans), Is.True);
            Assert.That(Cooking.Detonates(ECookerKind.Microwave, beans), Is.True);
            Assert.That(Cooking.Detonates(ECookerKind.Oven, beans), Is.False, "an oven is fine with a tin");
            Assert.That(Cooking.Detonates(ECookerKind.Microwave, bread), Is.False);
        }
    }
}
