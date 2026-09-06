using System;

// 1:1 port of the page layout in SDG.Unturned.PlayerInventory. Nine pages:
//   0 PRIMARY  slot (a held weapon)      1 SECONDARY slot (sidearm)      -- pages < SLOTS are single-item holsters
//   2 pockets  fixed 5x3 grid, always present (source: items[2].loadSize(5,3))
//   3 BACKPACK 4 VEST 5 SHIRT 6 PANTS    -- grids sized by the worn bag (0x0 when nothing is worn)
//   7 STORAGE  8 AREA  9 FREEZER          -- external containers (not the player; left empty here)
// tryAddItem walks pages SLOTS..OWNPAGES exactly like the source, so an item auto-lands in the first page with a
// free slot. This is a plain model owned by PlayerController (the dashboard UI renders it).
namespace SDG.Unturned
{
    public class PlayerInventory
    {
        public static readonly byte SLOTS = 2;
        public static readonly byte PAGES = 10;
        public static readonly byte BACKPACK = 3;
        public static readonly byte VEST = 4;
        public static readonly byte SHIRT = 5;
        public static readonly byte PANTS = 6;
        public static readonly byte STORAGE = 7;
        public static readonly byte AREA = 8;
        public static readonly byte FREEZER = 9;   // a fridge's freezer compartment -- a SECOND external grid shown above the fridge one (strawberry 2026-09-06)

        /// <summary>Exclusive upper bound of the pages the PLAYER carries (0..6: two holsters, pockets, and the
        /// four clothing grids). Everything at or above it is an EXTERNAL container being viewed -- a crate, the
        /// ground, a freezer -- and must never be swept by auto-add, ammo counts or crafting.
        ///
        /// This used to be spelled `PAGES - 2` in forty places, which silently meant "all but the last two". The
        /// moment a tenth page was appended for the freezer, all forty would have started including STORAGE, and
        /// the first symptom would have been items auto-landing inside whatever crate happened to be open. The
        /// bound is a fact about the player, not about how many external views exist, so it says so.</summary>
        public static readonly byte OWNPAGES = 7;

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

        // Whole-body clothing protection = the PRODUCT of every worn piece's multiplier (source PlayerClothing aggregates
        // fallingDamageMultiplier over all worn slots; a plain/missing item contributes 1.0). Fall + explosion use these.
        public float FallingDamageMultiplier => WornProduct(a => a.fallingDamageMultiplier);
        public float ExplosionArmor => WornProduct(a => a.explosionArmor);

        float WornProduct(Func<ItemAsset, float> pick)
        {
            float m = 1f;
            foreach (var it in new[] { wornShirt, wornPants, wornHat, wornBackpack, wornVest, wornMask, wornGlasses })
                if (it != null) { var a = Assets.find(it.id); if (a != null) m *= pick(a); }
            return m;
        }

        // Source: legs never break on a fall if ANY worn piece has Prevents_Falling_Broken_Bones (PlayerLife:2436).
        public bool PreventsFallingBoneBreak => AnyWorn(a => a.preventsFallingBoneBreak);

        /// <summary>The worn pieces a deadzone cares about. Unlike the fall/explosion aggregates this is
        /// PER SLOT, not "any worn piece": a radiation-proof pair of trousers on your head is not a
        /// respirator, and the harsher zones check the mask, shirt and trousers separately.</summary>
        public RadiationGear RadiationProtection()
        {
            return new RadiationGear
            {
                MaskProofs = SlotProofsRadiation(wornMask),
                MaskQuality = wornMask?.quality ?? 0,
                ShirtProofs = SlotProofsRadiation(wornShirt),
                PantsProofs = SlotProofsRadiation(wornPants),
            };
        }

        bool SlotProofsRadiation(Item worn)
        {
            if (worn == null) return false;
            var a = Assets.find(worn.id);
            return a != null && a.proofRadiation;
        }

        bool AnyWorn(Func<ItemAsset, bool> pred)
        {
            foreach (var it in new[] { wornShirt, wornPants, wornHat, wornBackpack, wornVest, wornMask, wornGlasses })
                if (it != null) { var a = Assets.find(it.id); if (a != null && pred(a)) return true; }
            return false;
        }

        void Resize(byte page, Item item)
        {
            var a = item?.GetAsset();
            items[page].resize(a?.width ?? 0, a?.height ?? 0);
        }

        public enum AutoPlace : byte { None, Grid, Slot, Worn }

        public static bool IsClothingType(EItemType t) => t == EItemType.HAT || t == EItemType.GLASSES || t == EItemType.MASK
            || t == EItemType.SHIRT || t == EItemType.VEST || t == EItemType.BACKPACK || t == EItemType.PANTS;
        public Item wornByType(EItemType t) => t switch
        {
            EItemType.HAT => wornHat, EItemType.GLASSES => wornGlasses, EItemType.MASK => wornMask, EItemType.SHIRT => wornShirt,
            EItemType.VEST => wornVest, EItemType.BACKPACK => wornBackpack, EItemType.PANTS => wornPants, _ => null,
        };
        public void wearByType(EItemType t, Item item)
        {
            switch (t)
            {
                case EItemType.HAT: wearHat(item); break;
                case EItemType.GLASSES: wearGlasses(item); break;
                case EItemType.MASK: wearMask(item); break;
                case EItemType.SHIRT: wearShirt(item); break;
                case EItemType.VEST: wearVest(item); break;
                case EItemType.BACKPACK: wearBackpack(item); break;
                case EItemType.PANTS: wearPants(item); break;
            }
        }

        /// <summary>Retail PlayerInventory.tryAddItemAuto (autoEquipClothing + autoEquipWeapon), the PICKUP placement
        /// (strawberry 2026-09-04): clothing whose slot is EMPTY is worn straight away (the WORN STATE -- the caller
        /// drives the visual); a holster item (gun / melee) whose preferred hand slot is empty -- or whose other hand
        /// slot fits and is empty -- goes into that slot; anything else lands in the first page with room. The caller
        /// learns where it went so it can force the weapon into the hands.</summary>
        public AutoPlace tryAddItemAuto(Item item, out byte slot)
        {
            slot = byte.MaxValue;
            var a = item?.GetAsset();
            if (a != null)
            {
                if (IsClothingType(a.type) && wornByType(a.type) == null) { wearByType(a.type, item); return AutoPlace.Worn; }
                int pref = a.slot.PreferredSlot();
                if (pref >= 0)
                {
                    if (items[pref].getItemCount() == 0) { equipToSlot((byte)pref, item); slot = (byte)pref; return AutoPlace.Slot; }
                    for (byte alt = 0; alt < SLOTS; alt++)
                        if (a.slot.CanEquipInPage(alt) && items[alt].getItemCount() == 0) { equipToSlot(alt, item); slot = alt; return AutoPlace.Slot; }
                }
            }
            return tryAddItem(item) ? AutoPlace.Grid : AutoPlace.None;
        }

        // auto-place an item in the first page that has room (pockets, then clothing), skipping the hand slots
        public bool tryAddItem(Item item)
        {
            for (byte b = SLOTS; b < OWNPAGES; b++)
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

        /// <summary>May this jar SIT in this page? An item whose asset has not resolved is allowed through --
        /// the same leniency the rest of the grid math uses, and refusing an unresolved asset would strand items
        /// during the join window before the catalog lands.</summary>
        static bool MayOccupy(ItemJar j, byte page)
        {
            var a = j?.GetAsset();
            return a == null || a.slot.CanOccupyPage(page);
        }

        // Drag an item from (page0, x0,y0) to (page1, x1,y1) at rotation rot1. Faithful port of ReceiveDragItem
        // (move onto empty space -> checkSpaceDrag, remove+add) and ReceiveSwapItem (drop onto another item -> swap,
        // checkSpaceSwap both ways, remove both, re-add crossed). Hand-slot pages force rot 0. Returns true if it moved.
        //
        // The holster rule IS enforced here (see CanOccupyPage). This comment used to say the source's
        // equipment/canEquipInPage guards were "omitted -- this port has no equipment system yet", which stopped
        // being true the moment ESlotType landed and nothing updated it: a primary-only rifle could be dragged
        // into the SECONDARY slot and equipped from there, and any item at all could be parked in a holster.
        // A stale comment naming a deliberate omission is a TODO addressed to whoever removes the reason.
        public bool TryDrag(byte page0, byte x0, byte y0, byte page1, byte x1, byte y1, byte rot1)
        {
            // AREA (the ground / "Nearby") is not a drag endpoint -- picking up and dropping route through their
            // own paths. Named explicitly rather than as `>= PAGES - 1`, which happened to point at AREA only
            // while AREA was last; appending FREEZER moved that arithmetic one page and would have inverted it,
            // banning the freezer and permitting the ground.
            if (page0 == AREA || page1 == AREA || page0 >= PAGES || page1 >= PAGES
                || items[page0] == null || items[page1] == null) return false;
            byte index = items[page0].getIndex(x0, y0);
            if (index == byte.MaxValue) return false;
            ItemJar item = items[page0].getItem(index);
            if (item == null) return false;

            byte destIndex = items[page1].getIndex(x1, y1);
            ItemJar dest = destIndex == byte.MaxValue ? null : items[page1].getItem(destIndex);

            // Holster rule, checked BEFORE any mutation and in BOTH directions -- a swap moves two items, and the
            // one being displaced INTO a slot has to satisfy it too, or a swap becomes the hole the direct drag
            // no longer is.
            if (!MayOccupy(item, page1)) return false;
            if (dest != null && dest != item && !MayOccupy(dest, page0)) return false;

            if (dest == null || dest == item)
            {
                // MOVE onto empty space
                if (!items[page1].checkSpaceDrag(x0, y0, item.rot, x1, y1, rot1, item.size_x, item.size_y, page0 == page1)) return false;
                if (page1 < SLOTS) rot1 = 0;
                items[page0].removeItem(index);
                items[page1].addItem(x1, y1, rot1, item.item);
                return true;
            }

            // SWAP with the item already there
            byte rot0 = dest.rot;
            if (!items[page0].checkSpaceSwap(x0, y0, item.size_x, item.size_y, item.rot, dest.size_x, dest.size_y, rot0)) return false;
            if (!items[page1].checkSpaceSwap(x1, y1, dest.size_x, dest.size_y, dest.rot, item.size_x, item.size_y, rot1)) return false;
            items[page0].removeItem(index);
            byte b = destIndex;
            if (page0 == page1 && b > index) b--;
            items[page1].removeItem(b);
            if (page0 < SLOTS) rot0 = 0;
            if (page1 < SLOTS) rot1 = 0;
            items[page0].addItem(x0, y0, rot0, dest.item);
            items[page1].addItem(x1, y1, rot1, item.item);
            return true;
        }

        // total count of an item id across the player's own pages (0..OWNPAGES), for HUD/ammo/craft checks later
        public int getItemCount(ushort id)
        {
            int n = 0;
            for (byte b = 0; b < OWNPAGES; b++)
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

        // Condition (quality 0-100) of the FIRST-found instance of `id` -- i.e. the exact one removeItemAmount(id,1)
        // will delete next, in the same page/index scan order. Used to score the moldy-food eating penalty against the
        // instance actually consumed (source eats player.equipment.quality). 100 if none found (treated as fresh).
        /// <summary>The first matching item itself, so a caller can read more than one of its fields without
        /// walking the grid once per field. peekItemQuality predates it and stays -- it has callers.</summary>
        public Item peekItem(ushort id)
        {
            for (byte b = 0; b < OWNPAGES; b++)
            {
                var page = items[b];
                for (byte i = 0; i < page.getItemCount(); i++)
                {
                    var jar = page.getItem(i);
                    if (jar?.item != null && jar.item.id == id) return jar.item;
                }
            }
            return null;
        }

        public byte peekItemQuality(ushort id)
        {
            for (byte b = 0; b < OWNPAGES; b++)
            {
                var page = items[b];
                for (byte i = 0; i < page.getItemCount(); i++)
                {
                    var jar = page.getItem(i);
                    if (jar?.item != null && jar.item.id == id) return jar.item.quality;
                }
            }
            return 100;
        }

        // consume up to `amount` of item id across the player's pages (crafting supply consumption); removes emptied jars
        public void removeItemAmount(ushort id, int amount)
        {
            for (byte b = 0; b < OWNPAGES && amount > 0; b++)
            {
                var page = items[b];
                byte i = 0;
                while (i < page.getItemCount() && amount > 0)
                {
                    var jar = page.getItem(i);
                    if (jar?.item != null && jar.item.id == id)
                    {
                        int take = Math.Min(amount, jar.item.amount);
                        jar.item.amount -= (byte)take;
                        amount -= take;
                        if (jar.item.amount == 0) { page.removeItem(i); continue; }   // jar removed -> list shifted, don't advance i
                    }
                    i++;
                }
            }
        }

        // restore the most-damaged item of `id` to `quality` (RepairTargetItem crafting operation)
        public void restoreQuality(ushort id, byte quality)
        {
            ItemJar best = null;
            for (byte b = 0; b < OWNPAGES; b++)
            {
                var page = items[b];
                for (byte i = 0; i < page.getItemCount(); i++)
                {
                    var jar = page.getItem(i);
                    if (jar?.item != null && jar.item.id == id && (best == null || jar.item.quality < best.item.quality)) best = jar;
                }
            }
            if (best != null) best.item.quality = quality;
        }
    }
}
