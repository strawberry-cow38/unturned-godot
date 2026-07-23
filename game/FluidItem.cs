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
            if (it != null && a != null && a.IsFluidContainer && it.fluidAmount < 0f)
            {
                it.fluidType = a.fluidDefaultType;
                it.fluidAmount = a.fluidDefaultType != 0 ? a.fluidCapacity : 0f;   // a None-default container (canteen) spawns EMPTY
                it.fluidQuality = a.fluidDefaultQuality;
            }
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

        // LMB (while NOT looking at a tank) to take a sip: SipML off the top, but only if the contents are DRINKABLE
        // (clean water, or soda / cola -- tainted / dirty water can't be drunk). Returns mL drunk; `hydration` = Water-vital
        // units to add (0..1 scale); msg carries the reason it did nothing (empty / undrinkable).
        public const float SipML = 50f;
        public const float HydrationPerML = 0.001f;   // a 50 mL sip restores 0.05 (5%) Water -> a 1 L bottle ~ a full hydrate (tunable)
        public static float Sip(Item held, ItemAsset a, out float hydration, out string msg)
        {
            hydration = 0f; msg = null;
            if (held == null || a == null || !a.IsFluidContainer) { msg = "not a fluid container"; return 0f; }
            Read(held, a, out var type, out var amount, out var q);
            if (amount <= 0.01f) { msg = "container is empty"; return 0f; }
            if (!FluidDef.Drinkable(type, q))
            {
                msg = type == FluidType.Water ? $"can't drink {FluidDef.WaterName(type, q).ToLowerInvariant()}" : $"can't drink {FluidDef.Name(type)}";
                return 0f;
            }
            float sip = Mathf.Min(SipML, amount);
            Write(held, type, amount - sip, q);
            hydration = sip * HydrationPerML;
            return sip;
        }
    }
}
