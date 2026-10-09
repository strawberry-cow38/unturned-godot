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
            bool bridges = InfiniteRoads.Bridges;
            InfiniteRoads.Bridges = false;
            try { CheckStretchesWith(cut); } finally { InfiniteRoads.Bridges = bridges; }
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
            double worstGap = 0, worstDy = 0, worstTurn = 0, worstBank = 0, worstPhase = 0, worstDeckPhase = 0;
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
                            double best = double.MaxValue; float by = 0, btx = 0, btz = 0, bs = 0;
                            foreach (var rp in pieces)
                            {
                                if (rp.Kind != (byte)RoadKind.Highway) continue;
                                for (int e = 0; e < 2; e++)
                                {
                                    double ex = e == 0 ? rp.X0 : rp.X1, ez = e == 0 ? rp.Z0 : rp.Z1;
                                    float ey = InfiniteRoads.SurfaceY(RoadKind.Highway, e == 0 ? rp.H0 : rp.H1);
                                    double d = Math.Sqrt((ex - cap.X) * (ex - cap.X) + (ey - cap.Y) * (ey - cap.Y) + (ez - cap.Z) * (ez - cap.Z));
                                    if (d < best) { best = d; by = ey; btx = e == 0 ? rp.T0X : rp.T1X; btz = e == 0 ? rp.T0Z : rp.T1Z; bs = e == 0 ? rp.S0 : rp.S1; }
                                }
                            }
                            if (best > worstGap) { worstGap = best; gapWhere = $" at ({cap.X:0.0}, {cap.Z:0.0})"; }
                            worstDy = Math.Max(worstDy, Math.Abs(by - cap.Y));
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
                                  $"paint phase {worstPhase * 1000:0.0} mm (and {worstDeckPhase * 1000:0.0} mm across {deckJoints} deck joints); " +
                                  $"ground 0.5 m off the deck end at {embank} of them within {worstBank * 1000:0.0} mm of the bed{bankWhere}");
            Assert.That(ends, Is.GreaterThan(40));
            Assert.That(embank, Is.GreaterThan(ends / 2), "the embankment check ran at most ends");
            Assert.That(worstGap, Is.LessThan(0.005), "a bridge end the ribbon does not reach (or overruns)");
            Assert.That(worstDy, Is.LessThan(0.005), "a step between the ribbon and the deck");
            Assert.That(worstTurn, Is.LessThan(0.5), "a kink between the ribbon and the deck");
            Assert.That(deckJoints, Is.GreaterThan(1000));
            Assert.That(worstPhase, Is.LessThan(0.01), "the dashes restart at a bridge end");
            Assert.That(worstDeckPhase, Is.LessThan(0.01), "the dashes jump at a deck joint");
            Assert.That(worstBank, Is.LessThan(0.01), "the embankment stops short of the deck");
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
            for (int k = 0; k < 120; k++)   // scattered far apart, so every one builds its own roads cold
            {
                var d = Gen.Generate(new RegionCoord(k * 23 - 1400, k * 43 - 2600), 3);
                Assert.That(d.MaxHeight, Is.LessThanOrEqualTo(InfiniteTerrain.MaxHeight));
                Assert.That(d.MinHeight, Is.GreaterThan(-256f));
            }
        }
    }
}
