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

    // Regression (master 2026-07-20): a HOLDABLE item must offer a hand action in its item menu, not just Drop/Close.
    // The Rope (item 64) had the equip code (EquipRopeTool) but the menu special-cased only the Wire (id==65) and never
    // consulted the Rope, so it showed just Drop/Close -- "the option to hold is NOT THERE". InventoryUI.HasHandAction is
    // the data-driven holdable predicate the menu now gates on; assert every holdable KIND trips it (both tools via ToolDef)
    // and that a plain SUPPLY item -- the SAME item type as the rope -- does not (proving it's the registry, not the type).
    public class InventoryHandActions : GameTest
    {
        public override string Name => "inventory.hand_actions";
        public override IEnumerable<Step> Run()
        {
            // catalog-free: HasHandAction reads fields off the asset (gunName/meleeName/IsConsumable/IsFuelContainer) + the
            // DeployableDef/ToolDef registries by id, so bare ItemAssets exercise every branch without RegisterAll.
            T.Check("rope (tool 64) offers a hand action", InventoryUI.HasHandAction(new ItemAsset { id = 64, type = EItemType.SUPPLY }));
            T.Check("wire (tool 65) offers a hand action", InventoryUI.HasHandAction(new ItemAsset { id = 65, type = EItemType.SUPPLY }));
            T.Check("a gun offers a hand action", InventoryUI.HasHandAction(new ItemAsset { gunName = "eaglefire" }));
            T.Check("a melee offers a hand action", InventoryUI.HasHandAction(new ItemAsset { meleeName = "knife_military" }));
            T.Check("a consumable (FOOD) offers a hand action", InventoryUI.HasHandAction(new ItemAsset { type = EItemType.FOOD }));
            T.Check("a fuel can offers a hand action", InventoryUI.HasHandAction(new ItemAsset { fuelCapacity = 500f }));
            // a fluid CONTAINER (bottle/canteen) offers a hand action even when it's a GENERIC-type EMPTY canteen (not
            // IsConsumable) -- pre-fix an empty canteen had the equip code but NO Hold button (the exact rope-style gap).
            T.Check("a fluid container (GENERIC canteen) offers a hand action", InventoryUI.HasHandAction(new ItemAsset { id = 63334, type = EItemType.GENERIC, fluidCapacity = 500f }));
            T.Check("a plain SUPPLY item (no ToolDef entry) offers NO hand action", !InventoryUI.HasHandAction(new ItemAsset { id = 63333, type = EItemType.SUPPLY }));
            T.Check("null asset -> no hand action", !InventoryUI.HasHandAction(null));
            yield break;
        }
    }

    // strawberry's two drink modes: equipped + LMB = CHUG the whole bottle; autodrink = passive 50 mL sips of a SAFE bottle
    // in the bag when hydration falls below the floor. Also guards the earlier fix — a fluid container (also EItemType.WATER
    // = IsConsumable) must equip as a CONTAINER, never reach the consumable chug path. Uses the REAL catalog (14/473/337).
    public class FluidContainerDrinkModes : GameTest
    {
        public override string Name => "fluid.container_drink_modes";
        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            foreach (var (id, name) in new[] { ((ushort)14, "Bottled Water"), ((ushort)473, "Bottled Soda"), ((ushort)337, "Canteen") })
            {
                var a = Assets.find(id);
                T.Check($"{name} ({id}) resolves as a fluid container", a != null && a.IsFluidContainer);
                var p = new PlayerController { Water = 0.1f, Inventory = new PlayerInventory() };
                p.EquipItemAsset(a, new Item(id));
                T.Check($"{name} equips as a container, NOT a chug-consumable", !p.HoldingConsumable);
            }

            // equipped + LMB = CHUG: empties the whole bottle at once, big hydration, the (empty) bottle stays reusable
            var wb = Assets.find(14);
            var p1 = new PlayerController { Water = 0.1f, Inventory = new PlayerInventory() };
            var bottle = new Item(14);
            p1.EquipHeldFluidContainer(wb, bottle);
            FluidItem.Read(bottle, wb, out _, out float b4, out _);
            p1.DebugDrinkContainer();
            FluidItem.Read(bottle, wb, out _, out float aft, out _);
            T.Check($"chug empties the WHOLE bottle (was {b4:0} mL, now {aft:0})", b4 > 900f && aft < 1f);
            T.Check("chug raised hydration a lot (1 L water)", p1.Water > 0.4f);
            T.Check("the empty bottle is still held (reusable)", !p1.Unarmed);

            // autodrink: a safe bottle in the bag sips 50 mL when hydration is below the floor
            var p2 = new PlayerController { Water = 0.2f, Inventory = new PlayerInventory() };
            var b2 = new Item(14);
            p2.Inventory.items[2].tryAddItem(b2);
            float w0 = p2.Water;
            p2.DebugAutoDrinkTick(1f);
            T.Check($"autodrink sipped ~5% hydration (a 50 mL sip); {w0:0.00}->{p2.Water:0.00}", System.Math.Abs((p2.Water - w0) - 0.05f) < 0.006f);
            FluidItem.Read(b2, wb, out _, out float left, out _);
            T.Check($"autodrink drained 50 mL from the bottle (1000 -> {left:0})", System.Math.Abs(left - 950f) < 1f);

            // autodrink NEVER touches an unsafe fluid (tainted water) or an autodrink-OFF bottle
            var p3 = new PlayerController { Water = 0.2f, Inventory = new PlayerInventory() };
            var tainted = new Item(14); FluidItem.Write(tainted, FluidType.Water, 1000f, WaterQuality.Tainted);
            p3.Inventory.items[2].tryAddItem(tainted);
            float w3 = p3.Water; p3.DebugAutoDrinkTick(1f);
            T.Check("autodrink refuses UNSAFE tainted water", System.Math.Abs(p3.Water - w3) < 0.001f);
            var p4 = new PlayerController { Water = 0.2f, Inventory = new PlayerInventory() };
            var off = new Item(14) { autoDrink = false };
            p4.Inventory.items[2].tryAddItem(off);
            float w4 = p4.Water; p4.DebugAutoDrinkTick(1f);
            T.Check("autodrink respects the OFF toggle", System.Math.Abs(p4.Water - w4) < 0.001f);

            // ONE active bottle at a time (strawberry): with two enabled full bottles, only the FIRST is active; empties ->
            // the next takes over; a DISABLED bottle is skipped. ActiveAutoDrink is the single source both drink + icon use.
            var p5 = new PlayerController { Water = 0.2f, Inventory = new PlayerInventory() };
            var f1 = new Item(14); var f2 = new Item(14);
            p5.Inventory.items[2].tryAddItem(f1); p5.Inventory.items[2].tryAddItem(f2);
            FluidItem.Read(f1, wb, out _, out _, out _); FluidItem.Read(f2, wb, out _, out _, out _);   // lazy-init both full
            T.Check("only the FIRST enabled bottle is the active autodrink one", ReferenceEquals(FluidItem.ActiveAutoDrink(p5.Inventory), f1));
            FluidItem.Write(f1, FluidType.Water, 0f, WaterQuality.Clean);   // empty the active
            T.Check("empty active -> the next enabled bottle takes over", ReferenceEquals(FluidItem.ActiveAutoDrink(p5.Inventory), f2));
            FluidItem.Write(f1, FluidType.Water, 1000f, WaterQuality.Clean); f1.autoDrink = false;   // refill but DISABLE it
            T.Check("a DISABLED bottle is skipped even when full", ReferenceEquals(FluidItem.ActiveAutoDrink(p5.Inventory), f2));
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

    // strawberry: the console time commands parse noon/midnight/8am/1800/18:30 into a time of day. Locks the tricky cases
    // (12am=00:00, 12pm=12:00, military HHMM, am/pm, named) + the readout round-trip. Pure DevConsole.ParseClock/FormatTime.
    public class ConsoleTimeParse : GameTest
    {
        public override string Name => "console.time_parse";
        public override IEnumerable<Step> Run()
        {
            void Hr(string s, float wantHours)
            {
                bool ok = DevConsole.ParseClock(s, out float h, allowNeg: false);
                T.Check($"'{s}' -> {wantHours:0.##}h (got {(ok ? h.ToString("0.##") : "PARSE-FAIL")})", ok && Mathf.Abs(h - wantHours) < 0.01f);
            }
            Hr("noon", 12f); Hr("midnight", 0f); Hr("dawn", 6f); Hr("dusk", 18f);
            Hr("8am", 8f); Hr("8pm", 20f); Hr("12am", 0f); Hr("12pm", 12f); Hr("8:30am", 8.5f);
            Hr("1800", 18f); Hr("0830", 8.5f); Hr("18:30", 18.5f); Hr("6", 6f);
            T.Check("garbage 'banana' is rejected", !DevConsole.ParseClock("banana", out _, false));
            T.Check("negative rejected when not allowed", !DevConsole.ParseClock("-3", out _, false));
            T.Check("negative ALLOWED for timeAdd", DevConsole.ParseClock("-3", out float neg, true) && Mathf.Abs(neg + 3f) < 0.01f);
            // readout: 0.5 -> noon, 0.75 -> dusk, 0.0 -> midnight
            T.Check($"FormatTime(0.5) = noon ({DevConsole.FormatTime(0.5f)})", DevConsole.FormatTime(0.5f).StartsWith("12:00"));
            T.Check($"FormatTime(0.75) = dusk ({DevConsole.FormatTime(0.75f)})", DevConsole.FormatTime(0.75f).StartsWith("18:00"));
            T.Check($"FormatTime(0) = midnight ({DevConsole.FormatTime(0f)})", DevConsole.FormatTime(0f).StartsWith("00:00"));
            yield break;
        }
    }
}
