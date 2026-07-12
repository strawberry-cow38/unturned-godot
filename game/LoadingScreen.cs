using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // Full-screen load overlay (master): a title, a progress bar + %, and the current phase label. After the load it
    // hides the overlay and leaves a top-left per-category TIMING breakdown up for a few seconds so you can see where
    // the load time went. Driven by Main.BuildObjectsTest's async Phase() calls.
    public partial class LoadingScreen : CanvasLayer
    {
        Label _status, _pct, _timings;
        ColorRect _barFill;
        Control _root;
        int _total = 1, _done;
        double _timingsHold = -1.0;

        public override void _Ready()
        {
            Layer = 128;
            _root = new Control(); _root.SetAnchorsPreset(Control.LayoutPreset.FullRect); AddChild(_root);

            var bg = new ColorRect { Color = new Color(0.04f, 0.05f, 0.07f) };
            bg.SetAnchorsPreset(Control.LayoutPreset.FullRect); _root.AddChild(bg);

            var title = new Label { Text = "UNTURNED  •  PEI", HorizontalAlignment = HorizontalAlignment.Center };
            title.SetAnchorsPreset(Control.LayoutPreset.CenterTop); title.Position = new Vector2(-300, 220); title.Size = new Vector2(600, 48);
            title.AddThemeFontSizeOverride("font_size", 44); title.AddThemeColorOverride("font_color", new Color(0.85f, 0.88f, 0.92f));
            _root.AddChild(title);

            // progress bar: a track + a fill rect we resize by percent (crisper than the themed ProgressBar)
            var track = new ColorRect { Color = new Color(0.12f, 0.14f, 0.17f) };
            track.SetAnchorsPreset(Control.LayoutPreset.CenterTop); track.Position = new Vector2(-300, 300); track.Size = new Vector2(600, 22);
            _root.AddChild(track);
            _barFill = new ColorRect { Color = new Color(0.36f, 0.62f, 0.44f), Position = new Vector2(0, 0), Size = new Vector2(0, 22) };
            track.AddChild(_barFill);

            _pct = new Label { Text = "0%", HorizontalAlignment = HorizontalAlignment.Center };
            _pct.SetAnchorsPreset(Control.LayoutPreset.CenterTop); _pct.Position = new Vector2(-300, 326); _pct.Size = new Vector2(600, 22);
            _pct.AddThemeColorOverride("font_color", new Color(0.7f, 0.74f, 0.78f));
            _root.AddChild(_pct);

            _status = new Label { Text = "…", HorizontalAlignment = HorizontalAlignment.Center };
            _status.SetAnchorsPreset(Control.LayoutPreset.CenterTop); _status.Position = new Vector2(-300, 356); _status.Size = new Vector2(600, 24);
            _status.AddThemeColorOverride("font_color", new Color(0.62f, 0.66f, 0.72f));
            _root.AddChild(_status);

            // top-left timing readout, hidden until Finish()
            _timings = new Label { Text = "", Visible = false, Position = new Vector2(16, 12) };
            _timings.AddThemeColorOverride("font_color", new Color(0.85f, 0.88f, 0.6f));
            _timings.AddThemeColorOverride("font_outline_color", Colors.Black);
            _timings.AddThemeConstantOverride("outline_size", 6);
            AddChild(_timings);
        }

        public void SetTotal(int n) => _total = Mathf.Max(1, n);
        public void SetStatus(string s) { if (_status != null) _status.Text = s; }

        public void Advance()
        {
            _done++;
            float f = Mathf.Clamp((float)_done / _total, 0f, 1f);
            if (_barFill != null) _barFill.Size = new Vector2(600f * f, 22f);
            if (_pct != null) _pct.Text = $"{Mathf.RoundToInt(f * 100f)}%";
        }

        // hide the loading overlay, print + show the per-category timing breakdown top-left for a few seconds
        public void Finish(Dictionary<string, double> timings)
        {
            double total = 0; foreach (var kv in timings) total += kv.Value;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"LOAD {total:0} ms");
            foreach (var kv in timings) sb.AppendLine($"  {kv.Key,-10} {kv.Value,6:0} ms  ({(total > 0 ? kv.Value / total * 100 : 0):0}%)");
            GD.Print("[load] " + sb.ToString().Replace("\n", " | "));
            if (_root != null) _root.Visible = false;   // drop the full-screen overlay
            if (_timings != null) { _timings.Text = sb.ToString(); _timings.Visible = true; }
            _timingsHold = 8.0;   // keep the breakdown up ~8s
            SetProcess(true);
        }

        public override void _Process(double delta)
        {
            if (_timingsHold > 0.0)
            {
                _timingsHold -= delta;
                if (_timingsHold <= 0.0) QueueFree();   // done -- remove the whole overlay
            }
        }
    }
}
