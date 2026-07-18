using Godot;

namespace UnturnedGodot
{
    // Terrain sub-editor, ported from SDG.Unturned EditorTerrain (Devkit heightmap brushes). Tools are picked from the
    // EditorTerrainPanel buttons (Raise/Lower/Flatten/Smooth/Ramp + Materials paint + layer), matching the source
    // EDevkitLandscapeToolHeightmapMode. Raise/Lower/Flatten/Smooth are held-drag continuous brushes (dt-scaled); RAMP is
    // two clicks (begin -> end) grading the terrain between them. Keys still work as shortcuts. Drives Terrain.Edit* -> the
    // in-memory _grid; only the chunks under the brush rebuild, colliders flushed on stroke-end / ramp-apply.
    public partial class EditorTerrain : Node3D
    {
        readonly Editor _editor;
        readonly Camera3D _cam;
        readonly EditorCamera _flyCam;
        readonly Terrain _terr;
        const uint TerrainLayer = 1u << 0;
        Node3D _ring, _rampMarker;
        float _radius = 28f, _strength = 20f;
        public enum EBrush { Raise, Lower, Flatten, Smooth, Ramp }   // source Devkit heightmap modes (ADJUST±/FLATTEN/SMOOTH/RAMP)
        EBrush _brush = EBrush.Raise;
        bool _paint;   // false = height sculpt, true = Materials splat-paint
        int _layer;    // 0-7 splat layer to paint
        Vector3 _rampBegin; bool _rampArmed;   // RAMP: first click sets begin, second applies
        static readonly string[] LayerNames = { "Dirt", "Wheat", "Grass", "Gravel", "Road", "Sand", "Snow", "Stone" };
        public static readonly string[] BrushNames = { "Raise", "Lower", "Flatten", "Smooth", "Ramp" };

        // --- accessors for the EditorTerrainPanel buttons/sliders ---
        public bool Painting => _paint;
        public int Brush => (int)_brush;
        public int Layer => _layer;
        public int LayerCount => LayerNames.Length;
        public string LayerName(int i) => LayerNames[i];
        public float RadiusVal { get => _radius; set => _radius = Mathf.Clamp(value, 6f, 140f); }
        public float StrengthVal { get => _strength; set => _strength = Mathf.Clamp(value, 1f, 60f); }
        public void SelectBrush(int b) { _brush = (EBrush)b; _paint = false; _rampArmed = false; }
        public void SelectPaint() { _paint = true; _rampArmed = false; }
        public void SelectLayer(int l) { _layer = Mathf.Clamp(l, 0, LayerNames.Length - 1); _paint = true; }

        public string ModeText => _paint
            ? $"PAINT {LayerNames[_layer]} · radius {_radius:0}m"
            : $"{BrushNames[(int)_brush]}{(_brush == EBrush.Ramp ? " (click begin, click end)" : "")} · radius {_radius:0}m · strength {_strength:0}";
        static string SavePath => ProjectSettings.GlobalizePath("res://content/terrain/") + "editor_heightmap.bin";

        public int Save()
        {
            if (_terr == null || !_terr.Dirty) return 0;
            _terr.SaveHeightmap(SavePath);
            GD.Print($"[editor-terrain] saved heightmap -> {SavePath}");
            return 1;
        }

        public EditorTerrain(Editor editor, Camera3D cam, Terrain terr)
        {
            _editor = editor; _cam = cam; _flyCam = cam as EditorCamera; _terr = terr;
            if (_terr != null && _terr.LoadHeightmap(SavePath)) GD.Print("[editor-terrain] loaded saved sculpt");
            _ring = new Node3D { Visible = false };
            _ring.AddChild(new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = 0.93f, OuterRadius = 1f }, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(1f, 0.9f, 0.2f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, NoDepthTest = true } });
            AddChild(_ring);
            _rampMarker = new MeshInstance3D { Visible = false, Mesh = new SphereMesh { Radius = 2f, Height = 4f }, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.2f, 1f, 0.4f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, NoDepthTest = true } };
            AddChild(_rampMarker);
            _editor.ModeChanged += _ => RefreshVisibility();
            RefreshVisibility();
        }

        void RefreshVisibility() { Visible = _editor.Mode == EEditorMode.Terrain; if (!Visible) { _rampArmed = false; _rampMarker.Visible = false; } }

        bool RaycastTerrain(Vector2 screen, out Vector3 pt)
        {
            pt = Vector3.Zero;
            var from = _cam.ProjectRayOrigin(screen);
            var to = from + _cam.ProjectRayNormal(screen) * 12000f;
            var q = new PhysicsRayQueryParameters3D { From = from, To = to, CollisionMask = TerrainLayer };
            var hit = GetWorld3D().DirectSpaceState.IntersectRay(q);
            if (hit.Count == 0) return false;
            pt = (Vector3)hit["position"]; return true;
        }

        public override void _Process(double d)
        {
            if (!Visible || (_flyCam != null && _flyCam.Flying) || Editor.PointerOverUI(this) || !RaycastTerrain(GetViewport().GetMousePosition(), out var pt))
            {
                if (_ring != null) _ring.Visible = false;
                return;
            }
            _ring.Position = pt + Vector3.Up * 0.5f;
            _ring.Scale = new Vector3(_radius, 1f, _radius);
            _ring.Visible = true;
            if (_terr == null || _brush == EBrush.Ramp || !Input.IsMouseButtonPressed(MouseButton.Left)) return;   // ramp is click-based (below), not held
            float dt = (float)d;
            if (_paint) { _terr.PaintSplat(pt.X, pt.Z, _radius, _layer); return; }
            switch (_brush)   // source-accurate held-drag: applies every frame, dt-scaled
            {
                case EBrush.Raise: _terr.EditHeight(pt.X, pt.Z, _radius, _strength * dt); break;
                case EBrush.Lower: _terr.EditHeight(pt.X, pt.Z, _radius, -_strength * dt); break;
                case EBrush.Flatten: _terr.EditFlatten(pt.X, pt.Z, _radius, Mathf.Clamp(_strength * dt * 0.15f, 0.01f, 1f)); break;
                case EBrush.Smooth: _terr.EditSmooth(pt.X, pt.Z, _radius, Mathf.Clamp(_strength * dt * 0.15f, 0.01f, 1f)); break;
            }
        }

        public override void _UnhandledInput(InputEvent ev)
        {
            if (_editor.Mode != EEditorMode.Terrain || (_flyCam != null && _flyCam.Flying) || _terr == null) return;
            if (ev is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed && _brush == EBrush.Ramp && !_paint && !Editor.PointerOverUI(this) && RaycastTerrain(GetViewport().GetMousePosition(), out var rp))
                {
                    if (!_rampArmed) { _rampBegin = rp; _rampArmed = true; _rampMarker.Position = rp + Vector3.Up * 0.5f; _rampMarker.Visible = true; }   // first click: begin
                    else { _terr.EditRamp(_rampBegin, rp, _radius); _rampArmed = false; _rampMarker.Visible = false; }                                     // second click: grade begin->end
                }
                else if (!mb.Pressed && !_paint && _brush != EBrush.Ramp) _terr.FlushColliders();   // held-drag stroke end: rebuild the touched chunks' colliders
            }
            else if (ev is InputEventKey { Pressed: true, Echo: false } k)   // keyboard shortcuts (buttons are the primary UI now)
            {
                switch (k.Keycode)
                {
                    case Key.P: _paint = !_paint; _rampArmed = false; break;
                    case Key.L: if (_paint) _layer = (_layer + 1) % 8; break;
                    case Key.M: if (!_paint) { _brush = (EBrush)(((int)_brush + 1) % 5); _rampArmed = false; } break;
                    case Key.Bracketleft: _radius = Mathf.Max(6f, _radius - 4f); break;
                    case Key.Bracketright: _radius = Mathf.Min(140f, _radius + 4f); break;
                    case Key.Comma: _strength = Mathf.Max(1f, _strength - 2f); break;
                    case Key.Period: _strength = Mathf.Min(60f, _strength + 2f); break;
                }
            }
        }

        // harness (UG_EDITORTERRAIN): raise a big bump so a render shows the sculpt working
        public void DemoSculpt(Vector3 at)
        {
            if (_terr == null) return;
            _terr.EditHeight(at.X, at.Z, 45f, 110f);
            for (int i = 0; i < 4; i++) _terr.EditSmooth(at.X, at.Z, 62f, 0.5f);
            _terr.FlushColliders();
            GD.Print("[editorterrain] raised + smoothed a demo hill");
        }

        public void DemoPaint(Vector3 at, int layer)
        {
            if (_terr == null) return;
            _terr.PaintSplat(at.X, at.Z, 55f, layer);
            GD.Print($"[editorterrain] painted a {LayerNames[layer]} patch (splat layer {layer})");
        }

        // harness (UG_EDITORTERRAIN UG_TERRAMP): grade a ramp between two points so a render shows the RAMP tool
        public void DemoRamp(Vector3 a, Vector3 b)
        {
            if (_terr == null) return;
            _terr.EditRamp(a, b, 40f);
            GD.Print($"[editorterrain] ramped {a} -> {b}");
        }
    }
}
