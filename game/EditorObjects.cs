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
        readonly List<Node3D> _selection = new();                      // multi-select (source EditorObjects.selection list)
        readonly List<MeshInstance3D> _markers = new();                // one yellow outline per selected object
        readonly List<(string guid, Transform3D x)> _copies = new();   // source EditorObjects.copies (Ctrl+C / Ctrl+V)
        EditorGizmo _gizmo;
        float _placeYaw;
        Node3D Primary => _selection.Count > 0 ? _selection[_selection.Count - 1] : null;   // gizmo target = most-recent selected
        bool _boxDragging;         // source: drag over empty ground = marquee box-select
        Vector2 _boxStart;
        MarqueeOverlay _marquee;
        readonly List<Transform3D> _groupRel = new();   // each selected's transform relative to Primary, captured at gizmo drag-start
        List<(Node3D n, Transform3D x)> _dragCapture;   // selection transforms at gizmo drag-start, for the move undo

        // --- undo helpers (Editor.PushUndo / Ctrl+Z) ---
        List<(Node3D n, Transform3D x)> CaptureSelection()
        {
            var l = new List<(Node3D, Transform3D)>();
            foreach (var s in _selection) l.Add((s, s.GlobalTransform));
            return l;
        }
        void RestoreTransforms(List<(Node3D n, Transform3D x)> cap)
        {
            foreach (var (n, x) in cap) if (IsInstanceValid(n)) n.GlobalTransform = x;
            if (Primary != null) { _gizmo.Attach(Primary); PositionMarkers(); }
        }
        void RemoveProp(Node3D n)   // fully detach a placed prop (used by delete + place/paste undo)
        {
            if (n == null) return;
            _placed.Remove(n); _selection.Remove(n);
            foreach (var kv in new List<KeyValuePair<Rid, Node3D>>(_pickToObj)) if (kv.Value == n) _pickToObj.Remove(kv.Key);
            if (IsInstanceValid(n)) n.QueueFree();
            _gizmo.Attach(Primary); RefreshMarkers();
        }
        Node3D RePlace(string guid, Transform3D x) => _guidToName.TryGetValue(guid, out var nm) ? Place(nm, x.Origin, x.Basis) : null;

        public EditorObjects(Editor editor, Node world, EditorCamera cam)
        {
            _editor = editor; _world = world; _cam = cam; _flyCam = cam;
            _gizmo = new EditorGizmo(cam); AddChild(_gizmo);   // the source TransformHandles translate gizmo, shown on the selection
            var cl = new CanvasLayer { Layer = 55 };            // marquee overlay for box drag-select
            _marquee = new MarqueeOverlay { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
            _marquee.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            cl.AddChild(_marquee); AddChild(cl);
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
            _catalog.Insert(0, LootCrateName);   // loot crate pinned to the top of the palette
            _catalog.Insert(1, StoreShelfName);  // store shelf right below it
            _catalog.Insert(2, GridPowerName);   // grid power box below that
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
            if (name.StartsWith("Glass"))   // source glass = shader-based transparent; give it a see-through look (matches WorldBuilder.MatFor)
                return new StandardMaterial3D
                {
                    AlbedoColor = new Color(0.62f, 0.73f, 0.78f, 0.26f),
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    Metallic = 0f, Roughness = 0.06f, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                };
            var mm = new StandardMaterial3D { Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled, VertexColorUseAsAlbedo = true };
            string tp = Dir + name + "_tex.png";
            var img = new Image();
            if (System.IO.File.Exists(tp) && img.Load(tp) == Error.Ok)
            {
                if (img.GetFormat() == Image.Format.Rgba8)   // leaf/foliage cutout: real transparency (>0.25% of texels) -> alpha-scissor (matches WorldBuilder)
                {
                    var data = img.GetData(); int tr = 0;
                    for (int i = 3; i < data.Length; i += 4) if (data[i] < 200) tr++;
                    if (tr > data.Length / 400) { mm.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor; mm.AlphaScissorThreshold = 0.5f; }
                }
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
        public const string LootCrateName = "★ Loot Crate";   // a placeable loot CONTAINER (not a mesh prop) -- rolls a PEI table in SP
        public const string StoreShelfName = "🛒 Store Shelf";   // the real Shelf_1 gondola AS a loot container -- rolls a PEI table + shows items on its tiers in SP
        public const string GridPowerName = "⚡ Grid Power";   // the Circuit_0 breaker box AS a configurable mains SOURCE -- name + wattage (custom or preset) set in the editor, spawns a GridPowerSource in SP

        public Node3D Place(string name, Vector3 pos, Basis rot)
        {
            if (name == LootCrateName) return PlaceLootCrate(pos, rot);
            if (name == StoreShelfName) return PlaceStoreShelf(pos, rot);
            if (name == GridPowerName) return PlaceGridPower(pos, rot);
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

        // a placeable loot CONTAINER marker (box) tagged with its PEI table; the SP loader spawns a real LootCrate here.
        Node3D PlaceLootCrate(Vector3 pos, Basis rot)
        {
            var root = new Node3D { Transform = new Transform3D(rot, pos) };
            root.SetMeta("loot_crate", true);
            root.SetMeta("loot_table", 0);   // default PEI table (retable later)
            root.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.75f, 0.75f, 0.75f) }, Position = new Vector3(0f, 0.375f, 0f), MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.42f, 0.2f), Roughness = 0.9f } });
            root.AddChild(new Label3D { Text = CrateLabelText(0), Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, PixelSize = 0.006f, Position = new Vector3(0f, 1.05f, 0f), Modulate = new Color(1f, 0.85f, 0.4f), NoDepthTest = true, FontSize = 40, OutlineSize = 8 });
            var body = new StaticBody3D { CollisionLayer = EditorPickLayer, CollisionMask = 0, Position = new Vector3(0f, 0.375f, 0f) };
            body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.75f, 0.75f, 0.75f) } });
            root.AddChild(body);
            _world.AddChild(root);
            _pickToObj[body.GetRid()] = root;
            _placed.Add(root);
            return root;
        }

        // the real Shelf_1 gondola placed AS a loot container: stands upright (270 X on the mesh, yaw on the root, matching
        // StoreShelf in SP), tagged with a PEI table; the SP loader spawns a real StoreShelf (items shown on the tiers) here.
        Node3D PlaceStoreShelf(Vector3 pos, Basis rot)
        {
            var mesh = MeshFor("Shelf_1");
            if (mesh == null) return null;
            float yaw = Mathf.Atan2(-rot.X.Z, rot.X.X);   // recover yaw from the Upright(yaw) basis the placer passes
            var stand = new Basis(Vector3.Right, Mathf.DegToRad(270f));
            var root = new Node3D { Transform = new Transform3D(new Basis(Vector3.Up, yaw), pos) };
            root.SetMeta("store_shelf", true);
            root.SetMeta("loot_table", 0);
            root.AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = MatFor("Shelf_1"), Basis = stand });
            root.AddChild(new Label3D { Text = ContainerLabelText("Store Shelf", 0), Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, PixelSize = 0.007f, Position = new Vector3(0f, 2.8f, 0f), Modulate = new Color(0.8f, 0.85f, 0.95f), NoDepthTest = true, FontSize = 40, OutlineSize = 8 });
            var shp = mesh.CreateTrimeshShape();
            if (shp != null)
            {
                var body = new StaticBody3D { CollisionLayer = EditorPickLayer, CollisionMask = 0, Basis = stand };
                body.AddChild(new CollisionShape3D { Shape = shp });
                root.AddChild(body);
                _pickToObj[body.GetRid()] = root;
            }
            _world.AddChild(root);
            _placed.Add(root);
            return root;
        }

        static string CrateLabelText(int table) => ContainerLabelText("Loot Crate", table);
        static string ContainerLabelText(string prefix, int table) => $"{prefix}\n[{LootTables.TableName(table)}]";
        void UpdateCrateLabel(Node3D container)
        {
            int tbl = container.HasMeta("loot_table") ? (int)container.GetMeta("loot_table") : 0;
            string prefix = container.HasMeta("store_shelf") ? "Store Shelf" : "Loot Crate";
            foreach (var c in container.GetChildren()) if (c is Label3D lbl) lbl.Text = ContainerLabelText(prefix, tbl);
        }
        public bool CrateSelected => Primary != null && Primary.HasMeta("loot_table");   // any loot container (crate OR shelf) -> the table dropdown applies
        public System.Action SelectionChanged;   // the browser watches this to show/sync the loot-table dropdown
        public int SelectedCrateTable => CrateSelected && Primary.HasMeta("loot_table") ? (int)Primary.GetMeta("loot_table") : 0;
        public void SetSelectedCrateTable(int t)   // dropdown -> set the selected container's table
        {
            if (!CrateSelected) return;
            Primary.SetMeta("loot_table", Mathf.Clamp(t, 0, System.Math.Max(0, LootTables.TableCount - 1)));
            UpdateCrateLabel(Primary);
        }

        // the Circuit_0 breaker box placed AS a mains SOURCE: the real mesh, tagged with a wattage + name; the SP loader
        // spawns a real GridPowerSource (wire-able output + generator UI) here. Config (name/wattage/preset) via the browser.
        Node3D PlaceGridPower(Vector3 pos, Basis rot)
        {
            var mesh = MeshFor("Circuit_0");
            if (mesh == null) return null;
            float yaw = Mathf.Atan2(-rot.X.Z, rot.X.X);
            var stand = new Basis(Vector3.Right, Mathf.DegToRad(270f));   // flat-authored (Z=height) -> stand it up, same as the shelf
            var root = new Node3D { Transform = new Transform3D(new Basis(Vector3.Up, yaw), pos) };
            root.SetMeta("grid_power", true);
            root.SetMeta("grid_watts", GridPowerSource.DefaultWatts);
            root.SetMeta("grid_name", "");
            root.AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = MatFor("Circuit_0"), Basis = stand });
            root.AddChild(new Label3D { Text = GridLabelText("", GridPowerSource.DefaultWatts), Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, PixelSize = 0.007f, Position = new Vector3(0f, 2.4f, 0f), Modulate = new Color(0.5f, 0.85f, 1f), NoDepthTest = true, FontSize = 40, OutlineSize = 8 });
            var shp = mesh.CreateTrimeshShape();
            if (shp != null)
            {
                var body = new StaticBody3D { CollisionLayer = EditorPickLayer, CollisionMask = 0, Basis = stand };
                body.AddChild(new CollisionShape3D { Shape = shp });
                root.AddChild(body);
                _pickToObj[body.GetRid()] = root;
            }
            _world.AddChild(root);
            _placed.Add(root);
            return root;
        }

        static string GridLabelText(string name, float watts) => $"⚡ Grid Power\n{(string.IsNullOrEmpty(name) ? "Unnamed" : name)}: {watts:0}W";
        void UpdateGridLabel(Node3D box)
        {
            string nm = box.HasMeta("grid_name") ? (string)box.GetMeta("grid_name") : "";
            float w = box.HasMeta("grid_watts") ? (float)box.GetMeta("grid_watts") : GridPowerSource.DefaultWatts;
            foreach (var c in box.GetChildren()) if (c is Label3D lbl) lbl.Text = GridLabelText(nm, w);
        }
        public bool GridSelected => Primary != null && Primary.HasMeta("grid_power");
        public float SelectedGridWatts => GridSelected && Primary.HasMeta("grid_watts") ? (float)Primary.GetMeta("grid_watts") : GridPowerSource.DefaultWatts;
        public string SelectedGridName => GridSelected && Primary.HasMeta("grid_name") ? (string)Primary.GetMeta("grid_name") : "";
        public void SetSelectedGridWatts(float w)
        {
            if (!GridSelected) return;
            Primary.SetMeta("grid_watts", Mathf.Clamp(w, 1f, 100000000f));
            UpdateGridLabel(Primary);
        }
        public void SetSelectedGridName(string n)
        {
            if (!GridSelected) return;
            Primary.SetMeta("grid_name", n ?? "");
            UpdateGridLabel(Primary);
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

        // source EditorObjects: LMB SELECTS the prop under the cursor (never places -- placement is E, master). Shift toggles
        // into the multi-selection. Returns true if a prop was selected; false if empty ground (caller arms box drag-select).
        bool TrySelect(Vector2 screen)
        {
            bool additive = Input.IsKeyPressed(Key.Shift);
            if (Raycast(screen, EditorPickLayer, out _, out var rid) && _pickToObj.TryGetValue(rid, out var obj))
                { Select(obj, additive); return true; }
            return false;   // clicked empty ground
        }

        // source tool_2 (E): with a prop selected, move the whole selection to the cursor; else summon the list-selected prop.
        void PlaceOrMoveAtCursor()
        {
            if (Editor.PointerOverUI(this)) return;
            var mp = GetViewport().GetMousePosition();
            if (!Raycast(mp, TerrainLayer | SmallPropLayer | EditorPickLayer, out var pt, out _)) return;
            if (Primary != null)   // E with a prop selected -> move it to the cursor
            {
                var cap = CaptureSelection();
                var delta = pt - Primary.GlobalPosition;
                foreach (var s in _selection) s.GlobalPosition += delta;
                _gizmo.Attach(Primary); PositionMarkers();
                _editor.PushUndo("move", () => RestoreTransforms(cap));
            }
            else if (PlaceName != null)   // E with only a list type -> summon one (stays unselected so E keeps placing)
            {
                var n = Place(PlaceName, pt, Upright(_placeYaw));
                if (n != null) _editor.PushUndo("place", () => RemoveProp(n));
            }
        }

        // browser list-click: arm this prop type for E-placement + clear any instance selection (so E summons, not moves)
        public void SetPlaceType(string name) { PlaceName = name; Select(null); }
        public void ClearPlaceType() { PlaceName = null; }   // "select/move only" button

        // select obj; additive (Shift) toggles it in the multi-selection, else it replaces the selection; null clears
        void Select(Node3D obj, bool additive = false)
        {
            if (obj == null) { if (!additive) _selection.Clear(); }
            else if (additive) { if (!_selection.Remove(obj)) _selection.Add(obj); }
            else { _selection.Clear(); _selection.Add(obj); }
            _gizmo.Attach(Primary);
            RefreshMarkers();
        }

        MeshInstance3D NewMarker() => new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = Vector3.One },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(1f, 0.85f, 0.15f, 0.28f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded },
            TopLevel = true,   // we drive its world transform from the selected object directly
        };

        void RefreshMarkers()   // selection changed: one yellow outline per selected object
        {
            foreach (var m in _markers) m.QueueFree();
            _markers.Clear();
            foreach (var _ in _selection) { var mk = NewMarker(); AddChild(mk); _markers.Add(mk); }
            PositionMarkers();
            SelectionChanged?.Invoke();
        }

        void PositionMarkers()   // each frame / after a transform: hug each selected object's FULL transform (rotate+scale)
        {
            for (int i = 0; i < _markers.Count && i < _selection.Count; i++)
            {
                var sel = _selection[i];
                var mi = sel.GetChildCount() > 0 ? sel.GetChild(0) as MeshInstance3D : null;
                var aabb = mi != null ? mi.GetAabb() : new Aabb(-Vector3.One * 0.5f, Vector3.One);
                _markers[i].GlobalTransform = sel.GlobalTransform * new Transform3D(Basis.FromScale(aabb.Size * 1.06f), aabb.Position + aabb.Size * 0.5f);
            }
        }

        public void DeleteSelected()   // source: delete the whole selection
        {
            if (_selection.Count == 0) return;
            var cap = new List<(string guid, Transform3D x)>();   // for undo: re-place the deleted props
            foreach (var sel in _selection) { string g = sel.HasMeta("guid") ? (string)sel.GetMeta("guid") : ""; if (g.Length > 0) cap.Add((g, sel.GlobalTransform)); }
            foreach (var sel in new List<Node3D>(_selection))
            {
                _placed.Remove(sel);
                foreach (var kv in new List<KeyValuePair<Rid, Node3D>>(_pickToObj))
                    if (kv.Value == sel) _pickToObj.Remove(kv.Key);
                sel.QueueFree();
            }
            Select(null);
            if (cap.Count > 0) _editor.PushUndo("delete", () =>
            {
                _selection.Clear();
                foreach (var (g, x) in cap) { var nn = RePlace(g, x); if (nn != null) _selection.Add(nn); }
                _gizmo.Attach(Primary); RefreshMarkers();
            });
        }

        // source EditorObjects Ctrl+C/Ctrl+V: copy stores each selected prop's (guid + world transform); paste re-creates
        // them at the same spot + selects the new ones (you then move the fresh copies off the originals).
        void CopySelection()
        {
            _copies.Clear();
            foreach (var sel in _selection)
            {
                string g = sel.HasMeta("guid") ? (string)sel.GetMeta("guid") : "";
                if (g.Length > 0) _copies.Add((g, sel.GlobalTransform));
            }
            GD.Print($"[editor] copied {_copies.Count} prop(s)");
        }

        void PasteSelection()
        {
            if (_copies.Count == 0) return;
            _selection.Clear();
            var pasted = new List<Node3D>();
            foreach (var (g, x) in _copies)
                if (_guidToName.TryGetValue(g, out var name)) { var nn = Place(name, x.Origin, x.Basis); if (nn != null) { _selection.Add(nn); pasted.Add(nn); } }
            _gizmo.Attach(Primary); RefreshMarkers();
            if (pasted.Count > 0) _editor.PushUndo("paste", () => { foreach (var n in pasted) RemoveProp(n); });
            GD.Print($"[editor] pasted {_selection.Count} prop(s)");
        }

        static Rect2 RectFrom(Vector2 a, Vector2 b) => new Rect2(new Vector2(Mathf.Min(a.X, b.X), Mathf.Min(a.Y, b.Y)), (b - a).Abs());

        void UpdateMarquee(Vector2 a, Vector2 b) { _marquee.Rect = RectFrom(a, b); _marquee.Visible = true; _marquee.QueueRedraw(); }

        void FinishBoxSelect(Vector2 end)
        {
            bool additive = Input.IsKeyPressed(Key.Shift);
            var rect = RectFrom(_boxStart, end);
            if (rect.Size.Length() < 8f) { if (!additive) Select(null); return; }   // basically a click on empty -> deselect
            BoxSelect(rect, additive);
        }

        // source: select every placed prop whose on-screen point (WorldToViewportPoint) falls inside the marquee rect
        void BoxSelect(Rect2 rect, bool additive)
        {
            if (!additive) _selection.Clear();
            foreach (var prop in _placed)
            {
                if (_cam.IsPositionBehind(prop.GlobalPosition)) continue;
                if (rect.HasPoint(_cam.UnprojectPosition(prop.GlobalPosition)) && !_selection.Contains(prop)) _selection.Add(prop);
            }
            _gizmo.Attach(Primary); RefreshMarkers();
            GD.Print($"[editor] box-select: {_selection.Count} selected");
        }

        // group-gizmo: the gizmo drives Primary; the rest of the multi-selection rigidly follows its transform delta
        void BeginGroupDrag()
        {
            _groupRel.Clear();
            if (_selection.Count <= 1 || Primary == null) return;
            var inv = Primary.GlobalTransform.AffineInverse();
            foreach (var s in _selection) _groupRel.Add(inv * s.GlobalTransform);
        }
        void ApplyGroupDrag()
        {
            if (_groupRel.Count != _selection.Count || Primary == null) return;
            var px = Primary.GlobalTransform;
            for (int i = 0; i < _selection.Count; i++) if (_selection[i] != Primary) _selection[i].GlobalTransform = px * _groupRel[i];
        }

        public override void _UnhandledInput(InputEvent ev)
        {
            if (_editor.Mode != EEditorMode.Level || _flyCam.Flying) return;   // Level tab only (object placement lives under Level); never while flying (RMB)
            if (ev is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
            {
                var mp = GetViewport().GetMousePosition();
                if (mb.Pressed)
                {
                    if (Editor.PointerOverUI(this)) return;                                  // clicking the dashboard/browser must not fire tools into the world
                    if (_gizmo.TryBeginDrag(mp)) { BeginGroupDrag(); _dragCapture = CaptureSelection(); return; }   // gizmo grab -> drag (+ capture for undo)
                    if (!TrySelect(mp)) { _boxDragging = true; _boxStart = mp; }            // click selects a prop; empty ground -> arm a box drag-select
                }
                else if (_gizmo.Dragging) { _gizmo.EndDrag(); PositionMarkers(); if (_dragCapture != null) { var c = _dragCapture; _dragCapture = null; _editor.PushUndo("move", () => RestoreTransforms(c)); } }
                else if (_boxDragging) { FinishBoxSelect(mp); _boxDragging = false; _marquee.Visible = false; }
            }
            else if (ev is InputEventMouseMotion)
            {
                if (_gizmo.Dragging) { _gizmo.DragTo(GetViewport().GetMousePosition(), Input.IsKeyPressed(Key.Ctrl)); ApplyGroupDrag(); PositionMarkers(); }   // TransformHandles drag (+ carry the group); Ctrl = snap
                else if (_boxDragging) UpdateMarquee(_boxStart, GetViewport().GetMousePosition());   // source marquee drag-select
            }
            else if (ev is InputEventKey { Pressed: true, Echo: false } k)
            {
                bool ctrl = Input.IsKeyPressed(Key.Ctrl);
                if (k.Keycode == Key.Delete || k.Keycode == Key.Backspace) DeleteSelected();   // source: delete the selection
                else if (ctrl && k.Keycode == Key.C) CopySelection();                    // Ctrl+C copy objects (source EditorObjects)
                else if (ctrl && k.Keycode == Key.V) PasteSelection();                   // Ctrl+V paste objects (source EditorObjects)
                else if (ctrl && k.Keycode == Key.B) CopyTransform();                    // Ctrl+B copy transform (source)
                else if (ctrl && k.Keycode == Key.N) PasteTransform();                   // Ctrl+N paste transform (source)
                else if (ctrl && k.Keycode == Key.Z) _editor.Undo();                      // Ctrl+Z undo (source EditorInteract)
                else if (k.Keycode == Key.E) PlaceOrMoveAtCursor();                     // E = source tool_2: move the selection to the cursor, or summon the list-selected prop
                else if (k.Keycode == Key.T) _gizmo.CycleMode();                        // T = cycle translate/rotate/scale gizmo (source TransformHandles EMode)
                else if (k.Keycode == Key.G) _gizmo.LocalSpace = !_gizmo.LocalSpace;    // G = toggle gizmo local/global space
                else if (k.Keycode == Key.Escape) Select(null);
            }
        }

        string SavePath => Dir + $"editor_{_editor.MapName}.txt";   // per-map editor placements (port format); loaded on open

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
            SaveLootCrates();
            SaveStoreShelves();
            SaveGridPower();
            GD.Print($"[editor] saved {n} placed props -> {SavePath}");
            return n;
        }

        string CratesPath => Dir + $"editor_{_editor.MapName}_crates.txt";   // per-map loot-crate placements (table + world pos)
        void SaveLootCrates()
        {
            var crates = _placed.FindAll(p => IsInstanceValid(p) && p.HasMeta("loot_crate"));
            using var cw = new System.IO.StreamWriter(CratesPath, false);
            foreach (var c in crates)
            {
                int tbl = c.HasMeta("loot_table") ? (int)c.GetMeta("loot_table") : 0;
                var gp = c.GlobalPosition;
                cw.WriteLine($"{tbl} {gp.X:0.###} {gp.Y:0.###} {(-gp.Z):0.###}");   // gpos.Z negates back (map convention)
            }
            if (crates.Count > 0) GD.Print($"[editor] saved {crates.Count} loot crates -> {CratesPath}");
        }

        // inverse of FromEuler (B = Ry(180-ey)*Rx(ex)*Rz(-ez)); Godot GetEuler(Yxz) gives (x,y,z) with B=Ry(y)Rx(x)Rz(z)
        static (float ex, float ey, float ez) DecomposeEuler(Basis b)
        {
            var e = b.GetEuler(EulerOrder.Yxz);
            return (Mathf.RadToDeg(e.X), 180f - Mathf.RadToDeg(e.Y), -Mathf.RadToDeg(e.Z));
        }

        void LoadSaved()   // restore previously-saved editor placements on open
        {
            LoadLootCrates();
            LoadStoreShelves();
            LoadGridPower();
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

        void LoadLootCrates()   // restore placed loot crates (markers) on open
        {
            if (!System.IO.File.Exists(CratesPath)) return;
            int n = 0;
            foreach (var line in System.IO.File.ReadLines(CratesPath))
            {
                var p = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 4 || !int.TryParse(p[0], out var tbl)
                    || !float.TryParse(p[1], out var px) || !float.TryParse(p[2], out var py) || !float.TryParse(p[3], out var pz)) continue;
                var root = PlaceLootCrate(new Vector3(px, py, -pz), Basis.Identity);
                root.SetMeta("loot_table", tbl);
                UpdateCrateLabel(root);
                n++;
            }
            if (n > 0) GD.Print($"[editor] loaded {n} loot crates");
        }

        string ShelvesPath => Dir + $"editor_{_editor.MapName}_shelves.txt";   // per-map store-shelf placements (table + world pos + yaw)
        void SaveStoreShelves()
        {
            var shelves = _placed.FindAll(p => IsInstanceValid(p) && p.HasMeta("store_shelf"));
            using var sw = new System.IO.StreamWriter(ShelvesPath, false);
            foreach (var s in shelves)
            {
                int tbl = s.HasMeta("loot_table") ? (int)s.GetMeta("loot_table") : 0;
                var gp = s.GlobalPosition;
                float yawDeg = Mathf.RadToDeg(s.GlobalTransform.Basis.GetEuler().Y);   // yaw-only root
                sw.WriteLine($"{tbl} {gp.X:0.###} {gp.Y:0.###} {(-gp.Z):0.###} {yawDeg:0.###}");
            }
            if (shelves.Count > 0) GD.Print($"[editor] saved {shelves.Count} store shelves -> {ShelvesPath}");
        }
        void LoadStoreShelves()   // restore placed store shelves (markers) on open
        {
            if (!System.IO.File.Exists(ShelvesPath)) return;
            int n = 0;
            foreach (var line in System.IO.File.ReadLines(ShelvesPath))
            {
                var p = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 4 || !int.TryParse(p[0], out var tbl)
                    || !float.TryParse(p[1], out var px) || !float.TryParse(p[2], out var py) || !float.TryParse(p[3], out var pz)) continue;
                float yawDeg = 0f; if (p.Length >= 5) float.TryParse(p[4], out yawDeg);
                var root = PlaceStoreShelf(new Vector3(px, py, -pz), Upright(yawDeg));
                if (root != null) { root.SetMeta("loot_table", tbl); UpdateCrateLabel(root); }
                n++;
            }
            if (n > 0) GD.Print($"[editor] loaded {n} store shelves");
        }

        string GridPath => Dir + $"editor_{_editor.MapName}_gridpower.txt";   // per-map grid-power boxes (watts + world pos + yaw + name)
        void SaveGridPower()
        {
            var boxes = _placed.FindAll(p => IsInstanceValid(p) && p.HasMeta("grid_power"));
            using var w = new System.IO.StreamWriter(GridPath, false);
            foreach (var b in boxes)
            {
                float watts = b.HasMeta("grid_watts") ? (float)b.GetMeta("grid_watts") : GridPowerSource.DefaultWatts;
                string nm = b.HasMeta("grid_name") ? (string)b.GetMeta("grid_name") : "";
                var gp = b.GlobalPosition;
                float yawDeg = Mathf.RadToDeg(b.GlobalTransform.Basis.GetEuler().Y);
                w.WriteLine($"{watts:0.###} {gp.X:0.###} {gp.Y:0.###} {(-gp.Z):0.###} {yawDeg:0.###} {nm}");   // name LAST (may contain spaces)
            }
            if (boxes.Count > 0) GD.Print($"[editor] saved {boxes.Count} grid-power boxes -> {GridPath}");
        }
        void LoadGridPower()
        {
            if (!System.IO.File.Exists(GridPath)) return;
            int n = 0;
            foreach (var line in System.IO.File.ReadLines(GridPath))
            {
                var p = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 5 || !float.TryParse(p[0], out var watts)
                    || !float.TryParse(p[1], out var px) || !float.TryParse(p[2], out var py) || !float.TryParse(p[3], out var pz)) continue;
                float yawDeg = 0f; float.TryParse(p[4], out yawDeg);
                string nm = p.Length >= 6 ? string.Join(" ", p, 5, p.Length - 5) : "";
                var root = PlaceGridPower(new Vector3(px, py, -pz), Upright(yawDeg));
                if (root != null) { root.SetMeta("grid_watts", watts); root.SetMeta("grid_name", nm); UpdateGridLabel(root); }
                n++;
            }
            if (n > 0) GD.Print($"[editor] loaded {n} grid-power boxes");
        }

        // source Ctrl+B / Ctrl+N: copy the selection pivot's TRANSFORM, then stamp it onto another selection (align props)
        Vector3 _copyPos; Basis _copyBasis; bool _hasCopyXform, _copyFull;
        void CopyTransform()   // source: rot/scale copied only in LOCAL space (hasCopiedRotation = dragCoordinate==LOCAL); GLOBAL = position only
        {
            if (Primary == null) return;
            _copyPos = Primary.GlobalPosition; _copyBasis = Primary.GlobalTransform.Basis;
            _copyFull = _gizmo.LocalSpace; _hasCopyXform = true;
            GD.Print($"[editor] copied transform ({(_copyFull ? "pos+rot+scale, local" : "position only, global")})");
        }
        void PasteTransform()
        {
            if (!_hasCopyXform || _selection.Count == 0) return;
            if (_copyFull && _selection.Count == 1)
                _selection[0].GlobalTransform = new Transform3D(_copyBasis, _copyPos);   // local + single: align pos+rot+scale exactly
            else   // global (position only), or a group: move the selection so the primary lands on the copied position
            {
                var delta = _copyPos - Primary.GlobalPosition;
                foreach (var s in _selection) s.GlobalPosition += delta;
            }
            _gizmo.Attach(Primary); PositionMarkers();
            GD.Print($"[editor] pasted transform ({(_copyFull ? "full" : "position")}) to {_selection.Count}");
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
                // verify multi-select + copy/paste programmatically (headless can't drive real clicks)
                if (_placed.Count > 3)
                {
                    Select(_placed[2], false); Select(_placed[3], true);   // Shift-add -> 2 selected
                    int sel = _selection.Count; CopySelection();
                    int before = _placed.Count; PasteSelection();
                    GD.Print($"[editordemo] multi-select={sel}, pasted {_placed.Count - before} (now {_selection.Count} selected)");
                }
                BoxSelect(new Rect2(Vector2.Zero, new Vector2(100000f, 100000f)), false);   // verify box drag-select: full-viewport rect selects every in-frame prop
                GD.Print($"[editordemo] box-select all-in-frame: {_selection.Count}");
                if (_placed.Count > 3)   // verify group-gizmo: moving Primary carries the rest of the selection rigidly
                {
                    Select(_placed[2], false); Select(_placed[3], true);
                    var os = _placed[2].GlobalPosition; BeginGroupDrag();
                    Primary.GlobalPosition += new Vector3(10f, 0f, 0f); ApplyGroupDrag();
                    GD.Print($"[editordemo] group-drag: other followed {(_placed[2].GlobalPosition - os).Length():0.#} (expect ~10)");
                }
                Select(pr, false);   // single-select the transformed prop for a clean outline in the render
                _gizmo.LocalSpace = false; CopyTransform();   // verify: global Ctrl+B = position only
                _gizmo.LocalSpace = true; CopyTransform();    // verify: local Ctrl+B = pos+rot+scale
                _gizmo.LocalSpace = false;                    // back to global for the render
            }
            // undo self-test (headless can't press Ctrl+Z): place a prop, push its undo, undo it, confirm it's gone
            if (_catalog.Count > 0 && Raycast(new Vector2(640, 400), TerrainLayer, out var up, out _))
            {
                var t = Place(_catalog[0], up, Upright(0f));
                if (t != null) { _editor.PushUndo("test", () => RemoveProp(t)); int b = _placed.Count; _editor.Undo(); GD.Print($"[editordemo] undo self-test: {b} -> {_placed.Count} placed (expect {b - 1})"); }
            }
            GD.Print($"[editordemo] placed {n}/6 props via raycast (catalog {_catalog.Count} types)");
        }
    }

    // 2D marquee rectangle drawn during a box drag-select
    public partial class MarqueeOverlay : Control
    {
        public Rect2 Rect;
        public override void _Draw()
        {
            DrawRect(Rect, new Color(0.3f, 0.6f, 1f, 0.14f), true);
            DrawRect(Rect, new Color(0.55f, 0.8f, 1f, 0.9f), false, 1.5f);
        }
    }
}
