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

        // returns true if the click placed or selected a prop; false if it hit empty ground (caller arms a box drag-select)
        bool TryPlaceOrSelect(Vector2 screen)
        {
            bool additive = Input.IsKeyPressed(Key.Shift);   // source 'modify' key -> toggle into the multi-selection
            if (PlaceName != null && !additive)   // place mode: drop the prop where the ray meets terrain / another prop
            {
                if (Raycast(screen, TerrainLayer | SmallPropLayer | EditorPickLayer, out var pt, out _))
                    Select(Place(PlaceName, pt, Upright(_placeYaw)));
                return true;   // place mode consumes the click
            }
            if (Raycast(screen, EditorPickLayer, out _, out var rid) && _pickToObj.TryGetValue(rid, out var obj))
                { Select(obj, additive); return true; }   // Shift+click toggles; plain click single-selects
            return false;   // clicked empty ground
        }

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
            foreach (var sel in new List<Node3D>(_selection))
            {
                _placed.Remove(sel);
                foreach (var kv in new List<KeyValuePair<Rid, Node3D>>(_pickToObj))
                    if (kv.Value == sel) _pickToObj.Remove(kv.Key);
                sel.QueueFree();
            }
            Select(null);
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
            foreach (var (g, x) in _copies)
                if (_guidToName.TryGetValue(g, out var name)) { var nn = Place(name, x.Origin, x.Basis); if (nn != null) _selection.Add(nn); }
            _gizmo.Attach(Primary); RefreshMarkers();
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

        public override void _UnhandledInput(InputEvent ev)
        {
            if (_editor.Mode != EEditorMode.Level || _flyCam.Flying) return;   // Level tab only (object placement lives under Level); never while flying (RMB)
            if (ev is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
            {
                var mp = GetViewport().GetMousePosition();
                if (mb.Pressed)
                {
                    if (_gizmo.TryBeginDrag(mp)) return;                                    // grabbed a gizmo axis -> drag
                    if (!TryPlaceOrSelect(mp)) { _boxDragging = true; _boxStart = mp; }     // empty ground -> arm a box drag-select
                }
                else if (_gizmo.Dragging) { _gizmo.EndDrag(); PositionMarkers(); }
                else if (_boxDragging) { FinishBoxSelect(mp); _boxDragging = false; _marquee.Visible = false; }
            }
            else if (ev is InputEventMouseMotion)
            {
                if (_gizmo.Dragging) { _gizmo.DragTo(GetViewport().GetMousePosition(), Input.IsKeyPressed(Key.Ctrl)); PositionMarkers(); }   // TransformHandles drag; Ctrl = snap
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
                else if (k.Keycode == Key.T) _gizmo.CycleMode();                        // T = cycle translate/rotate/scale gizmo (source TransformHandles EMode)
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

        // source Ctrl+B / Ctrl+N: copy the selection pivot's TRANSFORM, then stamp it onto another selection (align props)
        Vector3 _copyPos; Basis _copyBasis; bool _hasCopyXform;
        void CopyTransform()
        {
            if (Primary == null) return;
            _copyPos = Primary.GlobalPosition; _copyBasis = Primary.GlobalTransform.Basis; _hasCopyXform = true;
            GD.Print("[editor] copied transform");
        }
        void PasteTransform()
        {
            if (!_hasCopyXform || _selection.Count == 0) return;
            if (_selection.Count == 1) _selection[0].GlobalTransform = new Transform3D(_copyBasis, _copyPos);   // align exactly (source count==1)
            else { var delta = _copyPos - Primary.GlobalPosition; foreach (var s in _selection) s.GlobalPosition += delta; }   // else move the group onto the copied pos
            _gizmo.Attach(Primary); PositionMarkers();
            GD.Print($"[editor] pasted transform to {_selection.Count}");
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
                Select(pr, false);   // single-select the transformed prop for a clean outline in the render
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
