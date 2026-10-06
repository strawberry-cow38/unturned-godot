using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using SDG.Unturned;
using UnturnedGodot;

namespace UnturnedSim.Tests
{
    /// <summary>
    /// DURABILITY (strawberry 2026-10-06): the rulebook in core, before any wire or engine. Every number asserted here
    /// is either retail's (the penalty curves, the armor formula, Durability/Wear) or one she chose (10 days of wear, a
    /// 25% chance to lose 3 per craft) -- see Durability.
    /// </summary>
    [TestFixture]
    public class DurabilityTests
    {
        const ushort Gun = 9701, Knife = 9702, Shirt = 9703, Vest = 9704, Pants = 9705, Parka = 9706, GasMask = 9707, Poncho = 9708, Beans = 9709, Saw = 9710;

        [SetUp]
        public void SetUp()
        {
            Durability.ToolIds.Clear();
            LootCondition.Clear();
            Assets.add(new ItemAsset { id = Gun, itemName = "Test Rifle", type = EItemType.GUN, gunName = "testrifle", durability = 0.15f, wear = 1 });
            Assets.add(new ItemAsset { id = Knife, itemName = "Test Knife", type = EItemType.MELEE, meleeName = "testknife", durability = 0.1f, wear = 3 });
            Assets.add(new ItemAsset { id = Shirt, itemName = "Test Shirt", type = EItemType.SHIRT, armor = 0.9f });
            Assets.add(new ItemAsset { id = Vest, itemName = "Test Vest", type = EItemType.VEST, armor = 0.6f, explosionArmor = 0.5f });
            Assets.add(new ItemAsset { id = Pants, itemName = "Test Pants", type = EItemType.PANTS, armor = 0.8f, fallingDamageMultiplier = 0.5f });
            Assets.add(new ItemAsset { id = Parka, itemName = "Grey Parka", type = EItemType.SHIRT });
            Assets.add(new ItemAsset { id = GasMask, itemName = "Test Gasmask", type = EItemType.MASK, proofRadiation = true });
            Assets.add(new ItemAsset { id = Poncho, itemName = "Test Poncho", type = EItemType.SHIRT, proofWater = true });
            Assets.add(new ItemAsset { id = Beans, itemName = "Test Beans", type = EItemType.FOOD, qualityMin = 40, qualityMax = 60 });
            Assets.add(new ItemAsset { id = Saw, itemName = "Test Saw", type = EItemType.SUPPLY, guid = "dddd0000000000000000000000000010" });
        }

        [TearDown]
        public void TearDown() { Durability.ToolIds.Clear(); LootCondition.Clear(); }

        static Func<double> Rolls(params double[] seq) { int i = 0; return () => seq[i++ % seq.Length]; }

        // ---------------------------------------------------------------- what has a condition

        [Test]
        public void guns_melee_clothing_and_tools_have_a_condition_and_food_does_not()
        {
            Assert.That(Durability.KindOf(Gun), Is.EqualTo(Durability.Kind.Gun));
            Assert.That(Durability.KindOf(Knife), Is.EqualTo(Durability.Kind.Melee));
            Assert.That(Durability.KindOf(Shirt), Is.EqualTo(Durability.Kind.Clothing));
            Assert.That(Durability.KindOf(Saw), Is.EqualTo(Durability.Kind.None), "a SUPPLY is not a tool until a recipe uses it as one");
            var bp = new BlueprintDef { Operation = "Craft" };
            bp.Inputs.Add(new BlueprintDef.Ingredient { Guid = "dddd0000000000000000000000000010", Amount = 1, Consume = false });
            Durability.RegisterTools(new[] { bp });
            Assert.That(Durability.KindOf(Saw), Is.EqualTo(Durability.Kind.Tool), "...and then it is");
            Assert.That(Durability.HasCondition(Assets.find(Beans)), Is.False, "food's quality is freshness, a different thing");
            Assert.That(Durability.IsBroken(new Item(Beans) { quality = 0 }), Is.False, "rotten beans are not 'broken'");
            Assert.That(Durability.IsBroken(new Item(Gun) { quality = 0 }), Is.True);
        }

        // ---------------------------------------------------------------- retail penalties

        [TestCase((byte)100, 1f, 1f)]
        [TestCase((byte)50, 1f, 1f)]
        [TestCase((byte)25, 1.5f, 0.75f)]
        [TestCase((byte)0, 2f, 0.5f)]
        public void the_penalties_are_retails_curves(byte quality, float handling, float damage)
        {
            // UseableGun: spread/recoil *= q < 0.5 ? 1 + (1 - 2q) : 1;  bullet damage *= q < 0.5 ? 0.5 + q : 1
            Assert.That(Durability.HandlingPenalty(quality), Is.EqualTo(handling).Within(1e-5f));
            Assert.That(Durability.DamageMultiplier(quality), Is.EqualTo(damage).Within(1e-5f));
        }

        // ---------------------------------------------------------------- weapons

        [Test]
        public void a_weapon_use_loses_its_wear_only_when_the_roll_lands_under_its_durability()
        {
            var a = Assets.find(Knife);   // durability 0.1, wear 3
            var it = new Item(Knife) { quality = 50 };
            Assert.That(Durability.UseWeapon(it, a, Rolls(0.5)), Is.Zero, "a roll over the chance: nothing");
            Assert.That(it.quality, Is.EqualTo(50));
            Assert.That(Durability.UseWeapon(it, a, Rolls(0.05)), Is.EqualTo(3), "under it: the asset's Wear");
            Assert.That(it.quality, Is.EqualTo(47));
            it.quality = 2;
            Assert.That(Durability.UseWeapon(it, a, Rolls(0.0)), Is.EqualTo(2), "floored at 0, never wrapping a byte");
            Assert.That(it.quality, Is.EqualTo(0));
            Assert.That(Durability.UseWeapon(it, a, Rolls(0.0)), Is.Zero, "a broken weapon is not used, so it cannot lose more");
        }

        [Test]
        public void a_retail_rifle_lasts_about_as_long_as_its_durability_says()
        {
            // Eaglefire: Durability 0.15, Wear 1 -> one point per ~6.7 shots -> ~667 shots from 100 to 0
            var a = Assets.find(Gun);
            var it = new Item(Gun) { quality = 100 };
            var rng = new Random(7);
            int shots = 0;
            while (it.quality > 0 && shots < 10000) { Durability.UseWeapon(it, a, rng.NextDouble); shots++; }
            Assert.That(shots, Is.InRange(560, 780), $"{shots} shots to break (expected ~667)");
        }

        // ---------------------------------------------------------------- tools

        [Test]
        public void a_tool_has_a_quarter_chance_to_lose_three_per_use()
        {
            var it = new Item(Saw) { quality = 10 };
            Assert.That(Durability.UseTool(it, Rolls(0.3)), Is.Zero);
            Assert.That(Durability.UseTool(it, Rolls(0.2)), Is.EqualTo(3));
            Assert.That(it.quality, Is.EqualTo(7));
            var rng = new Random(3);
            int lost = 0;
            for (int i = 0; i < 4000; i++) { var t = new Item(Saw) { quality = 100 }; lost += Durability.UseTool(t, rng.NextDouble); }
            Assert.That(lost / 4000.0, Is.EqualTo(0.75).Within(0.06), "0.25 x 3 = 0.75 points a craft on average");
        }

        [Test]
        public void a_broken_tool_does_not_count_for_a_recipe_and_the_reason_says_so()
        {
            var bp = new BlueprintDef { Operation = "Craft" };
            bp.Inputs.Add(new BlueprintDef.Ingredient { Guid = "dddd0000000000000000000000000010", Amount = 1, Consume = false });
            Durability.RegisterTools(new[] { bp });
            var inv = new PlayerInventory();
            Assert.That(inv.tryAddItem(new Item(Saw) { quality = 0 }), Is.True);
            var adapter = new Crafting.PlayerInvAdapter(inv);
            Assert.That(Crafting.CanCraft(bp, adapter, out var why), Is.False, "the only saw is broken");
            Assert.That(why, Does.Contain("broken"), why);
            Assert.That(inv.tryAddItem(new Item(Saw) { quality = 1 }), Is.True);
            Assert.That(Crafting.CanCraft(bp, adapter, out why), Is.True, "a saw at 1% still saws");
            Assert.That(adapter.UsableTool(Saw).quality, Is.EqualTo(1), "and THAT is the one a craft would use, not the broken one");
        }

        // ---------------------------------------------------------------- clothing

        [Test]
        public void armor_passes_more_as_the_garment_wears_and_everything_when_it_is_broken()
        {
            var vest = Assets.find(Vest);   // armor 0.6 = 40% stopped at full
            Assert.That(Durability.PassThrough(vest, 100), Is.EqualTo(0.6f).Within(1e-5f));
            Assert.That(Durability.PassThrough(vest, 50), Is.EqualTo(0.8f).Within(1e-5f), "retail: armor + (1-armor)(1-q)");
            Assert.That(Durability.PassThrough(vest, 0), Is.EqualTo(1f).Within(1e-5f), "broken stops nothing");
        }

        [Test]
        public void a_zone_is_covered_by_the_right_pieces_and_their_protection_multiplies()
        {
            var inv = new PlayerInventory();
            inv.wearShirt(new Item(Shirt) { quality = 100 });   // 0.9
            inv.wearVest(new Item(Vest) { quality = 100 });     // 0.6
            inv.wearPants(new Item(Pants) { quality = 50 });    // 0.8 -> 0.9 at half
            Assert.That(inv.PassThrough(Durability.Zone.Torso), Is.EqualTo(0.9f * 0.6f).Within(1e-5f), "shirt AND vest cover the torso");
            Assert.That(inv.PassThrough(Durability.Zone.Legs), Is.EqualTo(0.9f).Within(1e-5f), "worn trousers at half condition");
            Assert.That(inv.PassThrough(Durability.Zone.Head), Is.EqualTo(1f), "nothing on the head");
        }

        [Test]
        public void whole_body_protection_relaxes_with_wear()
        {
            var inv = new PlayerInventory();
            var vest = new Item(Vest) { quality = 100 };
            inv.wearVest(vest);
            Assert.That(inv.ExplosionArmor, Is.EqualTo(0.5f).Within(1e-5f));
            vest.quality = 0;
            Assert.That(inv.ExplosionArmor, Is.EqualTo(1f).Within(1e-5f), "a broken vest stops no blast");
            var pants = new Item(Pants) { quality = 50 };
            inv.wearPants(pants);
            Assert.That(inv.FallingDamageMultiplier, Is.EqualTo(0.75f).Within(1e-5f), "fall 0.5 at full -> 0.75 at half");
        }

        [Test]
        public void insulation_scales_with_condition()
        {
            var inv = new PlayerInventory();
            var parka = new Item(Parka) { quality = 100 };
            inv.wearShirt(parka);
            float full = inv.InsulationColdC;
            Assert.That(full, Is.GreaterThan(0f), "fixture: a parka is warm");
            parka.quality = 50;
            Assert.That(inv.InsulationColdC, Is.EqualTo(full * 0.5f).Within(1e-4f));
            parka.quality = 0;
            Assert.That(inv.InsulationColdC, Is.Zero, "a broken parka keeps no warmth");
        }

        [Test]
        public void a_broken_poncho_no_longer_keeps_the_rain_off()
        {
            var inv = new PlayerInventory();
            var poncho = new Item(Poncho) { quality = 1 };
            inv.wearShirt(poncho);
            Assert.That(inv.ProofsWater, Is.True);
            poncho.quality = 0;
            Assert.That(inv.ProofsWater, Is.False);
        }

        [Test]
        public void a_gas_masks_quality_is_its_filter_and_it_protects_at_full_whatever_the_filter()
        {
            Assert.That(PlayerInventory.IsFilterMask(Assets.find(GasMask)), Is.True);
            Assert.That(PlayerInventory.IsFilterMask(Assets.find(Shirt)), Is.False);
        }

        // ---------------------------------------------------------------- loot

        [Test]
        public void loot_condition_is_uniform_in_the_band_and_the_bias_bends_it_without_leaving_it()
        {
            var rng = new Random(11);
            int[] Roll(float bias) => Enumerable.Range(0, 4000).Select(_ => (int)Durability.RollCondition(10, 90, bias, rng)).ToArray();
            var even = Roll(0f); var good = Roll(1f); var bad = Roll(-1f);
            foreach (var s in new[] { even, good, bad })
                Assert.That(s.Min() >= 10 && s.Max() <= 90, Is.True, "never outside the item's band");
            Assert.That(even.Average(), Is.EqualTo(50).Within(2.5), "bias 0 = retail's uniform draw");
            Assert.That(good.Average(), Is.GreaterThan(70), $"+1 leans good ({good.Average():0.0})");
            Assert.That(bad.Average(), Is.LessThan(30), $"-1 leans bad ({bad.Average():0.0})");
            Assert.That(good.Count(q => q < 20), Is.GreaterThan(0), "...but a bad one can still turn up: it bends, it does not clamp");
        }

        [Test]
        public void spawned_gear_rolls_a_condition_and_its_tables_bias_applies()
        {
            var qs = Enumerable.Range(0, 400).Select(_ => (int)Assets.makeLoot(Gun).quality).ToList();
            Assert.That(qs.Distinct().Count(), Is.GreaterThan(20), "a gun now spawns at a random condition, not always 100");
            Assert.That(qs.Min() >= 10 && qs.Max() <= 90, Is.True, "in retail's default 10-90 band");
            LootCondition.Set(5, 1f);
            var biased = Enumerable.Range(0, 400).Select(_ => (int)Assets.makeLoot(Gun, 5).quality).Average();
            var other = Enumerable.Range(0, 400).Select(_ => (int)Assets.makeLoot(Gun, 6).quality).Average();
            Assert.That(biased, Is.GreaterThan(other + 15), $"table 5 is set to 'better' ({biased:0.0} vs table 6 {other:0.0})");
            var beans = Enumerable.Range(0, 200).Select(_ => (int)Assets.makeLoot(Beans).quality).ToList();
            Assert.That(beans.Min() >= 40 && beans.Max() <= 60, Is.True, "food keeps its own band");
        }

        [Test]
        public void the_editors_per_table_settings_round_trip_through_their_file()
        {
            string path = Path.Combine(Path.GetTempPath(), $"lootcond_{Guid.NewGuid():N}.txt");
            try
            {
                LootCondition.Set(3, 0.5f); LootCondition.Set(17, -0.25f); LootCondition.Set(9, 0f);
                LootCondition.Save(path);
                LootCondition.Clear();
                LootCondition.Load(path);
                Assert.That(LootCondition.Bias(3), Is.EqualTo(0.5f).Within(1e-4f));
                Assert.That(LootCondition.Bias(17), Is.EqualTo(-0.25f).Within(1e-4f));
                Assert.That(LootCondition.Bias(9), Is.Zero, "0 is not stored -- it is the default");
                Assert.That(LootCondition.Count, Is.EqualTo(2));
                LootCondition.Load(path + ".missing");
                Assert.That(LootCondition.Count, Is.Zero, "a map with no file has no bias, not the last map's");
            }
            finally { File.Delete(path); }
        }
    }
}
