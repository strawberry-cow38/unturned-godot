using System.Collections.Generic;
using Godot;

namespace UnturnedGodot
{
    // The standalone Asset Factory editor (main-menu tool). Compose meshes into one asset,
    // grab each part with the gizmo to place/rotate/scale it, then Save a self-contained
    // .assetbundle the game auto-loads. Reuses EditorCamera (fly) + EditorGizmo (transform).
    //
    // Phase 2b (here): add-part (mesh picker) + list-select + gizmo manipulate + delete + save
    // (auto-fits a box collider so the saved asset is solid). Phase 3 adds volumes / named
    // points / hook dropdowns; Phase 4 the per-type binders.
    public partial class AssetFactoryEditor : Node3D
    {
        public System.Action OnExit;

        AssetBundle _bundle = new() { Name = "new_asset", Type = "prop" };
        string _savePath;
        Node3D _composeRoot;                          // holds the live editable part nodes
        readonly List<MeshInstance3D> _partNodes = new();
        int _sel = -1;

        EditorCamera _cam;
        EditorGizmo _gizmo;

        VBoxContainer _partsBox;
        Panel _picker;
        LineEdit _nameEdit;
        OptionButton _typeOpt;
        Label _status;
        string[] _meshNames = System.Array.Empty<string>();

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
            RebuildParts();
            BuildUI();
            Select(_bundle.Parts.Count > 0 ? 0 : -1);
            GD.Print($"[assetfactory] editor up: {_bundle.Name} [{_bundle.Type}] — {_bundle.Parts.Count} parts, {_meshNames.Length} meshes available");

            if (System.Environment.GetEnvironmentVariable("UG_AFSELFTEST") == "1") SelfTest();
        }

        // ---- part model <-> live nodes -------------------------------------
        void RebuildParts()
        {
            foreach (var n in _partNodes) if (IsInstanceValid(n)) n.QueueFree();
            _partNodes.Clear();
            foreach (var p in _bundle.Parts)
            {
                var mi = AssetBundleLoader.BuildPart(p) ?? Placeholder(p);
                _composeRoot.AddChild(mi);
                _partNodes.Add(mi);
            }
        }

        static MeshInstance3D Placeholder(AssetBundle.Part p)   // bad/missing mesh -> a magenta cube you can still place
        {
            return new MeshInstance3D
            {
                Name = "(missing)",
                Mesh = new BoxMesh { Size = Vector3.One * 0.4f },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(1f, 0f, 1f) },
                Transform = new Transform3D(AssetBundle.EulerDegBasis(p.Rot).Scaled(AssetBundle.V3(p.Scale, Vector3.One)), AssetBundle.V3(p.Pos)),
            };
        }

        void WriteBack()   // capture the selected part node's live transform into the bundle
        {
            if (_sel < 0 || _sel >= _partNodes.Count || _sel >= _bundle.Parts.Count) return;
            var n = _partNodes[_sel]; var p = _bundle.Parts[_sel];
            p.Pos = new[] { n.Position.X, n.Position.Y, n.Position.Z };
            p.Rot = new[] { n.RotationDegrees.X, n.RotationDegrees.Y, n.RotationDegrees.Z };
            p.Scale = new[] { n.Scale.X, n.Scale.Y, n.Scale.Z };
        }

        void Select(int i)
        {
            WriteBack();
            _sel = (i >= 0 && i < _partNodes.Count) ? i : -1;
            _gizmo.Attach(_sel >= 0 ? _partNodes[_sel] : null);
            RefreshPartsList();
        }

        void AddPart(string mesh)
        {
            WriteBack();
            _bundle.Parts.Add(new AssetBundle.Part { Mesh = mesh, Pos = new[] { 0f, 1f, 0f }, Rot = new[] { 0f, 0f, 0f }, Scale = new[] { 1f, 1f, 1f } });
            RebuildParts();
            Select(_bundle.Parts.Count - 1);
            Status($"added {mesh}");
        }

        void DeleteSelected()
        {
            if (_sel < 0 || _sel >= _bundle.Parts.Count) return;
            string nm = _bundle.Parts[_sel].Mesh;
            _bundle.Parts.RemoveAt(_sel);
            RebuildParts();
            Select(Mathf.Min(_sel, _bundle.Parts.Count - 1));
            Status($"deleted {nm}");
        }

        void Save()
        {
            WriteBack();
            _bundle.Name = SanitizeName(_nameEdit?.Text);
            if (_typeOpt != null && _typeOpt.Selected >= 0) _bundle.Type = _typeOpt.GetItemText(_typeOpt.Selected);
            AutoFitColliderIfNone();
            string path = _savePath ?? $"res://content/assets/{_bundle.Name}.assetbundle";
            _bundle.Save(path);
            _savePath = path;
            Status($"saved {_bundle.Name}.assetbundle ({_bundle.Parts.Count} parts)");
        }

        // If the author hasn't placed any collider, fit one box around all parts so the saved
        // asset is SOLID out of the box (Phase 3 lets them hand-author the real colliders).
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
            {
                _gizmo.DragTo(mm.Position, Input.IsKeyPressed(Key.Ctrl));
            }
            else if (ev is InputEventKey k && k.Pressed && !k.Echo)
            {
                if (k.Keycode == Key.T) { _gizmo.CycleMode(); Status($"gizmo: {GizmoMode()}"); }
                else if (k.Keycode == Key.G) { _gizmo.LocalSpace = !_gizmo.LocalSpace; Status(_gizmo.LocalSpace ? "local space" : "global space"); }
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
            col.AddChild(_typeOpt);

            var addBtn = new Button { Text = "＋ Add Part" };
            addBtn.Pressed += () => TogglePicker(true);
            col.AddChild(addBtn);

            col.AddChild(new Label { Text = "parts:" });
            var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(276, 220) };
            _partsBox = new VBoxContainer();
            scroll.AddChild(_partsBox);
            col.AddChild(scroll);

            var delBtn = new Button { Text = "🗑 Delete Selected" };
            delBtn.Pressed += DeleteSelected;
            col.AddChild(delBtn);
            var saveBtn = new Button { Text = "💾 Save" };
            saveBtn.Pressed += Save;
            col.AddChild(saveBtn);
            var exitBtn = new Button { Text = "Exit" };
            exitBtn.Pressed += () => OnExit?.Invoke();
            col.AddChild(exitBtn);

            _status = new Label { Text = "" };
            col.AddChild(_status);
            col.AddChild(new Label { Text = "select a part → drag gizmo · T mode · G space · Del remove" });

            BuildPicker(layer);
            RefreshPartsList();
        }

        void RefreshPartsList()
        {
            if (_partsBox == null) return;
            foreach (var c in _partsBox.GetChildren()) c.QueueFree();
            for (int i = 0; i < _bundle.Parts.Count; i++)
            {
                int idx = i;
                var b = new Button { Text = (i == _sel ? "▶ " : "   ") + (_bundle.Parts[i].Mesh ?? "?"), Alignment = HorizontalAlignment.Left };
                b.Pressed += () => Select(idx);
                _partsBox.AddChild(b);
            }
        }

        void BuildPicker(CanvasLayer layer)
        {
            _picker = new Panel { Position = new Vector2(324, 12), CustomMinimumSize = new Vector2(300, 460), Visible = false };
            layer.AddChild(_picker);
            var col = new VBoxContainer { CustomMinimumSize = new Vector2(300, 460) };
            _picker.AddChild(col);
            col.AddChild(new Label { Text = "pick a mesh to add" });
            var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(288, 400) };
            var box = new VBoxContainer();
            scroll.AddChild(box);
            col.AddChild(scroll);
            foreach (var m in _meshNames)
            {
                string mm = m;
                var b = new Button { Text = m, Alignment = HorizontalAlignment.Left };
                b.Pressed += () => { AddPart(mm); TogglePicker(false); };
                box.AddChild(b);
            }
            var close = new Button { Text = "close" };
            close.Pressed += () => TogglePicker(false);
            col.AddChild(close);
        }

        void TogglePicker(bool on) { if (_picker != null) _picker.Visible = on; }
        void Status(string s) { if (_status != null) _status.Text = s; GD.Print($"[assetfactory] {s}"); }

        static string[] ScanMeshes()
        {
            var list = new List<string>();
            foreach (var f in DirAccess.GetFilesAt("res://content/"))
                if (f.EndsWith(".txt")) list.Add(f);
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

        // Headless smoke test (UG_AFSELFTEST=1): add a part, nudge it, save -> verify the round-trip.
        void SelfTest()
        {
            string mesh = System.Array.IndexOf(_meshNames, "axe_fire.txt") >= 0 ? "axe_fire.txt" : (_meshNames.Length > 0 ? _meshNames[0] : null);
            if (mesh == null) { GD.Print("[assetfactory] SELFTEST: no meshes"); return; }
            AddPart(mesh);
            if (_partNodes.Count > 0) { _partNodes[_sel].Position = new Vector3(1.2f, 0.5f, -0.3f); _partNodes[_sel].RotationDegrees = new Vector3(0, 45, 0); }
            _nameEdit.Text = "selftest_asset";
            Save();
            var reload = AssetBundle.Load(_savePath);
            GD.Print(reload != null
                ? $"[assetfactory] SELFTEST OK: reloaded {reload.Name} type={reload.Type} parts={reload.Parts.Count} colliders={reload.Colliders.Count} p0.pos=({reload.Parts[0].Pos[0]},{reload.Parts[0].Pos[1]},{reload.Parts[0].Pos[2]})"
                : "[assetfactory] SELFTEST FAIL: reload null");
        }
    }
}
