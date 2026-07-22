using System.Collections.Generic;
using Godot;

namespace UnturnedGodot
{
    // The standalone Asset Factory editor (main-menu tool). Compose meshes into one asset and
    // HAND-PLACE its colliders / volumes / named hook-points with the gizmo — no more guessing
    // mounts from bundle math — then Save a self-contained .assetbundle the game auto-loads.
    // Reuses EditorCamera (fly) + EditorGizmo (transform).
    //
    // Phase 2: parts (add/select/gizmo/delete/save).  Phase 3 (here): colliders + volumes + named
    // points as gizmo-selectable items, with per-type hook-name presets.  Phase 4: per-type binders.
    public partial class AssetFactoryEditor : Node3D
    {
        public System.Action OnExit;

        enum Kind { Part, Collider, Volume, Point }

        AssetBundle _bundle = new() { Name = "new_asset", Type = "prop" };
        string _savePath;
        Node3D _composeRoot;
        readonly List<MeshInstance3D> _partNodes = new();
        readonly List<Node3D> _colNodes = new();
        readonly List<Node3D> _volNodes = new();
        readonly List<Node3D> _ptNodes = new();
        Kind _selKind = Kind.Part;
        int _selIdx = -1;

        EditorCamera _cam;
        EditorGizmo _gizmo;

        VBoxContainer _listBox;
        Panel _picker;
        Panel _openPanel; VBoxContainer _openList;   // open a saved .assetbundle to edit
        LineEdit _nameEdit;
        OptionButton _typeOpt, _hookOpt, _surfOpt, _powerKind;   // behaviours: impact surface, power in/out
        LineEdit _powerWatts, _powerLabel;
        Label _status;
        string[] _meshNames = System.Array.Empty<string>();
        Kind _clipKind; object _clipObj;    // copy/paste clipboard (a cloned item)
        string _pickerName;                 // the mesh highlighted in the picker (E places it)
        SubViewport _previewVp; Node3D _previewPivot; MeshInstance3D _previewMesh;   // 3D spinning preview of the highlighted mesh

        public void Setup(string loadPath = null)
        {
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.53f, 0.67f, 0.86f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.92f, 0.92f, 0.94f), AmbientLightEnergy = 1.1f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-52f, -38f, 0f), LightEnergy = 1.25f, ShadowEnabled = true });
            AddChild(BuildGroundGrid());

            _cam = new EditorCamera { Position = new Vector3(3.5f, 2.6f, 4.5f), RotationDegrees = new Vector3(-22f, 38f, 0f), Current = true };
            AddChild(_cam);
            _gizmo = new EditorGizmo(_cam);
            AddChild(_gizmo);
            _composeRoot = new Node3D { Name = "Compose" };
            AddChild(_composeRoot);

            if (loadPath != null)
            {
                var b = AssetBundle.Load(loadPath);
                if (b != null) { _bundle = b; _savePath = loadPath; }
                else GD.Print($"[assetfactory] could not load {loadPath} — starting empty");
            }
            _meshNames = ScanMeshes();
            RebuildAll();
            BuildUI();
            Select(Kind.Part, _bundle.Parts.Count > 0 ? 0 : -1);
            GD.Print($"[assetfactory] editor up: {_bundle.Name} [{_bundle.Type}] — {_bundle.Parts.Count}p/{_bundle.Colliders.Count}c/{_bundle.Volumes.Count}v/{_bundle.Points.Count}pt, {_meshNames.Length} meshes");

            if (System.Environment.GetEnvironmentVariable("UG_AFSELFTEST") == "1") SelfTest();
            if (System.Environment.GetEnvironmentVariable("UG_AFPICKER") == "1")   // render hook: open the picker + preview a mesh
            {
                TogglePicker(true);
                SetPickerMesh(System.Array.IndexOf(_meshNames, "axe_fire.txt") >= 0 ? "axe_fire.txt" : (_meshNames.Length > 0 ? _meshNames[0] : null));
            }
            if (System.Environment.GetEnvironmentVariable("UG_AFOPEN") == "1") { RefreshOpenList(); if (_openPanel != null) _openPanel.Visible = true; }   // render hook: show the open-bundle list
        }

        // ---- live nodes <-> bundle ------------------------------------------
        void RebuildAll()
        {
            foreach (var n in AllNodes()) if (IsInstanceValid(n)) n.QueueFree();
            _partNodes.Clear(); _colNodes.Clear(); _volNodes.Clear(); _ptNodes.Clear();
            foreach (var p in _bundle.Parts) { var mi = AssetBundleLoader.BuildPart(p) ?? Placeholder(p); _composeRoot.AddChild(mi); _partNodes.Add(mi); }
            foreach (var c in _bundle.Colliders) { var n = BoxViz(new Color(1f, 0.85f, 0.1f, 0.28f), c.Pos, c.Rot, c.Size); _composeRoot.AddChild(n); _colNodes.Add(n); }
            foreach (var v in _bundle.Volumes) { var n = BoxViz(new Color(0.1f, 0.85f, 1f, 0.24f), v.Pos, v.Rot, v.Size); _composeRoot.AddChild(n); _volNodes.Add(n); }
            foreach (var pt in _bundle.Points) { var n = PointViz(pt.Pos, pt.Rot); _composeRoot.AddChild(n); _ptNodes.Add(n); }
        }

        IEnumerable<Node3D> AllNodes()
        {
            foreach (var n in _partNodes) yield return n;
            foreach (var n in _colNodes) yield return n;
            foreach (var n in _volNodes) yield return n;
            foreach (var n in _ptNodes) yield return n;
        }

        Node3D SelNode() => _selKind switch
        {
            Kind.Part => Valid(_partNodes, _selIdx) ? _partNodes[_selIdx] : null,
            Kind.Collider => Valid(_colNodes, _selIdx) ? _colNodes[_selIdx] : null,
            Kind.Volume => Valid(_volNodes, _selIdx) ? _volNodes[_selIdx] : null,
            Kind.Point => Valid(_ptNodes, _selIdx) ? _ptNodes[_selIdx] : null,
            _ => null,
        };
        static bool Valid<T>(List<T> l, int i) => i >= 0 && i < l.Count;

        void WriteBack()
        {
            var n = SelNode(); if (n == null) return;
            var pos = new[] { n.Position.X, n.Position.Y, n.Position.Z };
            var rot = new[] { n.RotationDegrees.X, n.RotationDegrees.Y, n.RotationDegrees.Z };
            var scl = new[] { n.Scale.X, n.Scale.Y, n.Scale.Z };
            switch (_selKind)
            {
                case Kind.Part: { var p = _bundle.Parts[_selIdx]; p.Pos = pos; p.Rot = rot; p.Scale = scl; break; }
                case Kind.Collider: { var c = _bundle.Colliders[_selIdx]; c.Pos = pos; c.Rot = rot; if (c.Shape == "box") c.Size = scl; break; }
                case Kind.Volume: { var v = _bundle.Volumes[_selIdx]; v.Pos = pos; v.Rot = rot; v.Size = scl; break; }
                case Kind.Point: { var t = _bundle.Points[_selIdx]; t.Pos = pos; t.Rot = rot; break; }
            }
        }

        void Select(Kind k, int i)
        {
            WriteBack();
            _selKind = k;
            int count = k switch { Kind.Part => _partNodes.Count, Kind.Collider => _colNodes.Count, Kind.Volume => _volNodes.Count, Kind.Point => _ptNodes.Count, _ => 0 };
            _selIdx = (i >= 0 && i < count) ? i : -1;
            _gizmo.Attach(SelNode());
            RefreshList();
        }

        // ---- add / delete ---------------------------------------------------
        void AddPart(string mesh)
        {
            WriteBack();
            _bundle.Parts.Add(new AssetBundle.Part { Mesh = mesh, Albedo = AssetBundle.ResolveAlbedo(mesh), Pos = new[] { 0f, 1f, 0f }, Rot = new[] { 0f, 0f, 0f }, Scale = new[] { 1f, 1f, 1f } });
            RebuildAll(); Select(Kind.Part, _bundle.Parts.Count - 1); Status($"added part {mesh}");
        }

        void AddCollider()
        {
            WriteBack();
            _bundle.Colliders.Add(new AssetBundle.Collider { Shape = "box", Pos = new[] { 0f, 1f, 0f }, Rot = new[] { 0f, 0f, 0f }, Size = new[] { 1f, 1f, 1f } });
            RebuildAll(); Select(Kind.Collider, _bundle.Colliders.Count - 1); Status("added box collider");
        }

        void AddVolume()
        {
            WriteBack();
            _bundle.Volumes.Add(new AssetBundle.Volume { Name = "volume", Pos = new[] { 0f, 1f, 0f }, Rot = new[] { 0f, 0f, 0f }, Size = new[] { 1f, 1f, 1f } });
            RebuildAll(); Select(Kind.Volume, _bundle.Volumes.Count - 1); Status("added volume");
        }

        void AddPoint()
        {
            WriteBack();
            string nm = (_hookOpt != null && _hookOpt.Selected >= 0) ? _hookOpt.GetItemText(_hookOpt.Selected) : "Point_0";
            _bundle.Points.Add(new AssetBundle.Point { Name = nm, Pos = new[] { 0f, 1f, 0f }, Rot = new[] { 0f, 0f, 0f } });
            RebuildAll(); Select(Kind.Point, _bundle.Points.Count - 1); Status($"added point {nm}");
        }

        void CopySelected()
        {
            if (_selIdx < 0) return;
            object clone = _selKind switch
            {
                Kind.Part => ClonePart(_bundle.Parts[_selIdx]),
                Kind.Collider => CloneCollider(_bundle.Colliders[_selIdx]),
                Kind.Volume => CloneVolume(_bundle.Volumes[_selIdx]),
                Kind.Point => ClonePoint(_bundle.Points[_selIdx]),
                _ => null,
            };
            if (clone != null) { _clipKind = _selKind; _clipObj = clone; Status($"copied {_selKind}"); }
        }

        void PasteClipboard()
        {
            if (_clipObj == null) return;
            switch (_clipKind)
            {
                case Kind.Part: { var p = ClonePart((AssetBundle.Part)_clipObj); Nudge(p.Pos); _bundle.Parts.Add(p); RebuildAll(); Select(Kind.Part, _bundle.Parts.Count - 1); break; }
                case Kind.Collider: { var c = CloneCollider((AssetBundle.Collider)_clipObj); Nudge(c.Pos); _bundle.Colliders.Add(c); RebuildAll(); Select(Kind.Collider, _bundle.Colliders.Count - 1); break; }
                case Kind.Volume: { var v = CloneVolume((AssetBundle.Volume)_clipObj); Nudge(v.Pos); _bundle.Volumes.Add(v); RebuildAll(); Select(Kind.Volume, _bundle.Volumes.Count - 1); break; }
                case Kind.Point: { var t = ClonePoint((AssetBundle.Point)_clipObj); Nudge(t.Pos); _bundle.Points.Add(t); RebuildAll(); Select(Kind.Point, _bundle.Points.Count - 1); break; }
            }
            Status("pasted");
        }

        static void Nudge(float[] pos) { if (pos != null && pos.Length >= 3) { pos[0] += 0.5f; pos[2] += 0.5f; } }
        static AssetBundle.Part ClonePart(AssetBundle.Part p) => new() { Mesh = p.Mesh, Albedo = p.Albedo, Color = (float[])p.Color?.Clone(), Pos = (float[])p.Pos.Clone(), Rot = (float[])p.Rot.Clone(), Scale = (float[])p.Scale.Clone() };
        static AssetBundle.Collider CloneCollider(AssetBundle.Collider c) => new() { Shape = c.Shape, Pos = (float[])c.Pos.Clone(), Rot = (float[])c.Rot.Clone(), Size = (float[])c.Size.Clone() };
        static AssetBundle.Volume CloneVolume(AssetBundle.Volume v) => new() { Name = v.Name, Pos = (float[])v.Pos.Clone(), Rot = (float[])v.Rot.Clone(), Size = (float[])v.Size.Clone() };
        static AssetBundle.Point ClonePoint(AssetBundle.Point p) => new() { Name = p.Name, Pos = (float[])p.Pos.Clone(), Rot = (float[])p.Rot.Clone() };

        void DeleteSelected()
        {
            if (_selIdx < 0) return;
            switch (_selKind)
            {
                case Kind.Part: if (Valid(_bundle.Parts, _selIdx)) _bundle.Parts.RemoveAt(_selIdx); break;
                case Kind.Collider: if (Valid(_bundle.Colliders, _selIdx)) _bundle.Colliders.RemoveAt(_selIdx); break;
                case Kind.Volume: if (Valid(_bundle.Volumes, _selIdx)) _bundle.Volumes.RemoveAt(_selIdx); break;
                case Kind.Point: if (Valid(_bundle.Points, _selIdx)) _bundle.Points.RemoveAt(_selIdx); break;
            }
            RebuildAll();
            Select(_selKind, -1);
            Status("deleted");
        }

        void Save()
        {
            WriteBack();
            _bundle.Name = SanitizeName(_nameEdit?.Text);
            if (_typeOpt != null && _typeOpt.Selected >= 0) _bundle.Type = _typeOpt.GetItemText(_typeOpt.Selected);
            AutoFitColliderIfNone();
            string path = _savePath ?? $"res://content/assets/{_bundle.Name}.assetbundle";
            _bundle.Save(path); _savePath = path;
            Status($"saved {_bundle.Name}.assetbundle ({_bundle.Parts.Count}p/{_bundle.Colliders.Count}c/{_bundle.Volumes.Count}v/{_bundle.Points.Count}pt)");
        }

        void AutoFitColliderIfNone()
        {
            if (_bundle.Colliders.Count > 0 || _partNodes.Count == 0) return;
            Aabb bb = default; bool has = false;
            foreach (var n in _partNodes)
            {
                if (!IsInstanceValid(n) || n.Mesh == null) continue;
                var lb = n.Mesh.GetAabb(); var xf = n.Transform;
                for (int i = 0; i < 8; i++)
                {
                    var corner = lb.Position + new Vector3((i & 1) * lb.Size.X, ((i >> 1) & 1) * lb.Size.Y, ((i >> 2) & 1) * lb.Size.Z);
                    var wp = xf * corner;
                    if (!has) { bb = new Aabb(wp, Vector3.Zero); has = true; } else bb = bb.Expand(wp);
                }
            }
            if (!has) return;
            var c = bb.GetCenter(); var s = bb.Size;
            _bundle.Colliders.Add(new AssetBundle.Collider { Shape = "box", Pos = new[] { c.X, c.Y, c.Z }, Rot = new[] { 0f, 0f, 0f }, Size = new[] { s.X, s.Y, s.Z } });
        }

        // ---- input ----------------------------------------------------------
        public override void _UnhandledInput(InputEvent ev)
        {
            if (ev is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed) { if (_gizmo.TryBeginDrag(mb.Position)) GetViewport().SetInputAsHandled(); }
                else if (_gizmo.Dragging) { _gizmo.EndDrag(); WriteBack(); }
            }
            else if (ev is InputEventMouseMotion mm && _gizmo.Dragging)
                _gizmo.DragTo(mm.Position, Input.IsKeyPressed(Key.Ctrl));
            else if (ev is InputEventKey k && k.Pressed && !k.Echo)
            {
                if (k.CtrlPressed && k.Keycode == Key.C) CopySelected();
                else if (k.CtrlPressed && k.Keycode == Key.V) PasteClipboard();
                else if (k.Keycode == Key.T) { _gizmo.CycleMode(); Status($"gizmo: {GizmoMode()}"); }
                else if (k.Keycode == Key.G) { _gizmo.LocalSpace = !_gizmo.LocalSpace; Status(_gizmo.LocalSpace ? "local space" : "global space"); }
                else if (k.Keycode == Key.B) { _gizmo.LocalSpace = false; Status("global space"); }   // B = global transform space
                else if (k.Keycode == Key.N) { _gizmo.LocalSpace = true; Status("local space"); }      // N = local transform space
                else if (k.Keycode == Key.E) { if (_pickerName != null) AddPart(_pickerName); }         // E = place the highlighted picker mesh
                else if (k.Keycode == Key.Delete) DeleteSelected();
            }
        }

        string GizmoMode() => _gizmo.Mode switch { EditorGizmo.EMode.Rotate => "rotate", EditorGizmo.EMode.Scale => "scale", _ => "move" };

        // ---- UI -------------------------------------------------------------
        void BuildUI()
        {
            var layer = new CanvasLayer();
            AddChild(layer);
            var panel = new PanelContainer { Position = new Vector2(12, 12), CustomMinimumSize = new Vector2(300, 0) };
            layer.AddChild(panel);
            var col = new VBoxContainer();
            panel.AddChild(col);

            var title = new Label { Text = "ASSET FACTORY" };
            title.AddThemeFontSizeOverride("font_size", 22);
            col.AddChild(title);

            col.AddChild(new Label { Text = "name" });
            _nameEdit = new LineEdit { Text = _bundle.Name, CustomMinimumSize = new Vector2(276, 0) };
            col.AddChild(_nameEdit);
            col.AddChild(new Label { Text = "type" });
            _typeOpt = new OptionButton();
            foreach (var t in new[] { "prop", "deployable", "vehicle", "gun" }) _typeOpt.AddItem(t);
            for (int i = 0; i < _typeOpt.ItemCount; i++) if (_typeOpt.GetItemText(i) == _bundle.Type) _typeOpt.Selected = i;
            _typeOpt.ItemSelected += _ => RepopulateHooks();
            col.AddChild(_typeOpt);

            col.AddChild(new Label { Text = "— behaviours —" });
            var bRow = new HBoxContainer();
            bRow.AddChild(new Label { Text = "impact surface" });
            _surfOpt = new OptionButton();
            foreach (var sName in new[] { "none", "concrete", "grass", "dirt", "metal", "wood", "sand", "water" }) _surfOpt.AddItem(sName);
            SyncSurfUI();
            _surfOpt.ItemSelected += _ => { var v = _surfOpt.GetItemText(_surfOpt.Selected); _bundle.SetParam("surface", v == "none" ? "" : v); Status($"impact surface: {v}"); };
            bRow.AddChild(_surfOpt);
            col.AddChild(bRow);

            var pRow = new HBoxContainer();
            pRow.AddChild(new Label { Text = "power" });
            _powerKind = new OptionButton();
            foreach (var k in new[] { "none", "output", "consumer", "passthrough" }) _powerKind.AddItem(k);
            _powerKind.ItemSelected += _ => WritePower();
            pRow.AddChild(_powerKind);
            _powerWatts = new LineEdit { PlaceholderText = "watts", CustomMinimumSize = new Vector2(64, 0) };
            _powerWatts.TextChanged += _ => WritePower();
            pRow.AddChild(_powerWatts);
            col.AddChild(pRow);
            _powerLabel = new LineEdit { PlaceholderText = "port label (renameable)", CustomMinimumSize = new Vector2(276, 0) };
            _powerLabel.TextChanged += _ => WritePower();
            col.AddChild(_powerLabel);
            SyncPowerUI();

            var addRow = new HBoxContainer();
            var addPart = new Button { Text = "＋Part" }; addPart.Pressed += () => TogglePicker(true); addRow.AddChild(addPart);
            var addCol = new Button { Text = "＋Box" }; addCol.Pressed += AddCollider; addRow.AddChild(addCol);
            var addVol = new Button { Text = "＋Vol" }; addVol.Pressed += AddVolume; addRow.AddChild(addVol);
            col.AddChild(addRow);

            var ptRow = new HBoxContainer();
            _hookOpt = new OptionButton { CustomMinimumSize = new Vector2(180, 0) };
            col.AddChild(new Label { Text = "hook point:" });
            RepopulateHooks();
            ptRow.AddChild(_hookOpt);
            var addPt = new Button { Text = "＋Point" }; addPt.Pressed += AddPoint; ptRow.AddChild(addPt);
            col.AddChild(ptRow);

            var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(276, 240) };
            _listBox = new VBoxContainer();
            scroll.AddChild(_listBox);
            col.AddChild(scroll);

            var delBtn = new Button { Text = "🗑 Delete Selected" }; delBtn.Pressed += DeleteSelected; col.AddChild(delBtn);
            var openBtn = new Button { Text = "📂 Open" }; openBtn.Pressed += () => { TogglePicker(false); RefreshOpenList(); if (_openPanel != null) _openPanel.Visible = true; }; col.AddChild(openBtn);
            var saveBtn = new Button { Text = "💾 Save" }; saveBtn.Pressed += Save; col.AddChild(saveBtn);
            var exitBtn = new Button { Text = "Exit" }; exitBtn.Pressed += () => OnExit?.Invoke(); col.AddChild(exitBtn);
            _status = new Label { Text = "" }; col.AddChild(_status);
            col.AddChild(new Label { Text = "select an item → drag gizmo · T mode · G space · Del" });

            BuildPicker(layer);
            BuildOpenPanel(layer);
            RefreshList();
        }

        void BuildOpenPanel(CanvasLayer layer)
        {
            _openPanel = new Panel { Position = new Vector2(324, 12), CustomMinimumSize = new Vector2(300, 400), Visible = false };
            layer.AddChild(_openPanel);
            var col = new VBoxContainer { CustomMinimumSize = new Vector2(300, 400) };
            _openPanel.AddChild(col);
            col.AddChild(new Label { Text = "open a saved .assetbundle to edit" });
            var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(288, 340) };
            _openList = new VBoxContainer();
            scroll.AddChild(_openList);
            col.AddChild(scroll);
            var close = new Button { Text = "close" }; close.Pressed += () => { if (_openPanel != null) _openPanel.Visible = false; }; col.AddChild(close);
        }

        void RefreshOpenList()
        {
            if (_openList == null) return;
            foreach (var c in _openList.GetChildren()) c.QueueFree();
            foreach (var f in DirAccess.GetFilesAt(AssetCatalog.Dir))
            {
                if (!f.EndsWith(".assetbundle")) continue;
                string path = AssetCatalog.Dir + f;
                var b = new Button { Text = f, Alignment = HorizontalAlignment.Left };
                b.Pressed += () => { LoadBundle(path); if (_openPanel != null) _openPanel.Visible = false; };
                _openList.AddChild(b);
            }
        }

        void LoadBundle(string path)
        {
            var b = AssetBundle.Load(path);
            if (b == null) { Status($"failed to open {path}"); return; }
            _bundle = b; _savePath = path;
            if (_nameEdit != null) _nameEdit.Text = _bundle.Name;
            if (_typeOpt != null) for (int i = 0; i < _typeOpt.ItemCount; i++) if (_typeOpt.GetItemText(i) == _bundle.Type) _typeOpt.Selected = i;
            RepopulateHooks();
            SyncSurfUI();
            SyncPowerUI();
            RebuildAll();
            Select(Kind.Part, _bundle.Parts.Count > 0 ? 0 : -1);
            Status($"opened {System.IO.Path.GetFileNameWithoutExtension(path)} ({_bundle.Parts.Count}p/{_bundle.Colliders.Count}c/{_bundle.Volumes.Count}v/{_bundle.Points.Count}pt)");
        }

        void RepopulateHooks()
        {
            if (_hookOpt == null) return;
            _hookOpt.Clear();
            string type = (_typeOpt != null && _typeOpt.Selected >= 0) ? _typeOpt.GetItemText(_typeOpt.Selected) : _bundle.Type;
            foreach (var h in HooksFor(type)) _hookOpt.AddItem(h);
            if (_hookOpt.ItemCount > 0) _hookOpt.Selected = 0;
        }

        void SyncSurfUI()
        {
            if (_surfOpt == null) return;
            string cur = _bundle.ParamString("surface") ?? "none";
            for (int i = 0; i < _surfOpt.ItemCount; i++) if (_surfOpt.GetItemText(i) == cur) { _surfOpt.Selected = i; return; }
            _surfOpt.Selected = 0;
        }

        void WritePower()
        {
            if (_powerKind == null) return;
            string k = _powerKind.GetItemText(_powerKind.Selected);
            if (k == "none") { _bundle.SetParam("power_kind", ""); _bundle.SetParam("power_watts", 0f); _bundle.SetParam("power_label", ""); }
            else
            {
                _bundle.SetParam("power_kind", k);
                _bundle.SetParam("power_watts", float.TryParse(_powerWatts?.Text, out var w) ? w : 0f);
                _bundle.SetParam("power_label", _powerLabel?.Text ?? "");
            }
            Status($"power: {k}");
        }

        void SyncPowerUI()
        {
            if (_powerKind == null) return;
            string k = _bundle.ParamString("power_kind") ?? "none";
            int idx = 0;
            for (int i = 0; i < _powerKind.ItemCount; i++) if (_powerKind.GetItemText(i) == k) { idx = i; break; }
            _powerKind.Selected = idx;
            float w = _bundle.ParamFloat("power_watts", 0f);
            if (_powerWatts != null) _powerWatts.Text = w > 0f ? w.ToString("0") : "";
            if (_powerLabel != null) _powerLabel.Text = _bundle.ParamString("power_label") ?? "";
        }

        static string[] HooksFor(string type) => type switch
        {
            "vehicle" => new[] { "Wheel_FL", "Wheel_FR", "Wheel_RL", "Wheel_RR", "Seat_0", "Seat_1", "Steer", "Exit_0", "Exhaust", "Headlight_0", "Headlight_1", "Light_0" },
            "gun" => new[] { "Muzzle", "Sight", "Magazine", "Eject", "View", "Barrel", "Grip", "Tactical", "Aim" },
            "deployable" => new[] { "Storage", "Anchor", "Light_0", "Point_0" },
            _ => new[] { "Point_0", "Point_1", "Anchor" },
        };

        void RefreshList()
        {
            if (_listBox == null) return;
            foreach (var c in _listBox.GetChildren()) c.QueueFree();
            AddSection("PARTS", Kind.Part, System.Linq.Enumerable.Select(_bundle.Parts, p => p.Mesh ?? "?"));
            AddSection("COLLIDERS", Kind.Collider, System.Linq.Enumerable.Select(_bundle.Colliders, c => c.Shape));
            AddSection("VOLUMES", Kind.Volume, System.Linq.Enumerable.Select(_bundle.Volumes, v => v.Name));
            AddSection("POINTS", Kind.Point, System.Linq.Enumerable.Select(_bundle.Points, p => p.Name));
        }

        void AddSection(string head, Kind kind, IEnumerable<string> labels)
        {
            var list = new List<string>(labels);
            if (list.Count == 0) return;
            var h = new Label { Text = head }; h.AddThemeColorOverride("font_color", new Color(0.7f, 0.8f, 1f)); _listBox.AddChild(h);
            for (int i = 0; i < list.Count; i++)
            {
                int idx = i; Kind k = kind;
                bool selected = _selKind == kind && _selIdx == i;
                var b = new Button { Text = (selected ? "▶ " : "   ") + list[i], Alignment = HorizontalAlignment.Left };
                b.Pressed += () => Select(k, idx);
                _listBox.AddChild(b);
            }
        }

        void BuildPicker(CanvasLayer layer)
        {
            _picker = new Panel { Position = new Vector2(324, 12), CustomMinimumSize = new Vector2(320, 580), Visible = false };
            layer.AddChild(_picker);
            var col = new VBoxContainer { CustomMinimumSize = new Vector2(320, 580) };
            _picker.AddChild(col);
            col.AddChild(new Label { Text = "click a mesh to preview  •  E (or Add) to place" });

            // 3D spinning preview (own-world SubViewport so it renders JUST the mesh, not the editor)
            _previewVp = new SubViewport { Size = new Vector2I(300, 210), RenderTargetUpdateMode = SubViewport.UpdateMode.Always, OwnWorld3D = true };
            var pcam = new Camera3D { Position = new Vector3(0f, 0.55f, 2.3f), RotationDegrees = new Vector3(-13.4f, 0f, 0f), Current = true };   // aimed at origin (no LookAt — not in tree yet)
            _previewVp.AddChild(pcam);
            _previewVp.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-42f, -32f, 0f), LightEnergy = 1.2f });
            _previewVp.AddChild(new WorldEnvironment { Environment = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.18f, 0.2f, 0.24f), AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.8f, 0.8f, 0.82f), AmbientLightEnergy = 0.8f } });
            _previewPivot = new Node3D();
            _previewVp.AddChild(_previewPivot);
            _previewMesh = new MeshInstance3D();
            _previewPivot.AddChild(_previewMesh);
            var vpc = new SubViewportContainer { Stretch = true, CustomMinimumSize = new Vector2(300, 210) };
            vpc.AddChild(_previewVp);
            col.AddChild(vpc);

            var addBtn = new Button { Text = "Add (E)" };
            addBtn.Pressed += () => { if (_pickerName != null) AddPart(_pickerName); };
            col.AddChild(addBtn);

            var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(300, 260) };
            var box = new VBoxContainer();
            scroll.AddChild(box);
            col.AddChild(scroll);
            foreach (var m in _meshNames)
            {
                string mm = m;
                var b = new Button { Text = m, Alignment = HorizontalAlignment.Left };
                b.Pressed += () => SetPickerMesh(mm);
                box.AddChild(b);
            }
            var close = new Button { Text = "close" }; close.Pressed += () => TogglePicker(false); col.AddChild(close);
        }

        void SetPickerMesh(string name)
        {
            _pickerName = name;
            if (_previewMesh == null) return;
            var mesh = ContentProvider.ParseObj($"res://content/{name}");
            if (mesh == null) { Status($"no mesh {name}"); return; }
            _previewMesh.Mesh = mesh;
            var mat = new StandardMaterial3D { CullMode = BaseMaterial3D.CullModeEnum.Disabled, TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest, Roughness = 0.9f };
            var alb = AssetBundle.ResolveAlbedo(name);
            var tex = alb != null ? LoadTex($"res://content/{alb}") : null;
            if (tex != null) mat.AlbedoTexture = tex; else mat.AlbedoColor = new Color(0.72f, 0.74f, 0.8f);
            _previewMesh.MaterialOverride = mat;
            var aabb = mesh.GetAabb();
            float r = Mathf.Max(aabb.Size.X, Mathf.Max(aabb.Size.Y, aabb.Size.Z));
            float sc = r > 0.001f ? 1.5f / r : 1f;
            _previewMesh.Scale = Vector3.One * sc;
            _previewMesh.Position = -aabb.GetCenter() * sc;
            Status($"preview: {name} — E to place");
        }

        public override void _Process(double delta)
        {
            if (_previewPivot != null && _picker != null && _picker.Visible) _previewPivot.RotateY((float)delta * 0.9f);
        }

        void TogglePicker(bool on) { if (_picker != null) _picker.Visible = on; if (on && _openPanel != null) _openPanel.Visible = false; }
        void Status(string s) { if (_status != null) _status.Text = s; GD.Print($"[assetfactory] {s}"); }

        // ---- viz builders ---------------------------------------------------
        static Node3D BoxViz(Color c, float[] pos, float[] rot, float[] size)
        {
            var mat = new StandardMaterial3D
            {
                AlbedoColor = c, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            };
            return new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = Vector3.One }, MaterialOverride = mat,
                Position = AssetBundle.V3(pos), RotationDegrees = AssetBundle.V3(rot), Scale = AssetBundle.V3(size, Vector3.One),
            };
        }

        static Node3D PointViz(float[] pos, float[] rot)
        {
            var mat = new StandardMaterial3D { AlbedoColor = new Color(1f, 0.55f, 0.1f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
            return new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = Vector3.One * 0.18f }, MaterialOverride = mat,
                Position = AssetBundle.V3(pos), RotationDegrees = AssetBundle.V3(rot),
            };
        }

        static MeshInstance3D Placeholder(AssetBundle.Part p) => new()
        {
            Name = "(missing)",
            Mesh = new BoxMesh { Size = Vector3.One * 0.4f },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(1f, 0f, 1f) },
            Transform = new Transform3D(AssetBundle.EulerDegBasis(p.Rot).Scaled(AssetBundle.V3(p.Scale, Vector3.One)), AssetBundle.V3(p.Pos)),
        };

        static Texture2D LoadTex(string res)
        {
            string p = ProjectSettings.GlobalizePath(res);
            if (System.IO.File.Exists(p)) { var img = Image.LoadFromFile(p); if (img != null) return ImageTexture.CreateFromImage(img); }
            return null;
        }

        static string[] ScanMeshes()
        {
            var list = new List<string>();
            foreach (var f in DirAccess.GetFilesAt("res://content/")) if (f.EndsWith(".txt")) list.Add(f);
            list.Sort();
            return list.ToArray();
        }

        static string SanitizeName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "new_asset";
            var sb = new System.Text.StringBuilder();
            foreach (var ch in s.Trim().ToLowerInvariant()) sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
            return sb.ToString();
        }

        static Node3D BuildGroundGrid()
        {
            var mat = new StandardMaterial3D { AlbedoColor = new Color(0.45f, 0.47f, 0.52f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
            var im = new ImmediateMesh();
            im.SurfaceBegin(Mesh.PrimitiveType.Lines, mat);
            const int N = 12;
            for (int i = -N; i <= N; i++)
            {
                im.SurfaceAddVertex(new Vector3(i, 0, -N)); im.SurfaceAddVertex(new Vector3(i, 0, N));
                im.SurfaceAddVertex(new Vector3(-N, 0, i)); im.SurfaceAddVertex(new Vector3(N, 0, i));
            }
            im.SurfaceEnd();
            var holder = new Node3D { Name = "Grid" };
            holder.AddChild(new MeshInstance3D { Mesh = im });
            return holder;
        }

        void SelfTest()
        {
            string mesh = System.Array.IndexOf(_meshNames, "axe_fire.txt") >= 0 ? "axe_fire.txt" : (_meshNames.Length > 0 ? _meshNames[0] : null);
            if (mesh == null) { GD.Print("[assetfactory] SELFTEST: no meshes"); return; }
            AddPart(mesh);
            if (_partNodes.Count > 0) { _partNodes[0].Position = new Vector3(1.2f, 0.5f, -0.3f); _partNodes[0].RotationDegrees = new Vector3(0, 45, 0); }
            Select(Kind.Part, 0);
            AddCollider();
            if (_colNodes.Count > 0) _colNodes[0].Scale = new Vector3(2f, 1f, 3f);
            Select(Kind.Collider, 0);
            AddPoint();
            _bundle.SetParam("surface", "wood");   // behaviour: impact-fx surface
            _bundle.SetParam("power_kind", "output"); _bundle.SetParam("power_watts", 1500f); _bundle.SetParam("power_label", "Main Output");   // behaviour: power out
            _nameEdit.Text = "selftest_asset";
            Save();
            var r = AssetBundle.Load(_savePath);
            GD.Print(r != null
                ? $"[assetfactory] SELFTEST OK: {r.Name} type={r.Type} p={r.Parts.Count} c={r.Colliders.Count} v={r.Volumes.Count} pt={r.Points.Count} col0.size=({r.Colliders[0].Size[0]},{r.Colliders[0].Size[1]},{r.Colliders[0].Size[2]}) pt0={r.Points[0].Name} surface={r.ParamString("surface")} power={r.ParamString("power_kind")}/{r.ParamFloat("power_watts")}w"
                : "[assetfactory] SELFTEST FAIL");
        }
    }
}
