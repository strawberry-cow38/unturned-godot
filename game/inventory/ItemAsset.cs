using System.Collections.Generic;

// 1:1 port of the Unturned item data model (SDG.Unturned): an Item instance, its ItemAsset definition, and a
// minimal asset registry. Only the fields the inventory needs -- id, name, grid size (Size_X/Size_Y), type, icon,
// and (for bags) the storage grid they provide. Sizes come from the real item .dat via the same ParseUInt8 the
// game's ItemAsset.cs uses, clamped to a minimum of 1 exactly as the source does.
namespace SDG.Unturned
{
    // subset of EItemType -- the clothing types drive which page a worn item resizes, the rest are generic
    public enum EItemType { HAT, PANTS, SHIRT, MASK, BACKPACK, VEST, GLASSES, GUN, MAGAZINE, MELEE, FOOD, WATER, MEDICAL, SUPPLY, GENERIC }

    // ItemTool rarity -> the tile's colour in the UI (getRarityColorUI)
    public enum EItemRarity { COMMON, UNCOMMON, RARE, EPIC, LEGENDARY, MYTHICAL }

    public class ItemAsset
    {
        public ushort id;
        public string guid = "";   // the item's own GUID (from items_catalog.tsv) -> lets blueprints resolve ingredient GUIDs to numeric ids
        public string itemName = "";
        public string description = "";   // the real localized Description from the item's English.dat
        public byte size_x = 1;
        public byte size_y = 1;
        public EItemType type = EItemType.GENERIC;
        public EItemRarity rarity = EItemRarity.COMMON;
        public byte amount = 1;        // default stack Amount (min 1)
        public string iconPath;        // res:// icon texture, if we have one
        public string gunName;         // for a GUN: the content name (eaglefire|maplestrike|masterkey) to hold on Equip
        public string meleeName;       // for a MELEE weapon: the content folder name (knife_military|sledgehammer|...) to hold on Equip
        // ItemBagAsset: the storage grid a worn bag/shirt/pants/vest provides (0,0 = none)
        public byte width;
        public byte height;

        // ItemClothingAsset protection multipliers (default 1.0 = no protection; armor-eligible = hat/shirt/pants/vest).
        // The port applies the two WHOLE-BODY ones (source aggregates these as products of all worn clothing):
        //   fallingDamageMultiplier -> CheckFallDamage, explosionArmor -> Explode. `armor` (general bullet/melee) is
        //   PER-LIMB in source (hat=head, shirt/vest=torso, pants=legs) so it's stored but NOT applied yet -- the port
        //   has no per-limb player damage. Loaded additively from content/clothing_armor.tsv (WireClothingArmor).
        public float armor = 1f;
        public float explosionArmor = 1f;
        public float fallingDamageMultiplier = 1f;
        public bool preventsFallingBoneBreak;   // ItemClothingAsset.preventsFallingBrokenBones: if ANY worn piece has it, a hard fall doesn't break legs

        // ItemConsumeableAsset effects applied on Use, then the item is consumed. Health is absolute (0-100);
        // Food/Water are the .dat 0-100 values (the port's vitals are 0..1, so divided by 100 on apply).
        public int useHealth, useFood, useWater;
        public int useVirus, useDisinfectant, useEnergy;   // Virus (askInfect: raises infection), Disinfectant (askDisinfect: lowers it), Energy (askRest: restores stamina)
        public bool useStopsBleeding;   // Bleeding_Modifier = Heal
        public bool useHealBroken;      // Bones_Modifier = Heal (mends broken legs -- Medkit/Splint)
        // Every Food/Water/Medical item is holdable + consumable (source: they're ItemConsumeableAsset), plus anything with an explicit effect.
        public bool IsConsumable => type == EItemType.FOOD || type == EItemType.WATER || type == EItemType.MEDICAL
                                 || useHealth > 0 || useFood > 0 || useWater > 0 || useVirus > 0 || useDisinfectant > 0 || useEnergy > 0 || useStopsBleeding || useHealBroken;

        // ItemMagazineAsset: a magazine holds ammo (the Item instance's `amount` = current rounds). A mag fits a gun when
        // magCaliber == GunDef.Caliber. magCapacity = max rounds (0 = not a magazine). (Military STANAG = cap 30, caliber 1.)
        public int magCapacity;
        public int magCaliber;
        public bool IsMagazine => magCapacity > 0;
        // Loose per-round ammo (shotgun shells): stacks in a slot up to stackSize, matched to a gun by magCaliber (magCapacity 0).
        // A reload consumes shells from the stack rather than swapping a whole magazine. (12/20 Gauge Shells, stack 32.)
        public bool isAmmo;
        public int stackSize = 1;   // max per-slot stack (Unturned items = 1; ammo like shotgun shells stack, e.g. 32)

        // ItemTool.getRarityColorUI: the exact per-rarity UI colours
        public static Godot.Color RarityColorUI(EItemRarity r) => r switch
        {
            EItemRarity.UNCOMMON  => new Godot.Color(0.12156863f, 0.5294118f, 0.12156863f),
            EItemRarity.RARE      => new Godot.Color(0.29411766f, 20f / 51f, 50f / 51f),
            EItemRarity.EPIC      => new Godot.Color(0.5882353f, 0.29411766f, 50f / 51f),
            EItemRarity.LEGENDARY => new Godot.Color(40f / 51f, 10f / 51f, 50f / 51f),
            EItemRarity.MYTHICAL  => new Godot.Color(50f / 51f, 10f / 51f, 5f / 51f),
            _ => Godot.Colors.White,
        };

        // parse the grid size the way ItemAsset.cs does: Size_X/Size_Y default 0 then clamped to >=1
        public static byte ParseSize(IDatDictionary d, string key)
        {
            byte v = d.ParseUInt8(key, 0);
            return v < 1 ? (byte)1 : v;
        }
    }

    public class Item
    {
        public ushort id;
        public byte amount = 1;
        public byte quality = 100;

        public Item(ushort newID, byte newAmount = 1, byte newQuality = 100)
        {
            id = newID;
            amount = newAmount;
            quality = newQuality;
        }

        public ItemAsset GetAsset() => Assets.find(id);
        public T GetAsset<T>() where T : ItemAsset => GetAsset() as T;
    }

    // minimal stand-in for the game's Assets table: id -> ItemAsset. Populated at startup from real item .dats.
    public static class Assets
    {
        static readonly Dictionary<ushort, ItemAsset> _byId = new();
        static readonly Dictionary<string, ItemAsset> _byGuid = new();   // item GUID -> asset (blueprint ingredient resolution)

        public static void add(ItemAsset a) { if (a != null) { _byId[a.id] = a; if (!string.IsNullOrEmpty(a.guid)) _byGuid[a.guid] = a; } }
        public static ItemAsset find(ushort id) => _byId.TryGetValue(id, out var a) ? a : null;
        public static ItemAsset findByGuid(string guid) => !string.IsNullOrEmpty(guid) && _byGuid.TryGetValue(guid, out var a) ? a : null;
        public static IEnumerable<ItemAsset> all() => _byId.Values;
        public static void clear() { _byId.Clear(); _byGuid.Clear(); }
    }
}
