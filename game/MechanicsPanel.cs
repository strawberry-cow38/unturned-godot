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

            var hint = new Label { Text = "nothing to turn yet — F or Esc to close", HorizontalAlignment = HorizontalAlignment.Center, Modulate = new Color(1, 1, 1, 0.5f) };
            hint.AddThemeFontSizeOverride("font_size", 13);
            col.AddChild(hint);
        }

        public void Show(Vehicle v)
        {
            _v = v;
            Visible = true;
            Refresh();
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
                $"trunk       {(_v.HasTrunk ? "yes" : "none")}";
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
