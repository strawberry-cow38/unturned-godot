using Godot;
using System.Collections.Generic;

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

        /// <summary>A TOASTER HOLDS BREAD (master 2026-10-07: "switch the loot tables inside toasters to only
        /// spawn bread"). Virtual, for the same reason CashRegister is: the real tables are parsed per map and
        /// their count differs, so a synthetic table appended to the loaded array could not be named by one
        /// number across PEI and Washington.
        ///
        /// ⚠ Table 6 "Food" is what it used to roll, which is why toasters came out full of canned beans and
        /// MREs. One tier, one id, probability 1 -- there is nothing to weight when the answer is always bread.</summary>
        public const int Toaster = 1001;

        // ---- PER-CONTAINER TABLES (master 2026-10-07) -------------------------------------------------------
        //
        // "fridges should spawn only perishable food (good spoil %), sometimes drinks ... dishwashers should spawn
        // kitchen knives, cups, glasses, plates... ovens shouldnt spawn food, they should spawn trays, pots.
        // counters should spawn non-perishables as well as plates cups dishes... garbage bags should spawn low
        // durability melee weapons, spoiled food, tattered clothes ... add the following 'junk' to filing cabinets"
        //
        // ⭐⭐ THE FOOD TABLES ARE DERIVED FROM FoodSpoil.PerDay, NOT HAND-LISTED. "Perishable" is already a fact the
        // game knows -- FoodSpoil rates every FOOD item per in-game day from its own name (canned 2%/day, meat 22%)
        // -- so asking that function is the difference between a fridge that is right today and one that is right
        // after someone adds a food item. A hand-written id list would be a second opinion about the same question,
        // and the two would drift the first time anyone touched either.
        public const int Fridge = 1002, Freezer = 1003, Dishwasher = 1004, Oven = 1005,
                         Counter = 1006, GarbageBag = 1007, FilingCabinet = 1008, Barbecue = 1009;

        /// <summary>Spoil rate at or above which a food counts PERISHABLE -- i.e. belongs in a fridge rather than a
        /// cupboard. 5%/day sits between FoodSpoil's "dried/packaged" band (3) and its root veg (5), so canned and
        /// bagged goods fall out and anything that actually goes off falls in.</summary>
        public const float PerishableAtLeast = 5f;

        /// <summary>⭐ CONDITION BIAS PER TABLE, set in CODE because these tables only exist in code.
        /// LootCondition's biases load from a per-map file the editor writes, which a virtual table has no row in --
        /// so a fridge would roll uniform condition and master's "good spoil %" would quietly not happen.
        /// -1 = mostly worst, +1 = mostly best (tinyclaw's Durability.RollCondition bend).</summary>
        public static readonly Dictionary<int, float> VirtualBias = new()
        {
            [Fridge] = 0.65f,        // a working fridge: food in good condition
            [Freezer] = 0.85f,       // better still -- frozen is the point of it
            [Counter] = 0.25f,       // a cupboard: fine, not pristine
            [GarbageBag] = -0.85f,   // "low durability melee weapons, spoiled food, tattered clothes"
            [Oven] = 0f, [Dishwasher] = 0f, [FilingCabinet] = 0f,
        };

        // Kitchenware. Kitchen Knife is the REAL item 120, not a placeholder -- it already existed, so a second one
        // would be two items with the same name and only one of them a weapon.
        static readonly ushort[] Crockery = { 9300, 9301, 9302, 9303 };            // cup, glass, plate, bowl
        static readonly ushort[] Cutlery = { 9304, 9305, 9306 };                   // fork, spoon, table knife
        static readonly ushort[] Cookware = { 9307, 9308, 9309 };                  // pot, pan, baking tray
        // ⚠⚠ 499 AND 1328 WERE WRONG AND ARE GONE (master 2026-10-07: "\"paper\" item is a paper hat, not a sheet
        // of paper"). 499 "Paper" is a cosmetic **Hat**; 1328 "Note" is a **Barricade**, a placeable sign. I reused
        // both because the NAME matched, without reading the TYPE column sitting next to it -- so a filing cabinet
        // was handing out headwear. Replaced with real placeholders (9336, 9337).
        // ⭐ The lesson generalises past these two: when reusing an existing item, match on what it IS, not on
        // what it is called.
        static readonly ushort[] Stationery = { 9336, 9337, 9310, 9311, 9312, 9313, 9314, 9315, 9316, 9317, 9318, 9319, 9320, 9321 };
        static readonly ushort[] GarbageJunk = { 9322, 9323, 9324, 9325, 9326 };

        static readonly (float chance, ushort[] ids)[] DishwasherTiers =
        {
            (0.45f, Crockery),
            (0.35f, Cutlery),
            (0.12f, Cookware),
            (0.08f, new ushort[] { 120 }),   // the real Kitchen Knife
        };
        static readonly (float chance, ushort[] ids)[] OvenTiers =
        {
            (0.55f, new ushort[] { 9309 }),   // baking tray
            (0.45f, new ushort[] { 9307, 9308 }),   // pot, pan -- and NO food (master)
        };
        static readonly (float chance, ushort[] ids)[] FilingCabinetTiers = { (1.00f, Stationery) };

        // A BARBECUE HOLDS CHARCOAL (master 2026-10-07: "do we have charcoal added already? if not add it, if so,
        // just add it to barbeque's spawn tables"). It did not exist, so 9333 is new -- and a BBQ keeps holding the
        // grill food it already did, with the charcoal beside it rather than instead of it.
        static (float chance, ushort[] ids)[] _barbecue;

        // ---- derived-from-the-catalog tiers, built once on first use -----------------------------------------
        static (float chance, ushort[] ids)[] _fridge, _freezer, _counter, _garbage;

        static ushort[] FoodsWhere(System.Func<float, bool> rate)
        {
            var ids = new List<ushort>();
            foreach (var a in SDG.Unturned.Assets.all())
                if (a != null && a.type == SDG.Unturned.EItemType.FOOD && rate(UnturnedGodot.FoodSpoil.PerDay(a))) ids.Add(a.id);
            ids.Sort();
            return ids.ToArray();
        }
        static ushort[] OfType(SDG.Unturned.EItemType t)
        {
            var ids = new List<ushort>();
            foreach (var a in SDG.Unturned.Assets.all()) if (a != null && a.type == t) ids.Add(a.id);
            ids.Sort();
            return ids.ToArray();
        }

        /// <summary>⚠ Built LAZILY, never at static init: these read the item catalog, and LootTables is touched by
        /// code that can run before ItemCatalog.RegisterAll(). A static initialiser here would bake an empty table
        /// and every fridge on the map would roll nothing, silently.</summary>
        static (float chance, ushort[] ids)[] VirtualTiers(int table)
        {
            switch (table)
            {
                case Fridge:
                    return _fridge ??= Build(FoodsWhere(r => r >= PerishableAtLeast), OfType(SDG.Unturned.EItemType.WATER), 0.78f);
                case Freezer:
                    // Frozen: perishables only, no drinks -- a freezer is not where the cola lives.
                    return _freezer ??= Build(FoodsWhere(r => r >= PerishableAtLeast), null, 1f);
                case Counter:
                    // "non-perishables as well as plates cups dishes, pots pans utensils"
                    return _counter ??= new[]
                    {
                        (0.55f, FoodsWhere(r => r > 0f && r < PerishableAtLeast)),
                        (0.25f, Crockery),
                        (0.12f, Cutlery),
                        (0.08f, Cookware),
                    };
                case Barbecue:
                    return _barbecue ??= new[]
                    {
                        (0.45f, new ushort[] { 9333 }),                         // charcoal
                        (0.55f, FoodsWhere(r => r >= PerishableAtLeast)),       // the stuff you would grill
                    };
                case GarbageBag:
                    // "low durability melee weapons, spoiled food, tattered clothes, add a few misc random garbage"
                    // -- the CONDITION of those comes from VirtualBias[GarbageBag], not from picking different ids.
                    return _garbage ??= new[]
                    {
                        (0.40f, GarbageJunk),
                        (0.26f, FoodsWhere(r => r > 0f)),
                        (0.22f, Concat(OfType(SDG.Unturned.EItemType.SHIRT), OfType(SDG.Unturned.EItemType.PANTS))),
                        (0.12f, OfType(SDG.Unturned.EItemType.MELEE)),
                    };
            }
            return null;
        }

        static (float, ushort[])[] Build(ushort[] main, ushort[] occasional, float mainChance)
            => occasional == null || occasional.Length == 0
                ? new[] { (1f, main) }
                : new[] { (mainChance, main), (1f - mainChance, occasional) };

        static ushort[] Concat(ushort[] a, ushort[] b)
        {
            var r = new ushort[a.Length + b.Length];
            a.CopyTo(r, 0); b.CopyTo(r, a.Length);
            return r;
        }

        static readonly (float chance, ushort[] ids)[] ToasterTiers =
        {
            (1.00f, new ushort[] { 460 }),   // 460 Bread -- items_catalog.tsv
        };

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
            : t == Toaster ? "Toaster"
            : t == Fridge ? "Fridge" : t == Freezer ? "Freezer" : t == Dishwasher ? "Dishwasher"
            : t == Oven ? "Oven" : t == Counter ? "Counter" : t == GarbageBag ? "Garbage Bag"
            : t == FilingCabinet ? "Filing Cabinet" : t == Barbecue ? "Barbecue"
            : _names != null && t >= 0 && t < _names.Length ? _names[t] : $"table {t}";

        /// <summary>Push the code-defined condition biases into LootCondition. Idempotent, and called from Load so
        /// it lands once per map -- LootCondition.Load() clears its file-backed map every time one opens, and these
        /// tables have no row in any file to be cleared back to.</summary>
        public static void ApplyVirtualBias()
        {
            foreach (var kv in VirtualBias) SDG.Unturned.LootCondition.SetCodeDefault(kv.Key, kv.Value);
        }

        public static void Load(string itemsDatPath)
        {
            ApplyVirtualBias();   // before the early-out: the biases must land even when the tables are already loaded
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
                      : table == Toaster ? ToasterTiers
                      : table == Dishwasher ? DishwasherTiers
                      : table == Oven ? OvenTiers
                      : table == FilingCabinet ? FilingCabinetTiers
                      : (table == Fridge || table == Freezer || table == Counter || table == GarbageBag || table == Barbecue) ? VirtualTiers(table)
                      : (_tiers == null || table < 0 || table >= _tiers.Length) ? null : _tiers[table];
            // A derived table can come back with an EMPTY tier if the catalog has no item of that kind -- drop those,
            // or the weighted pick can land on a tier with nothing in it and silently return -1 forever.
            if (tiers != null) { var keep = new List<(float, ushort[])>(); foreach (var t in tiers) if (t.ids != null && t.ids.Length > 0) keep.Add(t); tiers = keep.ToArray(); }
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
