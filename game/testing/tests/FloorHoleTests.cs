using Godot;
using System.Collections.Generic;
using UnturnedSim;

namespace UnturnedGodot.Testing
{
    // Deleting a SECTION of a floor. strawberry: "the abilitly to partially delete sections of walls/floors".
    //
    // Wall span-cutting already existed; a slab could only be stripped full-width, which halves the floor
    // rather than putting a hole in it -- no stairwell, no light well. PunchHole carves a rectangle instead,
    // as an OPENING, so it rides the same partition that cuts doors and windows.
    //
    // That choice is the reason this test raycasts rather than counting triangles: the whole generate-don't-cut
    // design exists so the mesh and the collider come from ONE partition. A hole you can see and cannot fall
    // through is exactly the bug a CSG boolean gives you, and it renders perfectly.
    public class FloorHoleIsWalkThroughNotJustVisible : GameTest
    {
        public override string Name => "buildtool.floor_hole_is_real";

        public override IEnumerable<Step> Run()
        {
            var eb = new EditorBuildings();
            World.AddChild(eb);
            yield return Step.Ticks(1);

            // A flat slab, the way the editor makes one: lying down (pitch -90), 12 x 12.
            var floor = new WallSurface { Length = 12f, Height = 12f, Thickness = 0.5f, Kind = SurfaceKind.Floor };
            World.AddChild(floor);
            floor.RotationDegrees = new Vector3(-90f, 0f, 0f);
            floor.Rebuild();
            yield return Step.Ticks(2);

            // NO Rebuild() here on purpose. The first version of this test called floor.Rebuild() itself,
            // which made the collider correct whether or not PunchHole did -- so dropping the Rebuild from
            // PunchHole left this test GREEN. A test that supplies the step it is checking cannot fail.
            bool punched = eb.PunchHole(floor, 4f, 4f, 8f, 8f);
            yield return Step.Ticks(2);          // let Jolt take the new shapes
            T.Check("the rectangle was punched", punched && floor.Openings.Count == 1);

            var space = World.GetWorld3D().DirectSpaceState;
            bool Solid(float u, float v)
            {
                var p = floor.UVToWorld(u, v);
                var q = new PhysicsRayQueryParameters3D
                { From = p + new Vector3(0f, 3f, 0f), To = p - new Vector3(0f, 3f, 0f), CollisionMask = 1u << 0 };
                return space.IntersectRay(q).Count > 0;
            }

            // The claim: you fall through the middle and stand on the rest.
            T.Check("you fall through the hole", !Solid(6f, 6f));
            T.Check("the floor beyond the hole still holds you (near corner)", Solid(1.5f, 1.5f));
            T.Check("...and on the far side of it", Solid(10.5f, 10.5f));
            T.Check("...and beside it on both axes", Solid(6f, 1.5f) && Solid(1.5f, 6f));

            // The slab SURVIVES -- a hole is not a delete. This is the half that separates "punched a hole"
            // from "removed the floor and got lucky with the raycasts".
            T.Check("the slab is still there", GodotObject.IsInstanceValid(floor));

            // Degenerate drags are refused rather than making slivers nobody can see or select.
            T.Check("a tiny drag is refused", !eb.PunchHole(floor, 1f, 1f, 1.05f, 1.05f));
            T.Check("...and did not add an opening", floor.Openings.Count == 1);

            // Dragging over the WHOLE slab means delete, not a hole with nothing around it.
            var doomed = new WallSurface { Length = 6f, Height = 6f, Thickness = 0.5f, Kind = SurfaceKind.Floor };
            World.AddChild(doomed);
            doomed.RotationDegrees = new Vector3(-90f, 0f, 0f);
            doomed.Rebuild();
            yield return Step.Ticks(1);
            T.Check("a full-slab drag deletes it", eb.PunchHole(doomed, -1f, -1f, 99f, 99f));

            floor.QueueFree();
            eb.QueueFree();
        }
    }
}
