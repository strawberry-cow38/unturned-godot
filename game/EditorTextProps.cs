using Godot;
using System;
using System.Collections.Generic;

namespace UnturnedGodot
{
    /// <summary>Level → Text mesh. Drafts are previews; only Place/Apply creates a reversible map edit.</summary>
    public partial class EditorTextProps : Node3D
    {
        readonly Editor _editor; readonly EditorObjects _objects; readonly Camera3D _camera;
        CanvasLayer _ui; PanelContainer _panel; TextEdit _text; Label _status, _title;
        SpinBox _height, _width, _depth, _tracking, _word, _line, _offset, _roll;
        OptionButton _align, _anchor; ColorPickerButton _color; CheckBox _solid, _shadows, _surface;
        Button _apply, _place; SubViewport _previewView; Camera3D _previewCamera;
        ModeledTextProp _preview, _ghost; string _editing; TextPropStyle _draft = new();
        bool _sync, _open, _placing, _validHit, _angle; double _refreshIn = -1; Transform3D _placement;
        public bool BlocksWorld => _open;
        readonly List<(Node3D node,bool process,bool input,bool visible)> _suspendedTools = new();
        public EditorTextProps(Editor editor, EditorObjects objects, Camera3D camera) { _editor = editor; _objects = objects; _camera = camera; }
        public override void _Ready()
        {
            _ui = new CanvasLayer { Layer = 70 }; AddChild(_ui);
            _panel = new PanelContainer { Visible = false, CustomMinimumSize = new Vector2(430, 400) }; _ui.AddChild(_panel);
            var column = new VBoxContainer(); column.AddThemeConstantOverride("separation", 6); _panel.AddChild(column);
            _title = new Label { Text = "TEXT → PROP" }; _title.AddThemeFontSizeOverride("font_size", 22); column.AddChild(_title);
            var previewContainer = new SubViewportContainer { CustomMinimumSize = new Vector2(410, 150), Stretch = true, MouseFilter = Control.MouseFilterEnum.Ignore }; column.AddChild(previewContainer);
            _previewView = new SubViewport { Size = new Vector2I(820, 300), OwnWorld3D = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled, TransparentBg = false }; previewContainer.AddChild(_previewView);
            var env = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(.75f,.76f,.73f), AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = Colors.White, AmbientLightEnergy = .65f };
            _previewView.AddChild(new WorldEnvironment { Environment = env });
            _previewView.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-25,-25,0), LightEnergy = .8f });
            _previewCamera = new Camera3D { Projection = Camera3D.ProjectionType.Orthogonal, Current = true, Near = .001f }; _previewView.AddChild(_previewCamera);
            _preview = new ModeledTextProp(); _previewView.AddChild(_preview);
            var viewButtons = new HBoxContainer(); column.AddChild(viewButtons);
            Button(viewButtons, "Front", () => { _angle = false; RefreshPreview(); });
            Button(viewButtons, "Angle", () => { _angle = true; RefreshPreview(); });
            Button(viewButtons, "New text", () => Open(null));
            var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; column.AddChild(scroll);
            var fields = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; fields.AddThemeConstantOverride("separation", 5); scroll.AddChild(fields);
            fields.AddChild(new Label { Text = "Text (up to 512 characters / 16 lines)" });
            _text = new TextEdit { CustomMinimumSize = new Vector2(400,100), WrapMode = TextEdit.LineWrappingMode.Boundary }; fields.AddChild(_text); _text.TextChanged += Changed;
            var presets = new HBoxContainer(); fields.AddChild(presets);
            Button(presets, "Label", () => Preset(.2f,.04f)); Button(presets, "Store", () => Preset(.6f,.12f)); Button(presets, "Large", () => Preset(1.2f,.24f));
            _height = Number(fields,"Cap height (m)",.02,20,.01);
            _width = Number(fields,"Width (%)",10,500,1);
            _depth = Number(fields,"Depth (m)",.001,3,.001);
            _tracking = Number(fields,"Letter gap (× height)",-.5,2,.01);
            _word = Number(fields,"Word spacing (×)",.1,5,.05);
            _line = Number(fields,"Line height (×)",.5,5,.05);
            _align = Options(fields,"Line alignment",new[]{"Left","Centre","Right"});
            _anchor = Options(fields,"Vertical origin",new[]{"First baseline","Top","Middle","Bottom"});
            var colorRow = Row(fields,"Letter colour"); _color = new ColorPickerButton { EditAlpha = false, CustomMinimumSize = new Vector2(160,30), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; colorRow.AddChild(_color); _color.ColorChanged += _ => Changed();
            _solid = Tick(fields,"Solid letter collision (off = decoration)",false); _shadows = Tick(fields,"Cast shadows",true);
            fields.AddChild(new HSeparator());
            _surface = Tick(fields,"Align to clicked wall / surface",true);
            _offset = Number(fields,"Surface offset (m)",0,2,.005); _offset.Value = .01;
            _roll = Number(fields,"Rotation on surface (°)",-180,180,1);
            fields.AddChild(new Label { Text = "After placing: Q move · W rotate · R scale\nSelect the prop → Text mesh… to edit it again.\nASCII set; unsupported characters display as ?", AutowrapMode = TextServer.AutowrapMode.WordSmart });
            _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(400,48) }; column.AddChild(_status);
            var actions = new HBoxContainer(); column.AddChild(actions);
            _place = Button(actions,"Place new…", () => { RefreshPreview(); if (_preview.Geometry?.Mesh != null) { _placing = true; _status.Text = "Click a surface to place. Esc cancels placement."; GetViewport().GuiReleaseFocus(); } });
            _apply = Button(actions,"Apply changes", Apply);
            Button(actions,"Cancel / close", Close);
            var history = new HBoxContainer(); column.AddChild(history);
            Button(history,"Undo map edit", () => { Close(); _editor.Undo(); }); Button(history,"Redo map edit", () => { Close(); _editor.Redo(); });
        }
        Button Button(HBoxContainer box, string title, Action action)
        {
            var b = new Button { Text = title, FocusMode = Control.FocusModeEnum.None, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0,30) }; b.Pressed += action; box.AddChild(b); return b;
        }
        HBoxContainer Row(VBoxContainer box,string title)
        {
            var row = new HBoxContainer(); row.AddChild(new Label { Text = title, CustomMinimumSize = new Vector2(210,26) }); box.AddChild(row); return row;
        }
        SpinBox Number(VBoxContainer box,string title,double min,double max,double step)
        {
            var s = new SpinBox { MinValue = min, MaxValue = max, Step = step, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(160,26) }; Row(box,title).AddChild(s); s.ValueChanged += _ => Changed(); return s;
        }
        OptionButton Options(VBoxContainer box,string title,string[] options)
        {
            var s = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; foreach (var o in options) s.AddItem(o); Row(box,title).AddChild(s); s.ItemSelected += _ => Changed(); return s;
        }
        CheckBox Tick(VBoxContainer box,string title,bool value)
        {
            var c = new CheckBox { Text = title, ButtonPressed = value }; box.AddChild(c); c.Toggled += _ => Changed(); return c;
        }
        void Changed() { if (!_sync) _refreshIn = .12; }
        void Preset(float height,float depth) { _height.Value = height; _depth.Value = depth; Changed(); }
        public void Open(ModeledTextProp target)
        {
            if (_editor.IsPlaying || _editor.Mode != EEditorMode.Level) return;
            ClearGhost();
            _editing = target?.Id; _draft = target?.Style.Copy() ?? new TextPropStyle(); _open = true; _placing = false;
            // Suspend competing tools without throwing away an unfinished spline/path.
            if (_suspendedTools.Count == 0)
                foreach (var tool in new Node3D[] { _editor.PowerLinesEd, _editor.FenceRoadEd, _editor.FenceSplineEd, _editor.RailEd, _editor.BridgeEd, _editor.TunnelEd, _editor.BlockRoadEd })
                    if (IsInstanceValid(tool)) { _suspendedTools.Add((tool,tool.IsProcessing(),tool.IsProcessingUnhandledInput(),tool.Visible)); tool.SetProcess(false); tool.SetProcessUnhandledInput(false); tool.Visible = false; }
            _previewView.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
            _sync = true; _text.Text = _draft.Text; _height.Value = _draft.Height; _width.Value = _draft.WidthScale*100; _depth.Value = _draft.Depth;
            _tracking.Value = _draft.Tracking; _word.Value = _draft.WordSpacing; _line.Value = _draft.LineSpacing; _align.Select(_draft.Align); _anchor.Select(_draft.Anchor);
            _color.Color = new Color(_draft.Color); _solid.ButtonPressed = _draft.Solid; _shadows.ButtonPressed = _draft.Shadows; _sync = false;
            _title.Text = _editing == null ? "TEXT → PROP  ·  New" : "TEXT → PROP  ·  Edit selected";
            _objects.SetTextEditingOverlay(true);
            _apply.Disabled = _editing == null; _panel.Visible = true; RefreshPreview(); _text.GrabFocus();
        }
        TextPropStyle ReadDraft() => new TextPropStyle { Text = _text.Text, Height = (float)_height.Value, WidthScale = (float)_width.Value/100, Depth = (float)_depth.Value,
            Tracking = (float)_tracking.Value, WordSpacing = (float)_word.Value, LineSpacing = (float)_line.Value, Align = _align.Selected, Anchor = _anchor.Selected,
            Color = _color.Color.ToHtml(false), Solid = _solid.ButtonPressed, Shadows = _shadows.ButtonPressed }.Clean();
        void RefreshPreview()
        {
            if (!_open) return;
            try
            {
                _draft = ReadDraft(); _preview.Rebuild(_draft, collision:false); var g = _preview.Geometry;
                _place.Disabled = g.Mesh == null; _apply.Disabled = _editing == null || g.Mesh == null;
                if (g.Mesh == null) { _status.Text = "Type at least one visible letter or symbol."; return; }
                var center = g.Bounds.GetCenter(); float aspect = Math.Max(1f,(float)_previewView.Size.X / Math.Max(1,_previewView.Size.Y));
                _previewCamera.Size = Math.Max(.15f,Math.Max(g.Bounds.Size.Y*1.5f,g.Bounds.Size.X/aspect*1.25f));
                float distance = Math.Max(2,g.Bounds.Size.Length()*2);
                _previewCamera.Far = Math.Max(100, distance * 3);
                _previewCamera.Position = center + (_angle ? new Vector3(.25f,.18f,1).Normalized() : Vector3.Back)*distance; _previewCamera.LookAt(center,Vector3.Up);
                _status.Text = $"{g.Bounds.Size.X:0.##} × {g.Bounds.Size.Y:0.##} × {_draft.Depth:0.###} m  ·  {g.Triangles:N0} triangles";
                if (g.Replaced > 0) _status.Text += $"\n{g.Replaced} unsupported character(s) shown as ?";
                if (_text.Text.Length > 512 || _text.Text.Split('\n').Length > 16) _status.Text += "\nLIMIT: only the first 512 characters / 16 lines are used.";
                if (_ghost != null) _ghost.Rebuild(_draft,collision:false);
            }
            catch (Exception e) { _status.Text = "Cannot build text: " + e.Message; _place.Disabled = true; _apply.Disabled = true; }
        }
        void Apply()
        {
            RefreshPreview(); if (_apply.Disabled) return;
            if (!_objects.EditText(_editing,_draft)) { _status.Text = "That prop no longer exists. Place a new copy instead."; return; } Close();
        }
        public void Close()
        {
            _open = false; _placing = false; _panel.Visible = false; _refreshIn = -1; GetViewport().GuiReleaseFocus(); ClearGhost();
            _previewView.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
            foreach (var (node,process,input,visible) in _suspendedTools) if (IsInstanceValid(node)) { node.SetProcess(process); node.SetProcessUnhandledInput(input); node.Visible = visible; }
            _suspendedTools.Clear();
            _objects.SetTextEditingOverlay(false);
        }
        void ClearGhost() { if (_ghost != null) { _ghost.QueueFree(); _ghost = null; } _validHit = false; }
        public void SuspendForPlay() => Close(); // drafts never mutate committed map data
        public override void _Process(double delta)
        {
            if (!_open) return;
            if (_editor.IsPlaying || _editor.Mode != EEditorMode.Level) { Close(); return; }
            Vector2 size = GetViewport().GetVisibleRect().Size; _panel.Position = new Vector2(Math.Max(0,size.X-442),56); _panel.Size = new Vector2(430,Math.Max(400,size.Y-70));
            if (_refreshIn >= 0) { _refreshIn -= delta; if (_refreshIn < 0) RefreshPreview(); }
            if (!_placing) return;
            UpdatePlacement();
        }
        void UpdatePlacement()
        {
            if (_editor.Camera?.Flying == true || Editor.PointerOverUI(this)) { if (_ghost != null) _ghost.Visible = false; _validHit = false; return; }
            var mouse = GetViewport().GetMousePosition(); var from = _camera.ProjectRayOrigin(mouse); var ray = _camera.ProjectRayNormal(mouse);
            var hit = GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(from,from+ray*4000,1u | (1u<<6) | EditorObjects.PickLayer));
            _validHit = hit.Count > 0;
            if (!_validHit) { if (_ghost != null) _ghost.Visible = false; return; }
            Vector3 normal = _surface.ButtonPressed ? ((Vector3)hit["normal"]).Normalized() : _camera.GlobalBasis.Z.Normalized();
            var up = Mathf.Abs(normal.Dot(Vector3.Up)) > .98f ? _camera.GlobalBasis.Y : Vector3.Up;
            var right = up.Cross(normal).Normalized(); up = normal.Cross(right).Normalized();
            var basis = new Basis(right,up,normal).Rotated(normal,Mathf.DegToRad((float)_roll.Value));
            _placement = new Transform3D(basis,(Vector3)hit["position"]+normal*(float)_offset.Value);
            if (_ghost == null) { _ghost = new ModeledTextProp(); AddChild(_ghost); _ghost.Rebuild(_draft,collision:false); }
            _ghost.Visible = true; _ghost.GlobalTransform = _placement;
        }
        public override void _UnhandledInput(InputEvent ev)
        {
            if (_editor.IsPlaying || _editor.Mode != EEditorMode.Level || _editor.Camera?.Flying == true) return;
            if (ev is InputEventKey { Pressed:true, Echo:false, Keycode:Key.H, CtrlPressed:true, ShiftPressed:true })
            { Open(_objects.SelectedText); GetViewport().SetInputAsHandled(); return; }
            if (!_open) return;
            if (ev is InputEventKey { Pressed:true, Echo:false, Keycode:Key.Escape })
            { if (_placing) { _placing = false; ClearGhost(); RefreshPreview(); } else Close(); GetViewport().SetInputAsHandled(); return; }
            if (_placing && ev is InputEventMouseButton { Pressed:true, ButtonIndex:MouseButton.Left } && !Editor.PointerOverUI(this))
            {
                RefreshPreview(); UpdatePlacement(); // commit the controls/cursor now, not the previous debounced frame
                if (_validHit && _preview.Geometry?.Mesh != null) { var style = _draft.Copy(); var transform = _placement; Close(); _objects.CreateText(style,transform); }
                GetViewport().SetInputAsHandled(); return;
            }
            // Keep active path tools / world shortcuts from acting behind the text panel.
            if (ev is InputEventKey || ev is InputEventMouseButton { ButtonIndex:MouseButton.Left }) GetViewport().SetInputAsHandled();
        }
    }
}