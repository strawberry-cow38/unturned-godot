using Godot;

namespace UnturnedGodot
{
    // ESC pause menu with live viewmodel-tuning sliders: FOV + a single uniform offset (X/Y/Z) applied to ALL guns.
    // Writes Viewmodel.TuneFov / Viewmodel.TuneOffset live (Viewmodel._Process reads them every frame), so master can
    // dial the viewmodel in-game. Toggled by PlayerController's Escape handler, which frees the mouse while it's open.
    public partial class PauseMenu : CanvasLayer
    {
        public override void _Ready()
        {
            Layer = 60;
            Visible = false;

            var dim = new ColorRect { Color = new Color(0f, 0f, 0f, 0.55f) };
            dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(dim);

            var center = new CenterContainer();
            center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(center);

            var panel = new PanelContainer();
            center.AddChild(panel);

            var margin = new MarginContainer();
            foreach (var s in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
                margin.AddThemeConstantOverride(s, 22);
            panel.AddChild(margin);

            var vbox = new VBoxContainer { CustomMinimumSize = new Vector2(440, 0) };
            vbox.AddThemeConstantOverride("separation", 12);
            margin.AddChild(vbox);

            var title = new Label { Text = "VIEWMODEL", HorizontalAlignment = HorizontalAlignment.Center };
            title.AddThemeFontSizeOverride("font_size", 26);
            vbox.AddChild(title);

            AddSlider(vbox, "FOV", 40f, 90f, 1f, Viewmodel.TuneFov, v => Viewmodel.TuneFov = (float)v);
            AddSlider(vbox, "Offset X", -0.5f, 0.5f, 0.005f, Viewmodel.TuneOffset.X,
                v => { var o = Viewmodel.TuneOffset; o.X = (float)v; Viewmodel.TuneOffset = o; });
            AddSlider(vbox, "Offset Y", -0.5f, 0.5f, 0.005f, Viewmodel.TuneOffset.Y,
                v => { var o = Viewmodel.TuneOffset; o.Y = (float)v; Viewmodel.TuneOffset = o; });
            AddSlider(vbox, "Offset Z", -0.5f, 0.5f, 0.005f, Viewmodel.TuneOffset.Z,
                v => { var o = Viewmodel.TuneOffset; o.Z = (float)v; Viewmodel.TuneOffset = o; });

            var hint = new Label { Text = "esc to close", HorizontalAlignment = HorizontalAlignment.Center, Modulate = new Color(1f, 1f, 1f, 0.5f) };
            vbox.AddChild(hint);
        }

        // A labeled slider row: name | HSlider | live value readout. onChange fires on drag; the readout follows.
        static void AddSlider(VBoxContainer parent, string name, float min, float max, float step, float val, System.Action<double> onChange)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 12);
            parent.AddChild(row);

            row.AddChild(new Label { Text = name, CustomMinimumSize = new Vector2(90, 0) });

            var slider = new HSlider
            {
                MinValue = min, MaxValue = max, Step = step, Value = val,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(230, 0),
            };
            row.AddChild(slider);

            var valLabel = new Label { Text = val.ToString("0.###"), CustomMinimumSize = new Vector2(64, 0), HorizontalAlignment = HorizontalAlignment.Right };
            row.AddChild(valLabel);

            slider.ValueChanged += v => { onChange(v); valLabel.Text = v.ToString("0.###"); };
        }

        public void Toggle() => Visible = !Visible;
        public bool IsOpen => Visible;
    }
}
