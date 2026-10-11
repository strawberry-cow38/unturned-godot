using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    /// <summary>"Block Road": tile the concrete jersey barrier along a clicked spline, the way the guardrail
    /// tool does (strawberry 2026-10-10: "work on getting the road concrete barricade spline'd like fence
    /// road is. should be block road i think?" -- it is: Block_Road_0).
    ///
    /// ⭐ THE 3 m UNIT, NOT THE 16 m ONE. Both ship: Block_Road_0 is 3.00 m long, Block_Road_1 is 16.00 m,
    /// both 0.50 thick and 1.25 tall with their length on local +Y. A rigid 16 m tile cannot follow anything
    /// but a near-straight, for the same reason the bridge spans had to be cut down before they could follow
    /// a road at all -- so the spline walks the short one and the long one stays in the palette.
    ///
    /// ⭐ IT SHARES THE GUARDRAIL'S CURVE AND SEATING (SplineTiling.CurveThrough / SeatedBasis) rather than
    /// carrying its own. Two tools that lay props along a clicked path and disagree about what curve those
    /// clicks describe is a bug waiting for the first map that runs both down one road.</summary>
    public partial class EditorBlockRoad : Node3D
    {
        /// <summary>The tiling unit: 3.00 m of barrier. Measured off the mesh, not the name.</summary>
        public const string Unit = "Block_Road_0";
        public const string LongUnit = "Block_Road_1";    // 16 m, palette only -- see the class note
        public const float Pitch = 3.00f;

        /// <summary>HALF THE BARRIER'S THICKNESS (0.50 m across), which is what splays at a joint. ⚠ It is the
        /// transverse half-width that matters, NOT half the length: the tiles pivot about the centreline, so
        /// the corners that part are the ones offset ACROSS the run. Feeding this the 1.50 m half-LENGTH would
        /// back every unit off six times too far and open the gaps it is meant to close.</summary>
        public const float HalfWidth = 0.25f;

        /// <summary>Beyond this per-joint turn the chords visibly cut the corner. Same figure the guardrail
        /// uses; at this pitch it works out to a 10.1 m minimum radius against the guardrail's 13.5 m, because
        /// a shorter tile follows a tighter bend.</summary>
        public const float MaxTurnPerJointDeg = 17f;
        public static float MinBendRadius => Pitch / Mathf.DegToRad(MaxTurnPerJointDeg);

        /// <summary>How far a barrier is tilted onto a slope before it is left standing upright instead.</summary>
        public const float MaxSeatTiltDeg = 20f;

        /// <summary>Where a barrier sits relative to the asphalt edge when snapped to a road. NEGATIVE of the
        /// guardrail's clearance in spirit: a guardrail stands off the verge, a concrete barrier sits ON the
        /// shoulder, just inside the painted edge.</summary>
        public const float RoadInset = 0.4f;

        static readonly Color LineColor = new(0.85f, 0.85f, 0.9f);
        const uint TerrainLayer = 1u << 0;

        readonly Editor _editor;
        readonly Camera3D _cam;
        readonly EditorObjects _objects;
        readonly Terrain _terr;
        readonly RoadField _roads;

        bool _on;
        bool _flip;                              // R: which way the barrier faces
        readonly List<Vector3> _path = new();
        MeshInstance3D _preview;
        readonly List<List<Node3D>> _runs = new();

        public bool Active => _on;

        public EditorBlockRoad(Editor editor, Camera3D cam, EditorObjects objects, Terrain terr,
                               RoadField roads = null)
        {
            _editor = editor; _cam = cam; _objects = objects; _terr = terr; _roads = roads;
        }

        public string ModeText => _on
            ? (_path.Count == 0
                ? $"BLOCK ROAD · LMB to start a run · R = face · G = line the nearest road · Del = lift the last run · Shift+K = off"
                : $"BLOCK ROAD · {_path.Count} point(s), 3+ curves · ENTER lays it · Backspace drops a point · R = faces {(_flip ? "left" : "right")} · Esc")
            : "Shift+K = block road";

        public void SetActive(bool on) { if (_on != on) Toggle(); }

        void Toggle()
        {
            _on = !_on;
            if (_on) Log.Print($"[editor-block] ON: {Pitch:0.##} m units, min bend {MinBendRadius:0.#} m, {_runs.Count} run(s) laid");
            else { _path.Clear(); ClearPreview(); }
        }

        // ---- input ------------------------------------------------------------------------------------------

        public override void _UnhandledInput(InputEvent ev)
        {
            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.K } && Input.IsKeyPressed(Key.Shift))
            { Toggle(); return; }
            if (!_on) return;

            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Z } && Input.IsKeyPressed(Key.Ctrl)) { _editor.Undo(); return; }
            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }) { _path.Clear(); ClearPreview(); return; }
            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Backspace } && _path.Count > 0)
            { _path.RemoveAt(_path.Count - 1); return; }
            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Enter or Key.KpEnter } && _path.Count >= 2)
            { LayPathRun(_path); _path.Clear(); ClearPreview(); return; }
            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.R } && !Input.IsKeyPressed(Key.Shift))
            { _flip = !_flip; return; }

            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Delete } && _runs.Count > 0)
            {
                var last = _runs[^1];
                _objects.RemovePlaced(last);
                _runs.RemoveAt(_runs.Count - 1);
                _editor.MarkDirty();
                return;
            }

            // G: line the nearest road, both edges, for its whole length.
            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.G } && _roads != null)
            {
                if (!RaycastTerrain(GetViewport().GetMousePosition(), out var at)) return;
                if (!_roads.NearestRoad(at, out int rd, out _) || rd < 0) return;
                var laid = new List<Node3D>();
                int got = LayAlongRoad(_objects, _terr, _roads, rd, laid);
                if (got == 0) return;
                _runs.Add(laid);
                _editor.PushUndo("line road with barriers", () => { _objects.RemovePlaced(laid); _runs.Remove(laid); });
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
            var curve = SplineTiling.CurveThrough(pts);
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

        // ---- laying -----------------------------------------------------------------------------------------

        public int LayPathRun(IReadOnlyList<Vector3> pts)
        {
            var laid = new List<Node3D>();
            int n = LayPath(_objects, _terr, pts, _flip, laid);
            if (n == 0) return 0;
            _runs.Add(laid);
            _editor.PushUndo("lay block road", () => { _objects.RemovePlaced(laid); _runs.Remove(laid); });
            _editor.MarkDirty();
            return n;
        }

        /// <summary>The placement itself, with no editor state -- static so a showcase station lays a demo run
        /// through the SAME routine the tool uses rather than re-implementing it.
        ///
        /// ⭐ WALKED BY ARC LENGTH, and the step SHRINKS ON A BEND. Two rigid tiles meeting at a turn have flat
        /// parallel ends that splay about the centreline, so each one is backed off along its own axis by
        /// SplineTiling.OverlapFor -- which is zero on a straight, so a straight run is placed exactly where it
        /// would have been without this. Without the backoff a 0.5 m-thick barrier opens a visible slot at
        /// every joint of a bend; with it the inner corners bury themselves in solid concrete instead.</summary>
        public static int LayPath(EditorObjects objects, Terrain terr, IReadOnlyList<Vector3> pts,
                                  bool flip, List<Node3D> into, bool smooth = true)
        {
            if (objects == null || pts == null || pts.Count < 2) return 0;
            var curve = SplineTiling.CurveThrough(pts, smooth);
            float total = curve.GetBakedLength();
            if (total < Pitch)
            {
                Log.Print($"[editor-block] {total:0.#} m is under one {Pitch:0.##} m unit -- nothing laid");
                return 0;
            }

            int laid = 0;
            float s = 0f, tightest = float.MaxValue;
            // ⚠ GUARD THE WALK, not just the length: the step shrinks by the overlap, so a bug that drove the
            // overlap to the full pitch would loop forever placing units on the spot. OverlapFor already caps
            // at pitch/2, and this is the belt to that braces.
            int guard = Mathf.CeilToInt(total / (Pitch * 0.5f)) + 4;
            while (s + Pitch <= total + 0.001f && guard-- > 0)
            {
                var p0 = curve.SampleBaked(s, true);
                var p1 = curve.SampleBaked(s + Pitch, true);
                var chord = new Vector3(p1.X - p0.X, 0f, p1.Z - p0.Z);
                if (chord.LengthSquared() < 1e-6f) { s += Pitch; continue; }
                var dir = chord.Normalized();

                var centre = (p0 + p1) * 0.5f;
                if (terr != null) centre.Y = terr.SampleHeight(centre.X, centre.Z);

                // ⚠⚠ YawForDir TAKES PROC-FRAME COORDINATES, WHERE +Y IS WORLD −Z. A world direction mirrors
                // the run about X -- and a straight run along X has dz = 0, so it looks perfect and only a
                // curve bends into the error. Same negation the guardrail tool documents.
                float yaw = ProcIsland.YawForDir(dir.X, -dir.Z) + (flip ? 180f : 0f);
                var basis = SplineTiling.SeatedBasis(terr, centre, yaw, MaxSeatTiltDeg, out _);
                var node = objects.Place(Unit, centre, basis);
                if (node != null) { into?.Add(node); laid++; }

                // how hard the NEXT joint turns -> how far this one has to back off
                float turn = 0f;
                float sn = s + Pitch;
                if (sn + Pitch <= total)
                {
                    var q1 = curve.SampleBaked(sn + Pitch, true);
                    var next = new Vector3(q1.X - p1.X, 0f, q1.Z - p1.Z);
                    if (next.LengthSquared() > 1e-6f)
                    {
                        turn = dir.AngleTo(next.Normalized());
                        if (turn > 1e-4f) tightest = Mathf.Min(tightest, Pitch / turn);
                    }
                }
                s += Pitch - SplineTiling.OverlapFor(HalfWidth, Pitch, turn);
            }

            if (tightest < MinBendRadius)
                Log.Print($"[editor-block] tightest bend {tightest:0.#} m is under the {MinBendRadius:0.#} m "
                        + $"a {Pitch:0.##} m unit can follow -- the chords will cut that corner");
            Log.Print($"[editor-block] laid {laid} unit(s) over {total:0.#} m");
            return laid;
        }

        /// <summary>Line BOTH edges of a road with barrier, for its whole length. A concrete barrier sits on
        /// the shoulder just inside the painted edge, which is where RoadInset puts it -- unlike the guardrail,
        /// which stands off the verge beyond it.</summary>
        public static int LayAlongRoad(EditorObjects objects, Terrain terr, RoadField roads, int road,
                                       List<Node3D> into)
        {
            if (objects == null || roads == null) return 0;
            float total = roads.RoadLength(road);
            float half = roads.RoadHalfWidth(road);
            if (total < Pitch * 2f || half <= 0f) return 0;
            float off = Mathf.Max(0.1f, half - RoadInset);

            int n = Mathf.FloorToInt(total / Pitch);
            var left = new List<Vector3>(n + 1);
            var right = new List<Vector3>(n + 1);
            for (int i = 0; i <= n; i++)
            {
                if (!roads.EvaluateAlong(road, i * Pitch, out var p, out var t)) return 0;
                var side = new Vector3(t.Z, 0f, -t.X).Normalized();   // the spline's RIGHT
                right.Add(p + side * off);
                left.Add(p - side * off);
            }
            // ⚠ Each edge is laid from its OWN point list, NOT the centreline offset at lay time: the two
            // edges of a bend are different lengths, so one list walked twice would crowd the inside kerb.
            int laidR = LayPath(objects, terr, right, false, into, smooth: false);
            int laidL = LayPath(objects, terr, left, true, into, smooth: false);
            Log.Print($"[editor-block] road {road}: {laidR} right + {laidL} left over {total:0.#} m");
            return laidR + laidL;
        }
    }
}
