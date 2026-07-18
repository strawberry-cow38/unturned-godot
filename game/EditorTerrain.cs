using Godot;

namespace UnturnedGodot
{
    // Terrain sub-editor (Height slice), ported from SDG.Unturned EditorTerrain (Devkit heightmap ADJUST). LMB raises the
    // terrain under the brush, Shift+LMB lowers it (radial cos-falloff); '[' ']' size the brush, ',' '.' strength. Drives
    // Terrain.EditHeight -> modifies the in-memory _grid + rebuilds the mesh/collider. Shown on the Terrain dashboard tab.
    // NOTE: a stroke rebuilds the WHOLE merged island mesh (fine for the shot / occasional edits; chunking is a perf
    // follow-up). Materials (splat paint), Flatten/Smooth brushes, and the heightmap save are the next slices.
    public partial class EditorTerrain : Node3D
    {
        readonly Editor _editor;
        readonly Camera3D _cam;
        readonly EditorCamera _flyCam;
        readonly Terrain _terr;
        const uint TerrainLayer = 1u << 0;
        Node3D _ring;
        float _radius = 28f, _strength = 8f;   // brush radius (world m) + strength (world Y per stroke)

        public string ModeText => $"raise (Shift=lower) · radius {_radius:0}m · strength {_strength:0}";

        public EditorTerrain(Editor editor, Camera3D cam, Terrain terr)
        {
            _editor = editor; _cam = cam; _flyCam = cam as EditorCamera; _terr = terr;
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
        }

        public override void _UnhandledInput(InputEvent ev)
        {
            if (_editor.Mode != EEditorMode.Terrain || (_flyCam != null && _flyCam.Flying) || _terr == null) return;
            if (ev is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && mb.Pressed)
            {
                if (!RaycastTerrain(GetViewport().GetMousePosition(), out var pt)) return;
                float dir = Input.IsKeyPressed(Key.Shift) ? -1f : 1f;   // Shift = lower
                _terr.EditHeight(pt.X, pt.Z, _radius, _strength * dir);   // one stroke per click (whole-mesh rebuild is heavy)
                GD.Print($"[editor-terrain] {(dir > 0 ? "raised" : "lowered")} at ({pt.X:0},{pt.Z:0}) r={_radius:0} s={_strength:0}");
            }
            else if (ev is InputEventKey { Pressed: true, Echo: false } k)
            {
                switch (k.Keycode)
                {
                    case Key.Bracketleft: _radius = Mathf.Max(6f, _radius - 4f); break;    // brush radius (source brushRadius)
                    case Key.Bracketright: _radius = Mathf.Min(140f, _radius + 4f); break;
                    case Key.Comma: _strength = Mathf.Max(1f, _strength - 2f); break;      // brush strength
                    case Key.Period: _strength = Mathf.Min(60f, _strength + 2f); break;
                }
            }
        }

        // harness (--editor UG_EDITORTERRAIN): raise a big bump so a render shows the sculpt working
        public void DemoSculpt(Vector3 at) { if (_terr != null) { _terr.EditHeight(at.X, at.Z, 45f, 110f); GD.Print("[editorterrain] raised a demo bump"); } }
    }
}
