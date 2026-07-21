using System.Collections.Generic;

// 1:1 port of the Unturned item data model (SDG.Unturned): an Item instance, its ItemAsset definition, and a
// minimal asset registry. Only the fields the inventory needs -- id, name, grid size (Size_X/Size_Y), type, icon,
// and (for bags) the storage grid they provide. Sizes come from the real item .dat via the same ParseUInt8 the
// game's ItemAsset.cs uses, clamped to a minimum of 1 exactly as the source does.
namespace SDG.Unturned
{
    // subset of EItemType -- the clothing types drive which page a worn item resizes, the rest are generic
    public enum EItemType { HAT, PANTS, SHIRT, MASK, BACKPACK, VEST, GLASSES, GUN, MAGAZINE, MELEE, FOOD, WATER, MEDICAL, SUPPLY, GENERIC, FISHER }

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

        // ItemClothingAsset behavioral fields (P1 clothing port). Defaulted so non-clothing items are unaffected.
        // movementSpeedMultiplier: source aggregates worn clothing as a product (1.0 = no change).
        public float movementSpeedMultiplier = 1f;
        // Proof_* are whole-body immunities in source (any worn piece with the flag grants it). Stored for when the
        // port models water/fire/radiation damage; default false.
        public bool proofWater, proofFire, proofRadiation;
        // Hair_Visible/Beard_Visible drive whether the player's hair/beard render under this piece (default visible).
        public bool hairVisible = true, beardVisible = true;
        // ItemVestAsset.hasFallbackShirt: a shirtless-visible vest (oversize/zip-up) shows a fallback shirt mesh.
        public bool hasFallbackShirt;

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
        public float fuelCapacity;              // Fuel-type container (gas can): max fuel it holds (retail .dat "Fuel", e.g. Portable 500). 0 = not a fuel can.
        public bool IsFuelContainer => fuelCapacity > 0f;
        // Loose per-round ammo (shotgun shells): stacks in a slot up to stackSize, matched to a gun by magCaliber (magCapacity 0).
        // A reload consumes shells from the stack rather than swapping a whole magazine. (12/20 Gauge Shells, stack 32.)
        public bool isAmmo;
        public int stackSize = 1;   // max per-slot stack (Unturned items = 1; ammo like shotgun shells stack, e.g. 32)
        public int pellets = 1;     // ItemMagazineAsset.Pellets: rays fired per shot from THIS ammo (12ga shells = 6, 20ga = 8; slugs = 1)

        // (ItemTool.getRarityColorUI lives game-side as ItemTool.RarityColorUI -- it returns a Godot.Color,
        // and this file moved engine-free to core for the MP_PLAN §3.3 inventory replication.)

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
        // Gun state carried by the item so a gun REMEMBERS it through hands<->inventory<->drop (source: player.equipment.state).
        // -1 = unset (a fresh gun uses its defaults). Attachments are persisted separately by the attachment system (TODO).
        public int gunAmmo = -1;       // loaded rounds incl. the chambered one
        public int gunFiremode = -1;   // fire-mode index (Safety/Semi/Burst/Auto)
        public int gunMagId = -1;      // the magazine item id currently loaded
        public int gunAttach = -1;     // attachment bitmask (which slots are attached; -1 = unset -> gun's defaults)
        // Fuel carried on the item (strawberry): a generator REMEMBERS its tank through pickup <-> inventory <-> drop
        // <-> re-place, and a gas can holds the fuel pumped into it. HP rides on `quality` (0-100 %); fuel is its own
        // float. -1 = fresh -> full (a picked-up gen = its tank; a fresh can = its fuelCapacity).
        public float fuelLevel = -1f;

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
        // World loot factory: a magazine spawns FULL (Military Magazine = 30 rounds) rather than empty (master). Other items = 1.
        public static Item makeLoot(ushort id) { var a = find(id); var it = new Item(id, a != null && a.IsMagazine ? (byte)System.Math.Max(1, a.magCapacity) : (byte)1); if (a != null && a.IsFuelContainer) it.fuelLevel = 0f; return it; }   // a fresh gas can starts EMPTY -> fill it at a pump
        public static ItemAsset find(ushort id) => _byId.TryGetValue(id, out var a) ? a : null;
        public static ItemAsset findByGuid(string guid) => !string.IsNullOrEmpty(guid) && _byGuid.TryGetValue(guid, out var a) ? a : null;
        public static IEnumerable<ItemAsset> all() => _byId.Values;
        public static void clear() { _byId.Clear(); _byGuid.Clear(); }
    }
}
