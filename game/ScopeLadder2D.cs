using Godot;

namespace UnturnedGodot
{
    // The scope range ladder (8x/7x): a vertical drop line with tick marks + range labels below the reticle,
    // matching retail's InstantiateScopeDistanceMarkers. Text uses the global Units setting (metric/imperial/both).
    // Drawn as a 2D overlay centered on screen (retail draws these as UI too). Driven by the Viewmodel: Active is
    // set true while ADS'd with a ladder scope. Lives on a CanvasLayer the Viewmodel owns.
    public partial class ScopeLadder2D : Node2D
    {
        public bool Active;
        static readonly int[] Ranges = { 100, 200, 300 };   // the retail 8x/7x ladder marks

        public override void _Ready()
        {
            if (System.Environment.GetEnvironmentVariable("UG_UNITS") is string u && u.Length > 0) Units.TrySet(u);   // render-harness: pick the measurement system for the shot
        }

        public override void _Process(double delta) => QueueRedraw();

        public override void _Draw()
        {
            if (!Active) return;
            Vector2 c = GetViewport().GetVisibleRect().Size * 0.5f;
            var font = ThemeDB.FallbackFont;
            int fs = 15;
            var black = new Color(0f, 0f, 0f, 0.92f);
            // vertical drop line from just below the crosshair centre down past the last mark
            float top = c.Y + 14f;
            float step = 34f;                                    // px between range marks
            float bottom = top + step * Ranges.Length + 8f;
            DrawLine(new Vector2(c.X, top), new Vector2(c.X, bottom), black, 1.5f, true);
            for (int i = 0; i < Ranges.Length; i++)
            {
                float y = top + step * (i + 1);
                DrawLine(new Vector2(c.X - 5f, y), new Vector2(c.X + 5f, y), black, 1.5f, true);   // tick
                string label = Units.RangeLabel(Ranges[i]);
                DrawString(font, new Vector2(c.X + 12f, y + fs * 0.35f), label, HorizontalAlignment.Left, -1, fs, black);   // label to the right of the tick
            }
        }
    }
}
