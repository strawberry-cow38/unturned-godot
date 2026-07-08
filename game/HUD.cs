using Godot;

namespace UnturnedGodot
{
    // Minimal slice HUD: crosshair + ammo/kills readout. The real Glazier_Godot UI port comes later; this
    // is the smoke HUD so the playable loop reads on screen.
    public partial class HUD : CanvasLayer
    {
        public PlayerController Player;
        Label _info;

        public override void _Ready()
        {
            var cross = new Label { Text = "+" };
            cross.AddThemeFontSizeOverride("font_size", 34);
            cross.SetAnchorsPreset(Control.LayoutPreset.Center);
            cross.HorizontalAlignment = HorizontalAlignment.Center;
            cross.VerticalAlignment = VerticalAlignment.Center;
            cross.Position -= new Vector2(10, 20);
            AddChild(cross);

            _info = new Label();
            _info.AddThemeFontSizeOverride("font_size", 22);
            _info.Position = new Vector2(24, 24);
            AddChild(_info);
        }

        public override void _Process(double delta)
        {
            if (Player != null)
                _info.Text = $"AMMO {Player.Ammo}    KILLS {Player.Kills}";
        }
    }
}
