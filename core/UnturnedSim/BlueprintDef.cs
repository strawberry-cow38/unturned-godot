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
        /// <summary>Seconds this recipe takes, from an optional 9th TSV column. 0 = unset, and the caller falls
        /// back to its base time. Data rather than code because the time is a property OF THE RECIPE, and the
        /// alternative is a switch in the crafting menu that has to be edited every time a row is added.
        /// Retail's 1875 extracted rows have 8 columns and parse unchanged.</summary>
        public float Seconds;

        public bool RequiresStation => StationTags.Count > 0;
        public bool RequiresSkill => !string.IsNullOrEmpty(Skill) && SkillLevel > 0;

        // ---- BLUEPRINT KNOWLEDGE (strawberry 2026-10-04: "certain recipes arent known by default. unlocked by either
        // directly learning or via skill unlocks ... learned blueprints should be tracked per player") ----

        /// <summary>How this recipe becomes KNOWN, from an optional 10th TSV column, pipe-separated. EMPTY = known by
        /// everyone from the start, which is every row that existed before this column did. Otherwise the recipe is
        /// LOCKED until one of these fires:
        ///   skill:&lt;skill&gt;:&lt;level&gt;   learned automatically when that skill reaches the level (PlayerSkills.TryFind names)
        ///   item:&lt;itemId&gt;           using that item (a schematic, a manual) teaches it, and the item is spent
        ///   learn                    nothing in-world: only the admin console's `learn` grants it
        /// The skill names are TRIGGERS, read at the moment they fire, and never what gets saved -- the saved fact is
        /// the recipe's Key. So replacing the skill set changes which strings in here fire, and loses nobody's
        /// knowledge.</summary>
        public readonly List<string> Unlocks = new();
        public bool Locked => Unlocks.Count > 0;

        string _key;
        /// <summary>The recipe's STABLE identity: what a player's knowledge is saved and replicated under. An explicit
        /// 11th TSV column wins; otherwise it is derived from what the recipe IS (owner, operation, inputs, outputs),
        /// never from its ROW -- a row index renumbers the moment anyone inserts a recipe above it, and every save
        /// would then name the wrong recipe without a single error.</summary>
        public string Key { get => _key ??= DerivedKey(); set => _key = value; }

        string DerivedKey()
        {
            // FNV-1a over the content. Order-preserving on purpose: the same ingredients in another order is a
            // different row in the TSV and will not be produced by accident.
            uint h = 2166136261;
            void Mix(string s) { foreach (char ch in s ?? "") { h ^= ch; h *= 16777619; } h ^= '|'; h *= 16777619; }
            Mix(OwnerItemId); Mix(Operation);
            foreach (var i in Inputs) Mix($"{i.Guid}:{i.Amount}:{(i.Consume ? 1 : 0)}");
            Mix(">");
            foreach (var o in Outputs) Mix($"{o.Guid}:{o.Amount}");
            return $"bp-{OwnerItemId}-{h:x8}";
        }

        /// <summary>What a key may look like. It arrives over the wire and out of a save file, so it is checked
        /// rather than trusted: 1-64 of [A-Za-z0-9_.:-].</summary>
        public static bool IsValidKey(string k)
        {
            if (string.IsNullOrEmpty(k) || k.Length > 64) return false;
            foreach (char c in k)
                if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '.' or ':' or '-')) return false;
            return true;
        }

        /// <summary>The item ids whose use teaches this recipe (its `item:` unlocks).</summary>
        public IEnumerable<ushort> TaughtByItems()
        {
            foreach (var u in Unlocks)
                if (u.StartsWith("item:", System.StringComparison.OrdinalIgnoreCase) && ushort.TryParse(u.Substring(5), out var id) && id != 0)
                    yield return id;
        }

        /// <summary>The (skill, level) pairs that unlock this recipe (its `skill:` unlocks).</summary>
        public IEnumerable<(string skill, int level)> SkillUnlocks()
        {
            foreach (var u in Unlocks)
            {
                if (!u.StartsWith("skill:", System.StringComparison.OrdinalIgnoreCase)) continue;
                var p = u.Split(':');
                if (p.Length == 3 && p[1].Length > 0 && int.TryParse(p[2], out int lv)) yield return (p[1], lv);
            }
        }

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
        //   | seconds (optional) | unlocks (optional, see Unlocks) | key (optional, see Key)
        //   inputs = guid:amount:consume(1|0) pipe-sep ; outputs = guid:amount pipe-sep ; stations = guid pipe-sep
        public string ToTsv()
        {
            var ins = new List<string>();
            foreach (var i in Inputs) ins.Add($"{i.Guid}:{i.Amount}:{(i.Consume ? 1 : 0)}");
            var outs = new List<string>();
            foreach (var o in Outputs) outs.Add($"{o.Guid}:{o.Amount}");
            string name = (Name ?? "").Replace('\t', ' ');
            var cols = new List<string> { OwnerItemId, Operation, name, Skill ?? "", SkillLevel.ToString(),
                                          string.Join("|", ins), string.Join("|", outs), string.Join("|", StationTags) };
            // The optional tail only when it carries something, so an 8-column retail row round-trips as 8 columns.
            if (Seconds > 0f || Locked || _key != null)
                cols.Add(Seconds > 0f ? Seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) : "");
            if (Locked || _key != null) cols.Add(string.Join("|", Unlocks));
            if (_key != null) cols.Add(_key);
            return string.Join("\t", cols);
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
            if (c.Length > 8 && float.TryParse(c[8], System.Globalization.NumberStyles.Float,
                                               System.Globalization.CultureInfo.InvariantCulture, out float secs) && secs > 0f)
                bp.Seconds = secs;
            if (c.Length > 9)
                foreach (var u in c[9].Split('|')) { var t = u.Trim(); if (t.Length > 0) bp.Unlocks.Add(t); }
            if (c.Length > 10 && IsValidKey(c[10].Trim())) bp._key = c[10].Trim();
            return bp;
        }
    }
}
