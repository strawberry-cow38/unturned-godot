using System.Collections.Generic;
using Godot;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    // Food spoilage + condition (strawberry): the retail "food condition" system ported from UseableConsumeable /
    // ItemAsset -- a FOOD item's `quality` (0-100) is its freshness. It spawns in a per-item band (Quality_Min/Max),
    // decays a slice per in-game day by food type (FoodSpoil), and eating one under 50% scales its nutrition down + can
    // infect you. All engine-free (pure statics + detached PlayerController vitals math), so it runs at L0.
    public class FoodSpoilage : GameTest
    {
        public override string Name => "food.spoilage";
        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();

            // ── per-food-type decay rates (FoodSpoil.PerDay): keyword heuristic, non-food = 0, dairy/meat > produce > canned.
            float beef   = FoodSpoil.PerDay(new ItemAsset { type = EItemType.FOOD, itemName = "Raw Beef" });
            float cheese = FoodSpoil.PerDay(new ItemAsset { type = EItemType.FOOD, itemName = "Cheese" });
            float potato = FoodSpoil.PerDay(new ItemAsset { type = EItemType.FOOD, itemName = "Potato" });
            float canned = FoodSpoil.PerDay(new ItemAsset { type = EItemType.FOOD, itemName = "Canned Beans" });
            T.Check("non-food never spoils (rate 0)", Mathf.Abs(FoodSpoil.PerDay(new ItemAsset { type = EItemType.GENERIC, itemName = "Rock" })) < 0.001f);
            T.Check("null asset -> rate 0", Mathf.Abs(FoodSpoil.PerDay(null)) < 0.001f);
            T.Check("all foods spoil at a positive rate", beef > 0f && cheese > 0f && potato > 0f && canned > 0f);
            T.Check("dairy/meat spoil faster than root veg (strawberry's milk>potato)", cheese > potato && beef > potato);
            T.Check("canned is the slowest (preserved)", canned < potato && canned < cheese);

            // ── TickDay: each FOOD item loses PerDay% of condition, preserved is skipped, clamps at 0, non-food untouched.
            Assets.add(new ItemAsset { id = 64010, type = EItemType.FOOD, itemName = "Test Cheese" });   // rate 20 (dairy)
            Assets.add(new ItemAsset { id = 64011, type = EItemType.FOOD, itemName = "Test Potato" });   // rate 5  (root)
            Assets.add(new ItemAsset { id = 64012, type = EItemType.GENERIC, itemName = "Test Rock" });  // not food
            var inv = new PlayerInventory();
            inv.items[2].tryAddItem(new Item(64010, 1, 100));                       // fresh cheese
            inv.items[2].tryAddItem(new Item(64011, 1, 100));                       // fresh potato
            inv.items[2].tryAddItem(new Item(64012, 1, 100));                       // a rock
            var frozen = new Item(64010, 1, 100) { preserved = true };             // cheese in a fridge
            inv.items[2].tryAddItem(frozen);
            var lowCheese = new Item(64010, 1, 10);                                 // nearly-spoiled cheese (rate 20 > 10)
            inv.items[2].tryAddItem(lowCheese);
            int spoiled = FoodSpoil.TickDay(inv);
            T.Check("cheese lost 20 condition (100->80)", inv.items[2].getItem(0)?.item.quality == 80);
            T.Check("potato lost 5 condition (100->95)", inv.items[2].getItem(1)?.item.quality == 95);
            T.Check("the rock is untouched (non-food)", inv.items[2].getItem(2)?.item.quality == 100);
            // A FRIDGE SLOWS, IT NO LONGER STOPS (strawberry 2026-09-06: "fridge'd items spoil at a much slower
            // rate", with the hard stop moved to 100 % frozen). This used to assert `frozen.quality == 100` --
            // the retired rule -- so it is rewritten to the new one rather than deleted, and it asserts the SHAPE
            // (much slower than open air, but not zero) rather than the exact 97, so retuning the multiplier does
            // not have to come back here.
            int fridgeLost = 100 - frozen.quality;
            int openLost = 100 - (inv.items[2].getItem(0)?.item.quality ?? 100);   // the same cheese, unrefrigerated
            T.Check($"refrigerated cheese still spoils ({fridgeLost} lost)", fridgeLost > 0);
            T.Check($"...but far slower than in the open ({fridgeLost} vs {openLost})", fridgeLost * 3 < openLost);
            T.Check("condition clamps at 0 (10 - 20 -> 0, not underflow)", lowCheese.quality == 0);
            T.Check($"TickDay counts everything that spoiled -- the refrigerated cheese now does too ({spoiled})", spoiled == 4);

            // ── retail eating formula (FoodSpoil.NutritionScale / MoldyInfection), ported byte-for-byte.
            T.Check("nutrition scales by condition/100", Mathf.Abs(FoodSpoil.NutritionScale(100) - 1f) < 1e-4f
                                                       && Mathf.Abs(FoodSpoil.NutritionScale(50) - 0.5f) < 1e-4f
                                                       && Mathf.Abs(FoodSpoil.NutritionScale(0)) < 1e-4f);
            T.Check("over-100 condition clamps the scale to 1", Mathf.Abs(FoodSpoil.NutritionScale(150) - 1f) < 1e-4f);
            T.Check("fresh food (>=50) never infects", Mathf.Abs(FoodSpoil.MoldyInfection(55, 0, 100)) < 1e-6f
                                                     && Mathf.Abs(FoodSpoil.MoldyInfection(55, 0, 50)) < 1e-6f);
            T.Check("moldy food infects, scaled: (55+0)*0.5*(1-0/50)/100 = 0.275 at q=0", Mathf.Abs(FoodSpoil.MoldyInfection(55, 0, 0) - 0.275f) < 1e-4f);
            T.Check("moldy scale is linear to the threshold: q=25 -> half of q=0", Mathf.Abs(FoodSpoil.MoldyInfection(55, 0, 25) - 0.1375f) < 1e-4f);
            T.Check("a no-nutrition item never infects (a spoiled bandage doesn't)", Mathf.Abs(FoodSpoil.MoldyInfection(0, 0, 0)) < 1e-6f);

            // ── end-to-end through PlayerController.Consume: fresh beans vs moldy beans (id 13, food 55).
            var beans = Assets.find(13);
            var pFresh = new PlayerController { Infection = 0f, Food = 0f };
            pFresh.Consume(beans, 100);
            T.Check("eating FRESH beans (q=100) does not infect you", pFresh.Infection < 1e-4f);
            T.Check("fresh beans feed you the full 0.55", Mathf.Abs(pFresh.Food - 0.55f) < 0.01f);
            pFresh.QueueFree();
            var pMoldy = new PlayerController { Infection = 0f, Food = 0f };
            pMoldy.Consume(beans, 10);
            T.Check("eating MOLDY beans (q=10) raises infection", pMoldy.Infection > 0.1f);
            T.Check("moldy beans feed you only ~10% (0.55 * 0.10)", Mathf.Abs(pMoldy.Food - 0.055f) < 0.005f);
            pMoldy.QueueFree();

            // ── makeLoot rolls FOOD condition inside the item's band; non-food spawns fresh (100).
            var carrot = Assets.find(329);   // perishable: Quality_Max 50 -> can spawn already moldy
            bool carrotInBand = carrot != null, sawVariation = false; int first = -1;
            for (int i = 0; i < 100 && carrot != null; i++)
            {
                int q = Assets.makeLoot(329).quality;
                if (q < carrot.qualityMin || q > carrot.qualityMax) carrotInBand = false;
                if (first < 0) first = q; else if (q != first) sawVariation = true;
            }
            T.Check("carrot (329) is a perishable band (Quality_Max 50)", carrot != null && carrot.qualityMax == 50);
            T.Check("world-spawned carrots always roll within [qualityMin, qualityMax]", carrotInBand);
            T.Check("the spawn condition actually varies (it's a roll, not a constant)", sawVariation);
            T.Check("a non-food item (bandage 95) spawns fresh at 100", Assets.makeLoot(95).quality == 100);

            // ── peekItemQuality returns the first-found instance (the one the next eat removes).
            var inv2 = new PlayerInventory();
            inv2.items[2].tryAddItem(new Item(13, 1, 42));
            inv2.items[2].tryAddItem(new Item(13, 1, 88));
            T.Check("peekItemQuality returns the first-found instance's condition (42)", inv2.peekItemQuality(13) == 42);
            T.Check("peekItemQuality of an absent id -> 100 (treated fresh)", inv2.peekItemQuality(9999) == 100);

            // ── the condition colour ramp (ItemTool.QualityColor, source getQualityColor): red@0 -> yellow@50 -> green@100.
            var c0 = ItemTool.QualityColor(0f); var c50 = ItemTool.QualityColor(0.5f); var c100 = ItemTool.QualityColor(1f);
            T.Check("0% condition is red (#bf1f1f)", Mathf.Abs(c0.R - 191f / 255f) < 0.01f && Mathf.Abs(c0.G - 31f / 255f) < 0.01f);
            T.Check("50% condition is yellow (#dcb413)", Mathf.Abs(c50.R - 220f / 255f) < 0.01f && Mathf.Abs(c50.G - 180f / 255f) < 0.01f);
            T.Check("100% condition is green (#1f871f)", Mathf.Abs(c100.R - 31f / 255f) < 0.01f && Mathf.Abs(c100.G - 135f / 255f) < 0.01f);
            T.Check("red->yellow half brightens (G rises 0->50%)", ItemTool.QualityColor(0.4f).G > ItemTool.QualityColor(0.1f).G);
            T.Check("yellow->green half sheds red (R falls 50->100%)", ItemTool.QualityColor(0.9f).R < ItemTool.QualityColor(0.6f).R);

            yield break;
        }
    }

    // The day/night clock's running Day counter (drives food spoilage): DayNightCycle.Advance bumps Day on each forward
    // midnight crossing (natural cycle or a dev timeAdd that laps midnight), handles multi-day jumps, and never rewinds Day.
    public class DayNightDayCounter : GameTest
    {
        public override string Name => "daynight.day_counter";
        public override IEnumerable<Step> Run()
        {
            var d = new DayNightCycle { Time = 0.9f, Day = 0 };
            d.Advance(0.2f);
            T.Check("crossing midnight bumps Day (0.9 + 0.2 -> day 1)", d.Day == 1);
            T.Check("Time wraps into [0,1) (0.1)", Mathf.Abs(d.Time - 0.1f) < 1e-4f);
            d.Advance(0.05f);
            T.Check("a within-day advance leaves Day alone", d.Day == 1 && Mathf.Abs(d.Time - 0.15f) < 1e-4f);
            d.Advance(2.5f);
            T.Check("a big advance laps multiple days at once (+2)", d.Day == 3);
            T.Check("Time still wraps correctly (0.15 + 2.5 -> 0.65)", Mathf.Abs(d.Time - 0.65f) < 1e-4f);
            d.Advance(-0.9f);
            T.Check("a rewind repositions Time but never decrements Day", d.Day == 3 && Mathf.Abs(d.Time - 0.75f) < 1e-4f);
            d.QueueFree();
            yield break;
        }
    }

    // Fridge / food preservation (strawberry): placed storage crates spoil their food over days like the bag, EXCEPT a
    // Refrigerator, which preserves its contents only while its OWN Consumer port is wired + powered (was global grid
    // power in the stub). Cut its power and the fridge warms up -- its food spoils too. Needs the scene tree (crates
    // live in the "crates" group, the fridge also in "deployables"), so it's Tier 1.
    public class FridgePreservesFood : GameTest
    {
        public override string Name => "survival.fridge_preserves";
        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            Assets.add(new ItemAsset { id = 64030, type = EItemType.FOOD, itemName = "Test Steak" });   // meat -> FoodSpoil rate 22/day

            var fridge = Refrigerator.Spawn(World, Vector3.Zero);
            var crate  = StorageCrate.Spawn(World, new Vector3(3f, 0f, 0f));
            var gen    = Deployable.Spawn(World, DeployableDef.Generator, new Vector3(-3f, 0f, 0f), 0f);
            yield return Ticks(2);   // _Ready builds each Storage grid + joins the "crates"/"deployables" groups

            var genOut = gen.Ports.Find(p => p.Kind == DeployableDef.PortKind.Output);
            PowerRig.Connect(World, genOut, fridge.ConsumerPort);
            gen.TogglePower();
            yield return Ticks(4);           // generator ramps up + the net re-solves -> the fridge's port powers
            PowerNet.Recompute(Tree);

            fridge.Add(new Item(64030, 1, 100));   // a fresh steak in the fridge
            crate.Add(new Item(64030, 1, 100));    // a fresh steak in a plain crate

            T.Check("a plain crate never preserves", !crate.Preserves);
            T.Check("a wired + powered fridge preserves", fridge.Preserves);

            // TickDayCrates is AUTHORITATIVE: it reconciles each item's `preserved` to its crate's live power, then spoils.
            FoodSpoil.TickDayCrates(Tree);
            T.Check("steak in a plain crate spoils (100 -> 78, meat rate 22)", crate.Storage.getItem(0)?.item.quality == 78);
            // Slowed, not halted (strawberry 2026-09-06). Asserted as an ordering against the plain crate in the
            // same tick, so this survives a retune of the multiplier -- the claim is "a fridge is much better than
            // a shelf and not as good as a freezer", which is the actual rule.
            int fridgeQ = fridge.Storage.getItem(0)?.item.quality ?? 0;
            T.Check($"steak in a powered fridge spoils SLOWLY ({fridgeQ}, crate is 78)", fridgeQ > 90 && fridgeQ < 100);
            T.Check("the powered fridge's steak is flagged cold (preserved)", fridge.Storage.getItem(0)?.item.preserved == true);

            gen.TogglePower();
            yield return Ticks(4);           // generator winds down + the net re-solves -> the fridge's port goes unpowered
            PowerNet.Recompute(Tree);
            T.Check("an unpowered fridge stops preserving", !fridge.Preserves);

            FoodSpoil.TickDayCrates(Tree);
            T.Check("its steak is no longer flagged cold", fridge.Storage.getItem(0)?.item.preserved == false);
            // Full open-air rate once the power is gone. Measured as the DROP across this tick rather than an
            // absolute 78, because the steak already lost a little while the fridge was running -- the old
            // absolute expected a steak that had been perfectly preserved up to this point, which is exactly the
            // rule that changed.
            int afterCut = fridge.Storage.getItem(0)?.item.quality ?? 0;
            T.Check($"an unpowered fridge spoils at the full rate ({fridgeQ} -> {afterCut}, meat rate 22)",
                    fridgeQ - afterCut == 22);

            fridge.QueueFree(); crate.QueueFree(); gen.QueueFree();
            yield break;
        }
    }
}
