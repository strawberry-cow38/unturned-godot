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
        readonly RoadField _roads;

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
        /// <summary>Points along the path where a WRECKED section should sit, dropped with B while building
        /// the run. Stored as positions rather than unit indices because the unit count is not known until
        /// Enter -- adding a path point afterwards would silently move every marker.</summary>
        readonly List<Vector3> _wreckAt = new();
        MeshInstance3D _preview;

        /// <summary>Every run laid this session, newest last, so Delete can strip the rail off the last one.</summary>
        readonly List<(List<Node3D> posts, List<Node3D> rail)> _runs = new();

        public bool Active => _on;

        public EditorFenceRoad(Editor editor, Camera3D cam, EditorObjects objects, Terrain terr, RoadField roads = null)
        {
            _editor = editor; _cam = cam; _objects = objects; _terr = terr; _roads = roads;
        }

        public string ModeText => _on
            ? (_path.Count == 0
                ? $"FENCE ROAD · {(_broken ? "all broken" : "intact")} · LMB to start a run · R = side · G = guard a road's bends · Del = strip the last rail · Shift+F = off"
                : $"FENCE ROAD · {_path.Count} point(s), 3+ curves · ENTER lays it · B = wreck here ({_wreckAt.Count}) · Backspace drops a point · R = rail faces {(_flip ? "left" : "right")} · Esc")
            : "Shift+F = fence road";

        public void SetActive(bool on) { if (_on != on) Toggle(); }

        void Toggle()
        {
            _on = !_on;
            if (_on) Log.Print($"[editor-fence] ON: {SegmentLength:0.##} m segments, {_runs.Count} run(s) laid");
            else { _path.Clear(); _wreckAt.Clear(); ClearPreview(); }
        }

        // ---- input ------------------------------------------------------------------------------------------

        public override void _UnhandledInput(InputEvent ev)
        {
            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F } && Input.IsKeyPressed(Key.Shift))
            { Toggle(); GetViewport().SetInputAsHandled(); return; }
            if (!_on) return;

            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Z } && Input.IsKeyPressed(Key.Ctrl)) { _editor.Undo(); return; }
            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }) { _path.Clear(); _wreckAt.Clear(); ClearPreview(); return; }
            // Backspace drops the last click. A curve is defined by EVERY point on it, so "that one was wrong"
            // would otherwise mean Esc and re-clicking the whole run.
            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Backspace } && _path.Count > 0)
            { _path.RemoveAt(_path.Count - 1); return; }
            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Enter or Key.KpEnter } && _path.Count >= 2)
            { LayPathRun(_path); _path.Clear(); _wreckAt.Clear(); ClearPreview(); return; }
            // ⭐ B MARKS A WRECK WHERE THE CURSOR IS, rather than switching the whole run to the broken
            // variant. A fully wrecked 100 m guardrail is not a thing anyone maps; a mostly-intact run with a
            // smashed section in it is, and that is what master's "how do broken pieces integrate?" is about.
            // Shift+B still lays a wholly broken run, for the rare case.
            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.B })
            {
                if (Input.IsKeyPressed(Key.Shift)) { _broken = !_broken; return; }
                if (RaycastTerrain(GetViewport().GetMousePosition(), out var wreck)) _wreckAt.Add(wreck);
                return;
            }
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

            // ⭐ G GUARDS THE ROAD UNDER THE CURSOR -- the "parent it to a road spline" path. A separate key
            // rather than a click on the road, because LMB is already how you place a free path point and a
            // click that means two different things depending on what is underneath is a click you cannot aim.
            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.G } && _roads != null)
            {
                if (!RaycastTerrain(GetViewport().GetMousePosition(), out var at)) return;
                if (!_roads.NearestPointOnSpline(at, 60f, out int rd, out _, out _, out _))
                { Log.Print("[editor-fence] no road within 60 m of the cursor"); return; }
                var gp = new List<Node3D>(); var gr = new List<Node3D>();
                int got = LayAlongRoad(_objects, _terr, _roads, rd, _flip, gp, gr);
                if (got == 0) return;
                var all = new List<Node3D>(gp); all.AddRange(gr);
                var run = (gp, gr);
                _runs.Add(run);
                _editor.PushUndo("guard road bends", () => { _objects.RemovePlaced(all); _runs.Remove(run); });
                _editor.MarkDirty();
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
            int n = LayPath(_objects, _terr, pts, _broken, _flip, posts, rail, WreckUnits(pts));
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
        /// <summary>How far outside the asphalt's edge a guardrail sits. ProcIslandSpawn puts its roadside
        /// fences 11 m from the centreline against a 9.2 m rendered half-width -- 1.8 m of clearance -- and
        /// that is the number reused here, against each road's OWN half-width rather than a constant, so it
        /// stays right on a road material of any width.</summary>
        public const float RoadClearance = 1.8f;

        /// <summary>A bend tighter than this is worth guarding; anything straighter is not. A guardrail down a
        /// straight is not what the thing is for -- master: "the outer edge on a turn".</summary>
        public const float GuardBendRadius = 160f;

        /// <summary>The shortest stretch worth laying, in units. Below this a bend produces a 4 m stub that
        /// reads as litter rather than as a barrier.</summary>
        public const int MinGuardUnits = 3;

        /// <summary>Lay guardrail along the OUTER edge of every bend in a road. Returns the units laid.
        ///
        /// Master 2026-10-09: "next is parenting this to a road spline's outer edge on a turn".
        ///
        /// ⭐ THE OUTER SIDE IS DERIVED, NOT ASKED FOR, and it is the whole point: a barrier belongs on the
        /// side you would leave the road on. Walking the spline by arc length gives a signed turn per step
        /// (the Y of the cross product of consecutive tangents); turning LEFT puts the outer edge on the
        /// RIGHT, and vice versa. So the side follows the road rather than a flag somebody has to set per bend.
        ///
        /// ⚠ AND IT IS PER BEND, NOT PER ROAD. An S-bend's outer edge swaps sides at the inflection, and one
        /// continuous run cannot be on both -- so the road is cut into stretches of constant turn direction and
        /// each gets its own run. Laying one run for the whole road would put half of it on the inside of the
        /// curve, which is exactly the wrong side.</summary>
        public static int LayAlongRoad(EditorObjects objects, Terrain terr, RoadField roads, int road,
                                       bool flip, List<Node3D> posts, List<Node3D> rail)
        {
            if (objects == null || roads == null) return 0;
            float total = roads.RoadLength(road);
            float half = roads.RoadHalfWidth(road);
            if (total < PostSpacing * MinGuardUnits || half <= 0f) return 0;
            float off = half + RoadClearance;

            int n = Mathf.FloorToInt(total / PostSpacing);
            var pos = new Vector3[n + 1];
            var side = new Vector3[n + 1];      // unit vector pointing to the spline's RIGHT
            for (int i = 0; i <= n; i++)
            {
                if (!roads.EvaluateAlong(road, i * PostSpacing, out var p, out var t)) return 0;
                pos[i] = p;
                var flat = new Vector3(t.X, 0f, t.Z);
                side[i] = flat.LengthSquared() < 1e-8f ? Vector3.Right : flat.Normalized().Cross(Vector3.Up).Normalized();
            }

            // Signed turn at each interior sample: + is a LEFT turn, so the outer edge is to the RIGHT.
            var turn = new float[n + 1];
            for (int i = 1; i < n; i++)
            {
                var a = (pos[i] - pos[i - 1]); a.Y = 0f;
                var b = (pos[i + 1] - pos[i]); b.Y = 0f;
                if (a.LengthSquared() < 1e-8f || b.LengthSquared() < 1e-8f) continue;
                a = a.Normalized(); b = b.Normalized();
                turn[i] = a.Cross(b).Y;                       // radians, small-angle
            }

            float maxTurn = PostSpacing / GuardBendRadius;     // turn per step at the threshold radius
            int laid = 0, stretches = 0;
            int i0 = -1; float sign = 0f;

            void Flush(int i1)
            {
                if (i0 >= 0 && i1 - i0 >= MinGuardUnits)
                {
                    // OUTER = right of the spline on a left turn, left on a right turn.
                    float s = (sign > 0f ? 1f : -1f) * (flip ? -1f : 1f);
                    var pts = new List<Vector3>();
                    for (int k = i0; k <= i1; k++) pts.Add(pos[k] + side[k] * (off * s));
                    if (pts.Count >= 2)
                    {
                        // ⚠⚠ THE BEAM FACES THE ROAD, so the facing is the OPPOSITE of the side the run
                        // sits on. The rail is the prop's local +X half, and YawForDir puts +X on the run's
                        // RIGHT -- so a run offset to the right (s > 0) has the carriageway on its LEFT and
                        // needs the 180. I had this inverted and every beam pointed into the field with the
                        // posts presented to the traffic; master spotted it in a render, and the offset test
                        // could not -- the prop is 0.5 m wide, so both facings put every post ~11 m outside
                        // the curve and satisfy it equally.
                        int got = LayPath(objects, terr, pts, false, s > 0f, posts, rail, null, smooth: false);
                        if (got > 0) { laid += got; stretches++; }
                    }
                }
                i0 = -1; sign = 0f;
            }

            for (int i = 1; i < n; i++)
            {
                bool bend = Mathf.Abs(turn[i]) >= maxTurn;
                float sg = Mathf.Sign(turn[i]);
                if (bend && (i0 < 0 || Mathf.IsEqualApprox(sg, sign)))
                {
                    if (i0 < 0) { i0 = i - 1; sign = sg; }     // start one step back, so the run leads into the bend
                }
                else { Flush(Mathf.Min(i, n)); if (bend) { i0 = i - 1; sign = sg; } }
            }
            Flush(n);

            Log.Print($"[editor-fence] road {road}: {total:0.#} m, half-width {half:0.##} m -> guardrail "
                    + $"{off:0.##} m off the centreline; {stretches} bend(s) guarded, {laid} unit(s)"
                    + (stretches == 0 ? $" (nothing turns tighter than {GuardBendRadius:0} m)" : "")
                    + $"; steepest ground met {DebugRawTiltMax:0.#} deg (clamped at {MaxSeatTiltDeg:0})");
            return laid;
        }

        /// <summary>Turn the dropped wreck markers into unit indices on THIS path. A marker is snapped to the
        /// nearest unit boundary, and one that would overhang the end is dropped by LayPath.
        ///
        /// ⚠ RESOLVED AT LAY TIME, not when the marker is dropped: the path can still gain points afterwards,
        /// and a curve through one more click is a different curve -- an index worked out early would point
        /// somewhere else by the time the run went down.</summary>
        List<int> WreckUnits(IReadOnlyList<Vector3> pts)
        {
            if (_wreckAt.Count == 0 || pts.Count < 2) return null;
            var curve = BuildCurve(pts);
            float total = curve.GetBakedLength();
            int steps = Mathf.Max(2, Mathf.CeilToInt(total));
            var outp = new List<int>();
            foreach (var w in _wreckAt)
            {
                float bestS = 0f, bestD = float.MaxValue;
                for (int i = 0; i <= steps; i++)
                {
                    float s = total * i / steps;
                    var p = curve.SampleBaked(s, true);
                    float d = new Vector3(p.X - w.X, 0f, p.Z - w.Z).LengthSquared();
                    if (d < bestD) { bestD = d; bestS = s; }
                }
                // centre the wreck on the marker, then snap to a unit boundary
                int k = Mathf.RoundToInt((bestS - PostSpacing * BrokenUnits * 0.5f) / PostSpacing);
                if (!outp.Contains(k)) outp.Add(k);
            }
            return outp;
        }

        /// <summary>How many 4 m units a wrecked section spans. Measured: Fence_Road_Broken_0's own posts sit
        /// at −8.00, −4.10, 0.00, +4.18, +8.00 -- the SAME 4 m rhythm as the intact prop, with the two middle
        /// ones knocked askew, and its end posts at exactly ±8.00. So a wreck is four units of run, and it
        /// drops into an otherwise intact fence with its end posts landing exactly where the run's would.</summary>
        public const int BrokenUnits = 4;

        public static int LayPath(EditorObjects objects, Terrain terr, IReadOnlyList<Vector3> pts,
                                  bool broken, bool flip, List<Node3D> posts, List<Node3D> rail)
            => LayPath(objects, terr, pts, broken, flip, posts, rail, null);

        /// <param name="brokenAt">Unit indices where a WRECKED section starts, each covering BrokenUnits.</param>
        public static int LayPath(EditorObjects objects, Terrain terr, IReadOnlyList<Vector3> pts,
                                  bool broken, bool flip, List<Node3D> posts, List<Node3D> rail,
                                  IReadOnlyCollection<int> brokenAt, bool smooth = true)
        {
            if (objects == null || pts == null || pts.Count < 2) return 0;
            var curve = BuildCurve(pts, smooth);
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
            int wrecks = 0;
            HashSet<int> startsWreck = null, coveredByWreck = null, postSupplied = null;
            if (!broken && brokenAt != null && brokenAt.Count > 0)
            {
                startsWreck = new HashSet<int>(); coveredByWreck = new HashSet<int>(); postSupplied = new HashSet<int>();
                foreach (var k in brokenAt)
                {
                    if (k < 0 || k + BrokenUnits > n) continue;   // a wreck that would hang off the end is dropped
                    startsWreck.Add(k);
                    for (int u = k + 1; u < k + BrokenUnits; u++) coveredByWreck.Add(u);
                    // ⚠ FIVE POSTS, NOT FOUR. A wreck spanning units k..k+3 carries posts at BOTH its ends --
                    // boundaries k and k+4 -- so the unit that RESUMES the intact run at k+4 must place its
                    // span but not its post. Getting this off by one puts a second post on the wreck's far
                    // end, which is the same doubling as before, just hidden inside a damaged section.
                    for (int u = k; u <= k + BrokenUnits; u++) postSupplied.Add(u);
                }
            }
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

                // ⭐⭐ A WRECK REPLACES FOUR UNITS AND BRINGS ITS OWN POSTS -- all five of them, including the
                // two shared with the neighbouring intact spans. So the run places nothing at all across the
                // stretch; laying its own posts here as well is precisely the doubling this whole rework was
                // about, just hidden inside a damaged section where it is harder to spot.
                if (coveredByWreck != null && coveredByWreck.Contains(i)) continue;
                if (startsWreck != null && startsWreck.Contains(i))
                {
                    var w0 = curve.SampleBaked(s, true);
                    var w1 = curve.SampleBaked(Mathf.Min(total, s + step * BrokenUnits), true);
                    var wc = (w0 + w1) * 0.5f;
                    if (terr != null) wc.Y = terr.SampleHeight(wc.X, wc.Z);
                    var wd = new Vector3(w1.X - w0.X, 0f, w1.Z - w0.Z);
                    float wyaw = (wd.LengthSquared() < 1e-6f ? yaw
                                  : ProcIsland.YawForDir(wd.Normalized().X, -wd.Normalized().Z) + (flip ? 180f : 0f));
                    var wb = SeatedBasis(terr, wc, wyaw);
                    var wp = objects.Place(Broken + PostsSuffix, wc, wb);
                    var wr = objects.Place(Broken + RailSuffix, wc, wb);
                    if (wp != null) posts?.Add(wp);
                    if (wr != null) rail?.Add(wr);
                    wrecks++;
                    continue;
                }

                // ⭐ ONE POST PER BOUNDARY, laid at the START of each span -- so N spans get N posts and the
                // run is closed with one more at the far end below. Putting a post at both ends of every span
                // is exactly the duplicate master saw.
                if (postSupplied == null || !postSupplied.Contains(i))
                {
                    var postAt = p0;
                    if (terr != null) postAt.Y = terr.SampleHeight(postAt.X, postAt.Z);
                    var pn = objects.Place(PostUnit, postAt, SeatedBasis(terr, postAt, yaw));
                    if (pn != null) posts?.Add(pn);
                }
                var sp = objects.Place(SpanUnit, centre, basis);
                if (sp != null) rail?.Add(sp);
            }

            if (!broken && !(postSupplied != null && postSupplied.Contains(n)))
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
                    + (wrecks > 0 ? $", {wrecks} wrecked section(s)" : "")
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

        /// <summary>A Curve3D through the given points. Two points stay a straight line; three or more get
        /// Catmull-Rom handles so the run bends smoothly THROUGH every click rather than being pulled off them.
        ///
        /// ⚠ `smooth: false` FOR AN ALREADY-SAMPLED PATH. Catmull-Rom is right for a handful of hand-placed
        /// clicks and wrong for a curve someone has already sampled every 4 m: the handles round and OVERSHOOT
        /// between dense points, which on the outside of a tight bend threw the end sections off the line
        /// entirely. A road's offset edge is already the curve; it does not want re-interpolating.
        ///
        /// ⚠ Godot's Curve3D is straight between points until you give it handles -- "add the points and bake"
        /// silently produces a polyline, which looks like the curve maths is wrong when nothing was computed at
        /// all. The handle is the Catmull-Rom tangent, (next - prev) / 6.</summary>
        static Curve3D BuildCurve(IReadOnlyList<Vector3> pts, bool smooth = true)
            => SplineTiling.CurveThrough(pts, smooth);   // shared with the block-barrier tool

        /// <summary>Stand the prop up, turn it along the run, and lie it on the slope.
        ///
        /// ⚠ ex=270 IS THE MESH CONVENTION, NOT A TILT: the prop OBJs are Z-up, and 3602 of PEI's ~3900
        /// placements carry it. Leaving it out lays every fence flat on the ground, which on a long thin prop
        /// still looks like a fence from above -- so it is the kind of mistake a screenshot does not catch.</summary>
        /// <summary>The steepest the ground may tip a fence. Beyond this it stands up and lets the slope pass
        /// under it, which is what a real barrier does.
        ///
        /// ⚠ WITHOUT A CLAMP A ROAD CUT LAYS THE FENCE FLAT. A guardrail sits just outside the carriageway,
        /// and that is exactly where a road carved into a hillside leaves a near-vertical bank -- so the
        /// terrain normal there is nowhere near up, and tilting by the full angle puts whole sections on their
        /// side on the ground. Seen at both ends of every guarded bend in the showcase.</summary>
        public const float MaxSeatTiltDeg = 20f;

        /// <summary>Steepest ground any seat has met this session -- how hard the clamp is working.</summary>
        public static float DebugRawTiltMax;

        static Basis SeatedBasis(Terrain terr, Vector3 at, float yawDeg)
        {
            var b = SplineTiling.SeatedBasis(terr, at, yawDeg, MaxSeatTiltDeg, out float raw);
            DebugRawTiltMax = Mathf.Max(DebugRawTiltMax, raw);
            return b;
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
