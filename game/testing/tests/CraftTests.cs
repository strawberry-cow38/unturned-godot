using Godot;
using SDG.Unturned;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // Port of --crafttest: parse a real item .dat's Blueprints, resolve ingredient GUIDs through the catalog, and
    // prove the craft logic (consume inputs, keep tools, produce outputs) on both the mock and the real grid inventory.
    public class CraftBlueprintsResolve : GameTest
    {
        public override string Name => "craft.blueprints_resolve";
        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();   // populates the item GUID->id map used to resolve blueprint ingredients
            string path = ProjectSettings.GlobalizePath("res://content/eaglefire.dat");
            T.Check("eaglefire.dat bundled", System.IO.File.Exists(path));
            var d = new DatParser().Parse(System.IO.File.ReadAllText(path));
            var bps = BlueprintDef.ParseAll(d, "4");
            T.Check($"eaglefire.dat parses blueprints (got {bps.Count})", bps.Count > 0);
            int resolved = 0, total = 0;
            foreach (var bp in bps)
                foreach (var ing in bp.Inputs) { total++; if (Assets.findByGuid(ing.Guid) != null) resolved++; }
            T.Check($"all ingredient GUIDs resolve to item ids ({resolved}/{total})", total > 0 && resolved == total);

            // craft LOGIC against a mock inventory, using eaglefire's real Repair blueprint (4 Metal Scrap + Blowtorch tool)
            var repair = bps.Find(b => b.Operation == "RepairTargetItem");
            T.Check("eaglefire has a RepairTargetItem blueprint", repair != null);
            var inv = new Crafting.DictInv(); inv.Add(67, 4); inv.Add(76, 1);   // Metal Scrap x4 + Blowtorch x1
            T.Check("CanCraft with 4 scrap + blowtorch", Crafting.CanCraft(repair, inv, out _));
            Crafting.DoCraft(repair, inv);
            T.Check("craft consumed the scrap (0 left)", inv.Count(67) == 0);
            T.Check("craft kept the blowtorch (tool)", inv.Count(76) == 1);
            var inv2 = new Crafting.DictInv(); inv2.Add(67, 2); inv2.Add(76, 1);
            T.Check("CanCraft false with only 2 scrap", !Crafting.CanCraft(repair, inv2, out _));

            // outputs path: a synthetic Craft that turns 2 scrap -> 1 blowtorch
            var scrapA = Assets.find(67); var torchA = Assets.find(76);
            T.Check("scrap + blowtorch assets resolve", scrapA != null && torchA != null);
            var inv3 = new Crafting.DictInv(); inv3.Add(67, 2);
            var synth = new BlueprintDef { Operation = "Craft", Name = "synthetic" };
            synth.Inputs.Add(new BlueprintDef.Ingredient { Guid = scrapA.guid, Amount = 2, Consume = true });
            synth.Outputs.Add(new BlueprintDef.Ingredient { Guid = torchA.guid, Amount = 1, Consume = true });
            Crafting.DoCraft(synth, inv3);
            T.Check("outputs: 2 scrap -> 0 scrap + 1 blowtorch produced", inv3.Count(67) == 0 && inv3.Count(76) == 1);

            // craft against the REAL grid PlayerInventory via the adapter (in-game integration)
            var pinv = new PlayerInventory();
            pinv.tryAddItem(new Item(67, 4));
            pinv.tryAddItem(new Item(76, 1));
            var padapt = new Crafting.PlayerInvAdapter(pinv);
            T.Check("PlayerInventory adapter CanCraft", Crafting.CanCraft(repair, padapt, out _));
            Crafting.DoCraft(repair, padapt);
            T.Check("PlayerInventory craft: scrap consumed, blowtorch kept", pinv.getItemCount(67) == 0 && pinv.getItemCount(76) == 1);

            // blueprint REGISTRY: the pre-extracted catalog loads + lists craftables from a stocked inventory
            int loaded = BlueprintRegistry.Load();
            T.Check($"blueprint registry loads (got {loaded})", loaded > 0);
            var stock = new Crafting.DictInv(); stock.Add(67, 50); stock.Add(76, 1);   // 50 Metal Scrap + Blowtorch
            T.Check("something is craftable from 50 scrap + blowtorch", BlueprintRegistry.Applicable(stock).Count > 0);
            yield break;
        }
    }

    // Port of --craftgate: a blueprint requiring CRAFTING level 2 is blocked below it + allowed at/above it.
    public class CraftSkillGate : GameTest
    {
        public override string Name => "craft.skill_gate";
        public override IEnumerable<Step> Run()
        {
            var skills = new PlayerSkills();
            var bp = new BlueprintDef { Skill = "Craft", SkillLevel = 2 };   // requires CRAFTING >= 2
            var craft = skills.GetSkill((int)EPlayerSpeciality.SUPPORT, (int)EPlayerSupport.CRAFTING);

            T.Check("blocked at CRAFTING 0", !Crafting.MeetsSkill(bp, skills));
            craft.level = 1;
            T.Check("blocked at CRAFTING 1", !Crafting.MeetsSkill(bp, skills));
            craft.level = 2;
            T.Check("allowed at CRAFTING 2", Crafting.MeetsSkill(bp, skills));
            craft.level = 5;
            T.Check("allowed at CRAFTING 5", Crafting.MeetsSkill(bp, skills));
            T.Check("no-skill blueprint always ok", Crafting.MeetsSkill(new BlueprintDef { Skill = "", SkillLevel = 0 }, new PlayerSkills()));
            T.Check("null skills = ungated", Crafting.MeetsSkill(bp, null));
            yield break;
        }
    }
}
