using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace UnturnedSim.Tests
{
    // L0 for the wall partition -- the geometry engine under the building tool.
    //
    // This carries the bulk of the confidence deliberately: the partition decides both what you SEE and what
    // you COLLIDE with, and a wall whose collision disagrees with its mesh is the "see through it, can't walk
    // through it" bug that made the structures doorway three solids instead of a boolean in the first place.
    //
    // Every test below names the mutation that should break it. A test nobody has watched fail is a guess.
    [TestFixture]
    public class WallOpeningsTests
    {
        const float W = 6f, H = 4.25f;
        static List<WallOpening> One(WallOpening o) => new List<WallOpening> { o };
        static float TotalArea(List<WallSolid> s) { float a = 0f; foreach (var x in s) a += x.Area; return a; }

        // BREAK IT: drop the final gap in the strip complement -> the sill vanishes, count and area both fail.
        [Test]
        public void InteriorWindowLeavesExactlyFourBoxes()
        {
            var solids = WallOpenings.Solids(W, H, One(new WallOpening(2f, 1f, 2f, 1.5f)));
            Assert.That(solids.Count, Is.EqualTo(4), "left jamb, right jamb, lintel, sill");
            Assert.That(TotalArea(solids), Is.EqualTo(W * H - 2f * 1.5f).Within(1e-3f));
        }

        // BREAK IT: pin the opening base to Eps instead of 0 -> a sliver sill appears and the count goes to 3.
        [Test]
        public void FloorPinnedOpeningHasNoSillBox()
        {
            var solids = WallOpenings.Solids(W, H, One(new WallOpening(2f, 0f, 2.5f, WallOpenings.DoorHeight - 0.5f)));
            Assert.That(solids.Count, Is.EqualTo(3), "two jambs and a lintel, no sill under a door");
            foreach (var s in solids)
                Assert.That(s.V0 > 0f || s.U0 < 2f - 1e-3f || s.U1 > 4.5f + 1e-3f, "no box sits under the door");
        }

        // BREAK IT: subtract each opening independently instead of unioning the covered intervals -> the
        // overlap is removed twice and the area check fails.
        [Test]
        public void OverlappingOpeningsAreSubtractedOnce()
        {
            var two = new List<WallOpening>
            {
                new WallOpening(1f, 1f, 2f, 2f),
                new WallOpening(2f, 1.5f, 2f, 2f),   // overlaps the first
            };
            var solids = WallOpenings.Solids(W, H, two);
            // union area, computed independently of the code under test
            float union = 0f;
            const int N = 600;
            for (int i = 0; i < N; i++)
                for (int j = 0; j < N; j++)
                {
                    float u = (i + 0.5f) * W / N, v = (j + 0.5f) * H / N;
                    bool inside = false;
                    foreach (var o in two) if (u > o.U && u < o.U1 && v > o.V && v < o.V1) { inside = true; break; }
                    if (inside) union += (W / N) * (H / N);
                }
            Assert.That(TotalArea(solids), Is.EqualTo(W * H - union).Within(0.02f));
        }

        // BREAK IT: remove the Eps guards in step 1 or step 3 -> zero-area boxes reach the mesh and physics.
        [Test]
        public void FlushAndOversizedOpeningsProduceNoDegenerateBoxes()
        {
            var flush = WallOpenings.Solids(W, H, One(new WallOpening(0f, 0f, W, 2f)));   // spans the full width
            foreach (var s in flush)
                Assert.That(Math.Min(s.Width, s.Height), Is.GreaterThan(WallOpenings.Eps), "no slivers");
            Assert.That(TotalArea(flush), Is.EqualTo(W * (H - 2f)).Within(1e-3f));

            var over = WallOpenings.Solids(W, H, One(new WallOpening(-5f, -5f, 100f, 100f)));
            Assert.That(over.Count, Is.EqualTo(0), "an opening larger than the wall leaves nothing, and does not throw");
        }

        // The invariant everything else leans on. BREAK IT: any partition bug at all trips one of the three.
        [Test]
        public void PartitionInvariantHoldsOnRandomLayouts()
        {
            var rng = new Random(20260807);
            for (int iter = 0; iter < 200; iter++)
            {
                var ops = new List<WallOpening>();
                int n = rng.Next(0, 5);
                for (int k = 0; k < n; k++)
                {
                    float u = (float)rng.NextDouble() * W, v = (float)rng.NextDouble() * H;
                    ops.Add(new WallOpening(u, v, (float)rng.NextDouble() * 2.5f, (float)rng.NextDouble() * 2f));
                }
                var solids = WallOpenings.Solids(W, H, ops);

                // COVERAGE. Disjointness and not-overlapping-a-hole were both checked; nothing checked that
                // the solids actually COVER the wall, so a partition that silently dropped a box passed the
                // whole random sweep. Sampled rather than summed because area alone cannot tell a missing box
                // from a duplicated one.
                const int G = 23;
                for (int gx = 0; gx < G; gx++)
                    for (int gy = 0; gy < G; gy++)
                    {
                        float u = (gx + 0.5f) * W / G, v = (gy + 0.5f) * H / G;
                        bool inHole = false;
                        foreach (var o in ops)
                            if (u > o.U && u < o.U1 && v > o.V && v < o.V1) { inHole = true; break; }
                        if (inHole) continue;
                        int covering = 0;
                        foreach (var s in solids)
                            if (u > s.U0 && u < s.U1 && v > s.V0 && v < s.V1) covering++;
                        Assert.That(covering, Is.EqualTo(1),
                            $"iter {iter}: solid wall at ({u:0.##},{v:0.##}) is covered by {covering} boxes, want exactly 1");
                    }

                for (int a = 0; a < solids.Count; a++)
                {
                    Assert.That(solids[a].Width, Is.GreaterThan(WallOpenings.Eps));
                    Assert.That(solids[a].Height, Is.GreaterThan(WallOpenings.Eps));
                    for (int b = a + 1; b < solids.Count; b++)
                        Assert.That(Overlaps(solids[a], solids[b]), Is.False, $"iter {iter}: solids {a},{b} overlap");
                    foreach (var o in ops)
                        Assert.That(OverlapsOpening(solids[a], o), Is.False, $"iter {iter}: a solid intersects an opening");
                }
            }
        }

        static bool Overlaps(WallSolid a, WallSolid b) =>
            a.U0 < b.U1 - 1e-3f && b.U0 < a.U1 - 1e-3f && a.V0 < b.V1 - 1e-3f && b.V0 < a.V1 - 1e-3f;
        static bool OverlapsOpening(WallSolid s, WallOpening o) =>
            s.U0 < o.U1 - 1e-3f && Math.Max(o.U, 0f) < s.U1 - 1e-3f &&
            s.V0 < o.V1 - 1e-3f && Math.Max(o.V, 0f) < s.V1 - 1e-3f;

        // BREAK IT: return the target unchanged when out of bounds -> the opening leaves the wall.
        [Test]
        public void ClampStopsAtTheWallEdgeInsteadOfRefusing()
        {
            var o = WallOpenings.Clamp(new WallOpening(99f, 1f, 2f, 1.5f), W, H, null);
            Assert.That(o.U, Is.EqualTo(W - 2f).Within(1e-4f), "flush to the right edge, not rejected");
            var l = WallOpenings.Clamp(new WallOpening(-99f, -99f, 2f, 1.5f), W, H, null);
            Assert.That(l.U, Is.EqualTo(0f).Within(1e-4f));
            Assert.That(l.V, Is.EqualTo(0f).Within(1e-4f));
        }

        // BREAK IT: drop the V-overlap filter in the U pass -> the second case falsely blocks.
        [Test]
        public void ClampBlocksOnlyOnSiblingsThatActuallyOverlap()
        {
            var sibling = new List<WallOpening> { new WallOpening(3f, 1f, 2f, 1.5f) };

            var blocked = WallOpenings.Clamp(new WallOpening(2.9f, 1f, 2f, 1.5f), W, H, sibling);
            Assert.That(blocked.U + 2f, Is.LessThanOrEqualTo(3f + 1e-3f), "stops touching, never overlapping");

            // same U target, but at a height the sibling does not occupy -> must slide right past it
            var free = WallOpenings.Clamp(new WallOpening(2.9f, 3f, 2f, 1f), W, H, sibling);
            Assert.That(free.U, Is.EqualTo(2.9f).Within(1e-3f), "a sibling at another height must not block");
        }

        // BREAK IT: double the tolerance -> the out-of-range case snaps when it should not.
        [Test]
        public void SnapTakesNearestTargetOnlyWithinTolerance()
        {
            Assert.That(WallOpenings.Snap(4.20f, WallOpenings.HeadHeights, 0.1f), Is.EqualTo(4.25f).Within(1e-4f));
            Assert.That(WallOpenings.Snap(4.00f, WallOpenings.HeadHeights, 0.1f), Is.EqualTo(4.00f).Within(1e-4f),
                "outside tolerance returns the input untouched");
            Assert.That(WallOpenings.Snap(3.80f, WallOpenings.HeadHeights, 0.2f), Is.EqualTo(3.75f).Within(1e-4f),
                "nearest wins when several are in range");
        }

        // The measured constants are OURS-with-evidence: pinned with literals so a typo cannot slip through
        // by agreeing with itself.
        [Test]
        public void MeasuredDefaultsArePinned()
        {
            Assert.That(WallOpenings.DoorHeight, Is.EqualTo(4.25f).Within(1e-4f));
            Assert.That(WallOpenings.WindowHeight, Is.EqualTo(2.75f).Within(1e-4f));
            Assert.That(WallOpenings.StoreyPitch, Is.EqualTo(4.75f).Within(1e-4f));
            Assert.That(WallOpenings.LatticeStep, Is.EqualTo(3.0f).Within(1e-4f));
        }
    }
}
