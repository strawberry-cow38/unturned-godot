using System.Collections.Generic;
using Godot;

namespace UnturnedGodot
{
    /// <summary>The map editor's fence tool: click a path, ENTER, and a fence of the chosen style is SOLVED along it --
    /// posts plumb on every corner and at the style's pitch between, bays fitted between them and racked down any slope.
    ///
    /// Master 2026-10-14: "work on getting fences as splines, might wanna split the models into the individual pieces
    /// first and then solving them in engine?". The solving is FenceSpline's; this is EditorFenceRoad's idiom (click,
    /// click, ENTER; Backspace drops a point) pointed at it, and the fence is ONE placed thing saved as its line
    /// (EditorObjects.AddFenceSpline), so picking it up and deleting it works like any prop.
    ///
    /// Keys while on: 1-8 pick the fence, C switches a curve through the points / straight legs with a post on every
    /// corner, Shift+J off.</summary>
    public partial class EditorFenceSpline : Node3D
    {
        readonly Editor _editor;
        readonly Camera3D _cam;
        readonly EditorObjects _objects;

        static readonly Color LineColor = new(0.95f, 0.75f, 0.35f);

        bool _on;
        int _style;
        bool _corners;
        readonly List<Vector3> _path = new();
        MeshInstance3D _preview;

        public bool Active => _on;
        public FenceSpline.Style Style => FenceSpline.All[_style];

        public EditorFenceSpline(Editor editor, Camera3D cam, EditorObjects objects)
        {
            _editor = editor; _cam = cam; _objects = objects;
        }

        public string ModeText => _on
            ? $"FENCE · {Style.Name} (1-8) · {(_corners ? "corners" : "curve")} (C) · {_path.Count} point(s) · ENTER lays it · Backspace drops a point · Esc · Shift+J off"
            : "Shift+J = fence";

        public void SetActive(bool on) { if (_on != on) Toggle(); }

        void Toggle()
        {
            _on = !_on;
            if (_on) Log.Print($"[editor-fence-spline] ON: {Style.Name}, {(_corners ? "corners" : "curve")}");
            else { _path.Clear(); ClearPreview(); }
        }

        public override void _UnhandledInput(InputEvent ev)
        {
            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.J } && Input.IsKeyPressed(Key.Shift))
            { Toggle(); GetViewport().SetInputAsHandled(); return; }
            if (!_on) return;

            if (ev is InputEventKey { Pressed: true, Echo: false } k)
            {
                bool used = true;
                if (k.Keycode == Key.Z && Input.IsKeyPressed(Key.Ctrl)) _editor.Undo();
                else if (k.Keycode == Key.Escape) { _path.Clear(); ClearPreview(); }
                else if (k.Keycode == Key.Backspace) { if (_path.Count > 0) _path.RemoveAt(_path.Count - 1); }
                else if (k.Keycode is Key.Enter or Key.KpEnter) { if (_path.Count >= 2) Lay(); }
                else if (k.Keycode == Key.C && !Input.IsKeyPressed(Key.Ctrl)) _corners = !_corners;
                else if (k.Keycode >= Key.Key1 && k.Keycode <= Key.Key8)
                {
                    _style = (int)(k.Keycode - Key.Key1);
                    Log.Print($"[editor-fence-spline] {Style.Name}, {Style.Pitch:0.##} m between posts");
                }
                else used = false;
                // ⚠ HANDLED, so the object tool under this one does not ALSO act on C or a number -- EditorFenceRoad's R
                // flips its rail and switches the gizmo to scale at the same time
                if (used) GetViewport().SetInputAsHandled();
                return;
            }
            if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } && !Editor.PointerOverUI(this))
            {
                if (RaycastTerrain(GetViewport().GetMousePosition(), out var hit)) _path.Add(hit);
                GetViewport().SetInputAsHandled();
            }
        }

        void Lay()
        {
            var node = _objects.AddFenceSpline(Style, _path.ToArray(), !_corners);
            _path.Clear(); ClearPreview();
            if (node == null) { Log.Print("[editor-fence-spline] nothing laid -- the path is shorter than a post gap"); return; }
            var placed = new List<Node3D> { node };
            _editor.PushUndo("lay fence", () => _objects.RemovePlaced(placed));
            _editor.MarkDirty();
        }

        public override void _Process(double delta)
        {
            if (!_on || _path.Count == 0) { ClearPreview(); return; }
            // the rubber band draws the line the fence WILL follow: the curve through the clicks, or the straight legs
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
            var im = new ImmediateMesh();
            im.SurfaceBegin(Mesh.PrimitiveType.LineStrip);
            if (_corners || pts.Count < 3)
                foreach (var p in pts) im.SurfaceAddVertex(p + Vector3.Up * 0.5f);
            else
            {
                var curve = SplineTiling.CurveThrough(pts, true);
                float len = curve.GetBakedLength();
                int steps = Mathf.Clamp(Mathf.CeilToInt(len / 1f), 2, 600);
                for (int i = 0; i <= steps; i++) im.SurfaceAddVertex(curve.SampleBaked(len * i / steps, true) + Vector3.Up * 0.5f);
            }
            im.SurfaceEnd();
            _preview.Mesh = im;
        }

        void ClearPreview() { if (_preview != null) { _preview.QueueFree(); _preview = null; } }

        bool RaycastTerrain(Vector2 screen, out Vector3 point)
        {
            point = Vector3.Zero;
            var from = _cam.ProjectRayOrigin(screen);
            var to = from + _cam.ProjectRayNormal(screen) * 4000f;
            var q = PhysicsRayQueryParameters3D.Create(from, to, 1u << 0);
            var hit = GetWorld3D().DirectSpaceState.IntersectRay(q);
            if (hit.Count == 0) return false;
            point = (Vector3)hit["position"];
            return true;
        }
    }
}
