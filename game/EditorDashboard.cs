using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // Editor dashboard UI, ported from Unturned's EditorDashboardUI (SDG.Unturned, UI/Edit/). The mode-tab bar
    // that switches which sub-editor is active (Objects/Terrain/Environment/Spawns/Volumes), plus Save + Exit
    // and a status/help line. Phase 1: the tabs switch Editor.Mode (the per-mode panels land in later phases).
    public partial class EditorDashboard : CanvasLayer
    {
        public System.Action OnExit;         // Main wires this: tear the editor down + return to the menu
        public Editor Editor;                // set by Main before AddChild

        Label _status;
        readonly Dictionary<EEditorMode, Button> _tabs = new();

        public override void _Ready()
        {
            Layer = 60;

            // top-left: the mode tabs
            var bar = new HBoxContainer { Position = new Vector2(12f, 10f) };
            bar.AddThemeConstantOverride("separation", 6);
            AddChild(bar);
            foreach (EEditorMode m in System.Enum.GetValues(typeof(EEditorMode)))
            {
                var mode = m;
                var b = new Button { Text = m.ToString(), ToggleMode = true, CustomMinimumSize = new Vector2(112f, 40f) };
                b.AddThemeFontSizeOverride("font_size", 16);
                b.Pressed += () => { if (Editor != null) Editor.Mode = mode; };
                bar.AddChild(b);
                _tabs[m] = b;
            }

            // top-right: Save + Exit
            var right = new HBoxContainer();
            right.SetAnchorsPreset(Control.LayoutPreset.TopRight);
            right.Position = new Vector2(-206f, 10f);
            right.AddThemeConstantOverride("separation", 8);
            AddChild(right);
            var save = new Button { Text = "Save", CustomMinimumSize = new Vector2(90f, 40f) };
            save.Pressed += () => Editor?.Save();
            right.AddChild(save);
            var exit = new Button { Text = "Exit", CustomMinimumSize = new Vector2(90f, 40f) };
            exit.Pressed += () => OnExit?.Invoke();
            right.AddChild(exit);

            // bottom-left: status + controls help
            _status = new Label();
            _status.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
            _status.Position = new Vector2(12f, -30f);
            _status.AddThemeColorOverride("font_color", new Color(0.9f, 0.95f, 0.9f));
            _status.AddThemeColorOverride("font_outline_color", Colors.Black);
            _status.AddThemeConstantOverride("outline_size", 3);
            AddChild(_status);

            if (Editor != null) Editor.ModeChanged += _ => Refresh();
            Refresh();
        }

        void Refresh()
        {
            var active = Editor?.Mode ?? EEditorMode.Objects;
            foreach (var kv in _tabs) kv.Value.ButtonPressed = kv.Key == active;
        }

        public override void _Process(double delta)
        {
            if (Editor == null || _status == null) return;
            float spd = Editor.Camera?.Speed ?? 0f;
            _status.Text = $"{Editor.Mode}   ·   hold RMB to fly · WASD move · E/Q up-down · scroll = speed   ·   fly speed {spd:0}   ·   map: {Editor.MapName}";
        }
    }
}
