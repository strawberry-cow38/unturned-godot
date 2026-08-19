using Godot;

namespace UnturnedGodot
{
    // Road/rail tool buttons under the Environment tab (strawberry 2026-08-19: "add both road tools as actual
    // ui buttons"). Same shape as EditorTerrainPanel -- real buttons rather than dev keybinds, with the keys
    // still working because they drive the identical seam (EditorRoadDraw.SetActive / EditorRoads.SetActive).
    //
    // The two tools are MUTUALLY EXCLUSIVE and the panel enforces it. They both bind LMB on the terrain and
    // both keep their own marker set, so having both live means clicking does two different things at once and
    // the viewport shows two overlapping sets of handles. Turning one on therefore turns the other off, and
    // the button pressed-state shows which one owns the mouse -- the question a user actually has.
    public partial class EditorRoadsPanel : Control
    {
        readonly EditorRoadDraw _draw;
        readonly EditorRoads _legacy;
        Button _drawBtn, _legacyBtn;
        readonly Button[] _toolBtns = new Button[EditorRoadDraw.ToolNames.Length];
        Label _stats;

        public EditorRoadsPanel(EditorRoadDraw draw, EditorRoads legacy) { _draw = draw; _legacy = legacy; }

        public override void _Ready()
        {
            Position = new Vector2(12, 60);
            var panel = new PanelContainer();
            AddChild(panel);
            var box = new VBoxContainer { CustomMinimumSize = new Vector2(228, 0) };
            box.AddThemeConstantOverride("separation", 4);
            panel.AddChild(box);

            var head = new Label { Text = "ROADS & RAIL" };
            head.AddThemeFontSizeOverride("font_size", 18);
            box.AddChild(head);

            box.AddChild(Dim("Tool"));
            _drawBtn = new Button { Text = "Draw road/rail  (R)", ToggleMode = true };
            _drawBtn.Pressed += () => Activate(draw: _drawBtn.ButtonPressed);
            box.AddChild(_drawBtn);

            _legacyBtn = new Button { Text = "Legacy node tool  (Shift+R)", ToggleMode = true };
            _legacyBtn.Pressed += () => Activate(draw: !_legacyBtn.ButtonPressed && _drawBtn.ButtonPressed, legacy: _legacyBtn.ButtonPressed);
            box.AddChild(_legacyBtn);

            box.AddChild(new HSeparator());
            box.AddChild(Dim("Shape"));
            var shapes = new HBoxContainer();
            for (int i = 0; i < EditorRoadDraw.ToolNames.Length; i++)
            {
                int ti = i;
                var tb = new Button { Text = EditorRoadDraw.ToolNames[i], ToggleMode = true, CustomMinimumSize = new Vector2(68, 0) };
                tb.Pressed += () => { _draw?.SetTool((EditorRoadDraw.ETool)ti); _draw?.SetActive(true); _legacy?.SetActive(false); Sync(); };
                shapes.AddChild(tb);
                _toolBtns[i] = tb;
            }
            box.AddChild(shapes);

            box.AddChild(new HSeparator());
            box.AddChild(Dim("Straight/Curve: click to place ends\n(Curve takes a middle control click).\nFreehand: drag on the ground.\nEnds snap to nodes, rail ends, and anywhere\nALONG a spline -- which splits it into a\njunction right where you aimed.\nDrag a node to move every rail bound to it."));
            box.AddChild(new HSeparator());
            _stats = Dim("");
            box.AddChild(_stats);
        }

        void Activate(bool draw, bool legacy = false)
        {
            // One owner of the mouse at a time -- see the class comment.
            if (draw) legacy = false;
            _draw?.SetActive(draw);
            _legacy?.SetActive(legacy);
            Sync();
        }

        /// <summary>Push the tools' REAL state back onto the buttons. The keys still work, and a button that
        /// says "Draw" is on while the tool is off is worse than no button at all -- so the panel reads the
        /// tools every frame rather than trusting its own last click.</summary>
        void Sync()
        {
            if (_drawBtn != null && _draw != null) _drawBtn.ButtonPressed = _draw.Drawing;
            if (_legacyBtn != null && _legacy != null) _legacyBtn.ButtonPressed = _legacy.Paving;
            // Shape buttons show the ACTIVE sub-tool, and only while the draw tool owns the mouse -- a lit
            // "Straight" while the legacy node tool is running would be a straight lie about what LMB does.
            if (_draw != null)
                for (int i = 0; i < _toolBtns.Length; i++)
                    if (_toolBtns[i] != null) _toolBtns[i].ButtonPressed = _draw.Drawing && (int)_draw.Tool == i;
        }

        public override void _Process(double delta)
        {
            if (!Visible) return;
            Sync();
            if (_stats != null && _draw != null)
                _stats.Text = $"{_draw.JunctionNodeCount} nodes · {_draw.RealJunctionCount} junctions";
        }

        // --- test seams: the panel's logic without a mouse ---
        /// <summary>Click a button for real: flip its toggle state and fire its Pressed signal, so the test
        /// goes through the SAME handler the mouse does. The first version of this seam re-implemented the
        /// handlers' argument logic instead and got it wrong -- it reported the legacy button as doing nothing
        /// when the button itself was fine. A seam that reimplements the path it is meant to test can only
        /// ever tell you about itself.</summary>
        public void DebugClick(bool draw)
        {
            var b = draw ? _drawBtn : _legacyBtn;
            if (b == null) return;
            b.ButtonPressed = !b.ButtonPressed;      // ToggleMode: the state flips BEFORE Pressed fires
            b.EmitSignal(BaseButton.SignalName.Pressed);
        }
        public bool DebugDrawButtonOn => _drawBtn?.ButtonPressed ?? false;
        public bool DebugLegacyButtonOn => _legacyBtn?.ButtonPressed ?? false;
        public void DebugSync() => Sync();

        static Label Dim(string t)
        {
            var l = new Label { Text = t };
            l.AddThemeColorOverride("font_color", new Color(0.72f, 0.78f, 0.83f));
            l.AddThemeFontSizeOverride("font_size", 12);
            return l;
        }
    }
}
