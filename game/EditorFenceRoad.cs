using System.Collections.Generic;
using Godot;

namespace UnturnedGodot
{
    /// <summary>The map editor's fence-road tool: click a point, click the next, and a run of guardrail is laid
    /// between them -- seated on the terrain, turned to follow the line, with the rail facing the road.
    ///
    /// Master 2026-10-09: "make a new fence road tool. split the prop of fence road and fence road broken into
    /// the posts (brown wood) and the guardrail (metal, silver) first".
    ///
    /// ⭐ THE SPLIT IS WHAT MAKES IT A TOOL RATHER THAN A PROP BRUSH. Fence_Road_0 is one 16.25 m mesh, so
    /// hand-placing a roadside run means repeating a prop and eyeballing the joins -- which the object palette
    /// already does badly. Now that the posts and the rail are separate props, a run is a LINE with two things
    /// strung along it, and the two can disagree: a stretch of posts whose rail has been torn off is a shape
    /// you can author rather than a mesh somebody has to model. That is also why Delete removes the RAIL and
    /// leaves the posts standing -- the most common thing a mapper wants from a damaged roadside.
    ///
    /// ⭐ AND THE SELECTION CHAINS, like EditorPowerLines: after laying A to B the tool leaves B armed, so a
    /// kilometre of roadside is one click per corner instead of two. Same idiom, same muscle memory.</summary>
    public partial class EditorFenceRoad : Node3D
    {
        readonly Editor _editor;
        readonly Camera3D _cam;
        readonly EditorObjects _objects;
        readonly Terrain _terr;

        /// <summary>The pitch segments are laid at: 16.0 m, which is RETAIL'S, not the mesh's bbox.
        ///
        /// ⭐⭐ THE BBOX IS 16.25 AND THAT IS THE WRONG NUMBER. The prop measures 16.25 m along its local +Y
        /// (and its lods.txt row says so), but its five posts sit at -8, -4, 0, +4, +8 -- a 4.00 m rhythm
        /// spanning 16.0, with 0.125 m of empty bbox hanging off each end. Stepping by the bbox leaves a 0.25 m
        /// hole in the rail at every join and breaks the post rhythm, which is exactly the "gaps" the first
        /// showcase render showed.
        ///
        /// ⚠ AND IT WAS SETTLED BY MEASURING THE GAME, not by reasoning about the mesh: the 22 Fence_Road_0
        /// placements in PEI's placements.txt sit a median 16.047 m apart (min 15.948, commonest bucket 16.0).
        /// So retail butts them post-on-post, the end posts of neighbouring segments coinciding, and the run
        /// reads as one barrier with an unbroken 4 m rhythm. Master: "are the posts fixed distance or does it
        /// stretch and try place them?" -- fixed, at the spacing the game itself uses.</summary>
        public const float SegmentLength = 16.0f;

        /// <summary>The baked-in post rhythm, measured off the mesh: 5 posts per segment, 4.00 m apart. Nothing
        /// stretches -- the prop is rigid, so a run is a whole number of it and the remainder is left bare.</summary>
        public const float PostSpacing = 4.0f;

        /// <summary>How far a run may turn at one joint before the rigid units visibly cut the corner.
        ///
        /// ⭐ A TURN LIMIT, NOT A RADIUS, because the limit depends on how long the unit is. ProcIslandSpawn
        /// refuses roadside fences under a 55 m radius, and it places the WHOLE 16.25 m prop -- that is a turn
        /// of 16.25 / 55 = 0.295 rad = 17 deg per joint. Expressed that way the same rule covers the 4 m unit
        /// an intact run is now built from, where it works out at a 13.6 m radius. Keeping the 55 m number
        /// would have the tool crying wolf on every curve a 4 m unit follows perfectly well.</summary>
        public const float MaxTurnPerJointDeg = 17f;

        /// <summary>The tightest radius a given unit length can follow within MaxTurnPerJointDeg.</summary>
        public static float MinBendRadiusFor(float step) => step / Mathf.DegToRad(MaxTurnPerJointDeg);

        public const string Intact = "Fence_Road_0";
        public const string Broken = "Fence_Road_Broken_0";
        const string PostsSuffix = "_Posts";
        const string RailSuffix = "_Rail";

        /// <summary>The tiling units: ONE post, and ONE 4 m span of rail between two posts.
        ///
        /// ⭐⭐ THE RUN IS BUILT FROM THESE, NOT FROM THE WHOLE PROP. Master 2026-10-09: "the segments should
        /// be like.. the post-post width of guardrail, blending more seamlessly (and no dupe posts)". Tiling
        /// the 16 m prop puts its end post exactly where the next one's start post goes -- two posts in the
        /// same place, z-fighting and reading double -- and makes every joint a 16 m chord, which is what
        /// forces the turn-per-joint limit in the first place. A 4 m unit has one post per boundary by
        /// construction and chords the curve four times as finely.
        ///
        /// ⚠ Sliced, not re-authored: the rail carries vertex loops at exactly its post positions
        /// (−8/−4/0/+4/+8) and NO triangle crosses one, so the span is a SELECTION of whole triangles out of
        /// the shipped mesh. Nothing was cut, approximated or redrawn.</summary>
        public const string PostUnit = "Fence_Road_0_Post";
        public const string SpanUnit = "Fence_Road_0_Span";

        const uint TerrainLayer = 1u << 0;
        static readonly Color LineColor = new(0.45f, 0.95f, 0.55f);

        bool _on;
        bool _broken;        // B: lay the wrecked variant instead
        bool _flip;          // R: which side of the line the rail faces
        readonly List<Vector3> _path = new();   // the clicked run, laid on Enter; 2 points = straight, 3+ = a curve
        MeshInstance3D _preview;

        /// <summary>Every run laid this session, newest last, so Delete can strip the rail off the last one.</summary>
        readonly List<(List<Node3D> posts, List<Node3D> rail)> _runs = new();

        public bool Active => _on;

        public EditorFenceRoad(Editor editor, Camera3D cam, EditorObjects objects, Terrain terr)
        {
            _editor = editor; _cam = cam; _objects = objects; _terr = terr;
        }

        public string ModeText => _on
            ? (_path.Count == 0
                ? $"FENCE ROAD · {(_broken ? "broken" : "intact")} · LMB to start a run · B = variant · R = side · Del = strip the last rail · Shift+F = off"
                : $"FENCE ROAD · {_path.Count} point(s), 3+ curves · ENTER lays it · Backspace drops a point · R = rail faces {(_flip ? "left" : "right")} · Esc")
            : "Shift+F = fence road";

        public void SetActive(bool on) { if (_on != on) Toggle(); }

        void Toggle()
        {
            _on = !_on;
            if (_on) Log.Print($"[editor-fence] ON: {SegmentLength:0.##} m segments, {_runs.Count} run(s) laid");
            else { _path.Clear(); ClearPreview(); }
        }

        // ---- input ------------------------------------------------------------------------------------------

        public override void _UnhandledInput(InputEvent ev)
        {
            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F } && Input.IsKeyPressed(Key.Shift))
            { Toggle(); GetViewport().SetInputAsHandled(); return; }
            if (!_on) return;

            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Z } && Input.IsKeyPressed(Key.Ctrl)) { _editor.Undo(); return; }
            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }) { _path.Clear(); ClearPreview(); return; }
            // Backspace drops the last click. A curve is defined by EVERY point on it, so "that one was wrong"
            // would otherwise mean Esc and re-clicking the whole run.
            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Backspace } && _path.Count > 0)
            { _path.RemoveAt(_path.Count - 1); return; }
            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Enter or Key.KpEnter } && _path.Count >= 2)
            { LayPathRun(_path); _path.Clear(); ClearPreview(); return; }
            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.B }) { _broken = !_broken; return; }
            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.R } && !Input.IsKeyPressed(Key.Shift)) { _flip = !_flip; return; }

            // ⭐ DELETE STRIPS THE RAIL AND LEAVES THE POSTS. Deleting the whole run is what Ctrl+Z is for; this
            // is the thing the split exists to make possible, and a key that only duplicated undo would be waste.
            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Delete } && _runs.Count > 0)
            {
                var last = _runs[^1];
                if (last.rail.Count == 0) { Log.Print("[editor-fence] the last run has already lost its rail"); return; }
                var stripped = new List<Node3D>(last.rail);
                _objects.RemovePlaced(stripped);
                last.rail.Clear();
                _editor.MarkDirty();
                Log.Print($"[editor-fence] stripped {stripped.Count} rail section(s); the posts stay up");
                return;
            }

            if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } && !Editor.PointerOverUI(this))
            {
                if (RaycastTerrain(GetViewport().GetMousePosition(), out var hit)) _path.Add(hit);
            }
        }

        public override void _Process(double delta)
        {
            if (!_on || _path.Count == 0) { ClearPreview(); return; }
            // The rubber band, as EditorPowerLines does it -- and for a curve it must draw the CURVE, not the
            // clicks. A Catmull-Rom through three points does not run where the straight lines between them do,
            // so a polyline preview would promise a shape the tool is not about to lay.
            var pts = new List<Vector3>(_path);
            if (RaycastTerrain(GetViewport().GetMousePosition(), out var cursor)) pts.Add(cursor);
            if (pts.Count < 2) { ClearPreview(); return; }
            if (_preview == null)
            {
                _preview = new MeshInstance3D
                {
                    MaterialOverride = new StandardMaterial3D { AlbedoColor = LineColor, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded },
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                };
                AddChild(_preview);
            }
            var curve = BuildCurve(pts);
            float len = curve.GetBakedLength();
            int steps = Mathf.Clamp(Mathf.CeilToInt(len / 2f), 2, 400);
            var im = new ImmediateMesh();
            im.SurfaceBegin(Mesh.PrimitiveType.LineStrip);
            for (int i = 0; i <= steps; i++)
                im.SurfaceAddVertex(curve.SampleBaked(len * i / steps, true) + Vector3.Up * 0.5f);
            im.SurfaceEnd();
            _preview.Mesh = im;
        }

        void ClearPreview() { if (_preview != null) { _preview.QueueFree(); _preview = null; } }

        // ---- laying -----------------------------------------------------------------------------------------

        public int LayRun(Vector3 a, Vector3 b) => LayPathRun(new List<Vector3> { a, b });

        /// <summary>Lay the clicked path -- straight through two points, a curve through three or more -- with
        /// ONE undo entry for the whole run.</summary>
        public int LayPathRun(IReadOnlyList<Vector3> pts)
        {
            var posts = new List<Node3D>();
            var rail = new List<Node3D>();
            int n = LayPath(_objects, _terr, pts, _broken, _flip, posts, rail);
            if (n == 0) return 0;

            var all = new List<Node3D>(posts); all.AddRange(rail);
            var run = (posts, rail);
            _runs.Add(run);
            _editor.PushUndo("lay fence road", () => { _objects.RemovePlaced(all); _runs.Remove(run); });
            _editor.MarkDirty();
            return n;
        }

        /// <summary>The placement itself, with no editor state. STATIC and parameterised because MapShowcase
        /// lays a demo run too, and the one thing a showcase of a tool must not do is re-implement the tool.
        ///
        /// A straight run is just a two-point path, so there is ONE placement routine rather than a straight
        /// one and a curved one drifting apart.</summary>
        public static int LaySegments(EditorObjects objects, Terrain terr, Vector3 a, Vector3 b,
                                      bool broken, bool flip, List<Node3D> posts, List<Node3D> rail)
            => LayPath(objects, terr, new List<Vector3> { a, b }, broken, flip, posts, rail);

        /// <summary>Lay whole segments along a path of clicked points -- straight through two, a smooth curve
        /// through three or more. Returns how many went down.
        ///
        /// ⭐ WALKED BY ARC LENGTH, which is the only thing that keeps the post rhythm even round a bend.
        /// Placing one segment per spline SEGMENT would stretch the spacing on the long parts of the curve and
        /// crowd it on the short ones; stepping a fixed 16 m along the BAKED length puts every post where the
        /// previous one's 4 m rhythm says it should be, whatever the curve is doing.
        ///
        /// ⚠ EACH SEGMENT IS A CHORD, NOT AN ARC -- the prop is a rigid 16 m mesh and cannot bend. So its
        /// direction comes from the chord between its two ENDS on the curve rather than the tangent at its
        /// middle: on a bend those differ, and the tangent version leaves each segment's ends lifted off the
        /// line it is supposed to be following. A bend tighter than MinBendRadiusFor(step) is reported, because at that
        /// point the chords visibly cut the corner and the mapper should know rather than wonder.</summary>
        public static int LayPath(EditorObjects objects, Terrain terr, IReadOnlyList<Vector3> pts,
                                  bool broken, bool flip, List<Node3D> posts, List<Node3D> rail)
        {
            if (objects == null || pts == null || pts.Count < 2) return 0;
            var curve = BuildCurve(pts);
            float total = curve.GetBakedLength();

            // ⚠ THE WRECK IS NOT A TILING UNIT. Fence_Road_Broken_0's posts lean and its rail is twisted --
            // measured, its vertex Y values are irregular where the intact one's land exactly on the 4 m post
            // rhythm. Repeating a wreck every 4 m would produce the same wreck over and over; it is a one-off
            // section, so it is still laid whole, at the retail 16 m pitch.
            float step = broken ? SegmentLength : PostSpacing;
            int n = Mathf.FloorToInt(total / step);
            if (n <= 0)
            {
                Log.Print($"[editor-fence] {total:0.#} m is under one {step:0.##} m unit -- nothing laid");
                return 0;
            }
            float tightest = TightestBendRadius(pts, step);
            float limit = MinBendRadiusFor(step);
            Vector3 lastCentre = Vector3.Zero; float minGap = float.MaxValue, maxGap = 0f;

            for (int i = 0; i < n; i++)
            {
                float s = i * step;
                var p0 = curve.SampleBaked(s, true);
                var p1 = curve.SampleBaked(s + step, true);
                var chord = new Vector3(p1.X - p0.X, 0f, p1.Z - p0.Z);
                if (chord.LengthSquared() < 1e-6f) continue;
                var dir = chord.Normalized();
                var centre = (p0 + p1) * 0.5f;
                if (i > 0) { float gap = new Vector3(centre.X - lastCentre.X, 0f, centre.Z - lastCentre.Z).Length();
                             minGap = Mathf.Min(minGap, gap); maxGap = Mathf.Max(maxGap, gap); }
                lastCentre = centre;
                if (terr != null) centre.Y = terr.SampleHeight(centre.X, centre.Z);
                // ⚠⚠ YawForDir TAKES PROC-FRAME COORDINATES, WHERE +Y IS WORLD −Z. Feeding it a world
                // direction mirrors the run about the X axis -- and a straight run along X has dz = 0, so it
                // looks PERFECT and only a curve bends into the error. Caught by the curved joint check
                // (17.7 m holes) after master asked "is that meant to be a curve?"; every straight-run test
                // passed throughout. Same negation as reference_unturned_coord_znegate.
                float yaw = ProcIsland.YawForDir(dir.X, -dir.Z) + (flip ? 180f : 0f);
                var basis = SeatedBasis(terr, centre, yaw);

                if (broken)
                {
                    var bp = objects.Place(Broken + PostsSuffix, centre, basis);
                    var br = objects.Place(Broken + RailSuffix, centre, basis);
                    if (bp != null) posts?.Add(bp);
                    if (br != null) rail?.Add(br);
                    continue;
                }

                // ⭐ ONE POST PER BOUNDARY, laid at the START of each span -- so N spans get N posts and the
                // run is closed with one more at the far end below. Putting a post at both ends of every span
                // is exactly the duplicate master saw.
                var postAt = p0;
                if (terr != null) postAt.Y = terr.SampleHeight(postAt.X, postAt.Z);
                var pn = objects.Place(PostUnit, postAt, SeatedBasis(terr, postAt, yaw));
                var sp = objects.Place(SpanUnit, centre, basis);
                if (pn != null) posts?.Add(pn);
                if (sp != null) rail?.Add(sp);
            }

            if (!broken)
            {
                // The closing post: a run of N spans has N+1 posts, and without this the last span ends in
                // mid-air.
                var end = curve.SampleBaked(n * step, true);
                if (terr != null) end.Y = terr.SampleHeight(end.X, end.Z);
                var prev = curve.SampleBaked(Mathf.Max(0f, n * step - step), true);
                var d = new Vector3(end.X - prev.X, 0f, end.Z - prev.Z);
                float endYaw = (d.LengthSquared() < 1e-6f ? 0f : ProcIsland.YawForDir(d.Normalized().X, -d.Normalized().Z))
                             + (flip ? 180f : 0f);
                var ep = objects.Place(PostUnit, end, SeatedBasis(terr, end, endYaw));
                if (ep != null) posts?.Add(ep);
            }

            Log.Print($"[editor-fence] {n}x {(broken ? Broken : SpanUnit)} over {n * step:0.#} m of a {total:0.#} m path "
                    + $"({pts.Count} point(s)), rail on the {(flip ? "left" : "right")}"
                    + (maxGap > 0f ? $", unit spacing {minGap:0.###}..{maxGap:0.###} m" : "")
                    + (float.IsPositiveInfinity(tightest) ? ", straight"
                       : tightest < limit
                         ? $"  ⚠ tightest bend ~{tightest:0.#} m, under the {limit:0.#} m a {step:0.##} m unit can follow"
                         : $", tightest bend ~{tightest:0} m"));
            return n;
        }

        /// <summary>The tightest bend radius the path asks a segment to follow, in metres, or +inf if it is
        /// straight. Measured the way the SEGMENTS experience it -- the turn between consecutive 16 m chords,
        /// radius = chord / angle -- rather than from the spline's curvature, because a rigid prop only ever
        /// samples the curve at its two ends.
        ///
        /// ⭐ PUBLIC SO IT CAN BE ASSERTED ON. A limit that only ever appears in a log line is a limit nothing
        /// checks, and the turn limit came from ProcIslandSpawn, where it is enforced; here it is a warning, so
        /// the test has to be able to ask the question directly.</summary>
        public static float TightestBendRadius(IReadOnlyList<Vector3> pts, float step = PostSpacing)
        {
            if (pts == null || pts.Count < 3) return float.PositiveInfinity;
            var curve = BuildCurve(pts);
            float total = curve.GetBakedLength();
            float tightest = float.PositiveInfinity;
            for (float s = step; s + step <= total; s += step)
            {
                var a = curve.SampleBaked(s - step, true);
                var b = curve.SampleBaked(s, true);
                var c = curve.SampleBaked(s + step, true);
                var c0 = new Vector3(b.X - a.X, 0f, b.Z - a.Z);
                var c1 = new Vector3(c.X - b.X, 0f, c.Z - b.Z);
                if (c0.LengthSquared() < 1e-6f || c1.LengthSquared() < 1e-6f) continue;
                float turn = Mathf.Abs(c0.Normalized().AngleTo(c1.Normalized()));
                if (turn > 1e-4f) tightest = Mathf.Min(tightest, step / turn);
            }
            return tightest;
        }

        /// <summary>A Curve3D through the clicked points. Two points stay a straight line; three or more get
        /// Catmull-Rom handles so the run bends smoothly THROUGH every click rather than being pulled off them.
        ///
        /// ⚠ Godot's Curve3D is straight between points until you give it handles -- "add the points and bake"
        /// silently produces a polyline, which looks like the curve maths is wrong when nothing was computed at
        /// all. The handle is the Catmull-Rom tangent, (next - prev) / 6.</summary>
        static Curve3D BuildCurve(IReadOnlyList<Vector3> pts)
        {
            var c = new Curve3D();
            for (int i = 0; i < pts.Count; i++)
            {
                Vector3 inH = Vector3.Zero, outH = Vector3.Zero;
                if (pts.Count > 2)
                {
                    var prev = pts[Mathf.Max(i - 1, 0)];
                    var next = pts[Mathf.Min(i + 1, pts.Count - 1)];
                    var t = (next - prev) / 6f;
                    inH = -t; outH = t;
                }
                c.AddPoint(pts[i], inH, outH);
            }
            return c;
        }

        /// <summary>Stand the prop up, turn it along the run, and lie it on the slope.
        ///
        /// ⚠ ex=270 IS THE MESH CONVENTION, NOT A TILT: the prop OBJs are Z-up, and 3602 of PEI's ~3900
        /// placements carry it. Leaving it out lays every fence flat on the ground, which on a long thin prop
        /// still looks like a fence from above -- so it is the kind of mistake a screenshot does not catch.</summary>
        static Basis SeatedBasis(Terrain terr, Vector3 at, float yawDeg)
        {
            var stand = EditorObjects.FromEuler(270f, yawDeg, 0f);
            if (terr == null) return stand;
            var nrm = terr.NormalAt(at.X, at.Z);
            var axis = Vector3.Up.Cross(nrm);
            return axis.LengthSquared() < 1e-8f ? stand : new Basis(axis.Normalized(), Vector3.Up.AngleTo(nrm)) * stand;
        }

        bool RaycastTerrain(Vector2 screen, out Vector3 point)
        {
            point = Vector3.Zero;
            var from = _cam.ProjectRayOrigin(screen);
            var to = from + _cam.ProjectRayNormal(screen) * 4000f;
            var q = PhysicsRayQueryParameters3D.Create(from, to, TerrainLayer);
            var hit = GetWorld3D().DirectSpaceState.IntersectRay(q);
            if (hit.Count == 0) return false;
            point = (Vector3)hit["position"];
            return true;
        }
    }
}
