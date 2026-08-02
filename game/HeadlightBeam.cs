using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // The visible light shaft in front of a vehicle's headlights (strawberry): the streetlight beam idea, but the
    // car case has a constraint the lamp post does not -- there are TWO emitters, and two separate cones cross a
    // couple of metres out. Under additive blending that crossing region reads as a bright lens-shaped wedge
    // hanging in mid-air, which is the "weird overlap" being complained about.
    //
    // So this is ONE mesh, not two cones: the cross-section starts as the two real lamp outlines PINCHED TOGETHER
    // at a single point between them, and the pinch opens with distance until the two lobes are one solid volume.
    // Nothing ever overlaps itself, so nothing double-brightens, and the tips still read as the lamp shapes they
    // came from (a jeep's hexagons, a sedan's rectangles).
    //
    // The whole thing is a single closed loop at every depth, which is what keeps it buildable: an upper chain
    // running left-lobe -> waist -> right-lobe and a lower chain coming back. No topology change mid-mesh, so it
    // is an ordinary quad strip between consecutive rings.
    public static class HeadlightBeam
    {
        /// <summary>Do the two lobes fuse into one volume? Off: they stay separate for the whole throw.</summary>
        public static bool Merge = false;

        /// <summary>Convex hull (monotone chain) of a lamp's vertices projected on the car's XY plane -- the real
        /// lens outline, so the beam tip is the shape of the thing emitting it rather than a generic disc.</summary>
        public static Vector2[] Hull(IEnumerable<Vector2> pts)
        {
            var p = new List<Vector2>();
            foreach (var q in pts) p.Add(q);
            p.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
            for (int i = p.Count - 1; i > 0; i--) if (p[i].IsEqualApprox(p[i - 1])) p.RemoveAt(i);
            if (p.Count < 3) return p.ToArray();
            float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
            var lo = new List<Vector2>();
            foreach (var q in p) { while (lo.Count >= 2 && Cross(lo[^2], lo[^1], q) <= 0) lo.RemoveAt(lo.Count - 1); lo.Add(q); }
            var up = new List<Vector2>();
            for (int i = p.Count - 1; i >= 0; i--) { var q = p[i]; while (up.Count >= 2 && Cross(up[^2], up[^1], q) <= 0) up.RemoveAt(up.Count - 1); up.Add(q); }
            lo.RemoveAt(lo.Count - 1); up.RemoveAt(up.Count - 1);
            lo.AddRange(up);
            // Drop near-collinear corners. A lamp outline is a few straight runs, but float noise in the ripped
            // mesh leaves extra points sitting on those runs -- a jeep lamp hulls to 10 when it is really a
            // hexagon. They cost vertices in every ring of the beam and change the silhouette by nothing.
            for (int i = lo.Count - 1; i >= 0 && lo.Count > 3; i--)
            {
                var a2 = lo[(i - 1 + lo.Count) % lo.Count]; var b2 = lo[i]; var c2 = lo[(i + 1) % lo.Count];
                var e1 = (b2 - a2); var e2 = (c2 - b2);
                float len1 = e1.Length(), len2 = e2.Length();
                if (len1 < 1e-5f || len2 < 1e-5f) { lo.RemoveAt(i); continue; }
                float cr = Mathf.Abs(e1.X * e2.Y - e1.Y * e2.X) / (len1 * len2);   // |sin| of the turn
                if (cr < 0.08f) lo.RemoveAt(i);                                     // < ~4.6 degrees = straight
            }
            return lo.ToArray();
        }

        /// <summary>Split a hull into its upper and lower chains between the extreme-X points, each resampled to
        /// `n` points. The beam is built from chains rather than a radial sweep because a radial function around
        /// one centre cannot express a pinched waist -- it would bridge the gap between the lamps and put light
        /// across the grille where there is no lamp.</summary>
        static void Chains(Vector2[] hull, int n, out Vector2[] upper, out Vector2[] lower)
        {
            int iMin = 0, iMax = 0;
            for (int i = 1; i < hull.Length; i++)
            {
                if (hull[i].X < hull[iMin].X) iMin = i;
                if (hull[i].X > hull[iMax].X) iMax = i;
            }
            List<Vector2> a = new(), b = new();
            for (int k = 0, i = iMin; ; k++, i = (i + 1) % hull.Length) { a.Add(hull[i]); if (i == iMax || k > hull.Length) break; }
            for (int k = 0, i = iMax; ; k++, i = (i + 1) % hull.Length) { b.Add(hull[i]); if (i == iMin || k > hull.Length) break; }
            // `a` runs min-X -> max-X one way round, `b` the other; whichever has the greater mean Y is the top
            float MeanY(List<Vector2> c) { float s = 0; foreach (var v in c) s += v.Y; return s / Mathf.Max(1, c.Count); }
            var top = MeanY(a) >= MeanY(b) ? a : b;
            var bot = ReferenceEquals(top, a) ? b : a;
            upper = Resample(top, n);
            lower = Resample(bot, n);
            if (upper[0].X > upper[^1].X) System.Array.Reverse(upper);     // both chains run min-X -> max-X
            if (lower[0].X > lower[^1].X) System.Array.Reverse(lower);
        }

        static Vector2[] Resample(List<Vector2> chain, int n)
        {
            var outp = new Vector2[n];
            if (chain.Count == 1) { for (int i = 0; i < n; i++) outp[i] = chain[0]; return outp; }
            float total = 0f;
            for (int i = 1; i < chain.Count; i++) total += chain[i].DistanceTo(chain[i - 1]);
            if (total <= 0f) { for (int i = 0; i < n; i++) outp[i] = chain[0]; return outp; }
            float step = total / (n - 1), acc = 0f;
            int seg = 0; float segLeft = chain[1].DistanceTo(chain[0]);
            outp[0] = chain[0];
            for (int i = 1; i < n; i++)
            {
                float want = step;
                while (want > segLeft && seg < chain.Count - 2)
                {
                    want -= segLeft; seg++; segLeft = chain[seg + 1].DistanceTo(chain[seg]);
                }
                float segLen = chain[seg + 1].DistanceTo(chain[seg]);
                float u = segLen <= 0f ? 0f : 1f - (segLeft - want) / segLen;
                outp[i] = chain[seg].Lerp(chain[seg + 1], Mathf.Clamp(u, 0f, 1f));
                segLeft -= want; acc += step;
            }
            outp[n - 1] = chain[^1];
            return outp;
        }

        /// <param name="left">left lamp outline, vehicle XY, already centred on that lamp</param>
        /// <param name="lc">left lamp centre (vehicle XY)</param>
        /// <param name="rc">right lamp centre</param>
        /// <param name="len">how far the shaft throws, along -Z</param>
        /// <param name="spread">how much each lobe grows by the far end (1 = doubles)</param>
        /// <param name="mergeAt">fraction of the throw by which the waist is fully open (the two lobes are one)</param>
        /// <summary>Depth (0..1 of the throw) at which the two lobes' inner edges meet -- below this the beams are
        /// genuinely separate and anything between them is dark.</summary>
        public static float TouchDepth(Vector2[] left, Vector2 lc, Vector2 rc, float spread)
        {
            float halfW = 0.001f;
            foreach (var q in left) halfW = Mathf.Max(halfW, Mathf.Abs(q.X - lc.X));
            return Mathf.Clamp((Mathf.Abs(rc.X - lc.X) / (2f * halfW) - 1f) / Mathf.Max(spread, 0.001f), 0f, 1f);
        }

        /// <summary>Half-extents of ONE lobe at depth t -- what the mote sampler needs to stay inside the light.</summary>
        public static Vector2 LobeHalf(Vector2[] left, Vector2 lc, float spread, float vertical, float t)
        {
            float hw = 0.001f, hh = 0.001f;
            foreach (var q in left) { hw = Mathf.Max(hw, Mathf.Abs(q.X - lc.X)); hh = Mathf.Max(hh, Mathf.Abs(q.Y - lc.Y)); }
            return new Vector2(hw * (1f + spread * t), hh * (1f + spread * vertical * t));
        }

        public static ArrayMesh Build(Vector2[] left, Vector2 lc, Vector2 rc, float len,
                                      float spread = 3.2f, float mergeAt = 0.30f, float vertical = 0.45f,
                                      int n = 0, int rings = 12)
        {
            if (left == null || left.Length < 3) return null;
            float spreadY = spread * vertical;   // a headlight throws WIDE and comparatively flat, not a round cone

            // WHERE the lobes merge is geometry, not a schedule (strawberry: "only merge at the point where they
            // meet naturally ... beams stay separated until they touch"). mergeAt used to open the waist over a
            // fixed fraction of the throw, so light appeared between the lamps well before the two cones actually
            // reached each other. Solve for the depth at which the inner edges meet:
            //     centreGap = 2 * halfW * (1 + spread * t)   ->   t = (centreGap / (2*halfW) - 1) / spread
            // and it re-solves itself per vehicle, so a wide-set truck merges later than a quad.
            float halfW = 0.001f;
            foreach (var q in left) halfW = Mathf.Max(halfW, Mathf.Abs(q.X - lc.X));
            float centreGap = Mathf.Abs(rc.X - lc.X);
            float tTouch = Mathf.Clamp((centreGap / (2f * halfW) - 1f) / Mathf.Max(spread, 0.001f), 0f, 1f);
            float tFull = Mathf.Min(1f, tTouch + 0.18f);   // and a short blend once they do meet, not a hard seam
            // Sample each chain at the resolution the OUTLINE actually has, not a fixed number. A jeep lamp hulls
            // to 6 points -- 3 per chain -- so resampling to 10 spent seven vertices per chain interpolating
            // points along straight edges: 1512 triangles where ~400 draws the identical silhouette. Clamped low
            // because a lamp outline is a handful of straight runs, never a curve.
            if (n <= 0) n = Mathf.Clamp((left.Length + 1) / 2 + 1, 3, 6);
            Chains(left, n, out var lUp, out var lLo);
            // the right lamp is the left one mirrored in X -- the vehicle meshes are symmetric and this keeps a
            // single extracted outline authoritative for both sides
            var rUp = new Vector2[n]; var rLo = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                rUp[i] = new Vector2(-lUp[n - 1 - i].X, lUp[n - 1 - i].Y);
                rLo[i] = new Vector2(-lLo[n - 1 - i].X, lLo[n - 1 - i].Y);
            }

            var v = new List<Vector3>(); var nrm = new List<Vector3>(); var uv = new List<Vector2>();
            Vector2 mid = (lc + rc) * 0.5f;

            // One ring = upper chain (left lobe -> waist -> right lobe) then the lower chain back. 4n+2 points.
            Vector3[] Ring(float t)
            {
                float grow = 1f + spread * t;          // horizontal
                float growY = 1f + spreadY * t;        // vertical, deliberately less
                // MERGE OFF by default (strawberry, revised): the two beams stay separate the whole length. The
                // waist stays pinched to a point, so the loop still closes -- the sliver between the lobes has zero
                // height and therefore no area, and nothing renders between the lamps. Merge is kept as a switch
                // rather than deleted because the machinery is the same either way: with it on, the waist opens at
                // tTouch, the depth the lobes' inner edges actually meet, not on a fixed schedule.
                float open = Merge ? Mathf.SmoothStep(tTouch, tFull, t) : 0f;
                var pts = new Vector3[4 * n + 2];   // upper: n + waist + n, lower: n + waist + n
                float z = -t * len;
                // No outward drift: it widened the centre gap as the lobes grew, which fought the touch solve above
                // and delayed the merge past where the geometry says it happens.
                Vector2 P(Vector2 p, Vector2 c) => c + new Vector2((p.X - c.X) * grow, (p.Y - c.Y) * growY);
                float waistY = 0f, waistTop, waistBot;
                {
                    // the waist sits between the lamps; it is a single point at the lens (pinched) and opens to
                    // the lobes' own height by mergeAt, which is what turns two tips into one solid volume
                    // seeded from the DATA, not 0 -- these are vehicle-space Y (~0.76 on a jeep), so a 0 seed
                    // made hLo stay 0, dropped the waist centre half a metre and then multiplied that error by
                    // grow: the beam came out 12.5m tall, taller than it was wide.
                    float hUp = float.MinValue, hLo = float.MaxValue;
                    for (int i = 0; i < n; i++) { hUp = Mathf.Max(hUp, lUp[i].Y); hLo = Mathf.Min(hLo, lLo[i].Y); }
                    waistY = (hUp + hLo) * 0.5f;
                    waistTop = waistY + (hUp - waistY) * growY * open;
                    waistBot = waistY + (hLo - waistY) * growY * open;
                }
                int k = 0;
                for (int i = 0; i < n; i++) { var p = P(lUp[i], lc); pts[k++] = new Vector3(p.X, p.Y, z); }
                pts[k++] = new Vector3(mid.X, waistTop, z);
                for (int i = 0; i < n; i++) { var p = P(rUp[i], rc); pts[k++] = new Vector3(p.X, p.Y, z); }
                // lower chain, right -> left
                for (int i = n - 1; i >= 0; i--) { var p = P(rLo[i], rc); pts[k++] = new Vector3(p.X, p.Y, z); }
                pts[k++] = new Vector3(mid.X, waistBot, z);
                for (int i = n - 1; i >= 0; i--) { var p = P(lLo[i], lc); pts[k++] = new Vector3(p.X, p.Y, z); }
                return pts;
            }

            // TWO SEPARATE TUBES when not merging. The single-loop-with-a-waist trick is only needed to fuse the
            // lobes; with Merge off it actively hurts, because the waist point sits at mid-X on the lamps' vertical
            // CENTRE while each lobe's inner edge is at its own top and bottom -- so the strip between them is a
            // real triangular wedge spanning the gap, not the degenerate sliver I claimed. strawberry saw it
            // immediately: "they still seem connected by something in the middle". They were.
            // Rings bunch toward the LENS: everything that changes shape happens near the car, and past that it
            // is a straight taper two rings describe as well as ten.
            float Depth(int r) => Mathf.Pow((float)r / rings, 1.7f);

            void Strip(System.Func<float, Vector3[]> ring, int mm)
            {
                for (int r = 0; r < rings; r++)
                {
                    float t0 = Depth(r), t1 = Depth(r + 1);
                    var a = ring(t0); var b = ring(t1);
                    for (int i = 0; i < mm; i++)
                    {
                        int j = (i + 1) % mm;
                        Vector2 ua = new((float)i / mm, t0), ub = new((float)(i + 1) / mm, t0);
                        Vector2 uc = new((float)i / mm, t1), ud = new((float)(i + 1) / mm, t1);
                        void Tri(Vector3 p0, Vector3 p1, Vector3 p2, Vector2 q0, Vector2 q1, Vector2 q2)
                        {
                            var fn = (p1 - p0).Cross(p2 - p0).Normalized();
                            v.Add(p0); v.Add(p1); v.Add(p2);
                            nrm.Add(fn); nrm.Add(fn); nrm.Add(fn);
                            uv.Add(q0); uv.Add(q1); uv.Add(q2);
                        }
                        Tri(a[i], b[i], a[j], ua, uc, ub);
                        Tri(a[j], b[i], b[j], ub, uc, ud);
                    }
                }
            }

            if (Merge) Strip(Ring, 4 * n + 2);
            else
            {
                // one closed loop per lamp: its own upper chain out, its own lower chain back. Nothing between them.
                Vector3[] Lobe(Vector2[] up, Vector2[] lo, Vector2 c, float t)
                {
                    float grow = 1f + spread * t, growY = 1f + spreadY * t;
                    float z = -t * len;
                    var pts = new Vector3[2 * n];
                    for (int i = 0; i < n; i++)
                    {
                        var p = c + new Vector2((up[i].X - c.X) * grow, (up[i].Y - c.Y) * growY);
                        pts[i] = new Vector3(p.X, p.Y, z);
                    }
                    for (int i = 0; i < n; i++)
                    {
                        var q = lo[n - 1 - i];
                        var p = c + new Vector2((q.X - c.X) * grow, (q.Y - c.Y) * growY);
                        pts[n + i] = new Vector3(p.X, p.Y, z);
                    }
                    return pts;
                }
                Strip(t => Lobe(lUp, lLo, lc, t), 2 * n);
                Strip(t => Lobe(rUp, rLo, rc, t), 2 * n);
            }
            if (v.Count == 0) return null;
            var arr = new Godot.Collections.Array();
            arr.Resize((int)Mesh.ArrayType.Max);
            arr[(int)Mesh.ArrayType.Vertex] = v.ToArray();
            arr[(int)Mesh.ArrayType.Normal] = nrm.ToArray();
            arr[(int)Mesh.ArrayType.TexUV] = uv.ToArray();
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
            return mesh;
        }
    }
}
