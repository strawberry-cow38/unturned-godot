using Godot;

namespace UnturnedGodot
{
    // Shared PEI item drop tables (Spawns/Items.dat), loaded once + rolled by table index. Same binary format + weighted
    // tier/id roll as LootField, but reusable for lootable containers (LootCrate) rather than ground spawn points.
    public static class LootTables
    {
        static (float chance, ushort[] ids)[][] _tiers;
        static string[] _names;
        static bool _loaded;
        static readonly RandomNumberGenerator _rng = new();

        public static bool Loaded => _loaded && _tiers != null;
        public static int TableCount => _tiers?.Length ?? 0;
        public static string TableName(int t) => _names != null && t >= 0 && t < _names.Length ? _names[t] : $"table {t}";

        public static void Load(string itemsDatPath)
        {
            if (_loaded) return;
            _loaded = true;
            if (!System.IO.File.Exists(itemsDatPath)) { GD.PrintErr($"[loot-tables] not found: {itemsDatPath}"); return; }
            var b = System.IO.File.ReadAllBytes(itemsDatPath); int o = 0;
            byte U8() => b[o++];
            ushort U16() { var v = System.BitConverter.ToUInt16(b, o); o += 2; return v; }
            float F32() { var v = System.BitConverter.ToSingle(b, o); o += 4; return v; }
            string RStr() { int n = U8(); var s = System.Text.Encoding.UTF8.GetString(b, o, n); o += n; return s; }
            byte ver = U8();
            if (ver > 1 && ver < 3) o += 8;   // SteamID
            byte tcount = U8();
            _names = new string[tcount]; _tiers = new (float, ushort[])[tcount][];
            for (int t = 0; t < tcount; t++)
            {
                o += 3;   // table editor colour (RGB)
                _names[t] = RStr().Replace('_', ' ');
                if (ver > 3) o += 2;   // tableID
                byte tiers = U8();
                _tiers[t] = new (float, ushort[])[tiers];
                for (int ti = 0; ti < tiers; ti++)
                {
                    RStr();   // tier name
                    float chance = F32();
                    byte sc = U8();
                    var ids = new ushort[sc];
                    for (int s = 0; s < sc; s++) ids[s] = U16();
                    _tiers[t][ti] = (chance, ids);
                }
            }
            GD.Print($"[loot-tables] loaded {tcount} PEI item tables");
        }

        // roll one item id from a table: weighted tier pick (by chance), uniform id within the tier. -1 = nothing.
        public static int Roll(int table)
        {
            if (_tiers == null || table < 0 || table >= _tiers.Length) return -1;
            var tiers = _tiers[table];
            if (tiers == null || tiers.Length == 0) return -1;
            float total = 0f; foreach (var t in tiers) total += t.chance;
            int pick = tiers.Length - 1;
            if (total > 0f) { float acc = _rng.Randf() * total; for (int i = 0; i < tiers.Length; i++) { acc -= tiers[i].chance; if (acc <= 0f) { pick = i; break; } } }
            else pick = _rng.RandiRange(0, tiers.Length - 1);
            var ids = tiers[pick].ids;
            if (ids == null || ids.Length == 0) return -1;
            return ids[_rng.RandiRange(0, ids.Length - 1)];
        }
    }
}
