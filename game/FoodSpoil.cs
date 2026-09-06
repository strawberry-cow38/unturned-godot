using Godot;
using SDG.Unturned;

namespace UnturnedGodot
{
    // Food spoilage (strawberry). A FOOD item's `quality` (0-100) IS its freshness/condition: it ticks down once per
    // in-game day at a per-food-type rate (dairy/meat fast, canned/dried slow, root veg slowest). A `preserved` item (in
    // a powered fridge) spoils much more slowly, and one frozen solid does not spoil at all -- both through
    // Freezing.SpoilMultiplier, which is the single place that decision lives. Below the sickness threshold, eating it
    // costs you (wired in PlayerController.Consume).
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
            for (byte pg = 0; pg < PlayerInventory.PAGES; pg++) n += TickDayItems(inv.items[pg]);
            return n;
        }

        // Advance one in-game day of spoilage across a SINGLE grid -- a bag page OR a placed storage crate's contents:
        // each unpreserved FOOD item loses PerDay% of its condition, clamped to 0. Preserved items (a powered fridge)
        // are skipped. Returns how many items dropped a step (HUD hint / logging).
        public static int TickDayItems(Items page)
        {
            if (page == null) return 0;
            int n = 0;
            for (byte i = 0; i < page.getItemCount(); i++)
            {
                var it = page.getItem(i)?.item; var a = it?.GetAsset();
                if (a == null || a.type != EItemType.FOOD) continue;
                // A FRIDGE NO LONGER STOPS TIME, A FREEZER DOES (strawberry 2026-09-06: "at 100% they NEVER
                // spoil ... fridge'd items spoil at a much slower rate"). `preserved` used to mean "skip this
                // item entirely"; it now means refrigerated, which is a heavy multiplier instead of a halt, and
                // the hard stop moved to 100 % frozen where she put it.
                float rate = PerDay(a) * Freezing.SpoilMultiplier(it.frozen, it.preserved);
                if (rate <= 0f) continue;
                int before = it.quality;
                // KNOWN AND DELIBERATE: quality is an integer with no fractional carry, so a slow food in a
                // fridge (canned beans, 2 %/day * 0.15 = 0.3) rounds to zero and keeps indefinitely. That is a
                // reasonable outcome for tinned food and not worth an accumulator field on every Item -- but it
                // is a rounding artifact rather than a designed rule, so it is written down rather than implied.
                it.quality = (byte)Mathf.Max(0, it.quality - Mathf.RoundToInt(rate));
                if (it.quality < before) n++;
            }
            return n;
        }

        // Spoil one day across every PLACED storage crate in the world. A fridge (StorageCrate.Preserves -- true only
        // while it's powered) is skipped, so its food stays fresh; a plain crate spoils like the bag. Driven once per
        // in-game day from PlayerController.FoodSpoilTick, beside the bag sweep. SP-local (MP world-container spoilage =
        // fast-follow, like the bag). Returns how many items spoiled a step.
        public static int TickDayCrates(SceneTree tree)
        {
            if (tree == null) return 0;
            int n = 0;
            foreach (var node in tree.GetNodesInGroup("crates"))
            {
                if (node is not StorageCrate c || !GodotObject.IsInstanceValid(c)) continue;
                bool preserving = c.Preserves;   // live: a fridge's own powered port (always false for a plain crate)
                // Reconcile each FOOD item's `preserved` to the crate's CURRENT power state, so the daily sweep is
                // AUTHORITATIVE and doesn't depend on the fridge's per-frame _Process having caught up: a just-unpowered
                // fridge's food spoils this tick, a powered fridge's shows the cold ❄ + is skipped.
                for (byte i = 0; i < c.Storage.getItemCount(); i++)
                {
                    var it = c.Storage.getItem(i)?.item;
                    if (it != null && it.GetAsset()?.type == EItemType.FOOD) it.preserved = preserving;
                }
                // Always swept now: a powered fridge SLOWS its contents rather than exempting them, so skipping
                // the call would silently restore the old halt-behaviour through the back door.
                n += TickDayItems(c.Storage);
            }
            return n;
        }
    }
}
