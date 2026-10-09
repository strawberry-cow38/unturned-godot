using System;
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

        [Test]
        public void HeightsStayInsideTheWiresYRange()
        {
            for (int k = 0; k < 400; k++)
            {
                var d = Gen.Generate(new RegionCoord(k * 7 - 1400, k * 13 - 2600), 3);
                Assert.That(d.MaxHeight, Is.LessThanOrEqualTo(InfiniteTerrain.MaxHeight));
                Assert.That(d.MinHeight, Is.GreaterThan(-256f));
            }
        }
    }
}
