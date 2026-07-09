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
            //  id                name           sx sy  type                 rarity                 storage
            Add(4,   "Eaglefire",     4, 2, EItemType.GUN,      EItemRarity.RARE);
            Add(363, "Maplestrike",   4, 2, EItemType.GUN,      EItemRarity.EPIC);
            Add(253, "Alicepack",     2, 2, EItemType.BACKPACK, EItemRarity.EPIC,      8, 7);
            Add(209, "Cargo Pants",   2, 2, EItemType.PANTS,    EItemRarity.UNCOMMON,  6, 3);
            Add(15,  "Medkit",        2, 2, EItemType.MEDICAL,  EItemRarity.LEGENDARY);
            Add(95,  "Bandage",       1, 1, EItemType.MEDICAL,  EItemRarity.UNCOMMON);
            Add(14,  "Bottled Water", 1, 1, EItemType.WATER,    EItemRarity.COMMON);
            Add(13,  "Canned Beans",  1, 1, EItemType.FOOD,     EItemRarity.COMMON);
        }

        static void Add(ushort id, string name, byte sx, byte sy, EItemType type, EItemRarity rar, byte w = 0, byte h = 0)
        {
            Assets.add(new ItemAsset { id = id, itemName = name, size_x = sx, size_y = sy, type = type, rarity = rar, width = w, height = h });
        }
    }
}
