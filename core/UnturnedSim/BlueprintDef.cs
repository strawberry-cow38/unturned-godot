using System.Collections.Generic;
using SDG.Unturned;   // DatParser, IDatDictionary, IDatList, IDatNode (the ported UnturnedDat)

namespace UnturnedGodot
{
    // A crafting blueprint parsed from an item's .dat "Blueprints" list (modern v2 nested-GUID format).
    // Source model: Unturned/Inventory/Blueprint.cs -- InputItems (supplies) -> Outputs (products), plus an
    // Operation (Craft / RepairTargetItem / Ammo / ...), an optional Skill + Skill_Level, and
    // RequiresNearbyCraftingTags = a crafting STATION the player must be near (e.g. Workbench).
    // An input with "Delete false" is a TOOL (required present but NOT consumed).
    public sealed class BlueprintDef
    {
        public struct Ingredient { public string Guid; public int Amount; public bool Consume; }   // Consume=false => a tool

        public string Name;
        public string Operation = "Craft";     // Craft / RepairTargetItem / Ammo / ...
        public string OwnerItemId;              // numeric ID of the item whose .dat this blueprint came from
        public readonly List<Ingredient> Inputs = new();
        public readonly List<Ingredient> Outputs = new();
        public string Skill;                    // e.g. "Craft" / "Repair" (blank = no skill requirement)
        public int SkillLevel;
        public readonly List<string> StationTags = new();   // RequiresNearbyCraftingTags GUIDs (blank = craft anywhere)

        public bool RequiresStation => StationTags.Count > 0;
        public bool RequiresSkill => !string.IsNullOrEmpty(Skill) && SkillLevel > 0;

        // Parse every blueprint out of an already-parsed item .dat. ownerId = the item's numeric "ID".
        public static List<BlueprintDef> ParseAll(IDatDictionary d, string ownerId)
        {
            var result = new List<BlueprintDef>();
            IDatList bps = d?.GetList("Blueprints");
            if (bps == null) return result;
            foreach (IDatNode n in bps)
            {
                if (n is not IDatDictionary bp) continue;
                var def = new BlueprintDef
                {
                    Name = bp.GetString("Name"),
                    Operation = bp.GetString("Operation", "Craft"),
                    OwnerItemId = ownerId,
                    Skill = bp.GetString("Skill"),
                    SkillLevel = bp.ParseInt32("Skill_Level", 0),
                };
                ReadIngredients(bp.GetList("InputItems"), def.Inputs);
                // modern craft blueprints list products under "Outputs"; older/other layouts use "SupplyItems"/"Products"
                ReadIngredients(bp.GetList("Outputs") ?? bp.GetList("Products"), def.Outputs);
                IDatList stations = bp.GetList("RequiresNearbyCraftingTags");
                if (stations != null)
                    for (int i = 0; i < stations.Count; i++)
                    {
                        string tag = stations.GetString(i);
                        if (!string.IsNullOrEmpty(tag)) def.StationTags.Add(tag);
                    }
                result.Add(def);
            }
            return result;
        }

        static void ReadIngredients(IDatList list, List<Ingredient> into)
        {
            if (list == null) return;
            foreach (IDatNode n in list)
            {
                if (n is not IDatDictionary it) continue;
                string id = it.GetString("ID");
                if (string.IsNullOrEmpty(id)) continue;
                // "Delete false" marks a tool that must be present but is NOT consumed; absent => consumed.
                bool consume = !string.Equals(it.GetString("Delete", "true"), "false", System.StringComparison.OrdinalIgnoreCase);
                into.Add(new Ingredient { Guid = id, Amount = it.ParseInt32("Amount", 1), Consume = consume });
            }
        }

        public override string ToString()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(Operation).Append(' ').Append(Name ?? "(unnamed)").Append(": ");
            foreach (var i in Inputs) sb.Append(i.Amount).Append("x ").Append(Short(i.Guid)).Append(i.Consume ? " " : "(tool) ");
            sb.Append("-> ");
            if (Outputs.Count == 0) sb.Append("[target item]");
            foreach (var o in Outputs) sb.Append(o.Amount).Append("x ").Append(Short(o.Guid)).Append(' ');
            if (RequiresSkill) sb.Append("| skill ").Append(Skill).Append(' ').Append(SkillLevel);
            if (RequiresStation) sb.Append("| @station(").Append(StationTags.Count).Append(')');
            return sb.ToString();
        }

        static string Short(string guid) => string.IsNullOrEmpty(guid) || guid.Length < 8 ? guid : guid.Substring(0, 8);

        // TSV (de)serialization for the pre-extracted blueprint catalog (content/blueprints.tsv), since the port
        // bundles only a few item .dats. Layout: ownerId | operation | name | skill | skillLevel | inputs | outputs | stations
        //   inputs = guid:amount:consume(1|0) pipe-sep ; outputs = guid:amount pipe-sep ; stations = guid pipe-sep
        public string ToTsv()
        {
            var ins = new List<string>();
            foreach (var i in Inputs) ins.Add($"{i.Guid}:{i.Amount}:{(i.Consume ? 1 : 0)}");
            var outs = new List<string>();
            foreach (var o in Outputs) outs.Add($"{o.Guid}:{o.Amount}");
            string name = (Name ?? "").Replace('\t', ' ');
            return string.Join("\t", OwnerItemId, Operation, name, Skill ?? "", SkillLevel.ToString(),
                                string.Join("|", ins), string.Join("|", outs), string.Join("|", StationTags));
        }

        public static BlueprintDef FromTsv(string line)
        {
            var c = line.Split('\t');
            if (c.Length < 8) return null;
            var bp = new BlueprintDef { OwnerItemId = c[0], Operation = c[1], Name = c[2], Skill = c[3] };
            int.TryParse(c[4], out int lvl); bp.SkillLevel = lvl;
            foreach (var s in c[5].Split('|')) { if (string.IsNullOrEmpty(s)) continue; var p = s.Split(':'); if (p.Length >= 3 && int.TryParse(p[1], out int a)) bp.Inputs.Add(new Ingredient { Guid = p[0], Amount = a, Consume = p[2] == "1" }); }
            foreach (var s in c[6].Split('|')) { if (string.IsNullOrEmpty(s)) continue; var p = s.Split(':'); if (p.Length >= 2 && int.TryParse(p[1], out int a)) bp.Outputs.Add(new Ingredient { Guid = p[0], Amount = a, Consume = true }); }
            foreach (var s in c[7].Split('|')) { if (!string.IsNullOrEmpty(s)) bp.StationTags.Add(s); }
            return bp;
        }
    }
}
