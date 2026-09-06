using System.Collections.Generic;

// 1:1 port of the Unturned item data model (SDG.Unturned): an Item instance, its ItemAsset definition, and a
// minimal asset registry. Only the fields the inventory needs -- id, name, grid size (Size_X/Size_Y), type, icon,
// and (for bags) the storage grid they provide. Sizes come from the real item .dat via the same ParseUInt8 the
// game's ItemAsset.cs uses, clamped to a minimum of 1 exactly as the source does.
namespace SDG.Unturned
{
    // subset of EItemType -- the clothing types drive which page a worn item resizes, the rest are generic
    // SIGHT/BARREL/GRIP/TACTICAL appended (not inserted) so no persisted ordinal shifts. Before these existed every
    // attachment in items_catalog.tsv fell through ParseType to GENERIC -- 48 sights, 59 magazines' worth of
    // siblings, all indistinguishable from a rock -- so nothing could ask "what fits this slot?".
    public enum EItemType { HAT, PANTS, SHIRT, MASK, BACKPACK, VEST, GLASSES, GUN, MAGAZINE, MELEE, FOOD, WATER, MEDICAL, SUPPLY, GENERIC, SIGHT, BARREL, GRIP, TACTICAL, FISHER, OPTIC, THROWABLE }

    // ItemTool rarity -> the tile's colour in the UI (getRarityColorUI)
    public enum EItemRarity { COMMON, UNCOMMON, RARE, EPIC, LEGENDARY, MYTHICAL }

    public class ItemAsset
    {
        public ushort id;
        public string guid = "";   // the item's own GUID (from items_catalog.tsv) -> lets blueprints resolve ingredient GUIDs to numeric ids
        public string itemName = "";
        public string description = "";   // the real localized Description from the item's English.dat
        /// <summary>Holster rule from the .dat's `Slot` key. NONE by default, which is right for everything that
        /// is not a weapon: it stays equippable from the bag.</summary>
        public ESlotType slot = ESlotType.NONE;
        public byte size_x = 1;
        public byte size_y = 1;
        public EItemType type = EItemType.GENERIC;
        public EItemRarity rarity = EItemRarity.COMMON;
        public byte amount = 1;        // default stack Amount (min 1)
        public string iconPath;        // res:// icon texture, if we have one
        public string gunName;         // for a GUN: the content name (eaglefire|maplestrike|masterkey) to hold on Equip
        // The gun's Ammo_Max, copied off its .dat by ItemCatalog. GunDef lives in the game layer and core cannot
        // see it, but the SERVER needs a number to clamp a client-asserted ammo count against (CommandGunState),
        // and "the client says 4000" has to be answerable without a gun simulation. 0 = not a gun, or a gun whose
        // .dat did not parse.
        public int gunAmmoMax;
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
        // A "reservoir" magazine (the 100-round Military Drum): its magCapacity OVERRIDES the gun's Ammo_Max, so the gun
        // actually holds all of it. Default false -> a normal mag is still capped by the gun's Ammo_Max on a reload draw
        // (master: keeps the .45/.50 "gun caps the mag" niche, where a 12-round mag in a 7-round pistol still loads 7).
        public bool magOverridesCapacity;
        // WHICH ROUND this magazine is loaded with, distinct from magCaliber above (which is the mechanical
        // "does this mag fit that gun" group). The two differ precisely where a magazine body feeds more than one
        // cartridge: a STANAG mag physically fits every group-1 rifle, but a .300 BLK one must not chamber in a
        // 5.56 gun. master: "give stanag mags a flag for 300blk vs 5.56, we'll handle that when we get to
        // reloading" -- so this is carried and asserted, and no reload code reads it yet.
        public string magRound;
        // The bullet TYPE (FMJ / AP / HP / ...), separate from magRound (the CARTRIDGE above). A round is (cartridge,
        // type) -- 5.56 FMJ vs 5.56 AP. Carried on ammo/mag items + tracked on the gun's chamber, opening the door for
        // AP/HP/FMJ loads instead of just "5.56" (master). null/"" = FMJ (the default/standard load).
        public string ammoType;
        public bool IsMagazine => magCapacity > 0;
        public float fuelCapacity;              // Fuel-type container (gas can): max fuel it holds (retail .dat "Fuel", e.g. Portable 500). 0 = not a fuel can.
        public bool IsFuelContainer => fuelCapacity > 0f;
        // Fluid CONTAINER (strawberry 2026-07-23): a held item that stores a fluid you pour into a tank / drink from --
        // water bottle, soda/cola bottle, canteen. fluidCapacity = max mL (0 = not a container). It spawns holding
        // fluidDefaultType (0 = None = spawns empty, e.g. a canteen you refill) at fluidDefaultQuality. The fluid
        // TYPE/QUALITY enums live in the game assembly (UnturnedGodot), which this core lib can't reference -> stored
        // here as their raw byte values and interpreted game-side by FluidItem.
        public float fluidCapacity;
        public byte fluidDefaultType;      // FluidType enum value the container spawns holding (0 = None = spawns empty)
        public byte fluidDefaultQuality;   // WaterQuality enum value it spawns at (0 = Clean)
        public bool IsFluidContainer => fluidCapacity > 0f;
        // Spawn CONDITION band (source ItemAsset Quality_Min/Quality_Max; defaults 10/90). A WORLD-spawned FOOD item
        // rolls its freshness (the Item.quality byte, 0-100) in [qualityMin, qualityMax]: perishable raw foods ship a
        // low Quality_Max (e.g. 50 -> can spawn already-moldy), preserved foods a high Quality_Min (MRE 50-100). Loaded
        // from content/consumable_stats.tsv (cols 10/11). Non-food items keep the defaults (their quality stays 100).
        public byte qualityMin = 10;
        public byte qualityMax = 90;
        // Loose per-round ammo (shotgun shells): stacks in a slot up to stackSize, matched to a gun by magCaliber (magCapacity 0).
        // A reload consumes shells from the stack rather than swapping a whole magazine. (12/20 Gauge Shells, stack 32.)
        public bool isAmmo;
        public int stackSize = 1;   // max per-slot stack (Unturned items = 1; ammo like shotgun shells stack, e.g. 32)
        /// <summary>Per-shell damage override (0 = use the gun's Damage). A shotgun fires buckshot, slugs and
        /// beanbags from ONE gun, so the shell has to carry its own number -- the gun's single Damage field is
        /// per-pellet buckshot, and a 1-pellet slug reading it would deal a ninth of a shot.</summary>
        public float damageOverride = 0f;
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
        public bool gunChambered;      // a round sits in the chamber -- a real persistent state (survives holster/drop like gunAmmo), replacing the old HasChamber&&Ammo>0 inference (master)
        public string gunChamberedType;   // the bullet TYPE of the loaded rounds / chambered round (FMJ/AP/HP), persisted with the gun (master)
        public int gunFiremode = -1;   // fire-mode index (Safety/Semi/Burst/Auto)
        public int gunMagId = -1;      // the magazine item id currently loaded
        public int gunAttach = -1;     // attachment bitmask (which slots are attached; -1 = unset -> gun's defaults)
        // WHICH attachment item is installed in each slot -- an ITEM ID, not a flag, because an attachment is a
        // real object that came out of your bag and has to go back into it (strawberry: "irons are their own item
        // and can be installed across weapons"). The magazine slot already had gunMagId above and keeps using it;
        // these four are its siblings. -1 = nothing installed. Living on the ITEM, not the viewmodel, is what makes
        // a fitted scope survive holstering, dropping and picking the gun back up -- the same reason gunAmmo does.
        public int gunSightId = -1, gunBarrelId = -1, gunGripId = -1, gunTacticalId = -1;
        // Has this gun already been given its factory attachments? A SEPARATE flag because -1 cannot carry it:
        // -1 has to mean both "never fitted" and "the player took it off", and seeding keys on -1, so without
        // this every re-equip silently re-installs the sight the player just removed.
        public bool gunAttachSeeded;
        // Fuel carried on the item (strawberry): a generator REMEMBERS its tank through pickup <-> inventory <-> drop
        // <-> re-place, and a gas can holds the fuel pumped into it. HP rides on `quality` (0-100 %); fuel is its own
        // float. -1 = fresh -> full (a picked-up gen = its tank; a fresh can = its fuelCapacity).
        public float fuelLevel = -1f;
        // Fluid CONTENTS carried on a fluid-container item (strawberry 2026-07-23): the bottle/canteen REMEMBERS its
        // fluid through hands <-> inventory <-> drop. type/quality are the game FluidType/WaterQuality enum BYTE values
        // (interpreted by FluidItem); amount is mL. amount = -1 = fresh/uninitialized -> FluidItem lazily fills it from
        // the asset's default on first read (mirrors fuelLevel = -1 = fresh).
        public byte fluidType;
        public float fluidAmount = -1f;
        public byte fluidQuality;
        // AUTODRINK (strawberry): a fluid container with this ON passively sips 50 mL to keep the holder hydrated while it
        // sits in the bag (only for SAFE liquids -- FluidDef.Safe; gated game-side). Defaults ON; a per-item toggle in the
        // inventory flips it. Non-container items ignore it.
        public bool autoDrink = true;
        // FOOD SPOILAGE (strawberry): a food item's `quality` (0-100) doubles as its FRESHNESS/condition -- it ticks down
        // once per in-game day at a per-food-type rate (FoodSpoil). `preserved` HALTS spoilage (a fridge sets it; the fridge
        // itself is stubbed, but the flag + skip path are wired). Non-food items ignore both.
        public bool preserved;
        // The CARTRIDGE this magazine is currently loaded with (strawberry: drag-load loose rounds into a mag). Set
        // when the FIRST round enters an empty mag, cleared when it empties. Distinct from the ASSET's default
        // magRound -- a STANAG body physically feeds 5.56 OR .300 BLK, so an empty STANAG mag takes either, then LOCKS
        // to whatever went in (no mixing until it's unloaded). null = empty / follow the asset default.
        public string magLoadedRound;
        // COOKING (strawberry 2026-09-05: "give food a cooked %, increases from 0-100%, 90-100% cooked is
        // Cooked. above 100% is burnt. as well as a quality, just a flag between average (no label),
        // microwaved, charcoal grilled").
        //
        // `cooked` is DELIBERATELY NOT `quality`. quality is already FRESHNESS -- FoodSpoil ticks it down per
        // in-game day and a fridge halts it -- so folding cooking into it would mean roasting a steak made it
        // fresher and chilling it un-cooked it. Two axes, because they are two things.
        //
        // It is a byte that keeps counting PAST 100 rather than a clamped percentage plus a burnt flag,
        // because "above 100% is burnt" makes the overshoot the actual state; a separate flag is a second
        // source of truth that can disagree with the number beside it. Cooking.MaxCooked stops the count
        // before it can wrap a forgotten roast back around to raw.
        public byte cooked;
        public byte cookStyle;   // ECookStyle: 0 plain (no label) / 1 microwaved / 2 charcoal grilled
        /// <summary>0-100 % frozen (strawberry 2026-09-06: "frozen acts as a %"). Climbs in a freezer, falls
        /// anywhere else, and falls faster in something that is trying to cook it. A frozen item cannot be eaten
        /// and cannot start cooking; at 100 it never spoils at all.
        ///
        /// Deliberately NOT folded into `quality`, which is FRESHNESS and moves on its own clock -- a rock-solid
        /// steak and a fresh one are the same freshness and completely different to eat. Two independent axes
        /// need two fields, the same argument that gave `cooked` its own.</summary>
        public byte frozen;

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
        static readonly System.Random _lootRng = new();   // world-loot condition roll (freshness of spawned food) -- source uses UnityEngine.Random

        public static void add(ItemAsset a) { if (a != null) { _byId[a.id] = a; if (!string.IsNullOrEmpty(a.guid)) _byGuid[a.guid] = a; } }
        // World loot factory: a magazine spawns FULL (Military Magazine = 30 rounds) rather than empty (master). Other items = 1.
        public static Item makeLoot(ushort id)
        {
            var a = find(id);
            var it = new Item(id, a != null && a.IsMagazine ? (byte)System.Math.Max(1, a.magCapacity) : (byte)1);
            if (a != null && a.type == EItemType.FOOD)   // FOOD spawns at a random CONDITION (source Item ctor: Random(Quality_Min, Quality_Max)); <50 = moldy = infects you to eat, and it decays a bit more each in-game day (FoodSpoil)
                it.quality = (byte)_lootRng.Next(System.Math.Min(a.qualityMin, a.qualityMax), System.Math.Max(a.qualityMin, a.qualityMax) + 1);
            if (a != null && a.IsFuelContainer) it.fuelLevel = 0f;   // a fresh gas can starts EMPTY -> fill it at a pump
            if (a != null && a.IsFluidContainer)                     // a fluid container spawns holding its default fluid (a bottle = full; a canteen = empty)
            {
                it.fluidType = a.fluidDefaultType;
                it.fluidAmount = a.fluidDefaultType != 0 ? a.fluidCapacity : 0f;
                it.fluidQuality = a.fluidDefaultQuality;
            }
            return it;
        }
        // The set of CARTRIDGES a magazine BODY accepts = the union of magRound across every magazine sharing its
        // magCaliber (the mechanical body group). Data-driven: a STANAG body (caliber 1) has both a 5.56 mag (6) and a
        // .300 BLK mag (9142) defined, so it accepts {5.56, .300}; an AUG body (caliber 201) has only 5.56 mags -> {5.56}.
        // Called only while a bullet is dragged over a mag, so scanning the table is fine.
        public static HashSet<string> MagAcceptedRounds(ItemAsset mag)
        {
            var set = new HashSet<string>();
            if (mag == null || !mag.IsMagazine) return set;
            foreach (var a in _byId.Values)
                if (a.IsMagazine && a.magCaliber == mag.magCaliber && !string.IsNullOrEmpty(a.magRound))
                    set.Add(a.magRound);
            return set;
        }
        // ---- cartridge ids, for the wire -------------------------------------------------------------
        // A magazine's per-instance cartridge lock (Item.magLoadedRound) is a STRING, and NetPak has no
        // string primitive -- so it travels as a 1-byte index into the sorted set of distinct magRound
        // values. Sorted, so both sides derive the same order from the same item table; that they HAVE the
        // same table is the content-hash handshake's guarantee, which is exactly what CraftCommand's
        // blueprint index already leans on. 0 means "no lock".
        static List<string> _magRounds;
        static List<string> MagRoundTable()
        {
            if (_magRounds != null) return _magRounds;
            var set = new HashSet<string>();
            foreach (var a in _byId.Values)
                if (!string.IsNullOrEmpty(a.magRound)) set.Add(a.magRound);
            var list = new List<string>(set);
            list.Sort(System.StringComparer.Ordinal);
            _magRounds = list;
            return list;
        }

        public static byte MagRoundToId(string round)
        {
            if (string.IsNullOrEmpty(round)) return 0;
            int i = MagRoundTable().IndexOf(round);
            return i < 0 ? (byte)0 : (byte)System.Math.Min(255, i + 1);
        }

        public static string MagRoundFromId(byte id)
        {
            if (id == 0) return null;
            var t = MagRoundTable();
            return id - 1 < t.Count ? t[id - 1] : null;
        }

        public static ItemAsset find(ushort id) => _byId.TryGetValue(id, out var a) ? a : null;
        public static ItemAsset findByGuid(string guid) => !string.IsNullOrEmpty(guid) && _byGuid.TryGetValue(guid, out var a) ? a : null;
        public static IEnumerable<ItemAsset> all() => _byId.Values;
        // _magRounds is DERIVED from the item table, so it has to die with it -- a stale cartridge table
        // would silently remap every lock the moment a test (or a mod reload) re-seeds the registry.
        public static void clear() { _byId.Clear(); _byGuid.Clear(); _magRounds = null; }
    }
}
