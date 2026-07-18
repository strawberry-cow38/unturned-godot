using Godot;

namespace UnturnedGodot
{
    // Terrain sub-editor, ported from SDG.Unturned EditorTerrain (Devkit heightmap brushes). LMB raises the terrain under
    // the brush, Shift+LMB lowers it (source linear getBrushAlpha falloff); M cycles Raise/Flatten/Smooth, P toggles
    // Materials splat-paint, L cycles the layer; '[' ']' size the brush, ',' '.' strength. Held-drag applies every frame
    // dt-scaled (source continuous brush). Drives Terrain.EditHeight/EditFlatten/EditSmooth/PaintSplat -> edits the
    // in-memory _grid; only the CHUNKS under the brush rebuild, colliders deferred to mouse-up (FlushColliders) = smooth.
    public partial class EditorTerrain : Node3D
    {
        readonly Editor _editor;
        readonly Camera3D _cam;
        readonly EditorCamera _flyCam;
        readonly Terrain _terr;
        const uint TerrainLayer = 1u << 0;
        Node3D _ring;
        float _radius = 28f, _strength = 20f;   // brush radius (world m) + strength (world Y PER SECOND, source held brush is dt-scaled)
        enum EBrush { Raise, Flatten, Smooth }   // source Devkit heightmap modes (ADJUST / FLATTEN / SMOOTH)
        EBrush _brush = EBrush.Raise;
        bool _paint;   // false = height sculpt (Height tab), true = Materials splat-paint
        int _layer;    // 0-7 splat layer to paint
        static readonly string[] LayerNames = { "Dirt", "Wheat", "Grass", "Gravel", "Road", "Sand", "Snow", "Stone" };

        public string ModeText => _paint
            ? $"PAINT {LayerNames[_layer]} · radius {_radius:0}m · P=sculpt · L=layer"
            : $"{_brush}{(_brush == EBrush.Raise ? " (Shift=lower)" : "")} · radius {_radius:0}m · strength {_strength:0} · P=paint";
        static string SavePath => ProjectSettings.GlobalizePath("res://content/terrain/") + "editor_heightmap.bin";

        public int Save()   // Editor.Save() fan-out: persist the sculpted heightmap (only if edited)
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
            _editor.ModeChanged += _ => RefreshVisibility();
            RefreshVisibility();
        }

        void RefreshVisibility() { Visible = _editor.Mode == EEditorMode.Terrain; }

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
            if (!Visible || (_flyCam != null && _flyCam.Flying) || !RaycastTerrain(GetViewport().GetMousePosition(), out var pt))
            {
                if (_ring != null) _ring.Visible = false;
                return;
            }
            _ring.Position = pt + Vector3.Up * 0.5f;
            _ring.Scale = new Vector3(_radius, 1f, _radius);
            _ring.Visible = true;
            // source-accurate HELD-DRAG: the brush applies every frame while LMB is held, dt-scaled (Devkit continuous brush)
            if (_terr != null && Input.IsMouseButtonPressed(MouseButton.Left))
            {
                float dt = (float)d;
                if (_paint) _terr.PaintSplat(pt.X, pt.Z, _radius, _layer);
                else switch (_brush)
                {
                    case EBrush.Raise: _terr.EditHeight(pt.X, pt.Z, _radius, _strength * dt * (Input.IsKeyPressed(Key.Shift) ? -1f : 1f)); break;   // height += dt*strength*alpha
                    case EBrush.Flatten: _terr.EditFlatten(pt.X, pt.Z, _radius, Mathf.Clamp(_strength * dt * 0.15f, 0.01f, 1f)); break;
                    case EBrush.Smooth: _terr.EditSmooth(pt.X, pt.Z, _radius, Mathf.Clamp(_strength * dt * 0.15f, 0.01f, 1f)); break;
                }
            }
        }

        public override void _UnhandledInput(InputEvent ev)
        {
            if (_editor.Mode != EEditorMode.Terrain || (_flyCam != null && _flyCam.Flying) || _terr == null) return;
            if (ev is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && !mb.Pressed)
            {
                if (!_paint) _terr?.FlushColliders();   // held-drag stroke end (mouse-up): rebuild trimesh colliders for the touched chunks
            }
            else if (ev is InputEventKey { Pressed: true, Echo: false } k)
            {
                switch (k.Keycode)
                {
                    case Key.P: _paint = !_paint; break;                                       // toggle Height sculpt <-> Materials paint
                    case Key.L: if (_paint) _layer = (_layer + 1) % 8; break;                  // cycle the paint layer
                    case Key.M: if (!_paint) _brush = (EBrush)(((int)_brush + 1) % 3); break;   // cycle Raise/Flatten/Smooth (source Devkit modes)
                    case Key.Bracketleft: _radius = Mathf.Max(6f, _radius - 4f); break;    // brush radius (source brushRadius)
                    case Key.Bracketright: _radius = Mathf.Min(140f, _radius + 4f); break;
                    case Key.Comma: _strength = Mathf.Max(1f, _strength - 2f); break;      // brush strength
                    case Key.Period: _strength = Mathf.Min(60f, _strength + 2f); break;
                }
            }
        }

        // harness (--editor UG_EDITORTERRAIN): raise a big bump so a render shows the sculpt working
        public void DemoSculpt(Vector3 at)
        {
            if (_terr == null) return;
            _terr.EditHeight(at.X, at.Z, 45f, 110f);                             // a sharp spike (raise)
            for (int i = 0; i < 4; i++) _terr.EditSmooth(at.X, at.Z, 62f, 0.5f);   // smooth it into a rounded hill (verifies Smooth)
            _terr.FlushColliders();                                              // stroke end
            GD.Print("[editorterrain] raised + smoothed a demo hill");
        }

        // harness (--editor UG_EDITORPAINT with UG_EDITORTERRAIN): splat-paint a layer patch so a render shows Materials paint
        public void DemoPaint(Vector3 at, int layer)
        {
            if (_terr == null) return;
            _terr.PaintSplat(at.X, at.Z, 55f, layer);
            GD.Print($"[editorterrain] painted a {LayerNames[layer]} patch (splat layer {layer})");
        }
    }
}
