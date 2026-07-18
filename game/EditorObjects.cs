using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // Objects sub-editor (Phase 2), ported from SDG.Unturned EditorObjects. Pick a prop from the catalog, click to
    // PLACE it on the map; click a placed prop to SELECT it; the TransformHandles gizmo moves/rotates it (T cycles
    // translate<->rotate); Del removes it. Placement + picking raycast the editor world's colliders (WorldMode.Editor).
    // Each placed prop is built exactly like WorldBuilder builds the loaded ones (mesh + textured material + trimesh
    // collider). Placements persist to editor_PEI.txt (PEI euler, so any gizmo orientation round-trips) + reload on open.
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
        public bool GizmoLocalSpace => _gizmo?.LocalSpace ?? false;   // dashboard readout
        public string GizmoModeText => (_gizmo?.Mode ?? EditorGizmo.EMode.Translate) switch { EditorGizmo.EMode.Rotate => "rotate", EditorGizmo.EMode.Scale => "scale", _ => "move" };

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
            LoadSaved();   // restore any previously-saved editor placements
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
                if (p.Length < 2) continue;
                if (seen.Add(p[1])) _catalog.Add(p[1]);
                _nameToGuid.TryAdd(p[1], p[0]);   // name -> first guid (for writing placements on save)
                _guidToName.TryAdd(p[0], p[1]);   // guid -> mesh name (for loading placements)
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

        // upright placement basis: meshes are authored lying down, pitch ex=270 stands them up; yaw about world-up
        // (the WorldBuilder convention). Placing yaw-only left every prop flat (master), hence the baked 270 pitch.
        public static Basis Upright(float yawDeg) => new Basis(Vector3.Up, Mathf.DegToRad(yawDeg)) * new Basis(Vector3.Right, Mathf.DegToRad(270f));
        // WorldBuilder placement basis from PEI euler (ex,ey,ez): Basis(Y,180-ey)*Basis(X,ex)*Basis(Z,-ez)
        static Basis FromEuler(float ex, float ey, float ez) =>
            new Basis(new Vector3(0, 1, 0), Mathf.DegToRad(180f - ey)) * new Basis(new Vector3(1, 0, 0), Mathf.DegToRad(ex)) * new Basis(new Vector3(0, 0, 1), Mathf.DegToRad(-ez));

        // Build + add a prop at a world position with a rotation basis. Returns its root Node3D. The gizmo then rotates
        // it freely; Save decomposes the live basis back to PEI euler so any orientation round-trips.
        public Node3D Place(string name, Vector3 pos, Basis rot)
        {
            var mesh = MeshFor(name);
            if (mesh == null) return null;
            var root = new Node3D { Transform = new Transform3D(rot, pos) };
            root.SetMeta("obj_name", name);
            root.SetMeta("guid", _nameToGuid.TryGetValue(name, out var g) ? g : "");   // for the placements save
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
                    Select(Place(PlaceName, pt, Upright(_placeYaw)));
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
                    TopLevel = true,   // we drive its world transform from the selected object directly
                };
                AddChild(_marker);
            }
            if (_selected == null) { _marker.Visible = false; return; }
            var mi = _selected.GetChildCount() > 0 ? _selected.GetChild(0) as MeshInstance3D : null;
            var aabb = mi != null ? mi.GetAabb() : new Aabb(-Vector3.One * 0.5f, Vector3.One);
            // carry the mesh's LOCAL AABB box through the object's FULL transform, so the outline rotates + scales WITH it
            _marker.GlobalTransform = _selected.GlobalTransform * new Transform3D(Basis.FromScale(aabb.Size * 1.06f), aabb.Position + aabb.Size * 0.5f);
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
            if (_editor.Mode != EEditorMode.Level || _flyCam.Flying) return;   // Level tab only (object placement lives under Level); never while flying (RMB)
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
                else if (k.Keycode == Key.T) _gizmo.CycleMode();                        // T = cycle translate/rotate gizmo (source TransformHandles EMode)
                else if (k.Keycode == Key.G) _gizmo.LocalSpace = !_gizmo.LocalSpace;    // G = toggle gizmo local/global space
                else if (k.Keycode == Key.Escape) Select(null);
            }
        }

        static string SavePath => Dir + "editor_PEI.txt";   // editor placements (port format); loaded on open in addition to the baked map

        // Save the editor-placed props in the port's placements format (guid px py pz ex ey ez sx sy sz) so edits
        // persist + reload. The SOURCE persists objects via LevelObjects' binary .level Block; the port loads the baked
        // placements.txt, so this is the translator-to-our-format save master accepted (real binary .level = a later
        // refinement). gpos.Z negates back to pz; ex=270 + ey=180-yaw mirror the load/WorldBuilder convention.
        public int Save()
        {
            using var w = new System.IO.StreamWriter(SavePath, false);
            int n = 0;
            foreach (var p in _placed)
            {
                string guid = p.HasMeta("guid") ? (string)p.GetMeta("guid") : "";
                if (guid.Length == 0) continue;
                var gp = p.GlobalPosition;
                var b = p.GlobalTransform.Basis;
                var (ex, ey, ez) = DecomposeEuler(b.Orthonormalized());   // live basis -> PEI euler (any gizmo rotation)
                var sc = b.Scale;                                          // + gizmo scale (sx sy sz)
                w.WriteLine($"{guid} {gp.X:0.###} {gp.Y:0.###} {(-gp.Z):0.###} {ex:0.###} {ey:0.###} {ez:0.###} {sc.X:0.###} {sc.Y:0.###} {sc.Z:0.###}");
                n++;
            }
            GD.Print($"[editor] saved {n} placed props -> {SavePath}");
            return n;
        }

        // inverse of FromEuler (B = Ry(180-ey)*Rx(ex)*Rz(-ez)); Godot GetEuler(Yxz) gives (x,y,z) with B=Ry(y)Rx(x)Rz(z)
        static (float ex, float ey, float ez) DecomposeEuler(Basis b)
        {
            var e = b.GetEuler(EulerOrder.Yxz);
            return (Mathf.RadToDeg(e.X), 180f - Mathf.RadToDeg(e.Y), -Mathf.RadToDeg(e.Z));
        }

        void LoadSaved()   // restore previously-saved editor placements on open
        {
            if (!System.IO.File.Exists(SavePath)) return;
            int n = 0;
            foreach (var line in System.IO.File.ReadLines(SavePath))
            {
                var p = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 10 || !_guidToName.TryGetValue(p[0], out var name)) continue;
                if (!float.TryParse(p[1], out var px) || !float.TryParse(p[2], out var py) || !float.TryParse(p[3], out var pz)
                    || !float.TryParse(p[4], out var ex) || !float.TryParse(p[5], out var ey) || !float.TryParse(p[6], out var ez)) continue;
                float sx = 1f, sy = 1f, sz = 1f;
                float.TryParse(p[7], out sx); float.TryParse(p[8], out sy); float.TryParse(p[9], out sz);
                if (sx == 0f) sx = 1f; if (sy == 0f) sy = 1f; if (sz == 0f) sz = 1f;
                Place(name, new Vector3(px, py, -pz), FromEuler(ex, ey, ez) * Basis.FromScale(new Vector3(sx, sy, sz)));   // gpos=(px,py,-pz); PEI euler + scale
                n++;
            }
            if (n > 0) GD.Print($"[editor] loaded {n} saved props");
        }

        // harness hook (--editor): scatter a few props so a headless render shows placement working
        public readonly List<Vector3> DemoPositions = new();
        public void DemoPlace()
        {
            if (_catalog.Count == 0) { GD.Print("[editordemo] empty catalog"); return; }
            int n = 0;
            for (int i = 0; i < 6; i++)
                if (Raycast(new Vector2(300 + i * 110, 380), TerrainLayer, out var pt, out _) && Place(_catalog[(i * 7) % _catalog.Count], pt, Upright(i * 30f)) != null) { DemoPositions.Add(pt); n++; }
            // exercise an arbitrary (non-yaw) rotation on the framed prop [0] so the save round-trip covers the rotate
            // gizmo (not just yaw) + self-check the euler decompose<->recompose in one run (catches order mismatch).
            if (_placed.Count > 0)
            {
                var pr = _placed[0];
                var rb = pr.GlobalTransform.Basis.Orthonormalized().Rotated(Vector3.Right, Mathf.DegToRad(24f)).Rotated(Vector3.Up, Mathf.DegToRad(37f)).Rotated(Vector3.Forward, Mathf.DegToRad(32f));
                pr.GlobalTransform = new Transform3D(rb * Basis.FromScale(new Vector3(1.5f, 1.5f, 0.6f)), pr.GlobalPosition);   // arbitrary rotate (incl roll) + non-uniform scale
                var a = pr.GlobalTransform.Basis.Orthonormalized();
                var (ex, ey, ez) = DecomposeEuler(a);
                var re = FromEuler(ex, ey, ez);
                float err = (a.X - re.X).Length() + (a.Y - re.Y).Length() + (a.Z - re.Z).Length();
                var sc = pr.GlobalTransform.Basis.Scale;
                GD.Print($"[editordemo] euler round-trip err={err:0.####} (ex={ex:0.#} ey={ey:0.#} ez={ez:0.#}) scale=({sc.X:0.##},{sc.Y:0.##},{sc.Z:0.##})");
                Select(pr);   // translate-mode gizmo (thin) so the selection outline reads clearly in the render
            }
            GD.Print($"[editordemo] placed {n}/6 props via raycast (catalog {_catalog.Count} types)");
        }
    }
}
