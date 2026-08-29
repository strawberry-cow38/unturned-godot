using Godot;
using System.Collections.Generic;
using UnturnedSim;

namespace UnturnedGodot.Testing
{
    // Roof shapes and editable roofs, in the engine.
    //
    // RoofShapes is covered at L0 for its arithmetic. What L0 deliberately does NOT check is whether the
    // planes it describes actually meet, because proving that there would mean rebuilding Godot's yaw and
    // pitch transform inside the test -- and a test that reimplements the convention it is checking agrees
    // with the code precisely when the convention is wrong. So the meeting is checked here, by asking the
    // real surfaces where their top edges ended up.
    static class RoofProbe
    {
        /// <summary>A hall: 24 along X by 12 along Z, laid corner to corner anticlockwise.</summary>
        public static void Hall(EditorBuildings eb, float w = 24f, float d = 12f)
        {
            float y = eb.ActiveFloorY;
            eb.AddWall(new Vector3(0f, y, 0f), 0f, w);
            eb.AddWall(new Vector3(w, y, 0f), 90f, d);
            eb.AddWall(new Vector3(w, y, -d), 180f, w);
            eb.AddWall(new Vector3(0f, y, -d), 270f, d);
        }

        /// <summary>The two ends of a roof plane's TOP edge, in world space.
        ///
        /// Not UVToWorld(0, Height) and UVToWorld(Length, Height): a tapered surface's top edge is cut in
        /// from both sides, and on a hip's triangular end those cuts meet, so reading the uncut corners
        /// would report a full-width edge for a plane that comes to a point.</summary>
        public static (Vector3 A, Vector3 B) TopEdge(WallSurface w)
            => (w.UVToWorld(w.InsetL1, w.Height), w.UVToWorld(w.Length - w.InsetR1, w.Height));

        public static List<WallSurface> Roofs(EditorBuildings eb)
        {
            var list = new List<WallSurface>();
            foreach (var w in eb.Walls)
                if (GodotObject.IsInstanceValid(w) && w.Kind == SurfaceKind.Roof) list.Add(w);
            return list;
        }

        public static float MaxGableRise(EditorBuildings eb)
        {
            float m = 0f;
            foreach (var w in eb.Walls)
                if (GodotObject.IsInstanceValid(w) && w.Kind == SurfaceKind.Wall)
                    m = Mathf.Max(m, w.GableRise);
            return m;
        }
    }

    // THE CLAIM THAT MAKES A HIP A HIP: four planes arriving at one ridge. They only do so because all four
    // rise over the same run at the same pitch -- get the ends' slope length from the long span instead and
    // they overshoot, leaving two open wedges along the ridge that read as a shading artefact rather than a
    // hole. Nothing about that is visible in the numbers RoofShapes returns; it is a fact about where the
    // surfaces land once yaw, pitch and the trapezoid cuts have all been applied.
    public class HipRoofPlanesMeetAtOneRidge : GameTest
    {
        public override string Name => "buildtool.roof_hip_planes_meet";

        public override IEnumerable<Step> Run()
        {
            var eb = new EditorBuildings();
            World.AddChild(eb);
            yield return Step.Ticks(1);

            RoofProbe.Hall(eb);
            yield return Step.Ticks(1);
            eb.SolveCorners();
            yield return Step.Ticks(1);

            var roof = eb.PlaceRoofOverWalls(RoofKind.Hip, 30f);
            yield return Step.Ticks(1);
            T.Check("a hip was placed", roof != null);
            if (roof == null) yield break;

            var planes = RoofProbe.Roofs(eb);
            T.Check($"four planes ({planes.Count})", planes.Count == 4);
            if (planes.Count != 4) yield break;

            // Every top-edge endpoint of every plane must sit on ONE horizontal line.
            float loY = float.MaxValue, hiY = float.MinValue;
            var pts = new List<Vector3>();
            foreach (var p in planes)
            {
                var (a, b) = RoofProbe.TopEdge(p);
                pts.Add(a); pts.Add(b);
                loY = Mathf.Min(loY, Mathf.Min(a.Y, b.Y));
                hiY = Mathf.Max(hiY, Mathf.Max(a.Y, b.Y));
            }
            // BREAK IT: give the end planes a different slope length -> they land above or below the sides.
            T.Check($"all four planes top out at one height (spread {hiY - loY:0.000})", hiY - loY < 0.02f);

            // ...and the line is a line: the ridge runs along X here, so every point shares a Z.
            float loZ = float.MaxValue, hiZ = float.MinValue;
            foreach (var p in pts) { loZ = Mathf.Min(loZ, p.Z); hiZ = Mathf.Max(hiZ, p.Z); }
            T.Check($"and on one line in plan (z spread {hiZ - loZ:0.000})", hiZ - loZ < 0.02f);

            // The two hipped ends come to a POINT, and those points are the ends of the ridge -- so the
            // spread of all eight points along X is the ridge length, not the roof's.
            float loX = float.MaxValue, hiX = float.MinValue;
            foreach (var p in pts) { loX = Mathf.Min(loX, p.X); hiX = Mathf.Max(hiX, p.X); }
            float ridge = hiX - loX;
            float expected = RoofShapes.RidgeLength(roof.Spec);
            T.Check($"the ridge is shortened by a run at each end ({ridge:0.00} vs {expected:0.00})",
                    Mathf.Abs(ridge - expected) < 0.05f);
            T.Check($"...which is shorter than the roof itself ({ridge:0.00} < {roof.Spec.SpanX:0.00})",
                    ridge < roof.Spec.SpanX - 1f);

            // A hip closes its own ends, so the roof is FOUR PLANES AND NOTHING ELSE -- no raised walls and
            // no gable bands. Checking the walls' own GableRise is not enough: with any overhang the gable
            // path spawns a separate band SURFACE and never touches the wall, so a hip growing gable ends
            // would leave the walls reading zero while two triangles poke through the hipped ends.
            //
            // BREAK IT: let WallGetsGable return true for Hip -> two band surfaces join the members.
            int nonPlane = 0;
            foreach (var m in roof.Members)
                if (GodotObject.IsInstanceValid(m) && m.Kind != SurfaceKind.Roof) nonPlane++;
            T.Check($"the hip is four planes and nothing else ({roof.Members.Count}, {nonPlane} not planes)",
                    roof.Members.Count == 4 && nonPlane == 0);
            T.Check($"and no wall was raised ({roof.Raised.Count})", roof.Raised.Count == 0);

            eb.QueueFree();
        }
    }

    // A square hip is a pyramid: the ridge collapses to a point and all four planes meet there. Worth its own
    // case because it is the degenerate end of the same formula, and a guard added "to be safe" would break
    // it into something that is not a pyramid.
    public class ASquareHipIsAPyramid : GameTest
    {
        public override string Name => "buildtool.roof_square_hip_is_a_pyramid";

        public override IEnumerable<Step> Run()
        {
            var eb = new EditorBuildings();
            World.AddChild(eb);
            yield return Step.Ticks(1);

            RoofProbe.Hall(eb, 12f, 12f);
            yield return Step.Ticks(1);
            eb.SolveCorners();
            yield return Step.Ticks(1);
            eb.PlaceRoofOverWalls(RoofKind.Hip, 35f);
            yield return Step.Ticks(1);

            var planes = RoofProbe.Roofs(eb);
            T.Check($"four faces ({planes.Count})", planes.Count == 4);
            if (planes.Count != 4) yield break;

            var apexes = new List<Vector3>();
            foreach (var p in planes) { var (a, b) = RoofProbe.TopEdge(p); apexes.Add(a); apexes.Add(b); }

            float spread = 0f;
            foreach (var a in apexes)
                foreach (var b in apexes) spread = Mathf.Max(spread, a.DistanceTo(b));
            T.Check($"every face comes to the same apex (spread {spread:0.000})", spread < 0.05f);

            eb.QueueFree();
        }
    }

    // MODIFYING A PLACED ROOF, which was the ask. Before this, a roof was gone the moment it was drawn -- six
    // surfaces and two raised walls with nothing recording they were one thing -- so changing the pitch meant
    // undoing back past it and redrawing.
    public class APlacedRoofCanBeChanged : GameTest
    {
        public override string Name => "buildtool.roof_modify_in_place";

        public override IEnumerable<Step> Run()
        {
            var eb = new EditorBuildings();
            World.AddChild(eb);
            yield return Step.Ticks(1);

            RoofProbe.Hall(eb);
            yield return Step.Ticks(1);
            eb.SolveCorners();
            yield return Step.Ticks(1);

            var roof = eb.PlaceRoofOverWalls(RoofKind.Gable, 20f);
            yield return Step.Ticks(1);
            T.Check("a gable was placed", roof != null && RoofProbe.Roofs(eb).Count == 2);
            if (roof == null) yield break;

            float RidgeY()
            {
                float y = float.MinValue;
                foreach (var p in RoofProbe.Roofs(eb))
                { var (a, b) = RoofProbe.TopEdge(p); y = Mathf.Max(y, Mathf.Max(a.Y, b.Y)); }
                return y;
            }

            float shallow = RidgeY();
            int id = roof.Id;

            // Steepen it.
            var spec = roof.Spec;
            spec.PitchDeg = 45f;
            var steeper = eb.ModifyRoof(roof, spec);
            yield return Step.Ticks(2);

            T.Check("the roof survived the edit", steeper != null);
            T.Check($"and kept its identity ({steeper?.Id} vs {id})", steeper != null && steeper.Id == id);
            // BREAK IT: skip RemoveRoof in ModifyRoof -> 4 planes, the old roof still inside the new one.
            T.Check($"the old planes are GONE, not buried ({RoofProbe.Roofs(eb).Count})",
                    RoofProbe.Roofs(eb).Count == 2);
            T.Check($"the ridge went up ({shallow:0.00} -> {RidgeY():0.00})", RidgeY() > shallow + 0.5f);
            T.Check($"only one roof is on record ({eb.Roofs.Count})", eb.Roofs.Count == 1);

            // A gable raised gable ends on two walls. Switching to a hip has to take them back DOWN --
            // a hip closes its own ends, and leftover triangles would stick through the new roof.
            T.Check($"the gable raised its ends ({RoofProbe.MaxGableRise(eb):0.00})",
                    RoofProbe.MaxGableRise(eb) > 0.01f);

            spec.Kind = RoofKind.Hip;
            var hipped = eb.ModifyRoof(steeper, spec);
            yield return Step.Ticks(2);
            T.Check($"it is a hip now ({RoofProbe.Roofs(eb).Count} planes)",
                    hipped != null && RoofProbe.Roofs(eb).Count == 4);
            // BREAK IT: forget to restore Raised in RemoveRoof -> the old gable ends stay up.
            T.Check($"and the gable ends came back down ({RoofProbe.MaxGableRise(eb):0.00})",
                    RoofProbe.MaxGableRise(eb) < 0.01f);

            eb.QueueFree();
        }
    }

    // Removing a roof puts the walls back as they were -- back to what they HAD, not to zero. An imported
    // building can carry an authored gable on a wall, and a roof placed over it and then removed would
    // otherwise flatten a wall the roof never made.
    public class RemovingARoofRestoresWhatTheWallsHad : GameTest
    {
        public override string Name => "buildtool.roof_remove_restores_walls";

        public override IEnumerable<Step> Run()
        {
            var eb = new EditorBuildings();
            World.AddChild(eb);
            yield return Step.Ticks(1);

            RoofProbe.Hall(eb);
            yield return Step.Ticks(1);
            eb.SolveCorners();
            yield return Step.Ticks(1);

            // An authored gable, as an import leaves behind.
            WallSurface authored = null;
            foreach (var w in eb.Walls)
                if (GodotObject.IsInstanceValid(w) && w.Kind == SurfaceKind.Wall
                    && Mathf.Abs(Mathf.Wrap(w.RotationDegrees.Y, 0f, 180f) - 90f) < 1f)
                { authored = w; break; }
            T.Check("found a wall across the ridge", authored != null);
            if (authored == null) yield break;

            authored.GableRise = 1.75f;
            authored.Rebuild();
            yield return Step.Ticks(1);

            var roof = eb.PlaceRoofOverWalls(RoofKind.Gable, 25f);
            yield return Step.Ticks(1);
            T.Check($"the roof overwrote it ({authored.GableRise:0.00})",
                    roof != null && Mathf.Abs(authored.GableRise - 1.75f) > 0.05f);

            eb.RemoveRoof(roof);
            yield return Step.Ticks(1);
            // BREAK IT: restore to 0 instead of the recorded previous value -> 0.00, and the import is lost.
            T.Check($"removing it put back what was there ({authored.GableRise:0.00})",
                    Mathf.Abs(authored.GableRise - 1.75f) < 0.01f);
            T.Check($"and the roof surfaces are gone ({RoofProbe.Roofs(eb).Count})",
                    RoofProbe.Roofs(eb).Count == 0);
            T.Check($"and it is off the record ({eb.Roofs.Count})", eb.Roofs.Count == 0);

            eb.QueueFree();
        }
    }
}
