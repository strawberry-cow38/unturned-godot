using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace SDG.Unturned
{
    /// <summary>
    /// Per-LOOT-TABLE condition bias (strawberry 2026-10-06: "loot spawns at random condition yes, with some way to
    /// weight condition towards better/worse in the editors spawn tables"). -1 = most spawns near the table's worst,
    /// 0 = uniform (retail), +1 = most near its best -- the bend is Durability.RollCondition's. Keyed by the Items.dat
    /// table index, which is the same number the editor's item-spawn "Type" picker shows and the same one container
    /// loot (StoreShelf.RollInto) rolls from, so one setting covers a table's ground spawns and its shelves.
    ///
    /// One map's settings at a time, loaded when that map loads (WorldBuilder for PEI, EditorPlayMode for a custom map,
    /// the editor on open) from content/spawns/editor_&lt;map&gt;_lootcond.txt: one "table bias" pair per line. A table
    /// not in the file is 0. Static because every loot roll on the process -- client field, server shelves -- asks it.
    /// </summary>
    public static class LootCondition
    {
        static readonly Dictionary<int, float> _bias = new Dictionary<int, float>();

        /// <summary>Biases declared in CODE for tables that only exist in code (a fridge, a garbage bag). ⚠ Kept
        /// SEPARATE from `_bias` because Load() clears that dictionary on every map load -- a virtual table has no
        /// row in any map's file, so anything registered into `_bias` would be wiped the moment a map opened and
        /// the fridge would quietly go back to rolling uniform condition.
        ///
        /// ⭐ The file still WINS where it says something: a mapper who sets a bias for a table in the editor is
        /// making a decision about THEIR map, and a default in our source should not override it.</summary>
        static readonly Dictionary<int, float> _codeDefault = new Dictionary<int, float>();

        public static void SetCodeDefault(int table, float bias)
        {
            if (table < 0) return;
            _codeDefault[table] = Math.Clamp(bias, -1f, 1f);
        }

        public static float Bias(int table)
            => table < 0 ? 0f
             : _bias.TryGetValue(table, out var b) ? b
             : _codeDefault.TryGetValue(table, out var d) ? d
             : 0f;

        public static void Set(int table, float bias)
        {
            if (table < 0) return;
            bias = Math.Clamp(bias, -1f, 1f);
            if (Math.Abs(bias) < 0.005f) _bias.Remove(table); else _bias[table] = bias;
        }

        public static void Clear() => _bias.Clear();
        public static int Count => _bias.Count;

        /// <summary>Replace the current settings with the file's (none if it does not exist).</summary>
        public static void Load(string path)
        {
            _bias.Clear();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            foreach (var line in File.ReadAllLines(path))
            {
                var p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 2 || p[0].StartsWith("#")) continue;
                if (int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int t)
                    && float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float b))
                    Set(t, b);
            }
        }

        public static void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var lines = new List<string> { "# loot table index, condition bias (-1 worse .. +1 better)" };
            var keys = new List<int>(_bias.Keys);
            keys.Sort();
            foreach (var t in keys) lines.Add(t.ToString(CultureInfo.InvariantCulture) + " " + _bias[t].ToString("0.###", CultureInfo.InvariantCulture));
            File.WriteAllLines(path, lines);
        }
    }
}
