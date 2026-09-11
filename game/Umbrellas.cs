namespace UnturnedGodot
{
    /// <summary>The umbrellas: retail's `Cloud` item type, eight of them, and the port had no concept of the
    /// type at all -- EItemType has 22 values and everything outside it lands on GENERIC, so all eight equipped
    /// as nothing. Same hole the 32 vehicle spraypaints fell through.
    ///
    /// The mechanic is entirely in UseableCloud, which is nine lines: equip plays the animation; tick() waits
    /// for IsEquipAnimationFinished and then sets player.movement.itemGravityMultiplier to the asset's Gravity;
    /// dequip() puts it back to 1. What that multiplier then DOES is two separate things over in PlayerMovement,
    /// and only the first is obvious:
    ///   - it scales gravity on the way DOWN only (`fall &lt;= 0 ? totalGravityMultiplier : 1f`, PlayerMovement.cs:1277),
    ///     so an umbrella is a parachute and not a jump boost;
    ///   - it scales the TERMINAL-VELOCITY clamp (PlayerMovement.cs:1280), which is what actually makes it an
    ///     item: at 0.25 your descent is capped at 4.9 m/s. Without that half you would still accelerate to a
    ///     killing speed, just more slowly, and the thing would read as broken.
    /// Both live in PlayerMovementSim; the equip/dequip half lives in PlayerController.
    ///
    /// All eight ship Gravity 0.25 -- read out of Bundles/Items/Clouds/Umbrella_*.dat on the box, not assumed.
    /// Kept as a per-id table rather than one constant because the VALUE is per-item data in the source, and a
    /// workshop umbrella that glides differently should not need this rewritten.</summary>
    public static class Umbrellas
    {
        /// <summary>Its fall multiplier, or null if this item is not an umbrella. Doubles as the "is it one"
        /// test so there is one answer rather than two that can disagree.</summary>
        public static float? For(ushort itemId) => itemId switch
        {
            1103 => 0.25f,   // Black
            1122 => 0.25f,   // Blue
            1123 => 0.25f,   // Green
            1124 => 0.25f,   // Orange
            1125 => 0.25f,   // Purple
            1126 => 0.25f,   // Red
            1127 => 0.25f,   // White
            1128 => 0.25f,   // Yellow
            _ => null,
        };

        public static bool Is(ushort itemId) => For(itemId).HasValue;

        /// <summary>PlayerLife.cs:2399 -- fall damage requires `totalGravityMultiplier > 0.67f`, so it is
        /// skipped entirely at or below this. At an umbrella's 0.25 you land unhurt from any height, and that
        /// is the POINT of the item rather than a consequence of the slower descent: 4.9 m/s is well under the
        /// 22 m/s damage threshold anyway, so without this gate the item would still work and the source
        /// would still have a line the port did not. Compared with `&lt;=` so the boundary matches source's `&gt;`.</summary>
        public const float FallDamageGravityFloor = 0.67f;
    }
}
