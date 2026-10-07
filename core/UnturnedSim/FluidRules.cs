namespace SDG.Unturned
{
    /// <summary>
    /// What may be done to the CONTENTS of a fluid container (a water bottle, a soda, a canteen), engine-free so the
    /// SERVER can apply it. It used to live only in the game layer's FluidItem, which is why drinking never stuck:
    /// the inventory is server-owned, the server had no way to empty a bottle or raise thirst, and the client's own
    /// edits were put back by the next owner echo (strawberry 2026-10-07: "make sure we are actually granting thirst
    /// on drink"). FluidItem and FluidDef now call these, so the client's prediction and the server's ruling are one rule.
    ///
    /// Byte-level because this assembly cannot see the game's FluidType / WaterQuality enums. The values ARE those
    /// enums' (game/FluidTank.cs), both append-only and persisted on every Item -- fluid.rules_match_enums pins them.
    /// </summary>
    public static class FluidRules
    {
        // FluidType values this file needs by name.
        public const byte None = 0, Water = 2, Soda = 5, Cola = 6, OrangeJuice = 7, Milk = 8, CoconutWater = 9,
                          EnergyDrink = 10, AppleJuice = 11, GrapeJuice = 12;
        // WaterQuality.Clean. Every other quality of WATER is undrinkable.
        public const byte Clean = 0;

        public const float SipML = 50f;
        /// <summary>A 50 mL sip restores 5% Water, so a 1 L bottle is about a full hydrate.</summary>
        public const float HydrationPerML = 0.001f;

        /// <summary>Why an action did nothing. The game layer turns these into the words on screen.</summary>
        public enum Refusal : byte { None, NotContainer, Empty, Undrinkable, Full, WontMix }

        /// <summary>A beverage: always drinkable and safe to autodrink, carries no water quality.</summary>
        public static bool IsBeverage(byte t) => t == Soda || t == Cola || t == OrangeJuice || t == Milk
                                              || t == CoconutWater || t == EnergyDrink || t == AppleJuice || t == GrapeJuice;
        /// <summary>The only thing the drink gate blocks is BAD WATER (tainted / dirty / salty), so it can't be chugged
        /// by accident. Every other fluid is the player's choice.</summary>
        public static bool Drinkable(byte t, byte q) => !(t == Water && q != Clean);
        /// <summary>The narrower set autodrink will touch: clean water or a proper beverage.</summary>
        public static bool Safe(byte t, byte q) => (t == Water && q == Clean) || IsBeverage(t);

        /// <summary>A FRESH container (fluidAmount &lt; 0) takes its asset's default contents on first read, so any
        /// creation path -- loot, `give`, a hand-made Item -- reads correct contents.</summary>
        public static void Seed(Item it, ItemAsset a)
        {
            if (it == null || a == null || !a.IsFluidContainer || it.fluidAmount >= 0f) return;
            it.fluidType = a.fluidDefaultType;
            it.fluidAmount = a.fluidDefaultType != None ? a.fluidCapacity : 0f;   // a None-default container (canteen) spawns EMPTY
            it.fluidQuality = a.fluidDefaultQuality;
        }

        static float Amount(Item it) => it.fluidAmount > 0f ? it.fluidAmount : 0f;

        static Refusal CanDrink(Item it, ItemAsset a)
        {
            if (it == null || a == null || !a.IsFluidContainer) return Refusal.NotContainer;
            Seed(it, a);
            if (Amount(it) <= 0.01f) return Refusal.Empty;
            return Drinkable(it.fluidType, it.fluidQuality) ? Refusal.None : Refusal.Undrinkable;
        }

        /// <summary>CHUG the lot (strawberry: the deliberate big gulp, distinct from autodrink's sips). Empties the
        /// container but keeps its type and quality, so it is an empty bottle you can refill. No thirst gate: it
        /// drinks whether or not you need it (strawberry 2026-10-07: "remove the 'not thirsty' gate").</summary>
        public static float DrinkAll(Item it, ItemAsset a, out float hydration, out Refusal why)
        {
            hydration = 0f;
            why = CanDrink(it, a);
            if (why != Refusal.None) return 0f;
            float drank = Amount(it);
            it.fluidAmount = 0f;
            hydration = drank * HydrationPerML;
            return drank;
        }

        /// <summary>One SipML off the top. Autodrink's unit.</summary>
        public static float Sip(Item it, ItemAsset a, out float hydration, out Refusal why)
        {
            hydration = 0f;
            why = CanDrink(it, a);
            if (why != Refusal.None) return 0f;
            float amount = Amount(it);
            float sip = amount < SipML ? amount : SipML;
            it.fluidAmount = amount - sip;
            hydration = sip * HydrationPerML;
            return sip;
        }

        /// <summary>Fill to the brim with CLEAN water from a running tap -- a sink or a bathtub (strawberry 2026-10-07:
        /// "allow filling containers w clean water from sinks and bathtubs").
        ///
        /// Bad water already in the container is POURED OUT first (<paramref name="rinsed"/>). A tap is unlimited, and
        /// the ordinary worst-quality-wins rule would otherwise make a bottle of tainted water permanent: it cannot be
        /// drunk, nothing empties it, and topping it up at a sink would only make more tainted water. Any OTHER fluid
        /// is left alone and refused -- tipping out somebody's soda to make room is not the tap's decision.</summary>
        public static float FillClean(Item it, ItemAsset a, out bool rinsed, out Refusal why)
        {
            rinsed = false;
            if (it == null || a == null || !a.IsFluidContainer) { why = Refusal.NotContainer; return 0f; }
            Seed(it, a);
            float amount = Amount(it);
            if (amount > 0.01f && it.fluidType != Water) { why = Refusal.WontMix; return 0f; }
            if (amount > 0.01f && it.fluidQuality != Clean) { rinsed = true; amount = 0f; }
            float space = a.fluidCapacity - amount;
            if (space <= 0.01f) { why = Refusal.Full; return 0f; }
            it.fluidType = Water; it.fluidQuality = Clean; it.fluidAmount = a.fluidCapacity;
            why = Refusal.None;
            return space;
        }

        /// <summary>THE one autodrink bottle: the first container on the player's OWN pages that is autodrink-enabled
        /// and holds something safe. OWNPAGES, not PAGES -- the open crate and the Nearby scan are not yours
        /// (strawberry 2026-09-10).</summary>
        public static Item ActiveAutoDrink(PlayerInventory inv)
        {
            if (inv == null) return null;
            for (byte pg = 0; pg < PlayerInventory.OWNPAGES; pg++)
            {
                var page = inv.items[pg];
                if (page == null) continue;
                for (byte i = 0; i < page.getItemCount(); i++)
                {
                    var it = page.getItem(i)?.item; var a = it != null ? Assets.find(it.id) : null;
                    if (a == null || !a.IsFluidContainer || !it.autoDrink) continue;
                    Seed(it, a);
                    if (Amount(it) > 0.01f && Safe(it.fluidType, it.fluidQuality)) return it;
                }
            }
            return null;
        }
    }
}
