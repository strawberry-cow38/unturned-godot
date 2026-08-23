namespace SDG.Unturned
{
    public enum MagLoadResult { Ok, Full, WrongCaliber, WouldMix }

    /// <summary>Whether a loose round may go into a magazine, and what it does to the magazine.
    ///
    /// IN CORE, AND THAT IS THE POINT. This rule started life as a private static in
    /// game/inventory/InventoryUI.cs. Its two dependencies (Assets.MagAcceptedRounds and
    /// Item.magLoadedRound) were already here, which made it look like the server could just reuse it --
    /// but core/UnturnedNet references core/UnturnedSim and NOTHING references the game layer, so the
    /// server could not call it at all. The only way to give the server the same gate would have been to
    /// write it a second time.
    ///
    /// Two copies of a validation rule is precisely the shape of the bug this whole command exists to fix.
    /// The magazine reverted because the client mutated its own inventory and the authoritative server
    /// never learned about it: client and server disagreed about the state. A duplicated CheckLoad would
    /// have them disagree about the RULE instead, which is worse -- the load succeeds on one side and is
    /// refused on the other, and the echo silently reverts it, looking exactly like the bug we just fixed.
    ///
    /// So both sides call this. The client calls it to draw the drag-over hint and to pace the wheel; the
    /// server calls it before applying anything a client asked for. Same function, one definition.</summary>
    public static class MagRules
    {
        /// <summary>Capacity, floored at 1 so a malformed asset cannot produce a zero-capacity magazine
        /// that is simultaneously full and empty.</summary>
        public static int Capacity(ItemAsset mag) => mag != null ? System.Math.Max(1, mag.magCapacity) : 1;

        /// <summary>The cartridge a magazine currently holds: its per-instance locked round if set, else
        /// the asset default. Null when empty, which is what makes an empty magazine accept any round its
        /// body feeds.</summary>
        public static string EffectiveRound(Item mag, ItemAsset ma) =>
            (mag == null || mag.amount <= 0) ? null
            : (!string.IsNullOrEmpty(mag.magLoadedRound) ? mag.magLoadedRound : ma?.magRound);

        /// <summary>Can this loose round be loaded into this magazine right now?</summary>
        public static MagLoadResult CheckLoad(Item mag, ItemAsset ma, ItemAsset bullet)
        {
            if (mag == null || ma == null || !ma.IsMagazine) return MagLoadResult.WrongCaliber;
            if (bullet == null || !bullet.isAmmo || string.IsNullOrEmpty(bullet.magRound)) return MagLoadResult.WrongCaliber;
            // Compatibility is by BODY, not by item: a STANAG body feeds 5.56 and .300 BLK both, because
            // both are defined against the same magCaliber.
            if (!Assets.MagAcceptedRounds(ma).Contains(bullet.magRound)) return MagLoadResult.WrongCaliber;
            if (mag.amount >= Capacity(ma)) return MagLoadResult.Full;
            string cur = EffectiveRound(mag, ma);
            // Part-loaded with a different cartridge: no mixing, unload first. An EMPTY mag has no current
            // round and so takes either.
            if (cur != null && cur != bullet.magRound) return MagLoadResult.WouldMix;
            return MagLoadResult.Ok;
        }

        /// <summary>The player-facing reason a drop was refused. Null when it was allowed.
        ///
        /// One colour, three messages, by design: all three are "this will not happen" and the player reads
        /// which from the words. The text lives here rather than in the UI so the server can report the
        /// same reason it actually applied, instead of the client guessing at it.</summary>
        public static string Message(MagLoadResult r) =>
            r == MagLoadResult.Full ? "Magazine full"
            : r == MagLoadResult.WouldMix ? "Unload first"
            : r == MagLoadResult.WrongCaliber ? "Incompatible"
            : null;

        /// <summary>Apply ONE round going in. Returns false and changes nothing if the rule refuses.
        /// Locks the magazine to this cartridge when it was empty.</summary>
        public static bool ApplyLoad(Item mag, ItemAsset ma, ItemAsset bullet)
        {
            if (CheckLoad(mag, ma, bullet) != MagLoadResult.Ok) return false;
            if (mag.amount <= 0) mag.magLoadedRound = bullet.magRound;   // empty -> LOCK to this cartridge
            mag.amount++;
            return true;
        }

        /// <summary>Apply ONE round coming out. Returns the cartridge removed, or null if there was nothing
        /// to remove. Clears the lock at zero so the magazine can take a different cartridge next time.
        ///
        /// The CALLER must have somewhere to put the round before calling this -- a full bag has to abort
        /// BEFORE the decrement, or unloading into a full inventory destroys ammunition. That ordering is
        /// the caller's because only the caller knows whether the add succeeded.</summary>
        public static string ApplyUnload(Item mag, ItemAsset ma)
        {
            if (mag == null || mag.amount <= 0) return null;
            string round = EffectiveRound(mag, ma);
            mag.amount--;
            if (mag.amount <= 0) mag.magLoadedRound = null;   // emptied -> unlock the cartridge
            return round;
        }
    }
}
