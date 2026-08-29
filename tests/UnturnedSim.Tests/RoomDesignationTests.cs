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
            // AN ORDINAL IS NOT A NAME. Enum.TryParse accepts "3" and maps it to whichever member sits
            // there, which would silently re-label every room the day the enum is reordered -- the exact
            // thing persisting by name is supposed to prevent. It must NOT be honoured.
            // BREAK IT: drop the IsName guard -> "3" becomes a real kind and this fails.
            Assert.That(RoomDesignations.ParseRoom("3"), Is.EqualTo(RoomKind.Unassigned),
                "an ordinal must not be accepted as a kind");
            Assert.That(RoomDesignations.ParseBuilding("1"), Is.EqualTo(BuildingKind.Misc),
                "same for building kinds");
            Assert.That(RoomDesignations.ParseBuilding("Industrial"), Is.EqualTo(BuildingKind.Industrial));
            Assert.That(RoomDesignations.ParseBuilding("nonsense"), Is.EqualTo(BuildingKind.Misc),
                "buildings fall back to Misc, which is the catch-all the list already has");
        }

        // ---- persistence -------------------------------------------------------------------------

        // Round-trip. The claim is not "a string contains the word Kitchen" -- it is that a save written
        // now reads back as the same designations, because that is what makes a label survive a session.
        [Test]
        public void DesignationsRoundTripThroughTheSaveFormat()
        {
            var walls = new List<WallPlan> { new WallPlan { X = 1f, Y = 2f, Z = 3f, Length = 12f } };
            var stored = new List<RoomDesignation>
            {
                new RoomDesignation(RoomKind.Kitchen, 6.5f, -3.25f),
                new RoomDesignation(RoomKind.Garage, 20f, 4f),
            };
            string text = WallSave.Write(walls, BuildingKind.Industrial, stored);

            var back = WallSave.Read(text.Split('\n'), out var building, out var rooms);
            Assert.That(back.Count, Is.EqualTo(1), "the walls still load");
            Assert.That(building, Is.EqualTo(BuildingKind.Industrial));
            Assert.That(rooms.Count, Is.EqualTo(2));
            Assert.That(rooms[0].Kind, Is.EqualTo(RoomKind.Kitchen));
            Assert.That(rooms[0].X, Is.EqualTo(6.5f).Within(1e-3f));
            Assert.That(rooms[0].Z, Is.EqualTo(-3.25f).Within(1e-3f), "a negative coordinate survives");
            Assert.That(rooms[1].Kind, Is.EqualTo(RoomKind.Garage));
        }

        // EVERY SAVE WRITTEN BEFORE THIS EXISTED has no designation lines, so that is the DEFAULT path and
        // not an edge case. It must load as a normal building with no labels, never as a failure.
        [Test]
        public void AFileWithNoDesignationLinesStillLoads()
        {
            string old = WallSave.Write(new List<WallPlan> { new WallPlan { X = 1f, Length = 6f } });
            // Check DATA lines, not a substring of the whole file: the format-comment header always
            // contains "# building <kind>", so a bare Contains matches the documentation and proves
            // nothing about what was written. (That is exactly how the first version of this failed.)
            foreach (string ln in old.Split('\n'))
            {
                Assert.That(ln.StartsWith("building "), Is.False, $"no building record: {ln}");
                Assert.That(ln.StartsWith("room "), Is.False, $"no room record: {ln}");
            }

            var back = WallSave.Read(old.Split('\n'), out var building, out var rooms);
            Assert.That(back.Count, Is.EqualTo(1));
            Assert.That(building, Is.EqualTo(BuildingKind.Misc), "the catch-all, not a throw");
            Assert.That(rooms, Is.Empty);
        }

        // A malformed room line costs THAT label, not the building. Same principle as an unknown kind.
        // BREAK IT: parse coordinates with float.Parse -> the whole load throws on one bad line.
        [Test]
        public void AMalformedRoomLineDropsOnlyItself()
        {
            var lines = new List<string>
            {
                WallSave.Header,
                "wall 0 0 0 0 12 0.7 0",
                "room Kitchen notanumber 4",
                "room Garage 8 9",
            };
            var back = WallSave.Read(lines, out _, out var rooms);
            Assert.That(back.Count, Is.EqualTo(1), "the wall still loads");
            Assert.That(rooms.Count, Is.EqualTo(1), "the good label survives");
            Assert.That(rooms[0].Kind, Is.EqualTo(RoomKind.Garage));
        }

        // The old single-argument Read still works -- three callers depend on it.
        [Test]
        public void TheOriginalReadOverloadStillWorks()
        {
            string text = WallSave.Write(new List<WallPlan> { new WallPlan { Length = 9f } },
                                         BuildingKind.Commercial,
                                         new List<RoomDesignation> { new RoomDesignation(RoomKind.Shop, 1f, 2f) });
            var back = WallSave.Read(text.Split('\n'));
            Assert.That(back.Count, Is.EqualTo(1), "designation lines do not disturb the wall read");
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
