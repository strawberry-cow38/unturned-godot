using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // Objects sub-editor (Phase 2), ported from SDG.Unturned EditorObjects. Pick a prop from the catalog, click to
    // PLACE it on the map; click a placed prop to SELECT it; Delete removes it; R rotates the placement yaw. Placement
    // + picking raycast the editor world's colliders (WorldMode.Editor). Each placed prop is built exactly like
    // WorldBuilder builds the loaded ones (mesh + textured material + trimesh collider), so a placed prop is identical
    // to a native one. Move/rotate gizmos + .level save land in the next slices.
    public partial class EditorObjects : Node3D
    {
        readonly Editor _editor;
        readonly Node _world;      // Main root (a Node) -- placed props are added here (siblings of the loaded world)
        readonly Camera3D _cam;
        readonly EditorCamera _flyCam;

        static string Dir => ProjectSettings.GlobalizePath("res://content/objects/");
        const uint TerrainLayer = 1u << 0, SmallPropLayer = 1u << 6, EditorPickLayer = 1u << 7;

        readonly List<string> _catalog = new();
        readonly Dictionary<string, string> _nameToGuid = new();   // mesh name -> first guid (for writing placements)
        readonly Dictionary<string, string> _guidToName = new();   // guid -> mesh name (for loading placements)
        public IReadOnlyList<string> Catalog => _catalog;
        public string PlaceName;   // the prop to place on click; null = select mode

        readonly Dictionary<string, ArrayMesh> _meshCache = new();
        readonly List<Node3D> _placed = new();
        readonly Dictionary<Rid, Node3D> _pickToObj = new();
        Node3D _selected;
        MeshInstance3D _marker;
        EditorGizmo _gizmo;
        float _placeYaw;

        public EditorObjects(Editor editor, Node world, EditorCamera cam)
        {
            _editor = editor; _world = world; _cam = cam; _flyCam = cam;
            _gizmo = new EditorGizmo(cam); AddChild(_gizmo);   // the source TransformHandles translate gizmo, shown on the selection
            LoadCatalog();
            if (_catalog.Count > 0) PlaceName = _catalog[0];   // default to placing the first prop
        }

        void LoadCatalog()
        {
            string gm = Dir + "guid_mesh.txt";   // "<guid> <meshName>" -- the unique mesh names are the placeable catalog
            if (!System.IO.File.Exists(gm)) return;
            var seen = new HashSet<string>();
            foreach (var line in System.IO.File.ReadLines(gm))
            {
                var p = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (p.Length >= 2 && seen.Add(p[1])) _catalog.Add(p[1]);
            }
            _catalog.Sort();
        }

        ArrayMesh MeshFor(string name)
        {
            if (_meshCache.TryGetValue(name, out var m)) return m;
            m = ObjMesh.Load(Dir + name + ".obj"); _meshCache[name] = m; return m;
        }

        // mirrors WorldBuilder.MatFor (the common textured path): VertexColorUseAsAlbedo, nearest-filtered albedo,
        // palette textures skip mipmaps. Glass/foliage nuances are simplified for the editor.
        StandardMaterial3D MatFor(string name)
        {
            var mm = new StandardMaterial3D { Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled, VertexColorUseAsAlbedo = true };
            string tp = Dir + name + "_tex.png";
            var img = new Image();
            if (System.IO.File.Exists(tp) && img.Load(tp) == Error.Ok)
            {
                bool palette = img.GetWidth() <= 16 && img.GetHeight() <= 16;
                if (!palette) img.GenerateMipmaps();
                mm.AlbedoTexture = ImageTexture.CreateFromImage(img);
                mm.TextureFilter = palette ? BaseMaterial3D.TextureFilterEnum.Nearest : BaseMaterial3D.TextureFilterEnum.NearestWithMipmaps;
            }
            else mm.AlbedoColor = new Color(0.60f, 0.55f, 0.47f);
            return mm;
        }

        // Build + add a prop at a world position (yaw only for now). Returns its root Node3D.
        public Node3D Place(string name, Vector3 pos, float yawDeg)
        {
            var mesh = MeshFor(name);
            if (mesh == null) return null;
            // Upright = the loaded-prop convention (WorldBuilder basis): the extracted meshes are authored lying down,
            // so pitch ex=270 stands them up; yaw is about world-up. Placing yaw-only left every prop flat (master).
            var rot = new Basis(Vector3.Up, Mathf.DegToRad(yawDeg)) * new Basis(Vector3.Right, Mathf.DegToRad(270f));
            var root = new Node3D { Transform = new Transform3D(rot, pos) };
            root.SetMeta("obj_name", name);
            root.AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = MatFor(name) });
            var shp = mesh.CreateTrimeshShape();   // trimesh collider so the prop is pickable (+ later walkable)
            if (shp != null)
            {
                var body = new StaticBody3D { CollisionLayer = EditorPickLayer, CollisionMask = 0 };
                body.AddChild(new CollisionShape3D { Shape = shp });
                root.AddChild(body);
                _world.AddChild(root);
                _pickToObj[body.GetRid()] = root;
            }
            else _world.AddChild(root);
            _placed.Add(root);
            return root;
        }

        bool Raycast(Vector2 screen, uint mask, out Vector3 point, out Rid collider)
        {
            point = Vector3.Zero; collider = default;
            var from = _cam.ProjectRayOrigin(screen);
            var to = from + _cam.ProjectRayNormal(screen) * 8000f;
            var q = new PhysicsRayQueryParameters3D { From = from, To = to, CollisionMask = mask };
            var hit = GetWorld3D().DirectSpaceState.IntersectRay(q);
            if (hit.Count == 0) return false;
            point = (Vector3)hit["position"]; collider = (Rid)hit["rid"];
            return true;
        }

        void HandleClick(Vector2 screen)
        {
            if (PlaceName != null)   // place mode: drop the prop where the ray meets terrain / another prop
            {
                if (Raycast(screen, TerrainLayer | SmallPropLayer | EditorPickLayer, out var pt, out _))
                    Select(Place(PlaceName, pt, _placeYaw));
            }
            else if (Raycast(screen, EditorPickLayer, out _, out var rid) && _pickToObj.TryGetValue(rid, out var obj))
                Select(obj);
            else Select(null);
        }

        void Select(Node3D obj) { _selected = obj; _gizmo.Attach(obj); RefreshMarker(); }

        void RefreshMarker()
        {
            if (_marker == null)
            {
                _marker = new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = Vector3.One },
                    MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(1f, 0.85f, 0.15f, 0.28f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded },
                    Visible = false,
                };
                AddChild(_marker);
            }
            if (_selected == null) { _marker.Visible = false; return; }
            var mi = _selected.GetChildCount() > 0 ? _selected.GetChild(0) as MeshInstance3D : null;
            var aabb = mi != null ? mi.GetAabb() : new Aabb(Vector3.Zero, Vector3.One);
            var sz = aabb.Size; var wsz = new Vector3(sz.X, sz.Z, sz.Y);   // the ex=270 pitch swaps the mesh's local Y/Z into world height/depth
            _marker.GlobalPosition = _selected.GlobalPosition + _selected.GlobalTransform.Basis * (aabb.Position + aabb.Size * 0.5f);
            _marker.Scale = wsz * 1.06f;
            _marker.Visible = true;
        }

        // (move/rotate on the selection is the source TransformHandles gizmo -- ported next, not an improvised drag)

        public void DeleteSelected()
        {
            if (_selected == null) return;
            _placed.Remove(_selected);
            foreach (var kv in new List<KeyValuePair<Rid, Node3D>>(_pickToObj))
                if (kv.Value == _selected) _pickToObj.Remove(kv.Key);
            _selected.QueueFree();
            Select(null);
        }

        public override void _UnhandledInput(InputEvent ev)
        {
            if (_editor.Mode != EEditorMode.Objects || _flyCam.Flying) return;   // Objects tab only; never while flying (RMB)
            if (ev is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed)
                {
                    if (_gizmo.TryBeginDrag(GetViewport().GetMousePosition())) return;   // grabbed a gizmo axis -> drag, not place/select
                    HandleClick(GetViewport().GetMousePosition());                        // place (build mode) or select (source EditorSelection)
                }
                else if (_gizmo.Dragging) { _gizmo.EndDrag(); RefreshMarker(); }
            }
            else if (ev is InputEventMouseMotion && _gizmo.Dragging)
            {
                _gizmo.DragTo(GetViewport().GetMousePosition(), Input.IsKeyPressed(Key.Ctrl));   // TransformHandles POSITION_AXIS drag; Ctrl = 1u snap
                RefreshMarker();
            }
            else if (ev is InputEventKey { Pressed: true, Echo: false } k)
            {
                if (k.Keycode == Key.Delete || k.Keycode == Key.X) DeleteSelected();   // source: delete the selection
                else if (k.Keycode == Key.Escape) Select(null);
            }
        }

        // harness hook (--editor): scatter a few props so a headless render shows placement working
        public readonly List<Vector3> DemoPositions = new();
        public void DemoPlace()
        {
            if (_catalog.Count == 0) { GD.Print("[editordemo] empty catalog"); return; }
            int n = 0;
            for (int i = 0; i < 6; i++)
                if (Raycast(new Vector2(300 + i * 110, 380), TerrainLayer, out var pt, out _) && Place(_catalog[(i * 7) % _catalog.Count], pt, i * 30f) != null) { DemoPositions.Add(pt); n++; }
            if (_placed.Count > 0) Select(_placed[0]);   // show the translate gizmo on a prop in the render
            GD.Print($"[editordemo] placed {n}/6 props via raycast (catalog {_catalog.Count} types)");
        }
    }
}
