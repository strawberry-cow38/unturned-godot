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
        public Vector2 Center;   // the scope lens's screen position (set by the Viewmodel each frame) so the ladder SWAYS with the glass, not pinned to screen-centre
        /// <summary>The scope's ROLL about the view axis, radians (strawberry: "the range ladder doesnt follow the
        /// scope's rotation"). Tracking only Center made the ladder slide with the glass while staying stubbornly
        /// upright, so a tilted optic showed a vertical drop line against a rolled reticle.</summary>
        public float Roll;
        static readonly int[] Ranges = { 100, 200, 300 };   // the retail 8x/7x ladder marks

        public override void _Ready()
        {
            if (System.Environment.GetEnvironmentVariable("UG_UNITS") is string u && u.Length > 0) Units.TrySet(u);   // render-harness: pick the measurement system for the shot
        }

        public override void _Process(double delta)
        {
            using var _prof = Prof.Scope("ScopeLadder2D");   // the other expression-bodied callback the pass missed
            QueueRedraw();
        }

        public override void _Draw()
        {
            if (!Active) return;
            // Draw around the ORIGIN and let the node's own transform place and rotate it. Rotating each point by
            // hand around `c` would work for the lines and then silently not for DrawString, which takes a
            // position and always renders upright -- the labels would stay level while the ticks tilted.
            Position = Center != Vector2.Zero ? Center : GetViewport().GetVisibleRect().Size * 0.5f;
            Rotation = Roll;
            Vector2 c = Vector2.Zero;
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
