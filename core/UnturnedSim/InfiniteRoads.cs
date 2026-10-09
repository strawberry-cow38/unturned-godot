using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace SDG.Unturned
{
    /// <summary>The four road classes (strawberry 2026-10-09): "several roads designated 'highways' (2x highway 2 type
    /// splines a fixed distance apart) that stay on a pretty set course, avoiding elevation shifts and obstacles while
    /// trying to stay somewhat straight, dont have power lines. then there are main roads, using the highway 1 road type,
    /// spread similarly to what we have now. then there are smaller roads. and then trails. both trails and small roads
    /// can branch off main roads".</summary>
    public enum RoadKind : byte { Highway = 0, Main = 1, Small = 2, Trail = 3 }

    /// <summary>What the strongest road at a point says about it. Clear is the MINIMUM over every road of (distance to
    /// its centreline - its paved half-width): negative on asphalt, the room ground cover and trees go by.</summary>
    public struct RoadHit
    {
        public bool Any;          // some road's carve reaches this point
        public RoadKind Kind;     // ...the one whose carve is strongest
        public float Dist;        // distance to that road's centreline
        public float Height;      // that road's profile height at the closest point
        public float Weight;      // 1 on the road, falling to 0 across its shoulder
        public float Clear;       // min over ALL roads of (dist - paved half-width)
    }

    /// <summary>
    /// The road network for the infinite world. Every road is a centreline polyline with a height profile, built the
    /// first time any region asks and cached; built identically whoever asks, so roads cross region borders like the
    /// ground does. Nothing here is global: each class is laid out on its own macro grid and every line is a function of
    /// (seed, grid cell) alone.
    ///
    ///   HIGHWAY  -- a long route per 12 km band, east-west and north-south, through anchors every 6 km. Between two
    ///               anchors a dynamic-programming search over a lateral corridor (+-900 m) picks the path that keeps the
    ///               grade low, stays off water and high ground, and turns as little as possible. Drawn as TWO Highway_1
    ///               carriageways either side of a median. No power lines.
    ///   MAIN     -- the original network: one node per 1536 m cell, links east and south, ~4 in 5 kept. Highway_0,
    ///               power lines down one side.
    ///   SMALL / TRAIL -- branches off a main road: a wandering polyline leaving it at an angle, ending where the land
    ///               says stop (water, a climb too steep, high ground) or at its length.
    /// </summary>
    public sealed class InfiniteRoads
    {
        // ---- specs. Widths are PEI's Roads.dat x RoadField.WidthScale 1.15 (Highway_1 6, Highway_0 8, White/Yellow 4, Trail 4).
        public const float HighwayLaneHalf = 6.9f;        // one Highway_1 carriageway
        public const float HighwayMedian = 4f;            // grass between the two carriageways
        public const float HighwayRibbonOffset = HighwayMedian * 0.5f + HighwayLaneHalf;   // each carriageway's centre from the route's
        public static float PavedHalf(RoadKind k) => k switch
        {
            RoadKind.Highway => HighwayMedian * 0.5f + 2f * HighwayLaneHalf,   // the whole corridor, median included
            RoadKind.Main => 9.2f,
            _ => 4.6f,
        };
        public static float Shoulder(RoadKind k) => k switch { RoadKind.Highway => 14f, RoadKind.Main => 10f, RoadKind.Small => 6f, _ => 4f };
        public static float MaxGrade(RoadKind k) => k switch { RoadKind.Highway => 0.07f, RoadKind.Main => 0.16f, RoadKind.Small => 0.14f, _ => 0.22f };
        public const float Bed = 0.12f;   // the ground under a paved surface sits this far below it

        public const double MainCell = 1536.0;
        public const double HighwayBand = 12000.0, HighwaySeg = 6000.0;
        const int MainCtrl = 24, Sub = 4;
        const int HwStations = 40, HwLanes = 41;   // 41 lanes at 60 m: the route may swing 1.2 km either way round an obstacle
        const double BridgeCost = 2500.0;          // per wet station: a long detour is cheaper, a short crossing is not
        const int MaxBridgeStations = 10;          // 1.5 km: past that it is a sea, and the highway ends at the shore
        public const float DeckAboveWater = 3f;
        const double HwStep = HighwaySeg / HwStations, HwLaneStep = 60.0;

        readonly InfiniteTerrain _t;
        readonly ulong _s;

        public sealed class Line
        {
            public RoadKind Kind;
            public double[] X, Z; public float[] H;
            public double MinX, MaxX, MinZ, MaxZ;
            // per-chunk boxes (8 segments each) so a distance query skips most of a long line cheaply
            public double[] CMinX, CMaxX, CMinZ, CMaxZ;
            public List<Line> Branches;   // main roads only
            /// <summary>Highways: where the road stands clear of the natural ground across its whole width -- the
            /// places a spline bridge will replace the embankment the carve builds today. Null on other classes.</summary>
            public List<Stretch> Raised;
            /// <summary>Highways: where the road runs deep below the natural ground across its whole width -- the
            /// carve has dug a trench through a hill, and a tunnel is the candidate.</summary>
            public List<Stretch> Cut;
            public bool[][] RaisedSeg, CutSeg;   // [carriageway: 0 = -offset side, 1 = +offset side][segment] (what PiecesIn copies onto pieces)
            public bool Exists => X != null;
            public int Segments => X.Length - 1;
        }
        static readonly Line None = new Line();

        /// <summary>A stretch of ONE carriageway where the carve has moved a lot of ground (strawberry: "both lanes may
        /// candidate separately" -- on a side slope one carriageway can stand on fill while the other sits in a cut).
        /// RAISED (bridge candidate): the carriageway is at least RaiseFill above the natural terrain at its centre and
        /// both its edges, or any of them is over water, for at least RaiseMinLength. CUT (tunnel candidate): at least
        /// CutDepth BELOW it at all three, for at least CutMinLength. Indices are into the line's dense X/Z/H.</summary>
        public struct Stretch
        {
            public int Side;            // which carriageway: -1 = the -HighwayRibbonOffset one, +1 = the + one
            public int I0, I1;          // first and last dense point
            public float Length;        // metres of road
            public float Max;           // the tallest the embankment / deepest the cut gets (m, across the whole width)
            public bool OverWater;      // raised only: some of it crosses water, a bridge proper rather than a viaduct
        }
        // strawberry 2026-10-09: 4 m / 8 m "need more". UG_INF_RAISE / UG_INF_CUT override them, for tuning.
        public static readonly float RaiseFill = EnvF("UG_INF_RAISE", 8f);   // m of fill under the whole carriageway
        public const float RaiseMinLength = 30f;    // shorter than this is a bump, not a bridge
        public const float RaiseMergeGap = 40f;     // two stretches closer than this are one bridge
        public static readonly float CutDepth = EnvF("UG_INF_CUT", 16f);     // m of cut under the whole carriageway
        public const float CutMinLength = 40f;      // a tunnel shorter than this is a culvert
        public const float CutMergeGap = 40f;
        static float EnvF(string name, float fallback) =>
            float.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) && v > 0f ? v : fallback;
        /// <summary>Why each highway segment that does not exist was dropped (diagnostics; a dropped segment is a dead end).</summary>
        public readonly ConcurrentDictionary<(int axis, long band, long k), string> HighwayDrops = new();
        Line Drop(int axis, long band, long k, string why) { HighwayDrops[(axis, band, k)] = why; return None; }
        const int Chunk = 8;

        readonly ConcurrentDictionary<(long, long, int), Line> _main = new();
        readonly ConcurrentDictionary<(int, long, long), Line> _hwy = new();
        readonly ConcurrentDictionary<(int, long, long), (double u, double w, float h, bool ok)> _anchor = new();

        internal InfiniteRoads(InfiniteTerrain t, ulong seed) { _t = t; _s = seed; }

        static float Smoothstep(float a, float b, float x) { float t = Math.Clamp((x - a) / (b - a), 0f, 1f); return t * t * (3f - 2f * t); }

        // Only ever called on a cache MISS: ConcurrentDictionary.Count takes every bucket lock, and checking it on each
        // lookup (60 per height sample) serialised all the worker threads behind it
        void Trim() { if (_main.Count > 20000) _main.Clear(); if (_hwy.Count > 4000) _hwy.Clear(); if (_anchor.Count > 8000) _anchor.Clear(); if (HighwayDrops.Count > 4000) HighwayDrops.Clear(); }

        // =============================================================================================================
        // Main roads

        void MainNode(long cx, long cz, out double x, out double z)
        {
            uint h = InfiniteTerrain.Hash(cx, cz, _s ^ 0xDDDD);
            x = (cx + 0.2 + 0.6 * ((h & 0xFFFF) / 65536.0)) * MainCell;
            z = (cz + 0.2 + 0.6 * ((h >> 16) / 65536.0)) * MainCell;
        }

        internal Line Main(long cx, long cz, int dir)
        {
            return _main.TryGetValue((cx, cz, dir), out var hit) ? hit
                 : _main.GetOrAdd((cx, cz, dir), k => { Trim(); return BuildMain(k.Item1, k.Item2, k.Item3); });
        }

        Line BuildMain(long cx, long cz, int dir)
        {
            if (InfiniteTerrain.Hash(cx, cz, _s ^ 0xDDDD ^ (ulong)(dir + 7)) % 100 >= 78) return None;   // ~4 in 5 links exist
            MainNode(cx, cz, out double ax, out double az);
            MainNode(cx + (dir == 0 ? 1 : 0), cz + (dir == 1 ? 1 : 0), out double bx, out double bz);
            double dx = bx - ax, dz = bz - az, len = Math.Sqrt(dx * dx + dz * dz);
            double px = -dz / len, pz = dx / len;
            ulong ws = InfiniteTerrain.Mix(_s ^ 0xDDDD ^ (ulong)cx * 31UL ^ (ulong)cz * 1031UL ^ (ulong)dir);
            var cxs = new double[MainCtrl + 1]; var czs = new double[MainCtrl + 1]; var ch = new float[MainCtrl + 1];
            for (int k = 0; k <= MainCtrl; k++)
            {
                double t = (double)k / MainCtrl;
                double w = 160.0 * 4.0 * t * (1.0 - t) * InfiniteTerrain.Gradient(t * 2.7, 0.5, ws);   // zero at both towns
                cxs[k] = ax + dx * t + px * w; czs[k] = az + dz * t + pz * w;
                float h = _t.RawHeight(cxs[k], czs[k]);
                if (h < InfiniteTerrain.SeaLevel + 1.5f || h > 135f) return None;   // no bridges, no passes
                ch[k] = h;
            }
            Smooth(ch, 4);
            for (int k = 0; k < MainCtrl; k++)
                if (Math.Abs(ch[k + 1] - ch[k]) / (len / MainCtrl) > MaxGrade(RoadKind.Main)) return None;
            var line = Finish(RoadKind.Main, cxs, czs, ch);
            if (line == null) return None;
            line.Branches = BuildBranches(line, cx, cz, dir);
            return line;
        }

        // =============================================================================================================
        // Branches: small roads and trails leaving a main road

        List<Line> BuildBranches(Line main, long cx, long cz, int dir)
        {
            var list = new List<Line>();
            ulong bs = InfiniteTerrain.Mix(_s ^ 0xB4A1 ^ (ulong)cx * 7919UL ^ (ulong)cz * 104729UL ^ (ulong)dir);
            int count = (int)(InfiniteTerrain.Hash(cx, cz, bs) % 4);   // 0..3 per link
            var arc = Arc(main);
            // small roads and trails do not cross a highway at grade: they end at its shoulder
            List<Line> hw = null;
            if (count > 0) { hw = new List<Line>(); TakeHighways(main.MinX - 1200, main.MinZ - 1200, main.MaxX + 1200, main.MaxZ + 1200, hw); }
            bool NearHighway(double x, double z) => hw.Count > 0 && Influence(hw, x, z).Clear < Shoulder(RoadKind.Highway) + 6f;
            for (int b = 0; b < count; b++)
            {
                uint h = InfiniteTerrain.Hash(b, 17, bs);
                RoadKind kind = (h & 0xFF) < 140 ? RoadKind.Small : RoadKind.Trail;
                double s = arc[arc.Length - 1] * (0.15 + 0.7 * ((h >> 8 & 0xFFFF) / 65536.0));
                int side = (h >> 24 & 1) == 0 ? 1 : -1;
                double angle = ((h >> 25) / 127.0 - 0.5) * 1.2;   // +-0.6 rad off square
                At(main, arc, s, out double sx, out double sz, out _, out double tx, out double tz);
                double hx = -tz * side, hz = tx * side;   // perpendicular, this side
                double ca = Math.Cos(angle), sa = Math.Sin(angle);
                double headX = hx * ca - hz * sa, headZ = hx * sa + hz * ca;
                double length = kind == RoadKind.Small ? 400 + 600 * ((h >> 3 & 0xFF) / 255.0) : 250 + 550 * ((h >> 3 & 0xFF) / 255.0);
                const double step = 60.0;
                int steps = (int)(length / step);
                var xs = new List<double>(); var zs = new List<double>(); var hs = new List<float>();
                // start just inside the main road's edge, at the main road's surface THERE. Not `sh`: the start is
                // offset at an angle, so the main's nearest point (which is what the carve reads) sits along-track of s
                double start = PavedHalf(RoadKind.Main) - 1.0;
                double x = sx + headX * start, z = sz + headZ * start;
                if (NearHighway(x, z)) continue;   // the main meets a highway here
                xs.Add(x); zs.Add(z); hs.Add(ProfileNear(main, x, z));
                ulong turn = InfiniteTerrain.Mix(bs + (ulong)b * 0x9E37UL);
                float hiLimit = kind == RoadKind.Small ? 150f : 175f;
                for (int k = 1; k <= steps; k++)
                {
                    double bend = 0.32 * InfiniteTerrain.Gradient(k * 0.35, 0.5, turn);
                    double c2 = Math.Cos(bend), s2 = Math.Sin(bend);
                    double nx = headX * c2 - headZ * s2, nz = headX * s2 + headZ * c2;
                    headX = nx; headZ = nz;
                    // sample the step at thirds: a highway corridor is ~60 m across, the step is 60 m, so the ends alone
                    // could straddle it
                    if (NearHighway(x + headX * step / 3, z + headZ * step / 3) || NearHighway(x + headX * step * 2 / 3, z + headZ * step * 2 / 3)
                        || NearHighway(x + headX * step, z + headZ * step)) break;
                    x += headX * step; z += headZ * step;
                    float rh = _t.RawHeight(x, z);
                    if (rh < InfiniteTerrain.SeaLevel + 1.5f || rh > hiLimit) break;
                    xs.Add(x); zs.Add(z); hs.Add(rh);
                }
                if (xs.Count < 4) continue;   // under ~180 m: not worth a road
                var ch = hs.ToArray();
                float pin = ch[0];
                Smooth(ch, 2);
                ch[0] = pin;
                // truncate at the first stretch steeper than this class allows
                int keep = ch.Length;
                for (int k = 1; k < ch.Length; k++)
                    if (Math.Abs(ch[k] - ch[k - 1]) / step > MaxGrade(kind)) { keep = k; break; }
                if (keep < 4) continue;
                var line = Finish(kind, xs.GetRange(0, keep).ToArray(), zs.GetRange(0, keep).ToArray(), ch[..keep]);
                if (line != null) list.Add(line);
            }
            return list;
        }

        // =============================================================================================================
        // Highways. axis 0 runs east-west (along x, one per z band); axis 1 runs north-south (along z, one per x band).

        double BandCentre(int axis, long band)
        {
            uint h = InfiniteTerrain.Hash(band, axis, _s ^ 0x4777);
            return (band + 0.5 + 0.5 * ((h & 0xFFFF) / 65536.0 - 0.5)) * HighwayBand;   // +-1/4 band of jitter
        }

        void ToWorld(int axis, double u, double w, out double x, out double z) { if (axis == 0) { x = u; z = w; } else { x = w; z = u; } }

        (double u, double w, float h, bool ok) Anchor(int axis, long band, long k)
        {
            return _anchor.GetOrAdd((axis, band, k), key =>
            {
                double u = k * HighwaySeg;
                double meander = 1500.0 * InfiniteTerrain.Gradient(u / 9000.0, band * 3.1 + axis * 0.7, _s ^ 0x4778);
                double w0 = BandCentre(axis, band) + meander;
                double best = double.MaxValue, bw = w0; float bh = 0f;
                // +-1.2 km; the outer +-1.2..2.4 km only when every inner spot is wet (a lake on the band line), so a
                // lake shifts the anchor round it rather than ending the highway at its shore
                for (int c = -8; c <= 8; c++)
                {
                    double w = w0 + c * 300.0;
                    ToWorld(axis, u, w, out double x, out double z);
                    float h = _t.RawHeight(x, z);
                    double cost = (h < InfiniteTerrain.SeaLevel + 2f ? 1e6 : 0) + (Math.Abs(c) > 4 ? 5e5 : 0) + Math.Max(0f, h - 100f) * 20.0 + Math.Abs(c) * 15.0;
                    if (cost < best) { best = cost; bw = w; bh = h; }
                }
                return (u, bw, bh, best < 1e6);
            });
        }

        internal Line Highway(int axis, long band, long k)
        {
            return _hwy.TryGetValue((axis, band, k), out var hit) ? hit
                 : _hwy.GetOrAdd((axis, band, k), key => { Trim(); return BuildHighway(key.Item1, key.Item2, key.Item3); });
        }

        Line BuildHighway(int axis, long band, long k)
        {
            var a = Anchor(axis, band, k); var b = Anchor(axis, band, k + 1);
            if (!a.ok && !b.ok) return Drop(axis, band, k, "open sea");
            // The route's base line through the anchors is a Hermite cubic with Catmull-Rom tangents from the anchors either
            // side, so consecutive segments meet with the SAME heading. (A straight base per segment kinked the highway by
            // up to ~15 degrees at every anchor, every 6 km.)
            double wPrev = Anchor(axis, band, k - 1).w, wNext = Anchor(axis, band, k + 2).w;
            double m0 = 0.5 * (b.w - wPrev), m1 = 0.5 * (wNext - a.w);
            Func<double, double> baseW = t =>
            {
                double t2 = t * t, t3 = t2 * t;
                return (2 * t3 - 3 * t2 + 1) * a.w + (t3 - 2 * t2 + t) * m0 + (-2 * t3 + 3 * t2) * b.w + (t3 - t2) * m1;
            };
            // dw/dt at the two anchors is m0 / m1 -- and the NEIGHBOUR segment computes the identical value for the anchor
            // they share, so handing it to the spline as the end tangent makes the heading continuous across the join
            string why;
            if (a.ok && b.ok)
            {
                var whole = Route(a, b, axis, baseW, m0, m1, false, out why);
                if (whole != null) return whole;
                if (why != "bridge") return Drop(axis, band, k, why);
                // water too wide to bridge between two dry anchors (a bay, a big lake): run each side down to its own
                // shore, like a ferry crossing, rather than dropping 6 km of highway and dead-ending both neighbours
                var sa = Route(a, b, axis, baseW, m0, m1, true, out string wa);
                var sb = Route(b, a, axis, baseW, m0, m1, true, out string wb);
                if (sa == null && sb == null) return Drop(axis, band, k, $"water too wide to bridge, and no shore spur ({wa} / {wb})");
                var first = sa ?? sb;
                if (sa != null && sb != null) first.Branches = new List<Line> { sb };
                return first;
            }
            // a wet anchor is the COAST: from the dry end to the shore, so the highway runs down to the sea rather than
            // dead-ending at the last dry anchor, up to a whole segment (6 km) inland
            return Route(a.ok ? a : b, a.ok ? b : a, axis, baseW, m0, m1, true, out why) ?? Drop(axis, band, k, why);
        }

        /// <summary>One highway segment from anchor p toward anchor q. `toShore`: q is not reached -- the road goes as far as
        /// it can on dry land and ends there (water forbidden, end free). Otherwise both ends pinned, and water may be
        /// bridged (dear, and at most MaxBridgeStations); "bridge" in `why` means only a longer one would do.</summary>
        Line Route((double u, double w, float h, bool ok) p, (double u, double w, float h, bool ok) q, int axis, Func<double, double> baseW, double slope0, double slope1, bool toShore, out string why)
        {
            why = null;
            double du = Math.Sign(q.u - p.u) * HwStep;
            bool fwd = du > 0;
            double BaseAt(int i) => baseW(fwd ? (double)i / HwStations : 1.0 - (double)i / HwStations);
            int N = HwStations, L = HwLanes, mid = L / 2;
            float wetBelow = InfiniteTerrain.SeaLevel + 2f;
            var hgt = new float[N + 1, L];
            for (int i = 0; i <= N; i++)
            {
                double u = p.u + i * du, wBase = BaseAt(i);
                for (int j = 0; j < L; j++)
                {
                    if ((i == 0 || (i == N && !toShore)) && j != mid) { hgt[i, j] = float.NaN; continue; }   // pinned to the anchors
                    ToWorld(axis, u, wBase + (j - mid) * HwLaneStep, out double x, out double z);
                    hgt[i, j] = _t.RawHeight(x, z);
                }
            }
            // DP over stations x lateral lanes. Node cost: high ground expensive, water a BRIDGE (allowed when pinned, but
            // dearer than any sane detour). Edge cost: grade (squared, so gentle beats steep by a lot) and lateral change
            // (squared: straight beats wiggly).
            var cost = new double[N + 1, L];
            var from = new int[N + 1, L];
            for (int j = 0; j < L; j++) cost[0, j] = j == mid ? 0 : double.MaxValue;
            int end = 0;
            for (int i = 1; i <= N; i++)
            {
                bool reached = false;
                for (int j = 0; j < L; j++)
                {
                    cost[i, j] = double.MaxValue;
                    float h = hgt[i, j];
                    if (float.IsNaN(h)) continue;
                    bool wet = h < wetBelow;
                    if (wet && toShore) continue;
                    double node = (wet ? BridgeCost : 0) + Math.Max(0f, h - 100f) * 6.0 + Math.Abs(j - mid) * 0.15;
                    for (int jp = Math.Max(0, j - 2); jp <= Math.Min(L - 1, j + 2); jp++)
                    {
                        if (cost[i - 1, jp] == double.MaxValue) continue;
                        double run = Math.Sqrt(HwStep * HwStep + ((j - jp) * HwLaneStep) * ((j - jp) * HwLaneStep));
                        double grade = Math.Abs(h - hgt[i - 1, jp]) / run * 100.0;
                        double c = cost[i - 1, jp] + node + grade * grade * 0.35 + (j - jp) * (j - jp) * 3.0;
                        if (c < cost[i, j]) { cost[i, j] = c; from[i, j] = jp; reached = true; }
                    }
                }
                if (!reached) break;
                end = i;
            }
            int last = mid;
            if (toShore)
            {
                if (end < 6) { why = "shore within 900 m of the anchor"; return null; }
                for (int j = 0; j < L; j++) if (cost[end, j] < cost[end, last]) last = j;
            }
            else if (end < N || cost[N, mid] == double.MaxValue) { why = "no route"; return null; }
            var lane = new int[end + 1];
            lane[end] = last;
            for (int i = end; i > 0; i--) lane[i - 1] = from[i, lane[i]];
            int wetRun = 0, longestBridge = 0;
            for (int i = 0; i <= end; i++) { wetRun = hgt[i, lane[i]] < wetBelow ? wetRun + 1 : 0; longestBridge = Math.Max(longestBridge, wetRun); }
            if (longestBridge > MaxBridgeStations) { why = "bridge"; return null; }
            var xs = new double[end + 1]; var zs = new double[end + 1]; var hs = new float[end + 1];
            var lat = new double[end + 1];
            for (int i = 0; i <= end; i++) lat[i] = (lane[i] - mid) * HwLaneStep;
            // straighten the lane staircase (ends held), then place
            for (int pass = 0; pass < 6; pass++)
            {
                var tmp = (double[])lat.Clone();
                for (int i = 1; i < end; i++) tmp[i] = 0.25 * lat[i - 1] + 0.5 * lat[i] + 0.25 * lat[i + 1];
                lat = tmp;
            }
            // ...and fade the offset in over the first/last 600 m at a pinned anchor: lat is 0 there but its SLOPE is not,
            // and a slope is a heading change. Offset x a taper that is 0 at the end has zero slope at the end.
            for (int i = 0; i <= end; i++)
            {
                double fromEnd = toShore ? i : Math.Min(i, end - i);
                double f = Math.Clamp(fromEnd / 4.0, 0.0, 1.0);
                lat[i] *= f * f * (3 - 2 * f);
            }
            for (int i = 0; i <= end; i++)
            {
                double u = p.u + i * du, w = BaseAt(i) + lat[i];
                ToWorld(axis, u, w, out xs[i], out zs[i]);
                hs[i] = i == 0 ? p.h : i == N ? q.h : _t.RawHeight(xs[i], zs[i]);
                // over water (a bridged lake, or a station the straightening pulled off the shore) the deck stands clear
                // of it; the ground carve then raises a causeway underneath
                hs[i] = Math.Max(hs[i], InfiniteTerrain.SeaLevel + DeckAboveWater);
            }
            Smooth(hs, 8);   // averaging values that are all >= the deck height cannot take one below it
            hs[0] = Math.Max(p.h, InfiniteTerrain.SeaLevel + DeckAboveWater);
            if (!toShore) hs[end] = Math.Max(q.h, InfiniteTerrain.SeaLevel + DeckAboveWater);
            // cut and fill to the grade limit: forward then backward, each pass only ever moves a station TOWARD its
            // neighbour, so a stretch that is too steep is shared out across the stations either side of it
            float maxRise = MaxGrade(RoadKind.Highway) * (float)HwStep;
            for (int rep = 0; rep < 4; rep++)
            {
                for (int i = 1; i < end || (toShore && i == end); i++) hs[i] = Math.Clamp(hs[i], hs[i - 1] - maxRise, hs[i - 1] + maxRise);
                for (int i = end - 1; i > 0; i--) hs[i] = Math.Clamp(hs[i], hs[i + 1] - maxRise, hs[i + 1] + maxRise);
            }
            for (int i = 0; i < end; i++) if (Math.Abs(hs[i + 1] - hs[i]) > maxRise * 1.001f) { why = $"grade unfixable at station {i}"; return null; }
            // unit heading of the base curve at an anchor, in the direction of travel
            (double, double) Heading(double dwdt)
            {
                double tu = fwd ? HighwaySeg : -HighwaySeg, tw = fwd ? dwdt : -dwdt, l = Math.Sqrt(tu * tu + tw * tw);
                ToWorld(axis, tu / l, tw / l, out double tx, out double tz);
                return (tx, tz);
            }
            var startT = Heading(fwd ? slope0 : slope1);
            (double, double)? endT = toShore ? null : Heading(fwd ? slope1 : slope0);
            var line = Finish(RoadKind.Highway, xs, zs, hs, startT, endT);
            if (line == null) why = "drawn curve over grade";
            else MarkStretches(line);
            return line;
        }

        /// <summary>Find a highway's raised and cut Stretches, each CARRIAGEWAY on its own. Samples the natural ground
        /// at the carriageway's centre and both its edges at every dense point; the fill (or cut) that counts is the
        /// SMALLEST of the three, so a carriageway half on fill and half in a cut is a retaining wall, neither.</summary>
        void MarkStretches(Line e)
        {
            int n = e.X.Length;
            e.Raised = new List<Stretch>(); e.Cut = new List<Stretch>();
            e.RaisedSeg = new bool[2][]; e.CutSeg = new bool[2][];
            var arc = Arc(e);
            for (int side = -1; side <= 1; side += 2)
            {
                var on = new bool[n]; var fill = new float[n]; var wet = new bool[n];
                var deep = new bool[n]; var cut = new float[n];
                for (int i = 0; i < n; i++)
                {
                    CarriagewaySample(e, i, side, out float f, out float c, out bool w);
                    fill[i] = f; cut[i] = c; wet[i] = w;
                    on[i] = f >= RaiseFill || w;
                    deep[i] = c >= CutDepth;
                }
                int si = side < 0 ? 0 : 1;
                (var raised, e.RaisedSeg[si]) = Stretches(e, arc, on, fill, wet, RaiseMinLength, RaiseMergeGap, side);
                (var cuts, e.CutSeg[si]) = Stretches(e, arc, deep, cut, null, CutMinLength, CutMergeGap, side);
                e.Raised.AddRange(raised); e.Cut.AddRange(cuts);
            }
        }

        /// <summary>Fill and cut under one carriageway at dense point i: the smallest of the three samples (centre and
        /// both edges, just inside the asphalt), and whether any of them is over water. Public-facing twin of what the
        /// marking uses, so a test can measure the same carriageway without re-deriving its geometry.</summary>
        void CarriagewaySample(Line e, int i, int side, out float fill, out float cut, out bool wet)
        {
            int n = e.X.Length, a = Math.Max(0, i - 1), b = Math.Min(n - 1, i + 1);
            double tx = e.X[b] - e.X[a], tz = e.Z[b] - e.Z[a], tl = Math.Sqrt(tx * tx + tz * tz);
            double nx = -tz / tl, nz = tx / tl;
            float ground = e.H[i] - Bed;
            fill = float.MaxValue; cut = float.MaxValue; wet = false;
            for (int s = -1; s <= 1; s++)
            {
                double o = side * HighwayRibbonOffset + s * HighwayLaneHalf * 0.9;
                float r = _t.RawHeight(e.X[i] + nx * o, e.Z[i] + nz * o);
                fill = Math.Min(fill, ground - r);
                cut = Math.Min(cut, r - ground);
                if (r < InfiniteTerrain.SeaLevel) wet = true;
            }
        }

        /// <summary>Runs of marked points, merged across gaps under `mergeGap`, then the ones under `minLength` dropped.</summary>
        static (List<Stretch>, bool[]) Stretches(Line e, double[] arc, bool[] on, float[] measure, bool[] wet, float minLength, float mergeGap, int side)
        {
            int n = on.Length;
            var runs = new List<(int i0, int i1)>();
            for (int i = 0; i < n; i++)
            {
                if (!on[i]) continue;
                int j = i; while (j + 1 < n && on[j + 1]) j++;
                if (runs.Count > 0 && arc[i] - arc[runs[^1].i1] < mergeGap) runs[^1] = (runs[^1].i0, j);
                else runs.Add((i, j));
                i = j;
            }
            var list = new List<Stretch>();
            var seg = new bool[e.Segments];
            foreach (var (i0, i1) in runs)
            {
                float len = (float)(arc[i1] - arc[i0]);
                if (len < minLength) continue;
                float mx = 0f; bool w = false;
                for (int i = i0; i <= i1; i++) { mx = Math.Max(mx, measure[i]); if (wet != null) w |= wet[i]; }
                list.Add(new Stretch { Side = side, I0 = i0, I1 = i1, Length = len, Max = mx, OverWater = w });
                for (int k = i0; k < i1; k++) seg[k] = true;
            }
            return (list, seg);
        }

        // =============================================================================================================
        // Shared shaping

        static void Smooth(float[] h, int passes)
        {
            int n = h.Length - 1;
            var tmp = new float[h.Length];
            for (int p = 0; p < passes; p++)
            {
                tmp[0] = h[0]; tmp[n] = h[n];
                for (int k = 1; k < n; k++) tmp[k] = 0.25f * h[k - 1] + 0.5f * h[k] + 0.25f * h[k + 1];
                Array.Copy(tmp, h, h.Length);
            }
        }

        /// <summary>Catmull-Rom the control points x4 for the drawn/carved centreline; the profile LINEAR IN ARC LENGTH
        /// between controls (linear in the spline parameter steepens where Catmull-Rom bunches samples -- the drivability
        /// test caught that at 20.7%). Returns null if the drawn geometry breaks the class's grade limit.</summary>
        Line Finish(RoadKind kind, double[] cx, double[] cz, float[] ch, (double x, double z)? startTangent = null, (double x, double z)? endTangent = null)
        {
            int C = cx.Length - 1, P = C * Sub;
            var e = new Line { Kind = kind, X = new double[P + 1], Z = new double[P + 1], H = new float[P + 1] };
            // Catmull-Rom's tangent at P0 is (P1 - P[-1]) / 2. With no neighbour, P[-1] = P0 (the curve leaves along its
            // first chord). Given a heading T, P[-1] = P1 - 2|P1-P0| T makes the tangent exactly T, so two lines that are
            // handed the same T at a shared point meet there without a kink. Same at the far end.
            double sx0 = cx[0], sz0 = cz[0], ex1 = cx[C], ez1 = cz[C];
            if (startTangent is (double tx, double tz))
            {
                double l = Math.Sqrt((cx[1] - cx[0]) * (cx[1] - cx[0]) + (cz[1] - cz[0]) * (cz[1] - cz[0]));
                sx0 = cx[1] - 2 * l * tx; sz0 = cz[1] - 2 * l * tz;
            }
            if (endTangent is (double ux, double uz))
            {
                double l = Math.Sqrt((cx[C] - cx[C - 1]) * (cx[C] - cx[C - 1]) + (cz[C] - cz[C - 1]) * (cz[C] - cz[C - 1]));
                ex1 = cx[C - 1] + 2 * l * ux; ez1 = cz[C - 1] + 2 * l * uz;
            }
            double PX(int i) => i < 0 ? sx0 : i > C ? ex1 : cx[i];
            double PZ(int i) => i < 0 ? sz0 : i > C ? ez1 : cz[i];
            for (int k = 0; k < C; k++)
            {
                for (int sub = 0; sub < Sub; sub++)
                {
                    double u = (double)sub / Sub, u2 = u * u, u3 = u2 * u;
                    double b0 = -0.5 * u3 + u2 - 0.5 * u, b1 = 1.5 * u3 - 2.5 * u2 + 1.0, b2 = -1.5 * u3 + 2.0 * u2 + 0.5 * u, b3 = 0.5 * u3 - 0.5 * u2;
                    int i = k * Sub + sub;
                    e.X[i] = b0 * PX(k - 1) + b1 * cx[k] + b2 * cx[k + 1] + b3 * PX(k + 2);
                    e.Z[i] = b0 * PZ(k - 1) + b1 * cz[k] + b2 * cz[k + 1] + b3 * PZ(k + 2);
                }
            }
            e.X[P] = cx[C]; e.Z[P] = cz[C];
            var arc = Arc(e);
            for (int k = 0; k < C; k++)
            {
                double a0 = arc[k * Sub], a1 = arc[(k + 1) * Sub];
                for (int sub = 0; sub < Sub; sub++)
                {
                    int i = k * Sub + sub;
                    e.H[i] = ch[k] + (ch[k + 1] - ch[k]) * (float)((arc[i] - a0) / Math.Max(1e-9, a1 - a0));
                }
            }
            e.H[P] = ch[C];
            for (int i = 0; i < P; i++)
                if (Math.Abs(e.H[i + 1] - e.H[i]) / Math.Max(1e-9, arc[i + 1] - arc[i]) > MaxGrade(kind) * 1.0001f) return null;
            double m = PavedHalf(kind) + Shoulder(kind);
            int chunks = (P + Chunk - 1) / Chunk;
            e.CMinX = new double[chunks]; e.CMaxX = new double[chunks]; e.CMinZ = new double[chunks]; e.CMaxZ = new double[chunks];
            e.MinX = e.MinZ = double.MaxValue; e.MaxX = e.MaxZ = double.MinValue;
            for (int c = 0; c < chunks; c++)
            {
                double mnx = double.MaxValue, mxx = double.MinValue, mnz = double.MaxValue, mxz = double.MinValue;
                for (int i = c * Chunk; i <= Math.Min(P, (c + 1) * Chunk); i++)
                {
                    mnx = Math.Min(mnx, e.X[i]); mxx = Math.Max(mxx, e.X[i]); mnz = Math.Min(mnz, e.Z[i]); mxz = Math.Max(mxz, e.Z[i]);
                }
                e.CMinX[c] = mnx - m; e.CMaxX[c] = mxx + m; e.CMinZ[c] = mnz - m; e.CMaxZ[c] = mxz + m;
                e.MinX = Math.Min(e.MinX, e.CMinX[c]); e.MaxX = Math.Max(e.MaxX, e.CMaxX[c]);
                e.MinZ = Math.Min(e.MinZ, e.CMinZ[c]); e.MaxZ = Math.Max(e.MaxZ, e.CMaxZ[c]);
            }
            return e;
        }

        internal static double[] Arc(Line e)
        {
            var arc = new double[e.X.Length];
            for (int i = 0; i + 1 < e.X.Length; i++)
                arc[i + 1] = arc[i] + Math.Sqrt((e.X[i + 1] - e.X[i]) * (e.X[i + 1] - e.X[i]) + (e.Z[i + 1] - e.Z[i]) * (e.Z[i + 1] - e.Z[i]));
            return arc;
        }

        /// <summary>Point, profile height and unit tangent at arc length s along a line.</summary>
        internal static void At(Line e, double[] arc, double s, out double x, out double z, out float h, out double tx, out double tz)
        {
            int k = 0, n = e.Segments;
            while (k < n - 1 && arc[k + 1] < s) k++;
            double t = Math.Clamp((s - arc[k]) / Math.Max(1e-9, arc[k + 1] - arc[k]), 0.0, 1.0);
            double sx = e.X[k + 1] - e.X[k], sz = e.Z[k + 1] - e.Z[k], len = Math.Sqrt(sx * sx + sz * sz);
            x = e.X[k] + sx * t; z = e.Z[k] + sz * t; h = e.H[k] + (e.H[k + 1] - e.H[k]) * (float)t;
            tx = sx / len; tz = sz / len;
        }

        // =============================================================================================================
        // Queries

        /// <summary>Every line whose influence box touches the rectangle -- the per-region working set, so the 4,500
        /// height samples of a region each test a handful of lines instead of walking the grid.</summary>
        public List<Line> LinesIn(double x0, double z0, double x1, double z1)
        {
            var list = new List<Line>();
            void Take(Line e) { if (e.Exists && e.MaxX >= x0 && e.MinX <= x1 && e.MaxZ >= z0 && e.MinZ <= z1) list.Add(e); }
            // main links + their branches (branches reach ~1 km off their link: look two cells out)
            long ci0 = (long)Math.Floor(x0 / MainCell) - 2, ci1 = (long)Math.Floor(x1 / MainCell) + 2;
            long cj0 = (long)Math.Floor(z0 / MainCell) - 2, cj1 = (long)Math.Floor(z1 / MainCell) + 2;
            for (long i = ci0; i <= ci1; i++)
                for (long j = cj0; j <= cj1; j++)
                    for (int dir = 0; dir < 2; dir++)
                    {
                        var e = Main(i, j, dir);
                        if (!e.Exists) continue;
                        Take(e);
                        if (e.Branches != null) foreach (var br in e.Branches) Take(br);
                    }
            TakeHighways(x0, z0, x1, z1, list);
            return list;
        }

        /// <summary>Highway lines (both sides of any water gap) whose bounds touch the rectangle.</summary>
        void TakeHighways(double x0, double z0, double x1, double z1, List<Line> list)
        {
            void Take(Line e) { if (e.Exists && e.MaxX >= x0 && e.MinX <= x1 && e.MaxZ >= z0 && e.MinZ <= z1) list.Add(e); }
            // the bands whose route could pass within reach (jitter + meander + anchor pick + DP lane)
            const double reach = 0.25 * HighwayBand + 1500 + 2400 + 1200 + 40;
            for (int axis = 0; axis < 2; axis++)
            {
                double w0 = axis == 0 ? z0 : x0, w1 = axis == 0 ? z1 : x1, u0 = axis == 0 ? x0 : z0, u1 = axis == 0 ? x1 : z1;
                long b0 = (long)Math.Floor((w0 - reach) / HighwayBand), b1 = (long)Math.Floor((w1 + reach) / HighwayBand);
                long k0 = (long)Math.Floor(u0 / HighwaySeg) - 1, k1 = (long)Math.Floor(u1 / HighwaySeg);
                for (long band = b0; band <= b1; band++)
                    for (long k = k0; k <= k1; k++)
                    {
                        var e = Highway(axis, band, k);
                        Take(e);
                        if (e.Branches != null) foreach (var br in e.Branches) Take(br);   // the far side of a water gap
                    }
            }
        }

        /// <summary>The line's profile height at the centreline point nearest (x, z).</summary>
        static float ProfileNear(Line e, double x, double z)
        {
            double best = double.MaxValue; float bh = e.H[0];
            for (int k = 0; k < e.Segments; k++)
            {
                double sx = e.X[k + 1] - e.X[k], sz = e.Z[k + 1] - e.Z[k];
                double qx = x - e.X[k], qz = z - e.Z[k];
                double t = Math.Clamp((qx * sx + qz * sz) / (sx * sx + sz * sz), 0.0, 1.0);
                double ex = qx - sx * t, ez = qz - sz * t, d = ex * ex + ez * ez;
                if (d < best) { best = d; bh = e.H[k] + (e.H[k + 1] - e.H[k]) * (float)t; }
            }
            return bh;
        }

        /// <summary>The strongest road at a point among `lines`, and the clearance to the nearest asphalt of any.</summary>
        public static RoadHit Influence(List<Line> lines, double x, double z)
        {
            var hit = new RoadHit { Clear = float.MaxValue };
            foreach (var e in lines)
            {
                if (x < e.MinX || x > e.MaxX || z < e.MinZ || z > e.MaxZ) continue;
                float best = float.MaxValue, bh = 0f;
                for (int c = 0; c < e.CMinX.Length; c++)
                {
                    if (x < e.CMinX[c] || x > e.CMaxX[c] || z < e.CMinZ[c] || z > e.CMaxZ[c]) continue;
                    int end = Math.Min(e.Segments, (c + 1) * Chunk);
                    for (int k = c * Chunk; k < end; k++)
                    {
                        double sx = e.X[k + 1] - e.X[k], sz = e.Z[k + 1] - e.Z[k];
                        double qx = x - e.X[k], qz = z - e.Z[k];
                        double t = Math.Clamp((qx * sx + qz * sz) / (sx * sx + sz * sz), 0.0, 1.0);
                        double ex = qx - sx * t, ez = qz - sz * t;
                        float dist = (float)Math.Sqrt(ex * ex + ez * ez);
                        if (dist < best) { best = dist; bh = e.H[k] + (e.H[k + 1] - e.H[k]) * (float)t; }
                    }
                }
                if (best == float.MaxValue) continue;
                float half = PavedHalf(e.Kind), sh = Shoulder(e.Kind);
                hit.Clear = Math.Min(hit.Clear, best - half);
                if (best >= half + sh) continue;
                float w = Smoothstep(half + sh, half, best);
                // strongest carve wins; inside two corridors at once, the one you are deeper inside
                if (!hit.Any || w > hit.Weight + 1e-6f || (w >= hit.Weight - 1e-6f && best - half < hit.Dist - PavedHalf(hit.Kind)))
                {
                    hit.Any = true; hit.Kind = e.Kind; hit.Dist = best; hit.Height = bh; hit.Weight = w;
                }
            }
            return hit;
        }

        public RoadHit Influence(double x, double z) => Influence(LinesIn(x - 1, z - 1, x + 1, z + 1), x, z);

        // =============================================================================================================
        // What a region draws

        /// <summary>Road surface pieces whose midpoint lies in the rectangle, at most `maxPiece` long, with tangents
        /// averaged across bends (so neighbouring pieces close). A highway gives TWO ribbons, one per carriageway.</summary>
        public List<RoadPiece> PiecesIn(List<Line> lines, double x0, double z0, double x1, double z1, float maxPiece)
        {
            var list = new List<RoadPiece>();
            foreach (var e in lines)
            {
                if (e.Kind == RoadKind.Highway) { Ribbon(e, +HighwayRibbonOffset); Ribbon(e, -HighwayRibbonOffset); }   // each carriageway carries its OWN marks
                else Ribbon(e, 0.0);
            }
            return list;

            void Ribbon(Line e, double offset)
            {
                int n = e.Segments;
                // the ribbon's own centreline: the road's, moved sideways along the averaged normal
                var rx = new double[n + 1]; var rz = new double[n + 1];
                var tgx = new float[n + 1]; var tgz = new float[n + 1];
                for (int i = 0; i <= n; i++)
                {
                    int a = Math.Max(0, i - 1), b = Math.Min(n - 1, i);
                    double ax = e.X[a + 1] - e.X[a], az = e.Z[a + 1] - e.Z[a], al = Math.Sqrt(ax * ax + az * az);
                    double bx = e.X[b + 1] - e.X[b], bz = e.Z[b + 1] - e.Z[b], bl = Math.Sqrt(bx * bx + bz * bz);
                    double mx = ax / al + bx / bl, mz = az / al + bz / bl, ml = Math.Sqrt(mx * mx + mz * mz);
                    tgx[i] = (float)(mx / ml); tgz[i] = (float)(mz / ml);
                    rx[i] = e.X[i] - tgz[i] * offset; rz[i] = e.Z[i] + tgx[i] * offset;
                }
                float s = 0f;
                for (int k = 0; k < n; k++)
                {
                    double sx = rx[k + 1] - rx[k], sz = rz[k + 1] - rz[k];
                    float segLen = (float)Math.Sqrt(sx * sx + sz * sz);
                    if (segLen < 1e-4f) continue;
                    float dX = (float)(sx / segLen), dZ = (float)(sz / segLen);
                    int pcs = Math.Max(1, (int)Math.Ceiling(segLen / maxPiece));
                    for (int p = 0; p < pcs; p++)
                    {
                        double ta = (double)p / pcs, tb = (double)(p + 1) / pcs;
                        double mx = rx[k] + sx * (ta + tb) * 0.5, mz = rz[k] + sz * (ta + tb) * 0.5;
                        if (mx < x0 || mx >= x1 || mz < z0 || mz >= z1) continue;
                        list.Add(new RoadPiece
                        {
                            Kind = (byte)e.Kind,
                            X0 = rx[k] + sx * ta, Z0 = rz[k] + sz * ta, X1 = rx[k] + sx * tb, Z1 = rz[k] + sz * tb,
                            H0 = e.H[k] + (e.H[k + 1] - e.H[k]) * (float)ta, H1 = e.H[k] + (e.H[k + 1] - e.H[k]) * (float)tb,
                            S0 = s + segLen * (float)ta, S1 = s + segLen * (float)tb,
                            T0X = p == 0 ? tgx[k] : dX, T0Z = p == 0 ? tgz[k] : dZ,
                            T1X = p == pcs - 1 ? tgx[k + 1] : dX, T1Z = p == pcs - 1 ? tgz[k + 1] : dZ,
                            Raised = offset != 0.0 && e.RaisedSeg != null && e.RaisedSeg[offset > 0 ? 1 : 0][k],
                            Cut = offset != 0.0 && e.CutSeg != null && e.CutSeg[offset > 0 ? 1 : 0][k],
                        });
                    }
                    s += segLen;
                }
            }
        }

        public const float PoleSpacing = 40f, PoleSetback = 3f, PoleEndClear = 24f;

        /// <summary>Does this class carry power lines? Main and small roads do; highways and dirt trails do not
        /// (strawberry 2026-10-09: highways "dont have power lines", "dirt trails dont get power lines either").</summary>
        public static bool HasPowerLines(RoadKind k) => k == RoadKind.Main || k == RoadKind.Small;

        /// <summary>Power-line poles along main and small roads (see HasPowerLines), right-hand side, every
        /// PoleSpacing by arc length, clear of both towns; a pole that would stand on another road is skipped and the
        /// wire jumps to the next one. Each pole carries the next for the span that may leave the region.</summary>
        public List<PolePlacement> PolesIn(List<Line> lines, double x0, double z0, double x1, double z1)
        {
            var list = new List<PolePlacement>();
            foreach (var e in lines)
            {
                if (!HasPowerLines(e.Kind)) continue;
                double off = PavedHalf(e.Kind) + PoleSetback;
                var arc = Arc(e);
                double total = arc[arc.Length - 1];
                int count = (int)Math.Floor((total - 2 * PoleEndClear) / PoleSpacing) + 1;
                if (count < 2) continue;
                void Spot(int q, out double px, out double pz, out float dx, out float dz)
                {
                    At(e, arc, PoleEndClear + q * PoleSpacing, out double cx, out double cz, out _, out double tx, out double tz);
                    dx = (float)tx; dz = (float)tz;
                    px = cx - tz * off; pz = cz + tx * off;
                }
                bool Stands(int q)
                {
                    Spot(q, out double sx, out double sz, out _, out _);
                    return Influence(sx, sz).Clear > 1.5f;
                }
                for (int q = 0; q < count; q++)
                {
                    Spot(q, out double px, out double pz, out float dx, out float dz);
                    if (px < x0 || px >= x1 || pz < z0 || pz >= z1) continue;
                    if (!Stands(q)) continue;
                    var pp = new PolePlacement { X = px, Z = pz, H = _t.HeightAt(px, pz), DirX = dx, DirZ = dz };
                    for (int nq = q + 1; nq < count && nq <= q + 2; nq++)
                    {
                        if (!Stands(nq)) continue;
                        Spot(nq, out double nx, out double nz, out float ndx, out float ndz);
                        pp.HasNext = true; pp.NX = nx; pp.NZ = nz; pp.NH = _t.HeightAt(nx, nz); pp.NDirX = ndx; pp.NDirZ = ndz;
                        break;
                    }
                    list.Add(pp);
                }
            }
            return list;
        }

        // ---- for tests and tools
        static (double, double, float)[] Pts(Line e)
        {
            if (!e.Exists) return null;
            var r = new (double, double, float)[e.X.Length];
            for (int i = 0; i < r.Length; i++) r[i] = (e.X[i], e.Z[i], e.H[i]);
            return r;
        }
        public (double x, double z, float h)[] MainCentreline(long cx, long cz, int dir) => Pts(Main(cx, cz, dir));
        public (double x, double z, float h)[] HighwayCentreline(int axis, long band, long k) => Pts(Highway(axis, band, k));
        /// <summary>Test/tool accessor: the raised stretches of a highway segment (and of the far side of a water gap).</summary>
        public List<(Stretch r, (double x, double z, float h)[] pts)> RaisedOf(int axis, long band, long k) => StretchesOf(axis, band, k, false);
        /// <summary>Test/tool accessor: the cut (tunnel-candidate) stretches of a highway segment.</summary>
        public List<(Stretch r, (double x, double z, float h)[] pts)> CutOf(int axis, long band, long k) => StretchesOf(axis, band, k, true);
        List<(Stretch r, (double x, double z, float h)[] pts)> StretchesOf(int axis, long band, long k, bool cut)
        {
            var e = Highway(axis, band, k);
            var r = new List<(Stretch, (double, double, float)[])>();
            void Add(Line l)
            {
                var list = cut ? l.Cut : l.Raised;
                if (!l.Exists || list == null) return;
                foreach (var s in list) r.Add((s, CarriagewayPts(l, s.Side, s.I0, s.I1)));
            }
            Add(e);
            if (e.Branches != null) foreach (var b in e.Branches) Add(b);
            return r;
        }
        /// <summary>The centreline of one carriageway (side -1/+1) over dense points i0..i1, at the road's height.</summary>
        static (double x, double z, float h)[] CarriagewayPts(Line e, int side, int i0, int i1)
        {
            int n = e.X.Length;
            var pts = new (double, double, float)[i1 - i0 + 1];
            for (int i = i0; i <= i1; i++)
            {
                int a = Math.Max(0, i - 1), b = Math.Min(n - 1, i + 1);
                double tx = e.X[b] - e.X[a], tz = e.Z[b] - e.Z[a], tl = Math.Sqrt(tx * tx + tz * tz);
                pts[i - i0] = (e.X[i] - tz / tl * side * HighwayRibbonOffset, e.Z[i] + tx / tl * side * HighwayRibbonOffset, e.H[i]);
            }
            return pts;
        }
        /// <summary>Test accessor: one carriageway of a highway segment, as a centreline.</summary>
        public (double x, double z, float h)[] HighwayCarriageway(int axis, long band, long k, int side)
        {
            var e = Highway(axis, band, k);
            return e.Exists ? CarriagewayPts(e, side, 0, e.X.Length - 1) : null;
        }

        public List<(RoadKind kind, (double x, double z, float h)[] pts)> BranchesOf(long cx, long cz, int dir)
        {
            var e = Main(cx, cz, dir);
            var r = new List<(RoadKind, (double, double, float)[])>();
            if (e.Exists && e.Branches != null) foreach (var b in e.Branches) r.Add((b.Kind, Pts(b)));
            return r;
        }
    }
}
