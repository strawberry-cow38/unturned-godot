using Godot;
using System.Collections.Generic;
using UnturnedSim;

namespace UnturnedGodot.Testing
{
    // Automatic floors on enclosed rooms. strawberry_cow: "automatic foundations/floors on enclosed rooms".
    //
    // The enclosure maths is covered at L0 on synthetic segments, and that is the right place for it. These
    // exist for the half L0 cannot see: the editor's own coordinates. The stage sits at y = 2000, walls are
    // placed at StageOrigin.Y + FloorY + GroundClearance, and FloorY alone is the storey OFFSET -- which is
    // exactly the substitution that put every staircase 2000 m below the building last night, placed and
    // undoable and completely invisible. A module test cannot catch that; only driving the editor can.
    //
    // They also raycast rather than counting surfaces, because "a floor exists" and "you can stand on it"
    // are different claims and only the second one is the feature.
    static class RoomDraw
    {
        /// <summary>Lay a square room the way the room tool does: corner to corner, anticlockwise.
        /// yaw 0 runs +X, 90 runs -Z, 180 runs -X, 270 runs +Z.</summary>
        public static void Square(EditorBuildings eb, float x, float z, float w)
        {
            float y = eb.ActiveFloorY;
            eb.AddWall(new Vector3(x, y, z), 0f, w);
            eb.AddWall(new Vector3(x + w, y, z), 90f, w);
            eb.AddWall(new Vector3(x + w, y, z - w), 180f, w);
            eb.AddWall(new Vector3(x, y, z - w), 270f, w);
        }

        public static int Count(EditorBuildings eb, SurfaceKind kind)
        {
            int n = 0;
            foreach (var w in eb.Walls) if (GodotObject.IsInstanceValid(w) && w.Kind == kind) n++;
            return n;
        }

        /// <summary>Is there something solid straight down from here?</summary>
        public static bool Solid(GameTest t, Vector3 at, float above = 3f, float below = 3f)
        {
            var space = t.World.GetWorld3D().DirectSpaceState;
            var q = new PhysicsRayQueryParameters3D
            {
                From = at + new Vector3(0f, above, 0f),
                To = at - new Vector3(0f, below, 0f),
                CollisionMask = 1u << 0,
            };
            return space.IntersectRay(q).Count > 0;
        }
    }

    public class AutoFloorFillsAnEnclosedRoom : GameTest
    {
        public override string Name => "buildtool.auto_floor_fills_a_room";

        public override IEnumerable<Step> Run()
        {
            var eb = new EditorBuildings();
            World.AddChild(eb);
            yield return Step.Ticks(1);

            RoomDraw.Square(eb, 0f, 0f, 12f);
            yield return Step.Ticks(1);
            eb.SolveCorners();                      // the state the editor is actually in once you have drawn
            yield return Step.Ticks(1);

            float y = eb.ActiveFloorY;
            T.Check("nothing is floored before the button", !RoomDraw.Solid(this, new Vector3(6f, y, -6f)));

            // BREAK IT: filter the plan on FloorY instead of ActiveFloorY -> no wall is on the active storey,
            // no room is found, and this returns 0 while the editor looks completely normal.
            int made = eb.AutoFitRooms();
            yield return Step.Ticks(2);             // let Jolt take the new shapes
            T.Check($"surfaces were added ({made})", made > 0);
            T.Check($"exactly one floor for one room ({RoomDraw.Count(eb, SurfaceKind.Floor)})",
                    RoomDraw.Count(eb, SurfaceKind.Floor) == 1);

            T.Check("you can stand in the middle of the room", RoomDraw.Solid(this, new Vector3(6f, y, -6f)));
            T.Check("...and in its corners", RoomDraw.Solid(this, new Vector3(1f, y, -1f))
                                          && RoomDraw.Solid(this, new Vector3(11f, y, -11f)));
            T.Check("and there is no floor out in the open", !RoomDraw.Solid(this, new Vector3(40f, y, -40f)));

            // THE THRESHOLD. Rooms are found on wall CENTRELINES, so an un-grown floor stops halfway into
            // every wall -- invisible everywhere except a doorway, where the opening pierces the full
            // thickness and the outer half has nothing under it.
            //
            // BREAK IT: use Decompose(room.Outline) instead of FloorSlabs -> MinX comes back 0.
            float minX = float.MaxValue, maxX = float.MinValue;
            foreach (var w in eb.Walls)
                if (GodotObject.IsInstanceValid(w) && w.Kind == SurfaceKind.Floor)
                {
                    var box = new Aabb(w.UVToWorld(0f, 0f), Vector3.Zero)
                              .Merge(new Aabb(w.UVToWorld(w.Length, w.Height), Vector3.Zero));
                    minX = Mathf.Min(minX, Mathf.Min(box.Position.X, box.End.X));
                    maxX = Mathf.Max(maxX, Mathf.Max(box.Position.X, box.End.X));
                }
            T.Check($"the floor reaches the outer wall face, not the centreline ({minX:0.00}..{maxX:0.00})",
                    minX < -0.3f && maxX > 12.3f);

            // A BUTTON GETS PRESSED TWICE. A second floor in the same place is invisible until it z-fights.
            int again = eb.AutoFitRooms();
            yield return Step.Ticks(1);
            T.Check($"pressing it again adds nothing ({again})", again == 0);
            T.Check($"...and there is still one floor ({RoomDraw.Count(eb, SurfaceKind.Floor)})",
                    RoomDraw.Count(eb, SurfaceKind.Floor) == 1);

            eb.QueueFree();
        }
    }

    // "Enclosed" is the whole feature. Three walls is a courtyard and must get nothing -- and then closing it
    // must be all it takes. Asserting both halves in one test is deliberate: a version that never floors
    // anything passes the first half on its own.
    public class AutoFloorNeedsTheRoomActuallyClosed : GameTest
    {
        public override string Name => "buildtool.auto_floor_needs_enclosure";

        public override IEnumerable<Step> Run()
        {
            var eb = new EditorBuildings();
            World.AddChild(eb);
            yield return Step.Ticks(1);

            float y = eb.ActiveFloorY;
            eb.AddWall(new Vector3(0f, y, 0f), 0f, 12f);
            eb.AddWall(new Vector3(12f, y, 0f), 90f, 12f);
            eb.AddWall(new Vector3(12f, y, -12f), 180f, 12f);
            yield return Step.Ticks(1);
            eb.SolveCorners();
            yield return Step.Ticks(1);

            int open = eb.AutoFitRooms();
            T.Check($"three walls get no floor ({open})", open == 0);
            T.Check($"...and none appeared ({RoomDraw.Count(eb, SurfaceKind.Floor)})",
                    RoomDraw.Count(eb, SurfaceKind.Floor) == 0);

            // Close it.
            eb.AddWall(new Vector3(0f, y, -12f), 270f, 12f);
            yield return Step.Ticks(1);
            eb.SolveCorners();
            yield return Step.Ticks(1);

            int closed = eb.AutoFitRooms();
            T.Check($"the fourth wall makes it a room ({closed})", closed > 0);
            yield return Step.Ticks(2);
            T.Check("and now you can stand in it", RoomDraw.Solid(this, new Vector3(6f, y, -6f)));

            eb.QueueFree();
        }
    }

    // PER ROOM, not per bounding box -- which is the entire difference from the floor tool that already
    // existed. AddSlab takes the AABB of every wall on the stage, so two separate buildings get one floor
    // bridging the gap between them, and you would be walking on air out there.
    public class AutoFloorIsPerRoomNotPerBoundingBox : GameTest
    {
        public override string Name => "buildtool.auto_floor_per_room";

        public override IEnumerable<Step> Run()
        {
            var eb = new EditorBuildings();
            World.AddChild(eb);
            yield return Step.Ticks(1);

            RoomDraw.Square(eb, 0f, 0f, 12f);
            RoomDraw.Square(eb, 30f, 0f, 12f);
            yield return Step.Ticks(1);
            eb.SolveCorners();
            yield return Step.Ticks(1);

            eb.AutoFitRooms();
            yield return Step.Ticks(2);

            float y = eb.ActiveFloorY;
            T.Check($"a floor each ({RoomDraw.Count(eb, SurfaceKind.Floor)})",
                    RoomDraw.Count(eb, SurfaceKind.Floor) == 2);
            T.Check("you can stand in the first", RoomDraw.Solid(this, new Vector3(6f, y, -6f)));
            T.Check("...and in the second", RoomDraw.Solid(this, new Vector3(36f, y, -6f)));
            // BREAK IT: slab the bounding box of all the walls -> this is floored and you walk on air.
            T.Check("but NOT in the gap between them", !RoomDraw.Solid(this, new Vector3(21f, y, -6f)));

            eb.QueueFree();
        }
    }

    // A partition makes two rooms, and the pair of floors covers the building exactly once. The failure this
    // guards is two coplanar slabs overlapping down the length of the partition, which z-fights rather than
    // erroring, so it looks like a shader bug rather than a geometry one.
    public class AutoFloorSplitsOnAPartition : GameTest
    {
        public override string Name => "buildtool.auto_floor_partition";

        public override IEnumerable<Step> Run()
        {
            var eb = new EditorBuildings();
            World.AddChild(eb);
            yield return Step.Ticks(1);

            float y = eb.ActiveFloorY;
            RoomDraw.Square(eb, 0f, 0f, 12f);
            eb.AddWall(new Vector3(6f, y, 0f), 90f, 12f);      // straight down the middle, ends mid-wall
            yield return Step.Ticks(1);
            eb.SolveCorners();
            yield return Step.Ticks(1);

            eb.AutoFitRooms();
            yield return Step.Ticks(2);

            T.Check($"two rooms, two floors ({RoomDraw.Count(eb, SurfaceKind.Floor)})",
                    RoomDraw.Count(eb, SurfaceKind.Floor) == 2);
            T.Check("you can stand either side", RoomDraw.Solid(this, new Vector3(3f, y, -6f))
                                              && RoomDraw.Solid(this, new Vector3(9f, y, -6f)));

            // The two floors meet on the partition's centreline and do not cross it.
            var boxes = new List<Aabb>();
            foreach (var w in eb.Walls)
                if (GodotObject.IsInstanceValid(w) && w.Kind == SurfaceKind.Floor)
                    boxes.Add(new Aabb(w.UVToWorld(0f, 0f), Vector3.Zero).Merge(
                              new Aabb(w.UVToWorld(w.Length, w.Height), Vector3.Zero)));
            if (boxes.Count == 2)
            {
                float ox = Mathf.Min(boxes[0].End.X, boxes[1].End.X)
                         - Mathf.Max(boxes[0].Position.X, boxes[1].Position.X);
                float oz = Mathf.Min(boxes[0].End.Z, boxes[1].End.Z)
                         - Mathf.Max(boxes[0].Position.Z, boxes[1].Position.Z);
                T.Check($"the two floors do not overlap (x {ox:0.00}, z {oz:0.00})", ox < 0.01f || oz < 0.01f);
            }

            eb.QueueFree();
        }
    }

    // Foundations follow the walls that BOUND a room, and only under the lowest storey. A skirt hanging off
    // the first floor of a two-storey building is six metres of wall in mid-air.
    public class AutoFoundationsGoUnderTheLowestWallsOnly : GameTest
    {
        public override string Name => "buildtool.auto_foundation_lowest_only";

        public override IEnumerable<Step> Run()
        {
            var eb = new EditorBuildings();
            World.AddChild(eb);
            yield return Step.Ticks(1);

            RoomDraw.Square(eb, 0f, 0f, 12f);
            yield return Step.Ticks(1);
            eb.SolveCorners();
            yield return Step.Ticks(1);
            eb.AutoFitRooms();
            yield return Step.Ticks(1);

            T.Check($"the ground floor gets footings ({RoomDraw.Count(eb, SurfaceKind.Foundation)})",
                    RoomDraw.Count(eb, SurfaceKind.Foundation) == 4);

            // Now build the storey above and fit it. Its walls need no foundation -- there is a building
            // under them. BREAK IT: drop the AnyWallBelow test -> four more skirts appear in mid-air.
            eb.ChangeFloor(+1);
            RoomDraw.Square(eb, 0f, 0f, 12f);
            yield return Step.Ticks(1);
            eb.SolveCorners();
            yield return Step.Ticks(1);
            eb.AutoFitRooms();
            yield return Step.Ticks(1);

            T.Check($"the storey above adds none ({RoomDraw.Count(eb, SurfaceKind.Foundation)})",
                    RoomDraw.Count(eb, SurfaceKind.Foundation) == 4);
            T.Check($"but it does get its own floor ({RoomDraw.Count(eb, SurfaceKind.Floor)})",
                    RoomDraw.Count(eb, SurfaceKind.Floor) == 2);

            eb.QueueFree();
        }
    }

    // The plan is WALLS. Slabs, foundations and stair treads are all WallSurfaces too, and a floor fed in as
    // a wall is a wall lying across the room it was just built for -- so the next press finds rooms that are
    // really halves of the last one, and a foundation is a duplicate of the wall above it.
    //
    // This asserts the filter rather than a downstream symptom, deliberately. The symptom is order-dependent
    // (the first press has nothing to trip over, and a duplicate foundation edge dedupes away harmlessly), so
    // a behavioural test passes on a build with no filter at all -- which is what the first version of this
    // did. What is actually claimed here is small and exact: only walls reach the plan.
    public class AutoFloorPlanIsWallsOnly : GameTest
    {
        public override string Name => "buildtool.auto_floor_plan_is_walls_only";

        public override IEnumerable<Step> Run()
        {
            var eb = new EditorBuildings();
            World.AddChild(eb);
            yield return Step.Ticks(1);

            RoomDraw.Square(eb, 0f, 0f, 12f);
            yield return Step.Ticks(1);
            T.Check($"four walls, four segments ({eb.PlanOfActiveFloor().Count})",
                    eb.PlanOfActiveFloor().Count == 4);

            // Every other kind of surface the editor can make, all of them on this storey.
            eb.AddSlab(SurfaceKind.Floor);
            eb.AddFoundation();
            eb.AddStairs(new Vector3(3f, eb.ActiveFloorY, -3f), 0f);
            yield return Step.Ticks(1);
            T.Check($"the stage now holds much more than walls ({eb.Walls.Count})", eb.Walls.Count > 8);

            // BREAK IT: drop the Kind check in PlanOfActiveFloor -> this climbs past four.
            var plan = eb.PlanOfActiveFloor();
            T.Check($"but the plan is still just the four walls ({plan.Count})", plan.Count == 4);
            foreach (var seg in plan)
            {
                bool isWall = seg.Source >= 0 && seg.Source < eb.Walls.Count
                              && GodotObject.IsInstanceValid(eb.Walls[seg.Source])
                              && eb.Walls[seg.Source].Kind == SurfaceKind.Wall;
                T.Check($"segment {seg.Source} came from a wall", isWall);
            }

            eb.QueueFree();
        }
    }

    // The key goes through the one keyboard authority, same as every other build control. This has broken
    // twice before by a caller getting its own entry point.
    public class AutoFloorHasAKey : GameTest
    {
        public override string Name => "buildtool.auto_floor_key";

        public override IEnumerable<Step> Run()
        {
            var eb = new EditorBuildings();
            World.AddChild(eb);
            yield return Step.Ticks(1);

            RoomDraw.Square(eb, 0f, 0f, 12f);
            yield return Step.Ticks(1);
            eb.SolveCorners();
            yield return Step.Ticks(1);

            T.Check("H is handled", eb.HandleToolKey(Key.H));
            yield return Step.Ticks(2);
            T.Check($"...and it floored the room ({RoomDraw.Count(eb, SurfaceKind.Floor)})",
                    RoomDraw.Count(eb, SurfaceKind.Floor) == 1);
            // It is an ACTION, so it must not leave a tool armed behind it.
            T.Check($"it arms nothing ({eb.Tool})", eb.Tool == EditorBuildings.BuildTool.None);

            eb.QueueFree();
        }
    }
}
