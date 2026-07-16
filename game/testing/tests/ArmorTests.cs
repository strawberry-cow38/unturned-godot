using Godot;
using SDG.Unturned;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // Port of --armortest: worn clothing's whole-body protection aggregates as a PRODUCT of every worn piece
    // (source PlayerClothing) for fall + explosion armor; real catalog values are wired from clothing_armor.tsv;
    // and any worn Prevents_Falling_Broken_Bones piece stops leg-break (source PlayerLife:2436).
    // NOTE: rebuilds the catalog via RegisterAll mid-test -- RegisterAll starts with Assets.clear(), so this
    // leaves the shared boot's catalog valid for later tests (plus inert test ids 9001-9003).
    public class ArmorProductAggregation : GameTest
    {
        public override string Name => "armor.product_aggregation";
        public override IEnumerable<Step> Run()
        {
            Assets.clear();
            Assets.add(new ItemAsset { id = 9001, itemName = "Test Vest", type = EItemType.VEST, fallingDamageMultiplier = 0.5f, explosionArmor = 0.7f });
            Assets.add(new ItemAsset { id = 9002, itemName = "Test Hat", type = EItemType.HAT, fallingDamageMultiplier = 0.8f });
            var inv = new PlayerInventory();
            float bare = inv.FallingDamageMultiplier;
            T.Check($"bare player = 1.0 (got {bare:0.##})", Mathf.Abs(bare - 1f) < 1e-4f);
            inv.wearVest(new Item(9001));
            inv.wearHat(new Item(9002));
            T.Check($"fall armor is a product: .5 x .8 = .40 (got {inv.FallingDamageMultiplier:0.###})", Mathf.Abs(inv.FallingDamageMultiplier - 0.40f) < 1e-4f);
            T.Check($"explosion armor: vest .7 (got {inv.ExplosionArmor:0.###})", Mathf.Abs(inv.ExplosionArmor - 0.70f) < 1e-4f);

            // real data: RegisterAll loads the catalog + wires clothing_armor.tsv onto the actual items
            ItemCatalog.RegisterAll();
            var boots = Assets.find(1839);   // fall gear (Falling_Damage_Multiplier 0.05)
            var mil = Assets.find(2);        // armored top (Armor/Armor_Explosion 0.95)
            T.Check("real data: id1839 fall mult .05", boots != null && Mathf.Abs(boots.fallingDamageMultiplier - 0.05f) < 1e-3f);
            T.Check("real data: id2 explosion armor .95", mil != null && Mathf.Abs(mil.explosionArmor - 0.95f) < 1e-3f);

            // bone-proof: any worn piece with Prevents_Falling_Broken_Bones stops leg-break
            Assets.add(new ItemAsset { id = 9003, itemName = "Test Boots", type = EItemType.PANTS, preventsFallingBoneBreak = true });
            var inv2 = new PlayerInventory();
            T.Check("bare: legs CAN break", !inv2.PreventsFallingBoneBreak);
            inv2.wearPants(new Item(9003));
            T.Check("boots on: bones protected", inv2.PreventsFallingBoneBreak);
            yield break;
        }
    }
}
