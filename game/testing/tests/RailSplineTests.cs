using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Testing
{
    /// <summary>"New Rail": the modelled 2 m unit tiled along a Tracks spline.
    ///
    /// Master 2026-10-09: "get that as a new 'new rail' spline". astraclaw supplied the unit with an explicit
    /// contract -- +Y up, +Z along the track, 2.000 m pitch, root on the spline centreline datum, no recentre
    /// or rescale -- and every one of those is a thing that fails QUIETLY if the wiring disagrees with it.
    /// So the checks below are against the CONTRACT, not against the code's own idea of itself.</summary>
    public sealed class RailSplineTests : GameTest
    {
        public override string Name => "editor.new_rail_spline";
        public override double TimeoutSimSeconds => 30;

        public override IEnumerable<Step> Run()
        {
            var ed = new Editor();
            World.AddChild(ed);
            var cam = new Camera3D();
            World.AddChild(cam);
            var objs = new EditorObjects(ed, World, null);
            World.AddChild(objs);
            var field = new RoadField();
            World.AddChild(field);
            field.DebugSetMaterialWidth(RoadField.TracksMaterial, 4.0f);
            yield return Ticks(2);

            // ---- 0. THE ASSET ARRIVED UNMODIFIED. astraclaw's contract is a set of numbers in the mesh, and
            // if a converter had been run over it they would all be wrong at once.
            var unit = ContentProvider.ParseObj("res://content/objects/New_Rail_Unit.obj");
            T.Check("the unit mesh loads", unit != null && unit.GetSurfaceCount() > 0);
            if (unit == null) yield break;
            var box = unit.GetAabb();
            T.Check($"...+Z along the track, 2.5 m bbox over a {EditorRailSpline.Pitch:0.##} m pitch "
                  + $"(Z {box.Position.Z:0.##}..{box.End.Z:0.##})",
                    Mathf.Abs(box.Position.Z - -0.5f) < 0.02f && Mathf.Abs(box.End.Z - 2.0f) < 0.02f);
            // ⭐ THE DATUM IS THE CHECK THAT CATCHES A RECENTRE. Ballast is modelled BELOW the centreline, so
            // the root is NOT at the bottom of the mesh -- floor-aligning it (what every other prop here
            // wants) would lift the whole track 0.44 m into the air and nothing would look obviously wrong.
            T.Check($"...root on the centreline datum, ballast below it (Y {box.Position.Y:0.##}..{box.End.Y:0.##}, "
                  + "rail top should be +0.31, buried ballast -0.44)",
                    box.Position.Y < -0.4f && Mathf.Abs(box.End.Y - 0.31f) < 0.02f);

            // ---- 1. A TRACKS SPLINE, and the rail tiled along it.
            var pts = new List<Vector3>();
            for (int k = 0; k <= 10; k++) pts.Add(new Vector3(k * 10f, 0f, 0f));
            int road = field.AddRoadFromPolyline(pts, RoadField.TracksMaterial);
            T.Check($"fixture: a TRACKS road exists ({field.RoadLength(road):0.#} m, material "
                  + $"{field.RoadMaterialOf(road)})",
                    road >= 0 && field.RoadMaterialOf(road) == RoadField.TracksMaterial);

            int before = objs.PlacedCount;
            var placed = new List<Node3D>();
            int n = EditorRailSpline.LayAlong(objs, null, field, road, placed);
            int want = Mathf.FloorToInt(field.RoadLength(road) / EditorRailSpline.Pitch);
            T.Check($"laid {n} unit(s) at a {EditorRailSpline.Pitch:0.##} m pitch over "
                  + $"{field.RoadLength(road):0.#} m (expected {want})", n == want);
            T.Check($"...plus exactly ONE terminal sleeper ({objs.PlacedCount - before} props for {n} units)",
                    objs.PlacedCount - before == n + 1);

            // ⭐ NO DOUBLED INTERIOR SLEEPERS. Each unit carries its sleeper at the START, so N units give N
            // and the terminal closes the run. A sleeper at both ends of every unit is 2N-1 -- the same
            // doubling the fence had, and the reason astraclaw shipped a separate terminal piece at all.
            int sleepers = 0, units = 0;
            foreach (var _ in objs.PlacedOf(EditorRailSpline.Sleeper)) sleepers++;
            foreach (var _ in objs.PlacedOf(EditorRailSpline.Unit)) units++;
            T.Check($"...one sleeper per unit plus the terminal, none doubled ({units} units, {sleepers} terminal)",
                    units == n && sleepers == 1);

            // ---- 2. THE PITCH IS 2.000, NOT THE 2.5 m BBOX. Stepping by the bbox leaves a half-metre hole in
            // the rail at every joint, which is the mistake the fence made with 16.25 against 16.0.
            var xs = new List<float>();
            foreach (var x in objs.PlacedOf(EditorRailSpline.Unit)) xs.Add(x.Origin.X);
            xs.Sort();
            float worst = 0f;
            for (int i = 1; i < xs.Count; i++) worst = Mathf.Max(worst, Mathf.Abs((xs[i] - xs[i - 1]) - EditorRailSpline.Pitch));
            T.Check($"units sit exactly {EditorRailSpline.Pitch:0.##} m apart (worst error {worst:0.####} m over "
                  + $"{xs.Count} units)", xs.Count > 1 && worst < 0.01f);

            // ---- 3. ⭐⭐ THE TRAINS CAN STILL FIND IT. This is the one that would have been silent: Train.cs
            // and the console both locate track through NearestTrack, which skips any road whose material is
            // not TracksMaterial. Had "New Rail" been given a material of its own, every train would have
            // failed to find the new track with no error anywhere.
            bool found = field.NearestTrack(new Vector3(50f, 0f, 0f), out int tr, out float along);
            T.Check($"a train can still find this track (NearestTrack -> road {tr} at {along:0.#} m)",
                    found && tr == road);

            // ⭐ CONTROL: the same spline on an ordinary road material must NOT be findable as track --
            // otherwise the check above passes on a NearestTrack that matches everything.
            var rp = new List<Vector3>();
            for (int k = 0; k <= 10; k++) rp.Add(new Vector3(k * 10f, 0f, 500f));
            int plain = field.AddRoadFromPolyline(rp);   // material 0
            bool foundPlain = field.NearestTrack(new Vector3(50f, 0f, 500f), out int pr, out _);
            T.Check($"control: a plain road is NOT track (material {field.RoadMaterialOf(plain)}, "
                  + $"NearestTrack -> {(foundPlain ? pr.ToString() : "none")})",
                    !foundPlain || pr != plain);

            // ---- 4. THE BEND LIMIT IS DERIVED FROM THE TILE'S WIDTH, not guessed. A 6.9 m-wide rigid tile
            // chording a curve parts at its outer corners by about HalfWidth * Pitch / R.
            T.Check($"the bend limit follows from the tile ({EditorRailSpline.MinRadius:0} m for a "
                  + $"{EditorRailSpline.HalfWidth * 2f:0.#} m tile at {EditorRailSpline.MaxJointGap * 100f:0} cm)",
                    Mathf.Abs(EditorRailSpline.MinRadius
                              - EditorRailSpline.HalfWidth * EditorRailSpline.Pitch / EditorRailSpline.MaxJointGap) < 0.5f);
        }
    }
}
