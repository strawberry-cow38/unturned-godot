namespace SDG.Unturned
{
    /// <summary>Where an item may live and whether it can be equipped straight out of the bag.
    ///
    /// 1:1 with the source enum (ESlotType.cs) and its extension methods, because the .dat files the port
    /// already ships carry this key -- eaglefire and maplestrike are `Slot Primary`, colt is `Slot Secondary`.
    /// The data was there the whole time; nothing parsed it, so every item behaved as NONE and any gun could be
    /// equipped from anywhere. strawberry 2026-08-16: "guns can only be sent to the hands if they are in the 1/2
    /// slots. some guns only fit in the primary, and not the secondary, but all secondary guns fit in the
    /// primary as well as secondary" -- which is exactly PRIMARY vs SECONDARY below.</summary>
    public enum ESlotType
    {
        NONE,        // not a holster item: cannot sit in primary/secondary, CAN be equipped from the bag (3-9 binds)
        PRIMARY,     // primary slot only, and never from the bag
        SECONDARY,   // primary OR secondary, and never from the bag
        TERTIARY,    // source: NPCs only
        ANY,         // primary, secondary, or from the bag
    }

    public static class SlotTypeExtension
    {
        public static bool CanEquipAsPrimary(this ESlotType t)
            => t == ESlotType.PRIMARY || t == ESlotType.SECONDARY || t == ESlotType.ANY;

        public static bool CanEquipAsSecondary(this ESlotType t)
            => t == ESlotType.SECONDARY || t == ESlotType.ANY;

        /// <summary>Can it go straight from a bag page into your hands? A rifle cannot: it has to be holstered
        /// first. This is the rule that stops a bound backpack slot acting as a third weapon slot.</summary>
        public static bool CanEquipFromBag(this ESlotType t)
            => t != ESlotType.PRIMARY && t != ESlotType.SECONDARY;

        /// <summary>The slot page this item PREFERS when equipped from a menu: the smallest that fits it, so a
        /// sidearm holsters at the hip and leaves the primary free rather than taking the big slot by default.</summary>
        public static int PreferredSlot(this ESlotType t)
            => t.CanEquipAsSecondary() ? 1 : (t.CanEquipAsPrimary() ? 0 : -1);

        public static bool CanEquipInPage(this ESlotType t, byte page)
            => page == 0 ? t.CanEquipAsPrimary() : page == 1 ? t.CanEquipAsSecondary() : t.CanEquipFromBag();

        public static ESlotType Parse(string s) => (s ?? "").Trim().ToLowerInvariant() switch
        {
            "primary" => ESlotType.PRIMARY,
            "secondary" => ESlotType.SECONDARY,
            "tertiary" => ESlotType.TERTIARY,
            "any" => ESlotType.ANY,
            _ => ESlotType.NONE,
        };
    }
}
