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
        public IReadOnlyList<string> Catalog => _catalog;
        public string PlaceName;   // the prop to place on click; null = select mode

        readonly Dictionary<string, ArrayMesh> _meshCache = new();
        readonly List<Node3D> _placed = new();
        readonly Dictionary<Rid, Node3D> _pickToObj = new();
        Node3D _selected;
        MeshInstance3D _marker;
        float _placeYaw;

        public EditorObjects(Editor editor, Node world, EditorCamera cam)
        {
            _editor = editor; _world = world; _cam = cam; _flyCam = cam;
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
            var root = new Node3D { Position = pos, RotationDegrees = new Vector3(0, yawDeg, 0) };
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

        void Select(Node3D obj)
        {
            _selected = obj;
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
            if (obj == null) { _marker.Visible = false; return; }
            var mi = obj.GetChildCount() > 0 ? obj.GetChild(0) as MeshInstance3D : null;
            var aabb = mi != null ? mi.GetAabb() : new Aabb(Vector3.Zero, Vector3.One);
            _marker.GlobalPosition = obj.GlobalPosition + obj.GlobalTransform.Basis * (aabb.Position + aabb.Size * 0.5f);
            _marker.Scale = aabb.Size * 1.04f;
            _marker.Visible = true;
        }

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
            if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
                HandleClick(GetViewport().GetMousePosition());
            else if (ev is InputEventKey { Pressed: true, Echo: false } k)
            {
                if (k.Keycode == Key.Delete || k.Keycode == Key.X) DeleteSelected();
                else if (k.Keycode == Key.R) { _placeYaw = (_placeYaw + 45f) % 360f; }   // rotate the next placement (and the selected, if any)
                else if (k.Keycode == Key.Escape) Select(null);
            }
        }

        // harness hook (--editor): scatter a few props so a headless render shows placement working
        public void DemoPlace()
        {
            if (_catalog.Count == 0) { GD.Print("[editordemo] empty catalog"); return; }
            int n = 0;
            for (int i = 0; i < 6; i++)
                if (Raycast(new Vector2(300 + i * 110, 380), TerrainLayer, out var pt, out _) && Place(_catalog[(i * 7) % _catalog.Count], pt, i * 30f) != null) n++;
            GD.Print($"[editordemo] placed {n}/6 props via raycast (catalog {_catalog.Count} types)");
        }
    }
}
