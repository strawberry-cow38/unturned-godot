using System;
using System.Collections.Generic;

namespace UnturnedSim
{
    /// <summary>The swatch list behind the editor's material browser. strawberry_cow: "give a preview of all
    /// the building material combos, have them work as a paint tool".
    ///
    /// A surface's colour is not its palette -- it is the PAIR (palette, texel). One retail building is one
    /// palette and several colours, so "all the combos" is the cross product, not the 52 palettes.
    ///
    /// Lives in core rather than in the editor because the interesting part is the deduplication, and that
    /// is a pure function of the palette table that a headless test can drive. The editor half is one list
    /// of rectangles.</summary>
    public static class WallCombos
    {
        /// <summary>One entry in the browser: a palette, a texel, and the colour that pair produces.</summary>
        public readonly struct Combo
        {
            /// <summary>Index into the palette table.</summary>
            public readonly int Material;
            /// <summary>Texel to pin, or -1 for the palette's ROLE colour -- the one that paints a roof in
            /// the roof texel and a wall in the wall texel.</summary>
            public readonly int Texel;
            public readonly int Rgb;
            public readonly string Label;

            public Combo(int material, int texel, int rgb, string label)
            { Material = material; Texel = texel; Rgb = rgb; Label = label; }

            public bool IsRole => Texel < 0;
        }

        /// <summary>Every distinct combo, palette-major.
        ///
        /// TWO RULES, and they pull in opposite directions:
        ///
        /// DEDUPLICATE BY COLOUR WITHIN A PALETTE. These textures are 4x2 images with repeats -- a palette
        /// whose eight texels are three distinct colours must not show eight swatches, five of which do the
        /// same thing. Dedup is WITHIN a palette only: the same grey in two palettes is two different
        /// buildings' grey, and collapsing across palettes would let picking a swatch silently change which
        /// building's thickness and reveal you get.
        ///
        /// BUT NEVER FOLD THE ROLE ENTRY INTO A TEXEL. Texel -1 renders identically to the wall texel ON A
        /// WALL, so a colour-keyed dedup deletes it -- and then painting a ROOF with that swatch pins the
        /// wall colour instead of following the roof role, which is the exact bug ("a roof is not the wall
        /// colour") this codebase already fixed once. The role entry is a different BEHAVIOUR wearing the
        /// same colour, so it is emitted first and never compared.</summary>
        public static List<Combo> All(IReadOnlyList<WallPalette> palettes)
        {
            var outp = new List<Combo>();
            if (palettes == null) return outp;

            for (int m = 0; m < palettes.Count; m++)
            {
                var p = palettes[m];
                if (p.Rgb == null || p.Rgb.Length < 8) continue;

                outp.Add(new Combo(m, -1, p.Wall, $"{p.Name}"));

                var seen = new HashSet<int>();
                for (int t = 0; t < 8; t++)
                {
                    if (!seen.Add(p.Rgb[t])) continue;          // a repeat of a texel already shown
                    outp.Add(new Combo(m, t, p.Rgb[t], $"{p.Name} {Role(p, t)}"));
                }
            }
            return outp;
        }

        /// <summary>What this texel is FOR in this palette, when it is anything. Named rather than numbered
        /// because "Police_1 2" tells you nothing and "Police_1 reveal" tells you what you are about to
        /// paint. Roles are per-palette measurements, so the same index is not the same role twice.</summary>
        static string Role(WallPalette p, int t)
            => t == p.WallTexel   ? "wall"
             : t == p.RevealTexel ? "reveal"
             : t == p.RoofTexel   ? "roof"
             : $"#{t}";

        /// <summary>Find the combo a surface currently wears, or -1.
        ///
        /// Matches on the PAIR, not the colour: two swatches can be the same RGB (the role entry and its
        /// wall texel always are) and highlighting the wrong one in the browser would make the selection
        /// jump the moment you painted something.</summary>
        public static int IndexOf(IReadOnlyList<Combo> combos, int material, int texel)
        {
            if (combos == null) return -1;
            for (int i = 0; i < combos.Count; i++)
                if (combos[i].Material == material && combos[i].Texel == texel) return i;
            return -1;
        }
    }
}
