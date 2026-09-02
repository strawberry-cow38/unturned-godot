using Godot;

namespace UnturnedGodot
{
    /// <summary>The bonnet panel. strawberry: "hood opens a dummy 'mechanics' ui".
    ///
    /// Deliberately a STUB, but a stub pointed at real data: every figure on it is read live off the vehicle
    /// rather than mocked, so when this grows into repair, parts and fuelling it is already reading the fields
    /// those actions will change. A dummy that displays invented numbers has to be rewritten to be useful; a
    /// dummy that displays true ones only has to gain buttons.</summary>
    public partial class MechanicsPanel : CanvasLayer
    {
        Vehicle _v;
        Label _body;
        Control _root;
        VBoxContainer _glassBox;
        Label _glassTitle;
        readonly System.Collections.Generic.List<(Button btn, Label lbl, int idx)> _glassRows = new();
        VBoxContainer _lampBox;
        Label _lampTitle;
        readonly System.Collections.Generic.List<(Button btn, Label lbl, int idx)> _lampRows = new();
        VBoxContainer _tireBox;
        Label _tireTitle;
        readonly System.Collections.Generic.List<(Button btn, Label lbl, int idx)> _tireRows = new();

        public override void _Ready()
        {
            Layer = 55;
            ProcessMode = ProcessModeEnum.Always;
            Visible = false;

            var dim = new ColorRect { Color = new Color(0, 0, 0, 0.45f), MouseFilter = Control.MouseFilterEnum.Stop };
            dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(dim);

            var centre = new CenterContainer();
            centre.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(centre);

            var panel = new PanelContainer();
            _root = panel;
            centre.AddChild(panel);

            var margin = new MarginContainer();
            foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
                margin.AddThemeConstantOverride(side, 22);
            panel.AddChild(margin);

            var col = new VBoxContainer { CustomMinimumSize = new Vector2(360, 0) };
            col.AddThemeConstantOverride("separation", 12);
            margin.AddChild(col);

            var title = new Label { Text = "MECHANICS", HorizontalAlignment = HorizontalAlignment.Center };
            title.AddThemeFontSizeOverride("font_size", 28);
            col.AddChild(title);

            _body = new Label { Text = "" };
            _body.AddThemeFontSizeOverride("font_size", 16);
            col.AddChild(_body);

            _glassTitle = new Label { Text = "GLAZING" };
            _glassTitle.AddThemeFontSizeOverride("font_size", 18);
            col.AddChild(_glassTitle);

            _glassBox = new VBoxContainer();
            _glassBox.AddThemeConstantOverride("separation", 4);
            col.AddChild(_glassBox);

            _lampTitle = new Label { Text = "LAMPS" };
            _lampTitle.AddThemeFontSizeOverride("font_size", 18);
            col.AddChild(_lampTitle);

            _lampBox = new VBoxContainer();
            _lampBox.AddThemeConstantOverride("separation", 4);
            col.AddChild(_lampBox);

            _tireTitle = new Label { Text = "TIRES" };
            _tireTitle.AddThemeFontSizeOverride("font_size", 18);
            col.AddChild(_tireTitle);

            _tireBox = new VBoxContainer();
            _tireBox.AddThemeConstantOverride("separation", 4);
            col.AddChild(_tireBox);

            var hint = new Label { Text = "F or Esc to close", HorizontalAlignment = HorizontalAlignment.Center, Modulate = new Color(1, 1, 1, 0.5f) };
            hint.AddThemeFontSizeOverride("font_size", 13);
            col.AddChild(hint);
        }

        public void Show(Vehicle v)
        {
            _v = v;
            Visible = true;
            BuildGlassRows();
            BuildLampRows();
            BuildTireRows();
            Refresh();
        }

        /// <summary>One row per pane, built ONCE per open. Refresh() runs every frame from _Process, so building
        /// the buttons there would replace them between a press and its release and the click would never land.</summary>
        void BuildGlassRows()
        {
            foreach (var c in _glassBox.GetChildren()) c.QueueFree();
            _glassRows.Clear();
            if (_v == null) return;
            for (int i = 0; i < _v.GlassCount; i++)
            {
                int idx = i;   // capture per row, not the loop variable
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 8);
                var lbl = new Label { Text = "", CustomMinimumSize = new Vector2(210, 0) };
                lbl.AddThemeFontSizeOverride("font_size", 15);
                var btn = new Button { Text = "fix", CustomMinimumSize = new Vector2(70, 0) };
                btn.AddThemeFontSizeOverride("font_size", 14);
                btn.Pressed += () =>
                {
                    if (_v != null && IsInstanceValid(_v) && _v.RepairGlass(idx))
                        GD.Print($"[mechanics] repaired {Vehicle.GlassPaneDisplay(_v.GlassLabel(idx))} on {_v.DisplayName}");
                };
                row.AddChild(lbl); row.AddChild(btn);
                _glassBox.AddChild(row);
                _glassRows.Add((btn, lbl, idx));
            }
            _glassTitle.Visible = _v.GlassCount > 0;
            _glassBox.Visible = _v.GlassCount > 0;
        }

        /// <summary>One row per lamp, same once-per-open rule as the panes: Refresh() runs every frame, so
        /// rebuilding buttons there would swap them between a press and its release.</summary>
        void BuildLampRows()
        {
            foreach (var c in _lampBox.GetChildren()) c.QueueFree();
            _lampRows.Clear();
            if (_v == null) return;
            for (int i = 0; i < _v.LampCount; i++)
            {
                int idx = i;
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 8);
                var lbl = new Label { Text = "", CustomMinimumSize = new Vector2(210, 0) };
                lbl.AddThemeFontSizeOverride("font_size", 15);
                var btn = new Button { Text = "fix", CustomMinimumSize = new Vector2(70, 0) };
                btn.AddThemeFontSizeOverride("font_size", 14);
                btn.Pressed += () =>
                {
                    if (_v != null && IsInstanceValid(_v) && _v.RepairLamp(idx))
                        GD.Print($"[mechanics] repaired {Vehicle.LampDisplay(_v.LampLabel(idx))} on {_v.DisplayName}");
                };
                row.AddChild(lbl); row.AddChild(btn);
                _lampBox.AddChild(row);
                _lampRows.Add((btn, lbl, idx));
            }
            _lampTitle.Visible = _v.LampCount > 0;
            _lampBox.Visible = _v.LampCount > 0;
        }

        /// <summary>One row per wheel. Same once-per-open rule as the panes and lamps.</summary>
        void BuildTireRows()
        {
            foreach (var c in _tireBox.GetChildren()) c.QueueFree();
            _tireRows.Clear();
            if (_v == null) return;
            for (int i = 0; i < _v.TireCount; i++)
            {
                int idx = i;
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 8);
                var lbl = new Label { Text = "", CustomMinimumSize = new Vector2(210, 0) };
                lbl.AddThemeFontSizeOverride("font_size", 15);
                var btn = new Button { Text = "replace", CustomMinimumSize = new Vector2(70, 0) };
                btn.AddThemeFontSizeOverride("font_size", 14);
                btn.Pressed += () =>
                {
                    if (_v != null && IsInstanceValid(_v) && _v.RepairTire(idx))
                        GD.Print($"[mechanics] replaced {Vehicle.TireDisplay(idx, _v.TireCount)} on {_v.DisplayName}");
                };
                row.AddChild(lbl); row.AddChild(btn);
                _tireBox.AddChild(row);
                _tireRows.Add((btn, lbl, idx));
            }
            _tireTitle.Visible = _v.TireCount > 0;
            _tireBox.Visible = _v.TireCount > 0;
        }

        public new void Hide() { Visible = false; _v = null; }
        public bool IsOpen => Visible;

        void Refresh()
        {
            if (_v == null || !IsInstanceValid(_v)) { Hide(); return; }
            // Real fields, not placeholders -- see the class note.
            _body.Text =
                $"{_v.DisplayName}\n\n" +
                $"engine      {(_v.EngineOn ? "running" : "off")}\n" +
                $"health      {_v.Health:0} / {_v.HealthMax:0}\n" +
                $"fuel        {_v.Fuel:0} / {_v.FuelMax:0}\n" +
                $"battery     {_v.Battery:0}\n" +
                $"gears       {_v.GearCount}\n" +
                $"seats       {_v.SeatCount}\n" +
                $"trunk       {(_v.HasTrunk ? "yes" : "none")}\n" +
                $"glass       {_v.GlassCount - _v.GlassBrokenCount} / {_v.GlassCount} intact\n" +
                $"lamps       {_v.LampCount - _v.LampBrokenCount} / {_v.LampCount} working\n" +
                $"tires       {_v.TireCount - _v.TirePoppedCount} / {_v.TireCount} inflated";

            foreach (var (btn, lbl, idx) in _glassRows)
            {
                if (!IsInstanceValid(btn)) continue;
                bool broken = _v.IsGlassBroken(idx);
                lbl.Text = $"{Vehicle.GlassPaneDisplay(_v.GlassLabel(idx)),-14} {(broken ? "SHATTERED" : "intact")}";
                lbl.Modulate = broken ? new Color(1f, 0.55f, 0.5f) : new Color(1, 1, 1, 0.75f);
                btn.Disabled = !broken;   // nothing to fix on an intact pane
            }

            foreach (var (btn, lbl, idx) in _lampRows)
            {
                if (!IsInstanceValid(btn)) continue;
                bool dead = _v.IsLampBroken(idx);
                lbl.Text = $"{Vehicle.LampDisplay(_v.LampLabel(idx)),-16} {(dead ? "SHOT OUT" : "working")}";
                lbl.Modulate = dead ? new Color(1f, 0.55f, 0.5f) : new Color(1, 1, 1, 0.75f);
                btn.Disabled = !dead;
            }

            foreach (var (btn, lbl, idx) in _tireRows)
            {
                if (!IsInstanceValid(btn)) continue;
                bool flat = _v.IsTirePopped(idx);
                lbl.Text = $"{Vehicle.TireDisplay(idx, _v.TireCount),-16} {(flat ? "BLOWN" : "ok")}";
                lbl.Modulate = flat ? new Color(1f, 0.55f, 0.5f) : new Color(1, 1, 1, 0.75f);
                btn.Disabled = !flat;
            }
        }

        public override void _Process(double delta) { if (Visible) Refresh(); }

        public override void _UnhandledInput(InputEvent e)
        {
            if (!Visible) return;
            bool close = e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }
                         || Keybinds.JustPressed(GameAction.Interact, e);
            if (!close) return;
            Hide();
            Input.MouseMode = Input.MouseModeEnum.Captured;
            GetViewport().SetInputAsHandled();
        }
    }
}
