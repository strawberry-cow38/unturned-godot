namespace UnturnedGodot
{
    /// <summary>
    /// "Offline mode": the player has said they do not want multiplayer this session. Set by the launcher
    /// checkbox as UG_OFFLINE=1.
    ///
    /// ⚠ THIS IS A PREFERENCE, NOT A SECURITY BOUNDARY, and the distinction matters because the identity
    /// work happening alongside it IS one. Nothing here defends against anything -- the player chose it and
    /// can unchoose it, and a modified client ignoring it harms nobody but its owner. It exists so that
    /// somebody who cannot or does not want to sign in gets a menu with no dead ends in it, rather than a
    /// Multiplayer button that walks them to a rejection.
    ///
    /// Read live rather than cached in a static: an env read is ~365ns, this is touched at menu-build time
    /// and never in a frame loop, and a cached copy would be one more process-global surviving ResetGlobals
    /// for the leak detector to have an opinion about.
    /// </summary>
    public static class OfflineMode
    {
        public static bool Enabled => System.Environment.GetEnvironmentVariable("UG_OFFLINE") == "1";

        /// <summary>Shown on the controls it disables. Names the switch AND where to flip it -- a greyed-out
        /// button whose tooltip only says "unavailable" is indistinguishable from a broken one.</summary>
        public const string Reason = "Offline mode is on. Turn it off in the launcher to play online.";
    }
}
