using System;
using System.Collections.Generic;

namespace UnturnedSim
{
    /// <summary>Turn the flat panels of an existing building back into a wall plus its openings -- the
    /// translator that ports retail buildings into the editable format.
    ///
    /// The whole thing rests on one observation: the inverse of the partition IS the partition. Solids() takes
    /// a rectangle and its holes and returns the solid pieces; feed it the solid pieces instead and it returns
    /// the holes. So importing needs no second algorithm, and cannot disagree with the generator about what a
    /// wall with a window in it means -- the two directions are literally the same code.</summary>
    public static class WallImport
    {
        /// <summary>A wall recovered from a set of coplanar solid panels: its size, and the holes in it.</summary>
        public readonly struct Recovered
        {
            public readonly float U0, V0, Width, Height;
            public readonly List<WallOpening> Openings;
            public Recovered(float u0, float v0, float w, float h, List<WallOpening> openings)
            { U0 = u0; V0 = v0; Width = w; Height = h; Openings = openings; }
        }

        /// <summary>Recover the wall rectangle and its openings from the solid panels of one plane.
        ///
        /// `panels` are in that plane's own 2D space, in any order, and may overlap or abut. The wall is their
        /// bounding box; the openings are whatever the box has that they do not cover.</summary>
        public static Recovered FromPanels(IReadOnlyList<WallSolid> panels, float minOpening = WallOpenings.MinOpening)
        {
            if (panels == null || panels.Count == 0) return new Recovered(0f, 0f, 0f, 0f, new List<WallOpening>());

            float u0 = float.MaxValue, v0 = float.MaxValue, u1 = float.MinValue, v1 = float.MinValue;
            foreach (var p in panels)
            {
                u0 = Math.Min(u0, p.U0); v0 = Math.Min(v0, p.V0);
                u1 = Math.Max(u1, p.U1); v1 = Math.Max(v1, p.V1);
            }
            float w = u1 - u0, h = v1 - v0;
            if (w <= WallOpenings.Eps || h <= WallOpenings.Eps)
                return new Recovered(u0, v0, Math.Max(0f, w), Math.Max(0f, h), new List<WallOpening>());

            // panels, moved into wall space and handed to Solids AS IF they were openings
            var asHoles = new List<WallOpening>(panels.Count);
            foreach (var p in panels)
                asHoles.Add(new WallOpening(p.U0 - u0, p.V0 - v0, p.U1 - p.U0, p.V1 - p.V0));

            var holes = WallOpenings.Solids(w, h, asHoles);

            // Drop slivers. A ripped mesh's panel edges do not line up to the micron, so the complement is
            // always speckled with hairline rectangles that are seams rather than windows. Keeping them would
            // import a wall with two hundred openings in it, most of them invisible.
            var openings = new List<WallOpening>();
            foreach (var s in holes)
                if (s.Width >= minOpening && s.Height >= minOpening)
                    openings.Add(new WallOpening(s.U0, s.V0, s.Width, s.Height));

            return new Recovered(u0, v0, w, h, openings);
        }

        /// <summary>Snap a recovered opening onto the measured retail ladder, so an imported building lands on
        /// the same numbers a hand-drawn one would. Ripped geometry is a hair off round values, and without
        /// this an imported door is 2.4997 wide and stops matching anything.</summary>
        public static WallOpening SnapToRetail(WallOpening o, float tolerance = 0.12f)
        {
            float w = WallOpenings.Snap(o.Width, WallOpenings.Widths, tolerance);
            float v = WallOpenings.Snap(o.V, WallOpenings.SillHeights, tolerance);
            float head = WallOpenings.Snap(o.V + o.Height, WallOpenings.HeadHeights, tolerance);
            float h = Math.Max(WallOpenings.MinOpening, head - v);
            return new WallOpening(o.U, v, w, h, o.Depth, o.Archetype);
        }
    }
}
