using Godot;
using SDG.Unturned;

namespace UnturnedGodot
{
    // Bridges a fluid-CONTAINER item (water bottle / soda / cola / canteen) to the fluid model. The item stores its
    // contents as raw bytes (Item.fluidType / fluidQuality) + a mL amount (Item.fluidAmount), because the core UnturnedSim
    // assembly that defines Item can't reference THIS assembly's FluidType / WaterQuality enums. Everything that reads or
    // mutates a container's contents goes through here, so the byte <-> enum mapping and the fresh-container lazy fill live
    // in ONE place. Fill / Sip are pure (Item + FluidTank + vitals math, no scene nodes) so the headless fluid self-tests
    // exercise the real transfer logic.
    public static class FluidItem
    {
        public static bool IsContainer(ItemAsset a) => a != null && a.IsFluidContainer;

        // THE one active autodrink bottle (strawberry: "only 1 at a time · bottle empty = check for next · unless
        // disabled"): the FIRST container in scan order that is autodrink-ENABLED (its per-bottle opt-in) AND holds a SAFE,
        // non-empty fluid. Multiple bottles may be enabled, but only this one is drunk from + shows the icon; when it
        // empties the next enabled+safe bottle naturally becomes first, and a disabled bottle is skipped. Null = none.
        // ⚠ OWNPAGES, not PAGES (strawberry 2026-09-10: "prevent items in containers being eligable for autodrink").
        // The rule lives in core now (FluidRules), because the SERVER is the one that drinks from it.
        public static Item ActiveAutoDrink(SDG.Unturned.PlayerInventory inv) => FluidRules.ActiveAutoDrink(inv);

        // The in-hand VIEWMODEL mesh for a held container. Most match the item name (bottled_water, canteen, bottled_soda,
        // bottled_cola, bottled_coconut, bottled_energy); the two retail CARTONS don't (Orange Juice -> box_orange, Milk
        // Box -> box_milk), so they're mapped by id here.
        public static string HeldMesh(ItemAsset a)
        {
            if (a == null) return "bottled_water";
            return a.id switch
            {
                463 => "box_orange",     // Orange Juice
                462 => "box_milk",       // Milk Box
                91  => "juice_apple",    // Apple Juice
                92  => "juice_grape",    // Grape Juice
                481 => "bottle_maple",   // Maple Bottle
                482 => "bottle_birch",   // Birch Bottle
                483 => "bottle_pine",    // Pine Bottle
                _ => a.itemName?.ToLowerInvariant().Replace(" ", "_") ?? "bottled_water",   // bottled_water/soda/cola/coconut/energy, canteen, canned_cola/soda all match
            };
        }

        // Read a container's contents, lazily initializing a FRESH item (fluidAmount < 0) from its asset default -> any
        // creation path (loot, `give`, a hand-made Item) reads correct contents without every caller remembering to seed them.
        public static void Read(Item it, ItemAsset a, out FluidType type, out float amount, out WaterQuality q)
        {
            FluidRules.Seed(it, a);
            type = it != null ? (FluidType)it.fluidType : FluidType.None;
            amount = it != null ? Mathf.Max(0f, it.fluidAmount) : 0f;
            q = it != null ? (WaterQuality)it.fluidQuality : WaterQuality.Clean;
        }

        public static void Write(Item it, FluidType type, float amount, WaterQuality q)
        {
            if (it == null) return;
            it.fluidType = (byte)type; it.fluidAmount = Mathf.Max(0f, amount); it.fluidQuality = (byte)q;
        }

        // A held container's HUD label: "Canteen -- 500 mL Clean Water" / "Canteen -- empty (500 mL)".
        public static string Label(Item it, ItemAsset a)
        {
            if (a == null) return "";
            Read(it, a, out var type, out var amount, out var q);
            if (amount <= 0.001f || type == FluidType.None) return $"{a.itemName} -- empty ({FluidDef.Litres(a.fluidCapacity)})";
            return $"{a.itemName} -- {FluidDef.Litres(amount)} {FluidDef.WaterName(type, q)}";
        }

        // RMB a fluid device (tank / source) while holding this container: pull as much as fits = min(container free
        // space, tank contents). TYPE-LOCKED -- a partly-full container refuses a different fluid (fluids don't mix). The
        // container takes the WORST quality that enters it (one drop of dirty -> all dirty). Returns mL moved; msg carries
        // the refusal reason (full / empty tank / mismatch) for the HUD.
        public static float Fill(Item held, ItemAsset a, FluidTank from, out string msg)
        {
            msg = null;
            if (held == null || a == null || !a.IsFluidContainer) { msg = "not a fluid container"; return 0f; }
            if (from == null || from.Type == FluidType.None || from.Amount <= 0.01f) { msg = "that tank is empty"; return 0f; }
            Read(held, a, out var htype, out var hamount, out var hq);
            float space = a.fluidCapacity - hamount;
            if (space <= 0.01f) { msg = "container is full"; return 0f; }
            if (hamount > 0.01f && htype != from.Type) { msg = $"won't mix {FluidDef.Name(htype)} and {FluidDef.Name(from.Type)}"; return 0f; }
            float moved = Mathf.Min(space, from.Amount);
            from.Drain(moved);
            WaterQuality newQ = hamount > 0.01f ? (WaterQuality)Mathf.Max((int)hq, (int)from.Quality) : from.Quality;   // worst-wins if it already held some; else adopt the tank's
            Write(held, from.Type, hamount + moved, newQ);
            return moved;
        }

        // The DRINK rules are FluidRules' (core), so the server applies exactly what the client predicts. These wrap them
        // with the words for the HUD.
        public const float SipML = FluidRules.SipML;
        public const float HydrationPerML = FluidRules.HydrationPerML;

        /// <summary>The on-screen reason a drink or fill did nothing.</summary>
        public static string Why(FluidRules.Refusal why, Item it) => why switch
        {
            FluidRules.Refusal.None => null,
            FluidRules.Refusal.NotContainer => "not a fluid container",
            FluidRules.Refusal.Empty => "container is empty",
            FluidRules.Refusal.Full => "container is full",
            FluidRules.Refusal.WontMix => $"won't mix {FluidDef.Name((FluidType)(it?.fluidType ?? 0))} and water",
            FluidRules.Refusal.Undrinkable => (FluidType)(it?.fluidType ?? 0) == FluidType.Water
                ? $"can't drink {FluidDef.WaterName(FluidType.Water, (WaterQuality)(it?.fluidQuality ?? 0)).ToLowerInvariant()}"
                : $"can't drink {FluidDef.Name((FluidType)(it?.fluidType ?? 0))}",
            _ => "can't do that",
        };

        // A 50 mL sip off the top -- autodrink's unit. Returns mL drunk; `hydration` = Water-vital units (0..1 scale).
        public static float Sip(Item held, ItemAsset a, out float hydration, out string msg)
        {
            float drank = FluidRules.Sip(held, a, out hydration, out var why);
            msg = Why(why, held);
            return drank;
        }

        // Equipped LMB (not aimed at a tank): CHUG the whole bottle at once (strawberry) -- the deliberate big gulp, distinct
        // from the passive 50 mL autodrink. Empties the container (keeps the item + its type so you can refill it).
        public static float DrinkAll(Item held, ItemAsset a, out float hydration, out string msg)
        {
            float drank = FluidRules.DrinkAll(held, a, out hydration, out var why);
            msg = Why(why, held);
            return drank;
        }
    }
}
