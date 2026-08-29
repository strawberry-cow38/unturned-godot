using Godot;
using System.Collections.Generic;
using UnturnedSim;

namespace UnturnedGodot.Testing
{
    // strawberry_cow: "prevent placing overlapping stuff, showing the ghost as red. give everything that
    // doesnt have a ghost a ghost."
    //
    // WHAT A HEADLESS RUN CAN AND CANNOT SEE. The ghost is node state -- how many boxes, where, what
    // albedo -- and all of that is readable without rendering a pixel. What these tests CANNOT tell you is
    // whether the red is legible against a red brick wall, whether the ghost z-fights the surface it
    // previews, or whether it is visible at all through the wall in front of it. Those need eyes on it.
    static class Gh
    {
        public static EditorBuildings Rig(GameTest t, out Editor ed)
        {
            ed = new Editor(); t.World.AddChild(ed);
            var eb = new EditorBuildings(); t.World.AddChild(eb);
            eb.Setup(ed, null, null);
            eb.RestoreAll(new List<WallPlan>());       // Setup loads whatever is on disk; start known-empty
            return eb;
        }

        public static bool IsRed(Color c) => c.R > 0.7f && c.G < 0.4f;
    }

    public class EveryDragToolHasAGhost : GameTest
    {
        public override string Name => "buildtool.every_drag_tool_has_a_ghost";

        public override IEnumerable<Step> Run()
        {
            var eb = Gh.Rig(this, out var ed);
            yield return Step.Ticks(1);
            var at = new Vector3(30f, eb.ActiveFloorY, -30f);      // open ground, nothing to clash with

            // BREAK IT: drop any tool's branch from UpdatePlacementGhost -> that tool goes back to placing
            // blind, which is the state every one of them was in.
            foreach (var tool in new[] { EditorBuildings.BuildTool.Wall, EditorBuildings.BuildTool.Room,
                                         EditorBuildings.BuildTool.Floor, EditorBuildings.BuildTool.Roof,
                                         EditorBuildings.BuildTool.Foundation })
            {
                eb.SelectTool(tool);
                eb.UpdatePlacementGhost(at, 0f);
                yield return Step.Ticks(1);
                T.Check($"{tool} shows a ghost ({eb.Ghosts.VisibleCount} box(es))", eb.Ghosts.VisibleCount > 0);
                T.Check($"{tool}'s ghost is clear over open ground", !eb.Ghosts.Clashing);
            }

            // Stairs kept theirs through the migration onto the shared ghost -- a flight is several treads,
            // so this also proves the shared ghost still handles more than one box.
            eb.SelectTool(EditorBuildings.BuildTool.Stairs);
            eb.UpdateStairGhost(at, 0f);
            yield return Step.Ticks(1);
            T.Check($"stairs still preview a whole flight ({eb.Ghosts.VisibleCount} treads)",
                    eb.Ghosts.VisibleCount > 1);

            // BREAK IT: leave the ghost up when nothing is armed -> a box floats over the map for the rest
            // of the session, and it is not obviously a ghost rather than something you placed.
            eb.SelectTool(EditorBuildings.BuildTool.None);
            eb.UpdatePlacementGhost(at, 0f);
            yield return Step.Ticks(1);
            T.Check($"no armed tool means no ghost ({eb.Ghosts.VisibleCount})", eb.Ghosts.VisibleCount == 0);

            eb.QueueFree(); ed.QueueFree();
        }
    }

    public class GhostGoesRedInsideSomething : GameTest
    {
        public override string Name => "buildtool.ghost_goes_red_inside_something";

        public override IEnumerable<Step> Run()
        {
            var eb = Gh.Rig(this, out var ed);
            yield return Step.Ticks(1);
            float y = eb.ActiveFloorY;

            // A wall to collide with, and a spot well clear of it.
            eb.AddWall(new Vector3(0f, y, 0f), 0f, 12f);
            yield return Step.Ticks(1);
            var clear = new Vector3(60f, y, -60f);
            var inside = new Vector3(6f, y, 0f);                   // mid-way along the wall

            eb.SelectTool(EditorBuildings.BuildTool.Room);
            eb.UpdatePlacementGhost(clear, 0f);
            yield return Step.Ticks(1);
            // Assert it is DRAWN before asserting it is not red: a hidden ghost also reports no clash, so
            // without this the clear case passes for the wrong reason and the test proves nothing.
            T.Check($"the clear ghost is actually drawn ({eb.Ghosts.VisibleCount})", eb.Ghosts.VisibleCount > 0);
            T.Check("and reports no clash", !eb.Ghosts.Clashing);
            T.Check($"and is not tinted red ({eb.Ghosts.Tint})", !Gh.IsRed(eb.Ghosts.Tint));

            // BREAK IT: pass null as the overlap predicate -> Clashing is never set and the ghost is always
            // blue, which is the state every tool but stairs was in.
            eb.UpdatePlacementGhost(inside, 0f);
            yield return Step.Ticks(1);
            T.Check($"the ghost is drawn on the wall too ({eb.Ghosts.VisibleCount})", eb.Ghosts.VisibleCount > 0);
            T.Check("over an existing wall it reports a clash", eb.Ghosts.Clashing);
            T.Check($"and is tinted red ({eb.Ghosts.Tint})", Gh.IsRed(eb.Ghosts.Tint));

            eb.QueueFree(); ed.QueueFree();
        }
    }

    public class TouchingIsNotOverlapping : GameTest
    {
        public override string Name => "buildtool.ghost_touching_is_not_overlapping";

        public override IEnumerable<Step> Run()
        {
            // strawberry_cow: "warn on overlap not touch obv". Buildings are made of surfaces that MEET --
            // every corner, every partition running into an outer wall -- so a check that fires on contact
            // is a light that is always on, and a light that is always on is off.
            var eb = Gh.Rig(this, out var ed);
            yield return Step.Ticks(1);
            float y = eb.ActiveFloorY;

            eb.AddWall(new Vector3(0f, y, 0f), 0f, 12f);
            yield return Step.Ticks(1);

            eb.SelectTool(EditorBuildings.BuildTool.Wall);

            // Butted up against the far end of that wall, the way a corner is drawn.
            eb.UpdatePlacementGhost(new Vector3(12f, y, 0f), 0f);
            yield return Step.Ticks(1);
            T.Check($"the ghost is drawn ({eb.Ghosts.VisibleCount})", eb.Ghosts.VisibleCount > 0);

            // BREAK IT: remove the Grow(-0.05f) shrink from WouldOverlap -> every corner and every partition
            // junction reads as an overlap, and the warning stops meaning anything.
            T.Check("meeting a wall end-on is not an overlap", !eb.Ghosts.Clashing);
            T.Check($"so it stays clear ({eb.Ghosts.Tint})", !Gh.IsRed(eb.Ghosts.Tint));

            eb.QueueFree(); ed.QueueFree();
        }
    }

    public class GhostIsNotASurface : GameTest
    {
        public override string Name => "buildtool.ghost_is_not_a_surface";

        public override IEnumerable<Step> Run()
        {
            // A ghost is scenery for the cursor. If it ever counted as a surface it would be saved, would
            // enclose "rooms", would be duplicated by ctrl+D and would clash with itself -- and the last one
            // means every ghost turns red the instant it appears.
            var eb = Gh.Rig(this, out var ed);
            yield return Step.Ticks(1);
            float y = eb.ActiveFloorY;

            eb.SelectTool(EditorBuildings.BuildTool.Room);
            var at = new Vector3(30f, y, -30f);
            eb.UpdatePlacementGhost(at, 0f);
            yield return Step.Ticks(1);
            T.Check($"a ghost is up ({eb.Ghosts.VisibleCount})", eb.Ghosts.VisibleCount > 0);

            T.Check($"but no surface exists ({eb.Walls.Count})", eb.Walls.Count == 0);
            T.Check($"and nothing would be saved ({eb.Snapshot().Count})", eb.Snapshot().Count == 0);

            // BREAK IT: feed the ghost's own boxes to WouldOverlap -> it clashes with itself and is red from
            // the first frame, everywhere, which reads as "you can never place anything".
            eb.UpdatePlacementGhost(at, 0f);
            yield return Step.Ticks(1);
            T.Check("and it does not clash with itself", !eb.Ghosts.Clashing);

            eb.QueueFree(); ed.QueueFree();
        }
    }

    public class DragGhostIgnoresWhatIsBeingDrawn : GameTest
    {
        public override string Name => "buildtool.drag_ghost_ignores_what_is_being_drawn";

        public override IEnumerable<Step> Run()
        {
            // Once a drag starts the surface is REAL and already in _walls, so a clash check that does not
            // exclude it finds the thing you are dragging and goes red immediately -- for the whole drag,
            // every time, which is the same as having no warning at all.
            var eb = Gh.Rig(this, out var ed);
            yield return Step.Ticks(1);
            float y = eb.ActiveFloorY;

            eb.SelectTool(EditorBuildings.BuildTool.Wall);
            var w = eb.BeginWallDraw(new Vector3(40f, y, -40f));
            w.Length = 12f; w.Rebuild();
            yield return Step.Ticks(1);
            T.Check("a wall is being drawn", eb.Drawing);

            eb.UpdatePlacementGhost(new Vector3(40f, y, -40f), 0f);
            yield return Step.Ticks(1);
            T.Check($"the drag ghost tracks the surface ({eb.Ghosts.VisibleCount})", eb.Ghosts.VisibleCount > 0);

            // BREAK IT: drop the `drawn` set from the WouldOverlap call -> this is red, always.
            T.Check("and does not count the wall being drawn against itself", !eb.Ghosts.Clashing);

            // A second wall genuinely in the way still registers. It has to actually CROSS the drawn wall:
            // yaw 90 runs -Z, so an origin at Z=-34 spans -34..-46 through the drawn wall's Z=-40. My first
            // attempt started it at -46 and it ran AWAY, so the fixture proved nothing and read as a bug in
            // the ignore-set.
            eb.AddWall(new Vector3(46f, y, -34f), 90f, 12f);
            yield return Step.Ticks(1);
            eb.UpdatePlacementGhost(new Vector3(40f, y, -40f), 0f);
            yield return Step.Ticks(1);
            T.Check("but a real obstacle still turns it red", eb.Ghosts.Clashing);

            eb.QueueFree(); ed.QueueFree();
        }
    }
}
