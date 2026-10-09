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

        /// <summary>The prop's own length along its local +Y, measured off the mesh (bbox Y -8.125..+8.125) and
        /// corroborated by its lods.txt row ("Fence_Road_0 MEDIUM 16.25"). Segments butt end to end at this
        /// pitch, so a run reads as one continuous barrier rather than a dotted line of props.</summary>
        public const float SegmentLength = 16.25f;

        public const string Intact = "Fence_Road_0";
        public const string Broken = "Fence_Road_Broken_0";
        const string PostsSuffix = "_Posts";
        const string RailSuffix = "_Rail";

        const uint TerrainLayer = 1u << 0;
        static readonly Color LineColor = new(0.45f, 0.95f, 0.55f);

        bool _on;
        bool _broken;        // B: lay the wrecked variant instead
        bool _flip;          // R: which side of the line the rail faces
        bool _haveStart;
        Vector3 _start;
        MeshInstance3D _preview;

        /// <summary>Every run laid this session, newest last, so Delete can strip the rail off the last one.</summary>
        readonly List<(List<Node3D> posts, List<Node3D> rail)> _runs = new();

        public bool Active => _on;

        public EditorFenceRoad(Editor editor, Camera3D cam, EditorObjects objects, Terrain terr)
        {
            _editor = editor; _cam = cam; _objects = objects; _terr = terr;
        }

        public string ModeText => _on
            ? (_haveStart
                ? $"FENCE ROAD · {(_broken ? "broken" : "intact")} · LMB the far end · R = rail faces {(_flip ? "left" : "right")} · Esc"
                : $"FENCE ROAD · {(_broken ? "broken" : "intact")} · LMB to start a run · B = variant · R = side · Del = strip the last rail · Shift+F = off")
            : "Shift+F = fence road";

        public void SetActive(bool on) { if (_on != on) Toggle(); }

        void Toggle()
        {
            _on = !_on;
            if (_on) Log.Print($"[editor-fence] ON: {SegmentLength:0.##} m segments, {_runs.Count} run(s) laid");
            else { _haveStart = false; ClearPreview(); }
        }

        // ---- input ------------------------------------------------------------------------------------------

        public override void _UnhandledInput(InputEvent ev)
        {
            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F } && Input.IsKeyPressed(Key.Shift))
            { Toggle(); GetViewport().SetInputAsHandled(); return; }
            if (!_on) return;

            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Z } && Input.IsKeyPressed(Key.Ctrl)) { _editor.Undo(); return; }
            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }) { _haveStart = false; ClearPreview(); return; }
            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.B }) { _broken = !_broken; return; }
            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.R } && !Input.IsKeyPressed(Key.Shift)) { _flip = !_flip; return; }

            // ⭐ DELETE STRIPS THE RAIL AND LEAVES THE POSTS. Deleting the whole run is what Ctrl+Z is for; this
            // is the thing the split exists to make possible, and a key that only duplicated undo would be waste.
            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Delete } && _runs.Count > 0)
            {
                var last = _runs[^1];
                if (last.rail.Count == 0) { Log.Print("[editor-fence] the last run has already lost its rail"); return; }
                var stripped = new List<Node3D>(last.rail);
                _editor.PushUndo("strip fence rail", () => { /* the rail is gone; Ctrl+Z past this re-lays the run */ });
                _objects.RemovePlaced(stripped);
                last.rail.Clear();
                _editor.MarkDirty();
                Log.Print($"[editor-fence] stripped {stripped.Count} rail section(s); the posts stay up");
                return;
            }

            if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } && !Editor.PointerOverUI(this))
            {
                if (!RaycastTerrain(GetViewport().GetMousePosition(), out var hit)) return;
                if (!_haveStart) { _start = hit; _haveStart = true; return; }
                LayRun(_start, hit);
                _start = hit;   // CHAIN: the end becomes the next start
            }
        }

        public override void _Process(double delta)
        {
            if (!_on || !_haveStart) { ClearPreview(); return; }
            // The rubber band, as EditorPowerLines does it: a 16 m segment pitch means a misjudged click costs a
            // whole prop, and at editor camera distances the ground gives you nothing to judge the length against.
            if (!RaycastTerrain(GetViewport().GetMousePosition(), out var pt)) { ClearPreview(); return; }
            if (_preview == null)
            {
                _preview = new MeshInstance3D
                {
                    MaterialOverride = new StandardMaterial3D { AlbedoColor = LineColor, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded },
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                };
                AddChild(_preview);
            }
            var im = new ImmediateMesh();
            im.SurfaceBegin(Mesh.PrimitiveType.Lines);
            im.SurfaceAddVertex(_start + Vector3.Up * 0.5f);
            im.SurfaceAddVertex(pt + Vector3.Up * 0.5f);
            im.SurfaceEnd();
            _preview.Mesh = im;
        }

        void ClearPreview() { if (_preview != null) { _preview.QueueFree(); _preview = null; } }

        // ---- laying -----------------------------------------------------------------------------------------

        /// <summary>Lay whole segments from `a` to `b`. Returns how many went down.
        ///
        /// ⚠ WHOLE SEGMENTS ONLY, and the remainder is left bare on purpose. A fence is a rigid 16.25 m prop;
        /// stretching or overlapping the last one to reach the exact click is how a run ends up with a visibly
        /// doubled post or a section at the wrong scale. Click again to carry on past the gap.</summary>
        public int LayRun(Vector3 a, Vector3 b)
        {
            var posts = new List<Node3D>();
            var rail = new List<Node3D>();
            int n = LaySegments(_objects, _terr, a, b, _broken, _flip, posts, rail);
            if (n == 0) return 0;

            // One undo entry for the whole run -- see EditorObjects.RemovePlaced.
            var all = new List<Node3D>(posts); all.AddRange(rail);
            var run = (posts, rail);
            _runs.Add(run);
            _editor.PushUndo("lay fence road", () => { _objects.RemovePlaced(all); _runs.Remove(run); });
            _editor.MarkDirty();
            Log.Print($"[editor-fence] laid {n} segment(s) {(_broken ? "broken" : "intact")}, "
                    + $"rail on the {(_flip ? "left" : "right")}");
            return n;
        }

        /// <summary>The placement itself, with no editor state: how many whole segments fit between two points,
        /// and where each one sits. STATIC and parameterised because MapShowcase lays a demo run too, and the
        /// one thing a showcase of a tool must not do is re-implement the tool -- a second copy of this loop is
        /// a second place for the segment pitch or the rail facing to be wrong.</summary>
        public static int LaySegments(EditorObjects objects, Terrain terr, Vector3 a, Vector3 b,
                                      bool broken, bool flip, List<Node3D> posts, List<Node3D> rail)
        {
            if (objects == null) return 0;
            var flat = new Vector3(b.X - a.X, 0f, b.Z - a.Z);
            float len = flat.Length();
            int n = Mathf.FloorToInt(len / SegmentLength);
            if (n <= 0 || len < 1e-3f)
            {
                Log.Print($"[editor-fence] {len:0.#} m is under one {SegmentLength:0.##} m segment -- nothing laid");
                return 0;
            }
            var dir = flat / len;
            string b0 = broken ? Broken : Intact;

            // ⚠ THE RAIL FACE HAS TO FACE THE ROAD, and which way that is was MEASURED, not guessed: in the rail
            // height band Fence_Road_0 has 158 vertices at local x > 0 and 30 at x < 0, so the beam is the local
            // +X half (the same fact ProcIslandSpawn relies on). YawForDir aims local +Y along the run, which
            // puts +X on the run's RIGHT -- so the flip, which yaws by 180, puts the rail on the other side.
            float yaw = ProcIsland.YawForDir(dir.X, dir.Z) + (flip ? 180f : 0f);

            Vector3 first = Vector3.Zero;
            for (int i = 0; i < n; i++)
            {
                var centre = a + dir * ((i + 0.5f) * SegmentLength);
                if (terr != null) centre.Y = terr.SampleHeight(centre.X, centre.Z);
                if (i == 0) first = centre;
                var basis = SeatedBasis(terr, centre, yaw);
                var p = objects.Place(b0 + PostsSuffix, centre, basis);
                var r = objects.Place(b0 + RailSuffix, centre, basis);
                if (p != null) posts?.Add(p);
                if (r != null) rail?.Add(r);
            }
            // ⚠ SAY WHERE, not just how many. A run that lands at the wrong HEIGHT looks identical in a count
            // and obvious in one number -- the first showcase render had them hanging in the sky.
            Log.Print($"[editor-fence] {n}x {b0} from {a.Round()} to {b.Round()}, first segment at {first.Round()}"
                    + (terr == null ? " (no terrain: flat seating)" : $", ground {terr.SampleHeight(a.X, a.Z):0.##}"));
            return n;
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
