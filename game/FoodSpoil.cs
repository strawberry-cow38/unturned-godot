using Godot;
using SDG.Unturned;

namespace UnturnedGodot
{
    // Food spoilage (strawberry). A FOOD item's `quality` (0-100) IS its freshness/condition: it ticks down once per
    // in-game day at a per-food-type rate (dairy/meat fast, canned/dried slow, root veg slowest). A `preserved` item (in a
    // fridge -- stubbed) doesn't spoil. Below the sickness threshold, eating it costs you (wired in PlayerController.Consume).
    // Non-food items are untouched. Rates are hand-tuned heuristics by item name -- easy to retune / move to a tsv later.
    public static class FoodSpoil
    {
        public const int SickThreshold = 50;   // condition below this = spoiled: eating it makes you sick (source: quality < 50)

        // source UseableConsumeable.performUseOnSelf: the food/water an item restores scales by the eaten instance's
        // condition/100 -- a half-spoiled apple feeds you half as much.
        public static float NutritionScale(int quality) => Mathf.Clamp(quality, 0, 100) / 100f;

        // source: eating a FOOD/WATER item under the sick threshold infects you, scaled by how spoiled it is (0 at the
        // threshold, full at 0 condition) times half its (food + water) value. Returns the RAW infection fraction (0..1)
        // BEFORE the IMMUNITY skill cut -- PlayerController.Infect applies that. 0 when fresh enough or it has no nutrition.
        public static float MoldyInfection(int useFood, int useWater, int quality)
        {
            if (quality >= SickThreshold || useFood + useWater <= 0) return 0f;
            return (useFood + useWater) * 0.5f * (1f - quality / (float)SickThreshold) / 100f;
        }

        // % of the 0-100 condition a food loses per in-game day. Keyword heuristic on the item name; a sensible default
        // for anything unmatched. (milk/meat spoil fast, canned/dried slow, potato/root veg slowest -- strawberry's ordering.)
        public static float PerDay(ItemAsset a)
        {
            if (a == null || a.type != EItemType.FOOD) return 0f;
            string n = (a.itemName ?? "").ToLowerInvariant();
            bool Has(params string[] ks) { foreach (var k in ks) if (n.Contains(k)) return true; return false; }
            if (Has("canned", "tinned", "beans", "preserve"))                      return 2f;    // canned/tinned: very slow
            if (Has("bar", "dried", "jerky", "chocolate", "cereal", "cracker", "chips", "candy")) return 3f;   // dried/packaged
            if (Has("potato", "onion", "carrot", "pumpkin", "squash", "turnip"))   return 5f;    // root veg: slow (the "potato" end)
            if (Has("bread", "cake", "muffin", "donut", "pastry"))                 return 12f;   // baked goods
            if (Has("milk", "cheese", "yogurt", "cream", "egg"))                   return 20f;   // dairy: fast
            if (Has("meat", "steak", "beef", "pork", "chicken", "fish", "ham", "bacon", "raw", "meal")) return 22f;   // meat/fish: fastest
            if (Has("berr", "apple", "banana", "fruit", "grape", "orange", "melon", "vegetable", "salad")) return 10f;   // fresh fruit/veg
            return 8f;   // default perishable
        }

        // Advance one in-game day of spoilage across an inventory: each FOOD item loses PerDay% of its condition (quality),
        // clamped to 0. Preserved items are skipped. Returns how many items spoiled a step (for a HUD hint / logging).
        public static int TickDay(PlayerInventory inv)
        {
            if (inv == null) return 0;
            int n = 0;
            for (byte pg = 0; pg < PlayerInventory.PAGES; pg++)
            {
                var page = inv.items[pg];
                for (byte i = 0; i < page.getItemCount(); i++)
                {
                    var it = page.getItem(i)?.item; var a = it?.GetAsset();
                    if (a == null || a.type != EItemType.FOOD || it.preserved) continue;
                    float rate = PerDay(a);
                    if (rate <= 0f) continue;
                    int before = it.quality;
                    it.quality = (byte)Mathf.Max(0, it.quality - Mathf.RoundToInt(rate));
                    if (it.quality < before) n++;
                }
            }
            return n;
        }
    }
}
