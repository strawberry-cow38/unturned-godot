using Godot;

namespace UnturnedGodot
{
    // Terrain tool panel (ported from EditorTerrainUI): BUTTONS for the heightmap tools (Raise/Lower/Flatten/Smooth/Ramp)
    // + a Materials paint section with per-layer buttons, and radius/strength sliders. Drives EditorTerrain. Shown only in
    // the Terrain tab. This replaces the dev keybinds with real buttons (master: "terrain tools should use buttons").
    public partial class EditorTerrainPanel : Control
    {
        readonly EditorTerrain _terr;
        public EditorTerrainPanel(EditorTerrain terr) { _terr = terr; }

        public override void _Ready()
        {
            Position = new Vector2(12, 60);
            var panel = new PanelContainer();
            AddChild(panel);
            var box = new VBoxContainer { CustomMinimumSize = new Vector2(210, 0) };
            box.AddThemeConstantOverride("separation", 4);
            panel.AddChild(box);

            var head = new Label { Text = "TERRAIN" };
            head.AddThemeFontSizeOverride("font_size", 18);
            box.AddChild(head);

            box.AddChild(Dim("Sculpt tool"));
            for (int i = 0; i < EditorTerrain.BrushNames.Length; i++)
            {
                int bi = i;
                var b = new Button { Text = EditorTerrain.BrushNames[i] };
                b.Pressed += () => _terr.SelectBrush(bi);
                box.AddChild(b);
            }

            box.AddChild(new HSeparator());
            box.AddChild(Dim("Materials — paint texture"));
            var grid = new GridContainer { Columns = 2 };
            for (int i = 0; i < _terr.LayerCount; i++)
            {
                int li = i;
                var b = new Button { Text = _terr.LayerName(i), CustomMinimumSize = new Vector2(98, 0) };
                b.Pressed += () => _terr.SelectLayer(li);
                grid.AddChild(b);
            }
            box.AddChild(grid);

            box.AddChild(new HSeparator());
            box.AddChild(Slider("Radius", 6, 140, _terr.RadiusVal, v => _terr.RadiusVal = (float)v));
            box.AddChild(Slider("Strength", 1, 60, _terr.StrengthVal, v => _terr.StrengthVal = (float)v));
        }

        static Label Dim(string t)
        {
            var l = new Label { Text = t };
            l.AddThemeColorOverride("font_color", new Color(0.72f, 0.78f, 0.83f));
            l.AddThemeFontSizeOverride("font_size", 12);
            return l;
        }

        static VBoxContainer Slider(string name, float mn, float mx, float val, System.Action<double> onChange)
        {
            var v = new VBoxContainer();
            var lbl = new Label { Text = $"{name}: {val:0}" };
            v.AddChild(lbl);
            var s = new HSlider { MinValue = mn, MaxValue = mx, Value = val, CustomMinimumSize = new Vector2(200, 0), FocusMode = FocusModeEnum.None };
            s.ValueChanged += x => lbl.Text = $"{name}: {x:0}";
            s.ValueChanged += onChange.Invoke;
            v.AddChild(s);
            return v;
        }
    }
}
