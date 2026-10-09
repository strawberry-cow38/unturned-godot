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
