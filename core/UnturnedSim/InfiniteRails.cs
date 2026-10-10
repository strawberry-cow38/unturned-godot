using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace SDG.Unturned
{
    /// <summary>One tile of track (InfiniteRoads.TrackOf), absolute metres. Kind 0 = a New_Rail_Unit: its root on the
    /// centreline at RailOriginY, the unit's +Z along (DX, DY, DZ) -- grade included -- stretched by K along it, owning
    /// the sleeper at its root. Kind 1 = a New_Rail_Sleeper closing a line's open end.</summary>
    public struct RailPiece
    {
        public byte Kind;
        public double X, Z; public float Y;
        public float DX, DY, DZ;
        public float K;
    }

    /// <summary>Where a main road may cross a railway on the level: the rail is flat here (RailSiteFlat either way, at
    /// H), on natural ground, and nowhere near one of its grade separations or bridges. A = horizontal arc along the
    /// rail, F = the same as a fractional dense index, (TX, TZ) = the rail's unit heading.</summary>
    public struct RailSite { public double A, F, X, Z; public float H; public double TX, TZ; }

    /// <summary>A main road crossing a railway on the level: where the centrelines cross, the rail's heading and the
    /// road's (unit, horizontal), and Y the top of the rails -- the road's surface there stands RailProud under it.</summary>
    public struct LevelCrossing { public double X, Z; public float Y; public float RX, RZ, MX, MZ; }

    /// <summary>A crossbuck (Crossing_0) at a level crossing: its foot on the ground at (X, Y, Z), its face square to
    /// the road (DX, DZ: the road's heading), on the approaching traffic's right.</summary>
    public struct CrossingSign { public double X, Z; public float Y; public float DX, DZ; }

    /// <summary>
    /// RAILWAYS (strawberry 2026-10-10: "add railways."). Laid on their own macro grid the way the highways are -- one
    /// line per RailBand, east-west and north-south, through anchors every RailSeg -- because a railway is the thing a
    /// highway is, only more so: it wants to be straight and it cannot climb. Between two anchors a DP picks the line
    /// (stations x lateral lanes x the lane's rate of change, so the curvature is bounded as well as the drift), then the
    /// profile is smoothed and cut-and-filled to RailGrade (2.5%), and the ground is carved to it with cutting and
    /// embankment sides 1:2 (Line.Shoulders).
    ///
    /// What a railway meets:
    ///   HIGHWAYS -- built first; the rail crosses each one grade-separated, never on the level: UNDER it in a cutting
    ///               (the highway carries itself over on its own short decks, as it does over a main) or OVER it on a
    ///               viaduct, as the main roads do, and under a highway bridge that already spans it.
    ///   RAILS    -- a north-south line is built after the east-west ones and goes OVER them on a viaduct.
    ///   MAINS    -- built after rails, and treat a rail as they treat a highway (towns pushed off it, one square
    ///               crossing per link or none) -- but cross it ON THE LEVEL, at one of the rail's Sites: places the rail
    ///               chose because it is flat and at the land's own level there. The road comes up (or down) to the
    ///               rails' top; the track runs on under the road, its rail heads RailProud above the asphalt.
    ///   SMALL ROADS and TRAILS -- end at its shoulder, as at a highway's.
    ///   WATER    -- bridged like a highway's crossings (RailMaxBridgeStations), and the line runs down to the shore
    ///               where the sea is too wide.
    /// The track is cow tools' rail kit (EditorRailSpline): New_Rail_Unit tiled every RailPitch with its root on the
    /// centreline, each joint backed off by the turn, and a New_Rail_Sleeper closing an open end. Its numbers are THAT
    /// tool's, copied because core cannot see the game; L1 asserts they agree with the meshes.
    /// </summary>
    public sealed partial class InfiniteRoads
    {
        // ---- the kit: New_Rail_Unit, measured (+Y up, +Z along, root on the centreline; EditorRailSpline)
        public const float RailPitch = 2.0f;               // EditorRailSpline.Pitch
        public const float RailHalfWidth = 3.4675f;        // EditorRailSpline.HalfWidth: the ballast's foot
        public const float RailBallastFoot = -0.44f, RailBallastTop = 0.08f, RailBallastTopHalf = 2.5875f;
        public const float RailHead = 0.31f;               // the rails' top over the root
        /// <summary>The room a train needs over the rails' top and either side of the centreline: Train_Engine_0 stands
        /// 4.04 m over its wheels' tread and 1.69 m to its side; a margin on both.</summary>
        public const float RailLoadingHeight = 4.5f, RailLoadingHalf = 2.0f;
        /// <summary>The unit's root over the formation (the carved ground, profile - Bed): the ballast's foot, 0.44 m
        /// under the root, is buried 4 cm so a ground triangle never shows a sliver of daylight under it.</summary>
        public const float RailSet = 0.40f;
        public static float RailOriginY(float profileH) => profileH - Bed + RailSet;
        public static float RailHeadY(float profileH) => RailOriginY(profileH) + RailHead;
        /// <summary>At a level crossing the rail heads stand this far over the road's asphalt -- flush, as a crossing's
        /// rails are, with the rest of the unit (sleepers, ballast) buried under the road.</summary>
        public const float RailProud = 0.005f;

        // ---- the formation and the line
        /// <summary>Flat ground either side of the centreline: the ballast's foot plus one LOD0 cell's DIAGONAL (4 m grid),
        /// so every ground triangle any part of the ballast stands on has all three corners on the formation. At 5 m, a
        /// triangle under the foot reached a corner already in the shoulder's blend: 260 of 7594 feet hung over the
        /// mesh by more than 10 mm on embankments (worst 158 mm), and cuttings buried them by up to 495 mm.</summary>
        public const float RailFormationHalf = RailHalfWidth + 4f * 1.41422f + 0.1f;
        public const float RailBank = 2f;                  // a cutting's or embankment's side: 1 up in 2 across
        public const float RailMaxShoulder = 60f;
        public const float RailGrade = 0.025f;             // a main line's ruling grade, generously
        public const float RailDeckHalf = 4f;              // a rail deck's roadway: the bridge unit narrowed (DeckScale 0.5)
        /// <summary>Fill under the track (at its centre and the ballast's feet) past which an embankment becomes a
        /// viaduct. A highway's is 3 m; a railway is MADE of embankments, and bridges only what an embankment cannot.</summary>
        public const float RailRaise = 10f;
        public const float RailDeckAboveWater = 6f;        // the deck's 4 m soffit clear of the water
        // ---- the network
        public const double RailBand = 16000.0, RailSeg = 8000.0;
        const int RailStations = 40, RailLanes = 61, RailSub = 16;
        const double RailLaneStep = 50.0;
        const double RailBridgeCost = 4000.0;
        const int RailMaxBridgeStations = 8;               // 1.6 km of water; past that the line ends at the shore
        /// <summary>An anchor is never this near a highway (nor a north-south line's an east-west line): two segments meet
        /// there at ONE height, and a crossing's approach ramp -- 9.5 m at 0.92 of the grade, ~450 m with its level
        /// stretch -- must not reach it, or the two sides of the anchor are shaped apart (a 2.4 m step, before this).</summary>
        public const double RailAnchorOffHighway = 600.0;
        /// <summary>How far along its band an anchor may move from k * RailSeg.</summary>
        public const double RailAnchorSlide = 1600.0;
        /// <summary>A line keeps this far off a highway that runs its own way (a crossing one is crossed, square): a main
        /// link between the two would have to cross both, and a link crosses one thing at most.</summary>
        public const double RailKeepOffHighway = 1200.0;
        // ---- level-crossing sites
        public const double RailSiteEvery = 450.0;         // one chance per this much track
        public const double RailSiteSearch = 150.0;        // ...looked for this far either side
        public const double RailSiteEnd = 250.0;           // not this near a segment's end (the anchor joins the next)
        public const double RailSiteFlat = 16.0;           // the rail is level this far either way along itself
        public const float RailSiteRelief = 1.5f;          // natural ground within this of the formation, across the road's way
        public const double RailSiteClearOfSeparation = 450.0;

        /// <summary>UG_INF_RAILS=0: no railways.</summary>
        public static bool Rails = Environment.GetEnvironmentVariable("UG_INF_RAILS") != "0";

        readonly ConcurrentDictionary<(int, long, long), Line> _rail = new();
        readonly ConcurrentDictionary<(int, long, long), (double u, double w, float h, bool ok)> _railAnchor = new();
        /// <summary>Why each rail segment that does not exist was dropped (diagnostics; a dropped segment is two dead ends).</summary>
        public readonly ConcurrentDictionary<(int axis, long band, long k), string> RailDrops = new();
        Line RailDrop(int axis, long band, long k, string why) { RailDrops[(axis, band, k)] = why; return None; }

        double RailBandCentre(int axis, long band)
        {
            uint h = InfiniteTerrain.Hash(band, axis, _s ^ 0x7A11);
            return (band + 0.5 + 0.4 * ((h & 0xFFFF) / 65536.0 - 0.5)) * RailBand;   // +-1/5 band of jitter
        }

        /// <summary>Distance from (x, z) to the nearest highway centreline within `reach` (only axis `axis`'s, if >= 0).</summary>
        double HighwayDistance(double x, double z, double reach, int axis = -1)
        {
            var hw = new List<Line>();
            TakeHighways(x - reach, z - reach, x + reach, z + reach, hw);
            double best = double.MaxValue;
            foreach (var e in hw)
            {
                if (axis >= 0 && (int)((e.Id >> 60) & 1) != axis) continue;
                best = Math.Min(best, NearestOn(e, x, z).d);
            }
            return best;
        }

        (double u, double w, float h, bool ok) RailAnchor(int axis, long band, long k)
        {
            return _railAnchor.GetOrAdd((axis, band, k), key =>
            {
                double u0 = k * RailSeg;
                double meander = 1200.0 * InfiniteTerrain.Gradient(u0 / 11000.0, band * 2.3 + axis * 0.9, _s ^ 0x7A12);
                double w0 = RailBandCentre(axis, band) + meander;
                double best = double.MaxValue, bw = w0, bu = u0; float bh = 0f;
                // +-600 m across; the outer +-600..1200 m only when the inner ones are all wet or on a highway. And up to
                // RailAnchorSlide ALONG the band: a hill across the whole band at k * RailSeg is stepped round, not cut
                // through -- and a parallel highway and an east-west line 800 m apart leave no spot between them
                int slide = (int)(RailAnchorSlide / 200.0);
                for (int a = -slide; a <= slide; a++)
                for (int c = -8; c <= 8; c++)
                {
                    double u = u0 + a * 200.0, w = w0 + c * 150.0;
                    ToWorld(axis, u, w, out double x, out double z);
                    float h = _t.RawHeight(x, z);
                    double cost = (h < InfiniteTerrain.SeaLevel + 2f ? 1e6 : 0) + (Math.Abs(c) > 4 ? 5e5 : 0) + Math.Max(0f, h - 80f) * 20.0 + Math.Abs(c) * 15.0 + Math.Abs(a) * 10.0;
                    // ...and on ground that is level ALONG the band: the profile is pinned here, and a hill either side
                    // of a pinned point is a cutting the grade cannot climb out of
                    float relief = 0f;
                    foreach (double du in new[] { -400.0, -200.0, 200.0, 400.0 })
                    {
                        ToWorld(axis, u + du, w, out double rx, out double rz);
                        relief = Math.Max(relief, Math.Abs(_t.RawHeight(rx, rz) - h) - RailGrade * (float)Math.Abs(du));
                    }
                    cost += Math.Max(0f, relief) * 40.0;
                    // never ON a highway: an anchor is where two segments meet, heading along the band, and a highway
                    // crossing needs the rail free to shape itself either side
                    if (cost < best && HighwayDistance(x, z, RailAnchorOffHighway + 50) < RailAnchorOffHighway) cost += 2e5;
                    if (cost < best && axis == 1)
                    {
                        var ew = new List<Line>();
                        TakeRails(x - RailAnchorOffHighway - 50, z - RailAnchorOffHighway - 50, x + RailAnchorOffHighway + 50, z + RailAnchorOffHighway + 50, ew, 0);
                        foreach (var r in ew) if (NearestOn(r, x, z).d < RailAnchorOffHighway) { cost += 2e5; break; }
                    }
                    if (cost < best) { best = cost; bw = w; bu = u; bh = h; }
                }
                return (bu, bw, bh, best < 1e6);
            });
        }

        // TWO STAGES, because two segments meet at every anchor at ONE height. STAGE 1 (RailRaw, cached): the route, its
        // profile and its crossings shaped in -- which may move an end, where a crossing's approach reaches the anchor
        // (a 2.4 m step in the track, before; dropping the segment instead left a dead end). STAGE 2 (Rail): the ends
        // brought to the height the anchor's two sides agree (AnchorHeight), then the viaducts, the level-crossing sites,
        // the rounding, the decks and the tunnels. Stage 2 reads only stage 1, its own and its neighbours', so nothing
        // waits on itself; and it works on a COPY of the profile, so a raw line a neighbour reads never changes.
        readonly ConcurrentDictionary<(int, long, long), Line> _railRaw = new();
        Line RailRaw(int axis, long band, long k)
            => _railRaw.TryGetValue((axis, band, k), out var hit) ? hit : _railRaw.GetOrAdd((axis, band, k), key => { Trim(); return BuildRail(key.Item1, key.Item2, key.Item3); });

        internal Line Rail(int axis, long band, long k)
        {
            return _rail.TryGetValue((axis, band, k), out var hit) ? hit
                 : _rail.GetOrAdd((axis, band, k), key =>
                 {
                     Trim();
                     var raw = RailRaw(key.Item1, key.Item2, key.Item3);
                     if (!raw.Exists) return None;
                     var l = FinishRail(raw, key.Item1, key.Item2, key.Item3, out string why);
                     if (l == null) return RailDrop(key.Item1, key.Item2, key.Item3, why);
                     if (raw.Branches != null)
                         foreach (var rb in raw.Branches)
                             if (FinishRail(rb, key.Item1, key.Item2, key.Item3, out _) is Line fb) (l.Branches ??= new List<Line>()).Add(fb);
                     long Id(int br) => (1L << 61) | ((long)(key.Item1 & 1) << 60) | ((key.Item2 & 0xFFFFF) << 40) | ((key.Item3 & 0xFFFFFFF) << 8) | (long)br;
                     l.Id = Id(0);
                     if (l.Branches != null) for (int i = 0; i < l.Branches.Count; i++) l.Branches[i].Id = Id(i + 1);
                     return l;
                 });
        }

        /// <summary>The track's height where segments j-1 and j meet at anchor j, at (x, z): the side whose own crossing
        /// moved its end there (segment j's first), or else the anchor's own height.</summary>
        float AnchorHeight(int axis, long band, long j, double x, double z)
        {
            float baseH = Math.Max(RailAnchor(axis, band, j).h, InfiniteTerrain.SeaLevel + RailDeckAboveWater);
            foreach (long seg in new[] { j, j - 1 })
            {
                var raw = RailRaw(axis, band, seg);
                if (!raw.Exists) continue;
                foreach (var l in raw.Branches == null ? new List<Line> { raw } : new List<Line>(raw.Branches) { raw })
                    foreach (int at in new[] { 0, l.X.Length - 1 })
                        if (Math.Abs(l.X[at] - x) < 0.5 && Math.Abs(l.Z[at] - z) < 0.5 && Math.Abs(l.H[at] - baseH) > 1e-4f) return l.H[at];
            }
            return baseH;
        }

        Line BuildRail(int axis, long band, long k)
        {
            if (!Rails) return None;
            var a = RailAnchor(axis, band, k); var b = RailAnchor(axis, band, k + 1);
            if (!a.ok && !b.ok) return RailDrop(axis, band, k, "open sea");
            // the base line: a Hermite cubic through the anchors with each anchor's slope dw/du from its neighbours (the
            // anchors are not evenly spaced along the band, so the slope -- not a per-segment tangent -- is what the two
            // segments meeting there must share)
            var ap = RailAnchor(axis, band, k - 1); var an = RailAnchor(axis, band, k + 2);
            double s0 = (b.w - ap.w) / (b.u - ap.u), s1 = (an.w - a.w) / (an.u - a.u), span = b.u - a.u;
            double m0 = s0 * span, m1 = s1 * span;
            Func<double, double> baseW = t =>
            {
                double t2 = t * t, t3 = t2 * t;
                return (2 * t3 - 3 * t2 + 1) * a.w + (t3 - 2 * t2 + t) * m0 + (-2 * t3 + 3 * t2) * b.w + (t3 - t2) * m1;
            };
            string why;
            if (a.ok && b.ok)
            {
                var whole = RouteRail(a, b, axis, baseW, s0, s1, false, out why);
                if (whole != null) return whole;
                if (why != "bridge") return RailDrop(axis, band, k, why);
                var sa = RouteRail(a, b, axis, baseW, s0, s1, true, out string wa);
                var sb = RouteRail(b, a, axis, baseW, s0, s1, true, out string wb);
                if (sa == null && sb == null) return RailDrop(axis, band, k, $"water too wide to bridge, and no shore spur ({wa} / {wb})");
                var first = sa ?? sb;
                if (sa != null && sb != null) first.Branches = new List<Line> { sb };
                return first;
            }
            return RouteRail(a.ok ? a : b, a.ok ? b : a, axis, baseW, s0, s1, true, out why) ?? RailDrop(axis, band, k, why);
        }

        /// <summary>One rail segment from anchor p toward q (see Route, the highway's: the same shape of search). The DP's
        /// state is (station, lane, the lane's change from the last station), so the line's drift per station AND its
        /// change are bounded -- a heading off the band of at most 14 degrees, turning at most that much per station:
        /// curves of ~800 m radius before smoothing, where a highway's lanes may jump two at a time.</summary>
        Line RouteRail((double u, double w, float h, bool ok) p, (double u, double w, float h, bool ok) q, int axis, Func<double, double> baseW, double slope0, double slope1, bool toShore, out string why)
        {
            why = null;
            double du = (q.u - p.u) / RailStations, stepU = Math.Abs(du);
            bool fwd = du > 0;
            double BaseAt(int i) => baseW(fwd ? (double)i / RailStations : 1.0 - (double)i / RailStations);
            int N = RailStations, L = RailLanes, mid = L / 2;
            float wetBelow = InfiniteTerrain.SeaLevel + 2f;
            // the highways that run this rail's way: kept RailKeepOffHighway off (crossing ones are crossed)
            var par = new List<Line>();
            {
                double uMin = Math.Min(p.u, q.u), uMax = Math.Max(p.u, q.u), wMin = double.MaxValue, wMax = double.MinValue;
                for (int i = 0; i <= N; i++) { double w = BaseAt(i); wMin = Math.Min(wMin, w); wMax = Math.Max(wMax, w); }
                wMin -= mid * RailLaneStep + RailKeepOffHighway; wMax += mid * RailLaneStep + RailKeepOffHighway;
                ToWorld(axis, uMin, wMin, out double x0, out double z0); ToWorld(axis, uMax, wMax, out double x1, out double z1);
                var hw = new List<Line>();
                TakeHighways(Math.Min(x0, x1), Math.Min(z0, z1), Math.Max(x0, x1), Math.Max(z0, z1), hw);
                foreach (var e in hw) if ((int)((e.Id >> 60) & 1) == axis) par.Add(e);
            }
            var hgt = new float[N + 1, L];
            var node = new double[N + 1, L];
            for (int i = 0; i <= N; i++)
            {
                double u = p.u + i * du, wBase = BaseAt(i);
                for (int j = 0; j < L; j++)
                {
                    if ((i == 0 || (i == N && !toShore)) && j != mid) { hgt[i, j] = float.NaN; continue; }
                    ToWorld(axis, u, wBase + (j - mid) * RailLaneStep, out double x, out double z);
                    float h = _t.RawHeight(x, z);
                    hgt[i, j] = h;
                    double c = (h < wetBelow ? RailBridgeCost : 0) + Math.Max(0f, h - 80f) * 8.0 + Math.Abs(j - mid) * 0.2;
                    foreach (var e in par)
                    {
                        if (x < e.MinX - RailKeepOffHighway || x > e.MaxX + RailKeepOffHighway || z < e.MinZ - RailKeepOffHighway || z > e.MaxZ + RailKeepOffHighway) continue;
                        double d = NearestOn(e, x, z).d;
                        if (d < RailKeepOffHighway) { double f = 1 - d / RailKeepOffHighway; c += 1500 * f * f; }
                    }
                    node[i, j] = c;
                }
            }
            // states (lane, change): change d - D in -D..+D lanes from the last station, and a change may move by at
            // most one from the last one -- the drift is bounded by D, the curvature by that one
            const int D = 2, ND = 2 * D + 1;
            var cost = new double[N + 1, L, ND];
            var from = new int[N + 1, L, ND];
            for (int j = 0; j < L; j++) for (int d = 0; d < ND; d++) cost[0, j, d] = j == mid && d == D ? 0 : double.MaxValue;
            int end = 0;
            for (int i = 1; i <= N; i++)
            {
                bool reached = false;
                for (int j = 0; j < L; j++)
                {
                    for (int d = 0; d < ND; d++) cost[i, j, d] = double.MaxValue;
                    float h = hgt[i, j];
                    if (float.IsNaN(h)) continue;
                    bool wet = h < wetBelow;
                    if (wet && toShore) continue;
                    for (int d = 0; d < ND; d++)
                    {
                        int jp = j - (d - D);
                        if (jp < 0 || jp >= L) continue;
                        double run = Math.Sqrt(stepU * stepU + ((d - D) * RailLaneStep) * ((d - D) * RailLaneStep));
                        double grade = Math.Abs(h - hgt[i - 1, jp]) / run * 100.0;
                        // past the ruling grade the profile can only cut or fill, so the land's grade there is dear
                        double steep = Math.Max(0.0, grade - RailGrade * 100.0);
                        double edge = node[i, j] + grade * grade * 1.2 + steep * steep * 25.0 + (d - D) * (d - D) * 2.0;
                        for (int dp = Math.Max(0, d - 1); dp <= Math.Min(ND - 1, d + 1); dp++)
                        {
                            double c0 = cost[i - 1, jp, dp];
                            if (c0 == double.MaxValue) continue;
                            double c = c0 + edge + (d - dp) * (d - dp) * 10.0;
                            if (c < cost[i, j, d]) { cost[i, j, d] = c; from[i, j, d] = jp * ND + dp; reached = true; }
                        }
                    }
                }
                if (!reached) break;
                end = i;
            }
            int last = mid, lastD = D;
            {
                double bc = double.MaxValue;
                if (toShore)
                {
                    if (end < 5) { why = "shore within 1 km of the anchor"; return null; }
                    for (int j = 0; j < L; j++) for (int d = 0; d < ND; d++) if (cost[end, j, d] < bc) { bc = cost[end, j, d]; last = j; lastD = d; }
                }
                else
                {
                    if (end < N) { why = "no route"; return null; }
                    for (int d = 0; d < ND; d++) if (cost[N, mid, d] < bc) { bc = cost[N, mid, d]; lastD = d; }
                    if (bc == double.MaxValue) { why = "no route"; return null; }
                }
            }
            var lane = new int[end + 1];
            {
                int j = last, d = lastD;
                lane[end] = j;
                for (int i = end; i > 0; i--) { int f = from[i, j, d]; j = f / ND; d = f % ND; lane[i - 1] = j; }
            }
            int wetRun = 0, longestBridge = 0;
            for (int i = 0; i <= end; i++) { wetRun = hgt[i, lane[i]] < wetBelow ? wetRun + 1 : 0; longestBridge = Math.Max(longestBridge, wetRun); }
            if (longestBridge > RailMaxBridgeStations) { why = "bridge"; return null; }
            var xs = new double[end + 1]; var zs = new double[end + 1]; var hs = new float[end + 1];
            var lat = new double[end + 1];
            for (int i = 0; i <= end; i++) lat[i] = (lane[i] - mid) * RailLaneStep;
            for (int pass = 0; pass < 8; pass++)
            {
                var tmp = (double[])lat.Clone();
                for (int i = 1; i < end; i++) tmp[i] = 0.25 * lat[i - 1] + 0.5 * lat[i] + 0.25 * lat[i + 1];
                lat = tmp;
            }
            for (int i = 0; i <= end; i++)
            {
                double fromEnd = toShore ? i : Math.Min(i, end - i);
                double f = Math.Clamp(fromEnd / 4.0, 0.0, 1.0);
                lat[i] *= f * f * (3 - 2 * f);
            }
            float deckFloor = InfiniteTerrain.SeaLevel + RailDeckAboveWater;
            for (int i = 0; i <= end; i++)
            {
                double u = p.u + i * du, w = BaseAt(i) + lat[i];
                ToWorld(axis, u, w, out xs[i], out zs[i]);
                hs[i] = i == 0 ? p.h : i == N ? q.h : _t.RawHeight(xs[i], zs[i]);
                hs[i] = Math.Max(hs[i], deckFloor);
            }
            Smooth(hs, 10);
            hs[0] = Math.Max(p.h, deckFloor);
            if (!toShore) hs[end] = Math.Max(q.h, deckFloor);
            float maxRise = RailGrade * (float)stepU;
            for (int rep = 0; rep < 6; rep++)
            {
                for (int i = 1; i < end || (toShore && i == end); i++) hs[i] = Math.Clamp(hs[i], hs[i - 1] - maxRise, hs[i - 1] + maxRise);
                for (int i = end - 1; i > 0; i--) hs[i] = Math.Clamp(hs[i], hs[i + 1] - maxRise, hs[i + 1] + maxRise);
            }
            for (int i = 0; i < end; i++) if (Math.Abs(hs[i + 1] - hs[i]) > maxRise * 1.001f) { why = $"grade unfixable at station {i}"; return null; }
            (double, double) Heading(double dwdt)
            {
                double tu = fwd ? 1.0 : -1.0, tw = fwd ? dwdt : -dwdt, l = Math.Sqrt(tu * tu + tw * tw);   // dwdt: dw/du here
                ToWorld(axis, tu / l, tw / l, out double tx, out double tz);
                return (tx, tz);
            }
            var startT = Heading(fwd ? slope0 : slope1);
            (double, double)? endT = toShore ? null : Heading(fwd ? slope1 : slope0);
            var line = Finish(RoadKind.Rail, xs, zs, hs, startT, endT, RailSub);
            if (line == null) { why = "drawn curve over grade"; return null; }
            // VERTICAL CURVES: the profile is linear between stations 200 m apart, a kink of up to 5% at each; averaged
            // over the dense points it rounds into a curve tens of metres long. Averaging cannot steepen a grade-limited
            // run on even spacing; Catmull-Rom's is not quite even, so it is checked, and dropped if it did.
            {
                var hOld = (float[])line.H.Clone();
                Smooth(line.H, 12);
                var arcD = Arc(line);
                for (int i = 0; i < line.Segments; i++)
                    if (Math.Abs(line.H[i + 1] - line.H[i]) / Math.Max(1e-9, arcD[i + 1] - arcD[i]) > RailGrade * 1.0001f) { line.H = hOld; break; }
            }
            if (!ShapeRail(line, axis, out why)) return null;
            return line;
        }

        /// <summary>One crossing as stage 1 shaped it: level at Target for Half either side of arc AX, Over (the rail
        /// above the other line) or under; Deck: the rail carries itself over on a viaduct (not over a highway's tunnel,
        /// where it simply stays over the hill).</summary>
        internal struct RailCross { public double AX, Half; public float Target; public bool Over, Deck; }

        /// <summary>STAGE 1: grade-separate a rail from everything built before it (highways; for a north-south line, the
        /// east-west lines) and shape its profile through each crossing, laying the highways' decks where it goes under.
        /// False (and why) if a crossing cannot be made.</summary>
        bool ShapeRail(Line e, int axis, out string why)
        {
            why = null;
            var others = RailOthers(e, axis);
            var arc = Arc(e);
            double spacing = MaxSpacing(arc);
            var plan = new List<(Line o, double f, double g, double x, double z, double sin, double aX, float target, bool over, bool bridged, float oH, double half, bool deck)>();
            foreach (var o in others)
                foreach (var c in Crossings(e.X, e.Z, e.X.Length, o))
                {
                    if (c.angle < 10) { why = o.Kind == RoadKind.Rail ? "rail crossing under 10 degrees" : "highway crossing under 10 degrees"; return false; }
                    int jk = Math.Min((int)c.g, o.Segments - 1);
                    float oH = o.H[jk] + (o.H[jk + 1] - o.H[jk]) * (float)(c.g - jk);
                    double sin = Math.Sin(c.angle * Math.PI / 180);
                    int fk = Math.Min((int)c.f, e.Segments - 1);
                    double aX = arc[fk] + (arc[fk + 1] - arc[fk]) * (c.f - fk);
                    float hereH = e.H[fk] + (e.H[fk + 1] - e.H[fk]) * (float)(c.f - fk);
                    double half = (PavedHalf(o.Kind) + UnderMargin) / sin;    // the other's width along the rail
                    // OVER A TUNNEL (a highway's, or an east-west line's) the rail stays over the hill: its formation (and
                    // its own carve) at least TunnelCover over the bores' shells, no deck of its own
                    if (Tunnels && o.TunnelSpans != null)
                    {
                        double along = o.HArc[jk] + (o.HArc[jk + 1] - o.HArc[jk]) * (c.g - jk);
                        bool bored = false;
                        foreach (var tn in o.TunnelSpans) if (along >= tn.A0 - TunnelForecourt - 10 && along <= tn.A1 + TunnelForecourt + 10) bored = true;
                        if (bored)
                        {
                            float over = TunnelSurfaceY(o.Kind, oH) + ShellTopOf(o.Kind, TubeOffset(o.Kind, 0)) + TunnelCover + Bed + 1f;
                            plan.Add((o, c.f, c.g, c.x, c.z, sin, aX, over, true, false, oH, (ShellReachOf(o.Kind) + UnderMargin) / sin, false));
                            continue;
                        }
                    }
                    // under the other's OWN deck only if it spans the whole formation and margin -- both carriageways';
                    // at a deck's END (part spanned, part embankment) the rail goes over instead
                    int covered = 0;
                    if (Bridges && o.DeckCover != null)
                    {
                        double seg = Math.Sqrt((o.X[jk + 1] - o.X[jk]) * (o.X[jk + 1] - o.X[jk]) + (o.Z[jk + 1] - o.Z[jk]) * (o.Z[jk + 1] - o.Z[jk]));
                        double reach = (RailFormationHalf + UnderMargin) / sin / Math.Max(1e-6, seg);
                        for (int s2 = 0; s2 < 2; s2++)
                            for (int qq = -4; qq <= 4; qq++) if (Covered(o.DeckCover[s2], c.g + reach * qq / 4.0)) covered++;
                    }
                    bool bridged = covered == 18;
                    var (lo, hi) = ProfileRange(o, c.g, (RailFormationHalf + UnderMargin) / sin);
                    // OVER a rail always (its decks are its own, built before); over a highway where the rail already
                    // stands above it, going under would flood, the crossing is at a bridge's end, or it is too skewed for
                    // the highway's own short spans (LayUnderpass takes down to 30 degrees); under a deck that spans it
                    bool goOver = !bridged && (o.Kind == RoadKind.Rail || hereH > oH || lo - UnderDepth < InfiniteTerrain.SeaLevel + 1.2f || covered > 0 || c.angle < 30);
                    if (!goOver && lo - UnderDepth < InfiniteTerrain.SeaLevel + 1.2f) { why = "under a bridge by the sea"; return false; }
                    float target = goOver ? hi + OverRise : lo - UnderDepth;
                    plan.Add((o, c.f, c.g, c.x, c.z, sin, aX, target, goOver, bridged, oH, half, goOver));
                }
            // One over and one under close together pull against each other -- 19 m of rise between them takes 760 m at
            // the grade -- so an under-crossing within reach of an over-crossing goes over too (a viaduct carries the rail
            // over both), and only a crossing that still fails drops the segment.
            for (int pass = 0; pass < 3; pass++)
                for (int a2 = 0; a2 < plan.Count; a2++)
                {
                    var pa = plan[a2];
                    if (pa.over || pa.bridged) continue;
                    foreach (var pb in plan)
                        if (pb.over && Math.Abs(pb.aX - pa.aX) - pa.half - pb.half < (pb.target - pa.target) / (0.92 * RailGrade) + 40)
                        {
                            var (_, hi2) = ProfileRange(pa.o, pa.g, (RailFormationHalf + UnderMargin) / pa.sin);
                            plan[a2] = pa with { over = true, target = hi2 + OverRise, deck = true };
                            break;
                        }
                }
            foreach (var pl in plan)
                ShapeCrossing(e.H, arc, pl.aX, pl.target, pl.over, pl.half + spacing, 0.92f * RailGrade);
            e.Plan = new List<RailCross>();
            foreach (var pl in plan) e.Plan.Add(new RailCross { AX = pl.aX, Half = pl.half, Target = pl.target, Over = pl.over, Deck = pl.deck });
            if (!PlanHolds(e, arc, out why)) return false;
            // the highways' decks over the rail where it goes under
            if (plan.Count > 0) e.Underpasses = new List<Underpass>();
            foreach (var pl in plan)
            {
                var up = new Underpass { HwId = pl.o.Id, X = pl.x, Z = pl.z, Over = pl.over, Existing = pl.bridged, OverTunnel = pl.over && !pl.deck,
                                         HwSurface = SurfaceY(pl.o.Kind, pl.oH), MainSurface = SurfaceY(RoadKind.Rail, pl.target) };
                if (!pl.bridged && !pl.over && !LayUnderpass(e, pl.o, up)) { why = "underpass decks"; return false; }
                e.Underpasses.Add(up);
            }
            return true;
        }

        /// <summary>Highways (and, for a north-south line, the east-west lines) the rail's box touches.</summary>
        List<Line> RailOthers(Line e, int axis)
        {
            var others = new List<Line>();
            TakeHighways(e.MinX, e.MinZ, e.MaxX, e.MaxZ, others);
            if (axis == 1) TakeRails(e.MinX, e.MinZ, e.MaxX, e.MaxZ, others, 0);
            return others;
        }
        static double MaxSpacing(double[] arc) { double sp = 0; for (int i = 0; i + 1 < arc.Length; i++) sp = Math.Max(sp, arc[i + 1] - arc[i]); return sp; }

        /// <summary>Every crossing's clearance still holds -- one-sided: a rail pulled LOWER under a highway (or higher
        /// over one) by the next crossing's approach, or an anchor's, only clears it by more -- and nothing is under the sea.</summary>
        static bool PlanHolds(Line e, double[] arc, out string why)
        {
            why = null;
            if (e.Plan != null)
                foreach (var pl in e.Plan)
                    for (int i = 0; i < e.X.Length; i++)
                        if (Math.Abs(arc[i] - pl.AX) <= pl.Half && (pl.Over ? e.H[i] < pl.Target - 0.01f : e.H[i] > pl.Target + 0.01f)) { why = "crossings too close together"; return false; }
            foreach (var h in e.H) if (h < InfiniteTerrain.SeaLevel + 1.2f) { why = "cutting below the sea"; return false; }
            return true;
        }

        /// <summary>STAGE 2, on a copy of a stage-1 line: its ends brought to their anchors' agreed heights (ramped in at
        /// 0.92 of the grade), its crossings re-checked, then its viaducts, level-crossing sites, rounding, decks, tunnels
        /// and shoulders.</summary>
        Line FinishRail(Line raw, int axis, long band, long k, out string why)
        {
            var e = new Line { Kind = RoadKind.Rail, X = raw.X, Z = raw.Z, H = (float[])raw.H.Clone(), Underpasses = raw.Underpasses, Plan = raw.Plan,
                               CMinX = raw.CMinX, CMaxX = raw.CMaxX, CMinZ = raw.CMinZ, CMaxZ = raw.CMaxZ, MinX = raw.MinX, MaxX = raw.MaxX, MinZ = raw.MinZ, MaxZ = raw.MaxZ };
            var arc = Arc(e);
            double spacing = MaxSpacing(arc), total = arc[arc.Length - 1];
            int n = e.X.Length;
            // THE ANCHORS: each end that is at one of this segment's anchors (not a shore) takes the height its two sides agree
            foreach (long j in new[] { k, k + 1 })
            {
                var an = RailAnchor(axis, band, j);
                ToWorld(axis, an.u, an.w, out double ax, out double az);
                foreach (int at in new[] { 0, n - 1 })
                {
                    if (Math.Abs(e.X[at] - ax) > 0.5 || Math.Abs(e.Z[at] - az) > 0.5) continue;
                    float J = AnchorHeight(axis, band, j, ax, az);
                    if (Math.Abs(J - e.H[at]) <= 1e-4f) continue;
                    float g = 0.92f * RailGrade;
                    for (int i = 0; i < n; i++)
                    {
                        float d = (float)(at == 0 ? arc[i] : total - arc[i]);
                        e.H[i] = Math.Clamp(e.H[i], J - g * d, J + g * d);
                    }
                }
            }
            if (!PlanHolds(e, arc, out why)) { why = "crossings either side of an anchor"; return null; }
            var others = RailOthers(e, axis);
            // BRIDGES: wherever the fill would be a viaduct's, over water, and over everything the rail goes OVER on a deck
            // -- one walk, so a crossing on a viaduct's approach is part of the viaduct rather than a second deck beside it.
            // The forced run reaches a deck unit and a dense point past the other's width and margin: a deck is whole
            // units from its stretch's first point, so it can end up to a unit short of the stretch's last one.
            var forced = new List<(double a0, double a1)>();
            if (e.Plan != null) foreach (var pl in e.Plan) if (pl.Deck) forced.Add((pl.AX - pl.Half - spacing - BridgePitch - 2, pl.AX + pl.Half + spacing + BridgePitch + 2));
            MarkRailStretches(e, arc, forced);
            RailSites(e);
            // VERTICAL CURVES once more: the crossings', the anchors' and the sites' shaping left kinks of up to 5% in the
            // grade, and the ground mesh, linear over 4 m cells, cuts across a kink -- the ballast stood 18 mm off it
            // there. Rounded with every level stretch PINNED, so a crossing's clearance and a site's level are exactly
            // what they were (and the two ends, which are the anchors).
            // Pinned is only what must be level -- a crossing's span, a site's road -- and the first point past it each
            // side (the profile is linear between points); pinning the whole flattened run kept its edges' kinks (a site
            // may sit on 1.2%), and the ballast stood 9.5 mm off the mesh there.
            {
                var pins = new List<(double a0, double a1)>();
                if (e.Plan != null) foreach (var pl in e.Plan) pins.Add((pl.AX - pl.Half, pl.AX + pl.Half));
                double road = (PavedHalf(RoadKind.Main) + 1) / Math.Sin(60 * Math.PI / 180);   // a main crosses at 60 degrees or more
                foreach (var st in e.Sites) pins.Add((st.A - road, st.A + road));
                RoundRail(e, arc, pins);
            }
            if (Bridges)
            {
                Func<double, double, bool> pierOk = (x, z) => others.Count == 0 || Influence(others, x, z).Clear > 2f;
                e.BridgePieces = new List<BridgePiece>();
                foreach (var st in e.Raised) WalkBridge(e, st, e.BridgePieces, pierOk);
            }
            // TUNNELS where the hill buries the whole (single, narrower) bore's shell for two portals' length -- never
            // through a grade separation (its decks are over open track) nor up to a bridge's end
            if (Tunnels)
            {
                var near = new List<double>();
                if (e.Plan != null) foreach (var pl in e.Plan) near.Add(pl.AX);
                bool KeepOpen(double s)
                {
                    foreach (var a0 in near) if (Math.Abs(s - a0) < RailTunnelClearOfSeparation) return true;
                    if (e.DeckCover != null)
                    {
                        double f = FracAt(arc, s), seg = Math.Max(1e-6, arc[Math.Min(arc.Length - 1, (int)f + 1)] - arc[(int)f]);
                        if (Covered(e.DeckCover[0], f, -40.0 / seg)) return true;
                    }
                    return false;
                }
                FindTunnels(e, KeepOpen);
            }
            RailShoulders(e);
            Rebox(e);
            return e;
        }

        public const double RailTunnelClearOfSeparation = 150.0;

        /// <summary>Where the rail's viaducts go: dense points whose fill (the least under its centre and the ballast's
        /// feet) is RailRaise or more, or that are over water, or `forced` -- run into Stretches (merged, short ones
        /// dropped). Walked as bridges once the profile is final.</summary>
        void MarkRailStretches(Line e, double[] arc, List<(double a0, double a1)> forced)
        {
            int n = e.X.Length;
            var on = new bool[n]; var fill = new float[n]; var wet = new bool[n];
            for (int i = 0; i < n; i++)
            {
                var (tx, tz) = RibbonTangent(e, i);
                float ground = e.H[i] - Bed, f = float.MaxValue; bool w = false;
                for (int s = -1; s <= 1; s++)
                {
                    float r = _t.RawHeight(e.X[i] - tz * s * RailHalfWidth, e.Z[i] + tx * s * RailHalfWidth);
                    f = Math.Min(f, ground - r);
                    if (r < InfiniteTerrain.SeaLevel) w = true;
                }
                fill[i] = f; wet[i] = w;
                on[i] = f >= RailRaise || w;
                foreach (var (a0, a1) in forced) if (arc[i] >= a0 && arc[i] <= a1) on[i] = true;
            }
            (e.Raised, _) = Stretches(e, arc, on, fill, wet, RaiseMinLength, RaiseMergeGap, 0);
        }

        /// <summary>Round the profile's kinks (Smooth's 1-2-1 average, RailRoundPasses times) everywhere but the pinned
        /// arc ranges and the two ends (the anchors the next segments share); kept only if no grade grew past RailGrade.</summary>
        static void RoundRail(Line e, double[] arc, List<(double a0, double a1)> pins)
        {
            int n = e.X.Length;
            var pinned = new bool[n];
            foreach (var (a0, a1) in pins)
            {
                int lo = -1, hi = -1;
                for (int i = 0; i < n; i++) if (arc[i] >= a0 && arc[i] <= a1) { pinned[i] = true; if (lo < 0) lo = i; hi = i; }
                // ...and the first point beyond each side: between a pinned point and an unpinned one the line is not level
                if (lo < 0) { lo = hi = Math.Clamp((int)FracAt(arc, 0.5 * (a0 + a1)), 0, n - 1); pinned[lo] = true; }
                if (lo > 0) pinned[lo - 1] = true;
                if (hi < n - 1) pinned[hi + 1] = true;
            }
            var old = (float[])e.H.Clone();
            var tmp = new float[n];
            for (int pass = 0; pass < RailRoundPasses; pass++)
            {
                tmp[0] = e.H[0]; tmp[n - 1] = e.H[n - 1];
                for (int i = 1; i < n - 1; i++) tmp[i] = pinned[i] ? e.H[i] : 0.25f * e.H[i - 1] + 0.5f * e.H[i] + 0.25f * e.H[i + 1];
                Array.Copy(tmp, e.H, n);
            }
            for (int i = 0; i + 1 < n; i++)
                if (Math.Abs(e.H[i + 1] - e.H[i]) / Math.Max(1e-9, arc[i + 1] - arc[i]) > RailGrade * 1.0001f) { Array.Copy(old, e.H, n); return; }
        }
        const int RailRoundPasses = 8;

        /// <summary>Where a main may cross on the level: one spot per RailSiteEvery of track, the best within
        /// RailSiteSearch -- natural ground within RailSiteRelief of the formation across the road's way, the rail
        /// nearly level -- clear of grade separations, bridges and the segment's ends. The rail is then made LEVEL there
        /// for RailSiteFlat either way (and its grade restored either side), so a road crossing it lies flat on the
        /// rails across its whole width.</summary>
        void RailSites(Line e)
        {
            var arc = Arc(e);
            double total = arc[arc.Length - 1];
            var sites = new List<RailSite>();
            double prev = double.MinValue;
            for (double s0 = RailSiteEnd + RailSiteSearch; s0 <= total - RailSiteEnd - RailSiteSearch; s0 += RailSiteEvery)
            {
                double bestS = -1, bestScore = double.MaxValue;
                for (double s = s0 - RailSiteSearch; s <= s0 + RailSiteSearch; s += 10)
                {
                    if (s - prev < RailSiteEvery * 0.6) continue;
                    At(e, arc, s, out double x, out double z, out float h, out double tx, out double tz);
                    bool clear = true;
                    if (e.Underpasses != null)
                        foreach (var u in e.Underpasses)
                            if ((u.X - x) * (u.X - x) + (u.Z - z) * (u.Z - z) < RailSiteClearOfSeparation * RailSiteClearOfSeparation) clear = false;
                    if (!clear) continue;
                    // nor within 80 m of where a viaduct will stand
                    bool nearDeck = false;
                    if (e.Raised != null) foreach (var st in e.Raised) if (s > arc[st.I0] - 80 && s < arc[st.I1] + 80) nearDeck = true;
                    if (nearDeck) continue;
                    At(e, arc, s - 25, out _, out _, out float hA, out _, out _);
                    At(e, arc, s + 25, out _, out _, out float hB, out _, out _);
                    double grade = Math.Abs(hB - hA) / 50.0;
                    if (grade > 0.012) continue;
                    // the road's way across: the land within RailSiteRelief of the formation out to the road's own
                    // approach, so it neither climbs onto an embankment nor dives into a cutting to reach the rails
                    float form = h - Bed, worst = 0f;
                    foreach (double o in new[] { -40.0, -20.0, -8.0, 0.0, 8.0, 20.0, 40.0 })
                    {
                        float r = _t.RawHeight(x - tz * o, z + tx * o);
                        if (r < InfiniteTerrain.SeaLevel + 1.5f) { worst = float.MaxValue; break; }
                        worst = Math.Max(worst, Math.Abs(r - form));
                    }
                    if (worst > RailSiteRelief) continue;
                    double score = worst + 100 * grade;
                    if (score < bestScore) { bestScore = score; bestS = s; }
                }
                if (bestS < 0) continue;
                At(e, arc, bestS, out double sx, out double sz, out float sh, out double stx, out double stz);
                sites.Add(new RailSite { A = bestS, F = FracAt(arc, bestS), X = sx, Z = sz, H = sh, TX = stx, TZ = stz });
                prev = bestS;
            }
            // level at each site, out to the first dense point past RailSiteFlat (the profile is linear between points),
            // then the grade restored outward from the level stretch
            double spacing = 0;
            for (int i = 0; i < e.Segments; i++) spacing = Math.Max(spacing, arc[i + 1] - arc[i]);
            foreach (var st in sites)
            {
                int lo = -1, hi = -1;
                for (int i = 0; i < e.X.Length; i++)
                    if (Math.Abs(arc[i] - st.A) <= RailSiteFlat + spacing) { e.H[i] = st.H; if (lo < 0) lo = i; hi = i; }
                if (lo < 0) continue;
                for (int i = hi + 1; i < e.X.Length; i++)
                {
                    float g = RailGrade * (float)(arc[i] - arc[i - 1]);
                    float c = Math.Clamp(e.H[i], e.H[i - 1] - g, e.H[i - 1] + g);
                    if (c == e.H[i]) break;
                    e.H[i] = c;
                }
                for (int i = lo - 1; i >= 0; i--)
                {
                    float g = RailGrade * (float)(arc[i + 1] - arc[i]);
                    float c = Math.Clamp(e.H[i], e.H[i + 1] - g, e.H[i + 1] + g);
                    if (c == e.H[i]) break;
                    e.H[i] = c;
                }
            }
            e.Sites = sites;
        }

        /// <summary>The carve's shoulder at each dense point: RailBank x the depth of the cutting or the height of the
        /// embankment there (1:2 sides), at least Shoulder(Rail), at most RailMaxShoulder -- spread along the line a
        /// little, so a side does not step in and out with the ground's every bump.</summary>
        void RailShoulders(Line e)
        {
            int n = e.X.Length;
            var depth = new float[n];
            for (int i = 0; i < n; i++) depth[i] = InTunnel(e, Math.Min(i, n - 2), i == n - 1 ? 1 : 0) ? 0f : Math.Abs(e.H[i] - Bed - _t.RawHeight(e.X[i], e.Z[i]));   // a bore needs no sides
            var sh = new float[n];
            for (int i = 0; i < n; i++)
            {
                float d = 0f;
                for (int j = Math.Max(0, i - 3); j <= Math.Min(n - 1, i + 3); j++) d = Math.Max(d, depth[j]);
                sh[i] = Math.Clamp(RailBank * d, Shoulder(RoadKind.Rail), RailMaxShoulder);
            }
            Smooth(sh, 3);
            e.Shoulders = sh;
        }

        /// <summary>Rail lines (both sides of a water gap) whose bounds touch the rectangle; only axis `onlyAxis`'s if
        /// it is 0 or 1.</summary>
        void TakeRails(double x0, double z0, double x1, double z1, List<Line> list, int onlyAxis = -1)
        {
            if (!Rails) return;
            void Take(Line e) { if (e.Exists && e.MaxX >= x0 && e.MinX <= x1 && e.MaxZ >= z0 && e.MinZ <= z1) list.Add(e); }
            // band jitter + meander + anchor pick + DP lane + the widest shoulder -- then from the band's actual centre
            // (see TakeHighways), so a query never builds a line that cannot reach it
            const double fromCentre = 1200 + 1200 + 1500 + RailMaxShoulder + 40, reach = 0.2 * RailBand + fromCentre;
            for (int axis = 0; axis < 2; axis++)
            {
                if (onlyAxis >= 0 && axis != onlyAxis) continue;
                double w0 = axis == 0 ? z0 : x0, w1 = axis == 0 ? z1 : x1, u0 = axis == 0 ? x0 : z0, u1 = axis == 0 ? x1 : z1;
                long b0 = (long)Math.Floor((w0 - reach) / RailBand), b1 = (long)Math.Floor((w1 + reach) / RailBand);
                // an anchor may sit up to RailAnchorSlide along the band from k * RailSeg
                long k0 = (long)Math.Floor((u0 - RailAnchorSlide - 50) / RailSeg) - 1, k1 = (long)Math.Floor((u1 + RailAnchorSlide + 50) / RailSeg);
                for (long band = b0; band <= b1; band++)
                {
                    double c = RailBandCentre(axis, band);
                    if (c + fromCentre < w0 || c - fromCentre > w1) continue;
                    for (long k = k0; k <= k1; k++)
                    {
                        var e = Rail(axis, band, k);
                        Take(e);
                        if (e.Branches != null) foreach (var br in e.Branches) Take(br);
                    }
                }
            }
        }

        // =============================================================================================================
        // The track

        /// <summary>The line's track, laid once: New_Rail_Units from its start, each rooted at the profile's RailOriginY
        /// and pointing at the point RailPitch on (3D arc), each joint backed off by what its turn opens at the unit's
        /// outside corner (across: the ballast's foot, RailHalfWidth; up and down: the ballast's foot at a sag, the
        /// rails' top at a crest) -- EditorRailSpline's walk. Then every unit is stretched by the same K so the last one
        /// ends EXACTLY at the line's end, where the next segment's first unit starts: no gap, no overlap at an anchor.
        /// A terminal sleeper closes an end no other rail line starts or ends at.</summary>
        public List<RailPiece> TrackOf(Line e)
        {
            var have = e.Track;
            if (have != null) return have;
            int n = e.X.Length;
            var ys = new double[n]; var a3 = new double[n];
            for (int i = 0; i < n; i++) ys[i] = RailOriginY(e.H[i]);
            for (int i = 1; i < n; i++)
            {
                double dx = e.X[i] - e.X[i - 1], dy = ys[i] - ys[i - 1], dz = e.Z[i] - e.Z[i - 1];
                a3[i] = a3[i - 1] + Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }
            double total = a3[n - 1];
            // the line, by 3D arc; past either end it runs straight on (the fit below may ask a hair past the end)
            (double x, double y, double z) P(double s)
            {
                int lo, hi;
                if (s <= 0) { lo = 0; hi = 1; }
                else if (s >= total) { lo = n - 2; hi = n - 1; }
                else { lo = 0; hi = n - 1; while (hi - lo > 1) { int m = (lo + hi) >> 1; if (a3[m] <= s) lo = m; else hi = m; } }
                double t = (s - a3[lo]) / Math.Max(1e-9, a3[hi] - a3[lo]);
                return (e.X[lo] + (e.X[hi] - e.X[lo]) * t, ys[lo] + (ys[hi] - ys[lo]) * t, e.Z[lo] + (e.Z[hi] - e.Z[lo]) * t);
            }
            static (double x, double y, double z) Dir((double x, double y, double z) p, (double x, double y, double z) q)
            {
                double dx = q.x - p.x, dy = q.y - p.y, dz = q.z - p.z, l = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                return l < 1e-9 ? (1, 0, 0) : (dx / l, dy / l, dz / l);
            }
            var list = new List<RailPiece>();
            // one walk: tiles of RailPitch * k, as many as fit (count < 0) or exactly `count`; returns where the last ends
            double Walk(double k, int count)
            {
                list.Clear();
                double pitch = RailPitch * k, s = 0, endOfLast = 0;
                while (count < 0 ? s + pitch <= total + 1e-6 : list.Count < count)
                {
                    var p0 = P(s); var p1 = P(s + pitch);
                    var dir = Dir(p0, p1);
                    list.Add(new RailPiece { Kind = 0, X = p0.x, Y = (float)p0.y, Z = p0.z, DX = (float)dir.x, DY = (float)dir.y, DZ = (float)dir.z, K = (float)k });
                    endOfLast = s + pitch;
                    var nd = Dir(P(s + pitch), P(s + 2 * pitch));
                    double hl = Math.Sqrt(dir.x * dir.x + dir.z * dir.z), nl = Math.Sqrt(nd.x * nd.x + nd.z * nd.z);
                    double turn = hl > 1e-9 && nl > 1e-9 ? Math.Acos(Math.Clamp((dir.x * nd.x + dir.z * nd.z) / (hl * nl), -1.0, 1.0)) : 0;
                    double rise = Math.Asin(Math.Clamp(nd.y, -1.0, 1.0)) - Math.Asin(Math.Clamp(dir.y, -1.0, 1.0));
                    double lever = rise > 0 ? -RailBallastFoot : RailHead;
                    double overlap = Math.Min(RailHalfWidth * turn + lever * Math.Abs(rise), pitch * 0.5);
                    s += pitch - overlap;
                }
                return endOfLast;
            }
            // as many whole tiles as fit, then the stretch k that makes exactly that many end AT the line's end: a secant
            // on k (the end moves smoothly with it once the count is fixed -- letting the count float too made the fit
            // jump between n and n-1 tiles and leave a whole pitch open at the anchor)
            int units = 0;
            {
                double e0 = Walk(1.0, -1), k0 = 1.0;
                units = list.Count;
                if (units > 0 && Math.Abs(total - e0) > 1e-6)
                {
                    double k1 = total / Math.Max(1e-9, e0), e1 = Walk(k1, units);
                    // (the step's denominator is SIGNED: a step back past the root has e1 < e0, and clamping that to a tiny
                    // positive number sent k to minus infinity from 3 microns out)
                    for (int it = 0; it < 8 && Math.Abs(total - e1) > 1e-6; it++)
                    {
                        double de = e1 - e0;
                        if (Math.Abs(de) < 1e-12) break;
                        double k2 = k1 + (total - e1) * (k1 - k0) / de;
                        k0 = k1; e0 = e1; k1 = k2; e1 = Walk(k1, units);
                    }
                }
            }
            if (list.Count > 0)
            {
                var lastU = list[list.Count - 1];
                double ex = e.X[n - 1], ez = e.Z[n - 1];
                if (!RailMeetsAnother(e, ex, ez))
                    list.Add(new RailPiece { Kind = 1, X = lastU.X + lastU.DX * RailPitch * lastU.K, Y = lastU.Y + lastU.DY * RailPitch * lastU.K, Z = lastU.Z + lastU.DZ * RailPitch * lastU.K,
                                             DX = lastU.DX, DY = lastU.DY, DZ = lastU.DZ, K = 1f });
            }
            e.Track = list;
            return list;
        }

        /// <summary>Does another rail line (this one's segment neighbours, either side of a water gap) start or end at
        /// (x, z)? Then the track runs on there, and needs no closing sleeper.</summary>
        bool RailMeetsAnother(Line e, double x, double z)
        {
            var near = new List<Line>();
            TakeRails(x - 1, z - 1, x + 1, z + 1, near);
            foreach (var o in near)
            {
                if (o == e || !o.Exists) continue;
                int m = o.X.Length - 1;
                if ((o.X[0] - x) * (o.X[0] - x) + (o.Z[0] - z) * (o.Z[0] - z) < 0.25) return true;
                if ((o.X[m] - x) * (o.X[m] - x) + (o.Z[m] - z) * (o.Z[m] - z) < 0.25) return true;
            }
            return false;
        }

        /// <summary>The track pieces whose root lies in the rectangle (each belongs to exactly one region).</summary>
        public List<RailPiece> RailsIn(List<Line> lines, double x0, double z0, double x1, double z1)
        {
            var list = new List<RailPiece>();
            foreach (var e in lines)
            {
                if (e.Kind != RoadKind.Rail || e.MaxX < x0 || e.MinX > x1 || e.MaxZ < z0 || e.MinZ > z1) continue;
                foreach (var p in TrackOf(e))
                    if (p.X >= x0 && p.X < x1 && p.Z >= z0 && p.Z < z1) list.Add(p);
            }
            return list;
        }

        /// <summary>The crossbucks whose foot lies in the rectangle: one on each approach to every level crossing,
        /// CrossbuckBack before the rails' formation and CrossbuckOut past the road's asphalt on that approach's right.</summary>
        public List<CrossingSign> CrossingSignsIn(List<Line> lines, double x0, double z0, double x1, double z1)
        {
            var list = new List<CrossingSign>();
            foreach (var e in lines)
                if (e.LevelCrossings != null)
                    foreach (var c in e.LevelCrossings)
                        for (int s = -1; s <= 1; s += 2)
                        {
                            // traffic heading s * M meets the rails coming from the -s * M side; its right is (-vz, vx)
                            double vx = s * c.MX, vz = s * c.MZ;
                            double back = (RailFormationHalf + CrossbuckBack) / Math.Max(0.5, Math.Sqrt(Math.Max(0, 1 - Math.Pow(c.RX * c.MX + c.RZ * c.MZ, 2)))), out_ = PavedHalf(RoadKind.Main) + CrossbuckOut;
                            double x = c.X - vx * back - vz * out_, z = c.Z - vz * back + vx * out_;
                            if (x < x0 || x >= x1 || z < z0 || z >= z1) continue;
                            list.Add(new CrossingSign { X = x, Z = z, Y = _t.HeightAt(x, z), DX = (float)vx, DZ = (float)vz });
                        }
            return list;
        }
        public const double CrossbuckBack = 1.0, CrossbuckOut = 1.5;

        /// <summary>The level crossings whose centre lies in the rectangle.</summary>
        public List<LevelCrossing> LevelCrossingsIn(List<Line> lines, double x0, double z0, double x1, double z1)
        {
            var list = new List<LevelCrossing>();
            foreach (var e in lines)
                if (e.LevelCrossings != null)
                    foreach (var c in e.LevelCrossings)
                        if (c.X >= x0 && c.X < x1 && c.Z >= z0 && c.Z < z1) list.Add(c);
            return list;
        }

        // ---- for tests and tools
        public (double x, double z, float h)[] RailCentreline(int axis, long band, long k) => Pts(Rail(axis, band, k));
        /// <summary>Test accessor: the rail segment's lines (itself, and the far side of a water gap).</summary>
        public List<Line> RailLines(int axis, long band, long k)
        {
            var e = Rail(axis, band, k);
            var r = new List<Line>();
            if (e.Exists) r.Add(e);
            if (e.Branches != null) foreach (var b in e.Branches) if (b.Exists) r.Add(b);
            return r;
        }
        public List<RailPiece> TrackOf(int axis, long band, long k)
        {
            var r = new List<RailPiece>();
            foreach (var l in RailLines(axis, band, k)) r.AddRange(TrackOf(l));
            return r;
        }
        public List<RailSite> RailSitesOf(int axis, long band, long k)
        {
            var r = new List<RailSite>();
            foreach (var l in RailLines(axis, band, k)) if (l.Sites != null) r.AddRange(l.Sites);
            return r;
        }
        public IReadOnlyList<Underpass> RailSeparations(int axis, long band, long k)
        {
            var r = new List<Underpass>();
            foreach (var l in RailLines(axis, band, k)) if (l.Underpasses != null) r.AddRange(l.Underpasses);
            return r;
        }
        public List<BridgePiece> RailBridgesOf(int axis, long band, long k)
        {
            var r = new List<BridgePiece>();
            foreach (var l in RailLines(axis, band, k))
            {
                if (l.BridgePieces != null) r.AddRange(l.BridgePieces);
                if (l.Underpasses != null) foreach (var u in l.Underpasses) r.AddRange(u.Pieces);
            }
            return r;
        }
        public float[] RailShouldersOf(int axis, long band, long k) => Rail(axis, band, k).Shoulders;
        public List<TunnelSpan> RailTunnelsOf(int axis, long band, long k)
        {
            var r = new List<TunnelSpan>();
            foreach (var l in RailLines(axis, band, k)) if (l.TunnelSpans != null) r.AddRange(l.TunnelSpans);
            return r;
        }
        public IReadOnlyList<LevelCrossing> MainLevelCrossings(long cx, long cz, int dir) => Main(cx, cz, dir).LevelCrossings ?? (IReadOnlyList<LevelCrossing>)Array.Empty<LevelCrossing>();
    }
}
