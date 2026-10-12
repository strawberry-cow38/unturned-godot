using System;
using System.Collections.Generic;

namespace SDG.Unturned
{
    /// <summary>How a road's end meets another road. TEE: the end runs into the host's SIDE at an angle (a small road or
    /// trail off a main, a ramp's end on a main). TAPER: a ramp leaves (or joins) a highway carriageway running along it.</summary>
    public enum JoinType : byte { Tee, Taper }

    /// <summary>A road's end that is joined to another road instead of stopping in the open (Line.StartJoin / EndJoin).
    /// The host is held by reference where the joiner belongs to it (a branch, a main's own ramp's end), else by Id (a
    /// highway, which the joiner does not own and a cache trim may rebuild).</summary>
    public sealed class Join
    {
        public JoinType Type;
        public InfiniteRoads.Line Host;
        public long HostId;
        /// <summary>Taper: the host's carriageway (+1 = the +offset one), whose outer edge the ramp runs along.</summary>
        public int Side;
    }

    /// <summary>The ground a junction paves between its roads (InfiniteRoads.PiecesIn): the mouth of a tee with its two
    /// fillets, or the middle of a town where mains meet. Absolute X/Z; Y the surface (a mesh lifts a vertex no lower than
    /// its ground plus Lift, as it does a slab's); U across the road it continues (0..1), V metres along it (the region
    /// divides by the texture's repeat). Tris index the vertices; each Skirt is a chain of boundary vertices hung with an
    /// edge bevel, outward along (SkirtNX, SkirtNZ).</summary>
    public sealed class RoadPatch
    {
        public byte Kind; public bool Ramp;
        public double AnchorX, AnchorZ;
        public double[] X, Z; public float[] Y, Lift, U, V;
        public int[] Tris;
        public int[][] Skirts; public float[][] SkirtNX, SkirtNZ;
        /// <summary>Vertices from here on are inside the patch, off its edges (a town's rings): free to be lifted onto
        /// the ground under them, where nothing carves it flat (InfiniteRoads.PiecesIn).</summary>
        public int Interior = int.MaxValue;

        /// <summary>How far (x, z) is from the patch's surface in plan: 0 on it, else to its nearest edge.</summary>
        public double DistanceTo(double x, double z)
        {
            double best = double.MaxValue;
            for (int t = 0; t + 2 < Tris.Length; t += 3)
            {
                int a = Tris[t], b = Tris[t + 1], c = Tris[t + 2];
                double den = (Z[b] - Z[c]) * (X[a] - X[c]) + (X[c] - X[b]) * (Z[a] - Z[c]);
                if (Math.Abs(den) > 1e-9)
                {
                    double w1 = ((Z[b] - Z[c]) * (x - X[c]) + (X[c] - X[b]) * (z - Z[c])) / den, w2 = ((Z[c] - Z[a]) * (x - X[c]) + (X[a] - X[c]) * (z - Z[c])) / den;
                    if (w1 >= 0 && w2 >= 0 && 1 - w1 - w2 >= 0) return 0;
                }
                foreach (var (u, v) in new[] { (a, b), (b, c), (c, a) })
                {
                    double sx = X[v] - X[u], sz = Z[v] - Z[u], ss = sx * sx + sz * sz;
                    double f = ss < 1e-12 ? 0 : Math.Clamp(((x - X[u]) * sx + (z - Z[u]) * sz) / ss, 0, 1);
                    double dx = x - X[u] - sx * f, dz = z - Z[u] - sz * f;
                    best = Math.Min(best, Math.Sqrt(dx * dx + dz * dz));
                }
            }
            return best;
        }

        /// <summary>The patch's surface at (x, z), or NaN off it.</summary>
        public float HeightAt(double x, double z)
        {
            for (int t = 0; t + 2 < Tris.Length; t += 3)
            {
                int a = Tris[t], b = Tris[t + 1], c = Tris[t + 2];
                double den = (Z[b] - Z[c]) * (X[a] - X[c]) + (X[c] - X[b]) * (Z[a] - Z[c]);
                if (Math.Abs(den) < 1e-6) continue;   // (a sliver: its barycentrics are noise, and "inside" anywhere along its line)
                double w1 = ((Z[b] - Z[c]) * (x - X[c]) + (X[c] - X[b]) * (z - Z[c])) / den, w2 = ((Z[c] - Z[a]) * (x - X[c]) + (X[a] - X[c]) * (z - Z[c])) / den, w3 = 1 - w1 - w2;
                if (w1 >= -1e-9 && w2 >= -1e-9 && w3 >= -1e-9) return (float)(w1 * Y[a] + w2 * Y[b] + w3 * Y[c]);
            }
            return float.NaN;
        }
    }

    /// <summary>Paint laid on the road where a road's own texture cannot reach (strawberry 2026-10-11: "why do the
    /// markings disappear near the connection? also add new road markings for stop lines, merge lines etc"): a joined
    /// road's lines carried on into its junction, stop lines, a ramp's merge line. Quads, four corners each (start
    /// left, start right, end left, end right), absolute X/Z, Y the surface under them; drawn by the region holding the
    /// anchor, a hair over the road.</summary>
    public sealed class RoadMark
    {
        public byte Colour;   // 0 white, 1 yellow (the road textures' own: 200,200,200 and 210,191,34)
        public double AnchorX, AnchorZ;
        public List<double> X = new(), Z = new(); public List<float> Y = new(), Lift = new();
        /// <summary>The junction patch it is painted over, if any: its surface there is the floor.</summary>
        public RoadPatch On;
        public int Quads => X.Count / 4;
    }

    public sealed partial class InfiniteRoads
    {
        // =============================================================================================================
        // JUNCTIONS (strawberry 2026-10-10: "can we do some magic with the highway on/offramp merges to actually merge them
        // properly with the highway mesh. and with any other road branch/connection too i suppose, since right now its just
        // overlapping, what if they actually CONNECTED to the mesh of the road").
        //
        // A road that meets another used to be laid OVER it: its slab started a metre inside the other's asphalt, its end
        // ramp dipping under it, the host's edge bevel and its own crossing in a notch at each corner; a ramp ran under a
        // highway carriageway for its first hundred metres; the mains at a town all ended on the same spot, four end ramps
        // in a heap and their tops fighting. Now:
        //   TEE    the joiner's ribbon starts where it has left the host's asphalt, past two FILLETS (curb returns): a patch
        //          paves the mouth from the host's top edge -- vertex for vertex the host's own -- round the fillets to the
        //          joiner's first piece, its edge bevel hung round the fillets, and the host's bevel is left off across it.
        //   TAPER  a ramp: the carriageway WIDENS on its outer side to carry it (the widened strip is the carriageway's own
        //          slab) until the GORE, where the ramp's inner edge clears the carriageway's; the ramp's ribbon starts
        //          there, its first edge laid along the carriageway's square, at the carriageway's surface (the ramp follows
        //          the highway's profile until it has left it -- InfiniteInterchanges / BuildRamps).
        //   NODE   mains meeting at a town: each is cut back past the fillets between it and its neighbours, and one patch
        //          paves the middle.
        //   Highway segments meeting at an anchor carry straight on: neither gets an end ramp there.

        /// <summary>The texture column a patch wears: plain asphalt in every road's texture (road_0's lane dashes are at
        /// 0.24 and 0.74 -- the first town drawn at 0.25 was a zebra crossing -- road_8's and road_6's dividers at
        /// 0.47-0.50). Run across a patch, the joiner's own marks bent through the fillets' long triangles.</summary>
        public const float PatchU = 0.12f;
        /// <summary>How far apart a town's patch carries vertices inward from its edge, for this LOD's ground to lift.</summary>
        public const double PatchRing = 2.0;
        /// <summary>A tee's fillet radius by the joiner's class.</summary>
        public static double FilletRadius(RoadKind k, bool ramp) => ramp ? 9.0 : k == RoadKind.Main ? 10.0 : k == RoadKind.Small ? 7.0 : 4.0;
        /// <summary>The fillet between two mains at a town.</summary>
        public const double NodeFillet = 12.0;
        /// <summary>How far a level crossing keeps from any other crossing and any road's end, to be paved as one.</summary>
        public const double CrossingClear = 90.0;
        static float RibbonHalfOf(RoadKind k) => k == RoadKind.Highway ? HighwayLaneHalf : PavedHalf(k);

        /// <summary>A line's ribbon, built exactly as PiecesIn builds it: the centreline offset along RibbonTangent, the arc
        /// along that, and the normal a piece end is squared to -- the averaged tangent's at a profile point, the
        /// segment's own between.</summary>
        sealed class Rib
        {
            public readonly Line E; public readonly int N;
            public readonly double[] X, Z, A, TX, TZ;
            public Rib(Line e, double off)
            {
                E = e; N = e.Segments;
                X = new double[N + 1]; Z = new double[N + 1]; TX = new double[N + 1]; TZ = new double[N + 1]; A = new double[N + 1];
                for (int i = 0; i <= N; i++)
                {
                    var (tx, tz) = RibbonTangent(e, i);
                    TX[i] = tx; TZ[i] = tz;
                    X[i] = e.X[i] - tz * off; Z[i] = e.Z[i] + tx * off;
                }
                for (int i = 0; i < N; i++)
                {
                    double dx = X[i + 1] - X[i], dz = Z[i + 1] - Z[i], l = Math.Sqrt(dx * dx + dz * dz);
                    A[i + 1] = A[i] + ((float)l < 1e-4f ? 0.0 : l);
                }
            }
            public double Total => A[N];
            public double Len(int k) => A[k + 1] - A[k];
            public (int k, double t) Find(double a)
            {
                if (a <= 0) return (0, 0);
                if (a >= A[N]) return (N - 1, 1);
                int lo = 0, hi = N - 1;
                while (lo < hi) { int m = (lo + hi + 1) >> 1; if (A[m] <= a) lo = m; else hi = m - 1; }
                double l = Len(lo);
                return (lo, l < 1e-12 ? 0 : Math.Clamp((a - A[lo]) / l, 0, 1));
            }
            public void Dir(int k, out double dx, out double dz)
            {
                dx = X[k + 1] - X[k]; dz = Z[k + 1] - Z[k];
                double l = Math.Max(1e-12, Math.Sqrt(dx * dx + dz * dz));
                dx /= l; dz /= l;
            }
            /// <summary>The normal a piece end at (k, t) is squared to: RowEnd's rule, as PiecesIn applies it.</summary>
            public void Normal(int k, double t, out double nx, out double nz)
            {
                int e = RowEnd(t, Len(k));
                if (e == 0) { nx = -TZ[k]; nz = TX[k]; return; }
                if (e == 1) { nx = -TZ[k + 1]; nz = TX[k + 1]; return; }
                Dir(k, out double dx, out double dz);
                nx = -dz; nz = dx;
            }
            public void Point(int k, double t, out double x, out double z) { x = X[k] + (X[k + 1] - X[k]) * t; z = Z[k] + (Z[k + 1] - Z[k]) * t; }
            public void PointAt(double a, out double x, out double z) { var (k, t) = Find(a); Point(k, t, out x, out z); }
            public float H(int k, double t) => E.H[k] + (E.H[k + 1] - E.H[k]) * (float)t;
            public float HAt(double a) { var (k, t) = Find(a); return H(k, t); }
            /// <summary>The edge point a piece end at arc `a` has on `side` (+1 = +normal), `hw` out.</summary>
            public void Edge(double a, int side, double hw, out double x, out double z)
            {
                var (k, t) = Find(a);
                Point(k, t, out x, out z);
                Normal(k, t, out double nx, out double nz);
                x += nx * side * hw; z += nz * side * hw;
            }
            /// <summary>A junction's cut at arc `a`, moved onto a profile point that is too near it -- on the side `keep`
            /// (+1 later, -1 earlier, 0 either) -- for a ribbon `hw` wide. The piece between a cut and a profile point is
            /// squared one end to the segment and the other to the bend's average: shorter than hw x tan(half the bend) and
            /// it folds over its neighbour (a tee's mouth cut 1.5 m from a main's 31-degree kink doubled the main's folds).</summary>
            public double Snap(double a, double hw, int keep)
            {
                double best = a, bestD = double.MaxValue;
                for (int k = 1; k < N; k++)
                {
                    double d = A[k] - a;
                    if ((keep > 0 && d < 0) || (keep < 0 && d > 0) || Math.Abs(d) >= bestD) continue;
                    Dir(k - 1, out double ax, out double az); Dir(k, out double bx, out double bz);
                    double turn = Math.Acos(Math.Clamp(ax * bx + az * bz, -1.0, 1.0));
                    double reach = Math.Max(NearRow, hw * Math.Tan(turn * 0.5) * 1.25 + 0.25);
                    if (Math.Abs(d) < reach) { best = A[k]; bestD = Math.Abs(d); }
                }
                return best;
            }
            /// <summary>Nearest point of the ribbon: its arc and the signed lateral offset (+ on the +normal side).</summary>
            public (double a, double lat) Project(double x, double z)
            {
                double best = double.MaxValue, ba = 0, bl = 0;
                for (int k = 0; k < N; k++)
                {
                    double sx = X[k + 1] - X[k], sz = Z[k + 1] - Z[k], ss = sx * sx + sz * sz;
                    if (ss < 1e-12) continue;
                    double t = Math.Clamp(((x - X[k]) * sx + (z - Z[k]) * sz) / ss, 0, 1);
                    double px = X[k] + sx * t, pz = Z[k] + sz * t, d = (x - px) * (x - px) + (z - pz) * (z - pz);
                    if (d < best)
                    {
                        best = d; ba = A[k] + Len(k) * t;
                        double l = Math.Sqrt(ss);
                        bl = ((x - px) * -sz + (z - pz) * sx) / l;
                    }
                }
                return (ba, bl);
            }
        }

        /// <summary>A piece end within this of a profile point is squared to that point's averaged tangent, not the segment's.
        /// A junction's cut can land a few centimetres from one: squared differently, the sliver of piece between them was a
        /// bow-tie, its two ends crossing (a node's first test: 22 samples of a main's slab folded over its patch).</summary>
        public const double NearRow = 0.5;
        /// <summary>Which tangent squares a piece end at t of a segment segLen long: 0 the profile point's at its start, 1
        /// at its end, -1 the segment's own.</summary>
        internal static int RowEnd(double t, double segLen)
        {
            if (t <= 0 || t * segLen < NearRow) return 0;
            if (t >= 1 || (1 - t) * segLen < NearRow) return 1;
            return -1;
        }

        /// <summary>A stable name for a line: what it is and where it runs, so a line rebuilt after a cache trim is the
        /// same line to every junction that names it.</summary>
        internal readonly record struct LineKey(byte Kind, bool Ramp, long X0, long Z0, long X1, long Z1, int N);
        internal static LineKey KeyOf(Line e) => new((byte)e.Kind, e.Ramp, (long)Math.Round(e.X[0] * 100), (long)Math.Round(e.Z[0] * 100),
                                                     (long)Math.Round(e.X[e.X.Length - 1] * 100), (long)Math.Round(e.Z[e.Z.Length - 1] * 100), e.X.Length);

        /// <summary>What a junction does to a host ribbon over [A0, A1] (ribbon arc): leaves its edge bevel off on `Side`
        /// (a tee's mouth), or widens it on `Side` (a taper) by W interpolated over WArc.</summary>
        sealed class HostMod
        {
            public int Side; public double A0, A1; public bool NoBevel;
            public double[] WArc; public float[] W;
            public float WidenAt(double a)
            {
                if (WArc == null) return 0f;
                if (a <= WArc[0]) return W[0];
                for (int i = 0; i + 1 < WArc.Length; i++)
                    if (a <= WArc[i + 1]) { double f = (a - WArc[i]) / Math.Max(1e-12, WArc[i + 1] - WArc[i]); return W[i] + (W[i + 1] - W[i]) * (float)f; }
                return W[W.Length - 1];
            }
        }

        /// <summary>Where a joined line's ribbon is cut back to (arc from its start, and to), the heading its cut end is
        /// squared to (a taper's: the carriageway's), and whether each end is joined (no end ramp).</summary>
        sealed class LineTrim
        {
            public double R0, R1 = double.MaxValue;
            public (double x, double z)? T0, T1;
            public float X0, X1;   // a squared end's extra half-width (TaperGeo.Widen)
            public float H0, H1;   // ...and its profile (TaperGeo.H)
            /// <summary>Spans cut out of the middle (arc from, to): a road split at a crossing, its two tees' patches pave them.</summary>
            public List<(double a0, double a1)> Gaps;
            public bool J0, J1;
        }

        sealed class JunctionPlan
        {
            public readonly Dictionary<LineKey, LineTrim> Trims = new();
            public readonly Dictionary<(LineKey, double), List<HostMod>> Mods = new();
            public readonly List<RoadPatch> Patches = new();
            public readonly List<RoadMark> Marks = new();
            public LineTrim TrimOf(Line e) { var k = KeyOf(e); if (!Trims.TryGetValue(k, out var t)) Trims[k] = t = new LineTrim(); return t; }
            public List<HostMod> ModsOf(Line e, double off)
            {
                var k = (KeyOf(e), off);
                if (!Mods.TryGetValue(k, out var l)) Mods[k] = l = new List<HostMod>();
                return l;
            }
        }

        // ---- gathering

        /// <summary>Every joined end, town and highway anchor whose junction can reach the rectangle. Deterministic: the same
        /// junctions for whichever region asks, because they come from the lines' cells and bands, not the region's set.</summary>
        JunctionPlan PlanJunctions(double x0, double z0, double x1, double z1, double maxPiece)
        {
            var plan = new JunctionPlan();
            const double M = 120;   // a junction's reach (a node's trims, a taper's widening) past its anchor
            bool Near(double x, double z) => x >= x0 - M && x <= x1 + M && z >= z0 - M && z <= z1 + M;
            var tees = new List<(Line j, double a0, int dir, Line host, int pair)>();   // a0 NaN: the line's own end
            var tapers = new List<(Line r, bool atEnd, Line host, int cw)>();
            var ends = new Dictionary<(long, long), List<(Line e, bool atEnd)>>();
            void End(Line e, bool atEnd)
            {
                int n = e.X.Length - 1;
                double x = atEnd ? e.X[n] : e.X[0], z = atEnd ? e.Z[n] : e.Z[0];
                if (!Near(x, z)) return;
                var key = ((long)Math.Round(x * 10), (long)Math.Round(z * 10));
                if (!ends.TryGetValue(key, out var l)) ends[key] = l = new List<(Line, bool)>();
                l.Add((e, atEnd));
            }
            Line HighwayById(long id, double x, double z)
            {
                var hw = new List<Line>();
                TakeHighways(x - 50, z - 50, x + 50, z + 50, hw);
                foreach (var h in hw) if (h.Id == id) return h;
                return null;
            }
            void Joins(Line e)
            {
                int n = e.X.Length - 1;
                foreach (var (jn, atEnd) in new[] { (e.StartJoin, false), (e.EndJoin, true) })
                {
                    if (jn == null) continue;
                    double x = atEnd ? e.X[n] : e.X[0], z = atEnd ? e.Z[n] : e.Z[0];
                    if (!Near(x, z)) continue;
                    var host = jn.Host ?? HighwayById(jn.HostId, x, z);   // (0 is an Id: band 0's segment 0)
                    if (host == null || !host.Exists) continue;
                    if (jn.Type == JoinType.Tee) tees.Add((e, double.NaN, atEnd ? -1 : 1, host, -1));
                    else tapers.Add((e, atEnd, host, jn.Side));
                }
            }
            long ci0 = (long)Math.Floor((x0 - M) / MainCell) - 2, ci1 = (long)Math.Floor((x1 + M) / MainCell) + 2;
            long cj0 = (long)Math.Floor((z0 - M) / MainCell) - 2, cj1 = (long)Math.Floor((z1 + M) / MainCell) + 2;
            for (long i = ci0; i <= ci1; i++)
                for (long j = cj0; j <= cj1; j++)
                    for (int dir = 0; dir < 2; dir++)
                    {
                        var e = Main(i, j, dir);
                        if (!e.Exists) continue;
                        End(e, false); End(e, true);
                        if (e.Branches != null) foreach (var br in e.Branches) Joins(br);
                        if (e.Ramps != null) foreach (var rp in e.Ramps) Joins(rp);
                    }
            var hws = new List<Line>();
            TakeHighways(x0 - M, z0 - M, x1 + M, z1 + M, hws);
            var anchors = new List<(Line e, bool atEnd)>();
            foreach (var h in hws)
            {
                if (h.Ramps != null) foreach (var rp in h.Ramps) Joins(rp);
                int n = h.X.Length - 1;
                if (Near(h.X[0], h.Z[0])) anchors.Add((h, false));
                if (Near(h.X[n], h.Z[n])) anchors.Add((h, true));
            }
            // HIGHWAY ANCHORS: one segment's end where the next one's begins carries straight on -- no end ramp either side
            for (int a = 0; a < anchors.Count; a++)
                for (int b = 0; b < anchors.Count; b++)
                {
                    if (a == b) continue;
                    var (ea, ra) = anchors[a]; var (eb, rb) = anchors[b];
                    int na = ea.X.Length - 1, nb = eb.X.Length - 1;
                    double xa = ra ? ea.X[na] : ea.X[0], za = ra ? ea.Z[na] : ea.Z[0], xb = rb ? eb.X[nb] : eb.X[0], zb = rb ? eb.Z[nb] : eb.Z[0];
                    if (Math.Abs(xa - xb) > 0.5 || Math.Abs(za - zb) > 0.5) continue;
                    var tr = plan.TrimOf(ea);
                    if (ra) tr.J1 = true; else tr.J0 = true;
                }
            // TAPERS: the ramp cut back to its gore, the carriageway widened to carry it there
            // the decks other roads' crossings lay on a highway (its underpasses are theirs: mains', north-south highways')
            var ups = new List<Underpass>();
            for (long i = ci0; i <= ci1; i++)
                for (long j = cj0; j <= cj1; j++)
                    for (int dir = 0; dir < 2; dir++) { var e = Main(i, j, dir); if (e.Exists && e.Underpasses != null) ups.AddRange(e.Underpasses); }
            foreach (var h in hws) if (h.Underpasses != null) ups.AddRange(h.Underpasses);
            foreach (var (r, atEnd, host, cw) in tapers)
                if (TaperShape(r, atEnd, host, cw, ups) is TaperGeo g)
                {
                    var tr = plan.TrimOf(r);
                    if (atEnd) { tr.R1 = g.R.Total - g.Gore; tr.J1 = true; tr.T1 = g.Tangent; tr.X1 = g.Widen; tr.H1 = g.H; } else { tr.R0 = g.Gore; tr.J0 = true; tr.T0 = g.Tangent; tr.X0 = g.Widen; tr.H0 = g.H; }
                    plan.ModsOf(host, cw * HighwayRibbonOffset).Add(new HostMod { Side = cw, A0 = g.A0, A1 = g.A1, WArc = g.WArc, W = g.W });
                    PaintTaper(plan, g, host, cw);
                }
            // TOWNS: each main cut back past its fillets -- only where the middle could be paved
            foreach (var kv in ends)
            {
                if (kv.Value.Count < 2) continue;
                bool allMain = true;
                foreach (var (e, _) in kv.Value) if (e.Kind != RoadKind.Main || e.Ramp) allMain = false;
                if (!allMain) continue;
                var arms = new List<Arm>();
                foreach (var (e, atEnd) in kv.Value) { var r = new Rib(e, 0); arms.Add(new Arm { E = e, R = r, A0 = atEnd ? r.Total : 0, Dir = atEnd ? -1 : 1 }); }
                var (e0, end0) = kv.Value[0];
                MakeNode(arms, end0 ? e0.X[^1] : e0.X[0], end0 ? e0.Z[^1] : e0.Z[0]);
            }
            static bool DeckedAt(Line e, double f)
            {
                var cover = e.DeckCover?[0];
                if (!Bridges || cover == null) return false;
                foreach (var c in cover) if (c.F1 >= f - 3 && c.F0 <= f + 3) return true;
                return false;
            }
            void MakeNode(List<Arm> arms, double nx, double nz)
            {
                if (NodeShape(arms, nx, nz) is not NodeGeo g) return;
                if (NodePatch(g) is not RoadPatch p) return;
                plan.Patches.Add(p);
                PaintNode(plan, g, p);
                for (int i = 0; i < g.Arms.Count; i++)
                {
                    var arm = g.Arms[i];
                    var tr = plan.TrimOf(arm.E);
                    double cut = arm.Arc(g.Trim[i]);
                    if (arm.IsEnd)
                    {
                        if (arm.Dir > 0) { tr.R0 = Math.Max(tr.R0, cut); tr.J0 = true; } else { tr.R1 = Math.Min(tr.R1, cut); tr.J1 = true; }
                    }
                    else (tr.Gaps ??= new List<(double, double)>()).Add(arm.Dir > 0 ? (arm.A0, cut) : (cut, arm.A0));
                }
            }
            // LOOSE ENDS: a branch that stops ON another road -- it wandered into a main or another branch, which it does not
            // know of -- tees into it there like any other
            {
                var roads = new List<Line>();
                for (long i = ci0; i <= ci1; i++)
                    for (long j = cj0; j <= cj1; j++)
                        for (int dir = 0; dir < 2; dir++)
                        {
                            var e = Main(i, j, dir);
                            if (!e.Exists) continue;
                            roads.Add(e);
                            if (e.Branches != null) roads.AddRange(e.Branches);
                        }
                foreach (var e in roads)
                {
                    if (e.Kind == RoadKind.Main || e.Ramp || e.EndJoin != null) continue;
                    double x = e.X[^1], z = e.Z[^1];
                    if (!Near(x, z)) continue;
                    Line host = null; double best = double.MaxValue;
                    foreach (var h in roads)
                    {
                        if (ReferenceEquals(h, e) || x < h.MinX - 20 || x > h.MaxX + 20 || z < h.MinZ - 20 || z > h.MaxZ + 20) continue;
                        double d = NearestOn(h, x, z).d;
                        if (d < RibbonHalfOf(h.Kind) + 0.5 && d < best) { best = d; host = h; }
                    }
                    if (host != null) tees.Add((e, double.NaN, -1, host, -1));
                }
            }
            int pairs = 0;
            // CROSSINGS: two roads crossing on the level (a branch wandering over a main or another branch). Not at a shallow
            // angle, not where they stand apart in height, and not within CrossingClear of any other crossing or any road's
            // end (a tee's mouth, a town, a ramp's foot): two junctions' patches would overlap, and two roads bundled side by
            // side cross and recross with nothing to pave between. ⚠ Decided from what lies round the crossing alone,
            // gathered CrossingClear wider than the junctions a region draws -- a region whose window cut off a neighbouring
            // crossing would pave this one while the next region, seeing both, drew its roads whole
            const double W = M + CrossingClear + 20;
            bool Wide(double x, double z) => x >= x0 - W && x <= x1 + W && z >= z0 - W && z <= z1 + W;
            var flat = new List<Line>(); var endPts = new List<(double x, double z)>();
            void EndsOf(Line e) { endPts.Add((e.X[0], e.Z[0])); endPts.Add((e.X[^1], e.Z[^1])); }
            for (long i = ci0; i <= ci1; i++)
                for (long j = cj0; j <= cj1; j++)
                    for (int dir = 0; dir < 2; dir++)
                    {
                        var e = Main(i, j, dir);
                        if (!e.Exists) continue;
                        EndsOf(e);
                        if (OverlapsW(e)) flat.Add(e);
                        if (e.Branches != null) foreach (var br in e.Branches) { EndsOf(br); if (OverlapsW(br)) flat.Add(br); }
                        if (e.Ramps != null) foreach (var rp in e.Ramps) EndsOf(rp);
                    }
            bool OverlapsW(Line e) => e.MaxX >= x0 - W && e.MinX <= x1 + W && e.MaxZ >= z0 - W && e.MinZ <= z1 + W;
            var cands = new List<(double x, double z, bool good, Line A, int sa, double ta, Line B, int sb, double tb)>();
            for (int p = 0; p < flat.Count; p++)
                for (int q = p + 1; q < flat.Count; q++)
                {
                    var A = flat[p]; var B = flat[q];
                    if (A.MaxX < B.MinX || B.MaxX < A.MinX || A.MaxZ < B.MinZ || B.MaxZ < A.MinZ) continue;
                    for (int sa = 0; sa + 1 < A.X.Length; sa++)
                        for (int sb = 0; sb + 1 < B.X.Length; sb++)
                        {
                            if (!SegCross(A.X[sa], A.Z[sa], A.X[sa + 1], A.Z[sa + 1], B.X[sb], B.Z[sb], B.X[sb + 1], B.Z[sb + 1], out double ta, out double tb)) continue;
                            if (ta < 0 || ta >= 1 || tb < 0 || tb >= 1) continue;
                            double x = A.X[sa] + (A.X[sa + 1] - A.X[sa]) * ta, z = A.Z[sa] + (A.Z[sa + 1] - A.Z[sa]) * ta;
                            if (!Wide(x, z)) continue;
                            double ax = A.X[sa + 1] - A.X[sa], az = A.Z[sa + 1] - A.Z[sa], bx = B.X[sb + 1] - B.X[sb], bz = B.Z[sb + 1] - B.Z[sb];
                            double sin = Math.Abs(ax * bz - az * bx) / Math.Max(1e-9, Math.Sqrt((ax * ax + az * az) * (bx * bx + bz * bz)));
                            float ha = SurfaceY(A.Kind, A.H[sa] + (A.H[sa + 1] - A.H[sa]) * (float)ta), hb = SurfaceY(B.Kind, B.H[sb] + (B.H[sb + 1] - B.H[sb]) * (float)tb);
                            bool good = sin >= Math.Sin(25 * Math.PI / 180) && Math.Abs(ha - hb) <= 1.0f && !DeckedAt(A, sa + ta) && !DeckedAt(B, sb + tb);
                            cands.Add((x, z, good, A, sa, ta, B, sb, tb));
                        }
                }
            foreach (var c in cands)
            {
                if (!c.good || !Near(c.x, c.z)) continue;
                bool crowded = false;
                foreach (var o in cands) if (!ReferenceEquals(o.A, c.A) || !ReferenceEquals(o.B, c.B) || o.sa != c.sa || o.sb != c.sb) if (Math.Abs(o.x - c.x) < CrossingClear && Math.Abs(o.z - c.z) < CrossingClear && (o.x - c.x) * (o.x - c.x) + (o.z - c.z) * (o.z - c.z) < CrossingClear * CrossingClear) crowded = true;
                foreach (var (ex, ez) in endPts) if ((ex - c.x) * (ex - c.x) + (ez - c.z) * (ez - c.z) < CrossingClear * CrossingClear) crowded = true;
                if (crowded) continue;
                // the smaller road is split and each half tees into the bigger one on its own side (strawberry 2026-10-11:
                // "if a side road is going to cross another, instead of just crossing it, split it and merge-connect both
                // sides"); the bigger one runs on whole. Of two equal roads, the one starting further east (then north) is split
                bool aMinor = Rank(c.A) != Rank(c.B) ? Rank(c.A) < Rank(c.B) : KeyOf(c.A).X0 != KeyOf(c.B).X0 ? KeyOf(c.A).X0 > KeyOf(c.B).X0 : KeyOf(c.A).Z0 > KeyOf(c.B).Z0;
                var minor = aMinor ? c.A : c.B; var major = aMinor ? c.B : c.A;
                int seg = aMinor ? c.sa : c.sb; double tt = aMinor ? c.ta : c.tb;
                var rm = new Rib(minor, 0);
                double am = rm.A[seg] + rm.Len(seg) * tt;
                int pairId = pairs++;
                tees.Add((minor, am, 1, major, pairId)); tees.Add((minor, am, -1, major, pairId));
            }
            // TEES: a patch's edge along its host must have a vertex at every cut the host's pieces will have, so the
            // patches are made against every tee's cuts; one that cannot be paved (a mouth too tight for its fillets)
            // gives up its trim and its cuts, and the rest are made again without them
            var live = new List<TeeGeo>();
            foreach (var (j, a0, dir, host, pair) in tees)
                if (TeeShape(j, a0, dir, host, pair) is TeeGeo g && !Decked(g.H, host, g.ALo - 10, g.AHi + 10)) live.Add(g);
            // a road split at a crossing is cut only if BOTH its halves tee in: one alone would leave the other overlapping
            var halves = new Dictionary<int, int>();
            foreach (var g in live) if (g.Pair >= 0) halves[g.Pair] = halves.GetValueOrDefault(g.Pair) + 1;
            live.RemoveAll(g => g.Pair >= 0 && halves[g.Pair] < 2);
            var made = new List<(TeeGeo g, RoadPatch p)>();
            for (int pass = 0; pass < 4; pass++)
            {
                var mods = new Dictionary<LineKey, List<HostMod>>();
                foreach (var g in live)
                {
                    var k = KeyOf(g.HostLine);
                    if (!mods.TryGetValue(k, out var l)) mods[k] = l = new List<HostMod>();
                    l.Add(new HostMod { Side = g.HSide, A0 = g.ALo, A1 = g.AHi, NoBevel = true });
                }
                made.Clear();
                var failed = new List<TeeGeo>();
                foreach (var g in live)
                {
                    if (TeePatch(g, mods[KeyOf(g.HostLine)], maxPiece) is RoadPatch p) made.Add((g, p));
                    else failed.Add(g);
                }
                if (failed.Count == 0) break;
                foreach (var f in failed.ToArray()) if (f.Pair >= 0) foreach (var o in live) if (o.Pair == f.Pair && !failed.Contains(o)) failed.Add(o);
                foreach (var f in failed) live.Remove(f);
                if (pass == 3) made.Clear();
            }
            foreach (var (g, p) in made)
            {
                plan.Patches.Add(p);
                PaintTee(plan, g, p);
                var tr = plan.TrimOf(g.JLine);
                if (!g.IsEnd) (tr.Gaps ??= new List<(double, double)>()).Add(g.Dir > 0 ? (g.A0, g.A0 + g.Trim) : (g.A0 - g.Trim, g.A0));
                else if (g.Dir < 0) { tr.R1 = g.J.Total - g.Trim; tr.J1 = true; } else { tr.R0 = g.Trim; tr.J0 = true; }
                plan.ModsOf(g.HostLine, 0.0).Add(new HostMod { Side = g.HSide, A0 = g.ALo, A1 = g.AHi, NoBevel = true });
            }
            return plan;
        }

        /// <summary>Does a deck carry the host anywhere over [a0, a1] of its ribbon? A tee's mouth is not cut into a bridge.</summary>
        static bool Decked(Rib h, Line host, double a0, double a1)
        {
            var cover = host.DeckCover?[0];
            if (!Bridges || cover == null) return false;
            var (k0, t0) = h.Find(a0); var (k1, t1) = h.Find(a1);
            double f0 = k0 + t0, f1 = k1 + t1;
            foreach (var c in cover) if (c.F1 >= f0 && c.F0 <= f1) return true;
            return false;
        }

        /// <summary>The piece boundaries PiecesIn puts in segment k of a ribbon over [u0, u1]: split at every cut inside it,
        /// each part into pieces no longer than maxPiece. ONE rule for the pieces and for a patch's seam with them.</summary>
        static List<double> SegmentBreaks(double u0, double u1, List<double> cuts, double segLen, double maxPiece)
        {
            var pts = new List<double> { u0 };
            if (cuts != null)
            {
                var inside = new List<double>();
                foreach (var c in cuts) if (c > u0 + 1e-9 && c < u1 - 1e-9) inside.Add(c);
                inside.Sort();
                foreach (var c in inside) if (c - pts[pts.Count - 1] > 1e-9) pts.Add(c);
            }
            if (u1 - pts[pts.Count - 1] > 1e-9 || pts.Count == 1) pts.Add(u1);
            var outp = new List<double> { pts[0] };
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                double p = pts[i], q = pts[i + 1];
                int pcs = Math.Max(1, (int)Math.Ceiling(segLen * (q - p) / maxPiece));
                for (int s = 1; s <= pcs; s++) outp.Add(s == pcs ? q : p + (q - p) * s / pcs);
            }
            return outp;
        }

        /// <summary>The arcs at which a host ribbon's mods cut it, as fractions per segment.</summary>
        static Dictionary<int, List<double>> CutsOf(Rib r, List<HostMod> mods, LineTrim trim)
        {
            var cuts = new Dictionary<int, List<double>>();
            void Add(double a)
            {
                if (a <= 1e-9 || a >= r.Total - 1e-9) return;
                var (k, t) = r.Find(a);
                if (t <= 1e-9 || t >= 1 - 1e-9) return;   // on a profile point: already a boundary
                if (!cuts.TryGetValue(k, out var l)) cuts[k] = l = new List<double>();
                l.Add(t);
            }
            if (mods != null) foreach (var m in mods) { Add(m.A0); Add(m.A1); }
            if (trim != null)
            {
                if (trim.R0 > 0) Add(trim.R0);
                if (trim.R1 < r.Total) Add(trim.R1);
                if (trim.Gaps != null) foreach (var (g0, g1) in trim.Gaps) { Add(g0); Add(g1); }
            }
            return cuts;
        }

        // ---- PAINT (the road textures' own: dashes 4 m in every 8, on while the arc's eighth-metre phase is in [0.25, 0.75))

        const double DashPeriod = 8.0, StopWidth = 0.5;
        /// <summary>Where a class's lines lie across it (ribbon normal units from its centre), how wide, which colour, and
        /// dashed or solid -- read off its texture: a main's solid yellow centre 0.575 m wide and white lane dashes 4.82 m
        /// out; a small road's dashed yellow centre, a ramp's dashed white one, 0.29 m; a trail none.</summary>
        static IEnumerable<(double lat, double width, byte colour, bool dashed)> LinesOf(Line e)
        {
            if (e.Kind == RoadKind.Main) { yield return (0, 0.575, 1, false); yield return (4.82, 0.29, 0, true); yield return (-4.82, 0.29, 0, true); }
            else if (e.Kind == RoadKind.Small) yield return (0, 0.29, (byte)(e.Ramp ? 0 : 1), true);
        }

        /// <summary>One stripe along a ribbon from arc a0 to a1 at lateral `lat`, `width` wide; dashed in phase with the
        /// road's own texture so a line carried on from it keeps its rhythm.</summary>
        static void Stripe(RoadMark m, Rib r, double a0, double a1, double lat, double width, bool dashed, RoadKind kind)
        {
            if (a1 < a0) (a0, a1) = (a1, a0);
            a0 = Math.Max(0, a0); a1 = Math.Min(r.Total, a1);
            var spans = new List<(double, double)>();
            if (!dashed) spans.Add((a0, a1));
            else
                for (double c = Math.Floor(a0 / DashPeriod) * DashPeriod; c < a1; c += DashPeriod)
                {
                    double s0 = Math.Max(a0, c + 0.25 * DashPeriod), s1 = Math.Min(a1, c + 0.75 * DashPeriod);
                    if (s1 - s0 > 0.05) spans.Add((s0, s1));
                }
            float lift = Lift(kind);
            foreach (var (s0, s1) in spans)
            {
                int steps = Math.Max(1, (int)Math.Ceiling((s1 - s0) / 2.0));
                for (int q = 0; q < steps; q++)
                {
                    double sa = s0 + (s1 - s0) * q / steps, sb = s0 + (s1 - s0) * (q + 1) / steps;
                    foreach (double a in new[] { sa, sb })
                    {
                        var (k, t) = r.Find(a);
                        r.Point(k, t, out double x, out double z);
                        r.Dir(k, out double dx, out double dz);   // the segment's own normal: a stripe is not a piece row
                        double nx = -dz, nz = dx;
                        float y = SurfaceY(kind, r.H(k, t));
                        foreach (double l in new[] { lat + width * 0.5, lat - width * 0.5 })
                        { m.X.Add(x + nx * l); m.Z.Add(z + nz * l); m.Y.Add(y); m.Lift.Add(lift); }
                    }
                }
            }
        }

        /// <summary>A stop line across one approach, centred on arc `a`, from the centre line to the edge on `side`.</summary>
        static void StopLine(RoadMark m, Rib r, double a, int side, double hw, RoadKind kind) =>
            Stripe(m, r, a - StopWidth * 0.5, a + StopWidth * 0.5, side * (hw - 0.3) * 0.5, hw - 0.3, false, kind);

        /// <summary>A tee's paint: the joiner's lines carried on into the mouth, and a stop line across its approach just
        /// short of the host's edge.</summary>
        static void PaintTee(JunctionPlan plan, TeeGeo g, RoadPatch p)
        {
            var J = g.J; var e = g.JLine;
            if (e.Kind == RoadKind.Trail) return;
            double total = J.Total;
            double Arc(double s) => g.A0 + g.Dir * s;
            // the stop line as near the host as it clears the host's asphalt across the joiner's whole width (a skewed
            // tee's first metre beyond the edge is still the host's on one side: paint there stood on the host, at the
            // joiner's height)
            double stop = g.Trim - StopWidth;
            for (double s = g.SX + 1.2; s < g.Trim - StopWidth; s += 0.25)
            {
                bool clear = true;
                foreach (int side in new[] { 1, -1 })
                    foreach (double ds in new[] { -StopWidth * 0.5, StopWidth * 0.5 })
                    {
                        J.Edge(Arc(s + ds), side, g.hwJ, out double ex, out double ez);
                        if (Math.Abs(g.H.Project(ex, ez).lat) < g.hwH + 0.3) clear = false;
                    }
                if (clear) { stop = s; break; }
            }
            // the approach is the side on the right driving toward the host: -normal on a ribbon that starts at the host
            int approach = -g.Dir;
            var white = new RoadMark { Colour = 0, AnchorX = p.AnchorX, AnchorZ = p.AnchorZ, On = p };
            var yellow = new RoadMark { Colour = 1, AnchorX = p.AnchorX, AnchorZ = p.AnchorZ, On = p };
            foreach (var (lat, width, colour, dashed) in LinesOf(e))
                Stripe(colour == 1 ? yellow : white, J, Arc(stop + StopWidth * 0.5), Arc(g.Trim), lat, width, dashed, e.Kind);
            StopLine(white, J, Arc(stop), approach, g.hwJ, e.Kind);
            if (white.Quads > 0) plan.Marks.Add(white);
            if (yellow.Quads > 0) plan.Marks.Add(yellow);
        }

        // ---- TEE

        sealed class TeeGeo
        {
            public Line JLine, HostLine; public Rib J, H;
            /// <summary>Where the joiner's joined end is (arc) and which way it runs from it (+1 toward its end): a line's own
            /// end, or one side of a road split where it crossed another (Pair names the other side).</summary>
            public double A0; public int Dir; public bool IsEnd; public int Pair = -1;
            public double Trim, ALo, AHi; public int HSide;
            public double hwJ, hwH;
            // per side σ = +1 / -1 (the joiner's left / right, looking away from the host): the corner, the tangent points
            public double[] CX = new double[2], CZ = new double[2], BpX = new double[2], BpZ = new double[2], TA = new double[2], SBp = new double[2];
            public double DX, DZ;   // the joiner's heading away from the host
            public double SX;       // where the joiner's centreline leaves the host's asphalt (out from its joined end)
        }

        TeeGeo TeeShape(Line j, double a0, int dir, Line host, int pair)
        {
            var J = new Rib(j, 0); var H = new Rib(host, 0);
            double hwH = RibbonHalfOf(host.Kind), hwJ = RibbonHalfOf(j.Kind), total = J.Total;
            bool isEnd = double.IsNaN(a0);
            if (isEnd) a0 = dir > 0 ? 0 : total;
            double room = dir > 0 ? total - a0 : a0;
            double ArcOf(double s) => a0 + dir * s;
            double Out(double s) { J.PointAt(ArcOf(s), out double x, out double z); return Math.Abs(H.Project(x, z).lat) - hwH; }
            // 1. where the joiner's centreline leaves the host's asphalt
            double lo = 0, hi = -1;
            for (double s = 0; s <= Math.Min(40, room * 0.5); s += 0.25) if (Out(s) >= 0) { hi = s; break; }
            if (hi < 0) return null;
            lo = Math.Max(0, hi - 0.25);
            if (Out(lo) >= 0) hi = lo;
            for (int it = 0; it < 40 && hi - lo > 1e-6; it++) { double m = (lo + hi) * 0.5; if (Out(m) >= 0) hi = m; else lo = m; }
            double sX = hi;
            J.PointAt(ArcOf(sX), out double qx, out double qz);
            J.PointAt(ArcOf(Math.Min(room * 0.5, sX + 12)), out double q2x, out double q2z);
            double dx = q2x - qx, dz = q2z - qz, dl = Math.Sqrt(dx * dx + dz * dz);
            if (dl < 1) return null;
            dx /= dl; dz /= dl;
            var (aX, latX) = H.Project(qx, qz);
            int hSide = latX >= 0 ? 1 : -1;
            var (hk, ht) = H.Find(aX);
            H.Dir(hk, out double tHx, out double tHz);
            double nHx = -tHz, nHz = tHx;
            H.Point(hk, ht, out double ex, out double ez);
            ex += nHx * hSide * hwH; ez += nHz * hSide * hwH;
            double nJx = -dz, nJz = dx;
            var g = new TeeGeo { JLine = j, HostLine = host, J = J, H = H, A0 = a0, Dir = dir, IsEnd = isEnd, Pair = pair, HSide = hSide, hwJ = hwJ, hwH = hwH, DX = dx, DZ = dz, SX = sX };
            double R = FilletRadius(j.Kind, j.Ramp), trim = sX + 1.0;
            for (int si = 0; si < 2; si++)
            {
                int sg = si == 0 ? 1 : -1;
                // the joiner's edge (Q + sg nJ hwJ + u dJ) meets the host's (E + v tH)
                double px = qx + nJx * sg * hwJ - ex, pz = qz + nJz * sg * hwJ - ez;
                double den = dx * tHz - dz * tHx;
                if (Math.Abs(den) < 0.2) return null;   // too shallow for a tee
                double u = (tHx * pz - tHz * px) / den, v = (dx * pz - dz * px) / den;
                double cx = ex + tHx * v, cz = ez + tHz * v;
                // along the host edge away from the joiner on this side, and the corner's interior angle
                double sgn = Math.Sign(sg * (nJx * tHx + nJz * tHz));
                if (sgn == 0) sgn = 1;
                double hx = sgn * tHx, hz = sgn * tHz;
                double alpha = Math.Acos(Math.Clamp(hx * dx + hz * dz, -1.0, 1.0));
                double L = Math.Clamp(R / Math.Tan(alpha * 0.5), 1.5, 16.0);
                g.CX[si] = cx; g.CZ[si] = cz;
                g.BpX[si] = cx + dx * L; g.BpZ[si] = cz + dz * L;
                g.SBp[si] = sX + (g.BpX[si] - qx) * dx + (g.BpZ[si] - qz) * dz;
                g.TA[si] = aX + v + sgn * L;
                trim = Math.Max(trim, sX + (g.BpX[si] - qx) * dx + (g.BpZ[si] - qz) * dz + 0.75);
            }
            if (trim > room * 0.5) return null;
            // the joiner keeps the side away from the host; the host keeps both
            g.Trim = (J.Snap(a0 + dir * trim, hwJ, dir) - a0) * dir;
            g.ALo = H.Snap(Math.Min(g.TA[0], g.TA[1]), hwH, 0); g.AHi = H.Snap(Math.Max(g.TA[0], g.TA[1]), hwH, 0);
            if (g.AHi - g.ALo < 2 * hwJ) return null;
            if (g.ALo < 0 || g.AHi > H.Total) return null;
            return g;
        }

        RoadPatch TeePatch(TeeGeo g, List<HostMod> hostMods, double maxPiece)
        {
            var J = g.J; var H = g.H;
            var trimMods = new LineTrim();   // the host is not trimmed at a tee
            var cuts = CutsOf(H, hostMods, trimMods);
            var vx = new List<double>(); var vz = new List<double>(); var vy = new List<float>(); var vl = new List<float>();
            float liftH = Lift(g.HostLine.Kind), liftJ = Lift(g.JLine.Kind);
            int V(double x, double z, float y, float lift) { vx.Add(x); vz.Add(z); vy.Add(y); vl.Add(lift); return vx.Count - 1; }
            double total = J.Total;
            double JArc(double s) => g.A0 + g.Dir * s;
            float HSurf(double x, double z)
            {
                var (a, _) = H.Project(x, z);
                return SurfaceY(g.HostLine.Kind, H.HAt(a));
            }
            float JSurf(double x, double z)
            {
                var (a, _) = J.Project(x, z);
                return SurfaceY(g.JLine.Kind, J.HAt(a));
            }
            // the host's top edge from ALo to AHi, at every boundary its pieces will have. ⚠ By (segment, t), never by arc:
            // a boundary on a profile point found back from its arc can land a hair inside the segment and take the
            // SEGMENT's normal instead of the averaged one -- the edge point then slides along by hw x half the bend
            var chain = new List<int>();
            void HostVertex(int k, double t)
            {
                H.Point(k, t, out double x, out double z);
                H.Normal(k, t, out double nx, out double nz);
                x += nx * g.HSide * g.hwH; z += nz * g.HSide * g.hwH;
                chain.Add(V(x, z, SurfaceY(g.HostLine.Kind, H.H(k, t)), liftH));
            }
            { var (k, t) = H.Find(g.ALo); HostVertex(k, t); }
            for (int k = 0; k < H.N; k++)
            {
                if (H.A[k + 1] <= g.ALo || H.A[k] >= g.AHi) continue;
                double len = H.Len(k);
                if (len < 1e-4) continue;
                cuts.TryGetValue(k, out var segCuts);
                foreach (double t in SegmentBreaks(0, 1, segCuts, len, maxPiece))
                {
                    double a = H.A[k] + len * t;
                    if (a > g.ALo + 1e-6 && a < g.AHi - 1e-6) HostVertex(k, t);
                }
            }
            { var (k, t) = H.Find(g.AHi); HostVertex(k, t); }
            // the joiner's first edge
            var (jk, jt) = J.Find(JArc(g.Trim));
            J.Point(jk, jt, out double ajx, out double ajz);
            J.Normal(jk, jt, out double njx, out double njz);
            J.Dir(jk, out double tjx, out double tjz);
            // σ = +1 is the joiner's left looking away from the host: on its ribbon that is +normal at the start, -normal at the end
            int Na(int si) => (si == 0 ? 1 : -1) * g.Dir;
            // the joiner's top edge on side si, s out from where it leaves the host -- its own ribbon, as its pieces lay it
            int JEdgeV(int si, double s, float lift)
            {
                J.Edge(JArc(s), Na(si), g.hwJ, out double x, out double z);
                return V(x, z, SurfaceY(g.JLine.Kind, J.HAt(JArc(s))), lift);
            }
            int siLo = g.TA[0] <= g.TA[1] ? 0 : 1, siHi = 1 - siLo;
            var ring = new List<int>(chain);
            var skirts = new List<List<int>>();
            // A FILLET leaves the host's edge along it and arrives on the joiner's edge along that: its control point is
            // where the two edges' own tangents there meet (a host bending through the mouth is not its tangent at the
            // crossing, nor a curving ramp its chord). It runs a hair outside both, never in: an edge and a curve tangent to
            // it part only quadratically, and on the outside of a bend a curve built on a tangent cuts back into the asphalt
            (double x, double z) Control(int si, double aT, double bpx, double bpz)
            {
                var (k, _) = H.Find(aT);
                H.Dir(k, out double ex, out double ez);
                H.Edge(aT, g.HSide, g.hwH, out double tx, out double tz);
                var (jk2, _) = J.Find(JArc(g.SBp[si]));
                J.Dir(jk2, out double dx, out double dz);
                if (g.Dir < 0) { dx = -dx; dz = -dz; }   // away from the host
                double den = ex * dz - ez * dx;
                if (Math.Abs(den) < 1e-6) return (g.CX[si], g.CZ[si]);
                double qx = bpx - tx, qz = bpz - tz;
                double s = (qx * dz - qz * dx) / den, u = (qx * ez - qz * ex) / den;   // T + e s = Bp + d u
                double cx = tx + ex * s, cz = tz + ez * s;
                // in front of both: toward the mouth from the tangent point, toward the host from Bp
                double inX = g.CX[si] - tx, inZ = g.CZ[si] - tz;
                if (u > 0 || (cx - tx) * inX + (cz - tz) * inZ < 0) return (g.CX[si], g.CZ[si]);
                return (cx, cz);
            }
            void Outside(int si, ref double x, ref double z)
            {
                var (a, lat) = H.Project(x, z);
                double need = g.hwH + 0.003 - lat * g.HSide;
                if (need > 0)
                {
                    var (k, t) = H.Find(a);
                    H.Normal(k, t, out double nx, out double nz);
                    x += nx * g.HSide * need; z += nz * g.HSide * need;
                }
                var (aj, latJ) = J.Project(x, z);
                double needJ = g.hwJ + 0.003 - latJ * Na(si);
                if (needJ > 0)
                {
                    var (k, t) = J.Find(aj);
                    J.Normal(k, t, out double nx, out double nz);
                    x += nx * Na(si) * needJ; z += nz * Na(si) * needJ;
                }
            }
            // its height runs from the host's surface beside it to the joiner's, so it leaves each edge level with it
            int FilletV(int si, double x, double z, double tHost)
            {
                Outside(si, ref x, ref z);
                float h = HSurf(x, z), j = JSurf(x, z);
                return V(x, z, h + (j - h) * (float)(1 - tHost), liftH + (liftJ - liftH) * (float)(1 - tHost));
            }
            void WalkJ(List<int> sk, int si, double from, double to)   // along the joiner's edge, `from` excluded, `to` included
            {
                int steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(to - from) / 2.0));
                for (int q = 1; q <= steps; q++) { int id = JEdgeV(si, from + (to - from) * q / steps, liftJ); ring.Add(id); sk.Add(id); }
            }
            // the fillet at the high end: host edge (AHi) round to the joiner's edge, along it to the first piece
            {
                var sk = new List<int> { chain[chain.Count - 1] };
                double p0x = vx[sk[0]], p0z = vz[sk[0]];
                J.Edge(JArc(g.SBp[siHi]), Na(siHi), g.hwJ, out double bpx, out double bpz);
                var (ccx, ccz) = Control(siHi, g.AHi, bpx, bpz);
                for (int q = 1; q < 8; q++)
                {
                    double t = q / 8.0, a = (1 - t) * (1 - t), b = 2 * t * (1 - t), c = t * t;
                    int id = FilletV(siHi, a * p0x + b * ccx + c * bpx, a * p0z + b * ccz + c * bpz, 1 - t);
                    ring.Add(id); sk.Add(id);
                }
                int bp = JEdgeV(siHi, g.SBp[siHi], liftJ);
                ring.Add(bp); sk.Add(bp);
                WalkJ(sk, siHi, g.SBp[siHi], g.Trim);
                skirts.Add(sk);
            }
            // the first edge across, and back along the joiner's other edge and the fillet at the low end to the host (ALo)
            {
                var sk = new List<int>();
                int b0 = JEdgeV(siLo, g.Trim, liftJ);
                ring.Add(b0); sk.Add(b0);
                WalkJ(sk, siLo, g.Trim, g.SBp[siLo]);
                double bpx = vx[sk[sk.Count - 1]], bpz = vz[sk[sk.Count - 1]];
                double p2x = vx[chain[0]], p2z = vz[chain[0]];
                var (ccx, ccz) = Control(siLo, g.ALo, bpx, bpz);
                for (int q = 1; q < 8; q++)
                {
                    double t = q / 8.0, a = (1 - t) * (1 - t), b = 2 * t * (1 - t), c = t * t;
                    int id = FilletV(siLo, a * bpx + b * ccx + c * p2x, a * bpz + b * ccz + c * p2z, t);
                    ring.Add(id); sk.Add(id);
                }
                sk.Add(chain[0]);
                skirts.Add(sk);
            }
            // UV: plain asphalt (PatchU), along the joiner in metres from its first edge -- the joiner's marks stop there
            double sTrim = J.A[jk] + J.Len(jk) * jt;
            float VAt(double x, double z) => (float)(sTrim + (x - ajx) * tjx + (z - ajz) * tjz);
            var p = Finish(ring, skirts, vx, vz, vy, vl, -1, VAt);
            if (p == null) return null;
            p.Kind = (byte)g.JLine.Kind; p.Ramp = g.JLine.Ramp;
            H.Edge((g.ALo + g.AHi) * 0.5, g.HSide, g.hwH, out p.AnchorX, out p.AnchorZ);
            return p;
        }

        // ---- TAPER

        sealed class TaperGeo
        {
            public Rib R; public double Gore, A0, A1;
            public double[] WArc; public float[] W;
            public (double x, double z) Tangent;
            /// <summary>How much wider each side than its half-width the ramp's cut end is: squared to the carriageway, it
            /// spans the ramp's asphalt across that (Across), not along its own normal.</summary>
            public float Widen;
            /// <summary>The ramp's profile at its cut end: the carriageway's there, to the bit (+ the lift between them). Its
            /// own profile only follows the carriageway's between its dense points, which a highway's are not -- 4 mm off on
            /// a gentle crest, 37 mm where its climb had already begun inside the piece.</summary>
            public float H;
        }

        /// <summary>How far a ramp's asphalt reaches across a carriageway's normal from its centre at ramp arc `ar` (the
        /// carriageway at its arc `ah`): its half-width over the cosine of the angle between them. A ramp leaving at 10
        /// degrees is 7 cm wider than its half-width that way; taken as its half-width, the widened strip fell short of it
        /// and the first test found the ground showing along every gore.</summary>
        static double Across(Rib R, double ar, Rib H, double ah)
        {
            var (rk, _) = R.Find(ar); R.Dir(rk, out double rx, out double rz);
            var (hk, _) = H.Find(ah); H.Dir(hk, out double hx, out double hz);
            return RibbonHalfOf(RoadKind.Small) / Math.Max(0.5, Math.Abs(rx * hx + rz * hz));
        }

        /// <summary>The gore of a ramp on a carriageway: the arc from its joined end where its inner edge leaves the
        /// carriageway's outer edge (its centre HighwayLaneHalf + its reach across, Across, out from the carriageway's), or -1.</summary>
        static double GoreOf(Rib R, bool atEnd, Rib H, int outer)
        {
            double lane = HighwayLaneHalf, total = R.Total;
            double Clear(double s)
            {
                double ar = atEnd ? total - s : s;
                R.PointAt(ar, out double x, out double z);
                var (ah, lat) = H.Project(x, z);
                return lat * outer - Across(R, ar, H, ah) - lane;
            }
            double lo = 0, hi = -1;
            for (double s = 1; s <= Math.Min(260, total); s += 1) if (Clear(s) >= 0) { hi = s; break; }
            if (hi < 0) return -1;
            lo = hi - 1;
            for (int it = 0; it < 50 && hi - lo > 1e-7; it++) { double m = (lo + hi) * 0.5; if (Clear(m) >= 0) hi = m; else lo = m; }
            return hi;
        }

        /// <summary>Where a ramp's ribbon starts out of its carriageway (arc from its joined end): the gore, moved on as
        /// below. ONE function for the profile (FollowHosts) and the junction (TaperShape): the ramp runs at the
        /// carriageway's surface exactly as far as its slab is cut back, or it meets the widened strip with a step.</summary>
        static double GoreAt(Rib R, bool atEnd, Rib H, int cw)
        {
            double gore = GoreOf(R, atEnd, H, cw);
            if (gore < 0) return -1;
            double total = R.Total;
            // THE FIRST PIECE MUST NOT FOLD. The cut end is squared to the carriageway, the far end of the piece to the ramp:
            // shorter than its width times the tangent of the angle between them, and the two ends cross (a ramp leaving at
            // 18 degrees, cut a metre short of a profile point, folded its first piece back over the carriageway). So the
            // gore moves on to the next profile point while the first piece -- at the finest LOD's 4 m -- would be shorter
            for (int guard = 0; guard < 8; guard++)
            {
                double ar = atEnd ? total - gore : gore;
                R.PointAt(ar, out double x0, out double z0);
                var (ah, _) = H.Project(x0, z0);
                var (rk0, rt0) = R.Find(ar); R.Dir(rk0, out double rx0, out double rz0);
                var (hk0, _) = H.Find(ah); H.Dir(hk0, out double hx0, out double hz0);
                double cos = Math.Abs(rx0 * hx0 + rz0 * hz0);
                if (cos < Math.Cos(35 * Math.PI / 180)) return -1;   // leaving too steeply for a taper
                double need = Across(R, ar, H, ah) * Math.Sqrt(1 - cos * cos) / cos + 0.3;
                // to the next boundary on the drawn side (away from the joined end)
                double next = atEnd ? (rt0 > 0 ? R.A[rk0] : rk0 > 0 ? R.A[rk0 - 1] : 0) : (rt0 < 1 ? R.A[rk0 + 1] : rk0 + 2 <= R.N ? R.A[rk0 + 2] : total);
                double rem = Math.Abs(next - ar), first = rem / Math.Max(1, Math.Ceiling(rem / 4.0));
                if (first >= need || rem < 1e-9) break;
                gore = atEnd ? total - next : next;
            }
            return gore;
        }

        TaperGeo TaperShape(Line r, bool atEnd, Line host, int cw, List<Underpass> ups)
        {
            var R = new Rib(r, 0); var H = new Rib(host, cw * HighwayRibbonOffset);
            double gore = GoreAt(R, atEnd, H, cw);
            if (gore < 0 || !TaperClear(host, cw, H, R, atEnd, gore, ups)) return null;
            double hwR = RibbonHalfOf(RoadKind.Small), lane = HighwayLaneHalf, total = R.Total;
            var pts = new List<(double a, float w)>();
            double gAcross = hwR;
            for (double s = 0; ; s += 2)
            {
                bool last = s >= gore;
                double ss = last ? gore : s, ar = atEnd ? total - ss : ss;
                R.PointAt(ar, out double x, out double z);
                var (a, lat) = H.Project(x, z);
                double across = Across(R, ar, H, a);
                if (last) gAcross = across;
                pts.Add((a, (float)Math.Max(0, lat * cw + across - lane)));
                if (last) break;
            }
            pts.Sort((p, q) => p.a.CompareTo(q.a));
            var g = new TaperGeo { R = R, Gore = gore, A0 = pts[0].a, A1 = pts[pts.Count - 1].a, WArc = new double[pts.Count], W = new float[pts.Count] };
            for (int i = 0; i < pts.Count; i++) { g.WArc[i] = pts[i].a; g.W[i] = pts[i].w; }
            // the ramp's cut end is squared to the carriageway there, pointing the ramp's way
            R.PointAt(atEnd ? total - gore : gore, out double gx, out double gz);
            var (ga, _) = H.Project(gx, gz);
            var (hk, ht) = H.Find(ga);
            H.Normal(hk, ht, out double hnx, out double hnz);
            double tx = hnz, tz = -hnx;
            var (rk, _) = R.Find(atEnd ? total - gore : gore);
            R.Dir(rk, out double rx, out double rz);
            if (tx * rx + tz * rz < 0) { tx = -tx; tz = -tz; }
            g.Tangent = (tx, tz);
            g.Widen = (float)(gAcross - hwR);
            g.H = H.H(hk, ht) + (Lift(RoadKind.Highway) - Lift(RoadKind.Small));
            return g;
        }

        /// <summary>A taper's paint: the carriageway's edge, where it has widened by a lane's worth, as the short dashes
        /// of a merge/diverge line, up to the gore.</summary>
        static void PaintTaper(JunctionPlan plan, TaperGeo g, Line host, int cw)
        {
            var H = new Rib(host, cw * HighwayRibbonOffset);
            double lo = double.MaxValue, hi = double.MinValue;
            for (int i = 0; i < g.WArc.Length; i++) if (g.W[i] >= 1.5f) { lo = Math.Min(lo, g.WArc[i]); hi = Math.Max(hi, g.WArc[i]); }
            if (hi - lo < 4) return;
            var (k, t) = H.Find((g.A0 + g.A1) * 0.5);
            H.Point(k, t, out double ax, out double az);
            var m = new RoadMark { Colour = 0, AnchorX = ax, AnchorZ = az };
            // (dashes 2 m in every 4: the short, close dashes of a lane that leaves or joins, not a lane line's 4-in-8)
            double phase = Math.Floor(lo / 4.0) * 4.0;
            for (double c = phase; c < hi; c += 4.0)
            {
                double s0 = Math.Max(lo, c), s1 = Math.Min(hi, c + 2.0);
                if (s1 - s0 > 0.1) Stripe(m, H, s0, s1, cw * HighwayLaneHalf, 0.3, false, RoadKind.Highway);
            }
            if (m.Quads > 0) plan.Marks.Add(m);
        }

        /// <summary>A ramp's profile, given its joined ends: at each taper it runs at the carriageway's surface until its gore
        /// (so the carriageway's widened strip and the ramp's first edge meet flush), and climbs between them -- eased off
        /// the gores (RampEase) -- to the other end's. False if the climb left is too steep for a small road.</summary>
        /// <returns>null, or why the ramp cannot join that way.</returns>
        string FollowHosts(Line ramp, Line startHost, int startCw, Line endHost, int endCw, List<Underpass> upsStart, List<Underpass> upsEnd)
        {
            var R = new Rib(ramp, 0);
            double total = R.Total;
            float dL = Lift(RoadKind.Highway) - Lift(RoadKind.Small);
            var arc = R.A;
            double s0 = 0, s1 = total;
            float h0 = ramp.H[0], h1 = ramp.H[ramp.H.Length - 1];
            if (startHost != null)
            {
                var H = new Rib(startHost, startCw * HighwayRibbonOffset);
                double gs = GoreAt(R, false, H, startCw);
                if (gs < 0) return "no gore on its first carriageway";
                if (TaperBlock(startHost, startCw, H, R, false, gs, upsStart) is string b0) return "its first taper runs onto " + b0;
                s0 = gs;
                R.PointAt(gs, out double x, out double z);
                h0 = ProfileNear(startHost, x, z) + dL;
            }
            if (endHost != null)
            {
                var H = new Rib(endHost, endCw * HighwayRibbonOffset);
                double ge = GoreAt(R, true, H, endCw);
                if (ge < 0) return "no gore on its last carriageway";
                if (TaperBlock(endHost, endCw, H, R, true, ge, upsEnd) is string b1) return "its last taper runs onto " + b1;
                s1 = total - ge;
                R.PointAt(s1, out double x, out double z);
                h1 = ProfileNear(endHost, x, z) + dL;
            }
            if (s1 - s0 < 30) return "its gores overlap";
            for (int i = 0; i < ramp.H.Length; i++)
            {
                double a = arc[i];
                if (startHost != null && a <= s0) ramp.H[i] = ProfileNear(startHost, ramp.X[i], ramp.Z[i]) + dL;
                else if (endHost != null && a >= s1) ramp.H[i] = ProfileNear(endHost, ramp.X[i], ramp.Z[i]) + dL;
                else ramp.H[i] = h0 + (h1 - h0) * RampEase((float)Math.Clamp((a - s0) / (s1 - s0), 0, 1));
            }
            for (int i = 0; i + 1 < ramp.H.Length; i++)
                if (Math.Abs(ramp.H[i + 1] - ramp.H[i]) / Math.Max(1e-9, arc[i + 1] - arc[i]) > MaxGrade(RoadKind.Small) * 1.0001f) return "too steep between its gores";
            return null;
        }

        /// <summary>Is a carriageway its own slab, on the ground, all along a taper (and its deck mouths' widening past)? Not on a deck -- its own
        /// bridge, or one it lays over a road under it (an Underpass's Cover) -- where the slab it would widen is not drawn
        /// (the first full window found a ramp branching off in mid-air at a bridge's mouth), and not near a tunnel.</summary>
        static bool TaperClear(Line host, int cw, Rib H, Rib R, bool atEnd, double gore, List<Underpass> ups) => TaperBlock(host, cw, H, R, atEnd, gore, ups) == null;

        /// <summary>TaperClear's reason, or null.</summary>
        static string TaperBlock(Line host, int cw, Rib H, Rib R, bool atEnd, double gore, List<Underpass> ups)
        {
            double total = R.Total;
            R.PointAt(atEnd ? total : 0, out double x0, out double z0);
            R.PointAt(atEnd ? total - gore : gore, out double x1, out double z1);
            double a0 = H.Project(x0, z0).a, a1 = H.Project(x1, z1).a;   // the ramp's joined end, its gore
            // past the GORE by a deck mouth's widening too: the carriageway is already wider there, and the ramp's first
            // piece, leaving its edge, overlapped it. Behind the ramp's end the ramp is not drawn: a few metres do
            double back = 15, ahead = MouthTaper + 10;
            double lo = Math.Max(0, a1 >= a0 ? a0 - back : a1 - ahead), hi = Math.Min(H.Total, a1 >= a0 ? a1 + ahead : a0 + back);
            var (k0, t0) = H.Find(lo); var (k1, t1) = H.Find(hi);
            double f0 = k0 + t0, f1 = k1 + t1;
            int side = cw < 0 ? 0 : 1;
            if (Bridges)
            {
                if (host.DeckCover != null) foreach (var c in host.DeckCover[side]) if (c.F1 >= f0 && c.F0 <= f1) return "its own bridge";
                if (ups != null) foreach (var u in ups) if (u.HwId == host.Id && u.Cover[side] is DeckSpan c && c.F1 >= f0 && c.F0 <= f1) return "a deck over a crossing";
            }
            if (Tunnels && host.TunnelSpans != null && host.HArc != null)
            {
                double Route(double f) { int k = Math.Clamp((int)f, 0, host.HArc.Length - 2); return host.HArc[k] + (host.HArc[k + 1] - host.HArc[k]) * (f - k); }
                double r0 = Route(f0), r1 = Route(f1);
                foreach (var tn in host.TunnelSpans) if (r1 > tn.A0 - TunnelForecourt - 30 && r0 < tn.A1 + TunnelForecourt + 30) return "a tunnel";
            }
            return null;
        }

        // ---- NODE (a town)

        /// <summary>One road leaving a node: a line's ribbon from arc A0 on, the way Dir (+1 toward its end, -1 toward its
        /// start). A town's mains each give one, from the end they meet at; two roads CROSSING give two each, from the
        /// crossing both ways.</summary>
        sealed class Arm
        {
            public Line E; public Rib R; public double A0; public int Dir;
            public double Arc(double t) => A0 + Dir * t;
            public int Side(int ccw) => ccw * Dir;   // the counter-clockwise side, on the ribbon's own normal
            public double Room => Dir > 0 ? R.Total - A0 : A0;
            /// <summary>A caller-supplied half width, for arms that are not InfiniteRoads' own. ⚠ The town grid
            /// sizes its streets off RoadField MATERIALS (Highway_0 is 9.2 m), not off RoadKind, and a node
            /// built to the wrong width leaves its patch standing proud of the asphalt it is supposed to meet.</summary>
            public double HwOver;
            public double Hw => HwOver > 0 ? HwOver : RibbonHalfOf(E.Kind);
            public bool IsEnd => Dir > 0 ? A0 <= 1e-9 : A0 >= R.Total - 1e-9;
        }

        sealed class NodeGeo
        {
            public double NX, NZ;
            public List<Arm> Arms = new();
            public List<double> Trim = new();
            public List<(double x, double z)> D = new();   // each arm's heading away from the node (its chord to 20 m out)
            /// <summary>Per adjacent pair (i, i+1 counter-clockwise): a fillet -- its control point and its tangent points'
            /// distances out along each arm -- or, past a straight angle between them, none (the outside of the bend).</summary>
            public List<(bool fillet, double cx, double cz, double ti, double tj)> Pair = new();
            /// <summary>How far out each arm runs before it meets its neighbours' asphalt: where its paint stops.</summary>
            public List<double> Inner = new();
        }

        /// <summary>The fillet between two arms: the smaller road's (a town's mains 12 m).</summary>
        static double NodeFilletOf(Arm a, Arm b)
        {
            static double R(Line e) => e.Ramp ? 8.0 : e.Kind == RoadKind.Main ? NodeFillet : e.Kind == RoadKind.Small ? 8.0 : 5.0;
            return Math.Min(R(a.E), R(b.E));
        }

        // =============================================================================================
        // PUBLIC FACADE: the node geometry, for a caller whose roads are NOT InfiniteRoads' own.
        //
        // strawberry 2026-10-11, on the town grid's overlapping junctions: "use tiny's road-merging mechanic
        // instead of overlapping roads". The machinery below already does exactly that -- arms cut back past
        // their fillets and one fanned patch paving the middle -- but it is keyed to Line/RoadKind/Rib, none
        // of which a RoadField-side generator has. This hands it plain polylines instead and gives back the
        // two things such a generator needs: how far to cut each arm, and the patch to lay between them.
        // ⚠ FRAME: X/Z here are InfiniteRoads' own, where z is the NEGATED world z. Passing Godot world
        // coordinates straight in mirrors the node, and a mirror swaps which side is counter-clockwise --
        // which is the ordering every fillet below is built on.
        public readonly struct TownArm
        {
            public readonly double[] X, Z; public readonly float[] H;
            /// <summary>Is the node at this polyline's END (otherwise its start)?</summary>
            public readonly bool AtEnd;
            public readonly double HalfWidth;
            public TownArm(double[] x, double[] z, float[] h, bool atEnd, double halfWidth)
            { X = x; Z = z; H = h; AtEnd = atEnd; HalfWidth = halfWidth; }
        }

        /// <summary>The node where these arms meet: how far back each one is cut (same order as `arms`), and
        /// the patch that paves the middle. False when the shape cannot be made -- too few arms, one too short
        /// to cut, or a geometry the fillets cannot close -- in which case the caller keeps whatever it had.</summary>
        public static bool TownNode(IReadOnlyList<TownArm> arms, double nx, double nz,
                                    out double[] trims, out RoadPatch patch)
        {
            trims = null; patch = null;
            if (arms == null || arms.Count < 2) return false;
            var list = new List<Arm>(arms.Count);
            foreach (var a in arms)
            {
                if (a.X == null || a.Z == null || a.H == null || a.X.Length < 2
                    || a.Z.Length != a.X.Length || a.H.Length != a.X.Length || a.HalfWidth <= 0) return false;
                var e = new Line { Kind = RoadKind.Main, X = a.X, Z = a.Z, H = a.H };
                var r = new Rib(e, 0);
                list.Add(new Arm { E = e, R = r, A0 = a.AtEnd ? r.Total : 0, Dir = a.AtEnd ? -1 : 1, HwOver = a.HalfWidth });
            }
            if (NodeShape(list, nx, nz) is not NodeGeo g) return false;
            if (NodePatch(g) is not RoadPatch p) return false;
            trims = g.Trim.ToArray();
            patch = p;
            return true;
        }

        static NodeGeo NodeShape(List<Arm> arms, double nx, double nz)
        {
            var g = new NodeGeo { NX = nx, NZ = nz };
            var items = new List<(double ang, Arm arm, double dx, double dz)>();
            foreach (var arm in arms)
            {
                if (arm.Room < 40) return null;
                arm.R.PointAt(arm.Arc(20), out double x, out double z);
                double dx = x - g.NX, dz = z - g.NZ, l = Math.Sqrt(dx * dx + dz * dz);
                if (l < 5) return null;
                items.Add((Math.Atan2(dz, dx), arm, dx / l, dz / l));
            }
            items.Sort((p, q) => p.ang.CompareTo(q.ang));
            int n = items.Count;
            var need = new double[n]; var inner = new double[n];
            for (int i = 0; i < n; i++) need[i] = items[i].arm.Hw + 4;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                var a = items[i]; var b = items[j];
                double hi = a.arm.Hw, hj = b.arm.Hw;
                double beta = b.ang - a.ang; if (beta <= 0) beta += 2 * Math.PI;
                if (beta >= Math.PI - 0.05) { g.Pair.Add((false, 0, 0, 0, 0)); continue; }
                // first on the chords: arm i's edge toward j (N + d_i t + n_i hi) meets arm j's edge toward i (N + d_j u - n_j hj)
                double nix = -a.dz, niz = a.dx, njx = -b.dz, njz = b.dx;
                double px = -njx * hj - nix * hi, pz = -njz * hj - niz * hi;
                double cr = a.dx * b.dz - a.dz * b.dx;
                if (Math.Abs(cr) < 1e-6) return null;
                double t = (px * b.dz - pz * b.dx) / cr;
                double c0x = g.NX + a.dx * t + nix * hi, c0z = g.NZ + a.dz * t + niz * hi;
                inner[i] = Math.Max(inner[i], (c0x - g.NX) * a.dx + (c0z - g.NZ) * a.dz);
                inner[j] = Math.Max(inner[j], (c0x - g.NX) * b.dx + (c0z - g.NZ) * b.dz);
                double L = Math.Clamp(NodeFilletOf(a.arm, b.arm) / Math.Tan(beta * 0.5), 2.0, 45.0);
                double ti = (c0x - g.NX) * a.dx + (c0z - g.NZ) * a.dz + L, tj = (c0x - g.NX) * b.dx + (c0z - g.NZ) * b.dz + L;
                if (ti > 70 || tj > 70) return null;
                // a wide road and a narrow one at an obtuse angle: their edges meet BEHIND the node along the narrow one --
                // nothing to round there; a straight chamfer joins the edges a couple of metres out
                bool chamfer = ti < 2 || tj < 2;
                if (chamfer) { ti = Math.Max(ti, 2); tj = Math.Max(tj, 2); }
                // ...then on the ribbons as built: the tangent points ON each edge, and the control point where the edges'
                // own tangents there meet, so the fillet leaves each edge along it (a main still curving 60 m out is not
                // its chord)
                a.arm.R.Edge(a.arm.Arc(ti), a.arm.Side(+1), hi, out double tix, out double tiz);
                b.arm.R.Edge(b.arm.Arc(tj), b.arm.Side(-1), hj, out double tjx, out double tjz);
                var (ka, _) = a.arm.R.Find(a.arm.Arc(ti)); a.arm.R.Dir(ka, out double eix, out double eiz);
                var (kb, _) = b.arm.R.Find(b.arm.Arc(tj)); b.arm.R.Dir(kb, out double ejx, out double ejz);
                eix *= a.arm.Dir; eiz *= a.arm.Dir; ejx *= b.arm.Dir; ejz *= b.arm.Dir;   // both now pointing away from the node
                double cx = chamfer ? (tix + tjx) * 0.5 : c0x, cz = chamfer ? (tiz + tjz) * 0.5 : c0z;
                double den = eix * ejz - eiz * ejx;
                if (!chamfer && Math.Abs(den) > 1e-6)
                {
                    double qx = tjx - tix, qz = tjz - tiz;
                    double si = -(qx * ejz - qz * ejx) / den, sj = -(qx * eiz - qz * eix) / den;   // C = T_i - e_i si = T_j - e_j sj
                    if (si > 0 && sj > 0 && si < 3 * L + 5 && sj < 3 * L + 5) { cx = tix - eix * si; cz = tiz - eiz * si; }
                }
                g.Pair.Add((true, cx, cz, ti, tj));
                need[i] = Math.Max(need[i], ti + 1.0); need[j] = Math.Max(need[j], tj + 1.0);
            }
            foreach (var it in items) { g.Arms.Add(it.arm); g.D.Add((it.dx, it.dz)); }
            for (int i = 0; i < n; i++) g.Inner.Add(inner[i]);
            for (int i = 0; i < n; i++)
            {
                var arm = g.Arms[i];
                double cut = arm.R.Snap(arm.Arc(need[i]), arm.Hw, arm.Dir);   // onto a profile point too near it, on the side kept
                double tr = (cut - arm.A0) * arm.Dir;
                if (tr > arm.Room - 20) return null;
                g.Trim.Add(tr);
            }
            return g;
        }

        /// <summary>A node's paint. Each arm's lines run on in from its cut to where it meets its neighbours, and a stop
        /// line crosses its approach there. A town of three or more arms is an all-way stop; two arms only (a town on a
        /// bend) are one road, painted through.</summary>
        static void PaintNode(JunctionPlan plan, NodeGeo g, RoadPatch p)
        {
            int n = g.Arms.Count;
            var white = new RoadMark { Colour = 0, AnchorX = p.AnchorX, AnchorZ = p.AnchorZ, On = p };
            var yellow = new RoadMark { Colour = 1, AnchorX = p.AnchorX, AnchorZ = p.AnchorZ, On = p };
            for (int i = 0; i < n; i++)
            {
                var arm = g.Arms[i];
                if (arm.E.Kind == RoadKind.Trail) continue;
                bool through = n == 2;
                double stop = Math.Min(g.Inner[i] + 1.5, g.Trim[i] - StopWidth);
                double from = through ? 0 : stop + StopWidth * 0.5;
                if (from < g.Trim[i])
                    foreach (var (lat, width, colour, dashed) in LinesOf(arm.E))
                        Stripe(colour == 1 ? yellow : white, arm.R, arm.Arc(from), arm.Arc(g.Trim[i]), lat, width, dashed, arm.E.Kind);
                // the approach is on the right driving in: the arm's clockwise side
                if (!through && stop > 0) StopLine(white, arm.R, arm.Arc(stop), arm.Side(-1), arm.Hw, arm.E.Kind);
            }
            if (white.Quads > 0) plan.Marks.Add(white);
            if (yellow.Quads > 0) plan.Marks.Add(yellow);
        }
        static int Rank(Line e) => e.Kind == RoadKind.Main ? 2 : e.Kind == RoadKind.Small ? 1 : 0;

        static RoadPatch NodePatch(NodeGeo g)
        {
            int n = g.Arms.Count;
            var vx = new List<double>(); var vz = new List<double>(); var vy = new List<float>(); var vl = new List<float>();
            int V(double x, double z, float y, float lift) { vx.Add(x); vz.Add(z); vy.Add(y); vl.Add(lift); return vx.Count - 1; }
            // a point of arm i's top edge, `t` out from the node on side `ccw`: where its pieces' corners are
            int EdgeV(int i, double t, int ccw)
            {
                var arm = g.Arms[i];
                var (k, u) = arm.R.Find(arm.Arc(t));
                arm.R.Point(k, u, out double x, out double z);
                arm.R.Normal(k, u, out double nx, out double nz);
                int side = arm.Side(ccw);
                return V(x + nx * side * arm.Hw, z + nz * side * arm.Hw, SurfaceY(arm.E.Kind, arm.R.H(k, u)), Lift(arm.E.Kind));
            }
            float SurfAt(int i, double t) { var arm = g.Arms[i]; return SurfaceY(arm.E.Kind, arm.R.HAt(arm.Arc(t))); }
            // down an edge from `from` to `to` (both distances out from the node), every 2 m, the end excluded
            void Walk(List<int> ring, List<int> sk, int i, int ccw, double from, double to)
            {
                int steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(to - from) / 2.0));
                for (int q = 1; q < steps; q++) { int id = EdgeV(i, from + (to - from) * q / steps, ccw); ring.Add(id); sk.Add(id); }
            }
            var cw = new int[n]; var ccwc = new int[n];
            for (int i = 0; i < n; i++) { cw[i] = EdgeV(i, g.Trim[i], -1); ccwc[i] = EdgeV(i, g.Trim[i], +1); }
            var ring = new List<int>(); var skirts = new List<List<int>>();
            float centreY = 0f, centreLift = 0f;
            for (int i = 0; i < n; i++) { centreY += SurfAt(i, 0); centreLift = Math.Max(centreLift, Lift(g.Arms[i].E.Kind)); }
            centreY /= n;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                // across the arm's cut end (its first piece's row: straight, at one height), every 2 m
                ring.Add(cw[i]);
                {
                    int steps = Math.Max(1, (int)Math.Ceiling(2 * g.Arms[i].Hw / 2.0));
                    for (int q = 1; q < steps; q++)
                    {
                        double t = (double)q / steps;
                        ring.Add(V(vx[cw[i]] + (vx[ccwc[i]] - vx[cw[i]]) * t, vz[cw[i]] + (vz[ccwc[i]] - vz[cw[i]]) * t, vy[cw[i]] + (vy[ccwc[i]] - vy[cw[i]]) * (float)t, vl[cw[i]]));
                    }
                }
                ring.Add(ccwc[i]);
                var sk = new List<int> { ccwc[i] };
                var pr = g.Pair[i];
                if (pr.fillet)
                {
                    // in along arm i's edge to its tangent point, the fillet round to arm j's, out along j's edge
                    Walk(ring, sk, i, +1, g.Trim[i], pr.ti);
                    int ta = EdgeV(i, pr.ti, +1), tb = EdgeV(j, pr.tj, -1);
                    ring.Add(ta); sk.Add(ta);
                    double tix = vx[ta], tiz = vz[ta], tjx = vx[tb], tjz = vz[tb];
                    float yi = vy[ta], yj = vy[tb], li = vl[ta], lj = vl[tb];
                    for (int q = 1; q < 12; q++)
                    {
                        double t = q / 12.0, A = (1 - t) * (1 - t), B = 2 * t * (1 - t), C = t * t;
                        int id = V(A * tix + B * pr.cx + C * tjx, A * tiz + B * pr.cz + C * tjz, yi + (yj - yi) * (float)t, li + (lj - li) * (float)t);
                        ring.Add(id); sk.Add(id);
                    }
                    ring.Add(tb); sk.Add(tb);
                    Walk(ring, sk, j, -1, pr.tj, g.Trim[j]);
                }
                else
                {
                    // the outside of a bend (or straight on): arm i's edge in to the node, round it at the half-width from
                    // its foot to arm j's, out along j's edge. Straight on, the two feet are one point
                    Walk(ring, sk, i, +1, g.Trim[i], 0);
                    int fa = EdgeV(i, 0, +1), fb = EdgeV(j, 0, -1);
                    double a0 = Math.Atan2(vz[fa] - g.NZ, vx[fa] - g.NX), a1 = Math.Atan2(vz[fb] - g.NZ, vx[fb] - g.NX);
                    double sweep = a1 - a0;
                    while (sweep <= -Math.PI) sweep += 2 * Math.PI;
                    while (sweep > Math.PI) sweep -= 2 * Math.PI;
                    if (Math.Abs(sweep) < 0.06)
                    {
                        int id = V((vx[fa] + vx[fb]) * 0.5, (vz[fa] + vz[fb]) * 0.5, (vy[fa] + vy[fb]) * 0.5f, Math.Max(vl[fa], vl[fb]));
                        ring.Add(id); sk.Add(id);
                    }
                    else
                    {
                        if (sweep < 0) return null;   // edges crossing in front without a fillet: not past pi - 0.05
                        ring.Add(fa); sk.Add(fa);
                        double r0 = Math.Sqrt((vx[fa] - g.NX) * (vx[fa] - g.NX) + (vz[fa] - g.NZ) * (vz[fa] - g.NZ)), r1 = Math.Sqrt((vx[fb] - g.NX) * (vx[fb] - g.NX) + (vz[fb] - g.NZ) * (vz[fb] - g.NZ));
                        int steps = Math.Max(2, (int)Math.Ceiling(sweep / 0.2));
                        for (int q = 1; q < steps; q++)
                        {
                            double t = (double)q / steps, ang = a0 + sweep * t, rr = r0 + (r1 - r0) * t;
                            int id = V(g.NX + Math.Cos(ang) * rr, g.NZ + Math.Sin(ang) * rr, vy[fa] + (vy[fb] - vy[fa]) * (float)t, vl[fa] + (vl[fb] - vl[fa]) * (float)t);
                            ring.Add(id); sk.Add(id);
                        }
                        ring.Add(fb); sk.Add(fb);
                    }
                    Walk(ring, sk, j, -1, 0, g.Trim[j]);
                }
                sk.Add(cw[j]);
                skirts.Add(sk);
            }
            int centre = V(g.NX, g.NZ, centreY, centreLift);
            // paved as the biggest road through it: a main's asphalt over a trail's crossing, never the trail's dirt
            var kind = RoadKind.Trail;
            foreach (var arm in g.Arms) if (arm.E.Kind == RoadKind.Main || (arm.E.Kind == RoadKind.Small && kind == RoadKind.Trail)) kind = arm.E.Kind;
            var p = Finish(ring, skirts, vx, vz, vy, vl, centre, (x, z) => (float)(x * 0.6 + z * 0.8));
            if (p == null) return null;
            p.Kind = (byte)kind; p.AnchorX = g.NX; p.AnchorZ = g.NZ;
            return p;
        }

        // ---- the patch itself

        /// <summary>Triangulate a ring and work out each skirt's outward normals; null if the ring is not a simple polygon
        /// (a junction too tight to pave -- its roads then keep their own ends). With a `centre` vertex the ring is fanned
        /// from it where every fan triangle turns the ring's way (a node: the middle then has a vertex of its own to be
        /// lifted onto the ground), else ear-clipped.</summary>
        static RoadPatch Finish(List<int> ring, List<List<int>> skirts, List<double> vx, List<double> vz, List<float> vy, List<float> vl, int centre, Func<double, double, float> vAt)
        {
            // drop repeated vertices along the ring
            var r = new List<int>();
            foreach (int i in ring)
                if (r.Count == 0 || Math.Abs(vx[i] - vx[r[r.Count - 1]]) + Math.Abs(vz[i] - vz[r[r.Count - 1]]) > 1e-4) r.Add(i);
            while (r.Count > 2 && Math.Abs(vx[r[0]] - vx[r[r.Count - 1]]) + Math.Abs(vz[r[0]] - vz[r[r.Count - 1]]) <= 1e-4) r.RemoveAt(r.Count - 1);
            int n = r.Count;
            if (n < 3) return null;
            double area = 0;
            for (int i = 0; i < n; i++) { int a = r[i], b = r[(i + 1) % n]; area += vx[a] * vz[b] - vx[b] * vz[a]; }
            if (Math.Abs(area) < 1) return null;
            double orient = Math.Sign(area);
            // simple? no two non-adjacent edges cross
            for (int i = 0; i < n; i++)
                for (int k = i + 2; k < n; k++)
                {
                    if (i == 0 && k == n - 1) continue;
                    int a = r[i], b = r[(i + 1) % n], c = r[k], d = r[(k + 1) % n];
                    if (SegCross(vx[a], vz[a], vx[b], vz[b], vx[c], vz[c], vx[d], vz[d], out double ta, out double tb) && ta > 1e-6 && ta < 1 - 1e-6 && tb > 1e-6 && tb < 1 - 1e-6) return null;
                }
            var tris = new List<int>();
            var idx = new List<int>(r);
            int interior = int.MaxValue;
            double Cross(int a, int b, int c) => ((vx[b] - vx[a]) * (vz[c] - vz[a]) - (vz[b] - vz[a]) * (vx[c] - vx[a])) * orient;
            if (centre >= 0)
            {
                bool star = true;
                for (int i = 0; i < n && star; i++) if (Cross(centre, r[i], r[(i + 1) % n]) <= 1e-9) star = false;
                if (star)
                {
                    // in RINGS: the edge drawn in toward the middle every PatchRing, so the ground under a town's
                    // patch -- 60 m across, its fan's triangles that long -- has vertices to lift it by (the first one
                    // drawn let the verge show through in wedges)
                    // (and along the edge no further apart than that, or the outer rings' triangles are the edge's length wide)
                    {
                        var fine = new List<int>();
                        for (int i = 0; i < n; i++)
                        {
                            int a = r[i], b = r[(i + 1) % n];
                            fine.Add(a);
                            double len = Math.Sqrt((vx[b] - vx[a]) * (vx[b] - vx[a]) + (vz[b] - vz[a]) * (vz[b] - vz[a]));
                            int steps = (int)Math.Ceiling(len / PatchRing);
                            for (int q = 1; q < steps; q++)
                            {
                                double t = (double)q / steps;
                                vx.Add(vx[a] + (vx[b] - vx[a]) * t); vz.Add(vz[a] + (vz[b] - vz[a]) * t);
                                vy.Add(vy[a] + (vy[b] - vy[a]) * (float)t); vl.Add(vl[a] + (vl[b] - vl[a]) * (float)t);
                                fine.Add(vx.Count - 1);
                            }
                        }
                        r = fine; n = r.Count;
                    }
                    double far = 0;
                    foreach (int i in r) far = Math.Max(far, Math.Sqrt((vx[i] - vx[centre]) * (vx[i] - vx[centre]) + (vz[i] - vz[centre]) * (vz[i] - vz[centre])));
                    int K = Math.Max(1, (int)Math.Ceiling(far / PatchRing));
                    interior = vx.Count;
                    var rings = new int[K + 1][];
                    rings[K] = r.ToArray();
                    for (int k = 1; k < K; k++)
                    {
                        double f = (double)k / K;
                        rings[k] = new int[n];
                        for (int i = 0; i < n; i++)
                        {
                            int b = r[i];
                            vx.Add(vx[centre] + (vx[b] - vx[centre]) * f); vz.Add(vz[centre] + (vz[b] - vz[centre]) * f);
                            vy.Add(vy[centre] + (vy[b] - vy[centre]) * (float)f); vl.Add(vl[centre] + (vl[b] - vl[centre]) * (float)f);
                            rings[k][i] = vx.Count - 1;
                        }
                    }
                    int first = K > 1 ? 1 : K;
                    for (int i = 0; i < n; i++) { tris.Add(centre); tris.Add(rings[first][i]); tris.Add(rings[first][(i + 1) % n]); }
                    for (int k = first; k < K; k++)
                        for (int i = 0; i < n; i++)
                        {
                            int a = rings[k][i], b = rings[k][(i + 1) % n], c = rings[k + 1][i], d = rings[k + 1][(i + 1) % n];
                            tris.Add(a); tris.Add(c); tris.Add(d);
                            tris.Add(a); tris.Add(d); tris.Add(b);
                        }
                    idx.Clear();
                }
            }
            bool Inside(int p, int a, int b, int c) => Cross(a, b, p) > 1e-12 && Cross(b, c, p) > 1e-12 && Cross(c, a, p) > 1e-12;
            int guard = 0;
            while (idx.Count > 3 && guard++ < 10000)
            {
                bool cut = false;
                for (int i = 0; i < idx.Count; i++)
                {
                    int a = idx[(i + idx.Count - 1) % idx.Count], b = idx[i], c = idx[(i + 1) % idx.Count];
                    if (Cross(a, b, c) <= 1e-12) continue;   // reflex (or flat)
                    bool ear = true;
                    foreach (int p in idx) if (p != a && p != b && p != c && Inside(p, a, b, c)) { ear = false; break; }
                    if (!ear) continue;
                    tris.Add(a); tris.Add(b); tris.Add(c);
                    idx.RemoveAt(i);
                    cut = true;
                    break;
                }
                if (!cut)
                {
                    // only flat corners left: clip the flattest anyway
                    tris.Add(idx[idx.Count - 1]); tris.Add(idx[0]); tris.Add(idx[1]);
                    idx.RemoveAt(0);
                }
            }
            if (idx.Count == 3) { tris.Add(idx[0]); tris.Add(idx[1]); tris.Add(idx[2]); }
            if (tris.Count == 0) return null;
            // skirts: outward = to the right of the ring's travel for a counter-clockwise ring (orient > 0), left otherwise
            var outSk = new int[skirts.Count][]; var snx = new float[skirts.Count][]; var snz = new float[skirts.Count][];
            for (int s = 0; s < skirts.Count; s++)
            {
                var sk = skirts[s];
                outSk[s] = sk.ToArray(); snx[s] = new float[sk.Count]; snz[s] = new float[sk.Count];
                for (int q = 0; q < sk.Count; q++)
                {
                    int a = sk[Math.Max(0, q - 1)], b = sk[Math.Min(sk.Count - 1, q + 1)];
                    double tx = vx[b] - vx[a], tz = vz[b] - vz[a], tl = Math.Sqrt(tx * tx + tz * tz);
                    if (tl < 1e-9) { snx[s][q] = 0; snz[s][q] = 0; continue; }
                    // the ring runs one way round; a skirt is listed along it, so its right (CCW ring) is outward
                    double ox = tz / tl * orient, oz = -tx / tl * orient;
                    snx[s][q] = (float)ox; snz[s][q] = (float)oz;
                }
            }
            var U = new float[vx.Count]; var V = new float[vx.Count];
            for (int i = 0; i < vx.Count; i++) { U[i] = PatchU; V[i] = vAt(vx[i], vz[i]); }
            return new RoadPatch
            {
                X = vx.ToArray(), Z = vz.ToArray(), Y = vy.ToArray(), Lift = vl.ToArray(), U = U, V = V,
                Tris = tris.ToArray(), Skirts = outSk, SkirtNX = snx, SkirtNZ = snz, Interior = interior,
            };
        }

        // ---- for tests

        /// <summary>Test accessor: the junction plan's patches anchored in the rectangle, and its trims, for a window.</summary>
        public List<RoadPatch> PatchesIn(double x0, double z0, double x1, double z1, float maxPiece)
        {
            var plan = PlanJunctions(x0, z0, x1, z1, maxPiece);
            return plan.Patches.FindAll(p => p.AnchorX >= x0 && p.AnchorX < x1 && p.AnchorZ >= z0 && p.AnchorZ < z1);
        }
    }
}
