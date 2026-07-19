// A clothing item's data read from a real Unturned ItemClothingAsset-family .dat through the ported UnturnedDat
// layer, using the SAME accessors (ParseFloat/ParseInt32/ParseUInt8/ParseUInt16/GetString/ContainsKey/ParseBool)
// the game's ItemClothingAsset/ItemBagAsset/ItemGearAsset/... PopulateAsset(...) use -- so the numbers/flags are the
// real ones, not placeholders. Mirrors the shape of game/GunDef.cs (POCO + static FromDatText), but lives engine-free
// in core/UnturnedSim next to ItemAsset (no Godot dependency, so it is unit-testable by UnturnedSim.Tests).
//
// This is PHASE 1 (data types only) of the clothing/playermodel port: parse the .dat DATA. The actual texture/mesh/
// prefab bundle objects (Shirt/Pants/Emission/Metallic textures, Hat/Mask/Glasses/Vest/Backpack prefab GameObjects,
// Character_Mesh_*_Override meshes, Character_Material_Override) are asset-bundle refs resolved in P2 -- the content-
// pointer string fields below are DECLARED but left null here; P2 populates them.
//
// Source of truth (U3-SDK Assembly-CSharp/Unturned/Bundles/):
//   ItemClothingAsset.cs (base), ItemBagAsset.cs, ItemGearAsset.cs, ItemShirtAsset.cs, ItemPantsAsset.cs,
//   ItemVestAsset.cs, ItemHatAsset.cs, ItemMaskAsset.cs, ItemGlassesAsset.cs, ItemBackpackAsset.cs.
//
// NOTE on Pro/Gold items: source ItemClothingAsset.PopulateAsset forces armor/explosionArmor/fallingDamageMultiplier
// to their defaults (1.0) when isPro. This port targets real PEI items (id < 1000), none of which are Pro, so isPro
// is not modeled here -- feeding a Pro .asset to FromDatText would read its literal Armor keys rather than forcing 1.0.
namespace SDG.Unturned
{
    public sealed class ClothingDef
    {
        public string id;
        public EItemType slot;   // which slot this clothing occupies (drives the gear vs. bag parse branches below)

        // --- ItemClothingAsset (base) shared protection / visual keys ---
        // Armor: multiplier to incoming damage. Present -> value; absent -> 1.0 (source: _armor = 1.0 when key absent).
        public float armor = 1f;
        // Armor_Explosion: multiplier to explosive damage. Absent -> defaults to `armor` (source ItemClothingAsset).
        public float explosionArmor = 1f;
        // Falling_Damage_Multiplier: default 1.0 (take normal fall damage).
        public float fallingDamageMultiplier = 1f;
        // Movement_Speed_Multiplier: default 1.0.
        public float movementSpeedMultiplier = 1f;
        // Proof_Water / Proof_Fire / Proof_Radiation: KEY-PRESENCE bools (ContainsKey), not true/false values.
        public bool proofWater;
        public bool proofFire;
        public bool proofRadiation;
        // Prevents_Falling_Broken_Bones: ParseBool, default false (if any worn piece has it, hard falls never break bones).
        public bool preventsFallingBrokenBones;
        // Hair_Visible / Beard_Visible: default true. See FromDatText -- GEAR (hat/mask/glasses) overrides these with the
        // key-presence of Hair / Beard instead (source ItemGearAsset.PopulateAsset).
        public bool hairVisible = true;
        public bool beardVisible = true;
        // Visible_On_Ragdoll: ParseBool, default TRUE.
        public bool visibleOnRagdoll = true;
        // Mirror_Left_Handed_Model: ParseBool, default TRUE (source shouldMirrorLeftHandedModel).
        public bool mirrorLeftHandedModel = true;

        // --- ItemBagAsset storage grid (shirt/pants/vest/backpack; 0 for gear which is not a bag) ---
        public byte width;    // Width  (ParseUInt8)
        public byte height;   // Height (ParseUInt8)

        // --- ItemShirtAsset: hand + mesh/material override FLAGS (the actual meshes/material are P2 bundle objects) ---
        public bool ignoreHand;          // Ignore_Hand (KEY-PRESENCE)
        public bool has1pMeshOverride;   // Has_1P_Character_Mesh_Override (ParseBool, default false)
        public int override3pLodCount;   // Character_Mesh_3P_Override_LODs (ParseUInt16 count; there is no bool "Has_3P..." key)
        public bool hasMaterialOverride; // Has_Character_Material_Override (ParseBool, default false)

        // --- ItemVestAsset: fallback shirt (shown when no shirt equipped) ---
        public bool hasFallbackShirt;    // Has_Fallback_Shirt (ParseBool, default false)
        public string fallbackShirt;     // Fallback_Shirt (a guid/id string; only read when hasFallbackShirt)

        // --- ItemGearAsset: hair/beard material-swap targets (mesh-renderer names). NonGold colors are optional/P2. ---
        public string hairOverride;      // Hair_Override (GetString)
        public string beardOverride;     // Beard_Override (GetString)

        // --- ItemMaskAsset ---
        public bool isEarpiece;                            // Earpiece (KEY-PRESENCE)
        public float filterDegradationRateMultiplier = 1f; // FilterDegradationRateMultiplier (ParseFloat, default 1.0)

        // --- ItemGlassesAsset ---
        public string vision;    // Vision (GetString; the ELightingVision name -- NONE/CIVILIAN/MILITARY/HEADLAMP; null = NONE)
        public bool isBlindfold; // Blindfold (KEY-PRESENCE)
        public bool isNightvisionAllowedInThirdPerson;  // Nightvision_Allowed_In_ThirdPerson (ParseBool; only read when Vision present)

        // --- Content-pointer fields (P2 populates from the asset bundle; NULL in P1) ---
        public string shirtTexture;    // ItemShirtAsset "Shirt"
        public string pantsTexture;    // ItemPantsAsset "Pants"
        public string emissionTexture; // "Emission"
        public string metallicTexture; // "Metallic"
        public string prefabMesh;      // Hat/Mask/Glasses/Vest/Backpack prefab GameObject ("Hat"/"Mask"/"Glasses"/"Vest"/"Backpack")

        // hat/mask/glasses extend ItemGearAsset (which overrides the hair/beard rule); everything else extends
        // ItemClothingAsset/ItemBagAsset directly.
        static bool IsGear(EItemType t) => t == EItemType.HAT || t == EItemType.MASK || t == EItemType.GLASSES;

        public static ClothingDef FromDatText(string datText, EItemType slot)
        {
            IDatDictionary d = new DatParser().Parse(datText);
            var c = new ClothingDef { slot = slot, id = d.GetString("ID") };

            // ItemClothingAsset base keys (source PopulateAsset, non-pro branch)
            c.armor = d.ContainsKey("Armor") ? d.ParseFloat("Armor") : 1f;
            c.explosionArmor = d.ContainsKey("Armor_Explosion") ? d.ParseFloat("Armor_Explosion") : c.armor;
            c.fallingDamageMultiplier = d.ParseFloat("Falling_Damage_Multiplier", 1f);
            c.movementSpeedMultiplier = d.ParseFloat("Movement_Speed_Multiplier", 1f);
            c.proofWater = d.ContainsKey("Proof_Water");
            c.proofFire = d.ContainsKey("Proof_Fire");
            c.proofRadiation = d.ContainsKey("Proof_Radiation");
            c.preventsFallingBrokenBones = d.ParseBool("Prevents_Falling_Broken_Bones");
            c.visibleOnRagdoll = d.ParseBool("Visible_On_Ragdoll", true);
            c.mirrorLeftHandedModel = d.ParseBool("Mirror_Left_Handed_Model", true);

            // Hair/beard visibility diverges by slot: gear reads the presence of the Hair/Beard keys (source
            // ItemGearAsset.PopulateAsset overrides the base after calling base.PopulateAsset); other clothing keeps
            // the base ParseBool("Hair_Visible"/"Beard_Visible", true).
            if (IsGear(slot))
            {
                c.hairVisible = d.ContainsKey("Hair");
                c.beardVisible = d.ContainsKey("Beard");
                c.hairOverride = d.GetString("Hair_Override");
                c.beardOverride = d.GetString("Beard_Override");
            }
            else
            {
                c.hairVisible = d.ParseBool("Hair_Visible", true);
                c.beardVisible = d.ParseBool("Beard_Visible", true);
            }

            // ItemBagAsset storage grid (absent on gear -> 0)
            c.width = d.ParseUInt8("Width");
            c.height = d.ParseUInt8("Height");

            // slot-specific keys
            switch (slot)
            {
                case EItemType.SHIRT:
                    c.ignoreHand = d.ContainsKey("Ignore_Hand");
                    c.has1pMeshOverride = d.ParseBool("Has_1P_Character_Mesh_Override", false);
                    c.override3pLodCount = d.ParseUInt16("Character_Mesh_3P_Override_LODs");
                    c.hasMaterialOverride = d.ParseBool("Has_Character_Material_Override", false);
                    break;
                case EItemType.VEST:
                    c.hasFallbackShirt = d.ParseBool("Has_Fallback_Shirt");
                    if (c.hasFallbackShirt) c.fallbackShirt = d.GetString("Fallback_Shirt");
                    break;
                case EItemType.MASK:
                    c.isEarpiece = d.ContainsKey("Earpiece");
                    c.filterDegradationRateMultiplier = d.ParseFloat("FilterDegradationRateMultiplier", 1f);
                    break;
                case EItemType.GLASSES:
                    if (d.ContainsKey("Vision"))
                    {
                        c.vision = d.GetString("Vision");
                        c.isNightvisionAllowedInThirdPerson = d.ParseBool("Nightvision_Allowed_In_ThirdPerson");
                    }
                    c.isBlindfold = d.ContainsKey("Blindfold");
                    break;
            }
            return c;
        }
    }
}
