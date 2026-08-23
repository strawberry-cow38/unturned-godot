using Godot;

namespace UnturnedGodot
{
    // ESC pause menu (master): FREEZES the sim (GetTree().Paused) while staying interactive itself
    // (ProcessMode.Always), so the world halts in the background but the menu UI still responds. Replaces the
    // old viewmodel-tuning "offset" slider menu, which wasn't needed.
    public partial class PauseMenu : CanvasLayer
    {
        Control _root, _gfx;
        public FreezeMode Freeze;      // set by BuildPlayable; null in demos
        public Node WorldRoot;         // where the freecam is parented

        public override void _Ready()
        {
            Layer = 60;
            Visible = false;
            ProcessMode = Node.ProcessModeEnum.Always;   // keep the menu alive + its input flowing while the tree is paused

            var dim = new ColorRect { Color = UITheme.Scrim, MouseFilter = Control.MouseFilterEnum.Stop };
            dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(dim);

            var center = new CenterContainer();
            center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(center);

            var panel = new PanelContainer();
            _root = panel;
            center.AddChild(panel);

            // The graphics panel is a SIBLING that starts hidden, not a separate screen: the pause menu already
            // freezes the tree, and pushing/popping scenes from a paused tree is how you end up unable to unpause.
            var gfxPanel = new PanelContainer { Visible = false };
            _gfx = gfxPanel;
            center.AddChild(gfxPanel);
            gfxPanel.AddChild(GraphicsPanel.Build(this, () => { gfxPanel.Visible = false; panel.Visible = true; }));
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

            var gfx = new Button { Text = "Graphics", CustomMinimumSize = new Vector2(0, 46) };
            gfx.Pressed += () => { _root.Visible = false; _gfx.Visible = true; };
            vbox.AddChild(gfx);

            // FREEZE MODE: hand the paused world over to a freecam instead of resuming. The tree is ALREADY paused
            // here, so this hides the menu and lets FreezeMode keep it that way -- it never unpauses in between.
            var freeze = new Button { Text = "Freeze Mode", CustomMinimumSize = new Vector2(0, 46) };
            freeze.Pressed += () =>
            {
                if (Freeze == null) return;
                Visible = false;
                Freeze.Enter(WorldRoot);
            };
            vbox.AddChild(freeze);

            var toMenu = new Button { Text = "Exit to Menu", CustomMinimumSize = new Vector2(0, 46) };
            toMenu.Pressed += ExitToMenu;
            vbox.AddChild(toMenu);

            var hint = new Label { Text = "esc to resume", HorizontalAlignment = HorizontalAlignment.Center, Modulate = new Color(1f, 1f, 1f, 0.5f) };
            vbox.AddChild(hint);
        }

        // ESC while paused resumes (the player controller is paused + can't, so the menu handles it itself).
        public override void _UnhandledInput(InputEvent e)
        {
            if (!Visible || e is not InputEventKey { Pressed: true, Keycode: Key.Escape }) return;
            // ESC inside the graphics sub-panel steps BACK to the pause menu rather than closing everything. Closing
            // straight to the game from a sub-panel loses the level you were on and is the classic escape-key bug.
            if (_gfx != null && _gfx.Visible) { _gfx.Visible = false; _root.Visible = true; }
            else Close();
            GetViewport().SetInputAsHandled();
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

        // Tear the whole game down and go back to the main menu: reload Main.tscn (launched with no mode args ->
        // Main._Ready rebuilds the default MainMenu). Unpause first so the fresh scene doesn't start frozen.
        void ExitToMenu()
        {
            GetTree().Paused = false;
            Input.MouseMode = Input.MouseModeEnum.Visible;
            GetTree().ReloadCurrentScene();
        }
    }
}
