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
            var ag = skills.GetSkill((int)EPlayerSpeciality.SUPPORT, (int)EPlayerSupport.AGRICULTURE);

            ag.level = 0; T.Check("mastery 0 at agri 0", ag.Mastery == 0f);
            ag.level = 7; T.Check("mastery 1.0 at agri max", Mathf.Abs(ag.Mastery - 1f) < 0.001f);
            ag.level = 0; int f0 = 0; for (int i = 0; i < 2000; i++) if (T.Rng.Randf() < ag.Mastery) f0++;
            T.Check("no 2nd-yield at agri 0", f0 == 0);
            ag.level = 7; int f1 = 0; for (int i = 0; i < 2000; i++) if (T.Rng.Randf() < ag.Mastery) f1++;
            T.Check("always 2nd-yield at agri max", f1 == 2000);
            ag.level = 4; int f4 = 0; for (int i = 0; i < 4000; i++) if (T.Rng.Randf() < ag.Mastery) f4++;
            float rate = f4 / 4000f;   // mastery 4/7 ~= 0.571
            T.Check($"~57% 2nd-yield at agri 4 (got {rate:0.00})", Mathf.Abs(rate - 4f / 7f) < 0.05f);
            yield break;
        }
    }
}
