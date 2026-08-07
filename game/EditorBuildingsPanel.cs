using Godot;

namespace UnturnedGodot
{
    // The building tool's panel: lay walls, arm an opening preset, pick a palette. Lives under the Level tab
    // beside the object browser, because a wall and a prop are both things you place into a level.
    //
    // Every control here writes a plain field on EditorBuildings and nothing else -- no tool state lives in the
    // UI. The tool was fully built and registered before this panel existed and was therefore unreachable:
    // nothing turned it on, so from the outside it did not exist. Logic-first is worth nothing until something
    // calls it.
    public partial class EditorBuildingsPanel : Control
    {
        readonly EditorBuildings _b;
        Button _draw;
        readonly System.Collections.Generic.List<Button> _arch = new();
        Label _thickLbl;

        public EditorBuildingsPanel(EditorBuildings b) { _b = b; }

        public override void _Ready()
        {
            Position = new Vector2(12, 60);
            var panel = new PanelContainer();
            AddChild(panel);
            var box = new VBoxContainer { CustomMinimumSize = new Vector2(252, 0) };
            box.AddThemeConstantOverride("separation", 4);
            panel.AddChild(box);

            var head = new Label { Text = "BUILDINGS" };
            head.AddThemeFontSizeOverride("font_size", 18);
            box.AddChild(head);

            box.AddChild(Dim("Wall"));
            _draw = new Button { Text = "Draw wall", ToggleMode = true };
            _draw.Pressed += () =>
            {
                _b.WallDrawMode = _draw.ButtonPressed;
                if (_b.WallDrawMode) { _b.Arm(-1); foreach (var a in _arch) a.ButtonPressed = false; }
            };
            box.AddChild(_draw);

            // Thickness is a slider rather than an exterior/interior toggle: 0.70 and 0.50 are the two
            // measured clusters, not the only two legal answers, and the ask was for things to be tweakable.
            _thickLbl = new Label { Text = $"Thickness: {_b.NewWallThickness:0.00}" };
            box.AddChild(_thickLbl);
            var th = new HSlider
            {
                MinValue = 0.2, MaxValue = 1.2, Step = 0.05, Value = _b.NewWallThickness,
                CustomMinimumSize = new Vector2(240, 0), FocusMode = FocusModeEnum.None,
            };
            th.ValueChanged += v =>
            {
                _b.NewWallThickness = (float)v;
                _thickLbl.Text = $"Thickness: {v:0.00}";
                if (_b.SelectedWall != null) { _b.SelectedWall.Thickness = (float)v; _b.SelectedWall.Rebuild(); }
            };
            box.AddChild(th);

            box.AddChild(new HSeparator());
            box.AddChild(Dim("Opening — click a wall to place"));
            var grid = new GridContainer { Columns = 2 };
            for (int i = 0; i < EditorBuildings.Archetypes.Length; i++)
            {
                int ai = i;
                var a = EditorBuildings.Archetypes[i];
                var btn = new Button
                {
                    Text = $"{a.Name}  {a.Width:0.#}×{a.Height:0.#}",
                    ToggleMode = true,
                    CustomMinimumSize = new Vector2(120, 0),
                    TooltipText = a.FloorPinned ? "sits on the floor" : $"sill {a.Sill:0.##}m",
                };
                btn.Pressed += () =>
                {
                    bool on = btn.ButtonPressed;
                    foreach (var o in _arch) if (o != btn) o.ButtonPressed = false;
                    _b.Arm(on ? ai : -1);
                    if (on) { _b.WallDrawMode = false; _draw.ButtonPressed = false; }
                };
                _arch.Add(btn);
                grid.AddChild(btn);
            }
            box.AddChild(grid);

            box.AddChild(new HSeparator());
            box.AddChild(Dim("Material — a retail palette"));
            var drop = new OptionButton { CustomMinimumSize = new Vector2(240, 0) };
            for (int i = 0; i < WallMaterials.Count; i++) drop.AddItem($"{i}  {WallMaterials.At(i).Name}", i);
            if (WallMaterials.Count > 0) drop.Select(0);
            drop.ItemSelected += id => _b.SelectMaterial((int)id);
            box.AddChild(drop);
            box.AddChild(Dim(WallMaterials.Count > 0
                ? $"{WallMaterials.Count} sampled from the retail buildings"
                : "no palettes loaded — check content/wall_palettes.tsv"));
        }

        static Label Dim(string t)
        {
            var l = new Label { Text = t };
            l.AddThemeColorOverride("font_color", new Color(0.72f, 0.78f, 0.83f));
            l.AddThemeFontSizeOverride("font_size", 12);
            return l;
        }
    }
}
