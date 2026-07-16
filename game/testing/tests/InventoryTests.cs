using Godot;
using SDG.Unturned;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // Port of --invdragtest: the 9-page grid drag logic -- move to empty, out-of-bounds reject, swap, and a
    // cross-page move into a worn backpack. Pure inventory logic on a detached PlayerInventory.
    public class InvDragSwapCrosspage : GameTest
    {
        public override string Name => "inv.drag_swap_crosspage";
        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            var inv = new PlayerInventory();
            var pk = inv.items[2];                     // pockets 5x3
            pk.tryAddItem(new Item(15));               // Medkit 2x2 -> (0,0)
            pk.tryAddItem(new Item(14));               // Bottled Water 1x1 -> (2,0)
            pk.tryAddItem(new Item(95));               // Bandage 1x1 -> (3,0)

            T.Check("move-to-empty returns true", inv.TryDrag(2, 2, 0, 2, 4, 2, 0));
            T.Check("water now at (4,2)", pk.getItem(4, 2)?.item.id == 14);
            T.Check("old cell (2,0) freed", pk.getItem(2, 0) == null);

            T.Check("OOB move returns false", !inv.TryDrag(2, 4, 2, 2, 10, 10, 0));
            T.Check("water still at (4,2)", pk.getItem(4, 2)?.item.id == 14);

            T.Check("swap returns true", inv.TryDrag(2, 4, 2, 2, 3, 0, 0));
            T.Check("water swapped to (3,0)", pk.getItem(3, 0)?.item.id == 14);
            T.Check("bandage swapped to (4,2)", pk.getItem(4, 2)?.item.id == 95);

            inv.wearBackpack(new Item(253));           // Alicepack 8x7
            T.Check("cross-page move returns true", inv.TryDrag(2, 3, 0, PlayerInventory.BACKPACK, 5, 5, 0));
            T.Check("water now in backpack (5,5)", inv.items[PlayerInventory.BACKPACK].getItem(5, 5)?.item.id == 14);
            T.Check("water gone from pockets", pk.getItem(3, 0) == null);
            yield break;
        }
    }

    // Port of --invusetest: PlayerController.Consume applies the real .dat consumable effects to the vitals --
    // Medkit +75 hp + stops bleeding, Canned Beans +food (health capped), Bottled Water +water, energy/antibiotics,
    // and the previously-inert catalog items work from consumable_stats.tsv.
    public class VitalsConsumeEffects : GameTest
    {
        public override string Name => "vitals.consume_effects";
        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            var p = new PlayerController { Health = 20f, Food = 0.1f, Water = 0.1f, Bleeding = true };   // detached: pure vitals math

            p.Consume(Assets.find(15));   // Medkit: +75 health, stop bleeding
            T.Check("medkit -> health 20+75=95", Mathf.Abs(p.Health - 95f) < 0.01f);
            T.Check("medkit -> bleeding cleared", !p.Bleeding);
            p.Consume(Assets.find(13));   // Canned Beans: +10 health, +55 food
            T.Check("beans -> food 0.1+0.55=0.65", Mathf.Abs(p.Food - 0.65f) < 0.01f);
            T.Check("beans -> health capped at 100", Mathf.Abs(p.Health - 100f) < 0.01f);
            p.Consume(Assets.find(14));   // Bottled Water: +55 water
            T.Check("water -> water 0.1+0.55=0.65", Mathf.Abs(p.Water - 0.65f) < 0.01f);

            p.Stamina = 0.1f; p.Infection = 0.5f;
            p.Consume(Assets.find(93));   // Bottled Energy: +55 water, +75 energy
            T.Check("energy -> stamina 0.1+0.75=0.85", Mathf.Abs(p.Stamina - 0.85f) < 0.01f);
            var abx = Assets.find(389);   // Antibiotics: disinfectant 35 in consumable_stats.tsv (the old inline test
            T.Check("antibiotics (389) have a disinfectant effect", abx != null && abx.useDisinfectant > 0);   // probed id 11 behind a guard that silently skipped -- 11 has no stats row)
            p.Consume(abx);
            T.Check("antibiotics -> infection dropped", p.Infection < 0.5f);
            var cola = Assets.find(80);   // Canned Cola -- was inert (no hardcoded stats); works from the .dat
            T.Check("previously-inert cola is now IsConsumable", cola != null && cola.IsConsumable);
            T.Check("cola has real .dat effects (water/energy)", cola != null && (cola.useWater > 0 || cola.useEnergy > 0));
            p.QueueFree();
            yield break;
        }
    }

    // Port of --consumeholdtest: the full inventory HOLD flow -- equip a consumable to hand, click to eat,
    // decrement the stack, auto-unequip when the last one is gone -- plus the per-item Use clips, sounds, and
    // flat colours (source UseableConsumeable equip->use->remove).
    public class InvConsumeHoldFlow : GameTest
    {
        public override string Name => "inv.consume_hold_flow";
        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            var p = new PlayerController { Health = 50f, Food = 0.1f, Water = 0.1f, Inventory = new PlayerInventory() };
            p.Inventory.items[2].tryAddItem(new Item(13));   // 2x Canned Beans, each its own grid item
            p.Inventory.items[2].tryAddItem(new Item(13));

            var beans = Assets.find(13);
            T.Check("beans resolves + IsConsumable", beans != null && beans.IsConsumable);
            T.Check("beans has a held mesh in the registry", ConsumableRegistry.Mesh(13) != null);
            T.Check("start count = 2", p.Inventory.getItemCount(13) == 2);

            p.EquipHeldConsumable(beans, ConsumableRegistry.Mesh(13));
            T.Check("holding after equip", p.HoldingConsumable);
            T.Check("no stack spent just by holding", p.Inventory.getItemCount(13) == 2);

            p.StartConsume();                                          // 1st click -> eat
            for (int i = 0; i < 200; i++) p.DebugConsumeTick(0.05f);   // 10s > the longest per-item Use clip
            T.Check("food rose after 1st eat", p.Food > 0.5f);
            T.Check("count -> 1 after 1st eat", p.Inventory.getItemCount(13) == 1);
            T.Check("still holding (1 left)", p.HoldingConsumable);

            p.StartConsume();                                          // 2nd click -> eat the last one
            for (int i = 0; i < 200; i++) p.DebugConsumeTick(0.05f);
            T.Check("count -> 0 after 2nd eat", p.Inventory.getItemCount(13) == 0);
            T.Check("auto-unequipped when depleted", !p.HoldingConsumable);

            // per-item eat/drink archetypes (each item plays its own Use clip; useTime = that clip's length)
            var beansAn = ConsumableRegistry.Anims("canned_beans");
            var waterAn = ConsumableRegistry.Anims("bottled_water");
            var medkitAn = ConsumableRegistry.Anims("medkit");
            T.Check("beans has a Use archetype clip", !string.IsNullOrEmpty(beansAn.Use));
            T.Check("drink clip != eat clip (per-item)", waterAn.Use != beansAn.Use && !string.IsNullOrEmpty(waterAn.Use));
            T.Check("syringe/medkit clip != eat clip", medkitAn.Use != beansAn.Use && !string.IsNullOrEmpty(medkitAn.Use));
            T.Check("per-item useTime from Use-clip length", waterAn.UseLen > 0f && Mathf.Abs(waterAn.UseLen - beansAn.UseLen) > 0.01f);

            // per-item use/eat/drink SOUND (source ItemConsumeableAsset.use)
            T.Check("beans use-sound = eatcanl", ConsumableRegistry.Sound(13) == "eatcanl");
            T.Check("water use-sound = drinkswallow", ConsumableRegistry.Sound(14) == "drinkswallow");
            T.Check("medkit use-sound = use_medkit", ConsumableRegistry.Sound(15) == "use_medkit");
            T.Check("beans WAV loads as 16-bit PCM", PlayerController.DebugCanLoadWav("eatcanl"));
            T.Check("water WAV loads as 16-bit PCM", PlayerController.DebugCanLoadWav("drinkswallow"));

            // no-texture consumables use their flat _Color (cheese=yellow, potato=brown), not the gray default
            T.Check("cheese has a flat _Color (no texture)", ConsumableRegistry.FlatColor("cheese") is Color cc && cc.G > 0.5f && cc.B < 0.5f);
            T.Check("potato has a flat _Color", ConsumableRegistry.FlatColor("potato") != null);
            T.Check("textured item (canned_beans) has NO flat color", ConsumableRegistry.FlatColor("canned_beans") == null);
            p.QueueFree();
            yield break;
        }
    }
}
