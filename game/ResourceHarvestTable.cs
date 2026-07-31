using Godot;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SDG.Unturned;

namespace UnturnedGodot
{
    /// <summary>
    /// Loads content/resources_harvest.tsv -- every retail resource's health, reward table, XP and regrow
    /// time, baked by tools/extract_resource_harvest.py.
    ///
    /// The drops column is already RESOLVED to item ids with weights. Retail's Reward_ID is a legacy spawn
    /// TABLE id, not an item id (birch's 515 is a table; item 515 is Cooked Venison), and doing that
    /// resolution at bake time means nothing at runtime can get it wrong.
    /// </summary>
    public static class ResourceHarvestTable
    {
        static Dictionary<string, ResourceHarvestDef> _byName;

        /// <summary>Defs keyed by resource type name ("Birch_0"), loaded once.</summary>
        public static IReadOnlyDictionary<string, ResourceHarvestDef> ByName => _byName ??= Load();

        static Dictionary<string, ResourceHarvestDef> Load()
        {
            var map = new Dictionary<string, ResourceHarvestDef>();
            string path = ProjectSettings.GlobalizePath("res://content/resources_harvest.tsv");
            if (!File.Exists(path)) { GD.Print("[resources] no resources_harvest.tsv -- nothing is choppable"); return map; }
            var ci = CultureInfo.InvariantCulture;
            bool header = true;
            foreach (var line in File.ReadAllLines(path))
            {
                if (header) { header = false; continue; }
                var c = line.Split('\t');
                if (c.Length < 13) continue;
                var def = new ResourceHarvestDef
                {
                    AssetId = ushort.TryParse(c[1], out var a) ? a : (ushort)0,
                    Health = ushort.TryParse(c[2], out var h) ? h : (ushort)0,
                    RewardXp = uint.TryParse(c[3], out var xp) ? xp : 0u,
                    ResetSeconds = float.TryParse(c[4], NumberStyles.Float, ci, out var rs) ? rs : 0f,
                    RewardMin = byte.TryParse(c[5], out var lo) ? lo : (byte)0,
                    RewardMax = byte.TryParse(c[6], out var hi) ? hi : (byte)0,
                    HasDebris = c[7] == "1",
                    IsForage = c[8] == "1",
                    BladeId = byte.TryParse(c[9], out var b) ? b : (byte)0,
                    VulnerableToAllMelee = c[10] == "1",
                    VulnerableToFists = c[11] == "1",
                    Drops = ParseDrops(c[12]),
                };
                map[c[0]] = def;
            }
            return map;
        }

        /// <summary>"37:60|38:40" -> the weighted table. A malformed entry is skipped rather than defaulted
        /// to item 0, which would drop a phantom item nobody can name.</summary>
        static (ushort Item, int Weight)[] ParseDrops(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return System.Array.Empty<(ushort, int)>();
            var outp = new List<(ushort, int)>();
            foreach (var part in s.Split('|', System.StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split(':');
                if (kv.Length != 2) continue;
                if (!ushort.TryParse(kv[0], out var id) || id == 0) continue;
                if (!int.TryParse(kv[1], out var w) || w <= 0) continue;
                outp.Add((id, w));
            }
            return outp.ToArray();
        }

        /// <summary>Register every def, then bind each placed instance to its type. Both halves are needed:
        /// the defs say what a birch drops, the instance map says which trees ARE birches.</summary>
        public static int Bind(ResourceHarvestSim sim, ResourceField field)
        {
            if (sim == null || field == null) return 0;
            foreach (var kv in ByName) sim.RegisterDef(kv.Value);
            int bound = 0;
            for (int i = 0; i < field.InstanceCount; i++)
            {
                string name = field.TypeNameOf(i);
                if (name == null || !ByName.TryGetValue(name, out var def)) continue;
                sim.RegisterInstance(i, def.AssetId);
                bound++;
            }
            return bound;
        }
    }
}
