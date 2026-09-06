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
        public void a_campfire_is_the_slow_field_option_with_no_special_word()
        {
            // strawberry 2026-09-06: "takes wood as fuel. slower than a bbq. no food buff".
            Assert.That(Cooking.RatePerSecond(ECookerKind.Campfire),
                        Is.LessThan(Cooking.RatePerSecond(ECookerKind.Barbecue)), "slower than a bbq, explicitly");
            Assert.That(Cooking.StyleOf(ECookerKind.Campfire), Is.EqualTo(ECookStyle.Plain), "no buff word of its own");
            // ...and "no buff" is about the LABEL, not about cooking being pointless: food off a campfire is
            // worth the same as food out of an oven. If that reading is wrong it is this line that changes.
            Assert.That(Cooking.Nutrition(95, Cooking.StyleOf(ECookerKind.Campfire)),
                        Is.EqualTo(Cooking.Nutrition(95, Cooking.StyleOf(ECookerKind.Oven))));

        }

        static ItemAsset Wooden(ushort id, string name, byte w = 1, byte h = 1, EItemType t = EItemType.SUPPLY)
            => new ItemAsset { id = id, itemName = name, size_x = w, size_y = h, type = t };

        [Test]
        public void anything_wooden_burns_and_the_name_traps_do_not()
        {
            // strawberry 2026-09-06: "wood as a fuel should be anything wooden. sticks, planks, deployables."
            Assert.That(Cooking.IsWood(Wooden(37, "Birch Log", 2, 1)), Is.True);
            Assert.That(Cooking.IsWood(Wooden(38, "Birch Stick")), Is.True);
            Assert.That(Cooking.IsWood(Wooden(62, "Birch Plank", 1, 2)), Is.True);
            Assert.That(Cooking.IsWood(Wooden(282, "Birch Door", 1, 2, EItemType.GENERIC)), Is.True, "deployables too");
            Assert.That(Cooking.IsWood(Wooden(1064, "Large Birch Plate", 2, 2, EItemType.GENERIC)), Is.True);

            // THE TRAPS, and they are why this is a whole-word test and not a Contains. All three are real
            // catalog entries: a substring rule feeds a rifle and a hat into the fire.
            Assert.That(Cooking.IsWood(Wooden(363, "Maplestrike", 4, 2, EItemType.GUN)), Is.False, "a RIFLE");
            Assert.That(Cooking.IsWood(Wooden(364, "Maplestrike Iron Sights", 1, 1, EItemType.SIGHT)), Is.False);
            Assert.That(Cooking.IsWood(Wooden(764, "Pineapple", 1, 1, EItemType.HAT)), Is.False, "a HAT");
            // ...and the type gate catches anything wooden-NAMED that is food or gear.
            Assert.That(Cooking.IsWood(Wooden(999, "Maple Syrup", 1, 1, EItemType.FOOD)), Is.False);
        }

        [Test]
        public void burn_time_scales_with_species_and_with_size()
        {
            // "the three wood types have varying burn times, as well as the size of wooden fuel having
            // different burn times". Hardwood outlasts softwood; footprint multiplies.
            float pineStick = Cooking.BurnSecondsFor(Wooden(42, "Pine Stick"));
            float birchStick = Cooking.BurnSecondsFor(Wooden(38, "Birch Stick"));
            float mapleStick = Cooking.BurnSecondsFor(Wooden(40, "Maple Stick"));
            Assert.That(pineStick, Is.LessThan(birchStick), "pine is the fast softwood");
            Assert.That(mapleStick, Is.GreaterThan(birchStick), "maple is the dense one");

            // Same species, bigger piece: a 2x2 plate is four cells against the stick's one.
            float birchPlate = Cooking.BurnSecondsFor(Wooden(1064, "Large Birch Plate", 2, 2, EItemType.GENERIC));
            Assert.That(birchPlate, Is.EqualTo(birchStick * 4f).Within(0.01f));

            // A log (2x1) sits between them, which is the whole point of using the grid footprint: the
            // ordering a player would guess from looking at the items is the ordering they get.
            float birchLog = Cooking.BurnSecondsFor(Wooden(37, "Birch Log", 2, 1));
            Assert.That(birchLog, Is.GreaterThan(birchStick).And.LessThan(birchPlate));

            Assert.That(Cooking.BurnSecondsFor(Wooden(363, "Maplestrike", 4, 2, EItemType.GUN)), Is.Zero,
                        "a rifle burns for no time at all, because it is not fuel");
        }

        [Test]
        public void the_mains_appliances_declare_their_draw()
        {
            // strawberry 2026-09-06: "stove requires power io input, 2kw to cook/globalpower on. toaster
            // requires 1000w. microwave 1.5kw"
            Assert.That(Cooking.PowerWatts(ECookerKind.Oven), Is.EqualTo(2000f));
            Assert.That(Cooking.PowerWatts(ECookerKind.Toaster), Is.EqualTo(1000f));
            Assert.That(Cooking.PowerWatts(ECookerKind.Microwave), Is.EqualTo(1500f));
            // The two families are exclusive: a thing either draws watts or it burns something you put in it.
            foreach (var k in new[] { ECookerKind.Barbecue, ECookerKind.Campfire })
            {
                Assert.That(Cooking.NeedsPower(k), Is.False, $"{k} burns fuel");
                Assert.That(Cooking.NeedsFuel(k), Is.True);
            }
            foreach (var k in new[] { ECookerKind.Oven, ECookerKind.Toaster, ECookerKind.Microwave })
            {
                Assert.That(Cooking.NeedsPower(k), Is.True);
                Assert.That(Cooking.NeedsFuel(k), Is.False, $"{k} runs on the mains");
            }
        }

        [Test]
        public void each_appliance_burns_only_its_own_fuel()
        {
            var charcoal = Wooden(Cooking.CharcoalId, "Charcoal");
            var log = Wooden(37, "Birch Log", 2, 1);
            Assert.That(Cooking.IsFuelFor(ECookerKind.Barbecue, charcoal), Is.True);
            Assert.That(Cooking.IsFuelFor(ECookerKind.Barbecue, log), Is.False, "'bbqs can only take charcoal'");
            Assert.That(Cooking.IsFuelFor(ECookerKind.Campfire, log), Is.True);
            Assert.That(Cooking.IsFuelFor(ECookerKind.Campfire, charcoal), Is.False, "a campfire takes wood");
            foreach (var k in new[] { ECookerKind.Oven, ECookerKind.Toaster, ECookerKind.Microwave })
                Assert.That(Cooking.IsFuelFor(k, log), Is.False, $"{k} runs on the mains");
        }

        [Test]
        public void the_style_is_the_appliance_and_plain_carries_no_label()
        {
            Assert.That(Cooking.StyleOf(ECookerKind.Microwave), Is.EqualTo(ECookStyle.Microwaved));
            Assert.That(Cooking.StyleOf(ECookerKind.Barbecue), Is.EqualTo(ECookStyle.CharcoalGrilled));
            Assert.That(Cooking.StyleOf(ECookerKind.Oven), Is.EqualTo(ECookStyle.Plain));
            Assert.That(Cooking.StyleOf(ECookerKind.Toaster), Is.EqualTo(ECookStyle.Plain));

            // strawberry 2026-09-06: "just raw/uncooked, cooked:cooked quality and burnt". The STATE always
            // shows on food; the QUALITY is what varies the cooked word, and average still contributes no word
            // of its own -- "Cooked" IS the average case.
            Assert.That(Cooking.Label(0, ECookStyle.Plain), Is.EqualTo("Raw"));
            Assert.That(Cooking.Label(95, ECookStyle.Plain), Is.EqualTo("Cooked"));
            Assert.That(Cooking.Label(95, ECookStyle.Microwaved), Is.EqualTo("Microwaved"));
            Assert.That(Cooking.Label(95, ECookStyle.CharcoalGrilled), Is.EqualTo("Charcoal Grilled"));
            Assert.That(Cooking.Label(140, ECookStyle.CharcoalGrilled), Is.EqualTo("Burnt"),
                        "burnt outranks the style -- charcoal grilled charcoal is still charcoal");
        }

        [Test]
        public void cooked_bread_is_toast_and_nothing_else_is_renamed()
        {
            // strawberry 2026-09-06: "cooked bread -> toast". A whole new NAME, not a prefix -- "Cooked Bread"
            // is a description of toast rather than the word for it.
            Assert.That(Cooking.DisplayName("Bread", 460, 95, ECookStyle.Plain, isFood: true), Is.EqualTo("Toast"));
            Assert.That(Cooking.DisplayName("Bread", 460, 0, ECookStyle.Plain, isFood: true), Is.EqualTo("Raw Bread"));
            Assert.That(Cooking.DisplayName("Bread", 460, 140, ECookStyle.Plain, isFood: true), Is.EqualTo("Burnt Bread"),
                        "burnt bread is burnt bread, not burnt toast");
            // Only a PLAIN cook earns the name: a slice out of a microwave is microwaved bread, and calling it
            // "Microwaved Toast" would be the rename fighting the quality label instead of yielding to it.
            Assert.That(Cooking.DisplayName("Bread", 460, 95, ECookStyle.Microwaved, isFood: true),
                        Is.EqualTo("Microwaved Bread"));
            // Nothing else has an override, so everything else takes the prefix.
            Assert.That(Cooking.DisplayName("Canned Beans", 13, 95, ECookStyle.Plain, isFood: true),
                        Is.EqualTo("Cooked Canned Beans"));
        }

        [Test]
        public void a_non_food_item_never_gets_a_state_word()
        {
            // "Raw Bandage" is nonsense, and without this the state word lands on all ~1900 items in the
            // catalog the moment the label became unconditional for raw.
            Assert.That(Cooking.DisplayName("Bandage", 95, 0, ECookStyle.Plain, isFood: false), Is.EqualTo("Bandage"));
            Assert.That(Cooking.DisplayName("Metal Scrap", 67, 0, ECookStyle.Plain, isFood: false), Is.EqualTo("Metal Scrap"));
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
