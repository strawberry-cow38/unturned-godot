// A small catalog of REAL Unturned items, values read straight from the retail item .dats (Bundles/Items/...):
// id, grid Size_X/Size_Y, Type, Rarity, and (for bags/clothing) the storage grid Width/Height they provide. Used
// to register assets and populate a demo inventory. Not a full item DB -- just enough real items to show the
// system 1:1 across the rarity range and both a bag (8x7) and clothing (6x3) storage page.
namespace SDG.Unturned
{
    public static class ItemCatalog
    {
        public static void RegisterAll()
        {
            Assets.clear();
            //  id   name            sx sy  type                 rarity               storage   description (real, from English.dat)
            Add(4,   "Eaglefire",     4, 2, EItemType.GUN,      EItemRarity.RARE,      0, 0, "American assault rifle chambered in Military ammunition.");
            Add(363, "Maplestrike",   4, 2, EItemType.GUN,      EItemRarity.EPIC,      0, 0, "Canadian assault rifle chambered in Military ammunition.");
            Add(253, "Alicepack",     2, 2, EItemType.BACKPACK, EItemRarity.EPIC,      8, 7, "Large sized military cargo backpack.");
            Add(209, "Cargo Pants",   2, 2, EItemType.PANTS,    EItemRarity.UNCOMMON,  6, 3, "High capacity synthetic pants for all weather.");
            // consumables also carry their real ItemConsumeableAsset effects (Health / Food / Water / Bleeding heal)
            Add(15,  "Medkit",        2, 2, EItemType.MEDICAL,  EItemRarity.LEGENDARY, 0, 0, "A box of hospital medical equipment suited for healing a wide variety of injuries.", uh: 75, ub: true);
            Add(95,  "Bandage",       1, 1, EItemType.MEDICAL,  EItemRarity.UNCOMMON,  0, 0, "Medium quality cloth for stopping bleeding, and recovering.", uh: 15, ub: true);
            Add(14,  "Bottled Water", 1, 1, EItemType.WATER,    EItemRarity.COMMON,    0, 0, "Overpriced tap water.", uw: 55);
            Add(13,  "Canned Beans",  1, 1, EItemType.FOOD,     EItemRarity.COMMON,    0, 0, "Very tactically packed for maximum taste.", uh: 10, uf: 55);
        }

        static void Add(ushort id, string name, byte sx, byte sy, EItemType type, EItemRarity rar, byte w, byte h, string desc,
                        int uh = 0, int uf = 0, int uw = 0, bool ub = false)
        {
            Assets.add(new ItemAsset { id = id, itemName = name, size_x = sx, size_y = sy, type = type, rarity = rar,
                                       width = w, height = h, description = desc,
                                       useHealth = uh, useFood = uf, useWater = uw, useStopsBleeding = ub });
        }
    }
}
