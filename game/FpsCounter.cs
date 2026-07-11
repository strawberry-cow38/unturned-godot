using Godot;

namespace UnturnedGodot
{
    // Simple FPS counter -- top-right corner, yellow text (master 2026-07-11).
    public partial class FpsCounter : CanvasLayer
    {
        Label _label;
        double _acc;

        public override void _Ready()
        {
            Layer = 100;   // over the HUD
            _label = new Label { Text = "FPS --", HorizontalAlignment = HorizontalAlignment.Right };
            _label.AddThemeColorOverride("font_color", new Color(1f, 0.9f, 0.1f));       // yellow
            _label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f));   // dark outline so it reads over the sky
            _label.AddThemeConstantOverride("outline_size", 4);
            _label.AddThemeFontSizeOverride("font_size", 18);
            _label.AnchorLeft = 1f; _label.AnchorRight = 1f;                             // pin to the right edge
            _label.OffsetLeft = -150f; _label.OffsetRight = -12f; _label.OffsetTop = 6f; _label.OffsetBottom = 32f;
            AddChild(_label);
        }

        public override void _Process(double delta)
        {
            _acc += delta;
            if (_acc < 0.25) return;   // refresh 4x/sec so the number isn't a blur
            _acc = 0;
            _label.Text = "FPS " + (int)Engine.GetFramesPerSecond();
        }
    }
}
