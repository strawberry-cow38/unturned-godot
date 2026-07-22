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
            Add(6,   "Military Magazine", 2, 1, EItemType.MAGAZINE, EItemRarity.UNCOMMON, 0, 0, "Standard STANAG magazine for Military rifles.", magCap: 30, magCal: 1);   // the eaglefire/maplestrike mag (caliber 1); 2x1 per master (was hardcoded 1x3, overriding the catalog)
            Add(253, "Alicepack",     2, 2, EItemType.BACKPACK, EItemRarity.EPIC,      8, 7, "Large sized military cargo backpack.");
            Add(209, "Cargo Pants",   2, 2, EItemType.PANTS,    EItemRarity.UNCOMMON,  6, 3, "High capacity synthetic pants for all weather.");
            // consumables also carry their real ItemConsumeableAsset effects (Health / Food / Water / Bleeding heal)
            Add(15,  "Medkit",        2, 2, EItemType.MEDICAL,  EItemRarity.LEGENDARY, 0, 0, "A box of hospital medical equipment suited for healing a wide variety of injuries.", uh: 75, ub: true, hb: true);
            Add(95,  "Bandage",       1, 1, EItemType.MEDICAL,  EItemRarity.UNCOMMON,  0, 0, "Medium quality cloth for stopping bleeding, and recovering.", uh: 15, ub: true);
            Add(14,  "Bottled Water", 1, 1, EItemType.WATER,    EItemRarity.COMMON,    0, 0, "Overpriced tap water.", uw: 55);
            Add(13,  "Canned Beans",  1, 1, EItemType.FOOD,     EItemRarity.COMMON,    0, 0, "Very tactically packed for maximum taste.", uh: 10, uf: 55);
            // deployables: shorten the tile name so it reads cleanly + `give generator` matches by name (strawberry)
            { var g = Assets.find(458); if (g != null) g.itemName = "Generator"; }
            // custom electrical splitters (our own system, not retail): fan one power input out to 2/3/4 outputs. GENERIC
            // type -- placement is keyed on DeployableDef.ById, not the item type. IDs 9101-9103 sit above the retail range.
            Add(9101, "2-Way Splitter", 2, 2, EItemType.GENERIC, EItemRarity.UNCOMMON, 0, 0, "A junction box that splits one power wire into two. Each output carries the full wattage -- devices draw only what they need.");
            Add(9102, "3-Way Splitter", 2, 2, EItemType.GENERIC, EItemRarity.UNCOMMON, 0, 0, "A junction box that splits one power wire into three. Each output carries the full wattage -- devices draw only what they need.");
            Add(9103, "4-Way Splitter", 3, 2, EItemType.GENERIC, EItemRarity.UNCOMMON, 0, 0, "A junction box that splits one power wire into four. Each output carries the full wattage -- devices draw only what they need.");
            Add(9104, "2-Way Combiner", 2, 2, EItemType.GENERIC, EItemRarity.UNCOMMON, 0, 0, "A junction box that combines two power sources into one output -- their wattages add together, and the load splits back across the sources.");
            Add(9105, "Power Switch", 2, 2, EItemType.GENERIC, EItemRarity.UNCOMMON, 0, 0, "A wired power switch. Toggle it with [F] to pass power to its output or cut it off; it remembers its state, and a light shows on (green) or off (red).");
            Add(9106, "Wind Turbine", 3, 4, EItemType.GENERIC, EItemRarity.UNCOMMON, 0, 0, "A wind turbine. Place it out in the open -- higher ground gets stronger wind. Wire its output into your grid; the blades spin and its power rises and falls with the local wind.");
            WireExtractedGuns();
            WireExtractedMelee();
            WireClothingArmor();
            WireConsumableStats();
            WireShotgunShells();
            WireFuelCans();
            UnturnedGodot.AssetCatalog.RegisterFactoryItems();   // Asset Factory guns/deployables -> real give-able items (after real items + Assets.clear)
        }

        // Fuel containers (gas cans/jerrycans) carry a fuelCapacity from the retail .dat "Fuel" field, so a right-click on
        // a pump can fill them (master's fluids system). Portable Gas Can (28) = 500, Industrial (1440) = 2500; jerrycans
        // (Maple/Birch/Pine 1114-1116) default to 500 (2x2, like the portable).
        static void WireFuelCans()
        {
            // PZ-scale fuel economy (master): a PZ gas can = 8 units. Portable/jerrycans = 8, Industrial = 20 (bigger).
            void Cap(ushort id, float cap) { var a = Assets.find(id); if (a != null) a.fuelCapacity = cap; }
            Cap(28, 8f); Cap(1440, 20f);
            Cap(1114, 8f); Cap(1115, 8f); Cap(1116, 8f);
        }

        // Real Unturned shotgun shells as stackable loose ammo (master: new ammo types, stack to 32 per slot). These items
        // (12 Gauge = 113, 20 Gauge = 381) already load from items_catalog.tsv as type Magazine; here we make them FUNCTIONAL
        // ammo -- magCaliber matches the shotgun (12ga -> caliber 8 = Bluntforce; 20ga -> caliber 16 = Masterkey/Sawed-Off),
        // isAmmo so a reload consumes shells from the stack, and stackSize 32.
        static void WireShotgunShells()
        {
            void Shell(ushort id, int caliber, int pellets) { var a = Assets.find(id); if (a != null) { a.magCaliber = caliber; a.isAmmo = true; a.stackSize = 32; a.pellets = pellets; } }
            Shell(113, 8, 6);    // 12 Gauge Shells (Bluntforce / Quadbarrel / Determinator) -- 6 pellets (retail Shells_8.dat)
            Shell(381, 16, 8);   // 20 Gauge Shells (Masterkey / Sawed-Off) -- 8 pellets (retail Shells_2.dat)
        }

        // Load real ItemConsumeableAsset effects (content/consumable_stats.tsv: id health food water virus disinfectant
        // energy bleeding bones) onto every Food/Water/Medical item -- so the WHOLE catalog is consumable, not just the
        // 8 hardcoded above. Overwrites the hardcoded 8 with the same authoritative .dat values. bleeding/bones: 1=Heal.
        static void WireConsumableStats()
        {
            const string path = "res://content/consumable_stats.tsv";
            if (!Godot.FileAccess.FileExists(path)) return;
            using var f = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
            int n = 0;
            while (f != null && !f.EofReached())
            {
                string line = f.GetLine();
                if (string.IsNullOrEmpty(line)) continue;
                var c = line.Split('\t');
                if (c.Length < 9 || !ushort.TryParse(c[0], out var id)) continue;
                var a = Assets.find(id);
                if (a == null) continue;
                int I(int k) => int.TryParse(c[k], out var v) ? v : 0;
                a.useHealth = I(1); a.useFood = I(2); a.useWater = I(3);
                a.useVirus = I(4); a.useDisinfectant = I(5); a.useEnergy = I(6);
                a.useStopsBleeding = I(7) == 1;   // Bleeding_Modifier Heal
                a.useHealBroken = I(8) == 1;       // Bones_Modifier Heal
                n++;
            }
            GD.Print($"[items] wired consumable effects for {n} food/water/medical items");
        }

        // Load the additive clothing-armor table (content/clothing_armor.tsv: id  Armor  Armor_Explosion  Falling_Damage_Multiplier)
        // onto the already-registered ItemAssets. Kept separate from items_catalog.tsv so it never risks the main 1937-item catalog.
        // The port applies the two WHOLE-BODY ones (explosionArmor -> Explode, fallingDamageMultiplier -> CheckFallDamage);
        // `armor` (general per-limb) is stored for when the port models limb damage.
        static void WireClothingArmor()
        {
            const string path = "res://content/clothing_armor.tsv";
            if (!Godot.FileAccess.FileExists(path)) return;
            using var f = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var fs = System.Globalization.NumberStyles.Float;
            int n = 0;
            while (f != null && !f.EofReached())
            {
                string line = f.GetLine();
                if (string.IsNullOrEmpty(line)) continue;
                var c = line.Split('\t');
                if (c.Length < 4 || !ushort.TryParse(c[0], out var id)) continue;
                var a = Assets.find(id);
                if (a == null) continue;
                if (float.TryParse(c[1], fs, inv, out var ar)) a.armor = ar;
                if (float.TryParse(c[2], fs, inv, out var ae)) a.explosionArmor = ae;
                if (float.TryParse(c[3], fs, inv, out var fl)) a.fallingDamageMultiplier = fl;
                if (c.Length > 4) a.preventsFallingBoneBreak = c[4].Trim() == "1";
                n++;
            }
            GD.Print($"[items] wired clothing armor for {n} items (fall + explosion whole-body multipliers)");
        }

        // Wire gunName on the extracted PEI gun items (content/<name>.dat's numeric ID -> ItemAsset.gunName) so equipping
        // or picking up the item loads the right viewmodel via EquipHeldGun. Gun names come from content/guns_visual.tsv.
        static void WireExtractedGuns()
        {
            const string gv = "res://content/guns_visual.tsv";
            if (!Godot.FileAccess.FileExists(gv)) return;
            using var f = Godot.FileAccess.Open(gv, Godot.FileAccess.ModeFlags.Read);
            int n = 0;
            while (f != null && !f.EofReached())
            {
                string line = f.GetLine();
                if (string.IsNullOrEmpty(line)) continue;
                string name = line.Split('\t')[0];
                string datPath = ProjectSettings.GlobalizePath($"res://content/{name}.dat");
                if (!System.IO.File.Exists(datPath)) continue;
                try
                {
                    var d = new DatParser().Parse(System.IO.File.ReadAllText(datPath));
                    if (ushort.TryParse(d.GetString("ID"), out var id)) { var a = Assets.find(id); if (a != null) { a.gunName = name; n++; } }
                }
                catch { /* skip a malformed .dat */ }
            }
            GD.Print($"[items] wired {n} extracted guns for in-game equip");
        }

        // Wire meleeName on the extracted PEI melee items (content/<folder>.dat's ID -> ItemAsset.meleeName) so equipping
        // a knife/axe/bat loads its viewmodel + weapon-specific swings via EquipHeldMelee. Folders from content/melee_list.tsv.
        static void WireExtractedMelee()
        {
            const string ml = "res://content/melee_list.tsv";
            if (!Godot.FileAccess.FileExists(ml)) return;
            using var f = Godot.FileAccess.Open(ml, Godot.FileAccess.ModeFlags.Read);
            int n = 0;
            while (f != null && !f.EofReached())
            {
                string line = f.GetLine();
                if (string.IsNullOrEmpty(line)) continue;
                string name = line.Split('\t')[0].Trim();
                string datPath = ProjectSettings.GlobalizePath($"res://content/{name}.dat");
                if (!System.IO.File.Exists(datPath)) continue;
                try
                {
                    var d = new DatParser().Parse(System.IO.File.ReadAllText(datPath));
                    if (ushort.TryParse(d.GetString("ID"), out var id)) { var a = Assets.find(id); if (a != null) { a.meleeName = name; n++; } }
                }
                catch { /* skip a malformed .dat */ }
            }
            GD.Print($"[items] wired {n} extracted melee weapons for in-game equip");
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
                        int uh = 0, int uf = 0, int uw = 0, bool ub = false, bool hb = false, string gun = null, int magCap = 0, int magCal = 0)
        {
            Assets.add(new ItemAsset { id = id, itemName = name, size_x = sx, size_y = sy, type = type, rarity = rar,
                                       width = w, height = h, description = desc,
                                       useHealth = uh, useFood = uf, useWater = uw, useStopsBleeding = ub, useHealBroken = hb, gunName = gun,
                                       magCapacity = magCap, magCaliber = magCal });
        }
    }
}
