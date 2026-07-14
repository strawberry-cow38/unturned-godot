using Godot;

namespace UnturnedGodot
{
    // ESC pause menu (master): FREEZES the sim (GetTree().Paused) while staying interactive itself
    // (ProcessMode.Always), so the world halts in the background but the menu UI still responds. Replaces the
    // old viewmodel-tuning "offset" slider menu, which wasn't needed.
    public partial class PauseMenu : CanvasLayer
    {
        public override void _Ready()
        {
            Layer = 60;
            Visible = false;
            ProcessMode = Node.ProcessModeEnum.Always;   // keep the menu alive + its input flowing while the tree is paused

            var dim = new ColorRect { Color = new Color(0f, 0f, 0f, 0.6f), MouseFilter = Control.MouseFilterEnum.Stop };
            dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(dim);

            var center = new CenterContainer();
            center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(center);

            var panel = new PanelContainer();
            center.AddChild(panel);
            var margin = new MarginContainer();
            foreach (var s in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" }) margin.AddThemeConstantOverride(s, 30);
            panel.AddChild(margin);
            var vbox = new VBoxContainer { CustomMinimumSize = new Vector2(320, 0) };
            vbox.AddThemeConstantOverride("separation", 16);
            margin.AddChild(vbox);

            var title = new Label { Text = "PAUSED", HorizontalAlignment = HorizontalAlignment.Center };
            title.AddThemeFontSizeOverride("font_size", 34);
            vbox.AddChild(title);

            var resume = new Button { Text = "Resume", CustomMinimumSize = new Vector2(0, 46) };
            resume.Pressed += Close;
            vbox.AddChild(resume);

            var hint = new Label { Text = "esc to resume", HorizontalAlignment = HorizontalAlignment.Center, Modulate = new Color(1f, 1f, 1f, 0.5f) };
            vbox.AddChild(hint);
        }

        // ESC while paused resumes (the player controller is paused + can't, so the menu handles it itself).
        public override void _UnhandledInput(InputEvent e)
        {
            if (Visible && e is InputEventKey { Pressed: true, Keycode: Key.Escape }) { Close(); GetViewport().SetInputAsHandled(); }
        }

        public void Open()
        {
            Visible = true;
            GetTree().Paused = true;                       // freeze the sim in the background (master)
            Input.MouseMode = Input.MouseModeEnum.Visible; // free the cursor for the menu
        }
        public void Close()
        {
            Visible = false;
            GetTree().Paused = false;
            Input.MouseMode = Input.MouseModeEnum.Captured;
        }
        public void Toggle() { if (Visible) Close(); else Open(); }
        public bool IsOpen => Visible;
    }
}
