namespace UnturnedGodot
{
    /// <summary>Player control preferences. Separate from <see cref="GraphicsOptions"/> because these change how
    /// the game READS you rather than how it draws, and mixing the two makes both harder to find.</summary>
    public static class ControlsOptions
    {
        /// <summary>Helicopter pitch axis. This exists because VoX and strawberry want opposite things and both
        /// are right -- VoX 2026-08-16: "invert the roll direction (forward mouse moves forward)"; strawberry,
        /// minutes later: "i feel like it controls fine inverted", then "make it a toggle in the options for
        /// inverted/regular".
        ///
        /// FALSE (default, "Regular") = push the mouse forward, the nose drops, you fly forward. The arcade
        /// mapping, and what someone who has never flown a sim expects from a vehicle.
        /// TRUE ("Inverted") = push forward, the nose comes UP, like a real cyclic or a flight-sim yoke.
        ///
        /// Genuinely a preference and not a bug either way, which is exactly what a setting is for. Defaulting
        /// to Regular because it is the one a new player will not have to discover.</summary>
        public static bool InvertHeliPitch;

        public static string InvertHeliPitchLabel => InvertHeliPitch ? "Inverted" : "Regular";
    }
}
