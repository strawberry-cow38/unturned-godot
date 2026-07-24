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
            T.Check("preserved cheese does NOT spoil", frozen.quality == 100);
            T.Check("condition clamps at 0 (10 - 20 -> 0, not underflow)", lowCheese.quality == 0);
            T.Check("TickDay counts only what actually spoiled (cheese+potato+lowCheese=3; preserved & rock skipped)", spoiled == 3);

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
}
