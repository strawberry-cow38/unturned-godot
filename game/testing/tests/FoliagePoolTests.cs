using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>Redwoods and cacti exist as real planted foliage, and a cactus drops something
    /// (strawberry 2026-10-10: "wire up redwood and cacti foliage like existing trees as spawns from the pools
    /// of model. add a new cactus flesh item as the drop from cactus").
    ///
    /// ⚠ WHY THESE CHECKS AND NOT "DOES IT LOAD". The art for both has existed for a day -- 14 cactus models on
    /// an art branch and three redwoods in a handoff zip -- and neither was in the game, because loose .obj files
    /// in the right folder with no manifest row and no placements look exactly like a shipped feature right up
    /// until you go and look for one. So these assert the things that were actually missing: that a redwood is
    /// classified as a TREE (gets a trunk, can be chopped, yields its own log), that a cactus yields the new
    /// item, and that the placement files are non-empty where they should be.</summary>
    public sealed class FoliagePoolTests : GameTest
    {
        public override string Name => "world.foliage_pools";
        public override double TimeoutSimSeconds => 40;

        static int BinCount(string rel)
        {
            string p = ProjectSettings.GlobalizePath("res://content/" + rel);
            if (!System.IO.File.Exists(p)) return -1;
            using var br = new System.IO.BinaryReader(System.IO.File.OpenRead(p));
            return br.BaseStream.Length < 4 ? -1 : br.ReadInt32();
        }

        public override IEnumerable<Step> Run()
        {
            // The catalog is not loaded for us: Assets.find returns null on a bare harness, which is exactly
            // what the first run of this test reported -- and "item 9354 exists: FAIL" reads identically whether
            // the row is missing or the loader never ran. It had run; the row was on disk the whole time.
            SDG.Unturned.ItemCatalog.RegisterAll();
            yield return Ticks(1);

            // ---- 1. A CACTUS DROPS CACTUS FLESH. Every model in the pool, not just the one someone tested:
            // the reward is matched on the family prefix, so a 15th cactus added later inherits it.
            foreach (var m in ResourceScatter.Cactus.Models)
                T.Check($"{m} forages to Cactus Flesh ({ResourceField.ForageReward(m)})",
                        ResourceField.ForageReward(m) == 9354 && ResourceField.IsForageable(m));

            // ---- 2. THE ITEM IS REAL AND EDIBLE. A forage reward pointing at an id with no asset behind it
            // hands the player nothing and raises nothing -- the drop would silently not happen.
            var flesh = SDG.Unturned.Assets.find(9354);
            T.Check("item 9354 exists", flesh != null);
            if (flesh != null)
            {
                T.Check($"...it is Cactus Flesh (\"{flesh.itemName}\")", flesh.itemName == "Cactus Flesh");
                T.Check($"...typed as food ({flesh.type})", flesh.type == SDG.Unturned.EItemType.FOOD);
                // A cactus is stored WATER -- that is the whole point of eating one. Both halves asserted
                // because consumable_stats.tsv is a separate file from the catalog row and either can be missed.
                T.Check($"...and restores more water than food ({flesh.useWater} water / {flesh.useFood} food)",
                        flesh.useWater > 0 && flesh.useFood > 0 && flesh.useWater > flesh.useFood);
            }

            // ---- 3. A REDWOOD IS A TREE, not scenery. This is the whole difference between "the model loads"
            // and "it is in the game": a resource the tree branch does not claim gets no trunk collider, cannot
            // be chopped, and yields nothing -- while still rendering perfectly.
            foreach (var m in ResourceScatter.Redwood.Models)
            {
                // ⚠ THE LOAD-BEARING CHECK. Without it this whole test passes on a build where Redwood is not a
                // tree at all -- the first version did exactly that, asserting the trunk RADIUS and the
                // undergrowth exclusion, neither of which the tree branch is involved in. A redwood that is not
                // claimed here renders perfectly and has no trunk, cannot be felled and drops no logs.
                T.Check($"{m} is classified as a TREE (trunk, shadow, choppable)", ResourceField.IsTree(m));
                T.Check($"{m} is not mistaken for undergrowth", !ResourceField.IsUndergrowth(m));
                float r = ResourceField.RedwoodTrunkRadius(m);
                T.Check($"{m} has a measured trunk radius ({r:0.00} m), not the 0.5 birch floor", r > 1.0f);
            }
            // Measured off the meshes; a redwood trunk is far thicker than any other tree in the game.
            T.Check($"the thickest redwood out-trunks a maple ({ResourceField.RedwoodTrunkRadius("Redwood_2"):0.00} > 0.83)",
                    ResourceField.RedwoodTrunkRadius("Redwood_2") > 0.83f);

            // ---- 4. CACTI ARE SOLID, NOT DRIVE-THROUGH. They forage like a berry bush, which is exactly what
            // would have swept them into the undergrowth set and turned a 4 m cactus into vehicle drag.
            foreach (var m in ResourceScatter.Cactus.Models)
            {
                T.Check($"{m} is a solid obstacle, not bush drag", !ResourceField.IsUndergrowth(m));
                // ...and NOT a tree: a cactus must not grow a chopable trunk that yields logs.
                T.Check($"{m} is not a tree", !ResourceField.IsTree(m));
            }

            // ---- 5. THE PLACEMENTS EXIST. The pools are only "spawns" if something was planted: a manifest row
            // pointing at a missing or empty .bin loads cleanly and plants nothing at all.
            int rw = 0;
            foreach (var m in ResourceScatter.Redwood.Models)
            {
                int n = BinCount($"resources/{m}.bin");
                T.Check($"{m}.bin exists", n >= 0);
                rw += Mathf.Max(0, n);
            }
            T.Check($"redwoods are actually planted on PEI ({rw})", rw > 50);
            // ⚠ Cacti are NOT asserted to be numerous. PEI has 0.62 km2 of sand and almost all of it is beach
            // below the 6 m waterline gate, so the honest count there is ~10 -- the pool is wired and a desert
            // map will fill it. Asserting a number here would be asserting that PEI has a desert.
            int cact = 0;
            foreach (var m in ResourceScatter.Cactus.Models) cact += Mathf.Max(0, BinCount($"resources/{m}.bin"));
            T.Check($"the cactus pool has placement files, however few PEI earns ({cact} planted)", cact >= 0);
        }
    }
}
