using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace SDG.Unturned
{
    /// <summary>A lattice transmission tower (Power_Line_1, cow tools' pylon) standing in a region, the line's heading
    /// there, and the far tower of every span this one OWNS -- each span is owned by exactly one tower, so a region
    /// strings the spans that leave it, whichever region the far tower stands in. Absolute metres.</summary>
    public struct PylonPlacement
    {
        public double X, Z; public float H, DirX, DirZ;
        public (double X, double Z, float H, float DirX, float DirZ)[] Wired;
    }

    /// <summary>HIGH-VOLTAGE LINES (strawberry 2026-10-10: "implement the big pylon splines. cross country, their own
    /// network. make sure the pylons themselves dont overlap the roads"). Their own network, owing nothing to the
    /// roads: one node per Cell, jittered, and a line to the east and north neighbours where the hash says so, each
    /// in three straight legs (two angle towers) so the grid does not read as one. Along each leg a tower every
    /// TowerSpan or less, and every tower:
    /// - stands CLEAR OF EVERY ROAD: its base centre RoadKeep from the nearest asphalt (the base's half-diagonal plus
    ///   a verge). A blocked station slides along its leg until it is clear, inside the longest span;
    /// - stands on land, its base at the LOWEST of its footprint's ground, so no leg floats;
    /// and every span keeps its lowest conductor WireClear over the ground and any road deck all the way along --
    /// where a ridge or a bridge would come up into it, a tower goes in at the worst point. A line that cannot meet
    /// all of that is not built (Drops says why), rather than built through something.
    /// The game strings them with PowerLineField's pylon kind; the numbers it measures off the prop are copied here
    /// because core cannot see the game, and L1 asserts the two agree.</summary>
    public sealed class InfinitePylons
    {
        /// <summary>UG_INF_PYLONS=0: no high-voltage lines.</summary>
        public static bool Enabled = Environment.GetEnvironmentVariable("UG_INF_PYLONS") != "0";
        public const double Cell = 5120;            // one node per cell
        public const double TowerSpan = 360;        // the spacing a straight leg aims for
        public const double MaxSpan = 395;          // PowerLineField.PylonMaxSpan is 400: never a span it would refuse
        public const double MinSpan = 90;           // a station's least span: shorter reads as two towers that collided
        public const double MinClimbSpan = 45;      // ...but over steep ground, where the alternative is no line
        public const float Scale = 1.6f;            // PowerLineField.PylonScale
        public const float FootHalf = 2.46f * Scale;          // half the lattice base's side (the mesh's +-2.46 m)
        public const float LowestConductor = 13.473f * Scale; // the lower crossarm's insulator foot over the base...
        public const float LowerReach = 4.708f * Scale;       // ...and out from the line
        public const float MiddleConductor = 19.209f * Scale; // the middle (widest) crossarm's
        public const float MiddleReach = 5.952f * Scale;
        public const float SagFraction = 0.035f;    // PowerLineField.SagFraction (of the span's length, at mid-span)
        public const float RoadKeep = 12f;          // base centre to the nearest asphalt: 5.6 m half-diagonal + verge
        public const float WireClear = 7f;          // lowest conductor over the ground / a road's surface, any span
        /// <summary>The right-of-way kept clear of trees either side of a line (strawberry: "have those pylons clear trees
        /// along their path like they would irl"): a 50 m cut, about what a line of 48 m towers gets.</summary>
        public const float TreeKeep = 25f;
        const int Bends = 2;                        // angle towers per line
        const int Routes = 5;                       // routes tried per line (see Build, RouteInner)
        const int CorridorHalf = 15;                // lanes either side of A->B in Corridor (up to 200 m apart)
        public const float NearRoad = 60f;          // a tower this near asphalt counts against its route
        /// <summary>How much MOUNTAIN (InfiniteTerrain.MountainHeight: the ridged belts, over the rolling hills) a line
        /// shrugs off -- below this it is a hill, and strawberry: "hills are okay" -- and how much it never stands a
        /// tower on: a route that would is not taken, and a line with no other is not built.</summary>
        public const float HillHeight = 12f, MountainLimit = 30f;
        /// <summary>...except over a PASS: where a range lies right between two nodes and no corridor goes round it, the
        /// lowest way over may stand towers on this much, and only if nothing else fits.</summary>
        public const float PassLimit = 60f;

        readonly InfiniteTerrain _t;
        readonly ulong _s;
        readonly ConcurrentDictionary<(long, long), (double x, double z, bool ok)> _node = new();
        readonly ConcurrentDictionary<(long, long, int), Line> _line = new();
        /// <summary>Why a line the hash asked for was not built (survey/debug aid, like InfiniteRoads.MainDrops).</summary>
        public readonly ConcurrentDictionary<(long cx, long cz, int dir), string> Drops = new();

        internal InfinitePylons(InfiniteTerrain t, ulong seed) { _t = t; _s = seed ^ 0x5A5A_1E7E; }

        public sealed class Tower { public double X, Z; public float H, DirX, DirZ; }

        /// <summary>One line between two nodes: its towers in order, the two nodes' included, and its bounds.</summary>
        public sealed class Line
        {
            public List<Tower> Towers;
            public double MinX, MaxX, MinZ, MaxZ;
            public bool Exists => Towers != null;
        }
        static readonly Line None = new Line();

        // ---- nodes

        /// <summary>The node of cell (cx, cz): jittered inside it, then moved to the nearest spot a tower may stand on
        /// (a ring search out to 400 m); not ok when there is none, or the cell's spot is sea.</summary>
        (double x, double z, bool ok) Node(long cx, long cz) => _node.GetOrAdd((cx, cz), k =>
        {
            uint h = InfiniteTerrain.Hash(k.Item1, k.Item2, _s);
            double x0 = (k.Item1 + 0.2 + 0.6 * ((h & 0xFFFF) / 65536.0)) * Cell, z0 = (k.Item2 + 0.2 + 0.6 * ((h >> 16) / 65536.0)) * Cell;
            // off the mountains first (a line has to climb to its node), within 1.2 km; then anywhere a tower stands
            for (int pass = 0; pass < 2; pass++)
                for (int r = 0; r <= (pass == 0 ? 1200 : 400); r += pass == 0 ? 100 : 25)
                    for (int a = 0; a < (r == 0 ? 1 : 12); a++)
                    {
                        double x = x0 + r * Math.Cos(a * Math.PI / 6), z = z0 + r * Math.Sin(a * Math.PI / 6);
                        if (pass == 0 && _t.MountainHeight(x, z) > HillHeight) continue;
                        if (Stands(x, z)) return (x, z, true);
                    }
            return (x0, z0, false);
        });

        /// <summary>May a tower stand here: on land, and clear of every road by RoadKeep.</summary>
        public bool Stands(double x, double z)
        {
            if (_t.RawHeight(x, z) < InfiniteTerrain.SeaLevel + 2f) return false;
            return _t.Roads.Influence(x, z).Clear >= RoadKeep;
        }

        /// <summary>Where a tower's base sits: the lowest ground under its footprint (centre and eight points round
        /// it), so no leg hangs in the air on a slope -- the uphill legs bury instead.</summary>
        public float BaseHeight(double x, double z)
        {
            float h = _t.HeightAt(x, z);
            double r = FootHalf * 1.42;
            for (int a = 0; a < 8; a++) h = Math.Min(h, _t.HeightAt(x + r * Math.Cos(a * Math.PI / 4), z + r * Math.Sin(a * Math.PI / 4)));
            return h;
        }

        // ---- lines

        /// <summary>The line from node (cx, cz) to its east (dir 0) or north (dir 1) neighbour; not Exists when the
        /// hash leaves that link out or it could not be built.</summary>
        internal Line LineAt(long cx, long cz, int dir) =>
            _line.TryGetValue((cx, cz, dir), out var hit) ? hit
            : _line.GetOrAdd((cx, cz, dir), k => { if (_line.Count > 4000) _line.Clear(); return Build(k.Item1, k.Item2, k.Item3); });

        bool Hashed(long cx, long cz, int dir) => InfiniteTerrain.Hash(cx, cz, _s ^ (ulong)(dir + 3)) % 100 < 60;   // ~3 in 5 links exist

        /// <summary>Could line (cx, cz, dir) reach the rectangle at all, from its nodes alone? The widest any route can
        /// stray off the straight line between them: the last route's bends (36% of the length) or the widest corridor (3 km), the angle point's
        /// search, a station stepped off its leg, and a tower inserted beside a span. A region asking for its
        /// towers then builds only the lines that can reach it -- not all eighteen round its node cell, which made a
        /// cold region cost seconds.</summary>
        bool MayReach(long cx, long cz, int dir, double x0, double z0, double x1, double z1)
        {
            if (!Hashed(cx, cz, dir)) return false;
            var a = Node(cx, cz); var b = Node(cx + (dir == 0 ? 1 : 0), cz + (dir == 1 ? 1 : 0));
            double len = Math.Sqrt((b.x - a.x) * (b.x - a.x) + (b.z - a.z) * (b.z - a.z));
            double pad = Math.Max(len * (0.08 + 0.07 * (Routes - 1)) + 240, CorridorHalf * 200.0 + 120) + 120 + 60 + 40 + 10;
            return Math.Max(a.x, b.x) + pad >= x0 && Math.Min(a.x, b.x) - pad <= x1 && Math.Max(a.z, b.z) + pad >= z0 && Math.Min(a.z, b.z) - pad <= z1;
        }

        Line Build(long cx, long cz, int dir)
        {
            if (!Hashed(cx, cz, dir)) return None;
            // FOUR ROUTES (other angle points, swinging wider each time), and the one that keeps its towers furthest from
            // the roads wins: a line that happens to run along a road stands a tower at its verge every 300 m, clear of
            // the asphalt and still wrong. Then fewest towers. A line no route fits is dropped.
            string why = null; Line best = None; double bestScore = double.MaxValue;
            for (int attempt = 0; attempt < Routes; attempt++)
            {
                var line = Route(cx, cz, dir, attempt, out string w);
                if (!line.Exists) { why ??= w; continue; }
                int near = 0; double peak = 0, climb = 0;
                foreach (var t in line.Towers)
                {
                    if (_t.Roads.Influence(t.X, t.Z).Clear < NearRoad) near++;
                    float mh = _t.MountainHeight(t.X, t.Z);
                    peak = Math.Max(peak, mh); climb += Math.Max(0f, mh - HillHeight);
                }
                if (peak > PassLimit) { why ??= $"over a mountain ({peak:0} m of it under a tower)"; continue; }
                double score = near * 10 + line.Towers.Count + climb * 4;
                if (score < bestScore) { bestScore = score; best = line; }
                if (near == 0 && climb == 0) break;   // nothing near a road and nothing on a mountain: take it
            }
            if (best.Exists) { Drops.TryRemove((cx, cz, dir), out _); return best; }
            Drops[(cx, cz, dir)] = why;
            return None;
        }

        Line Route(long cx, long cz, int dir, int attempt, out string why)
        {
            string reason = null;
            Line Gone(string w) { reason = w; return None; }
            var r = RouteInner(cx, cz, dir, attempt, Gone);
            why = reason;
            return r;
        }

        Line RouteInner(long cx, long cz, int dir, int attempt, Func<string, Line> Gone)
        {
            var a = Node(cx, cz); var b = Node(cx + (dir == 0 ? 1 : 0), cz + (dir == 1 ? 1 : 0));
            if (!a.ok || !b.ok) return Gone("node at sea or boxed in by roads");
            double dx = b.x - a.x, dz = b.z - a.z, len = Math.Sqrt(dx * dx + dz * dz), px = -dz / len, pz = dx / len;
            var corners = new List<(double x, double z)> { (a.x, a.z) };
            // THROUGH THE VALLEYS (strawberry 2026-10-10: "power lines are loving to go over mountains, try to reroute
            // them to not go over mountains, hills are okay"): the first two routes follow the cheapest corridor across
            // the mountain belts (Corridor), simplified to a few straight legs; the later ones are the old jittered bends
            if (attempt < 3)
            {
                // 0: the corridor; 1: a wider one; 2: over the lowest PASS, where a mountain range lies between the
                // two nodes and no valley goes round it (PassLimit, not MountainLimit)
                var path = Corridor(a.x, a.z, b.x, b.z, attempt == 0 ? 120.0 : 200.0, attempt == 2 ? PassLimit : MountainLimit);
                if (path == null) return Gone("no corridor below the mountains");
                for (int c = 1; c + 1 < path.Count; c++)
                {
                    var (x0, z0) = path[c];
                    bool ok = false;
                    for (int r = 0; r <= 120 && !ok; r += 20)
                        for (int ang = 0; ang < (r == 0 ? 1 : 8) && !ok; ang++)
                        {
                            double x = x0 + r * Math.Cos(ang * Math.PI / 4), z = z0 + r * Math.Sin(ang * Math.PI / 4);
                            if (Stands(x, z)) { corners.Add((x, z)); ok = true; }
                        }
                    if (!ok) return Gone("no angle point clear");
                }
            }
            // the angle points: at 1/3 and 2/3, pushed sideways by up to 8% of the length, each slid to a spot it stands on
            else for (int c = 1; c <= Bends; c++)
            {
                uint h = InfiniteTerrain.Hash(cx * 7 + c, cz * 13 + dir, _s ^ 0xC0 ^ (ulong)attempt * 0x9E37);
                double t = (double)c / (Bends + 1), off = len * (0.08 + 0.07 * attempt) * ((h & 0xFFFF) / 32768.0 - 1.0);
                double x0 = a.x + dx * t + px * off, z0 = a.z + dz * t + pz * off;
                bool found = false;
                for (int r = 0; r <= 240 && !found; r += 20)
                    for (int s = -1; s <= 1 && !found; s += 2)
                        for (int along = 0; along <= 120 && !found; along += 40)
                            for (int sa = -1; sa <= 1 && !found; sa += 2)
                            {
                                double x = x0 + px * r * s + dx / len * along * sa, z = z0 + pz * r * s + dz / len * along * sa;
                                if (Stands(x, z)) { corners.Add((x, z)); found = true; }
                            }
                if (!found) return Gone("no angle point clear");
            }
            corners.Add((b.x, b.z));
            // towers along each leg, every station slid clear of the roads
            var pts = new List<(double x, double z)> { corners[0] };
            for (int l = 0; l + 1 < corners.Count; l++)
            {
                var (x0, z0) = corners[l]; var (x1, z1) = corners[l + 1];
                double lx = x1 - x0, lz = z1 - z0, ll = Math.Sqrt(lx * lx + lz * lz), ux = lx / ll, uz = lz / ll;
                int n = Math.Max(1, (int)Math.Ceiling(ll / TowerSpan));
                double at = 0;   // where along the leg the last tower stands
                for (int k = 1; k < n; k++)
                {
                    double want = ll * k / n;
                    if (!Place(x0, z0, ux, uz, ll, at, want, out double got, out double side)) return Gone("no clear station on a leg");
                    pts.Add((x0 + ux * got - uz * side, z0 + uz * got + ux * side)); at = got;
                }
                if (ll - at > MaxSpan) return Gone("span over the limit at a corner");
                pts.Add((x1, z1));
            }
            // heights, then the wires: a tower goes in wherever a span's lowest conductor comes within WireClear of
            // the ground or a road's surface
            var hs = new List<float>(); foreach (var (x, z) in pts) hs.Add(BaseHeight(x, z));
            for (int guard = 0, i = 0; i + 1 < pts.Count; guard++)
            {
                if (guard > 400) return Gone("wire clearance did not settle");
                if (SpanClear(pts[i], hs[i], pts[i + 1], hs[i + 1], out double worstT)) { i++; continue; }
                // the worst point, then nearby spots on the span until one stands
                double sx = pts[i + 1].x - pts[i].x, sz = pts[i + 1].z - pts[i].z, sl = Math.Sqrt(sx * sx + sz * sz);
                bool put = false;
                for (int off = 0; off <= 40 && !put; off += 20)
                    for (int q = 0; q <= 12 && !put; q++)
                        for (int sg = -1; sg <= 1 && !put; sg += 2)
                            for (int ss = -1; ss <= 1 && !put; ss += 2)
                            {
                                if (q == 0 && sg > 0 || off == 0 && ss > 0) continue;
                                double t = worstT + sg * q * 12.0 / sl;
                                if (t * sl < MinClimbSpan || (1 - t) * sl < MinClimbSpan) continue;   // not on top of a tower
                                double x = pts[i].x + sx * t - sz / sl * ss * off, z = pts[i].z + sz * t + sx / sl * ss * off;
                                if (!Stands(x, z)) continue;
                                pts.Insert(i + 1, (x, z)); hs.Insert(i + 1, BaseHeight(x, z)); put = true;
                            }
                if (!put)
                {
                    SpanClear(pts[i], hs[i], pts[i + 1], hs[i + 1], out double wt, out double wg);
                    return Gone($"a span's wires meet the ground and no tower fits under them (span {sl:0} m, gap {wg:0.0} m at t {wt:0.00}, at ({pts[i].x + sx * wt:0},{pts[i].z + sz * wt:0}))");
                }
            }
            var line = new Line { Towers = new List<Tower>(), MinX = double.MaxValue, MinZ = double.MaxValue, MaxX = double.MinValue, MaxZ = double.MinValue };
            for (int i = 0; i < pts.Count; i++)
            {
                // the heading: the run's at a straight tower, the axial mean of the two at an angle tower or an end
                double ix = i > 0 ? pts[i].x - pts[i - 1].x : 0, iz = i > 0 ? pts[i].z - pts[i - 1].z : 0;
                double ox = i + 1 < pts.Count ? pts[i + 1].x - pts[i].x : 0, oz = i + 1 < pts.Count ? pts[i + 1].z - pts[i].z : 0;
                double il = Math.Sqrt(ix * ix + iz * iz), ol = Math.Sqrt(ox * ox + oz * oz);
                double hx = (il > 0 ? ix / il : 0) + (ol > 0 ? ox / ol : 0), hz = (il > 0 ? iz / il : 0) + (ol > 0 ? oz / ol : 0), hl = Math.Sqrt(hx * hx + hz * hz);
                float dX = (float)(hx / hl), dZ = (float)(hz / hl), h = hs[i];
                // ...and now the heading is known, the base's four actual FEET (the lattice square, turned with it): the
                // footprint circle above samples between them, and a foot on a slope can sit a little under it
                foreach (var (u, v) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
                    h = Math.Min(h, _t.HeightAt(pts[i].x + (dX * u - dZ * v) * FootHalf, pts[i].z + (dZ * u + dX * v) * FootHalf));
                line.Towers.Add(new Tower { X = pts[i].x, Z = pts[i].z, H = h, DirX = dX, DirZ = dZ });
                line.MinX = Math.Min(line.MinX, pts[i].x); line.MaxX = Math.Max(line.MaxX, pts[i].x);
                line.MinZ = Math.Min(line.MinZ, pts[i].z); line.MaxZ = Math.Max(line.MaxZ, pts[i].z);
            }
            return line;
        }

        /// <summary>The cheapest corridor from A to B across the mountains: a dynamic programme over stations every ~320 m
        /// along A->B and lanes `laneStep` apart across it (+-15 lanes), costing each station the MOUNTAIN under it above
        /// HillHeight (heavily past MountainLimit), water, and each lane changed (so it stays straight where nothing is
        /// in the way); a lane may move at most two per station. The path is then simplified to straight legs
        /// (Douglas-Peucker, 90 m) -- a line's angle towers. Null when even the cheapest corridor has a station on
        /// a mountain (water it may cross: the towers either find land in reach or the route is dropped later).</summary>
        List<(double x, double z)> Corridor(double ax, double az, double bx, double bz, double laneStep, float limit)
        {
            double dx = bx - ax, dz = bz - az, len = Math.Sqrt(dx * dx + dz * dz), px = -dz / len, pz = dx / len;
            int S = Math.Max(4, (int)Math.Round(len / 320)), H = CorridorHalf, L = 2 * H + 1;
            (double x, double z) At(int i, int j) { double t = (double)i / S; return (ax + dx * t + px * (j - H) * laneStep, az + dz * t + pz * (j - H) * laneStep); }
            var cost = new double[S + 1, L]; var from = new int[S + 1, L];
            const double Inf = double.MaxValue / 4;
            for (int j = 0; j < L; j++) cost[0, j] = j == H ? 0 : Inf;
            for (int i = 1; i <= S; i++)
                for (int j = 0; j < L; j++)
                {
                    cost[i, j] = Inf;
                    if (i == S && j != H) continue;   // it ends at B
                    var (x, z) = At(i, j);
                    float mh = _t.MountainHeight(x, z);
                    double here = 4.0 * Math.Max(0f, mh - HillHeight) + (mh > limit ? 400 : 0)
                                + (_t.RawHeight(x, z) < InfiniteTerrain.SeaLevel + 2f ? 300 : 0);
                    for (int k = Math.Max(0, j - 2); k <= Math.Min(L - 1, j + 2); k++)
                    {
                        double c = cost[i - 1, k] + here + 6.0 * Math.Abs(j - k);
                        if (c < cost[i, j]) { cost[i, j] = c; from[i, j] = k; }
                    }
                }
            var path = new List<(double x, double z)>();
            for (int i = S, j = H; i >= 0; j = from[i, j], i--) path.Add(At(i, j));
            path.Reverse();
            // the cheapest way across still climbs a mountain: there is no valley here
            foreach (var (x, z) in path) if (_t.MountainHeight(x, z) > limit) return null;
            // to straight legs
            var keep = new bool[path.Count]; keep[0] = keep[path.Count - 1] = true;
            void Simplify(int i0, int i1)
            {
                double sx = path[i1].x - path[i0].x, sz = path[i1].z - path[i0].z, sl = Math.Sqrt(sx * sx + sz * sz);
                int worst = -1; double wd = 90;
                for (int i = i0 + 1; i < i1; i++)
                {
                    double d = Math.Abs((path[i].x - path[i0].x) * sz - (path[i].z - path[i0].z) * sx) / Math.Max(1e-9, sl);
                    if (d > wd) { wd = d; worst = i; }
                }
                if (worst < 0) return;
                keep[worst] = true; Simplify(i0, worst); Simplify(worst, i1);
            }
            Simplify(0, path.Count - 1);
            var legs = new List<(double x, double z)>();
            for (int i = 0; i < path.Count; i++) if (keep[i]) legs.Add(path[i]);
            return legs;
        }

        /// <summary>A station along a leg: `want` metres in, or the nearest spot to it where a tower stands -- along the
        /// leg first (12 m steps, up to 144 m either way), then stepped off it sideways (20 m steps, up to 60 m: a small
        /// angle in the line) -- no further than MaxSpan past the last tower (`after`) and short of the leg's end.</summary>
        bool Place(double x0, double z0, double ux, double uz, double ll, double after, double want, out double got, out double side)
        {
            for (int off = 0; off <= 60; off += 20)
                for (int q = 0; q <= 12; q++)
                    for (int sg = -1; sg <= 1; sg += 2)
                        for (int ss = -1; ss <= 1; ss += 2)
                        {
                            if (q == 0 && sg > 0 || off == 0 && ss > 0) continue;
                            got = want + sg * q * 12.0; side = ss * off;
                            if (got - after > MaxSpan - 5 || got - after < MinSpan || ll - got < MinSpan) continue;
                            if (Stands(x0 + ux * got - uz * side, z0 + uz * got + ux * side)) return true;
                        }
            got = 0; side = 0; return false;
        }

        /// <summary>The span's lowest conductor along it, against the ground and any road surface under it (a bridge deck
        /// stands above the ground): clear by WireClear everywhere? If not, `worstT` is where it is closest.</summary>
        bool SpanClear((double x, double z) a, float ha, (double x, double z) b, float hb, out double worstT) => SpanClear(a, ha, b, hb, out worstT, out _);

        bool SpanClear((double x, double z) a, float ha, (double x, double z) b, float hb, out double worstT, out double worstGap)
        {
            double sx = b.x - a.x, sz = b.z - a.z, sl = Math.Sqrt(sx * sx + sz * sz);
            double len3 = Math.Sqrt(sl * sl + (hb - ha) * (hb - ha));
            int n = Math.Max(4, (int)(sl / 12));
            double worst = double.MaxValue; worstT = 0.5;
            for (int k = 1; k < n; k++)
            {
                double t = (double)k / n, x = a.x + sx * t, z = a.z + sz * t, sag = len3 * SagFraction * 4 * t * (1 - t);
                // the LOWEST conductor (the lower arm's), over the ground under the outermost one either side -- the
                // middle arm's, 9.5 m out: a side slope comes up to the outer wires first, and the middle arm is 9 m
                // higher than this assumes, so this is the conservative reading at a third of the samples. Every
                // sample of the ground already carries the roads' carve; a road DECK over it is checked where the
                // line is over a road at all
                double under = double.MinValue;
                for (int side = -1; side <= 1; side += 2)
                    under = Math.Max(under, _t.HeightAt(x - sz / sl * side * MiddleReach, z + sx / sl * side * MiddleReach));
                var hit = _t.Roads.Influence(x, z);
                if (hit.Any && hit.Clear < MiddleReach) under = Math.Max(under, InfiniteRoads.SurfaceY(hit.Kind, hit.Height) + InfiniteRoads.DeckParapetTop);
                double gap = ha + LowestConductor + (hb - ha) * t - sag - under;
                if (gap < worst) { worst = gap; worstT = t; }
            }
            worstGap = worst;
            return worst >= WireClear;
        }

        // ---- queries

        /// <summary>Every line whose towers' bounds (plus `pad`) touch the rectangle.</summary>
        public List<Line> LinesIn(double x0, double z0, double x1, double z1, double pad = 0)
        {
            var list = new List<Line>();
            if (!Enabled) return list;
            long c0 = (long)Math.Floor((x0 - pad) / Cell) - 1, c1 = (long)Math.Floor((x1 + pad) / Cell) + 1;
            long r0 = (long)Math.Floor((z0 - pad) / Cell) - 1, r1 = (long)Math.Floor((z1 + pad) / Cell) + 1;
            for (long cx = c0; cx <= c1; cx++)
                for (long cz = r0; cz <= r1; cz++)
                    for (int dir = 0; dir < 2; dir++)
                    {
                        if (!_line.ContainsKey((cx, cz, dir)) && !MayReach(cx, cz, dir, x0 - pad, z0 - pad, x1 + pad, z1 + pad)) continue;
                        var e = LineAt(cx, cz, dir);
                        if (e.Exists && e.MaxX + pad >= x0 && e.MinX - pad <= x1 && e.MaxZ + pad >= z0 && e.MinZ - pad <= z1) list.Add(e);
                    }
            return list;
        }

        /// <summary>The towers standing in the rectangle, each with the spans it owns. In a line T0..Tn every tower
        /// owns the span BACK to the one before it, and the last intermediate one also the span on to Tn -- so the two
        /// NODE towers (T0, Tn) own nothing of the line, and a node shared by several lines is listed once.</summary>
        public List<PylonPlacement> PylonsIn(double x0, double z0, double x1, double z1)
        {
            var list = new List<PylonPlacement>();
            var nodes = new Dictionary<(long, long), (Tower t, double sx, double sz, int n)>();
            foreach (var e in LinesIn(x0, z0, x1, z1))
            {
                var tw = e.Towers; int n = tw.Count - 1;
                for (int i = 1; i < n; i++)
                {
                    var t = tw[i];
                    if (t.X < x0 || t.X >= x1 || t.Z < z0 || t.Z >= z1) continue;
                    var wired = new List<(double, double, float, float, float)>();
                    var p = tw[i - 1]; wired.Add((p.X, p.Z, p.H, p.DirX, p.DirZ));
                    if (i == n - 1) { var q = tw[n]; wired.Add((q.X, q.Z, q.H, q.DirX, q.DirZ)); }
                    list.Add(new PylonPlacement { X = t.X, Z = t.Z, H = t.H, DirX = t.DirX, DirZ = t.DirZ, Wired = wired.ToArray() });
                }
                // the nodes: headed by the axial mean of every line that meets them
                foreach (var (end, next) in new[] { (tw[0], tw[1]), (tw[n], tw[n - 1]) })
                {
                    if (end.X < x0 || end.X >= x1 || end.Z < z0 || end.Z >= z1) continue;
                    var key = ((long)Math.Floor(end.X / Cell), (long)Math.Floor(end.Z / Cell));
                    double dx = next.X - end.X, dz = next.Z - end.Z, dl = Math.Sqrt(dx * dx + dz * dz), th = Math.Atan2(dz, dx);
                    nodes.TryGetValue(key, out var acc);
                    nodes[key] = (end, acc.sx + Math.Cos(2 * th), acc.sz + Math.Sin(2 * th), acc.n + 1);
                }
            }
            foreach (var (t, sx, sz, n) in nodes.Values)
            {
                double th = Math.Atan2(sz, sx) * 0.5;   // the axial mean: a straight-through node heads along its line
                list.Add(new PylonPlacement { X = t.X, Z = t.Z, H = t.H, DirX = (float)Math.Cos(th), DirZ = (float)Math.Sin(th),
                                              Wired = Array.Empty<(double, double, float, float, float)>() });
            }
            return list;
        }

        /// <summary>Distance from (x, z) to the nearest line's wires (tower to tower, horizontally) among `lines`.</summary>
        public static double CorridorDistance(List<Line> lines, double x, double z)
        {
            double best = double.MaxValue;
            foreach (var e in lines)
            {
                if (x < e.MinX - 200 || x > e.MaxX + 200 || z < e.MinZ - 200 || z > e.MaxZ + 200) continue;
                for (int i = 0; i + 1 < e.Towers.Count; i++)
                {
                    var a = e.Towers[i]; var b = e.Towers[i + 1];
                    double sx = b.X - a.X, sz = b.Z - a.Z, ss = sx * sx + sz * sz;
                    double t = Math.Clamp(((x - a.X) * sx + (z - a.Z) * sz) / ss, 0, 1);
                    double ex = x - a.X - sx * t, ez = z - a.Z - sz * t;
                    best = Math.Min(best, Math.Sqrt(ex * ex + ez * ez));
                }
            }
            return best;
        }

        // ---- for tests and tools
        public List<Tower> TowersOf(long cx, long cz, int dir) { var e = LineAt(cx, cz, dir); return e.Exists ? e.Towers : null; }
    }
}
