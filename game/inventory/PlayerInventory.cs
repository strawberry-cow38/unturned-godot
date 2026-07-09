using System;

// 1:1 port of the page layout in SDG.Unturned.PlayerInventory. Nine pages:
//   0 PRIMARY  slot (a held weapon)      1 SECONDARY slot (sidearm)      -- pages < SLOTS are single-item holsters
//   2 pockets  fixed 5x3 grid, always present (source: items[2].loadSize(5,3))
//   3 BACKPACK 4 VEST 5 SHIRT 6 PANTS    -- grids sized by the worn bag (0x0 when nothing is worn)
//   7 STORAGE  8 AREA                    -- external containers (not the player; left empty here)
// tryAddItem walks pages SLOTS..PAGES-2 exactly like the source, so an item auto-lands in the first page with a
// free slot. This is a plain model owned by PlayerController (the dashboard UI renders it).
namespace SDG.Unturned
{
    public class PlayerInventory
    {
        public static readonly byte SLOTS = 2;
        public static readonly byte PAGES = 9;
        public static readonly byte BACKPACK = 3;
        public static readonly byte VEST = 4;
        public static readonly byte SHIRT = 5;
        public static readonly byte PANTS = 6;
        public static readonly byte STORAGE = 7;
        public static readonly byte AREA = 8;

        public Items[] items { get; private set; }

        // the currently worn clothing (shown in the dashboard's equip slots); a bag also resizes its storage page
        public Item wornHat, wornGlasses, wornMask, wornShirt, wornVest, wornBackpack, wornPants;

        public event Action<byte> onPageChanged;   // page index whose contents/size changed (UI refresh hook)

        public PlayerInventory()
        {
            items = new Items[PAGES];
            for (byte b = 0; b < PAGES; b++)
            {
                items[b] = new Items(b);
                byte page = b;
                items[b].onStateUpdated += () => onPageChanged?.Invoke(page);
            }
            // the two hand slots hold one item regardless of size; pockets are a fixed 5x3; clothing/external start empty
            items[0].loadSize(0, 0);
            items[1].loadSize(0, 0);
            items[2].loadSize(5, 3);
            for (byte b = 3; b < PAGES; b++) items[b].loadSize(0, 0);
        }

        // wear a bag: track the worn item + resize its clothing page to the bag's grid (source resizes
        // SHIRT/PANTS/BACKPACK/VEST to itemBagAsset.width/height on equip, or 0x0 on removal)
        public void wearBackpack(Item item) { wornBackpack = item; Resize(BACKPACK, item); }
        public void wearVest(Item item) { wornVest = item; Resize(VEST, item); }
        public void wearShirt(Item item) { wornShirt = item; Resize(SHIRT, item); }
        public void wearPants(Item item) { wornPants = item; Resize(PANTS, item); }
        public void wearHat(Item item) => wornHat = item;
        public void wearGlasses(Item item) => wornGlasses = item;
        public void wearMask(Item item) => wornMask = item;

        void Resize(byte page, Item item)
        {
            var a = item?.GetAsset();
            items[page].resize(a?.width ?? 0, a?.height ?? 0);
        }

        // auto-place an item in the first page that has room (pockets, then clothing), skipping the hand slots
        public bool tryAddItem(Item item)
        {
            for (byte b = SLOTS; b < (byte)(PAGES - 2); b++)
                if (items[b].tryAddItem(item))
                    return true;
            return false;
        }

        // put a weapon straight into a hand slot (0 primary / 1 secondary)
        public bool equipToSlot(byte slot, Item item)
        {
            if (slot >= SLOTS || items[slot].getItemCount() > 0) return false;
            items[slot].addItem(0, 0, 0, item);
            return true;
        }

        // total count of an item id across the player's own pages (0..PAGES-2), for HUD/ammo/craft checks later
        public int getItemCount(ushort id)
        {
            int n = 0;
            for (byte b = 0; b < (byte)(PAGES - 2); b++)
            {
                var page = items[b];
                for (byte i = 0; i < page.getItemCount(); i++)
                {
                    var jar = page.getItem(i);
                    if (jar?.item != null && jar.item.id == id) n += jar.item.amount;
                }
            }
            return n;
        }
    }
}
