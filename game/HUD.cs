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
            // crosshair removed (master 2026-07-08) — no reticle

            _info = new Label();
            _info.AddThemeFontSizeOverride("font_size", 22);
            _info.Position = new Vector2(24, 24);
            AddChild(_info);
        }

        public override void _Process(double delta)
        {
            if (Player != null)
                _info.Text = $"HP {Player.Health:0}    AMMO {Player.Ammo}    KILLS {Player.Kills}    DEATHS {Player.Deaths}";
        }
    }
}
