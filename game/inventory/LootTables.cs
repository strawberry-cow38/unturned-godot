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

        /// <summary>A CASH REGISTER HOLDS CASH (strawberry 2026-09-15: "change the loot table of the cash
        /// registers to spawn money inside").
        ///
        /// ⚠ This is a VIRTUAL table id, deliberately outside every real map's range, and it has to be: the real
        /// tables are parsed from each map's OWN Spawns/Items.dat and the COUNT differs per map, so a synthetic
        /// table appended to the loaded array would have a different index on PEI than on Washington and the
        /// register's entry could not name one number.
        ///
        /// Why not a real table: PEI has exactly one containing money -- 24 "Booty", a single tier of
        /// {1056, 1057} -- so pointing tills at it makes every register in the world hand back a loonie or a
        /// toonie, which is why it was passed over when these props were first wired. This spreads the
        /// denominations instead, weighted like a till: mostly small change, a note now and then.
        ///
        /// Every id here converts at FACE VALUE on pickup (Items.tryAddItem -> Currency), so a rolled $20 note
        /// really is $20 and the whole till collapses into ONE wallet stack rather than filling the grid.</summary>
        public const int CashRegister = 1000;

        static readonly (float chance, ushort[] ids)[] CashRegisterTiers =
        {
            (0.40f, new ushort[] { 1056, 1057 }),   // $1 loonie, $2 toonie -- the float in the drawer
            (0.32f, new ushort[] { 1051 }),         // $5
            (0.18f, new ushort[] { 1052 }),         // $10
            (0.08f, new ushort[] { 1053 }),         // $20
            (0.02f, new ushort[] { 1054, 1055 }),   // $50, $100 -- rare, so a till is worth opening but not a jackpot
        };

        // ---- test hooks (L1 loot-projection tests need a deterministic table without a real Items.dat) ----
        public static void ResetForTests() { _loaded = false; _tiers = null; _names = null; }
        public static void LoadTiersForTests((float chance, ushort[] ids)[][] tiers, string[] names) { _tiers = tiers; _names = names; _loaded = true; }
        public static string TableName(int t) => t == CashRegister ? "Cash Register"
            : _names != null && t >= 0 && t < _names.Length ? _names[t] : $"table {t}";

        public static void Load(string itemsDatPath)
        {
            if (_loaded) return;
            _loaded = true;
            if (!System.IO.File.Exists(itemsDatPath)) { Log.Err($"[loot-tables] not found: {itemsDatPath}"); return; }
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
            Log.Print($"[loot-tables] loaded {tcount} PEI item tables");
        }

        // roll one item id from a table: weighted tier pick (by chance), uniform id within the tier. -1 = nothing.
        public static int Roll(int table) => Roll(table, _rng);

        /// <summary>Roll from a CALLER-SUPPLIED stream, so a container can own its own deterministic sequence
        /// (LootSeed). The parameterless form keeps the shared static for callers that genuinely want "any
        /// item" -- the airdrop spawner, the give console -- rather than reproducible contents.</summary>
        public static int Roll(int table, RandomNumberGenerator rng)
        {
            rng ??= _rng;
            // The virtual table is answered BEFORE the bounds check, which would otherwise reject it as
            // out-of-range -- and it needs no loaded Items.dat, so a till is stocked on any map.
            var tiers = table == CashRegister ? CashRegisterTiers
                      : (_tiers == null || table < 0 || table >= _tiers.Length) ? null : _tiers[table];
            if (tiers == null || tiers.Length == 0) return -1;
            float total = 0f; foreach (var t in tiers) total += t.chance;
            int pick = tiers.Length - 1;
            if (total > 0f) { float acc = rng.Randf() * total; for (int i = 0; i < tiers.Length; i++) { acc -= tiers[i].chance; if (acc <= 0f) { pick = i; break; } } }
            else pick = rng.RandiRange(0, tiers.Length - 1);
            var ids = tiers[pick].ids;
            if (ids == null || ids.Length == 0) return -1;
            return ids[rng.RandiRange(0, ids.Length - 1)];
        }
    }
}
