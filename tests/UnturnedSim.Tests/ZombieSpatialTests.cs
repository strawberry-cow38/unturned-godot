using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedSim.Tests
{
    // L0 tests for the zombie spatial hash (rewrite plan phase 0).
    //
    // The grid's whole contract is that it is an ACCELERATOR, not an approximation: it must return
    // exactly what a brute-force scan would. So the important tests here are differential -- build a
    // field, ask the grid, ask a linear scan, demand the same set. Hash collisions, negative
    // coordinates and cell-boundary cases are then covered by construction rather than by guessing
    // which ones are dangerous.
    [TestFixture]
    public class ZombieSpatialTests
    {
        // Deterministic PRNG: a seeded LCG, so a failure is reproducible instead of a Tuesday.
        sealed class Lcg
        {
            uint _s;
            public Lcg(uint seed) { _s = seed; }
            public float Unit() { _s = _s * 1664525u + 1013904223u; return (_s >> 8) * (1f / 16777216f); }
            public float Range(float a, float b) => a + (b - a) * Unit();
        }

        static Vector3[] Field(int n, uint seed, float extent = 400f)
        {
            var rng = new Lcg(seed);
            var p = new Vector3[n];
            for (int i = 0; i < n; i++)
                p[i] = new Vector3(rng.Range(-extent, extent), rng.Range(-30f, 30f), rng.Range(-extent, extent));
            return p;
        }

        static List<int> BruteSphere(Vector3[] pos, int n, Vector3 c, float r)
        {
            var o = new List<int>();
            for (int i = 0; i < n; i++) if ((pos[i] - c).sqrMagnitude <= r * r) o.Add(i);
            return o;
        }

        static List<int> BruteSegment(Vector3[] pos, int n, Vector3 a, Vector3 b, float r)
        {
            var o = new List<int>();
            for (int i = 0; i < n; i++)
                if (ZombieSpatial.SqrDistanceToSegment(pos[i], a, b - a) <= r * r) o.Add(i);
            return o;
        }

        [Test]
        public void Sphere_Query_Matches_Brute_Force()
        {
            var pos = Field(600, 0xC0FFEE);
            var grid = new ZombieSpatial();
            grid.Build(pos, pos.Length);

            var rng = new Lcg(7);
            var buf = new int[pos.Length];
            for (int q = 0; q < 60; q++)
            {
                var c = new Vector3(rng.Range(-450f, 450f), rng.Range(-40f, 40f), rng.Range(-450f, 450f));
                float r = rng.Range(0.5f, 60f);
                int n = grid.QuerySphere(c, r, buf);
                CollectionAssert.AreEquivalent(BruteSphere(pos, pos.Length, c, r), buf.Take(n).ToList(),
                    $"sphere query {q} at {c} r={r}");
            }
        }

        [Test]
        public void Segment_Query_Matches_Brute_Force()
        {
            var pos = Field(600, 0xBEEF);
            var grid = new ZombieSpatial();
            grid.Build(pos, pos.Length);

            var rng = new Lcg(99);
            var buf = new int[pos.Length];
            for (int q = 0; q < 60; q++)
            {
                var a = new Vector3(rng.Range(-450f, 450f), rng.Range(-30f, 30f), rng.Range(-450f, 450f));
                var b = new Vector3(rng.Range(-450f, 450f), rng.Range(-30f, 30f), rng.Range(-450f, 450f));
                float r = rng.Range(0.2f, 12f);
                int n = grid.QuerySegment(a, b, r, buf);
                CollectionAssert.AreEquivalent(BruteSegment(pos, pos.Length, a, b, r), buf.Take(n).ToList(),
                    $"segment query {q} {a}->{b} r={r}");
            }
        }

        [Test]
        public void Segment_Query_Spans_Cells_Far_Beyond_One_Cell()
        {
            // A 300 m shot with 8 m cells crosses ~38 cells; the traversal must not stop at the first.
            var pos = new Vector3[40];
            for (int i = 0; i < pos.Length; i++) pos[i] = new Vector3(i * 8f, 0f, 0f);
            var grid = new ZombieSpatial();
            grid.Build(pos, pos.Length);

            var buf = new int[64];
            int n = grid.QuerySegment(new Vector3(-4f, 0f, 0f), new Vector3(320f, 0f, 0f), 0.5f, buf);
            Assert.That(n, Is.EqualTo(40), "every zombie strung along the ray should be a candidate");
        }

        [Test]
        public void Segment_Query_Rejects_Items_Beside_The_Line()
        {
            var pos = new[] { new Vector3(50f, 0f, 0.4f), new Vector3(50f, 0f, 3f) };
            var grid = new ZombieSpatial();
            grid.Build(pos, pos.Length);

            var buf = new int[8];
            int n = grid.QuerySegment(Vector3.zero, new Vector3(100f, 0f, 0f), 0.5f, buf);
            Assert.That(n, Is.EqualTo(1));
            Assert.That(buf[0], Is.EqualTo(0), "only the one within the widened radius");
        }

        [Test]
        public void Degenerate_Segment_Behaves_Like_A_Point_Query()
        {
            var pos = new[] { new Vector3(10f, 0f, 10f), new Vector3(10f, 0f, 14f) };
            var grid = new ZombieSpatial();
            grid.Build(pos, pos.Length);

            var buf = new int[8];
            int n = grid.QuerySegment(new Vector3(10f, 0f, 10f), new Vector3(10f, 0f, 10f), 1f, buf);
            Assert.That(n, Is.EqualTo(1));
            Assert.That(buf[0], Is.EqualTo(0));
        }

        [Test]
        public void Negative_Coordinates_Are_Not_A_Special_Case()
        {
            var pos = new[] { new Vector3(-100.5f, -3f, -100.5f), new Vector3(-104f, -3f, -100f), new Vector3(200f, 0f, 200f) };
            var grid = new ZombieSpatial();
            grid.Build(pos, pos.Length);

            var buf = new int[8];
            int n = grid.QuerySphere(new Vector3(-102f, -3f, -100f), 5f, buf);
            CollectionAssert.AreEquivalent(new[] { 0, 1 }, buf.Take(n).ToList());
        }

        [Test]
        public void Empty_Grid_Answers_Nothing()
        {
            var grid = new ZombieSpatial();
            grid.Build(System.Array.Empty<Vector3>(), 0);
            var buf = new int[4];
            Assert.That(grid.QuerySphere(Vector3.zero, 100f, buf), Is.EqualTo(0));
            Assert.That(grid.QuerySegment(Vector3.zero, new Vector3(100f, 0f, 0f), 5f, buf), Is.EqualTo(0));
        }

        [Test]
        public void Rebuild_Reflects_Moved_Items_And_Leaks_Nothing_From_The_Previous_Build()
        {
            var pos = new[] { new Vector3(0f, 0f, 0f), new Vector3(300f, 0f, 300f) };
            var grid = new ZombieSpatial();
            grid.Build(pos, pos.Length);
            var buf = new int[8];
            Assert.That(grid.QuerySphere(Vector3.zero, 5f, buf), Is.EqualTo(1));

            pos[0] = new Vector3(300f, 0f, 300f);   // walked away
            grid.Build(pos, pos.Length);
            Assert.That(grid.QuerySphere(Vector3.zero, 5f, buf), Is.EqualTo(0), "stale cell contents survived a rebuild");
            Assert.That(grid.QuerySphere(new Vector3(300f, 0f, 300f), 5f, buf), Is.EqualTo(2));
        }

        [Test]
        public void Shrinking_The_Live_Count_Drops_The_Tail()
        {
            var pos = Field(200, 42);
            var grid = new ZombieSpatial();
            grid.Build(pos, pos.Length);
            grid.Build(pos, 50);                    // 150 despawned; array still holds their positions
            Assert.That(grid.Count, Is.EqualTo(50));

            var buf = new int[256];
            int n = grid.QuerySphere(Vector3.zero, 10000f, buf);
            Assert.That(n, Is.EqualTo(50));
            Assert.That(buf.Take(n).Max(), Is.LessThan(50), "returned a row past the live count");
        }

        [Test]
        public void A_Radius_Larger_Than_The_Level_Answers_In_Item_Time_Not_Cell_Time()
        {
            // Regression: QuerySphere walked its cell AABB unconditionally, so a 10 km radius visited
            // 2500^3 cells to find 50 zombies -- 154 seconds for one L0 test. Past the crossover the
            // query must fall back to scanning items, and must still return the same answer.
            var pos = Field(400, 0xD00D);
            var grid = new ZombieSpatial();
            grid.Build(pos, pos.Length);

            var buf = new int[512];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int n = grid.QuerySphere(new Vector3(1f, 2f, 3f), 10000f, buf);
            int m = grid.QuerySegment(new Vector3(-9000f, 0f, -9000f), new Vector3(9000f, 0f, 9000f), 4000f, buf);
            sw.Stop();

            Assert.That(n, Is.EqualTo(400), "the huge sphere contains the whole field");
            Assert.That(m, Is.GreaterThan(0));
            Assert.That(sw.ElapsedMilliseconds, Is.LessThan(500),
                "a query degenerated into a cell walk again");
        }

        [Test]
        public void The_Fallback_And_The_Grid_Walk_Agree_Across_The_Crossover()
        {
            var pos = Field(300, 0x51DE);
            var grid = new ZombieSpatial();
            grid.Build(pos, pos.Length);

            var buf = new int[512];
            // radii from "a couple of cells" to "the whole level" -- the crossover is somewhere inside.
            foreach (float r in new[] { 4f, 16f, 64f, 200f, 600f, 3000f })
            {
                var c = new Vector3(12f, -3f, -40f);
                int n = grid.QuerySphere(c, r, buf);
                CollectionAssert.AreEquivalent(BruteSphere(pos, pos.Length, c, r), buf.Take(n).ToList(), $"sphere r={r}");

                var a = new Vector3(-380f, 0f, -120f);
                var b = new Vector3(380f, 5f, 140f);
                int s = grid.QuerySegment(a, b, r, buf);
                CollectionAssert.AreEquivalent(BruteSegment(pos, pos.Length, a, b, r), buf.Take(s).ToList(), $"segment r={r}");
            }
        }

        [Test]
        public void Results_Are_Capped_At_The_Callers_Buffer()
        {
            var pos = Field(300, 5150);
            var grid = new ZombieSpatial();
            grid.Build(pos, pos.Length);

            var small = new int[16];
            int n = grid.QuerySphere(Vector3.zero, 10000f, small);
            Assert.That(n, Is.EqualTo(16), "must fill and stop, not overrun");
        }
    }
}
