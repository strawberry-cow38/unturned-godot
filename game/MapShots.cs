using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    /// <summary>The per-map hero shot, and WHO TOOK IT.
    ///
    /// Retail ships LoadingScreens/ -- community screenshots at 3840x2160, each credited to its author in the
    /// filename. That credit is the terms the art was given on, so it travels with the picture rather than being
    /// an optional garnish: anywhere one of these is drawn, the name is drawn too (strawberry 2026-09-15: "add
    /// proper credit to the top right of every community screenshot on the main menu").
    ///
    /// ⚠ ONE TABLE, because there is more than one screen showing these now (the loading cover and the main
    /// menu's map preview). Two copies is how a picture ends up credited to the wrong person on one of them.
    ///
    /// `mappreview_&lt;key&gt;.png` stays the fallback: it is the 320x180 thumbnail retail ships per map, it covers
    /// keys with no hero shot (playground), and a key with no credited art must draw NO credit rather than a
    /// stale one.</summary>
    public static class MapShots
    {
        static readonly Dictionary<string, string> Credits = new()
        {
            ["pei"]        = "PEI Lighthouse Sunrise by BoomViz",
            ["washington"] = "Washington Landscape by Phobia",
            ["russia"]     = "Russia Landscape by Toste",
            ["yukon"]      = "Yukon Cabin by cucuycharles",
            ["germany"]    = "Germany Landscape by That One Beach Guy",
        };

        /// <summary>The author line for a map key, or null when its art is not one of the credited shots --
        /// which is also the signal to draw no credit label at all.</summary>
        public static string CreditFor(string key) => Credits.TryGetValue(key ?? "", out var c) ? c : null;

        /// <summary>True when the hi-res community shot exists for this key (so the caller knows the credit
        /// applies to the picture it is actually about to draw, not to one it fell back from).</summary>
        public static bool HasHiRes(string key) =>
            !string.IsNullOrEmpty(key)
            && System.IO.File.Exists(ProjectSettings.GlobalizePath($"res://content/menu/loadscreen_{key}.jpg"));

        /// <summary>File name of the best art for a key: the community shot when there is one, else the map
        /// thumbnail. Returned as a NAME rather than a texture so each caller keeps its own loader -- the menu
        /// downsamples to a panel, the loading cover wants it whole.</summary>
        public static string FileFor(string key) =>
            HasHiRes(key) ? $"loadscreen_{key}.jpg" : $"mappreview_{key}.png";
    }
}
