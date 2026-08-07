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

        /// <summary>A triangle in a plane's own 2D space.</summary>
        public readonly struct Tri2
        {
            public readonly float AX, AY, BX, BY, CX, CY;
            public Tri2(float ax, float ay, float bx, float by, float cx, float cy)
            { AX = ax; AY = ay; BX = bx; BY = by; CX = cx; CY = cy; }
        }

        /// <summary>Recover a wall and its openings from a plane's TRIANGLES, by rasterising what they
        /// actually cover.
        ///
        /// This replaces treating each triangle's bounding box as a solid panel. That assumption -- "these
        /// buildings are box-modelled, so every triangle is half of an axis-aligned rectangle" -- is simply
        /// false: measured on House_00, only 49% of the vertical-plane triangles have two edges along their
        /// plane's axes. For the other half the bounding box is a rectangle that does not exist in the mesh,
        /// so the importer invented solid where there was none and then reported the leftovers as windows.
        /// That is what "windows overlapping in the wrong places" looks like from the inside.
        ///
        /// Rasterising is not clever, but it is CORRECT for any triangle soup, which is what a ripped mesh
        /// is. A hole is then a connected region of cells nothing covers.</summary>
        /// <summary>Split a plane's triangles into CONNECTED REGIONS of covered area and recover each as its
        /// own wall.
        ///
        /// One wall per plane is wrong for the same reason one wall per bounding box was: a plane holds every
        /// coplanar thing in the building. On House_00 that merged the reveal boards of every window on one
        /// axis into a 22 m slab at window height, and merged two real wall segments across the 0.5 m gap
        /// where a crossing wall passes. The coverage grid already knows where the solid actually is.</summary>
        public static List<Recovered> FromTrianglesSplit(IReadOnlyList<Tri2> tris, float cell = 0.05f,
                                                         float minOpening = WallOpenings.MinOpening)
        {
            var outp = new List<Recovered>();
            if (tris == null || tris.Count == 0) return outp;
            var groups = SplitConnected(tris, cell);
            foreach (var g in groups)
            {
                var r = FromTriangles(g, cell, minOpening);
                if (r.Width > WallOpenings.Eps && r.Height > WallOpenings.Eps) outp.Add(r);
            }
            return outp;
        }

        /// <summary>Group triangles whose covered areas touch. Cheap and approximate on purpose: two
        /// triangles are connected if their bounding boxes overlap, which over-groups slightly but never
        /// splits a genuinely connected wall -- and the coverage step then measures the truth.</summary>
        static List<List<Tri2>> SplitConnected(IReadOnlyList<Tri2> tris, float cell)
        {
            int n = tris.Count;
            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;
            int Find(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }

            var box = new (float U0, float V0, float U1, float V1)[n];
            for (int i = 0; i < n; i++)
            {
                var t = tris[i];
                box[i] = (Math.Min(t.AX, Math.Min(t.BX, t.CX)), Math.Min(t.AY, Math.Min(t.BY, t.CY)),
                          Math.Max(t.AX, Math.Max(t.BX, t.CX)), Math.Max(t.AY, Math.Max(t.BY, t.CY)));
            }
            float tol = cell * 1.5f;
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    if (box[i].U0 > box[j].U1 + tol || box[j].U0 > box[i].U1 + tol) continue;
                    if (box[i].V0 > box[j].V1 + tol || box[j].V0 > box[i].V1 + tol) continue;
                    int a = Find(i), b = Find(j);
                    if (a != b) parent[a] = b;
                }
            var map = new Dictionary<int, List<Tri2>>();
            for (int i = 0; i < n; i++)
            {
                int r = Find(i);
                if (!map.TryGetValue(r, out var g)) map[r] = g = new List<Tri2>();
                g.Add(tris[i]);
            }
            return new List<List<Tri2>>(map.Values);
        }

        public static Recovered FromTriangles(IReadOnlyList<Tri2> tris, float cell = 0.05f,
                                              float minOpening = WallOpenings.MinOpening)
        {
            var empty = new Recovered(0f, 0f, 0f, 0f, new List<WallOpening>());
            if (tris == null || tris.Count == 0) return empty;

            float u0 = float.MaxValue, v0 = float.MaxValue, u1 = float.MinValue, v1 = float.MinValue;
            foreach (var t in tris)
            {
                u0 = Math.Min(u0, Math.Min(t.AX, Math.Min(t.BX, t.CX)));
                u1 = Math.Max(u1, Math.Max(t.AX, Math.Max(t.BX, t.CX)));
                v0 = Math.Min(v0, Math.Min(t.AY, Math.Min(t.BY, t.CY)));
                v1 = Math.Max(v1, Math.Max(t.AY, Math.Max(t.BY, t.CY)));
            }
            float w = u1 - u0, h = v1 - v0;
            if (w <= WallOpenings.Eps || h <= WallOpenings.Eps) return new Recovered(u0, v0, Math.Max(0f, w), Math.Max(0f, h), new List<WallOpening>());

            int nx = Math.Clamp((int)Math.Ceiling(w / cell), 1, 2048);
            int ny = Math.Clamp((int)Math.Ceiling(h / cell), 1, 2048);
            var covered = new bool[nx * ny];
            float cw = w / nx, ch = h / ny;

            foreach (var t in tris)
            {
                float tu0 = Math.Min(t.AX, Math.Min(t.BX, t.CX)), tu1 = Math.Max(t.AX, Math.Max(t.BX, t.CX));
                float tv0 = Math.Min(t.AY, Math.Min(t.BY, t.CY)), tv1 = Math.Max(t.AY, Math.Max(t.BY, t.CY));
                int x0 = Math.Max(0, (int)((tu0 - u0) / cw)), x1 = Math.Min(nx - 1, (int)((tu1 - u0) / cw));
                int y0 = Math.Max(0, (int)((tv0 - v0) / ch)), y1 = Math.Min(ny - 1, (int)((tv1 - v0) / ch));
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        if (covered[y * nx + x]) continue;
                        float pu = u0 + (x + 0.5f) * cw, pv = v0 + (y + 0.5f) * ch;
                        if (Inside(t, pu, pv)) covered[y * nx + x] = true;
                    }
            }

            // holes = connected components of uncovered cells that do NOT touch the boundary. A region open
            // to the edge is the wall's outline being non-rectangular, not a window.
            var openings = new List<WallOpening>();
            var seen = new bool[nx * ny];
            var stack = new Stack<int>();
            for (int start = 0; start < nx * ny; start++)
            {
                if (covered[start] || seen[start]) continue;
                stack.Clear();
                stack.Push(start);
                seen[start] = true;
                int minx = nx, maxx = -1, miny = ny, maxy = -1;
                bool touchesEdge = false;
                while (stack.Count > 0)
                {
                    int i = stack.Pop();
                    int x = i % nx, y = i / nx;
                    if (x == 0 || y == 0 || x == nx - 1 || y == ny - 1) touchesEdge = true;
                    if (x < minx) minx = x;
                    if (x > maxx) maxx = x;
                    if (y < miny) miny = y;
                    if (y > maxy) maxy = y;
                    if (x > 0 && !covered[i - 1] && !seen[i - 1]) { seen[i - 1] = true; stack.Push(i - 1); }
                    if (x < nx - 1 && !covered[i + 1] && !seen[i + 1]) { seen[i + 1] = true; stack.Push(i + 1); }
                    if (y > 0 && !covered[i - nx] && !seen[i - nx]) { seen[i - nx] = true; stack.Push(i - nx); }
                    if (y < ny - 1 && !covered[i + nx] && !seen[i + nx]) { seen[i + nx] = true; stack.Push(i + nx); }
                }
                if (touchesEdge) continue;
                float ou = u0 + minx * cw, ov = v0 + miny * ch;
                float ow = (maxx - minx + 1) * cw, oh = (maxy - miny + 1) * ch;
                if (ow >= minOpening && oh >= minOpening)
                    openings.Add(new WallOpening(ou - u0, ov - v0, ow, oh));
            }
            return new Recovered(u0, v0, w, h, openings);
        }

        static bool Inside(Tri2 t, float px, float py)
        {
            float d1 = (px - t.BX) * (t.AY - t.BY) - (t.AX - t.BX) * (py - t.BY);
            float d2 = (px - t.CX) * (t.BY - t.CY) - (t.BX - t.CX) * (py - t.CY);
            float d3 = (px - t.AX) * (t.CY - t.AY) - (t.CX - t.AX) * (py - t.AY);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0;
            bool pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
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
