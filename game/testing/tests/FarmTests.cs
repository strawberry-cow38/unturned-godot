using Godot;
using SDG.Unturned;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // Port of --farmtest: a planted crop grows over FarmDef.Growth seconds, then harvest yields FarmDef.Grow
    // (source InteractableFarm); fresh crops yield nothing and GrowthFraction is linear.
    public class FarmGrowHarvest : GameTest
    {
        public override string Name => "farm.grow_harvest";
        public override IEnumerable<Step> Run()
        {
            FarmRegistry.Load();
            T.Check($"farm registry loads crops (got {FarmRegistry.Count})", FarmRegistry.Count > 0);
            T.Check("Carrot Seed (330) is a seed", FarmRegistry.IsSeed(330));
            T.Check("Carrot Seed def resolves", FarmRegistry.TryGet(330, out var carrot) && carrot.Growth > 0);
            FarmRegistry.TryGet(330, out var def);
            var crop = new PlantedCrop { Def = def, PlantedAt = 0.0 };
            T.Check("just planted -> not grown, no yield", !crop.IsFullyGrown(5.0) && crop.Harvest(5.0) == 0);
            double t = def.Growth + 1.0;
            ushort yield = crop.Harvest(t);
            T.Check($"grown -> harvest yields Grow item {def.Grow} (got {yield})", crop.IsFullyGrown(t) && yield == def.Grow && yield != 0);
            float half = crop.GrowthFraction(def.Growth / 2.0);
            T.Check($"growth fraction linear (half = {half:0.00})", Mathf.Abs(half - 0.5f) < 0.01f);
            yield break;
        }
    }

    // Port of --farmloop: the plant->grow->harvest loop across the crops.tsv<->farms.tsv seed linkage for the
    // staple crops (carrot/wheat/tomato/potato).
    public class FarmCropsLoop : GameTest
    {
        public override string Name => "farm.crops_loop";
        public override IEnumerable<Step> Run()
        {
            CropRegistry.Load();
            FarmRegistry.Load();
            foreach (var cropName in new[] { "carrot", "wheat", "tomato", "potato" })
            {
                if (!CropRegistry.TryByName(cropName, out var cd)) { T.Fail($"{cropName}: no crops.tsv entry"); continue; }
                FarmRegistry.TryGet(cd.SeedId, out var def);
                var crop = new PlantedCrop { Def = def, PlantedAt = 0 };
                bool young = !crop.IsFullyGrown(1);
                bool grown = def.Growth > 0 && crop.IsFullyGrown(def.Growth + 1);
                ushort yield = crop.Harvest(def.Growth + 1);
                T.Check($"{cropName}: seed {cd.SeedId} young->grown->yield {yield}", young && grown && yield == def.Grow && yield != 0);
            }
            yield break;
        }
    }

    // Port of --farmyield: the agriculture-skill 2nd-yield roll (source InteractableFarm:
    // Random.value < mastery(AGRICULTURE)). Seeded via T.Rng, so the mid-mastery rate check is deterministic.
    public class FarmSecondYieldRoll : GameTest
    {
        public override string Name => "farm.second_yield_roll";
        public override IEnumerable<Step> Run()
        {
            var skills = new PlayerSkills();
            var ag = skills.GetSkill(ESkill.Plants);

            // ⚠ EXPECTATIONS DERIVED FROM ag.max, NOT HARDCODED. This test used to assert "~57%" because retail
            // AGRICULTURE had max 7 and 4/7 = 0.571. Our Plants has max 5, so the same level 4 is 0.80 -- the CODE
            // was right and the test's copied constant was the thing that was stale. A rate test that hardcodes
            // the rate re-fails every time the design moves and tells you nothing about whether the roll works.
            byte mid = (byte)(ag.max - 1);
            float expected = (float)mid / ag.max;

            ag.level = 0; T.Check("mastery 0 at Plants 0", ag.Mastery == 0f);
            ag.level = ag.max; T.Check("mastery 1.0 at Plants max", Mathf.Abs(ag.Mastery - 1f) < 0.001f);
            ag.level = 0; int f0 = 0; for (int i = 0; i < 2000; i++) if (T.Rng.Randf() < ag.Mastery) f0++;
            T.Check("no 2nd-yield at Plants 0", f0 == 0);
            ag.level = ag.max; int f1 = 0; for (int i = 0; i < 2000; i++) if (T.Rng.Randf() < ag.Mastery) f1++;
            T.Check("always 2nd-yield at Plants max", f1 == 2000);
            ag.level = mid; int f4 = 0; for (int i = 0; i < 4000; i++) if (T.Rng.Randf() < ag.Mastery) f4++;
            float rate = f4 / 4000f;
            T.Check($"~{expected:P0} 2nd-yield at Plants {mid}/{ag.max} (got {rate:0.00})",
                    Mathf.Abs(rate - expected) < 0.05f);
            yield break;
        }
    }
}
