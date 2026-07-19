using NUnit.Framework;
using SDG.Unturned;

namespace UnturnedSim.Tests
{
    // L0 tests pinning ClothingDef.FromDatText against REAL Unturned clothing .dat data (P1 of the clothing port).
    // The .dat text in each test is the verbatim key/value content of the named retail item file under
    // /home/ec2-user/unturned-bundles/Bundles/Items/... (the trailing Blueprints crafting block, which ClothingDef
    // does not read, is omitted). All are real PEI items (id < 1000) except the LS_Oversize case, which is the only
    // shipped file carrying Has_Fallback_Shirt (a newer .asset, flagged in that test).
    //
    // These pin the non-obvious 1:1 behaviors that a naive parser gets wrong: Armor_Explosion defaulting to Armor,
    // and the gear-vs-clothing split for Hair/Beard visibility (gear = key-presence of Hair/Beard; other clothing =
    // ParseBool Hair_Visible/Beard_Visible defaulting true).
    [TestFixture]
    public class ClothingDefTests
    {
        // Shirts/Vests/Hats/Backpacks/Pants/Masks/Glasses.  (id  Name)
        // 232  Construction_Top  (Shirt) -- Armor 0.95, Width 4, Height 3.  CRLF, to mirror the real on-disk bytes.
        const string ConstructionTop =
            "GUID 233f86ae53de4a76bc96c266d3adb537\r\n" +
            "Type Shirt\r\nRarity Uncommon\r\nUseable Clothing\r\nID 232\r\n\r\n" +
            "Size_X 3\r\nSize_Y 2\r\nSize_Z 0.6\r\n\r\n" +
            "Width 4\r\nHeight 3\r\n\r\n" +
            "Armor 0.95\r\n";

        // 10  Vest_Police  (Vest) -- Armor 0.8, Width 5, Height 4, NO Has_Fallback_Shirt (so it defaults false).
        const string VestPolice =
            "GUID fe9c024b96504144952c055aea57c5fc\n" +
            "Type Vest\nRarity Rare\nUseable Clothing\nID 10\n\n" +
            "Size_X 2\nSize_Y 2\nSize_Z 0.5\n\n" +
            "Width 5\nHeight 4\n\n" +
            "Armor 0.8\n";

        // 27  Tophat  (Hat = gear) -- Armor 0.95, bare `Beard` key present, NO `Hair` key.
        const string Tophat =
            "GUID ada195ec008f43ae90ebd34086412e8f\n" +
            "Type Hat\nUseable Clothing\nID 27\n\n" +
            "Size_X 2\nSize_Y 2\nSize_Z 0.4\n\n" +
            "Beard\n\n" +
            "Armor 0.95\n";

        // 253  Alicepack  (Backpack) -- Width 8, Height 7, NO Armor key (armor + explosionArmor default 1.0).
        const string Alicepack =
            "GUID c135cc7900f647d2b3e5ffdf45f9d449\n" +
            "Type Backpack\nRarity Epic\nUseable Clothing\nID 253\n\n" +
            "Size_X 2\nSize_Y 2\nSize_Z 0.6\n\n" +
            "Width 8\nHeight 7\n";

        // LS_Oversize  (Vest) -- the shipped oversize vest; verbatim full file. Has_Fallback_Shirt true + guid.
        // (Newer .asset format, Pro/no-id -- included only because it is the sole real file exercising fallbackShirt.)
        const string LsOversizeVest =
            "GUID 4ab8d527f4034f1ab3df486872482ad8\n" +
            "Type Vest\n\n" +
            "Bundle_Path_Include_Filename true\nPro\n\n" +
            "Skin_Override Model_0\n\n" +
            "Has_Fallback_Shirt true\n" +
            "Fallback_Shirt f058b6c06f644b1fad860aa9a5318bd0\n";

        [Test]
        public void Shirt_ParsesArmor_StorageGrid_AndClothingDefaults()
        {
            var c = ClothingDef.FromDatText(ConstructionTop, EItemType.SHIRT);

            Assert.That(c.id, Is.EqualTo("232"));
            Assert.That(c.slot, Is.EqualTo(EItemType.SHIRT));
            Assert.That(c.armor, Is.EqualTo(0.95f).Within(1e-6f));
            // Armor_Explosion absent -> defaults to armor (NOT 1.0). This is the source's default-to-armor rule.
            Assert.That(c.explosionArmor, Is.EqualTo(0.95f).Within(1e-6f));
            Assert.That(c.width, Is.EqualTo((byte)4));
            Assert.That(c.height, Is.EqualTo((byte)3));
            // clothing-item defaults (no keys present)
            Assert.That(c.fallingDamageMultiplier, Is.EqualTo(1f).Within(1e-6f));
            Assert.That(c.movementSpeedMultiplier, Is.EqualTo(1f).Within(1e-6f));
            Assert.That(c.proofWater, Is.False);
            Assert.That(c.proofFire, Is.False);
            Assert.That(c.proofRadiation, Is.False);
            Assert.That(c.preventsFallingBrokenBones, Is.False);
            Assert.That(c.visibleOnRagdoll, Is.True);      // Visible_On_Ragdoll defaults TRUE
            Assert.That(c.mirrorLeftHandedModel, Is.True); // Mirror_Left_Handed_Model defaults TRUE
            // non-gear slot: hair/beard visibility come from Hair_Visible/Beard_Visible (absent -> true)
            Assert.That(c.hairVisible, Is.True);
            Assert.That(c.beardVisible, Is.True);
            // shirt mesh-override flags absent -> false / 0
            Assert.That(c.ignoreHand, Is.False);
            Assert.That(c.has1pMeshOverride, Is.False);
            Assert.That(c.override3pLodCount, Is.EqualTo(0));
            Assert.That(c.hasMaterialOverride, Is.False);
        }

        [Test]
        public void Vest_ParsesArmor_And_DefaultsNoFallbackShirt()
        {
            var c = ClothingDef.FromDatText(VestPolice, EItemType.VEST);

            Assert.That(c.id, Is.EqualTo("10"));
            Assert.That(c.armor, Is.EqualTo(0.8f).Within(1e-6f));
            Assert.That(c.explosionArmor, Is.EqualTo(0.8f).Within(1e-6f));  // defaults to armor
            Assert.That(c.width, Is.EqualTo((byte)5));
            Assert.That(c.height, Is.EqualTo((byte)4));
            Assert.That(c.hasFallbackShirt, Is.False);
            Assert.That(c.fallbackShirt, Is.Null);
        }

        [Test]
        public void Hat_GearHairBeard_ComeFromKeyPresence_NotDefaults()
        {
            var c = ClothingDef.FromDatText(Tophat, EItemType.HAT);

            Assert.That(c.id, Is.EqualTo("27"));
            Assert.That(c.armor, Is.EqualTo(0.95f).Within(1e-6f));
            Assert.That(c.explosionArmor, Is.EqualTo(0.95f).Within(1e-6f));
            // GEAR rule: hairVisible = ContainsKey("Hair"). Tophat has NO Hair key -> FALSE (a shirt would default TRUE).
            Assert.That(c.hairVisible, Is.False);
            // GEAR rule: beardVisible = ContainsKey("Beard"). Tophat HAS the bare `Beard` key -> TRUE.
            Assert.That(c.beardVisible, Is.True);
            // a hat is not a bag -> no storage grid
            Assert.That(c.width, Is.EqualTo((byte)0));
            Assert.That(c.height, Is.EqualTo((byte)0));
        }

        [Test]
        public void Backpack_ParsesStorageGrid_AndArmorDefaultsToOne()
        {
            var c = ClothingDef.FromDatText(Alicepack, EItemType.BACKPACK);

            Assert.That(c.id, Is.EqualTo("253"));
            Assert.That(c.slot, Is.EqualTo(EItemType.BACKPACK));
            Assert.That(c.width, Is.EqualTo((byte)8));
            Assert.That(c.height, Is.EqualTo((byte)7));
            // no Armor key -> armor AND explosionArmor default to 1.0
            Assert.That(c.armor, Is.EqualTo(1f).Within(1e-6f));
            Assert.That(c.explosionArmor, Is.EqualTo(1f).Within(1e-6f));
            // non-gear -> hair/beard default visible
            Assert.That(c.hairVisible, Is.True);
            Assert.That(c.beardVisible, Is.True);
        }

        [Test]
        public void Vest_WithFallbackShirt_ParsesFlagAndGuid()
        {
            var c = ClothingDef.FromDatText(LsOversizeVest, EItemType.VEST);

            Assert.That(c.hasFallbackShirt, Is.True);
            Assert.That(c.fallbackShirt, Is.EqualTo("f058b6c06f644b1fad860aa9a5318bd0"));
        }
    }
}
