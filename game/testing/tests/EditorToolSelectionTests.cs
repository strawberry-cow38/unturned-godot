using Godot;
using System.Collections.Generic;
using UnturnedSim;

namespace UnturnedGodot.Testing
{
    // EXACTLY ONE TOOL IS LIVE. This invariant has now been broken twice in this file, both times the same
    // way: the rule was written out by hand in more than one place and the copies drifted.
    //
    //   1st: five hand-written "clear the others", each clearing a DIFFERENT subset, so an opening preset
    //        could stay armed alongside the room tool. Fixed by centralising in the panel's SetTool.
    //   2nd: the KEYBOARD never went through it -- pressing 1-6 set _armed directly, leaving the room tool
    //        armed too, with the room button still lit. The fix had landed on the panel path only.
    //
    // So this asserts the invariant against EVERY tool rather than the one that broke, because the failure
    // mode is a NEW entry point forgetting the rule, and clicking around the editor cannot find that.
    public class EditorBuildingsHasExactlyOneToolLive : GameTest
    {
        public override string Name => "buildtool.one_tool_at_a_time";

        public override IEnumerable<Step> Run()
        {
            var eb = new EditorBuildings();
            World.AddChild(eb);
            yield return Step.Ticks(1);

            // Every mode flag the editor branches on, read back after each selection. If someone adds a tool
            // and a flag but forgets to clear it in SelectTool, the count below goes to 2.
            int LiveCount() =>
                (eb.WallDrawMode ? 1 : 0) + (eb.RoomDrawMode ? 1 : 0) + (eb.SlabDrawMode ? 1 : 0) +
                (eb.DeleteDrawMode ? 1 : 0) + (eb.FoundationDrawMode ? 1 : 0) + (eb.StairsDrawMode ? 1 : 0) +
                (eb.ArmedArchetype >= 0 ? 1 : 0);

            var tools = new[]
            {
                EditorBuildings.BuildTool.Wall, EditorBuildings.BuildTool.Room,
                EditorBuildings.BuildTool.Floor, EditorBuildings.BuildTool.Roof,
                EditorBuildings.BuildTool.Foundation, EditorBuildings.BuildTool.Delete,
                EditorBuildings.BuildTool.Stairs,
            };

            foreach (var t in tools)
            {
                eb.SelectTool(t);
                T.Check($"{t}: exactly one mode live ({LiveCount()})", LiveCount() == 1);
                T.Check($"{t}: Tool reports itself", eb.Tool == t);
            }

            // The historical failure, explicitly: arm a tool, then arm an opening preset. The preset must
            // REPLACE it, not join it. BREAK IT: set _armed directly instead of going through SelectTool.
            eb.SelectTool(EditorBuildings.BuildTool.Room);
            eb.SelectTool(EditorBuildings.BuildTool.Opening, 2);
            T.Check($"an opening preset replaces the room tool ({LiveCount()} live)", LiveCount() == 1);
            T.Check("...and it is the preset that is armed", eb.ArmedArchetype == 2 && !eb.RoomDrawMode);

            // And the reverse: a tool must disarm the preset. This is the direction the ORIGINAL bug went.
            eb.SelectTool(EditorBuildings.BuildTool.Wall);
            T.Check($"a tool disarms the preset ({eb.ArmedArchetype})", eb.ArmedArchetype < 0 && LiveCount() == 1);

            // None clears everything -- pressing the live tool's key again drops to select.
            eb.SelectTool(EditorBuildings.BuildTool.None);
            T.Check($"None leaves nothing live ({LiveCount()})", LiveCount() == 0);

            // Floor and Roof share SlabDrawMode, so the KIND has to switch or picking roof after floor
            // silently keeps drawing floors -- one flag serving two tools is the same drift risk.
            eb.SelectTool(EditorBuildings.BuildTool.Floor);
            T.Check("floor sets the slab kind", eb.SlabDrawKind == SurfaceKind.Floor);
            eb.SelectTool(EditorBuildings.BuildTool.Roof);
            T.Check("roof switches the slab kind", eb.SlabDrawKind == SurfaceKind.Roof);

            // AND THE KEYBOARD PATH ITSELF, not just the method it should call. This is the half that was
            // missing last time: SelectTool was correct and the caller bypassed it, so a test of SelectTool
            // alone would have stayed green through the whole bug.
            eb.SelectTool(EditorBuildings.BuildTool.Room);
            bool took = eb.HandleToolKey(Key.Key3);
            T.Check("the preset key is handled", took);
            T.Check($"...and it went THROUGH SelectTool ({LiveCount()} live, armed {eb.ArmedArchetype})",
                    LiveCount() == 1 && eb.ArmedArchetype == 2 && !eb.RoomDrawMode);

            T.Check("B arms the wall tool", eb.HandleToolKey(Key.B) && eb.Tool == EditorBuildings.BuildTool.Wall);
            T.Check("T arms stairs", eb.HandleToolKey(Key.T) && eb.Tool == EditorBuildings.BuildTool.Stairs);
            T.Check("X arms delete", eb.HandleToolKey(Key.X) && eb.Tool == EditorBuildings.BuildTool.Delete);
            // Pressing the live tool's key again returns to select rather than re-arming it.
            T.Check("X again returns to select", eb.HandleToolKey(Key.X) && eb.Tool == EditorBuildings.BuildTool.None);
            // A key that is not a tool key must NOT be swallowed, or it stops reaching Q/E and Ctrl+Z.
            T.Check("a non-tool key is left alone", !eb.HandleToolKey(Key.Q) && !eb.HandleToolKey(Key.Z));

            eb.QueueFree();
        }
    }

    // Basements. strawberry: "support for basements" -- which was one clamp, but the thing worth ASSERTING is
    // that a negative storey actually places BELOW the ground rather than merely being allowed as a number.
    public class EditorBuildingsGoesBelowGround : GameTest
    {
        public override string Name => "buildtool.basements";

        public override IEnumerable<Step> Run()
        {
            var eb = new EditorBuildings();
            World.AddChild(eb);
            yield return Step.Ticks(1);

            eb.ActiveFloor = 0;
            eb.ChangeFloor(-1);
            // BREAK IT: clamp at 0 (the original) -> ActiveFloor stays 0 and FloorY stays 0.
            T.Check($"Q goes below the ground floor ({eb.ActiveFloor})", eb.ActiveFloor == -1);
            T.Check($"...and that is BELOW it, not just a label ({eb.FloorY:0.00})",
                    eb.FloorY < -0.01f && Mathf.Abs(eb.FloorY + EditorBuildings.StoreyHeight) < 0.01f);

            // A basement is a whole storey down, same pitch as an upper floor -- so a two-storey building
            // with a basement spans exactly three storey heights.
            eb.ChangeFloor(-1);
            T.Check($"basements stack ({eb.FloorY:0.00})",
                    Mathf.Abs(eb.FloorY + 2f * EditorBuildings.StoreyHeight) < 0.01f);

            // Bounded, so hold-Q cannot walk the stage somewhere you can't fly back from.
            for (int i = 0; i < 40; i++) eb.ChangeFloor(-1);
            T.Check($"the descent is bounded ({eb.ActiveFloor})", eb.ActiveFloor == EditorBuildings.MinFloor);
            for (int i = 0; i < 60; i++) eb.ChangeFloor(+1);
            T.Check($"and the climb is too ({eb.ActiveFloor})", eb.ActiveFloor == EditorBuildings.MaxFloor);

            eb.QueueFree();
        }
    }

    // The lattice follows the ACTIVE storey. It is stage furniture built once at Y=0.02, so it sat on
    // the ground floor forever while everything you place goes to FloorY -- on any storey but the first
    // you were drawing against a grid that was not where the walls would land.
    public class EditorGridFollowsTheActiveStorey : GameTest
    {
        public override string Name => "buildtool.grid_follows_storey";

        public override IEnumerable<Step> Run()
        {
            var eb = new EditorBuildings();
            World.AddChild(eb);
            yield return Step.Ticks(1);
            eb.SetActive(true);              // builds the stage, which is what owns the grid
            yield return Step.Ticks(1);

            MeshInstance3D Grid()
            {
                foreach (var st in eb.GetChildren())
                    if (st is Node3D stage && stage.Name == "Stage")
                        foreach (var c in stage.GetChildren())
                            if (c is MeshInstance3D mi && mi.Mesh is ArrayMesh) return mi;
                return null;
            }
            var g = Grid();
            T.Check("found the lattice", g != null);
            if (g == null) yield break;

            T.Check($"on the ground floor it sits at 0 ({g.Position.Y:0.00})", Mathf.Abs(g.Position.Y) < 0.01f);

            // BREAK IT: drop the PositionGrid() call in ChangeFloor -> stays at 0 while FloorY climbs.
            eb.ChangeFloor(+2);
            yield return Step.Ticks(1);
            T.Check($"two storeys up it follows ({g.Position.Y:0.00} vs {eb.FloorY:0.00})",
                    Mathf.Abs(g.Position.Y - eb.FloorY) < 0.01f);
            T.Check($"...and that is actually above the ground ({eb.FloorY:0.00})", eb.FloorY > 1f);

            // Down into a basement too -- the clamp allows it, so the grid has to go there.
            eb.ChangeFloor(-3);
            yield return Step.Ticks(1);
            T.Check($"and it follows into a basement ({g.Position.Y:0.00} vs {eb.FloorY:0.00})",
                    Mathf.Abs(g.Position.Y - eb.FloorY) < 0.01f && eb.FloorY < -0.01f);

            eb.QueueFree();
        }
    }

    // Overlap warning behind the placement ghosts. strawberry asked for "prevent placing overlapping stuff,
    // showing the ghost as red", then chose WARN rather than refuse -- so this is about the signal being
    // TRUSTWORTHY, not about blocking anything.
    //
    // The failure mode that matters is a light that is always on: surfaces are MEANT to touch along shared
    // edges and at corners, and a naive AABB test flags every neighbouring wall. A warning that fires on
    // correct building is one people learn to ignore, which is worse than no warning.
    public class EditorBuildingsWarnsOnRealOverlapOnly : GameTest
    {
        public override string Name => "buildtool.overlap_warning";

        public override IEnumerable<Step> Run()
        {
            var eb = new EditorBuildings();
            World.AddChild(eb);
            yield return Step.Ticks(1);

            var w = eb.AddWall(new Vector3(0f, 0f, 0f), 0f, 12f);
            yield return Step.Ticks(1);
            T.Check("a wall exists to clash with", GodotObject.IsInstanceValid(w));

            // Squarely inside the wall's run, at its height: a real clash.
            var inside = new Aabb(new Vector3(4f, 1f, -0.2f), new Vector3(2f, 2f, 0.4f));
            T.Check("a box inside the wall clashes", eb.WouldOverlap(inside));

            // Well clear of it: no warning. BREAK IT: drop the Intersects test and everything clashes.
            var away = new Aabb(new Vector3(40f, 1f, 40f), new Vector3(2f, 2f, 2f));
            T.Check("a box well away does not", !eb.WouldOverlap(away));

            // ABOVE it -- the wall is one storey tall, so the next floor up must not read as a clash, or
            // building a second storey warns on every single piece.
            var upstairs = new Aabb(new Vector3(4f, EditorBuildings.StoreyHeight + 0.5f, -0.2f),
                                    new Vector3(2f, 2f, 0.4f));
            T.Check("the storey above is not a clash", !eb.WouldOverlap(upstairs));

            // A HAIR of interpenetration, which is what corner solving actually produces -- walls are
            // extended to cross their neighbour's centreline, so adjoining pieces genuinely overlap by
            // millimetres. This is the always-on-light case, and note that EXACT edge-touching would pass
            // regardless (Godot's Aabb.Intersects is false for surfaces that merely meet), so testing that
            // would prove nothing about the tolerance.
            // BREAK IT: remove the Grow(-0.05f) and this fails.
            var hair = new Aabb(new Vector3(11.98f, 0f, -0.2f), new Vector3(3f, 2f, 0.4f));
            T.Check("a neighbour overlapping by a hair is not a clash", !eb.WouldOverlap(hair));

            // And a surface never clashes with ITSELF when asked to ignore it -- otherwise dragging an
            // existing wall would light up red against its own old position.
            T.Check("a surface can be excluded", !eb.WouldOverlap(inside, w));

            eb.QueueFree();
        }
    }
}
