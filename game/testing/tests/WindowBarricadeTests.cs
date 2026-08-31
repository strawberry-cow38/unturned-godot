using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // The window barricade (master 2026-08-31): a deployable that snaps INTO a building-editor window opening, one per
    // inside/outside face, placeable ONLY when the reticle is on a window. A window HOLE has no collider, so the placer
    // UV-projects the camera ray onto live WallSurface nodes ("walls" group) instead of raycasting. This drives that
    // real path: front/back face detection, the one-per-face slot gate, and that a floor-pinned DOOR opening is rejected.
    public sealed class WindowBarricadeTests : GameTest
    {
        public override string Name => "barricade.window_snap";
        public override double TimeoutSimSeconds => 30;

        static Camera3D CamLookingAt(Node parent, Vector3 pos, Vector3 target)
        {
            var cam = new Camera3D();
            parent.AddChild(cam);          // AddChild BEFORE LookAt (LookAt needs the node in the tree)
            cam.GlobalPosition = pos;
            cam.LookAt(target, Vector3.Up);
            return cam;
        }

        public override IEnumerable<Step> Run()
        {
            var wall = new WallSurface { Length = 6f, Height = 3f, Thickness = 0.5f };
            wall.Openings.Add(new UnturnedSim.WallOpening(0.5f, 0f, 1.5f, 2.5f));    // opening 0: a DOOR -- floor-pinned (V=0), NOT a window
            wall.Openings.Add(new UnturnedSim.WallOpening(2.5f, 1.0f, 1.5f, 1.5f));  // opening 1: a WINDOW -- sill at V=1.0
            World.AddChild(wall);
            yield return Ticks(2);   // _Ready -> Rebuild + AddToGroup("walls")

            var placer = new BarricadePlacer();
            World.AddChild(placer);
            placer.SetDef(DeployableDef.WindowBarricade);
            Vector3 winCentre = wall.UVToWorld(2.5f + 0.75f, 1.0f + 0.75f);   // opening 1 centre in world space

            // aim at the WINDOW from the +Z (front) face
            var camFront = CamLookingAt(World, winCentre + Vector3.Back * 3f, winCentre);   // Vector3.Back = +Z
            yield return Ticks(1);
            bool v1 = placer.Aim(camFront);
            T.Check($"snaps to the window from the front (opening {placer.SnappedOpening}, valid={v1})", v1 && placer.SnappedOpening == 1);
            T.Check($"...on the +Z (front) face (face={placer.SnappedFace})", placer.SnappedFace == 1);

            // place it on the front face
            var b1 = Barricade.PlaceInWindow(wall, 1, 1, DeployableDef.WindowBarricade);
            yield return Ticks(1);
            T.Check("the barricade spawned as a child of the wall", b1 != null && b1.GetParent() == wall);
            T.Check("...and is stamped with its slot (opening 1, +Z)", b1 != null && b1.HasMeta("ug_wb_opening") && b1.GetMeta("ug_wb_opening").AsInt32() == 1);

            // the front slot is now TAKEN -> aiming there again is INVALID
            yield return Ticks(1);
            bool v2 = placer.Aim(camFront);
            T.Check("re-aiming the same (front) face is now INVALID -- slot filled", !v2);

            // but the BACK (inside) face is still free
            var camBack = CamLookingAt(World, winCentre + Vector3.Forward * 3f, winCentre);   // Vector3.Forward = -Z
            yield return Ticks(1);
            bool v3 = placer.Aim(camBack);
            T.Check($"the opposite (inside) face is still placeable (valid={v3}, face={placer.SnappedFace})", v3 && placer.SnappedFace == -1);

            // aiming at the DOOR opening (floor-pinned) is NOT a window -> invalid
            Vector3 doorCentre = wall.UVToWorld(0.5f + 0.75f, 1.25f);
            var camDoor = CamLookingAt(World, doorCentre + Vector3.Back * 3f, doorCentre);
            yield return Ticks(1);
            bool v4 = placer.Aim(camDoor);
            T.Check("a floor-pinned DOOR opening is not a window -> invalid", !v4);

            placer.QueueFree();
            if (b1 != null && GodotObject.IsInstanceValid(b1)) b1.QueueFree();
        }
    }
}
