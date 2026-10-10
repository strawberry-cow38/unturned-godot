using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SDG.Unturned;

namespace UnturnedSim.Tests
{
    // The infinite world's generator (strawberry 2026-10-09). Pure function of (seed, absolute x, z), so these pin the
    // three properties streaming leans on: same input -> same region, neighbours agree on their shared edge EXACTLY
    // (at any LOD pairing), and the noise 10^7 m out is the same noise as at the origin.
    [TestFixture]
    public class InfiniteTerrainTests
    {
        static readonly InfiniteTerrain Gen = new InfiniteTerrain(1337);

        [Test]
        public void SameSeedSameRegion_AnyInstanceAnyOrder()
        {
            var a = Gen.Generate(new RegionCoord(3, -7), 0);
            Gen.Generate(new RegionCoord(40, 40), 1);   // something in between, to catch hidden state
            var b = new InfiniteTerrain(1337).Generate(new RegionCoord(3, -7), 0);
            Assert.That(b.Heights, Is.EqualTo(a.Heights));
            Assert.That(b.Normals, Is.EqualTo(a.Normals));
            Assert.That(b.Layers, Is.EqualTo(a.Layers));
            Assert.That(b.Trees.Count, Is.EqualTo(a.Trees.Count));
            var other = new InfiniteTerrain(1338).Generate(new RegionCoord(3, -7), 0);
            Assert.That(other.Heights, Is.Not.EqualTo(a.Heights), "a different seed must be a different world");
        }

        static float[] EastEdge(RegionData d) => Enumerable.Range(0, d.Cells + 1).Select(j => d.Heights[j * (d.Cells + 1) + d.Cells]).ToArray();
        static float[] WestEdge(RegionData d) => Enumerable.Range(0, d.Cells + 1).Select(j => d.Heights[j * (d.Cells + 1)]).ToArray();
        static float[] NorthEdge(RegionData d) => Enumerable.Range(0, d.Cells + 1).Select(i => d.Heights[d.Cells * (d.Cells + 1) + i]).ToArray();
        static float[] SouthEdge(RegionData d) => Enumerable.Range(0, d.Cells + 1).Select(i => d.Heights[i]).ToArray();

        [TestCase(0, 0)]
        [TestCase(-1, 5)]
        [TestCase(39062, -39062)]   // ~10^7 m out
        public void NeighboursShareTheirEdgeExactly(int rx, int rz)
        {
            var c = Gen.Generate(new RegionCoord(rx, rz), 0);
            var e = Gen.Generate(new RegionCoord(rx + 1, rz), 0);
            var n = Gen.Generate(new RegionCoord(rx, rz + 1), 0);
            Assert.That(WestEdge(e), Is.EqualTo(EastEdge(c)), "x seam");
            Assert.That(SouthEdge(n), Is.EqualTo(NorthEdge(c)), "z seam");
            // ...and a coarse neighbour's edge samples are a subset of the fine edge: LOD cracks are only ever the
            // in-between vertices (the skirt's job), never a disagreement where both have a vertex
            var e2 = Gen.Generate(new RegionCoord(rx + 1, rz), 2);
            var fine = EastEdge(c); var coarse = WestEdge(e2);
            for (int k = 0; k < coarse.Length; k++) Assert.That(coarse[k], Is.EqualTo(fine[k * 4]), $"LOD2 edge sample {k}");
        }

        [Test]
        public void RegionOfANegativePositionFloors()
        {
            Assert.That(RegionCoord.Containing(-0.5, 0.0), Is.EqualTo(new RegionCoord(-1, 0)));
            Assert.That(RegionCoord.Containing(255.99, -256.0), Is.EqualTo(new RegionCoord(0, -1)));
            Assert.That(RegionCoord.Containing(1e7, -1e7), Is.EqualTo(new RegionCoord(39062, -39063)));
        }

        // cow tools 2026-10-09: a hash that loses precision far out looks PERFECT near the origin and grids thousands
        // of units out. So compare the finest band's statistics over the same-size patch at 0, 10^6 and 10^7 m.
        // ⚠ WHAT THIS CAN AND CANNOT SEE, measured by mutation (2026-10-09): it is a texture check -- collapsed or
        // inflated variance, or a direction the noise stops varying in. It did NOT fail for float-coordinate noise
        // (a 4 m grid of whole metres is exact in a float out to 1.6e7 m -- that is NoiseFarOutStillMovesEvery-
        // QuarterMetre's job, which does fail) nor for a shader-style sin() hash, which is only broken on a GPU's
        // approximate sin; on the CPU it is a valid if ugly hash at these magnitudes. Kept as the smoke check it is.
        static (double mean, double std, double ax, double az, double ad) Stats(double x0, double z0)
        {
            const int N = 128; const double S = 4.0;
            var v = new double[N, N];
            for (int j = 0; j < N; j++) for (int i = 0; i < N; i++) v[i, j] = Gen.DetailNoise(x0 + i * S, z0 + j * S);
            double m = 0; foreach (var x in v) m += x; m /= N * N;
            double var_ = 0; foreach (var x in v) var_ += (x - m) * (x - m); var_ /= N * N;
            double Corr(int di, int dj)
            {
                double s = 0; int c = 0;
                for (int j = 0; j < N - Math.Abs(dj); j++) for (int i = 0; i < N - di; i++) { s += (v[i, j] - m) * (v[i + di, j + dj] - m); c++; }
                return s / c / var_;
            }
            return (m, Math.Sqrt(var_), Corr(3, 0), Corr(0, 3), Corr(2, 2));
        }

        [Test]
        public void NoiseFarOutIsTheSameNoise()
        {
            var o = Stats(0, 0);
            foreach (var (x, z) in new[] { (1e6, -3e6), (1e7, 1e7), (-2.5e7, 4e6) })
            {
                var f = Stats(x, z);
                TestContext.WriteLine($"at ({x:0.#e0},{z:0.#e0}): std {f.std:0.000} vs {o.std:0.000}, corr x/z/diag {f.ax:0.00}/{f.az:0.00}/{f.ad:0.00} vs {o.ax:0.00}/{o.az:0.00}/{o.ad:0.00}");
                Assert.That(f.std, Is.EqualTo(o.std).Within(o.std * 0.2), "lost (or gained) variance far out");
                // a gridded / axis-aligned artefact shows as the axis correlations parting from the diagonal one
                Assert.That(f.ax, Is.EqualTo(o.ax).Within(0.12), "x correlation");
                Assert.That(f.az, Is.EqualTo(o.az).Within(0.12), "z correlation");
                Assert.That(f.ad, Is.EqualTo(o.ad).Within(0.12), "diagonal correlation");
            }
        }

        // ⚠ THE STATS ABOVE ARE BLIND TO PRECISION LOSS, and the first mutation run proved it: noise computed from
        // FLOAT coordinates passed them unchanged, because they sample a 4 m grid of whole metres and a float holds
        // every whole metre exactly out to 1.6e7 m. Losing precision means NEIGHBOURING POSITIONS COLLAPSE ONTO THE
        // SAME VALUE, so measure exactly that: step 25 cm at a time and count the steps where the noise did not move.
        static double FrozenStepFraction(double x0, double z0, bool alongX)
        {
            int frozen = 0; const int N = 4000;
            float prev = Gen.DetailNoise(x0, z0);
            for (int k = 1; k <= N; k++)
            {
                double d = k * 0.25 + 0.013;
                float v = alongX ? Gen.DetailNoise(x0 + d, z0) : Gen.DetailNoise(x0, z0 + d);
                if (v == prev) frozen++;
                prev = v;
            }
            return (double)frozen / N;
        }

        [Test]
        public void NoiseFarOutStillMovesEveryQuarterMetre()
        {
            foreach (var (x, z) in new[] { (0.0, 0.0), (1e7, -3e6), (-6e7, 2e7), (3e8, 3e8) })
            {
                double fx = FrozenStepFraction(x, z, true), fz = FrozenStepFraction(x, z, false);
                TestContext.WriteLine($"at ({x:0.#e0},{z:0.#e0}): {fx:P1} / {fz:P1} of 25 cm steps frozen");
                Assert.That(fx, Is.LessThan(0.01), $"x steps frozen at {x}");
                Assert.That(fz, Is.LessThan(0.01), $"z steps frozen at {z}");
            }
        }

        [Test]
        public void TreesStayInTheirRegionAndOutOfTheSea()
        {
            int total = 0;
            for (int rz = -2; rz <= 2; rz++)
                for (int rx = -2; rx <= 2; rx++)
                {
                    var t = Gen.PlaceTrees(new RegionCoord(rx, rz));
                    total += t.Count;
                    foreach (var s in t)
                    {
                        Assert.That(s.X, Is.InRange(0f, InfiniteTerrain.RegionSize));
                        Assert.That(s.Z, Is.InRange(0f, InfiniteTerrain.RegionSize));
                        Assert.That(s.Y, Is.GreaterThan(InfiniteTerrain.SeaLevel + 2f));
                    }
                }
            TestContext.WriteLine($"{total} trees over 25 regions");
            Assert.That(total, Is.GreaterThan(0), "a 1.3 km patch with no trees at all means the density field is dead");
        }

        // ROADS (strawberry 2026-10-09): four classes -- highways (dual Highway_1 carriageways on a set course), main roads
        // (Highway_0), and small roads + trails branching off the mains. A road is checked by DRIVING it: every centreline
        // point where this road is the one in charge must have the ground at its profile less the bed, on its bed layer,
        // and no stretch steeper than its class allows.
        static IEnumerable<(RoadKind kind, (double x, double z, float h)[] pts)> AllRoads(long c0, long c1)
        {
            for (long cx = c0; cx <= c1; cx++)
                for (long cz = c0; cz <= c1; cz++)
                    for (int dir = 0; dir < 2; dir++)
                    {
                        var m = Gen.Roads.MainCentreline(cx, cz, dir);
                        if (m == null) continue;
                        yield return (RoadKind.Main, m);
                        foreach (var b in Gen.Roads.BranchesOf(cx, cz, dir)) yield return b;
                    }
            for (int axis = 0; axis < 2; axis++)
                for (long band = -2; band <= 1; band++)
                    for (long k = -4; k <= 3; k++)
                    {
                        var h = Gen.Roads.HighwayCentreline(axis, band, k);
                        if (h != null) yield return (RoadKind.Highway, h);
                    }
            // ...and the railways (InfiniteRails), which the ground is carved to the same way
            for (int axis = 0; axis < 2; axis++)
                for (long band = -1; band <= 0; band++)
                    for (long k = -1; k <= 1; k++)
                    {
                        var r = Gen.Roads.RailCentreline(axis, band, k);
                        if (r != null) yield return (RoadKind.Rail, r);
                    }
        }

        [Test]
        public void RoadsAreDrivable()
        {
            var lines = new Dictionary<RoadKind, int>(); var points = new Dictionary<RoadKind, int>();
            var worstGrade = new Dictionary<RoadKind, float>(); float worstGap = 0f;
            foreach (var (kind, pts) in AllRoads(-3, 3))
            {
                lines[kind] = lines.GetValueOrDefault(kind) + 1;
                double run = 0;
                for (int k = 0; k < pts.Length; k++)
                {
                    var (x, z, h) = pts[k];
                    if (k > 0)
                    {
                        var (px, pz, ph) = pts[k - 1];
                        double seg = Math.Sqrt((x - px) * (x - px) + (z - pz) * (z - pz));
                        run += seg;
                        worstGrade[kind] = Math.Max(worstGrade.GetValueOrDefault(kind), (float)(Math.Abs(h - ph) / seg));
                    }
                    if (k % 3 != 0) continue;   // the grade is checked on every piece; the ground on every third point
                    float ground = Gen.Sample(x, z, out var hit);
                    // judge only where THIS road is in charge (at a junction the other road may be)
                    if (!hit.Any || hit.Kind != kind || hit.Dist > 0.01f || hit.Weight < 0.999f) continue;
                    points[kind] = points.GetValueOrDefault(kind) + 1;
                    worstGap = Math.Max(worstGap, Math.Abs(ground + InfiniteRoads.Bed - h));
                    var bed = kind == RoadKind.Trail ? InfiniteTerrain.Layer.Dirt : InfiniteTerrain.Layer.Gravel;
                    Assert.That(Gen.LayerAt(x, z, ground, 0f, hit.Clear, hit.Kind), Is.EqualTo(bed), $"{kind} centreline ({x:0},{z:0}) is not on its bed");
                }
            }
            foreach (var k in lines.Keys)
                TestContext.WriteLine($"{k}: {lines[k]} lines, {points.GetValueOrDefault(k)} centreline points checked, worst grade {worstGrade.GetValueOrDefault(k):P1} (limit {InfiniteRoads.MaxGrade(k):P0})");
            TestContext.WriteLine($"worst ground-vs-road {worstGap * 1000f:0.###} mm");
            foreach (RoadKind k in Enum.GetValues(typeof(RoadKind)))
            {
                Assert.That(lines.GetValueOrDefault(k), Is.GreaterThan(0), $"no {k} at all within ~11 km");
                Assert.That(points.GetValueOrDefault(k), Is.GreaterThan(20), $"{k}: too few centreline points checked to mean anything");
                Assert.That(worstGrade.GetValueOrDefault(k), Is.LessThanOrEqualTo(InfiniteRoads.MaxGrade(k) * 1.0001f), $"{k} too steep");
            }
            Assert.That(worstGap, Is.LessThan(0.001f), "the ground on a centreline must be the road profile, less its bed");
        }

        // "stay on a pretty set course, avoiding elevation shifts and obstacles while trying to stay somewhat straight"
        [Test]
        public void HighwaysRunStraightLevelAndDry()
        {
            int segs = 0; double worstSinuosity = 0;
            for (int axis = 0; axis < 2; axis++)
                for (long band = -2; band <= 1; band++)
                    for (long k = -4; k <= 3; k++)
                    {
                        var h = Gen.Roads.HighwayCentreline(axis, band, k);
                        if (h == null) continue;
                        segs++;
                        double arc = 0;
                        for (int i = 1; i < h.Length; i++)
                        {
                            arc += Math.Sqrt((h[i].x - h[i - 1].x) * (h[i].x - h[i - 1].x) + (h[i].z - h[i - 1].z) * (h[i].z - h[i - 1].z));
                            Assert.That(h[i].h, Is.GreaterThan(InfiniteTerrain.SeaLevel), "a highway never runs through water");
                        }
                        double chord = Math.Sqrt((h[^1].x - h[0].x) * (h[^1].x - h[0].x) + (h[^1].z - h[0].z) * (h[^1].z - h[0].z));
                        worstSinuosity = Math.Max(worstSinuosity, arc / chord);
                    }
            TestContext.WriteLine($"{segs} highway segments (6 km each) over 48 km, worst sinuosity {worstSinuosity:0.000}");
            Assert.That(segs, Is.GreaterThan(4));
            Assert.That(worstSinuosity, Is.LessThan(1.15), "a highway segment wanders more than 15% over its straight line");
        }

        [Test]
        public void HighwaySegmentsJoinWithoutAKink()
        {
            // each 6 km segment is routed on its own; where two meet at an anchor they must leave it on the same heading
            int joins = 0; double worst = 0;
            for (int axis = 0; axis < 2; axis++)
                for (long band = -2; band <= 1; band++)
                    for (long k = -4; k <= 2; k++)
                    {
                        var a = Gen.Roads.HighwayCentreline(axis, band, k);
                        var b = Gen.Roads.HighwayCentreline(axis, band, k + 1);
                        if (a == null || b == null) continue;
                        if (Math.Abs(a[^1].x - b[0].x) + Math.Abs(a[^1].z - b[0].z) > 0.01) continue;   // a shore spur: not a join
                        joins++;
                        double h0 = Math.Atan2(a[^1].z - a[^2].z, a[^1].x - a[^2].x), h1 = Math.Atan2(b[1].z - b[0].z, b[1].x - b[0].x);
                        double d = Math.Abs(Math.IEEERemainder(h1 - h0, 2 * Math.PI)) * 180 / Math.PI;
                        worst = Math.Max(worst, d);
                    }
            TestContext.WriteLine($"{joins} joins, worst heading change across one {worst:0.00} degrees");
            Assert.That(joins, Is.GreaterThan(10));
            Assert.That(worst, Is.LessThan(3.0), "a highway kinks where two segments meet");
        }

        [Test]
        public void BranchesStopShortOfHighways()
        {
            // small roads and trails end at a highway's shoulder instead of crossing it at grade
            var hw = new List<(double x, double z, float h)[]>();
            for (int axis = 0; axis < 2; axis++)
                for (long band = -2; band <= 1; band++)
                    for (long k = -4; k <= 3; k++) { var h = Gen.Roads.HighwayCentreline(axis, band, k); if (h != null) hw.Add(h); }
            double clearance = InfiniteRoads.PavedHalf(RoadKind.Highway) + InfiniteRoads.PavedHalf(RoadKind.Small);
            double closest = double.MaxValue; int branches = 0;
            for (long cx = -4; cx <= 4; cx++)
                for (long cz = -4; cz <= 4; cz++)
                    for (int dir = 0; dir < 2; dir++)
                        foreach (var (_, pts) in Gen.Roads.BranchesOf(cx, cz, dir))
                        {
                            branches++;
                            foreach (var (x, z, _) in pts)
                                foreach (var h in hw)
                                    for (int i = 0; i + 1 < h.Length; i++)
                                    {
                                        double sx = h[i + 1].x - h[i].x, sz = h[i + 1].z - h[i].z, qx = x - h[i].x, qz = z - h[i].z;
                                        double t = Math.Clamp((qx * sx + qz * sz) / (sx * sx + sz * sz), 0, 1);
                                        closest = Math.Min(closest, Math.Sqrt((qx - sx * t) * (qx - sx * t) + (qz - sz * t) * (qz - sz * t)));
                                    }
                        }
            TestContext.WriteLine($"{branches} branches, {hw.Count} highway segments; closest approach {closest:0.0} m (asphalt touches at {clearance:0.0})");
            Assert.That(branches, Is.GreaterThan(20)); Assert.That(hw.Count, Is.GreaterThan(10));
            Assert.That(closest, Is.GreaterThan(clearance), "a branch runs onto a highway");
        }

        [Test]
        public void RaisedStretchesAreWhereTheCarveLiftsTheGround() => CheckStretches(cut: false);

        [Test]
        public void CutStretchesAreWhereTheCarveDigsTheGround() => CheckStretches(cut: true);

        /// <summary>Judged against the CARVED ground the player walks on (HeightAt) rather than re-deriving MarkStretches'
        /// own profile arithmetic, so the test and the rule cannot be wrong together: a marked stretch must be one where
        /// the carve has moved the ground (up for raised, down for cut) under its WHOLE CARRIAGEWAY, and no long
        /// unmarked run may be. Each carriageway on its own (strawberry: "both lanes may candidate separately").</summary>
        void CheckStretches(bool cut)
        {
            // the marking is judged against the carve that WOULD lift a raised stretch: with bridges on, the ground
            // under one is left natural on purpose (BridgesLeaveTheGroundAndJoinUp), so measure with them off
            // ...and tunnels off too: a bored run keeps its hill, so it is not where the carve digs
            bool bridges = InfiniteRoads.Bridges, tunnels = InfiniteRoads.Tunnels;
            InfiniteRoads.Bridges = false; InfiniteRoads.Tunnels = false;
            try { CheckStretchesWith(cut); } finally { InfiniteRoads.Bridges = bridges; InfiniteRoads.Tunnels = tunnels; }
        }

        void CheckStretchesWith(bool cut)
        {
            // how far the carve moved the ground, in the direction this kind is about (up = raised, down = cut)
            float Moved(double x, double z) => (cut ? -1f : 1f) * (Gen.HeightAt(x, z) - Gen.NaturalHeight(x, z));
            float threshold = cut ? InfiniteRoads.CutDepth : InfiniteRoads.RaiseFill, minLength = cut ? InfiniteRoads.CutMinLength : InfiniteRoads.RaiseMinLength;
            // the ground under asphalt is the profile minus Bed: a raise moves it Bed less than the fill, a cut Bed more
            float need = (cut ? threshold + InfiniteRoads.Bed : threshold - InfiniteRoads.Bed) - 0.05f;
            // p is a CARRIAGEWAY's centreline: its centre and both edges, just inside the asphalt
            float AcrossWidth((double x, double z, float h)[] p, int i)
            {
                int ia = Math.Max(0, i - 1), ib = Math.Min(p.Length - 1, i + 1);
                double tx = p[ib].x - p[ia].x, tz = p[ib].z - p[ia].z, tl = Math.Sqrt(tx * tx + tz * tz);
                double nx = -tz / tl, nz = tx / tl; float o = InfiniteRoads.HighwayLaneHalf * 0.9f;
                return Math.Min(Moved(p[i].x, p[i].z), Math.Min(Moved(p[i].x + nx * o, p[i].z + nz * o), Moved(p[i].x - nx * o, p[i].z - nz * o)));
            }
            int stretches = 0, wet = 0, peaks = 0; float biggest = 0f, worstPeak = float.MaxValue;
            double worstUnmarkedRun = 0; string worstWhere = "";
            for (int axis = 0; axis < 2; axis++)
                for (long band = -2; band <= 1; band++)
                    for (long k = -4; k <= 3; k++)
                    {
                        if (Gen.Roads.HighwayCentreline(axis, band, k) == null) continue;
                        var marked = new HashSet<(int, long, long)>();
                        foreach (var (r, pts) in cut ? Gen.Roads.CutOf(axis, band, k) : Gen.Roads.RaisedOf(axis, band, k))
                        {
                            Assert.That(r.Side, Is.AnyOf(-1, 1), "a stretch belongs to one carriageway");
                            stretches++; if (r.OverWater) wet++;
                            biggest = Math.Max(biggest, r.Max);
                            Assert.That(r.Length, Is.GreaterThanOrEqualTo(minLength));
                            Assert.That(r.Max >= threshold || r.OverWater, $"a {r.Max:0.0} m stretch marked");
                            Assert.That(!cut || !r.OverWater, "a cut never claims water");
                            foreach (var p in pts) marked.Add((r.Side, (long)Math.Round(p.x * 100), (long)Math.Round(p.z * 100)));
                            // where it peaks the carve must have moved the ground under the whole width
                            if (!r.OverWater && r.Max > threshold + 1f)
                            {
                                float best = float.MinValue;
                                for (int i = 0; i < pts.Length; i++) best = Math.Max(best, AcrossWidth(pts, i));
                                peaks++; worstPeak = Math.Min(worstPeak, best);
                                Assert.That(best, Is.GreaterThan(need), "a marked stretch where the carve never moves the whole width");
                            }
                        }
                        // completeness, per carriageway: an unmarked run (first moved point to last, as a stretch is
                        // measured) where the carve moves the whole carriageway by more than the threshold
                        for (int side = -1; side <= 1; side += 2)
                        {
                            var line = Gen.Roads.HighwayCarriageway(axis, band, k, side);
                            double arcI = 0, runStart = -1;
                            for (int i = 1; i + 1 < line.Length; i++)
                            {
                                arcI += Math.Sqrt((line[i].x - line[i - 1].x) * (line[i].x - line[i - 1].x) + (line[i].z - line[i - 1].z) * (line[i].z - line[i - 1].z));
                                bool isMarked = marked.Contains((side, (long)Math.Round(line[i].x * 100), (long)Math.Round(line[i].z * 100)));
                                // where ANOTHER road shapes the ground (a crossing highway 14 m higher, measured), the lift is
                                // that road's embankment, not this carriageway's: the marking is about its own fill
                                bool ownGround = Math.Abs(Gen.Roads.Influence(line[i].x, line[i].z).Height - line[i].h) < 1f;
                                if (!isMarked && ownGround && AcrossWidth(line, i) > need + 0.5f)
                                {
                                    if (runStart < 0) runStart = arcI;
                                    if (arcI - runStart > worstUnmarkedRun) { worstUnmarkedRun = arcI - runStart; worstWhere = $"side {side} ending ({line[i].x:0.0}, {line[i].z:0.0}) moved {AcrossWidth(line, i):0.00}"; }
                                }
                                else runStart = -1;
                            }
                        }
                    }
            TestContext.WriteLine($"{stretches} {(cut ? "cut" : "raised")} stretches ({wet} over water), biggest {biggest:0.0} m; at {peaks} peaks the carve moves the whole width by >= {worstPeak:0.0} m; longest unmarked run {worstUnmarkedRun:0} m {worstWhere}");
            Assert.That(stretches, Is.GreaterThan(5)); Assert.That(peaks, Is.GreaterThan(3));
            Assert.That(worstUnmarkedRun, Is.LessThan(minLength), "a moved run the marking missed");
        }

        [Test]
        public void BridgesLeaveTheGroundAndJoinUp()
        {
            // the kit numbers the core copies from the game's EditorBridgeSpline (asserted equal there, in L1)
            const float P = InfiniteRoads.BridgePitch, W = InfiniteRoads.BridgeHalfWidth;
            // the carriageway fits between the parapets: a wider road class laid on this deck would run through them
            Assert.That(InfiniteRoads.HighwayLaneHalf, Is.LessThanOrEqualTo(InfiniteRoads.DeckRoadwayHalf), "the carriageway is wider than the deck's roadway");
            int bridges = 0, decks = 0, piers = 0, joints = 0, raised = 0;
            double worstGap = 0, worstPierFoot = 0, worstPierTop = 0, worstOff = 0, worstGround = 0, liftOff = 0, worstDh = 0; int liftN = 0;
            string groundWhere = "", gapWhere = ""; int overpasses = 0, atGrade = 0;
            for (int axis = 0; axis < 2; axis++)
                for (long band = -2; band <= 1; band++)
                    for (long k = -4; k <= 3; k++)
                    {
                        if (Gen.Roads.HighwayCentreline(axis, band, k) == null) continue;
                        foreach (var (r, _) in Gen.Roads.RaisedOf(axis, band, k)) if (r.Length >= P) raised++;
                        var lanes = new[] { Gen.Roads.HighwayCarriageway(axis, band, k, -1), Gen.Roads.HighwayCarriageway(axis, band, k, 1) };
                        BridgePiece? prevDeck = null;
                        foreach (var p in Gen.Roads.BridgesOf(axis, band, k))
                        {
                            if (p.Kind == 2) { if (prevDeck != null) bridges++; prevDeck = null; continue; }   // caps close a bridge (two per bridge)
                            if (p.Kind == 1)
                            {
                                piers++;
                                // foot on the NATURAL ground, top 1.02 m up inside the deck it hangs from (retail's joint)
                                worstPierFoot = Math.Max(worstPierFoot, Math.Abs(p.Y + InfiniteRoads.PierBottom * p.K - Gen.NaturalHeight(p.X, p.Z)));
                                if (prevDeck is BridgePiece dk) worstPierTop = Math.Max(worstPierTop, Math.Abs(p.Y + InfiniteRoads.PierTop * p.K - (dk.Y + InfiniteRoads.PierTop)));
                                continue;
                            }
                            decks++;
                            // ON a carriageway centreline; and both ENDS of the unit on its driven surface (the middle of a
                            // straight unit is a chord of the profile, off it by the sagitta wherever it spans a profile vertex)
                            double off = double.MaxValue;
                            foreach (var lane in lanes)
                                for (int i = 0; i + 1 < lane.Length; i++)
                                {
                                    double sx = lane[i + 1].x - lane[i].x, sz = lane[i + 1].z - lane[i].z, qx = p.X - lane[i].x, qz = p.Z - lane[i].z;
                                    double t = Math.Clamp((qx * sx + qz * sz) / (sx * sx + sz * sz), 0, 1);
                                    off = Math.Min(off, Math.Sqrt((qx - sx * t) * (qx - sx * t) + (qz - sz * t) * (qz - sz * t)));
                                }
                            worstOff = Math.Max(worstOff, off);
                            for (int end = -1; end <= 1; end += 2)
                            {
                                double ex = p.X + p.DX * P * 0.5 * end, ey = p.Y + p.DY * P * 0.5 * end, ez = p.Z + p.DZ * P * 0.5 * end;
                                double eo = double.MaxValue, dh = 0;
                                foreach (var lane in lanes)
                                    for (int i = 0; i + 1 < lane.Length; i++)
                                    {
                                        double sx = lane[i + 1].x - lane[i].x, sz = lane[i + 1].z - lane[i].z, qx = ex - lane[i].x, qz = ez - lane[i].z;
                                        double t = Math.Clamp((qx * sx + qz * sz) / (sx * sx + sz * sz), 0, 1);
                                        double d = Math.Sqrt((qx - sx * t) * (qx - sx * t) + (qz - sz * t) * (qz - sz * t));
                                        if (d < eo) { eo = d; dh = ey - InfiniteRoads.SurfaceY(RoadKind.Highway, lane[i].h + (lane[i + 1].h - lane[i].h) * (float)t); }
                                    }
                                worstDh = Math.Max(worstDh, Math.Abs(dh));
                            }
                            // nothing under it was carved: the ground stays natural (and with bridges off it would not)
                            double moved = Math.Abs(Gen.HeightAt(p.X, p.Z) - Gen.NaturalHeight(p.X, p.Z));
                            // ...unless a DIFFERENT road shapes it. Measured: another highway 6.5 m below a deck at
                            // (-17176, -7642) -- an overpass, fine -- and a MAIN road at the deck's own height at
                            // (19483, 21920), climbing on its own embankment to meet the bridge AT GRADE: roads still
                            // cross highways at grade (no interchanges), so that is counted and reported, not hidden here
                            var under = Gen.Roads.Influence(p.X, p.Z);
                            if (under.Any && under.Kind != RoadKind.Highway) { atGrade++; moved = 0; }
                            else if (under.Any && Math.Abs(under.Height - p.Y) > 1f) { overpasses++; moved = 0; }
                            if (moved > worstGround) { worstGround = moved; var hit = Gen.Roads.Influence(p.X, p.Z); groundWhere = $" at ({p.X:0.0}, {p.Z:0.0}): carved by a {hit.Kind} {hit.Dist:0.0} m off at road height {hit.Height:0.0}, deck y {p.Y:0.0}, natural {Gen.NaturalHeight(p.X, p.Z):0.0}"; }
                            InfiniteRoads.Bridges = false;
                            try { liftOff += Gen.HeightAt(p.X, p.Z) - Gen.NaturalHeight(p.X, p.Z); liftN++; } finally { InfiniteRoads.Bridges = true; }
                            // the JOINT with the previous unit: its front outer corner must meet this one's back corner
                            if (prevDeck is BridgePiece a)
                            {
                                joints++;
                                // IN 3D, along the deck's own (graded) axis: a unit is P long on that axis, so on a slope its
                                // horizontal footprint is shorter than P. strawberry saw the seams this measures ("slight gaps")
                                // when the walk spaced units by horizontal distance -- 1.96 cm at 7%, which a 2 cm bar passed.
                                double ah = Math.Sqrt(a.DX * a.DX + a.DZ * a.DZ), bh = Math.Sqrt(p.DX * p.DX + p.DZ * p.DZ);
                                double alx = -a.DZ / ah, alz = a.DX / ah, blx = -p.DZ / bh, blz = p.DX / bh;
                                double gap = 0;
                                for (int s = -1; s <= 1; s += 2)
                                {
                                    double fx = a.X + a.DX * P * 0.5 + alx * s * W, fy = a.Y + a.DY * P * 0.5, fz = a.Z + a.DZ * P * 0.5 + alz * s * W;
                                    double bx = p.X - p.DX * P * 0.5 + blx * s * W, by = p.Y - p.DY * P * 0.5, bz = p.Z - p.DZ * P * 0.5 + blz * s * W;
                                    // signed: positive = daylight between them along the run, negative = solid inside solid
                                    double along = (bx - fx) * a.DX + (by - fy) * a.DY + (bz - fz) * a.DZ;
                                    gap = Math.Max(gap, along);
                                }
                                if (gap > worstGap) { worstGap = gap; gapWhere = $" at ({p.X:0}, {p.Z:0})"; }
                            }
                            prevDeck = p;
                        }
                    }
            TestContext.WriteLine($"{bridges} bridges over {raised} raised stretches: {decks} deck units, {piers} piers, {joints} joints; worst joint daylight {worstGap * 100:0.00} cm{gapWhere}; " +
                                  $"decks within {worstOff:0.00} m of a carriageway, unit ends {worstDh * 1000:0.0} mm off its driven surface; pier feet {worstPierFoot * 1000:0.0} mm off the ground, tops {worstPierTop * 1000:0.0} mm off the deck; " +
                                  $"ground under decks moved {worstGround:0.000} m{groundWhere}, {overpasses} decks over another highway, {atGrade} where a lesser road meets the deck at grade (with bridges off it would rise {liftOff / Math.Max(1, liftN):0.0} m on average)");
            Assert.That(bridges, Is.EqualTo(raised), "every raised stretch at least a unit long gets its bridge");
            Assert.That(decks, Is.GreaterThan(50)); Assert.That(piers, Is.GreaterThan(5)); Assert.That(joints, Is.GreaterThan(40));
            Assert.That(worstOff, Is.LessThan(0.5), "a deck unit off its carriageway");
            Assert.That(worstDh, Is.LessThan(0.005), "a deck unit's end off its road's driven surface");
            Assert.That(worstGap, Is.LessThan(0.005), "daylight at a deck joint");
            Assert.That(worstPierFoot, Is.LessThan(0.01)); Assert.That(worstPierTop, Is.LessThan(0.01));
            Assert.That(worstGround, Is.LessThan(0.01), "the carve raised ground under a bridge");
            Assert.That(liftOff / Math.Max(1, liftN), Is.GreaterThan(2.0), "control: without bridges the same spots ARE embanked");
        }

        /// <summary>strawberry 2026-10-09: "fix the smoothness between highways and bridges, make sure they are aligned
        /// properly and theres no big gap". At every bridge end, the carriageway's ribbon must END where the deck ends:
        /// same point, same driven height, same heading -- and the embankment under the ribbon must run right up to it.
        /// Before, the ribbon was dropped per whole 37 m profile segment while the deck stopped wherever its last 7.83 m
        /// unit fitted, so a bridge end could have up to a deck-length of neither; and the deck sat on the profile while
        /// the ribbon floated 4.5 cm over it.</summary>
        [Test]
        public void BridgeEndsMeetTheirRoad()
        {
            int ends = 0, embank = 0, deckJoints = 0;
            double worstGap = 0, worstDy = 0, worstTurn = 0, worstBank = 0, worstPhase = 0, worstDeckPhase = 0, worstMouth = 0, worstAway = 0;
            int awayProbes = 0;
            string gapWhere = "", bankWhere = "";
            for (int axis = 0; axis < 2; axis++)
                for (long band = -2; band <= 1; band++)
                    for (long k = -4; k <= 3; k++)
                    {
                        if (Gen.Roads.HighwayCentreline(axis, band, k) == null) continue;
                        // BridgesOf lists each bridge's decks and piers, then its far cap, then its near cap
                        BridgePiece? firstDeck = null, lastDeck = null; int capsSeen = 0;
                        foreach (var cap in Gen.Roads.BridgesOf(axis, band, k))
                        {
                            if (cap.Kind == 0)
                            {
                                if (capsSeen > 0) { firstDeck = null; capsSeen = 0; }
                                // the roadway drawn over consecutive units agrees on the texture distance where they meet
                                if (lastDeck is BridgePiece a)
                                {
                                    double along = (cap.X - cap.DX * InfiniteRoads.BridgePitch * 0.5 - (a.X - a.DX * InfiniteRoads.BridgePitch * 0.5)) * a.DX
                                                 + (cap.Y - cap.DY * InfiniteRoads.BridgePitch * 0.5 - (a.Y - a.DY * InfiniteRoads.BridgePitch * 0.5)) * a.DY
                                                 + (cap.Z - cap.DZ * InfiniteRoads.BridgePitch * 0.5 - (a.Z - a.DZ * InfiniteRoads.BridgePitch * 0.5)) * a.DZ;
                                    double predicted = a.S0 + (a.S1 - a.S0) * along / InfiniteRoads.BridgePitch;
                                    worstDeckPhase = Math.Max(worstDeckPhase, Math.Abs(predicted - cap.S0)); deckJoints++;
                                }
                                firstDeck ??= cap; lastDeck = cap;
                                continue;
                            }
                            if (cap.Kind != 2) continue;
                            capsSeen++;
                            // the far cap meets the LAST unit's front, the near cap the FIRST unit's back
                            float deckS = capsSeen == 1 ? lastDeck.Value.S1 : firstDeck.Value.S0;
                            if (capsSeen == 2) lastDeck = null;
                            ends++;
                            // the deck's end edge: the cap stands on it, facing out along the road
                            double x0 = cap.X - 60, z0 = cap.Z - 60, x1 = cap.X + 60, z1 = cap.Z + 60;
                            var pieces = Gen.Roads.PiecesIn(Gen.Roads.LinesIn(x0, z0, x1, z1), x0, z0, x1, z1, 4f);
                            double best = double.MaxValue; float by = 0, btx = 0, btz = 0, bs = 0, bw = 0;
                            foreach (var rp in pieces)
                            {
                                if (rp.Kind != (byte)RoadKind.Highway) continue;
                                for (int e = 0; e < 2; e++)
                                {
                                    double ex = e == 0 ? rp.X0 : rp.X1, ez = e == 0 ? rp.Z0 : rp.Z1;
                                    float ey = InfiniteRoads.SurfaceY(RoadKind.Highway, e == 0 ? rp.H0 : rp.H1);
                                    double d = Math.Sqrt((ex - cap.X) * (ex - cap.X) + (ey - cap.Y) * (ey - cap.Y) + (ez - cap.Z) * (ez - cap.Z));
                                    if (d < best) { best = d; by = ey; btx = e == 0 ? rp.T0X : rp.T1X; btz = e == 0 ? rp.T0Z : rp.T1Z; bs = e == 0 ? rp.S0 : rp.S1; bw = e == 0 ? rp.W0 : rp.W1; }
                                }
                            }
                            if (best > worstGap) { worstGap = best; gapWhere = $" at ({cap.X:0.0}, {cap.Z:0.0})"; }
                            worstDy = Math.Max(worstDy, Math.Abs(by - cap.Y));
                            // ...and is as WIDE as the deck's roadway there (strawberry 2026-10-10: "widen the width of the
                            // actual highway road splines at the mouths of bridges to match up with the bridge's road width")
                            worstMouth = Math.Max(worstMouth, Math.Abs(InfiniteRoads.HighwayLaneHalf + bw - InfiniteRoads.DeckRoadwayHalf * InfiniteRoads.DeckScale(RoadKind.Highway)));
                            // ...and only THERE: well past the taper, back along the approach, it is its own width again
                            {
                                double chh = Math.Sqrt(cap.DX * cap.DX + cap.DZ * cap.DZ);
                                double ax = cap.X + cap.DX / chh * (InfiniteRoads.MouthTaper + 20), az = cap.Z + cap.DZ / chh * (InfiniteRoads.MouthTaper + 20);
                                // (another deck's mouth near the probe -- a raised stretch's, or the highway's own decks over a
                                // main road -- rightly widens it there: not a probe of "away")
                                double pr = InfiniteRoads.MouthTaper + 20;
                                bool otherMouth = Gen.Roads.BridgesIn(Gen.Roads.LinesIn(ax - pr, az - pr, ax + pr, az + pr), ax - pr, az - pr, ax + pr, az + pr)
                                                     .Any(o => o.Kind == 2 && o.Road == (byte)RoadKind.Highway && Math.Sqrt((o.X - ax) * (o.X - ax) + (o.Z - az) * (o.Z - az)) < InfiniteRoads.MouthTaper + 10);
                                if (!otherMouth)
                                {
                                    double bd = double.MaxValue; float aw = 0;
                                    foreach (var rp in Gen.Roads.PiecesIn(Gen.Roads.LinesIn(ax - 30, az - 30, ax + 30, az + 30), ax - 30, az - 30, ax + 30, az + 30, 4f))
                                    {
                                        if (rp.Kind != (byte)RoadKind.Highway) continue;
                                        double dd = Math.Min((rp.X0 - ax) * (rp.X0 - ax) + (rp.Z0 - az) * (rp.Z0 - az), (rp.X1 - ax) * (rp.X1 - ax) + (rp.Z1 - az) * (rp.Z1 - az));
                                        if (dd < bd) { bd = dd; aw = Math.Max(rp.W0, rp.W1); }
                                    }
                                    if (bd < 100) { awayProbes++; worstAway = Math.Max(worstAway, aw); }
                                }
                            }
                            // ...and the paint runs on: the deck's roadway starts at the texture distance the ribbon stopped at
                            worstPhase = Math.Max(worstPhase, Math.Abs(bs - deckS));
                            double ch = Math.Sqrt(cap.DX * cap.DX + cap.DZ * cap.DZ);
                            double cos = Math.Abs(btx * cap.DX + btz * cap.DZ) / Math.Max(1e-9, ch);
                            worstTurn = Math.Max(Degrees(cos), worstTurn);
                            // the embankment under the approach: just outside the deck end the ground is the road's bed. The
                            // bed is read with bridges OFF (what the carve would make there with no deck anywhere), so a
                            // carve suspended too far reads as a miss here instead of quietly dropping out of the check
                            double ox = cap.X + cap.DX / ch * 0.5, oz = cap.Z + cap.DZ / ch * 0.5;
                            RoadHit bed;
                            InfiniteRoads.Bridges = false;
                            try { bed = Gen.Roads.Influence(ox, oz); } finally { InfiniteRoads.Bridges = true; }
                            if (bed.Any && bed.Kind == RoadKind.Highway && bed.Weight > 0.999f)
                            {
                                embank++;
                                double bank = Math.Abs(Gen.HeightAt(ox, oz) - (bed.Height - InfiniteRoads.Bed));
                                if (bank > worstBank) { worstBank = bank; bankWhere = $" at ({ox:0.0}, {oz:0.0})"; }
                            }
                        }
                    }
            static double Degrees(double cos) => Math.Acos(Math.Clamp(cos, -1, 1)) * 180 / Math.PI;
            TestContext.WriteLine($"{ends} bridge ends: ribbon end within {worstGap * 1000:0.0} mm of the deck end{gapWhere}, driven height {worstDy * 1000:0.0} mm, heading {worstTurn:0.000} deg, " +
                                  $"half-width {worstMouth * 1000:0.0} mm off the deck roadway's (and {worstAway * 1000:0.0} mm wider than its lanes {InfiniteRoads.MouthTaper + 20} m back, at {awayProbes}), paint phase {worstPhase * 1000:0.0} mm (and {worstDeckPhase * 1000:0.0} mm across {deckJoints} deck joints); " +
                                  $"ground 0.5 m off the deck end at {embank} of them within {worstBank * 1000:0.0} mm of the bed{bankWhere}");
            Assert.That(ends, Is.GreaterThan(40));
            Assert.That(embank, Is.GreaterThan(ends / 2), "the embankment check ran at most ends");
            Assert.That(worstGap, Is.LessThan(0.005), "a bridge end the ribbon does not reach (or overruns)");
            Assert.That(worstDy, Is.LessThan(0.005), "a step between the ribbon and the deck");
            Assert.That(worstMouth, Is.LessThan(0.005), "the ribbon is narrower (or wider) than the deck it runs onto");
            Assert.That(awayProbes, Is.GreaterThan(100)); Assert.That(worstAway, Is.LessThan(0.001), "the ribbon is still widened well away from any bridge");
            Assert.That(worstTurn, Is.LessThan(0.5), "a kink between the ribbon and the deck");
            Assert.That(deckJoints, Is.GreaterThan(1000));
            Assert.That(worstPhase, Is.LessThan(0.01), "the dashes restart at a bridge end");
            Assert.That(worstDeckPhase, Is.LessThan(0.01), "the dashes jump at a deck joint");
            Assert.That(worstBank, Is.LessThan(0.01), "the embankment stops short of the deck");
        }

        /// <summary>strawberry 2026-10-09: "next is wiring up tunnels to use the tool nyatools made". A tunnel is bored
        /// where the hill already buries the whole widened shell; the requirement it has to meet is that you can DRIVE
        /// THROUGH IT: the ground mesh that is actually drawn and collided with (LOD0, holes dropped) is never inside the
        /// bore anywhere along the run, and the mouth at each facade is open -- a heightfield cannot overhang, so without
        /// holes the hill's surface runs straight across the portal. And the approach is the open cut, down at the bed.</summary>
        [Test]
        public void TunnelsAreBoredAndOpen()
        {
            float L = InfiniteRoads.TunnelLateral, boreW = InfiniteRoads.TunnelBoreHalf * L;
            // one tube per carriageway (strawberry: "do the separate carriageways as separate tunnels"): they must not meet
            float between = 2f * (InfiniteRoads.HighwayRibbonOffset - boreW);
            Assert.That(between, Is.GreaterThan(0.5f), "the two tubes' bores touch");
            int tunnels = 0, probes = 0, mouthProbes = 0, inBore = 0, mouthShut = 0;
            double shortest = double.MaxValue, longest = 0, worstApproach = 0, controlLift = 0; int controlN = 0;
            string inBoreWhere = "", shutWhere = "";
            var regions = new Dictionary<(long, long), RegionData>();
            RegionData RegionAt(double x, double z)
            {
                var rc = RegionCoord.Containing(x, z);
                if (!regions.TryGetValue((rc.X, rc.Z), out var d)) regions[(rc.X, rc.Z)] = d = Gen.Generate(rc, 0);
                return d;
            }
            // the drawn ground at a point: null where the LOD0 cell is dropped for a hole
            float? Mesh(double x, double z)
            {
                var d = RegionAt(x, z);
                float lx = (float)(x - d.Coord.MinX), lz = (float)(z - d.Coord.MinZ);
                if (d.Holes != null)
                {
                    int v = d.Cells + 1, i = Math.Clamp((int)(lx / d.Spacing), 0, d.Cells - 1), j = Math.Clamp((int)(lz / d.Spacing), 0, d.Cells - 1);
                    if (d.Holes[j * v + i] || d.Holes[j * v + i + 1] || d.Holes[(j + 1) * v + i] || d.Holes[(j + 1) * v + i + 1]) return null;
                }
                return InfiniteTerrain.MeshHeightAt(d, lx, lz);
            }
            for (int axis = 0; axis < 2; axis++)
                for (long band = -2; band <= 1; band++)
                    for (long k = -4; k <= 3; k++)
                    {
                        if (Gen.Roads.HighwayCentreline(axis, band, k) == null) continue;
                        foreach (var t in Gen.Roads.TunnelsOf(axis, band, k))
                        {
                            tunnels++;
                            double len = t.A1 - t.A0;
                            shortest = Math.Min(shortest, len); longest = Math.Max(longest, len);
                            int m = t.X.Length;
                            // 1. NOTHING IN EITHER BORE: walk each tube's stations, sample across it every metre
                            for (int s = 0; s < 2; s++)
                            for (int i = 0; i + 1 < m; i++)
                            {
                                double dx = t.SX[s][i + 1] - t.SX[s][i], dz = t.SZ[s][i + 1] - t.SZ[s][i], sl = Math.Sqrt(dx * dx + dz * dz);
                                double nx = -dz / sl, nz = dx / sl;
                                for (double o = -boreW + 0.5; o <= boreW - 0.5; o += 1.0)
                                {
                                    double px = t.SX[s][i] + nx * o, pz = t.SZ[s][i] + nz * o;
                                    var g = Mesh(px, pz);
                                    probes++;
                                    if (g is not float gy) continue;   // a hole: open, which is fine anywhere
                                    // the floor is the road's bed: ground AT it is a floor, ground above it and under the crown is in the way
                                    float bed = t.Y[i] - InfiniteRoads.Proud - InfiniteRoads.Lift(RoadKind.Highway), crown = t.Y[i] + InfiniteRoads.BoreTop((float)(o / L));
                                    if (gy > bed + 0.05f && gy < crown)
                                    {
                                        inBore++;
                                        if (inBoreWhere == "") inBoreWhere = $" first at ({px:0.0}, {pz:0.0}): ground {gy:0.00} between the bed {bed:0.00} and the bore {crown:0.00}";
                                    }
                                }
                            }
                            // 2. THE MOUTHS ARE OPEN: on each facade line and a cell either side of it, across both bores
                            for (int end = 0; end < 2; end++)
                            {
                                int a = end == 0 ? 0 : m - 1, b = end == 0 ? 1 : m - 2;
                                double dx = t.X[b] - t.X[a], dz = t.Z[b] - t.Z[a], sl = Math.Sqrt(dx * dx + dz * dz);
                                double ux = dx / sl, uz = dz / sl;   // u points INTO the tunnel
                                for (int s = 0; s < 2; s++)
                                for (double inward = -0.5; inward <= 4.0; inward += 0.5)
                                    for (double o = -boreW + 1.0; o <= boreW - 1.0; o += 1.0)
                                    {
                                        double tx = t.SX[s][b] - t.SX[s][a], tz = t.SZ[s][b] - t.SZ[s][a], tl = Math.Sqrt(tx * tx + tz * tz);
                                        double nx = -tz / tl, nz = tx / tl;
                                        double px = t.SX[s][a] + ux * inward + nx * o, pz = t.SZ[s][a] + uz * inward + nz * o;
                                        var g = Mesh(px, pz);
                                        mouthProbes++;
                                        // the approach's own bed is a floor, not a blockage: anything half a metre over it and under the crown is
                                        float bed = t.Y[a] - InfiniteRoads.Proud - InfiniteRoads.Lift(RoadKind.Highway), crown = t.Y[a] + InfiniteRoads.BoreTop((float)(o / L));
                                        if (g is float gy && gy > bed + 0.5f && gy < crown - 0.5f)
                                        {
                                            mouthShut++;
                                            if (shutWhere == "") shutWhere = $" first at ({px:0.0}, {pz:0.0}) {inward:0.0} m in: ground {gy:0.00} across a mouth from {bed:0.00} to {crown:0.00}";
                                        }
                                    }
                                // 3. THE APPROACH IS THE OPEN CUT: 10 m out, the ground under the road is its carved bed (the profile
                                // there less Bed), untouched by the tunnel's shaping
                                double ax = t.X[a] - ux * 10, az = t.Z[a] - uz * 10;
                                var near = Gen.Roads.Influence(ax, az);
                                worstApproach = Math.Max(worstApproach, near.Tunnel ? double.MaxValue : Math.Abs(Gen.HeightAt(ax, az) - (near.Height - InfiniteRoads.Bed)));
                            }
                            // 4. CONTROL: with tunnels off the same run is dug out to the bed -- the tunnel is what keeps the hill
                            int mid = m / 2;
                            InfiniteRoads.Tunnels = false;
                            try { controlLift += Gen.HeightAt(t.X[mid], t.Z[mid]) - (t.Y[mid] - InfiniteRoads.Proud - InfiniteRoads.Lift(RoadKind.Highway)); controlN++; }
                            finally { InfiniteRoads.Tunnels = true; }
                        }
                    }
            // and every height round them is a NUMBER: one -Infinity from the shells' outer edge once took out the whole
            // collider of every region it was in, and nothing above noticed (the mesh probes skip nothing for it)
            int heights = 0, nonFinite = 0;
            foreach (var d in regions.Values) foreach (var hgt in d.Heights) { heights++; if (!float.IsFinite(hgt)) nonFinite++; }
            Assert.That(nonFinite, Is.EqualTo(0), $"{nonFinite} of {heights} ground heights round the tunnels are not finite");
            TestContext.WriteLine($"{tunnels} tunnels, {shortest:0}-{longest:0} m: ground inside the bore at {inBore} of {probes} probes{inBoreWhere}; " +
                                  $"mouth shut at {mouthShut} of {mouthProbes}{shutWhere}; approach 10 m out within {worstApproach * 1000:0} mm of the bed; " +
                                  $"control (tunnels off): mid-tunnel ground {controlLift / Math.Max(1, controlN):0.00} m off the bed");
            Assert.That(tunnels, Is.GreaterThan(20));
            Assert.That(shortest, Is.GreaterThanOrEqualTo(InfiniteRoads.TunnelMinLength));
            Assert.That(inBore, Is.EqualTo(0), "ground inside a tunnel bore");
            Assert.That(mouthShut, Is.EqualTo(0), "ground across a tunnel mouth");
            Assert.That(worstApproach, Is.LessThan(0.05), "the approach is not the open cut");
            Assert.That(Math.Abs(controlLift / Math.Max(1, controlN)), Is.LessThan(0.05), "control: with tunnels off the run is not dug out");
        }

        /// <summary>strawberry 2026-10-10: "fixing up all of the road pathing etc. routing roads around eachother/over/under
        /// eachother with bridges". A main road meets a highway only to cross it -- once, square-on -- and never at grade.
        /// Over the WHOLE patch where the two asphalts overlap: one road is a deck's depth plus 5 m of headroom above the
        /// other; the lower road is open (no embankment filled across it); and the upper road is carried by deck units,
        /// at its own surface, not by a ribbon on nothing. Away from a crossing a main keeps clear of the highway.</summary>
        [Test]
        public void MainsCrossHighwaysGradeSeparated()
        {
            var hw = new List<((int axis, long band, long k) id, (double x, double z, float h)[] pts)>();
            for (int axis = 0; axis < 2; axis++)
                for (long band = -2; band <= 1; band++)
                    for (long k = -4; k <= 3; k++) { var h = Gen.Roads.HighwayCentreline(axis, band, k); if (h != null) hw.Add(((axis, band, k), h)); }
            // nearest point on a polyline: distance, profile there, the point
            static (double d, float h, double x, double z) Near((double x, double z, float h)[] p, double x, double z)
            {
                var r = (d: double.MaxValue, h: 0f, x: 0.0, z: 0.0);
                for (int i = 0; i + 1 < p.Length; i++)
                {
                    double sx = p[i + 1].x - p[i].x, sz = p[i + 1].z - p[i].z, ss = sx * sx + sz * sz;
                    if (ss < 1e-12) continue;
                    double t = Math.Clamp(((x - p[i].x) * sx + (z - p[i].z) * sz) / ss, 0, 1), px = p[i].x + sx * t, pz = p[i].z + sz * t;
                    double d = Math.Sqrt((x - px) * (x - px) + (z - pz) * (z - pz));
                    if (d < r.d) r = (d, p[i].h + (p[i + 1].h - p[i].h) * (float)t, px, pz);
                }
                return r;
            }
            float mainHalf = InfiniteRoads.PavedHalf(RoadKind.Main), hwHalf = InfiniteRoads.PavedHalf(RoadKind.Highway), depth = -InfiniteRoads.DeckSoffit;
            int crossings = 0, overs = 0, unders = 0, onBridge = 0, tunnelled = 0, patchPts = 0, nearPts = 0, mains = 0, worstCount = 0;
            double worstAngle = 90, worstHead = double.MaxValue, worstFill = double.MinValue, worstDeckY = 0, worstTunnelDh = double.MaxValue;
            int uncarried = 0; string headWhere = "", fillWhere = "", deckWhere = "", nearWhere = "";
            var bad = new List<string>();   // per crossing, the ones that fail anything (first few printed)
            for (long cx = -6; cx <= 5; cx++)
                for (long cz = -6; cz <= 5; cz++)
                    for (int dir = 0; dir < 2; dir++)
                    {
                        var m = Gen.Roads.MainCentreline(cx, cz, dir);
                        if (m == null) continue;
                        mains++;
                        var ups = Gen.Roads.MainUnderpasses(cx, cz, dir);
                        foreach (var (id, h) in hw)
                        {
                            var xs = new List<(double x, double z, double angle)>();
                            for (int i = 0; i + 1 < m.Length; i++)
                                for (int j = 0; j + 1 < h.Length; j++)
                                {
                                    double rx = m[i + 1].x - m[i].x, rz = m[i + 1].z - m[i].z, sx = h[j + 1].x - h[j].x, sz = h[j + 1].z - h[j].z, den = rx * sz - rz * sx;
                                    if (Math.Abs(den) < 1e-12) continue;
                                    double t = ((h[j].x - m[i].x) * sz - (h[j].z - m[i].z) * sx) / den, u = ((h[j].x - m[i].x) * rz - (h[j].z - m[i].z) * rx) / den;
                                    if (t < 0 || t >= 1 || u < 0 || u >= 1) continue;
                                    double ang = Math.Abs(Math.Atan2(rx * sz - rz * sx, rx * sx + rz * sz)) * 180 / Math.PI;
                                    xs.Add((m[i].x + rx * t, m[i].z + rz * t, ang > 90 ? 180 - ang : ang));
                                }
                            worstCount = Math.Max(worstCount, xs.Count);
                            // AWAY from a crossing of this highway the main keeps clear of it
                            foreach (var (x, z, _) in m)
                            {
                                var n = Near(h, x, z);
                                if (n.d >= InfiniteRoads.MainClear - 10) continue;
                                bool atCrossing = false;
                                foreach (var c in xs) if (Math.Sqrt((x - c.x) * (x - c.x) + (z - c.z) * (z - c.z)) < InfiniteRoads.MainApproach + 30) atCrossing = true;
                                if (!atCrossing) { nearPts++; nearWhere = $" ({x:0},{z:0}) {n.d:0.0} m off"; }
                            }
                            foreach (var c in xs)
                            {
                                crossings++;
                                worstAngle = Math.Min(worstAngle, c.angle);
                                var up = ups.FirstOrDefault(u => Math.Abs(u.X - c.x) < 1 && Math.Abs(u.Z - c.z) < 1);
                                if (up == null)
                                {
                                    // no record: the highway is in a tunnel here, and the main goes over the hill
                                    tunnelled++;
                                    worstTunnelDh = Math.Min(worstTunnelDh, Near(m, c.x, c.z).h - Near(h, c.x, c.z).h);
                                    continue;
                                }
                                if (up.Existing) onBridge++; else if (up.Over) overs++; else unders++;
                                bool over = up.Over;
                                // the deck units carrying the upper road here
                                var decks = (up.Existing ? Gen.Roads.BridgesOf(id.axis, id.band, id.k) : up.Pieces.ToList()).Where(p => p.Kind == 0).ToList();
                                int cUncarried = 0; double cFill = double.MinValue, cHead = double.MaxValue, cDeckY = 0;
                                for (double gx = -40; gx <= 40; gx += 0.5)
                                    for (double gz = -40; gz <= 40; gz += 0.5)
                                    {
                                        double px = c.x + gx, pz = c.z + gz;
                                        var nm = Near(m, px, pz); var nh = Near(h, px, pz);
                                        if (nm.d > mainHalf || nh.d > hwHalf) continue;   // the patch where the two asphalts overlap
                                        patchPts++;
                                        float sM = InfiniteRoads.SurfaceY(RoadKind.Main, nm.h), sH = InfiniteRoads.SurfaceY(RoadKind.Highway, nh.h);
                                        double head = (over ? sM - sH : sH - sM) - depth;
                                        cHead = Math.Min(cHead, head);
                                        if (head < worstHead) { worstHead = head; headWhere = $" at ({px:0.0},{pz:0.0}) {(over ? "over" : up.Existing ? "under a bridge" : "under")}"; }
                                        // the lower road is open: the ground there is at most its own surface
                                        double fill = Gen.HeightAt(px, pz) - (over ? sH : sM);
                                        cFill = Math.Max(cFill, fill);
                                        if (fill > worstFill) { worstFill = fill; fillWhere = $" at ({px:0.0},{pz:0.0}) {(over ? "over" : "under")}"; }
                                        // the upper road is on a deck here (the highway's median carries nothing)
                                        if (!over && nh.d < InfiniteRoads.HighwayMedian * 0.5f + 0.5f) continue;
                                        bool carried = false;
                                        foreach (var dk in decks)
                                        {
                                            double hl = Math.Sqrt(dk.DX * dk.DX + dk.DZ * dk.DZ), ux = dk.DX / hl, uz = dk.DZ / hl;
                                            double along = (px - dk.X) * ux + (pz - dk.Z) * uz, across = -(px - dk.X) * uz + (pz - dk.Z) * ux;
                                            double halfLen = InfiniteRoads.BridgePitch * 0.5 * hl;   // the unit's horizontal half-length
                                            if (Math.Abs(along) > halfLen + 0.05 || Math.Abs(across) > InfiniteRoads.DeckRoadwayHalf * InfiniteRoads.DeckScale((RoadKind)dk.Road) + 0.05) continue;
                                            carried = true;
                                            double deckY = dk.Y + along / hl * dk.DY;
                                            double dy = Math.Abs(deckY - (over ? sM : sH));
                                            cDeckY = Math.Max(cDeckY, dy);
                                            if (dy > worstDeckY) { worstDeckY = dy; deckWhere = $" at ({px:0.0},{pz:0.0})"; }
                                            break;
                                        }
                                        if (!carried) { uncarried++; cUncarried++; deckWhere = $" NOT CARRIED at ({px:0.0},{pz:0.0}) {(over ? "over" : "under")}"; }
                                    }
                                if (cUncarried > 0 || cFill > 0.01 || cHead < 5.0 || cDeckY > 0.05)
                                    bad.Add($"({c.x:0},{c.z:0}) {(over ? "over" : up.Existing ? "under-bridge" : "under")} main ({cx},{cz},{dir}) hw {id}: angle {c.angle:0}, headroom {cHead:0.00}, fill {cFill:0.00}, uncarried {cUncarried}, decks {decks.Count}, deck off its road {cDeckY * 1000:0} mm");
                            }
                        }
                    }
            TestContext.WriteLine($"{mains} mains x {hw.Count} highway segments: {crossings} crossings (over {overs}, under {unders}, under a highway bridge {onBridge}, over a tunnel {tunnelled}); " +
                                  $"most crossings of one highway by one main {worstCount}; squarest-worst {worstAngle:0.0} deg; across {patchPts} points where the asphalts overlap: " +
                                  $"headroom under the deck >= {worstHead:0.00} m{headWhere}, ground over the lower road <= {worstFill * 1000:0} mm{fillWhere}, " +
                                  $"{uncarried} points of the upper road on no deck, deck height off its road {worstDeckY * 1000:0.0} mm{deckWhere}; " +
                                  $"over a tunnel the main stands >= {(tunnelled > 0 ? $"{worstTunnelDh:0.0} m" : "(none)")} up; {nearPts} main points near a highway away from a crossing{nearWhere}");
            foreach (var b in bad.Take(12)) TestContext.WriteLine("  FAILS " + b);
            // ...and every link the routing lays out comes out as laid: what is dropped is dropped for the land (sea,
            // a pass, a grade), never because the drawn line crossed a highway the controls kept it clear of
            var why = Gen.Roads.MainDrops.Where(kv => kv.Key.cx >= -6 && kv.Key.cx <= 5 && kv.Key.cz >= -6 && kv.Key.cz <= 5)
                                          .GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.Count());
            TestContext.WriteLine("links dropped: " + string.Join(", ", why.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}")));
            int misdrawn = why.Where(kv => kv.Key.StartsWith("drawn")).Sum(kv => kv.Value);
            // the clearance the code designs for: a deck's depth under UnderDepth/OverRise, less the slabs' difference
            double designed = Math.Min(InfiniteRoads.OverRise, InfiniteRoads.UnderDepth) - depth + (InfiniteRoads.SurfaceY(RoadKind.Main, 0f) - InfiniteRoads.SurfaceY(RoadKind.Highway, 0f));
            Assert.That(crossings, Is.GreaterThan(20)); Assert.That(overs, Is.GreaterThan(5)); Assert.That(unders, Is.GreaterThan(3));
            Assert.That(patchPts, Is.GreaterThan(crossings * 1000), "the overlap patch was sampled");
            Assert.That(worstCount, Is.LessThanOrEqualTo(1), "a main crosses the same highway more than once");
            Assert.That(worstAngle, Is.GreaterThanOrEqualTo(60), "a skewed crossing");
            Assert.That(worstHead, Is.GreaterThanOrEqualTo(5.0), "less than 5 m under a deck");
            Assert.That(worstHead, Is.GreaterThanOrEqualTo(designed - 0.01), "the designed clearance does not hold across the whole overlap");
            Assert.That(misdrawn, Is.EqualTo(0), "a link whose drawn line crossed a highway its controls kept clear of");
            Assert.That(worstFill, Is.LessThanOrEqualTo(0.01), "ground filled over the lower road");
            Assert.That(uncarried, Is.EqualTo(0), "the upper road is not on a deck");
            Assert.That(worstDeckY, Is.LessThan(0.05), "a deck off its road's surface");
            Assert.That(nearPts, Is.EqualTo(0), "a main runs near a highway away from a crossing");
            if (tunnelled > 0) Assert.That(worstTunnelDh, Is.GreaterThan(InfiniteRoads.TwinShellTop(InfiniteRoads.HighwayRibbonOffset)), "a main over a tunnel down in its shell");
        }

        /// <summary>A link between two towns on the SAME side of a highway that bows out between them: the straight line
        /// crosses it twice, and the link used to be dropped (or braid across). It keeps to the towns' side, round the
        /// bow, clear of the highway -- the place strawberry pointed at ("branching off the highway properly instead of in
        /// a tangle", around (-20200, 16400)) is one; (-2,-13) is another. Checked over the cells around both.</summary>
        [Test]
        public void MainsKeepToTheirSideRoundAHighwaysBow()
        {
            var hw = new List<(double x, double z, float h)[]>();
            for (int axis = 0; axis < 2; axis++)
                for (long band = -3; band <= 2; band++)
                    for (long k = -6; k <= 5; k++) { var h = Gen.Roads.HighwayCentreline(axis, band, k); if (h != null) hw.Add(h); }
            static int Cross(double ax, double az, double bx, double bz, (double x, double z, float h)[] p)
            {
                int n = 0;
                for (int j = 0; j + 1 < p.Length; j++)
                {
                    double rx = bx - ax, rz = bz - az, sx = p[j + 1].x - p[j].x, sz = p[j + 1].z - p[j].z, den = rx * sz - rz * sx;
                    if (Math.Abs(den) < 1e-12) continue;
                    double t = ((p[j].x - ax) * sz - (p[j].z - az) * sx) / den, u = ((p[j].x - ax) * rz - (p[j].z - az) * rx) / den;
                    if (t >= 0 && t < 1 && u >= 0 && u < 1) n++;
                }
                return n;
            }
            int bows = 0, bowsBuilt = 0, links = 0, misdrawn = 0, wrongCross = 0; double closest = double.MaxValue;
            var seen = new List<string>();
            foreach (var (c0, c1, z0, z1) in new[] { (-15L, -13L, 9L, 11L), (-3L, -1L, -14L, -12L) })
                for (long cx = c0; cx <= c1; cx++)
                    for (long cz = z0; cz <= z1; cz++)
                        for (int dir = 0; dir < 2; dir++)
                        {
                            Gen.Roads.MainNodeAt(cx, cz, out double ax, out double az);
                            Gen.Roads.MainNodeAt(cx + (dir == 0 ? 1 : 0), cz + (dir == 1 ? 1 : 0), out double bx, out double bz);
                            var straight = hw.Select(h => Cross(ax, az, bx, bz, h)).ToList();
                            bool bow = straight.Any(n => n >= 2 && n % 2 == 0) && straight.All(n => n % 2 == 0);
                            var m = Gen.Roads.MainCentreline(cx, cz, dir);
                            if (bow) bows++;
                            if (Gen.Roads.MainDrops.TryGetValue((cx, cz, dir), out var why) && why.StartsWith("drawn")) { misdrawn++; seen.Add($"({cx},{cz},{dir}) dropped: {why}"); }
                            if (m == null) continue;
                            links++;
                            if (bow) { bowsBuilt++; seen.Add($"({cx},{cz},{dir}) bows and is built"); }
                            // the drawn line crosses each highway exactly as often as it must: once where the towns are on
                            // opposite sides, never where they are on the same side
                            for (int q = 0; q < hw.Count; q++)
                            {
                                int drawn = 0;
                                for (int i = 0; i + 1 < m.Length; i++) drawn += Cross(m[i].x, m[i].z, m[i + 1].x, m[i + 1].z, hw[q]);
                                if (drawn != straight[q] % 2) { wrongCross++; seen.Add($"({cx},{cz},{dir}) crosses a highway {drawn}x, straight line {straight[q]}x"); }
                            }
                            // ...and away from a crossing it keeps clear (the towns are NodeClear off; the main MainClear)
                            if (straight.All(n => n % 2 == 0))
                                foreach (var (x, z, _) in m)
                                    foreach (var h in hw)
                                        for (int j = 0; j + 1 < h.Length; j++)
                                        {
                                            double sx = h[j + 1].x - h[j].x, sz = h[j + 1].z - h[j].z, qx = x - h[j].x, qz = z - h[j].z, ss = sx * sx + sz * sz;
                                            if (ss < 1e-12) continue;
                                            double t = Math.Clamp((qx * sx + qz * sz) / ss, 0, 1);
                                            closest = Math.Min(closest, Math.Sqrt((qx - sx * t) * (qx - sx * t) + (qz - sz * t) * (qz - sz * t)));
                                        }
                        }
            TestContext.WriteLine($"{links} links built round two bowing highways; {bows} whose towns' straight line crosses a highway twice, {bowsBuilt} of them built; " +
                                  $"{wrongCross} wrong crossings, {misdrawn} dropped for a misdrawn line; links that need not cross come within {closest:0.0} m of a highway centreline");
            foreach (var s in seen) TestContext.WriteLine("  " + s);
            Assert.That(bows, Is.GreaterThanOrEqualTo(1), "the cells hold no bowing link: the check has nothing to test");
            Assert.That(bowsBuilt, Is.EqualTo(bows), "a bowing link was not built");
            Assert.That(wrongCross, Is.EqualTo(0));
            Assert.That(misdrawn, Is.EqualTo(0));
            Assert.That(closest, Is.GreaterThan(InfiniteRoads.MainClear - 10), "a main that need not cross runs up against a highway");
        }

        /// <summary>strawberry 2026-10-10: "fixing the gaps with vertical bends on bridges". Two rigid deck units meeting at
        /// a bend splay about the roadway's centre line, opening the corners on the outside of the turn: the deck's edge
        /// on a horizontal bend, the soffit at a sag, the parapet's top at a crest. At every joint of every deck run --
        /// the highways' bridges and the decks of every grade separation -- each of the six outer corners (edge and
        /// centre, soffit and parapet top) of the next unit starts no later along the run than the last one ends.</summary>
        [Test]
        public void DeckJointsCloseAtEveryCorner()
        {
            var runs = new List<List<BridgePiece>>();
            void Split(IEnumerable<BridgePiece> pieces)
            {
                var cur = new List<BridgePiece>();
                foreach (var bp in pieces)
                {
                    if (bp.Kind == 0) cur.Add(bp);
                    else if (bp.Kind == 2 && cur.Count > 0) { runs.Add(cur); cur = new List<BridgePiece>(); }   // a run's far cap ends it
                }
            }
            for (int axis = 0; axis < 2; axis++)
                for (long band = -2; band <= 1; band++)
                    for (long k = -4; k <= 3; k++) Split(Gen.Roads.BridgesOf(axis, band, k));
            for (long cx = -6; cx <= 5; cx++)
                for (long cz = -6; cz <= 5; cz++)
                    for (int dir = 0; dir < 2; dir++)
                        foreach (var u in Gen.Roads.MainUnderpasses(cx, cz, dir)) Split(u.Pieces);
            static (double x, double y, double z) Unit((double x, double y, double z) v) { double l = Math.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z); return (v.x / l, v.y / l, v.z / l); }
            static (double x, double y, double z) Cross((double x, double y, double z) a, (double x, double y, double z) b) => (a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
            double P = InfiniteRoads.BridgePitch, worst = 0, worstTurn = 0;
            int joints = 0, sags = 0, crests = 0, open = 0; string where = "";
            foreach (var run in runs)
                for (int i = 0; i + 1 < run.Count; i++)
                {
                    var A = run[i]; var B = run[i + 1];
                    joints++;
                    // each unit's frame: along (with its grade), across, up (square to both)
                    var da = Unit((A.DX, A.DY, A.DZ)); var db = Unit((B.DX, B.DY, B.DZ));
                    var la = Unit(Cross((0, 1, 0), (da.x, 0, da.z))); var lb = Unit(Cross((0, 1, 0), (db.x, 0, db.z)));
                    var ua = Unit(Cross(da, la)); var ub = Unit(Cross(db, lb));
                    if (ua.y < 0) ua = (-ua.x, -ua.y, -ua.z);
                    if (ub.y < 0) ub = (-ub.x, -ub.y, -ub.z);
                    double rise = Math.Asin(db.y) - Math.Asin(da.y);
                    worstTurn = Math.Max(worstTurn, Math.Abs(rise));
                    if (rise > 0.002) sags++; else if (rise < -0.002) crests++;
                    double hw = InfiniteRoads.BridgeHalfWidth * InfiniteRoads.DeckScale((RoadKind)A.Road);
                    bool any = false;
                    foreach (double up in new[] { (double)InfiniteRoads.DeckSoffit, InfiniteRoads.DeckParapetTop })
                        foreach (double lat in new[] { -hw, 0.0, hw })
                        {
                            // A's end corner and B's start corner, and how far B's starts PAST A's end along A
                            double ex = A.X + da.x * P / 2 + ua.x * up + la.x * lat, ey = A.Y + da.y * P / 2 + ua.y * up + la.y * lat, ez = A.Z + da.z * P / 2 + ua.z * up + la.z * lat;
                            double sx = B.X - db.x * P / 2 + ub.x * up + lb.x * lat, sy = B.Y - db.y * P / 2 + ub.y * up + lb.y * lat, sz = B.Z - db.z * P / 2 + ub.z * up + lb.z * lat;
                            double gap = (sx - ex) * da.x + (sy - ey) * da.y + (sz - ez) * da.z;
                            if (gap > 0.002) any = true;
                            if (gap > worst) { worst = gap; where = $" at ({A.X:0.0}, {A.Z:0.0}), {(up < 0 ? "soffit" : "parapet top")} {(lat == 0 ? "centre" : "edge")}, vertical turn {rise * 180 / Math.PI:+0.00;-0.00} deg"; }
                        }
                    if (any) open++;
                }
            TestContext.WriteLine($"{runs.Count} deck runs, {joints} joints ({sags} at a sag, {crests} at a crest; steepest vertical turn {worstTurn * 180 / Math.PI:0.00} deg): " +
                                  $"{open} open by over 2 mm at a corner, worst {worst * 1000:0.0} mm{where}");
            Assert.That(joints, Is.GreaterThan(5000)); Assert.That(sags, Is.GreaterThan(50)); Assert.That(crests, Is.GreaterThan(10));
            Assert.That(worst, Is.LessThan(0.002), "a deck joint stands open");
        }

        /// <summary>strawberry 2026-10-10: "work on a simple on/offramp for these areas". A diamond at each grade
        /// separation: every ramp starts INSIDE a highway carriageway at its surface, heading along it; ends just inside
        /// the main's asphalt at the main's surface, square to it; keeps to its grade; crosses no road on the way; keeps
        /// clear of the crossing's decks; and is carved into the ground like any road.</summary>
        [Test]
        public void RampsJoinTheirRoads()
        {
            var hw = new List<(double x, double z, float h)[]>();
            for (int axis = 0; axis < 2; axis++)
                for (long band = -2; band <= 1; band++)
                    for (long k = -4; k <= 3; k++) { var h = Gen.Roads.HighwayCentreline(axis, band, k); if (h != null) hw.Add(h); }
            static (double d, float h, double tx, double tz) Near((double x, double z, float h)[] p, double x, double z)
            {
                var r = (d: double.MaxValue, h: 0f, tx: 0.0, tz: 0.0);
                for (int i = 0; i + 1 < p.Length; i++)
                {
                    double sx = p[i + 1].x - p[i].x, sz = p[i + 1].z - p[i].z, ss = sx * sx + sz * sz;
                    if (ss < 1e-12) continue;
                    double t = Math.Clamp(((x - p[i].x) * sx + (z - p[i].z) * sz) / ss, 0, 1), px = p[i].x + sx * t, pz = p[i].z + sz * t;
                    double d = Math.Sqrt((x - px) * (x - px) + (z - pz) * (z - pz)), l = Math.Sqrt(ss);
                    if (d < r.d) r = (d, p[i].h + (p[i + 1].h - p[i].h) * (float)t, sx / l, sz / l);
                }
                return r;
            }
            static int Cross((double x, double z, float h)[] a, (double x, double z, float h)[] b)
            {
                int n = 0;
                for (int i = 0; i + 1 < a.Length; i++)
                    for (int j = 0; j + 1 < b.Length; j++)
                    {
                        double rx = a[i + 1].x - a[i].x, rz = a[i + 1].z - a[i].z, sx = b[j + 1].x - b[j].x, sz = b[j + 1].z - b[j].z, den = rx * sz - rz * sx;
                        if (Math.Abs(den) < 1e-12) continue;
                        double t = ((b[j].x - a[i].x) * sz - (b[j].z - a[i].z) * sx) / den, u = ((b[j].x - a[i].x) * rz - (b[j].z - a[i].z) * rx) / den;
                        if (t >= 0 && t < 1 && u >= 0 && u < 1) n++;
                    }
                return n;
            }
            double laneIn = InfiniteRoads.HighwayLaneHalf - InfiniteRoads.PavedHalf(RoadKind.Small);   // how far a ramp's centre may stray off its carriageway's
            float bed = InfiniteRoads.PavedHalf(RoadKind.Main) - 1f;
            int ramps = 0, full = 0, crossingsSeen = 0, carved = 0, crossed = 0;
            double worstLat = 0, worstStartH = 0, worstStartTurn = 0, worstEndOff = 0, worstEndH = 0, worstEndSquare = 90, worstGrade = 0, nearestDeck = double.MaxValue, worstGround = 0;
            string where = "";
            for (long cx = -6; cx <= 5; cx++)
                for (long cz = -6; cz <= 5; cz++)
                    for (int dir = 0; dir < 2; dir++)
                    {
                        var m = Gen.Roads.MainCentreline(cx, cz, dir);
                        var ups = Gen.Roads.MainUnderpasses(cx, cz, dir);
                        if (m == null || ups.Count == 0 || ups[0].Existing) continue;
                        crossingsSeen++;
                        var rs = Gen.Roads.MainRamps(cx, cz, dir);
                        if (rs.Count == 4) full++;
                        foreach (var r in rs)
                        {
                            ramps++;
                            // START: inside a carriageway, on its profile, heading along it
                            var s0 = r[0];
                            var nh = hw.Select(h => Near(h, s0.x, s0.z)).OrderBy(n => n.d).First();
                            double lat = Math.Abs(nh.d - InfiniteRoads.HighwayRibbonOffset);
                            if (lat > worstLat) { worstLat = lat; where = $" ramp from ({s0.x:0},{s0.z:0})"; }
                            worstStartH = Math.Max(worstStartH, Math.Abs(s0.h - nh.h));
                            double rx = r[1].x - r[0].x, rz = r[1].z - r[0].z, rl = Math.Sqrt(rx * rx + rz * rz);
                            worstStartTurn = Math.Max(worstStartTurn, Math.Acos(Math.Min(1.0, Math.Abs(rx * nh.tx + rz * nh.tz) / rl)) * 180 / Math.PI);
                            // END: just inside the main's asphalt, on its profile, square to it
                            var e1 = r[r.Length - 1];
                            var nm = Near(m, e1.x, e1.z);
                            worstEndOff = Math.Max(worstEndOff, Math.Abs(nm.d - bed));
                            worstEndH = Math.Max(worstEndH, Math.Abs(e1.h - nm.h));
                            double ex = e1.x - r[r.Length - 2].x, ez = e1.z - r[r.Length - 2].z, el = Math.Sqrt(ex * ex + ez * ez);
                            worstEndSquare = Math.Min(worstEndSquare, Math.Acos(Math.Min(1.0, Math.Abs(ex * nm.tx + ez * nm.tz) / el)) * 180 / Math.PI);
                            // GRADE, and nothing crossed: the main, any highway, the other ramps
                            for (int i = 0; i + 1 < r.Length; i++)
                            {
                                double l = Math.Sqrt((r[i + 1].x - r[i].x) * (r[i + 1].x - r[i].x) + (r[i + 1].z - r[i].z) * (r[i + 1].z - r[i].z));
                                worstGrade = Math.Max(worstGrade, Math.Abs(r[i + 1].h - r[i].h) / Math.Max(1e-9, l));
                            }
                            crossed += Cross(r, m) + hw.Sum(h => Cross(r, h)) + rs.Where(o => o != r).Sum(o => Cross(r, o));
                            // clear of the crossing's decks
                            foreach (var p in r) nearestDeck = Math.Min(nearestDeck, Math.Sqrt((p.x - ups[0].X) * (p.x - ups[0].X) + (p.z - ups[0].Z) * (p.z - ups[0].Z)));
                            // CARVED: along its middle third, where the ramp is the road there, the ground is its bed
                            for (int i = r.Length / 3; i < 2 * r.Length / 3; i++)
                            {
                                var hit = Gen.Roads.Influence(r[i].x, r[i].z);
                                if (!hit.Any || hit.Kind != RoadKind.Small || hit.Weight < 0.999f || Math.Abs(hit.Height - r[i].h) > 0.01f) continue;
                                carved++;
                                worstGround = Math.Max(worstGround, Math.Abs(Gen.HeightAt(r[i].x, r[i].z) - (r[i].h - InfiniteRoads.Bed)));
                            }
                        }
                    }
            TestContext.WriteLine($"{crossingsSeen} grade separations, {ramps} ramps ({full} with all four): start off its carriageway's centre {worstLat:0.00} m (room {laneIn:0.00}){where}, " +
                                  $"{worstStartH * 1000:0.0} mm off its profile, {worstStartTurn:0.0} deg off its heading; end {worstEndOff * 1000:0.0} mm off {bed:0.0} m in, {worstEndH * 1000:0.0} mm off the main's profile, " +
                                  $">= {worstEndSquare:0.0} deg to it; steepest {worstGrade * 100:0.0}% (limit {InfiniteRoads.MaxGrade(RoadKind.Small) * 100:0}); {crossed} crossings on the way; nearest the crossing {nearestDeck:0.0} m; " +
                                  $"ground on {carved} mid-ramp points within {worstGround * 1000:0.0} mm of the bed");
            Assert.That(ramps, Is.GreaterThan(40)); Assert.That(full, Is.GreaterThan(crossingsSeen / 2));
            Assert.That(worstLat, Is.LessThanOrEqualTo(laneIn + 0.01), "a ramp starts outside its carriageway");
            Assert.That(worstStartH, Is.LessThan(0.01), "a ramp starts off the carriageway's surface");
            Assert.That(worstStartTurn, Is.LessThan(3.0), "a ramp leaves at an angle");
            Assert.That(worstEndOff, Is.LessThan(0.05), "a ramp stops short of (or past) the main's edge");
            Assert.That(worstEndH, Is.LessThan(0.01), "a ramp ends off the main's surface");
            Assert.That(worstEndSquare, Is.GreaterThan(75), "a ramp meets the main askew");
            Assert.That(worstGrade, Is.LessThanOrEqualTo(InfiniteRoads.MaxGrade(RoadKind.Small) + 1e-4));
            Assert.That(crossed, Is.EqualTo(0), "a ramp crosses a road");
            Assert.That(nearestDeck, Is.GreaterThan(InfiniteRoads.CrossFlat + 10), "a ramp runs into the crossing's decks");
            Assert.That(carved, Is.GreaterThan(ramps * 2)); Assert.That(worstGround, Is.LessThan(0.02), "a ramp is not carved into the ground");
        }

        /// <summary>strawberry 2026-10-10: "implement the big pylon splines. cross country, their own network. make sure
        /// the pylons themselves dont overlap the roads". Over 36 node cells (31 km square): every tower's base is clear
        /// of every road's asphalt, on land, and no leg hangs over the ground; every span is one PowerLineField will
        /// string; every conductor -- measured here from the towers' own headings and the prop's anchors, not the
        /// generator's shortcut -- stays clear of the ground and any road deck all the way along; no tree grows in a
        /// line's corridor; and the regions together string every span exactly once.</summary>
        [Test]
        public void PylonsStandClearOfRoadsAndStringEverySpanOnce()
        {
            var lines = new List<List<InfinitePylons.Tower>>();
            for (long cx = -3; cx < 3; cx++)
                for (long cz = -3; cz < 3; cz++)
                    for (int dir = 0; dir < 2; dir++) { var tw = Gen.Pylons.TowersOf(cx, cz, dir); if (tw != null) lines.Add(tw); }
            int towers = 0, spans = 0, onRoad = 0, wet = 0, floating = 0, badSpan = 0, low = 0, onMountain = 0, overPass = 0;
            double worstMountain = 0;
            double nearest = double.MaxValue, worstFloat = 0, shortest = double.MaxValue, longest = 0, worstGap = double.MaxValue;
            string nearWhere = "", gapWhere = "";
            // the prop's two lowest conductor pairs (lower arm, middle arm), local to a tower: x across the line, z up
            var anchors = new[] { (4.708f, 13.473f), (5.952f, 19.209f) };
            foreach (var tw in lines)
            {
                for (int i = 0; i < tw.Count; i++)
                {
                    var t = tw[i];
                    towers++;
                    double clear = Gen.Roads.Influence(t.X, t.Z).Clear;
                    if (clear < nearest) { nearest = clear; nearWhere = $" at ({t.X:0},{t.Z:0})"; }
                    if (clear < InfinitePylons.RoadKeep) onRoad++;
                    if (Gen.NaturalHeight(t.X, t.Z) < InfiniteTerrain.SeaLevel + 2f) wet++;
                    // OFF THE MOUNTAINS (strawberry 2026-10-10: "try to reroute them to not go over mountains, hills are
                    // okay"): on more than MountainLimit of mountain only where a pass is the only way, never past PassLimit
                    float mh = Gen.MountainHeight(t.X, t.Z);
                    worstMountain = Math.Max(worstMountain, mh);
                    if (mh > InfinitePylons.MountainLimit) onMountain++;
                    if (mh > InfinitePylons.PassLimit) overPass++;
                    // the four feet of the base, square to the tower's own heading: none above the ground
                    double ax = -t.DirZ, az = t.DirX, f = InfinitePylons.FootHalf;
                    foreach (var (u, v) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
                    {
                        double fx = t.X + t.DirX * f * u + ax * f * v, fz = t.Z + t.DirZ * f * u + az * f * v;
                        double air = t.H - Gen.HeightAt(fx, fz);
                        worstFloat = Math.Max(worstFloat, air);
                        if (air > 0.01) floating++;
                    }
                    if (i + 1 == tw.Count) continue;
                    var n = tw[i + 1];
                    spans++;
                    double sl = Math.Sqrt((n.X - t.X) * (n.X - t.X) + (n.Z - t.Z) * (n.Z - t.Z));
                    shortest = Math.Min(shortest, sl); longest = Math.Max(longest, sl);
                    if (sl > InfinitePylons.MaxSpan || sl < InfinitePylons.MinClimbSpan) badSpan++;
                    // each conductor of the lower two arms, both sides, from its anchor on this tower to the same anchor
                    // on the next (each tower's arms square to ITS heading), sagging as PowerLineField.SpanPoint draws it
                    double bx = -n.DirZ, bz = n.DirX;
                    foreach (var (reach, up) in anchors)
                        for (int sd = -1; sd <= 1; sd += 2)
                        {
                            // pair the sides by which way each tower's arm points, as the field pairs mirror-symmetric anchors
                            double sa = sd, sb = (ax * bx + az * bz) >= 0 ? sd : -sd;
                            double x0 = t.X + ax * reach * InfinitePylons.Scale * sa, z0 = t.Z + az * reach * InfinitePylons.Scale * sa, y0 = t.H + up * InfinitePylons.Scale;
                            double x1 = n.X + bx * reach * InfinitePylons.Scale * sb, z1 = n.Z + bz * reach * InfinitePylons.Scale * sb, y1 = n.H + up * InfinitePylons.Scale;
                            double len = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0) + (z1 - z0) * (z1 - z0));
                            for (int k = 1; k < 40; k++)
                            {
                                double s = k / 40.0, x = x0 + (x1 - x0) * s, z = z0 + (z1 - z0) * s;
                                double y = y0 + (y1 - y0) * s - len * InfinitePylons.SagFraction * 4 * s * (1 - s);
                                double under = Gen.HeightAt(x, z);
                                var hit = Gen.Roads.Influence(x, z);
                                if (hit.Any && hit.Clear < 0) under = Math.Max(under, InfiniteRoads.SurfaceY(hit.Kind, hit.Height) + InfiniteRoads.DeckParapetTop);
                                double gap = y - under;
                                if (gap < worstGap) { worstGap = gap; gapWhere = $" at ({x:0},{z:0})"; }
                                if (gap < 3.0) low++;
                            }
                        }
                }
            }
            // OWNERSHIP: the regions over every line, together, string each of its spans exactly once
            var owned = new Dictionary<(long, long, long, long), int>();
            static long Q(double v) => (long)Math.Round(v * 10);
            var regions = new HashSet<(int, int)>();
            foreach (var tw in lines) foreach (var t in tw) regions.Add(((int)Math.Floor(t.X / InfiniteTerrain.RegionSize), (int)Math.Floor(t.Z / InfiniteTerrain.RegionSize)));
            foreach (var (rx, rz) in regions)
                foreach (var p in Gen.Pylons.PylonsIn(rx * (double)InfiniteTerrain.RegionSize, rz * (double)InfiniteTerrain.RegionSize, (rx + 1) * (double)InfiniteTerrain.RegionSize, (rz + 1) * (double)InfiniteTerrain.RegionSize))
                    foreach (var w in p.Wired)
                    {
                        var key = Q(p.X) < Q(w.X) || Q(p.X) == Q(w.X) && Q(p.Z) < Q(w.Z) ? (Q(p.X), Q(p.Z), Q(w.X), Q(w.Z)) : (Q(w.X), Q(w.Z), Q(p.X), Q(p.Z));
                        owned[key] = owned.GetValueOrDefault(key) + 1;
                    }
            int missing = 0, twice = 0;
            foreach (var tw in lines)
                for (int i = 0; i + 1 < tw.Count; i++)
                {
                    var a = tw[i]; var b = tw[i + 1];
                    var key = Q(a.X) < Q(b.X) || Q(a.X) == Q(b.X) && Q(a.Z) < Q(b.Z) ? (Q(a.X), Q(a.Z), Q(b.X), Q(b.Z)) : (Q(b.X), Q(b.Z), Q(a.X), Q(a.Z));
                    int c = owned.GetValueOrDefault(key);
                    if (c == 0) missing++; else if (c > 1) twice++;
                }
            // TREES: none in a corridor, over the regions a line crosses
            int treesNear = 0, treesSeen = 0;
            var pl = new List<InfinitePylons.Line>();
            foreach (var (rx, rz) in regions.Take(40))
            {
                var rc = new RegionCoord(rx, rz);
                var lin = Gen.Pylons.LinesIn(rc.MinX, rc.MinZ, rc.MinX + InfiniteTerrain.RegionSize, rc.MinZ + InfiniteTerrain.RegionSize, 50);
                foreach (var tr in Gen.PlaceTrees(rc))
                {
                    treesSeen++;
                    if (InfinitePylons.CorridorDistance(lin, rc.MinX + tr.X, rc.MinZ + tr.Z) < InfinitePylons.TreeKeep) treesNear++;
                }
            }
            TestContext.WriteLine($"{lines.Count} lines, {towers} towers, {spans} spans ({shortest:0}..{longest:0} m; limit {InfinitePylons.MaxSpan}): nearest asphalt to a tower {nearest:0.0} m{nearWhere} (keep {InfinitePylons.RoadKeep}), " +
                                  $"{onRoad} too near, {wet} wet; {onMountain} on more than {InfinitePylons.MountainLimit} m of mountain (passes), {overPass} past {InfinitePylons.PassLimit}, worst {worstMountain:0} m; feet above the ground {floating} (worst {worstFloat * 1000:0.0} mm); lowest conductor {worstGap:0.0} m over the ground or a road{gapWhere}, {low} samples under 3 m; " +
                                  $"spans strung {spans - missing - twice} once, {missing} never, {twice} twice; {treesNear} of {treesSeen} trees in a corridor");
            Assert.That(lines.Count, Is.GreaterThan(10)); Assert.That(towers, Is.GreaterThan(200));
            Assert.That(onRoad, Is.EqualTo(0), "a pylon stands on (or at the edge of) a road");
            Assert.That(wet, Is.EqualTo(0), "a pylon stands in the sea");
            Assert.That(overPass, Is.EqualTo(0), "a pylon stands on a mountain");
            Assert.That(onMountain, Is.LessThanOrEqualTo(towers / 20), "lines run over mountain passes as a habit, not a last resort");
            Assert.That(floating, Is.EqualTo(0), "a pylon's foot hangs above the ground");
            Assert.That(badSpan, Is.EqualTo(0), "a span PowerLineField would refuse, or two towers on top of each other");
            Assert.That(worstGap, Is.GreaterThan(3.0), "a conductor runs into the ground or a road");
            Assert.That(missing + twice, Is.EqualTo(0), "a span strung by no region, or by two");
            Assert.That(treesSeen, Is.GreaterThan(1000)); Assert.That(treesNear, Is.EqualTo(0), "a tree in a line's corridor");
        }

        /// <summary>The network is a pure function of the seed: a fresh generator asked about a far corner first, then the
        /// same region, places the same towers (streaming builds lines in whatever order regions arrive).</summary>
        [Test]
        public void PylonsAreTheSameWhoeverAsks()
        {
            var fresh = new InfiniteTerrain(1337);
            fresh.Pylons.PylonsIn(9000, 9000, 9256, 9256);
            var a = Gen.Pylons.PylonsIn(-5000, -5000, 5000, 5000); var b = fresh.Pylons.PylonsIn(-5000, -5000, 5000, 5000);
            TestContext.WriteLine($"{a.Count} towers from the shared generator, {b.Count} from a fresh one (a 10 km square)");
            Assert.That(a.Count, Is.GreaterThan(20));
            Assert.That(b.Select(p => (p.X, p.Z, p.H, p.Wired.Length)).OrderBy(p => p.X).ThenBy(p => p.Z),
                        Is.EqualTo(a.Select(p => (p.X, p.Z, p.H, p.Wired.Length)).OrderBy(p => p.X).ThenBy(p => p.Z)));
        }

        /// <summary>strawberry 2026-10-10: "increase the dirt painted footprint of all roads, add it to the power lines,
        /// both big and small ones". Over LOD0 regions round a pylon and along roads: every ground vertex nearer a road's
        /// asphalt than its verge (less the edge noise) wears the road's bed; every vertex inside a pole's or a pylon's
        /// footing (on land) wears it too.</summary>
        [Test]
        public void RoadVergesAndPowerFootingsAreWorn()
        {
            int vergeVerts = 0, vergeBare = 0, poleVerts = 0, poleBare = 0, pylonVerts = 0, pylonBare = 0, poles = 0, pylons = 0;
            var tower = Gen.Pylons.PylonsIn(-6000, -6000, 6000, 6000).OrderBy(p => p.X * p.X + p.Z * p.Z).First();
            var home = RegionCoord.Containing(tower.X, tower.Z);
            for (int dz = -2; dz <= 2; dz++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    var rc = new RegionCoord(home.X + dx, home.Z + dz);
                    var d = Gen.Generate(rc, 0);
                    int v = d.Cells + 1;
                    var near = d.Pylons.Select(p => (p.X, p.Z, r: InfiniteTerrain.PylonDirt)).Concat(d.Poles.Select(p => (p.X, p.Z, r: InfiniteTerrain.PoleDirt))).ToList();
                    poles += d.Poles.Count; pylons += d.Pylons.Count;
                    for (int j = 0; j < v; j++)
                        for (int i = 0; i < v; i++)
                        {
                            int k = j * v + i;
                            if (d.Heights[k] < InfiniteTerrain.SeaLevel + 1.6f || d.RoadClear[k] < 0) continue;
                            double x = rc.MinX + i * (double)d.Spacing, z = rc.MinZ + j * (double)d.Spacing;
                            var l = (InfiniteTerrain.Layer)d.Layers[k];
                            bool worn = l == InfiniteTerrain.Layer.Gravel || l == InfiniteTerrain.Layer.Dirt;
                            var hit = Gen.Roads.Influence(x, z);
                            if (hit.Any && d.RoadClear[k] < InfiniteTerrain.VergeWidth(hit.Kind) - 1.2f) { vergeVerts++; if (!worn) vergeBare++; }
                            foreach (var (fx, fz, r) in near)
                                if ((x - fx) * (x - fx) + (z - fz) * (z - fz) <= r * r)
                                {
                                    if (r == InfiniteTerrain.PylonDirt) { pylonVerts++; if (!worn) pylonBare++; } else { poleVerts++; if (!worn) poleBare++; }
                                    break;
                                }
                        }
                }
            TestContext.WriteLine($"25 regions: {vergeVerts} verge vertices ({vergeBare} bare); {poles} poles' footings {poleVerts} vertices ({poleBare} bare); {pylons} pylons' {pylonVerts} ({pylonBare} bare)");
            Assert.That(vergeVerts, Is.GreaterThan(500)); Assert.That(poleVerts, Is.GreaterThan(20)); Assert.That(pylonVerts, Is.GreaterThan(5));
            Assert.That(vergeBare, Is.EqualTo(0), "a road's verge vertex is not worn");
            Assert.That(poleBare + pylonBare, Is.EqualTo(0), "a power line's footing is not worn");
        }

        [Test]
        public void HighwaysAreTwoCarriageways()
        {
            // find a region a highway passes through, then require its Highway pieces to come in two parallel ribbons
            foreach (var axis in new[] { 0, 1 })
                for (long band = -1; band <= 0; band++)
                {
                    var h = Gen.Roads.HighwayCentreline(axis, band, 0);
                    if (h == null) continue;
                    var mid = h[h.Length / 2];
                    var d = Gen.Generate(RegionCoord.Containing(mid.x, mid.z), 0);
                    var hw = d.Roads.FindAll(p => p.Kind == (byte)RoadKind.Highway);
                    Assert.That(hw.Count, Is.GreaterThan(4));
                    // every highway piece's midpoint is HighwayRibbonOffset from the route's centreline, on one side or the other
                    int left = 0, right = 0;
                    foreach (var p in hw)
                    {
                        double mx = (p.X0 + p.X1) * 0.5, mz = (p.Z0 + p.Z1) * 0.5;
                        double best = double.MaxValue, side = 0;
                        for (int i = 0; i + 1 < h.Length; i++)
                        {
                            double sx = h[i + 1].x - h[i].x, sz = h[i + 1].z - h[i].z, qx = mx - h[i].x, qz = mz - h[i].z;
                            double t = Math.Clamp((qx * sx + qz * sz) / (sx * sx + sz * sz), 0, 1);
                            double ex = qx - sx * t, ez = qz - sz * t, dd = Math.Sqrt(ex * ex + ez * ez);
                            if (dd < best) { best = dd; side = sx * ez - sz * ex; }
                        }
                        Assert.That(best, Is.EqualTo(InfiniteRoads.HighwayRibbonOffset).Within(0.6), "a carriageway sits its offset from the route");
                        if (side > 0) left++; else right++;
                    }
                    TestContext.WriteLine($"highway in {d.Coord}: {left} pieces one side, {right} the other");
                    Assert.That(left, Is.GreaterThan(0)); Assert.That(right, Is.GreaterThan(0));
                    return;
                }
            Assert.Fail("no highway found near the origin to inspect");
        }

        [Test]
        public void BranchesLeaveFromAMainRoad()
        {
            int small = 0, trail = 0;
            for (long cx = -3; cx <= 3; cx++)
                for (long cz = -3; cz <= 3; cz++)
                    for (int dir = 0; dir < 2; dir++)
                    {
                        var main = Gen.Roads.MainCentreline(cx, cz, dir);
                        if (main == null) continue;
                        foreach (var (kind, pts) in Gen.Roads.BranchesOf(cx, cz, dir))
                        {
                            if (kind == RoadKind.Small) small++; else if (kind == RoadKind.Trail) trail++;
                            Assert.That(kind, Is.AnyOf(RoadKind.Small, RoadKind.Trail));
                            var (x0, z0, h0) = pts[0];
                            double best = double.MaxValue; float mh = 0;
                            for (int i = 0; i + 1 < main.Length; i++)
                            {
                                double sx = main[i + 1].x - main[i].x, sz = main[i + 1].z - main[i].z, qx = x0 - main[i].x, qz = z0 - main[i].z;
                                double t = Math.Clamp((qx * sx + qz * sz) / (sx * sx + sz * sz), 0, 1);
                                double ex = qx - sx * t, ez = qz - sz * t, dd = Math.Sqrt(ex * ex + ez * ez);
                                if (dd < best) { best = dd; mh = main[i].h + (main[i + 1].h - main[i].h) * (float)t; }
                            }
                            Assert.That(best, Is.LessThan(InfiniteRoads.PavedHalf(RoadKind.Main)), "a branch must start ON its main road");
                            // ...at the main's profile at the NEAREST point, which is what the carve reads; pinning to the
                            // arc position the branch was spawned from instead put starts 0.4 m off on a 15% grade
                            Assert.That(h0, Is.EqualTo(mh).Within(0.01f), "...at the main road's own height");
                        }
                    }
            TestContext.WriteLine($"{small} small roads, {trail} trails off the mains within 10.7 km");
            Assert.That(small, Is.GreaterThan(0)); Assert.That(trail, Is.GreaterThan(0));
        }

        [Test]
        public void RoadsAreTheSameWhoeverAsks()
        {
            var a = Gen.Roads.MainCentreline(1, 1, 0) ?? Gen.Roads.MainCentreline(1, 1, 1) ?? Gen.Roads.MainCentreline(0, 1, 0);
            Assert.That(a, Is.Not.Null, "need one road to compare");
            var fresh = new InfiniteTerrain(1337);
            fresh.Generate(new RegionCoord(5, 5), 0);   // a different first question for the fresh instance's cache
            var b = fresh.Roads.MainCentreline(1, 1, 0) ?? fresh.Roads.MainCentreline(1, 1, 1) ?? fresh.Roads.MainCentreline(0, 1, 0);
            Assert.That(b, Is.EqualTo(a));
            Assert.That(fresh.Roads.HighwayCentreline(0, 0, 0), Is.EqualTo(Gen.Roads.HighwayCentreline(0, 0, 0)));
        }

        // POWER LINES: main and small roads only (not highways, not dirt trails -- strawberry 2026-10-09). Every pole stands
        // off the asphalt, and a span that leaves a region lands on a pole the NEXT region also places.
        [Test]
        public void PolesLineTheRoadsAndMeetAcrossBorders()
        {
            Assert.That(InfiniteRoads.HasPowerLines(RoadKind.Main) && InfiniteRoads.HasPowerLines(RoadKind.Small));
            Assert.That(!InfiniteRoads.HasPowerLines(RoadKind.Highway) && !InfiniteRoads.HasPowerLines(RoadKind.Trail));
            int poles = 0, crossings = 0;
            var byRegion = new Dictionary<RegionCoord, List<PolePlacement>>();
            List<PolePlacement> PolesOf(RegionCoord rc)
            {
                if (!byRegion.TryGetValue(rc, out var l)) byRegion[rc] = l = Gen.Generate(rc, 1).Poles;
                return l;
            }
            for (int rz = 0; rz < 8; rz++)
                for (int rx = 0; rx < 8; rx++)
                {
                    var rc = new RegionCoord(rx, rz);
                    foreach (var p in PolesOf(rc))
                    {
                        poles++;
                        float clear = Gen.RoadClearance(p.X, p.Z);
                        Assert.That(clear, Is.GreaterThan(0f), $"pole at ({p.X:0},{p.Z:0}) stands ON asphalt ({clear:0.0} m)");
                        Assert.That(p.H, Is.GreaterThan(InfiniteTerrain.SeaLevel));
                        if (!p.HasNext) continue;
                        double span = Math.Sqrt((p.NX - p.X) * (p.NX - p.X) + (p.NZ - p.Z) * (p.NZ - p.Z));
                        Assert.That(span, Is.InRange(15.0, 2 * InfiniteRoads.PoleSpacing + 1.0), "a span is one pole spacing, two where a pole was skipped");
                        var nrc = RegionCoord.Containing(p.NX, p.NZ);
                        if (nrc.Equals(rc)) continue;
                        crossings++;
                        bool found = false;
                        foreach (var q in PolesOf(nrc)) if (Math.Abs(q.X - p.NX) < 1e-6 && Math.Abs(q.Z - p.NZ) < 1e-6) { found = true; break; }
                        Assert.That(found, $"the span from ({p.X:0},{p.Z:0}) ends at ({p.NX:0},{p.NZ:0}) in {nrc}, which places no pole there");
                    }
                }
            TestContext.WriteLine($"{poles} poles over 2 km x 2 km, {crossings} spans crossing a region border");
            Assert.That(poles, Is.GreaterThan(20));
            Assert.That(crossings, Is.GreaterThan(0), "no span ever crossed a border -- the cross-border case went untested");
        }

        [Test]
        public void FoliageGrowsOnlyWhereItBelongs()
        {
            int grass = 0, flowers = 0, bushes = 0, pebbles = 0;
            for (int k = 0; k < 6; k++)
            {
                var d = Gen.Generate(new RegionCoord(k * 3 - 6, 2 - k), 0);
                int v = d.Cells + 1;
                foreach (var f in d.Foliage)
                {
                    Assert.That(f.X, Is.InRange(0f, InfiniteTerrain.RegionSize));
                    Assert.That(f.Z, Is.InRange(0f, InfiniteTerrain.RegionSize));
                    var layer = (InfiniteTerrain.Layer)d.Layers[Math.Clamp((int)MathF.Round(f.Z / d.Spacing), 0, d.Cells) * v + Math.Clamp((int)MathF.Round(f.X / d.Spacing), 0, d.Cells)];
                    Assert.That(layer, Is.Not.EqualTo(InfiniteTerrain.Layer.Road), "the Road layer is car-park paving; the infinite world never paints it");
                    Assert.That(Gen.RoadClearance(d.Coord.MinX + f.X, d.Coord.MinZ + f.Z), Is.GreaterThan(0f), "nothing grows through the asphalt");
                    switch ((InfiniteTerrain.FoliageKind)f.Kind)
                    {
                        case InfiniteTerrain.FoliageKind.Grass: grass++; Assert.That(layer, Is.AnyOf(InfiniteTerrain.Layer.Grass, InfiniteTerrain.Layer.Wheat, InfiniteTerrain.Layer.Dirt)); break;
                        case InfiniteTerrain.FoliageKind.Bush0: case InfiniteTerrain.FoliageKind.Bush1: bushes++; break;
                        case InfiniteTerrain.FoliageKind.Pebble: case InfiniteTerrain.FoliageKind.PebbleSand: pebbles++; break;
                        default: flowers++; Assert.That(layer, Is.EqualTo(InfiniteTerrain.Layer.Grass)); break;
                    }
                    Assert.That(f.Y, Is.GreaterThan(InfiniteTerrain.SeaLevel), "nothing grows under the sea");
                }
            }
            TestContext.WriteLine($"6 regions: grass {grass}, flowers {flowers}, bushes {bushes}, pebbles {pebbles}");
            Assert.That(grass, Is.GreaterThan(6 * 5000), "grass should carpet a temperate region");
            Assert.That(flowers + bushes, Is.GreaterThan(0));
        }

        [Test]
        public void HeightsStayInsideTheWiresYRange()
        {
            // the pylon network does not touch the ground, and a cold region pays for every 5 km line that could reach
            // it (and the roads along it): off here, or this alone is two minutes
            InfinitePylons.Enabled = false;
            try
            {
                for (int k = 0; k < 120; k++)   // scattered far apart, so every one builds its own roads cold
                {
                    var d = Gen.Generate(new RegionCoord(k * 23 - 1400, k * 43 - 2600), 3);
                    Assert.That(d.MaxHeight, Is.LessThanOrEqualTo(InfiniteTerrain.MaxHeight));
                    Assert.That(d.MinHeight, Is.GreaterThan(-256f));
                }
            }
            finally { InfinitePylons.Enabled = true; }
        }
    }
}
