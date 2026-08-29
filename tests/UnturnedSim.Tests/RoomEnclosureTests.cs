using System;
using System.Collections.Generic;
using NUnit.Framework;
using Seg = UnturnedSim.RoomEnclosure.PlanSegment;

namespace UnturnedSim.Tests
{
    // L0 for enclosure detection -- which walls form a room.
    //
    // These drive RoomEnclosure.Find and read what it returns. None of them re-derives the answer: the point
    // of the module is that a room is a property of the whole plan rather than of any wall, so a test that
    // computed the rooms itself would be testing its own arithmetic. Every case names the mutation it exists
    // to catch.
    [TestFixture]
    public class RoomEnclosureTests
    {
        static Seg S(float x0, float z0, float x1, float z1, int src = -1)
            => new Seg(x0, z0, x1, z1, src);

        /// <summary>A 12 x 12 room drawn corner to corner on the wall CENTRELINES.</summary>
        static List<Seg> Square(float x0 = 0f, float z0 = 0f, float w = 12f)
            => new List<Seg>
            {
                S(x0, z0, x0 + w, z0, 0),
                S(x0 + w, z0, x0 + w, z0 + w, 1),
                S(x0 + w, z0 + w, x0, z0 + w, 2),
                S(x0, z0 + w, x0, z0, 3),
            };

        // Four walls in a loop are one room -- and ONE, not two. The planar traversal produces the outer
        // boundary as a face as well, so the outside of the building is a perfectly valid closed loop that
        // must not be handed back as a room to put a floor in.
        //
        // BREAK IT: keep faces of either sign -> 2 rooms, the second one the outside of the building.
        [Test]
        public void FourWallsInALoopAreExactlyOneRoom()
        {
            var rooms = RoomEnclosure.Find(Square());
            Assert.That(rooms.Count, Is.EqualTo(1), "expected one room");
            Assert.That(rooms[0].Area, Is.EqualTo(144f).Within(0.1f));
        }

        // The whole outer-face rejection rests on winding: rooms come back counter-clockwise (positive area
        // in x-z) and the outside comes back clockwise. That convention is a consequence of the turn rule in
        // the traversal, so pin it -- if someone "tidies" the walk to take the other neighbour, the inside
        // and the outside of every building swap places.
        //
        // IT HAS TO BE A DIVIDED PLAN. Flipping the rule on a single square yields the same four corners
        // walked the other way, which is a valid counter-clockwise 144 -- the outside impersonating the room
        // exactly. Only a plan whose inside and outside DIFFER can see the flip: two rooms of 72 here, one
        // "room" of 144 if the walk turns the wrong way.
        //
        // BREAK IT: take the successor instead of the predecessor in the angular order.
        [Test]
        public void RoomOutlinesComeBackCounterClockwise()
        {
            var plan = Square();
            plan.Add(S(6, 0, 6, 12, 4));

            var rooms = RoomEnclosure.Find(plan);
            Assert.That(rooms.Count, Is.EqualTo(2), "the walk turned the wrong way and found the outside");
            foreach (var r in rooms)
            {
                Assert.That(RoomEnclosure.SignedArea(r.Outline), Is.GreaterThan(0f),
                    "a room's outline must wind counter-clockwise in (x, z)");
                Assert.That(r.Area, Is.EqualTo(72f).Within(0.5f));
            }
        }

        // THE ACTUAL CLAIM. "Enclosed" has to mean enclosed: three sides of a square is a courtyard, and the
        // whole feature is worthless if it drops a floor into one.
        //
        // What rejects it is the WINDING, not the area floor -- an open run has no interior, so its one face
        // walks out and back with a signed area of zero and fails the same sign test the outside of a
        // building fails. I first wrote "BREAK IT: drop MinRoomArea" here and the mutation stayed green,
        // which is how I found out. MinRoomArea earns its keep on slivers instead; see below.
        [Test]
        public void AnUnclosedRunOfWallsIsNotARoom()
        {
            var open = Square();
            open.RemoveAt(3);                       // take one side away
            Assert.That(RoomEnclosure.Find(open), Is.Empty);
        }

        // Walls that STOP SHORT of each other still close the room. This is the case the weld tolerance is
        // actually for: ends that neither meet nor cross, which is what a dragged wall left a few centimetres
        // short leaves behind. A solved building does not need the weld -- its walls overshoot far enough to
        // cross, and the crossing split places the node exactly -- so without this case the weld could be cut
        // to an epsilon and every test would still pass.
        //
        // BREAK IT: set DefaultWeld to 1e-3 -> the four corners never join and there is no room.
        [Test]
        public void WallsThatStopShortOfEachOtherStillEnclose()
        {
            const float g = 0.3f;                   // each end a little shy of the corner
            var shy = new List<Seg>
            {
                S(g, 0, 12 - g, 0, 0),
                S(12, g, 12, 12 - g, 1),
                S(12 - g, 12, g, 12, 2),
                S(0, 12 - g, 0, g, 3),
            };
            var rooms = RoomEnclosure.Find(shy);
            Assert.That(rooms.Count, Is.EqualTo(1), "near misses should still close");
            Assert.That(rooms[0].Area, Is.EqualTo(144f).Within(0.5f),
                "and the corner belongs where the centrelines cross, not where the ends stopped");
        }

        // A sliver is not a room. Two walls drawn almost on top of each other with the ends closed off make a
        // genuine, correctly-wound enclosure that you could not stand in, and an auto-floor in one is a scrap
        // of geometry nobody can select. This is what MinRoomArea is for -- the smallest room the lattice can
        // express is 3 x 3, so a 1 m2 floor is far below anything real.
        //
        // BREAK IT: lower the test to `area <= 0f` -> the sliver comes back as a room.
        [Test]
        public void ASliverIsNotARoom()
        {
            var sliver = new List<Seg>
            {
                S(0, 0, 1.1f, 0, 0), S(1.1f, 0, 1.1f, 0.9f, 1),
                S(1.1f, 0.9f, 0, 0.9f, 2), S(0, 0.9f, 0, 0, 3),
            };
            Assert.That(RoomEnclosure.Find(sliver), Is.Empty, "0.99 m2 is not a space");
        }

        // A gap in the middle of a side is still not enclosed, and this is the harder half: the loop LOOKS
        // closed at both ends, only the middle is missing. A test that only ever removed a whole wall would
        // pass on an implementation that just counted walls.
        [Test]
        public void AGapInTheMiddleOfAWallIsNotEnclosed()
        {
            var gapped = new List<Seg>
            {
                S(0, 0, 5, 0), S(7, 0, 12, 0),      // two metres of nothing between them
                S(12, 0, 12, 12), S(12, 12, 0, 12), S(0, 12, 0, 0),
            };
            Assert.That(RoomEnclosure.Find(gapped), Is.Empty);
        }

        // SOLVED CORNERS, which is what the editor actually holds. Corner solving runs every wall past its
        // neighbour to the outer face, so the four walls of a drawn room do NOT meet at their endpoints --
        // each end overshoots by half a thickness, in a different direction from its neighbour's.
        //
        // Welding them to the cluster centroid finds the room but puts every corner diagonally outside the
        // true one, which inflates a 12 x 12 room to 12.35 x 12.35 and would hang the auto-floor a quarter of
        // a metre past the walls on all four sides. The corner is where the two CENTRELINES cross, and that
        // is exactly recoverable however far the ends overshoot.
        //
        // BREAK IT: return false from Sharpen (use the centroid) -> corners land at -0.175 and area reads
        // 152.5 instead of 144.
        [Test]
        public void SolvedCornersRecoverTheTrueCornerNotTheOvershoot()
        {
            const float o = 0.35f;                  // half of DefaultThickness, what SolveCorners extends by
            var solved = new List<Seg>
            {
                S(-o, 0, 12 + o, 0, 0),
                S(12, -o, 12, 12 + o, 1),
                S(12 + o, 12, -o, 12, 2),
                S(0, 12 + o, 0, -o, 3),
            };

            var rooms = RoomEnclosure.Find(solved);
            Assert.That(rooms.Count, Is.EqualTo(1), "a solved room is still one room");
            Assert.That(rooms[0].Area, Is.EqualTo(144f).Within(0.5f),
                $"overshoot leaked into the footprint: {rooms[0].Area}");

            foreach (var p in rooms[0].Outline)
            {
                Assert.That(Math.Min(Math.Abs(p.X - 0f), Math.Abs(p.X - 12f)), Is.LessThan(0.02f),
                    $"corner x {p.X} is not on the drawn line");
                Assert.That(Math.Min(Math.Abs(p.Z - 0f), Math.Abs(p.Z - 12f)), Is.LessThan(0.02f),
                    $"corner z {p.Z} is not on the drawn line");
            }
        }

        // A partition across a room makes TWO rooms, and this is the case that decides whether the whole
        // approach was worth it. Nobody draws a loop for the second room: one wall is added, its ends land in
        // the middle of two existing walls, and the plan now contains two enclosures that were never drawn.
        //
        // BREAK IT: skip SplitAtJunctions -> the partition dangles, contributes nothing, and one 144 room
        // comes back instead of two 72s.
        [Test]
        public void APartitionTurnsOneRoomIntoTwo()
        {
            var plan = Square();
            plan.Add(S(6, 0, 6, 12, 4));            // ends land mid-span, not on any corner

            var rooms = RoomEnclosure.Find(plan);
            Assert.That(rooms.Count, Is.EqualTo(2), "a partition should divide the room");
            foreach (var r in rooms)
                Assert.That(r.Area, Is.EqualTo(72f).Within(0.5f), $"half a 12x12 room, got {r.Area}");
        }

        // The partition is a SHARED edge and the outer walls are not. A floor stops at the centreline of a
        // wall it shares with the next room (each takes its half) and at the outer face of an exterior one;
        // without this flag the caller cannot tell which, and picks one rule for both -- either overlapping
        // the two slabs by a full thickness or leaving a gap all the way round the building.
        //
        // BREAK IT: count edge uses over ALL faces rather than the kept interior ones -> the outer walls are
        // seen by the outside face too and every edge reads Shared.
        [Test]
        public void OnlyThePartitionIsMarkedShared()
        {
            var plan = Square();
            plan.Add(S(6, 0, 6, 12, 4));

            var rooms = RoomEnclosure.Find(plan);
            Assert.That(rooms.Count, Is.EqualTo(2));

            foreach (var r in rooms)
            {
                int shared = 0, exterior = 0;
                foreach (var e in r.Edges) { if (e.Shared) shared++; else exterior++; }
                Assert.That(shared, Is.EqualTo(1), "exactly the partition is shared");
                Assert.That(exterior, Is.GreaterThanOrEqualTo(3), "the outside walls are not shared");
                foreach (var e in r.Edges)
                    if (e.Shared)
                        Assert.That(e.Source, Is.EqualTo(4), "the shared edge is the wall that was added");
            }
        }

        // Two crossing partitions -- a genuine X, where neither wall ends at the crossing. Endpoint-onto-wall
        // splitting cannot see this one; it needs the segment-segment case.
        //
        // BREAK IT: delete the LineIntersect branch of SplitAtJunctions -> the two partitions pass through
        // each other unsplit and the plan reads as 2 rooms, not 4.
        [Test]
        public void CrossingPartitionsMakeFourRooms()
        {
            var plan = Square();
            plan.Add(S(6, 0, 6, 12, 4));
            plan.Add(S(0, 6, 12, 6, 5));

            var rooms = RoomEnclosure.Find(plan);
            Assert.That(rooms.Count, Is.EqualTo(4));
            foreach (var r in rooms)
                Assert.That(r.Area, Is.EqualTo(36f).Within(0.5f), $"a quarter of the room, got {r.Area}");
        }

        // A wall sticking into a room is not a room and does not stop the room around it from being one.
        // The face walk goes out along the spur and back, which cancels in the area but leaves the detour in
        // the point list, and the slab decomposition then reads the doubled edge as a real boundary.
        //
        // THE SPUR HAS TO LIE ALONG X. The decomposition scans for horizontal edges, so a spur running along
        // Z is skipped by that filter and the room comes out right whether or not the outline was pruned --
        // which is what the first version of this test used, and it passed with the pruning deleted. A spur
        // along X is seen twice by the scan, flips its parity, and splits one floor into three.
        //
        // BREAK IT: drop PruneBacktracks -> 3 slabs instead of 1.
        [Test]
        public void ASpurIsNotARoomAndDoesNotBreakTheOneAroundIt()
        {
            var plan = Square();
            plan.Add(S(0, 6, 4, 6, 4));             // dead-ends inside the room, along the scan axis

            var rooms = RoomEnclosure.Find(plan);
            Assert.That(rooms.Count, Is.EqualTo(1), "a stub does not enclose anything");
            Assert.That(rooms[0].Area, Is.EqualTo(144f).Within(0.5f));
            Assert.That(rooms[0].Slabs.Count, Is.EqualTo(1), "still one rectangular floor");
            Assert.That(rooms[0].Slabs[0].Area, Is.EqualTo(144f).Within(0.5f));
        }

        // ...and it is not a PARTITION either, though the face walk touches it twice. Sharing is about having
        // a different room on the far side, so it has to count distinct rooms rather than traversals -- a
        // spur counted by traversal reads as shared and would stop an auto-floor at a wall with nothing
        // behind it.
        //
        // BREAK IT: count half-edge traversals instead of distinct faces.
        [Test]
        public void ASpurIsNotASharedWall()
        {
            var plan = Square();
            plan.Add(S(0, 6, 4, 6, 4));

            var rooms = RoomEnclosure.Find(plan);
            Assert.That(rooms.Count, Is.EqualTo(1));
            foreach (var e in rooms[0].Edges)
                Assert.That(e.Shared, Is.False, $"wall {e.Source} borders no second room");
        }

        // Separate buildings are separate. Each connected component contributes its own outer face, so a
        // rule that dropped "the largest face" or "the first face" instead of judging every face by sign
        // would keep one building's outside as a room.
        //
        // BREAK IT: reject only the single biggest face -> the second building's outside survives.
        [Test]
        public void TwoBuildingsAreTwoRooms()
        {
            var plan = Square();
            plan.AddRange(Square(30f, 0f));
            var rooms = RoomEnclosure.Find(plan);
            Assert.That(rooms.Count, Is.EqualTo(2));
            foreach (var r in rooms) Assert.That(r.Area, Is.EqualTo(144f).Within(0.5f));
        }

        // An L-shaped room cannot be one slab, because a WallSurface is a generated BOX. It has to come back
        // as a disjoint cover whose pieces sum to the room -- and "sum to the room" is the assertion that
        // separates a correct decomposition from a bounding box, which would sum to 144 and cover a corner
        // that has no floor under it.
        //
        // BREAK IT: return the outline's AABB as a single rect -> area sums to 144, not 108.
        [Test]
        public void AnLShapedRoomDecomposesIntoSlabsThatSumToIt()
        {
            var plan = new List<Seg>
            {
                S(0, 0, 12, 0, 0), S(12, 0, 12, 6, 1), S(12, 6, 6, 6, 2),
                S(6, 6, 6, 12, 3), S(6, 12, 0, 12, 4), S(0, 12, 0, 0, 5),
            };

            var rooms = RoomEnclosure.Find(plan);
            Assert.That(rooms.Count, Is.EqualTo(1));
            var room = rooms[0];
            Assert.That(room.Area, Is.EqualTo(108f).Within(0.5f));
            Assert.That(room.IsRectilinear, Is.True);
            Assert.That(room.Slabs.Count, Is.GreaterThan(1), "an L cannot be covered by one box");

            float sum = 0f;
            foreach (var s in room.Slabs) sum += s.Area;
            Assert.That(sum, Is.EqualTo(room.Area).Within(0.5f),
                "the slabs must cover the room exactly -- no gap, no overhang");

            // Disjoint, or the two floors z-fight along the seam.
            for (int i = 0; i < room.Slabs.Count; i++)
                for (int j = i + 1; j < room.Slabs.Count; j++)
                {
                    var a = room.Slabs[i]; var b = room.Slabs[j];
                    float ox = Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX);
                    float oz = Math.Min(a.MaxZ, b.MaxZ) - Math.Max(a.MinZ, b.MinZ);
                    Assert.That(ox <= 1e-3f || oz <= 1e-3f, Is.True, "slabs overlap");
                }
        }

        // A rectangular room comes back as ONE slab even when its outline has extra vertices along the way --
        // a row of abutting boxes renders identically and is then several things to select, move and delete.
        //
        // The spur is what forces the issue: it puts a vertex mid-run on the bottom wall, so the room is cut
        // into two strips that carry the same span and have to be welded back. A room with only its four
        // corners is one strip already and cannot tell whether merging works at all.
        //
        // BREAK IT: skip the strip merge -> 2 slabs.
        [Test]
        public void ARectangularRoomIsASingleSlab()
        {
            var plan = Square();
            plan.Add(S(6, 0, 6, 4, 4));             // a stub, so x = 6 becomes a strip boundary
            var rooms = RoomEnclosure.Find(plan);
            Assert.That(rooms.Count, Is.EqualTo(1));
            Assert.That(rooms[0].Slabs.Count, Is.EqualTo(1), "strips of the same span should merge back");
        }

        // A room with a diagonal wall gets NO slabs rather than a wrong one. The decomposition is only valid
        // for axis-aligned edges, and a bounding box over a diagonal room overhangs the wall it is meant to
        // meet -- which renders as a floor poking through a wall and is not obviously a bug in a screenshot.
        //
        // IT MUST BE A SHAPE THE DECOMPOSITION WOULD ACTUALLY BOTCH. A plain triangle has only one horizontal
        // edge, so the scan finds an unpaired crossing and returns nothing whether or not the guard is there
        // -- the first version of this test used one, and it passed with the guard deleted, proving only
        // that a triangle is hard to decompose. A rectangle with one corner sliced off has two horizontal
        // edges: the scan pairs them happily and produces a confident 96 for a room of 136.
        //
        // BREAK IT: decompose regardless of IsRectilinear -> a slab appears, 40 short.
        [Test]
        public void ADiagonalRoomIsFoundButNotGivenSlabs()
        {
            var plan = new List<Seg>
            {
                S(0, 0, 12, 0, 0), S(12, 0, 12, 12, 1), S(12, 12, 4, 12, 2),
                S(4, 12, 0, 8, 3), S(0, 8, 0, 0, 4),
            };
            var rooms = RoomEnclosure.Find(plan);
            Assert.That(rooms.Count, Is.EqualTo(1), "a cut corner is still enclosed");
            Assert.That(rooms[0].Area, Is.EqualTo(136f).Within(0.5f));
            Assert.That(rooms[0].IsRectilinear, Is.False);
            Assert.That(rooms[0].Slabs, Is.Empty, "no guessed slab for a shape boxes cannot cover");
        }

        // Every wall bounding a room maps back to the surface it came from, which is what an auto-foundation
        // needs: a skirt under the walls that bound rooms rather than under every wall on the stage.
        [Test]
        public void ARoomKnowsWhichWallsBoundIt()
        {
            var rooms = RoomEnclosure.Find(Square());
            var srcs = rooms[0].SourceWalls();
            srcs.Sort();
            Assert.That(srcs, Is.EqualTo(new List<int> { 0, 1, 2, 3 }));
        }

        [Test]
        public void NothingAtAllIsNoRooms()
        {
            Assert.That(RoomEnclosure.Find(new List<Seg>()), Is.Empty);
            Assert.That(RoomEnclosure.Find(null), Is.Empty);
        }
    }
}
