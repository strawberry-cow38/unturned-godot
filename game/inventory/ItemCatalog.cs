using Godot;

// Real Unturned items. The bulk (id, name, Type, Rarity, Size_X/Y, Description) is loaded from content/items_catalog.tsv,
// pre-extracted straight from the retail item .dats (Bundles/Items/<Cat>/<Name>/<Name>.dat + English.dat) by
// tools/gen_item_catalog.py -- 1937 items, the same fields the game's ItemAsset.cs parses. On top of that, the handful the
// player actually uses get hand-tuned overrides carrying gameplay data the tile fields don't cover (gun viewmodel name,
// consumable Health/Food/Water/bleed effects, and bag/clothing storage grids).
namespace SDG.Unturned
{
    public static class ItemCatalog
    {
        public static void RegisterAll()
        {
            Assets.clear();
            LoadCatalogFile();
            //  id   name            sx sy  type                 rarity               storage   description (real, from English.dat)
            Add(4,   "Eaglefire",     4, 2, EItemType.GUN,      EItemRarity.RARE,      0, 0, "American assault rifle chambered in Military ammunition.", gun: "eaglefire");
            Add(363, "Maplestrike",   4, 2, EItemType.GUN,      EItemRarity.EPIC,      0, 0, "Canadian assault rifle chambered in Military ammunition.", gun: "maplestrike");
            Add(253, "Alicepack",     2, 2, EItemType.BACKPACK, EItemRarity.EPIC,      8, 7, "Large sized military cargo backpack.");
            Add(209, "Cargo Pants",   2, 2, EItemType.PANTS,    EItemRarity.UNCOMMON,  6, 3, "High capacity synthetic pants for all weather.");
            // consumables also carry their real ItemConsumeableAsset effects (Health / Food / Water / Bleeding heal)
            Add(15,  "Medkit",        2, 2, EItemType.MEDICAL,  EItemRarity.LEGENDARY, 0, 0, "A box of hospital medical equipment suited for healing a wide variety of injuries.", uh: 75, ub: true, hb: true);
            Add(95,  "Bandage",       1, 1, EItemType.MEDICAL,  EItemRarity.UNCOMMON,  0, 0, "Medium quality cloth for stopping bleeding, and recovering.", uh: 15, ub: true);
            Add(14,  "Bottled Water", 1, 1, EItemType.WATER,    EItemRarity.COMMON,    0, 0, "Overpriced tap water.", uw: 55);
            Add(13,  "Canned Beans",  1, 1, EItemType.FOOD,     EItemRarity.COMMON,    0, 0, "Very tactically packed for maximum taste.", uh: 10, uf: 55);
        }

        // bulk-load the pre-extracted retail catalog: one tab-separated line per item -- id,name,Type,Rarity,Size_X,Size_Y,Description
        static void LoadCatalogFile()
        {
            const string path = "res://content/items_catalog.tsv";
            if (!Godot.FileAccess.FileExists(path)) { GD.PrintErr("[items] catalog file missing: " + path + " (loot shows table fallbacks only)"); return; }
            using var f = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
            int n = 0;
            while (f != null && !f.EofReached())
            {
                string line = f.GetLine();
                if (string.IsNullOrEmpty(line)) continue;
                var c = line.Split('\t');
                if (c.Length < 6 || !ushort.TryParse(c[0], out var id)) continue;
                Assets.add(new ItemAsset
                {
                    id = id, itemName = c[1], type = ParseType(c[2]), rarity = ParseRarity(c[3]),
                    size_x = ParseByte(c[4]), size_y = ParseByte(c[5]), description = c.Length > 6 ? c[6] : "",
                    guid = c.Length > 7 ? c[7] : "",
                });
                n++;
            }
            GD.Print($"[items] loaded {n} item assets from {path}");
        }

        static byte ParseByte(string s) => byte.TryParse(s, out var v) && v >= 1 ? v : (byte)1;

        static EItemType ParseType(string s) => s switch
        {
            "Gun" => EItemType.GUN, "Magazine" => EItemType.MAGAZINE, "Melee" => EItemType.MELEE,
            "Food" => EItemType.FOOD, "Water" => EItemType.WATER, "Medical" => EItemType.MEDICAL,
            "Hat" => EItemType.HAT, "Pants" => EItemType.PANTS, "Shirt" => EItemType.SHIRT,
            "Mask" => EItemType.MASK, "Backpack" => EItemType.BACKPACK, "Vest" => EItemType.VEST,
            "Glasses" => EItemType.GLASSES, "Supply" => EItemType.SUPPLY,
            _ => EItemType.GENERIC,
        };

        static EItemRarity ParseRarity(string s) => s switch
        {
            "Uncommon" => EItemRarity.UNCOMMON, "Rare" => EItemRarity.RARE, "Epic" => EItemRarity.EPIC,
            "Legendary" => EItemRarity.LEGENDARY, "Mythical" => EItemRarity.MYTHICAL,
            _ => EItemRarity.COMMON,
        };

        static void Add(ushort id, string name, byte sx, byte sy, EItemType type, EItemRarity rar, byte w, byte h, string desc,
                        int uh = 0, int uf = 0, int uw = 0, bool ub = false, bool hb = false, string gun = null)
        {
            Assets.add(new ItemAsset { id = id, itemName = name, size_x = sx, size_y = sy, type = type, rarity = rar,
                                       width = w, height = h, description = desc,
                                       useHealth = uh, useFood = uf, useWater = uw, useStopsBleeding = ub, useHealBroken = hb, gunName = gun });
        }
    }
}
