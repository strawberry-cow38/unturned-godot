using System.Collections.Generic;
using NUnit.Framework;
using Seg = UnturnedSim.RoomEnclosure.PlanSegment;

namespace UnturnedSim.Tests
{
    // L0 for the designation framework: how a label is anchored, how it re-finds its room, and what happens
    // when it cannot. No placement logic here -- that is the stories system and it is deliberately not built.
    //
    // The claim under test is not "a struct holds a kind". It is that a designation SURVIVES the plan being
    // edited, because rooms are derived and have no stable identity to key on.
    [TestFixture]
    public class RoomDesignationTests
    {
        static Seg S(float x0, float z0, float x1, float z1, int src = -1) => new Seg(x0, z0, x1, z1, src);

        static List<Seg> Square(float x0 = 0f, float z0 = 0f, float w = 12f) => new List<Seg>
        {
            S(x0, z0, x0 + w, z0, 0), S(x0 + w, z0, x0 + w, z0 + w, 1),
            S(x0 + w, z0 + w, x0, z0 + w, 2), S(x0, z0 + w, x0, z0, 3),
        };

        // An L-shaped room: this is why the anchor is not just "the centroid".
        static List<Seg> Lshape() => new List<Seg>
        {
            S(0, 0, 12, 0, 0), S(12, 0, 12, 6, 1), S(12, 6, 6, 6, 2),
            S(6, 6, 6, 12, 3), S(6, 12, 0, 12, 4), S(0, 12, 0, 0, 5),
        };

        [Test]
        public void ContainsIsTrueInsideAndFalseOutside()
        {
            var room = RoomEnclosure.Find(Square())[0];
            Assert.That(RoomDesignations.Contains(room.Outline, 6f, 6f), Is.True, "centre");
            Assert.That(RoomDesignations.Contains(room.Outline, 40f, 6f), Is.False, "well outside");
            Assert.That(RoomDesignations.Contains(room.Outline, -1f, 6f), Is.False, "outside on the other side");
        }

        // A ray through a VERTEX is the classic even-odd double-count: counted twice, the point reads as
        // outside its own room. BREAK IT: make the straddle test symmetric ((a.Z >= z) != (b.Z >= z) on both
        // ends) and a point level with a corner flips.
        [Test]
        public void AVertexOnTheRayIsNotDoubleCounted()
        {
            // IT MUST BE A SHAPE WITH AN INTERIOR VERTEX ON THE TEST ROW. A square's only vertices sit at
            // z = 0 and z = 12, which are its boundary -- a point there is ambiguous by definition, so the
            // symmetric and asymmetric straddle tests agree and the case proves nothing. (That is what the
            // first version of this test did, and the mutation survived it.) The L has vertices at EXACTLY
            // z = 6 that are interior to the ray's path, so a ray along z = 6 passes through them.
            var l = RoomEnclosure.Find(Lshape())[0];
            Assert.That(RoomDesignations.Contains(l.Outline, 3f, 6f), Is.True,
                "a point level with the inner corners is still inside the room");
            Assert.That(RoomDesignations.Contains(l.Outline, 9f, 6f), Is.False,
                "and one in the notch, at the same height, is still outside");
        }

        // THE POINT OF THE WHOLE DESIGN: a designation survives the plan being edited. Add a partition
        // somewhere else and the room set is rebuilt from scratch -- the label must still land on the room
        // that still contains its anchor.
        [Test]
        public void ADesignationSurvivesTheRoomSetBeingRebuilt()
        {
            var before = RoomEnclosure.Find(Square(0f, 0f, 24f));
            Assert.That(before.Count, Is.EqualTo(1));
            Assert.That(RoomDesignations.AnchorFor(before[0], out float ax, out float az), Is.True);
            var stored = new List<RoomDesignation> { new RoomDesignation(RoomKind.Kitchen, ax, az) };

            // Now split the building with a partition. Two rooms exist where one did; the anchor sits in
            // whichever half still contains it, and the label goes there.
            var plan = Square(0f, 0f, 24f);
            plan.Add(S(18f, 0f, 18f, 24f, 4));
            var after = RoomEnclosure.Find(plan);
            Assert.That(after.Count, Is.EqualTo(2));

            RoomDesignations.Resolve(after, stored, out var byRoom, out var orphans);
            Assert.That(orphans, Is.Empty, "the anchor is still inside one of the new rooms");
            int labelled = 0;
            foreach (var k in byRoom) if (k == RoomKind.Kitchen) labelled++;
            Assert.That(labelled, Is.EqualTo(1), "exactly one room carries the label");
        }

        // A CONCAVE room's centroid is outside itself. An anchor outside the room it labels matches nothing
        // on the next load, so the fallback is the point of the exercise.
        //
        // BREAK IT: return the centroid unconditionally -> the anchor is outside and Resolve orphans it.
        [Test]
        public void AnLShapedRoomGetsAnAnchorInsideItself()
        {
            var room = RoomEnclosure.Find(Lshape())[0];
            Assert.That(room.Area, Is.EqualTo(108f).Within(0.5f), "the L, not its bounding box");
            Assert.That(RoomDesignations.AnchorFor(room, out float x, out float z), Is.True);
            Assert.That(RoomDesignations.Contains(room.Outline, x, z), Is.True,
                $"anchor ({x},{z}) must be INSIDE the room it labels");

            // ...and it round-trips: the label re-finds this room.
            var stored = new List<RoomDesignation> { new RoomDesignation(RoomKind.Workshop, x, z) };
            RoomDesignations.Resolve(new[] { room }, stored, out var byRoom, out var orphans);
            Assert.That(orphans, Is.Empty);
            Assert.That(byRoom[0], Is.EqualTo(RoomKind.Workshop));
        }

        // A designation whose room was DELETED is an orphan, handed back rather than dropped. Losing it
        // silently is how a save quietly loses work; reporting it lets the editor say so.
        [Test]
        public void ADesignationWhoseRoomIsGoneBecomesAnOrphan()
        {
            var stored = new List<RoomDesignation> { new RoomDesignation(RoomKind.Bedroom, 6f, 6f) };
            var elsewhere = RoomEnclosure.Find(Square(100f, 100f));   // a room, but not around that point
            RoomDesignations.Resolve(elsewhere, stored, out var byRoom, out var orphans);
            Assert.That(orphans.Count, Is.EqualTo(1));
            Assert.That(orphans[0].Kind, Is.EqualTo(RoomKind.Bedroom));
            Assert.That(byRoom[0], Is.EqualTo(RoomKind.Unassigned), "the unrelated room is not labelled");
        }

        // Two labels landing in ONE room is a conflict the caller should see, not something resolved by
        // arrival order. BREAK IT: let the second overwrite -> orphans is empty and the first label is gone.
        [Test]
        public void TwoDesignationsInOneRoomLeaveTheSecondAsAnOrphan()
        {
            var rooms = RoomEnclosure.Find(Square());
            var stored = new List<RoomDesignation>
            {
                new RoomDesignation(RoomKind.Kitchen, 5f, 5f),
                new RoomDesignation(RoomKind.Bedroom, 7f, 7f),   // same room
            };
            RoomDesignations.Resolve(rooms, stored, out var byRoom, out var orphans);
            Assert.That(byRoom[0], Is.EqualTo(RoomKind.Kitchen), "first one wins");
            Assert.That(orphans.Count, Is.EqualTo(1));
            Assert.That(orphans[0].Kind, Is.EqualTo(RoomKind.Bedroom), "the loser is reported, not discarded");
        }

        // Kinds persist by NAME, so the enum can be extended or reordered without re-labelling old saves.
        // An unknown name degrades to Unassigned rather than throwing -- a newer editor's kind should cost
        // one label, not the whole building.
        [Test]
        public void KindsParseByNameAndDegradeSafely()
        {
            Assert.That(RoomDesignations.ParseRoom("Kitchen"), Is.EqualTo(RoomKind.Kitchen));
            Assert.That(RoomDesignations.ParseRoom("kitchen"), Is.EqualTo(RoomKind.Kitchen), "case-insensitive");
            Assert.That(RoomDesignations.ParseRoom("Conservatory"), Is.EqualTo(RoomKind.Unassigned),
                "a kind this build has never heard of");
            Assert.That(RoomDesignations.ParseRoom(""), Is.EqualTo(RoomKind.Unassigned));
            Assert.That(RoomDesignations.ParseBuilding("Industrial"), Is.EqualTo(BuildingKind.Industrial));
            Assert.That(RoomDesignations.ParseBuilding("nonsense"), Is.EqualTo(BuildingKind.Misc),
                "buildings fall back to Misc, which is the catch-all the list already has");
        }

        [Test]
        public void NothingToResolveIsNotACrash()
        {
            RoomDesignations.Resolve(null, null, out var byRoom, out var orphans);
            Assert.That(byRoom, Is.Empty);
            Assert.That(orphans, Is.Empty);
        }
    }
}
