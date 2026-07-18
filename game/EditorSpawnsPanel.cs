using Godot;

namespace UnturnedGodot
{
    // Spawns tool panel (ported from EditorSpawnsUI): category buttons + Add/Remove toggle + per-category options
    // (player Alternate / vehicle+item+animal type stepper) + rotation & remove-radius sliders. Drives EditorSpawns;
    // the options section rebuilds when the category changes. Master: "spawns too complex, use buttons per src".
    public partial class EditorSpawnsPanel : Control
    {
        readonly EditorSpawns _spawns;
        VBoxContainer _dynamic;   // per-category options (rebuilt on category change)
        public EditorSpawnsPanel(EditorSpawns spawns) { _spawns = spawns; }

        public override void _Ready()
        {
            Position = new Vector2(12, 60);
            var panel = new PanelContainer();
            AddChild(panel);
            var box = new VBoxContainer { CustomMinimumSize = new Vector2(200, 0) };
            box.AddThemeConstantOverride("separation", 4);
            panel.AddChild(box);

            var head = new Label { Text = "SPAWNS" };
            head.AddThemeFontSizeOverride("font_size", 18);
            box.AddChild(head);

            box.AddChild(Dim("Category"));
            var cats = new GridContainer { Columns = 2 };
            for (int i = 0; i < EditorSpawns.CategoryNames.Length; i++)
            {
                int ci = i;
                var b = new Button { Text = EditorSpawns.CategoryNames[i], CustomMinimumSize = new Vector2(94, 0) };
                b.Pressed += () => _spawns.SetCategoryTo(ci);
                cats.AddChild(b);
            }
            box.AddChild(cats);

            box.AddChild(new HSeparator());
            box.AddChild(Dim("Click terrain to:"));
            var mode = new HBoxContainer();
            var grp = new ButtonGroup();
            var add = new Button { Text = "Add", ToggleMode = true, ButtonPressed = true, ButtonGroup = grp, CustomMinimumSize = new Vector2(94, 0) };
            add.Pressed += () => _spawns.RemoveMode = false;
            var rem = new Button { Text = "Remove", ToggleMode = true, ButtonGroup = grp, CustomMinimumSize = new Vector2(94, 0) };
            rem.Pressed += () => _spawns.RemoveMode = true;
            mode.AddChild(add); mode.AddChild(rem);
            box.AddChild(mode);

            box.AddChild(new HSeparator());
            _dynamic = new VBoxContainer();
            _dynamic.AddThemeConstantOverride("separation", 4);
            box.AddChild(_dynamic);
            RebuildDynamic();
            _spawns.CategoryChanged += RebuildDynamic;
        }

        void RebuildDynamic()
        {
            foreach (var c in _dynamic.GetChildren()) c.QueueFree();
            if (_spawns.ShowsAlt)
            {
                var alt = new CheckBox { Text = "Alternate spawn", ButtonPressed = _spawns.Alt, FocusMode = FocusModeEnum.None };
                alt.Toggled += on => _spawns.Alt = on;
                _dynamic.AddChild(alt);
            }
            if (_spawns.ShowsTypes)
            {
                var h = new HBoxContainer();
                var lbl = new Label { Text = _spawns.TypeLabel(_spawns.TypeIdx) };
                var prev = new Button { Text = "◀" };
                prev.Pressed += () => { _spawns.SetType((_spawns.TypeIdx - 1 + _spawns.NumTypes) % _spawns.NumTypes); lbl.Text = _spawns.TypeLabel(_spawns.TypeIdx); };
                var next = new Button { Text = "▶" };
                next.Pressed += () => { _spawns.SetType((_spawns.TypeIdx + 1) % _spawns.NumTypes); lbl.Text = _spawns.TypeLabel(_spawns.TypeIdx); };
                h.AddChild(new Label { Text = "Type:" }); h.AddChild(prev); h.AddChild(lbl); h.AddChild(next);
                _dynamic.AddChild(h);
            }
            _dynamic.AddChild(Slider("Facing", 0, 360, _spawns.RotationDeg, v => _spawns.RotationDeg = (float)v));
            _dynamic.AddChild(Slider("Remove radius", 2, 30, _spawns.RemoveRadius, v => _spawns.RemoveRadius = (int)v));
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
            var s = new HSlider { MinValue = mn, MaxValue = mx, Value = val, CustomMinimumSize = new Vector2(190, 0), FocusMode = FocusModeEnum.None };
            s.ValueChanged += x => lbl.Text = $"{name}: {x:0}";
            s.ValueChanged += onChange.Invoke;
            v.AddChild(s);
            return v;
        }
    }
}
