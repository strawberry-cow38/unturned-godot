using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    /// <summary>Where the loading art comes from, and who (if anyone) to credit for it.
    ///
    /// THREE DIFFERENT PICTURES, and they are not interchangeable (strawberry 2026-09-15):
    ///   • loading INTO A MAP  -> that map's OFFICIAL screenshot, Maps/&lt;Folder&gt;/Level.png, 3840x2160. Made by
    ///     the game's authors, so no photo credit.
    ///   • loading into the MAIN MENU -> a RANDOM community screenshot from LoadingScreens/, 3840x2160, each
    ///     credited to its author in the filename.
    ///   • the map picker's little preview -> the 320x180 menu ICON we ship as mappreview_&lt;key&gt;.png. That is
    ///     what it IS -- an icon -- so it stays small and gets no credit.
    ///
    /// ⭐ READ FROM THE PLAYER'S OWN UNTURNED INSTALL, never hosted. 39 community shots plus one 4K screenshot
    /// per map is ~60 MB of images that would sit in git history forever and never delta -- and every player
    /// already has them, since the port needs the install for its map data regardless.</summary>
    public static class MapShots
    {
        /// <summary>The Unturned install, same resolution Main.MapDir uses (UG_UNTURNED_DIR, else the default
        /// Steam path). Duplicated deliberately rather than reached for across the Main node: this runs during
        /// the loading screen's own _Ready, before there is anything to ask.</summary>
        public static string InstallRoot =>
            (System.Environment.GetEnvironmentVariable("UG_UNTURNED_DIR")?.TrimEnd('\\', '/')
             ?? @"C:\Program Files (x86)\Steam\steamapps\common\Unturned");

        // Lowercase UI key -> Steam Maps/ folder. Spelled out rather than derived by casing, because "pei" is
        // upper and the rest are title case, and a rule with one exception is a rule waiting to acquire another.
        static readonly Dictionary<string, string> Folders = new()
        {
            ["pei"] = "PEI", ["washington"] = "Washington", ["russia"] = "Russia",
            ["yukon"] = "Yukon", ["germany"] = "Germany",
        };

        /// <summary>A map's own official loading screenshot, or null when the install (or that map) is absent.</summary>
        public static string OfficialMapShot(string key)
        {
            if (string.IsNullOrEmpty(key) || !Folders.TryGetValue(key, out var folder)) return null;
            string p = $"{InstallRoot}/Maps/{folder}/Level.png";
            return System.IO.File.Exists(p) ? p : null;
        }

        /// <summary>Every community screenshot in the install's LoadingScreens/ folder. Empty when there is no
        /// install -- callers fall back rather than failing.</summary>
        public static List<string> CommunityShots()
        {
            var outp = new List<string>();
            string dir = $"{InstallRoot}/LoadingScreens";
            if (!System.IO.Directory.Exists(dir)) return outp;
            foreach (var f in System.IO.Directory.GetFiles(dir))
            {
                string e = System.IO.Path.GetExtension(f).ToLowerInvariant();
                if (e == ".jpg" || e == ".jpeg" || e == ".png") outp.Add(f);
            }
            outp.Sort(System.StringComparer.OrdinalIgnoreCase);   // deterministic order; the PICK is what is random
            return outp;
        }

        /// <summary>The photographer, read out of the filename ("Title by Author"), or null when the name carries
        /// no author -- the official Unturned.png and Classic.png do not, and those must credit nobody.
        ///
        /// ⚠ PARSED, not tabulated. There are 39 of these and the install's set can change under us; a hardcoded
        /// list would quietly credit the wrong person the first time Nelson adds one.</summary>
        public static string CreditFor(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            int at = name.LastIndexOf(" by ", System.StringComparison.OrdinalIgnoreCase);
            return at > 0 ? name : null;   // the whole "Title by Author" line is the credit retail shows
        }
    }
}
