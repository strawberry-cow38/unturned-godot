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
        public static ArrayMesh Build(Vector2[] left, Vector2 lc, Vector2 rc, float len,
                                      float spread = 3.2f, float mergeAt = 0.30f, float vertical = 0.45f,
                                      int n = 0, int rings = 12)
        {
            if (left == null || left.Length < 3) return null;
            float spreadY = spread * vertical;   // a headlight throws WIDE and comparatively flat, not a round cone
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
                float open = Mathf.SmoothStep(0f, 1f, Mathf.Clamp(t / mergeAt, 0f, 1f));   // waist: shut at the lens, open by mergeAt
                var pts = new Vector3[4 * n + 2];   // upper: n + waist + n, lower: n + waist + n
                float z = -t * len;
                Vector2 P(Vector2 p, Vector2 c) => c + new Vector2((p.X - c.X) * grow, (p.Y - c.Y) * growY)
                                                     + (c - mid) * (grow - 1f) * 0.15f;   // grow, and drift apart a little
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

            int m = 4 * n + 2;
            // Rings bunch toward the LENS. Everything that changes shape -- the waist opening from a pinch into
            // one volume -- happens inside the first mergeAt of the throw; past that it is a straight taper that
            // two rings describe as well as ten. Uniform spacing spent most of its rings on the boring part.
            float Depth(int r) => Mathf.Pow((float)r / rings, 1.7f);
            for (int r = 0; r < rings; r++)
            {
                float t0 = Depth(r), t1 = Depth(r + 1);
                var a = Ring(t0); var b = Ring(t1);
                for (int i = 0; i < m; i++)
                {
                    int j = (i + 1) % m;
                    // v runs 0 at the lens -> 1 at the far end; ConeGradient is sampled so the shaft fades out
                    // with distance rather than ending in a rim (strawberry: "fade towards the end of the cone")
                    Vector2 ua = new((float)i / m, t0), ub = new((float)(i + 1) / m, t0);
                    Vector2 uc = new((float)i / m, t1), ud = new((float)(i + 1) / m, t1);
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
