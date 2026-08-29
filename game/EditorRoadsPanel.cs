using Godot;

namespace UnturnedGodot
{
    // Road/rail tool buttons under the Environment tab (strawberry 2026-08-19: "add both road tools as actual
    // ui buttons"). Same shape as EditorTerrainPanel -- real buttons rather than dev keybinds, with the keys
    // still working because they drive the identical seam (EditorRoadDraw.SetActive / EditorRoads.SetActive).
    //
    // The tools are MUTUALLY EXCLUSIVE and the panel enforces it. They all bind LMB on the terrain and each
    // keeps its own marker set, so having two live means clicking does two different things at once and the
    // viewport shows two overlapping sets of handles. Turning one on therefore turns the others off, and the
    // button pressed-state shows which one owns the mouse -- the question a user actually has.
    //
    // RIVER joined this panel on 2026-08-24 (strawberry_cow: "why isnt the river tool under the same area as
    // the road spline tools"). It was a terrain BRUSH, which was right about the implementation and wrong
    // about the tool -- you drive it by pulling a curve through anchors, same as a road. It is a third
    // claimant on LMB, so it goes through the same exclusion rather than beside it.
    public partial class EditorRoadsPanel : Control
    {
        readonly EditorRoadDraw _draw;
        readonly EditorRoads _legacy;
        readonly EditorRiver _river;
        Button _drawBtn, _legacyBtn, _riverBtn;
        readonly Button[] _toolBtns = new Button[EditorRoadDraw.ToolNames.Length];
        readonly Button[] _riverToolBtns = new Button[EditorRiver.ToolNames.Length];
        Label _stats, _riverStats;
        HSlider _widthSlider, _depthSlider;

        public EditorRoadsPanel(EditorRoadDraw draw, EditorRoads legacy, EditorRiver river = null)
        { _draw = draw; _legacy = legacy; _river = river; }

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

            if (_river != null) BuildRiverSection(box);
        }

        void BuildRiverSection(VBoxContainer box)
        {
            box.AddChild(new HSeparator());
            var head = new Label { Text = "RIVER" };
            head.AddThemeFontSizeOverride("font_size", 18);
            box.AddChild(head);

            _riverBtn = new Button { Text = "Carve river  (V)", ToggleMode = true };
            _riverBtn.Pressed += () => Activate(draw: false, legacy: false, river: _riverBtn.ButtonPressed);
            box.AddChild(_riverBtn);

            box.AddChild(Dim("Shape"));
            var shapes = new HBoxContainer();
            for (int i = 0; i < EditorRiver.ToolNames.Length; i++)
            {
                int ti = i;
                var tb = new Button { Text = EditorRiver.ToolNames[i], ToggleMode = true, CustomMinimumSize = new Vector2(68, 0) };
                tb.Pressed += () => { _river?.SetTool((EditorRiver.ETool)ti); Activate(draw: false, legacy: false, river: true); };
                shapes.AddChild(tb);
                _riverToolBtns[i] = tb;
            }
            box.AddChild(shapes);

            // Sliders rather than only keys: width and depth are the two numbers you actually tune per river,
            // and reaching for a remembered bracket key mid-draw is the thing that made this feel like a
            // debug brush rather than a tool.
            _widthSlider = NumRow(box, "Half-width", EditorRiver.MinHalfWidth, EditorRiver.MaxHalfWidth, 0.5f,
                                  _river.HalfWidth, v => _river.SetHalfWidth((float)v));
            _depthSlider = NumRow(box, "Depth", EditorRiver.MinDepth, EditorRiver.MaxDepth, 0.25f,
                                  _river.Depth, v => _river.SetDepth((float)v));

            // Re-cut existing rivers with today's carve code. A saved river replays BAKED geometry on load, so
            // a carve fix cannot reach one that already exists -- without this button the only way to pick up a
            // fix is to delete the river and draw it again.
            var rebuild = new Button { Text = "Rebuild existing rivers" };
            rebuild.Pressed += () => { int n = _river?.RebuildExisting() ?? 0; if (_riverStats != null) _riverStats.Text = $"rebuilt {n} river(s)"; };
            box.AddChild(rebuild);

            box.AddChild(Dim("Straight: click both ends.\nCurve: click each bend, Enter to carve.\nFreehand: drag along the ground.\nDel drops the last point · Esc cancels.\nPreview shows the centreline AND both banks.\nCarving cuts the terrain -- there is no undo."));
            _riverStats = Dim("");
            box.AddChild(_riverStats);
        }

        HSlider NumRow(VBoxContainer box, string label, float min, float max, float step, float val, System.Action<double> onSet)
        {
            var lab = Dim($"{label}  {val:0.#}m");
            box.AddChild(lab);
            var sl = new HSlider { MinValue = min, MaxValue = max, Step = step, Value = val, CustomMinimumSize = new Vector2(0, 18) };
            sl.ValueChanged += v => { onSet(v); lab.Text = $"{label}  {v:0.#}m"; };
            box.AddChild(sl);
            sl.SetMeta("label", lab);
            sl.SetMeta("prefix", label);
            return sl;
        }

        void Activate(bool draw, bool legacy = false, bool river = false)
        {
            // One owner of the mouse at a time -- see the class comment. Ordered draw > legacy > river so a
            // single call can never leave two on, whatever combination the caller passed.
            if (draw) { legacy = false; river = false; }
            else if (legacy) river = false;
            _draw?.SetActive(draw);
            _legacy?.SetActive(legacy);
            _river?.SetActive(river);
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

            if (_riverBtn != null && _river != null) _riverBtn.ButtonPressed = _river.Carving;
            if (_river != null)
                for (int i = 0; i < _riverToolBtns.Length; i++)
                    if (_riverToolBtns[i] != null) _riverToolBtns[i].ButtonPressed = _river.Carving && (int)_river.Tool == i;
            // The keys move width/depth too, so the sliders read the tool rather than trusting their own last
            // drag -- same reason the tool buttons do.
            SyncSlider(_widthSlider, _river?.HalfWidth);
            SyncSlider(_depthSlider, _river?.Depth);
        }

        static void SyncSlider(HSlider sl, float? v)
        {
            if (sl == null || v == null) return;
            if (Mathf.Abs((float)sl.Value - v.Value) < 0.001f) return;
            sl.SetValueNoSignal(v.Value);   // NoSignal: writing the tool's value back must not call SetX again
            if (sl.GetMeta("label").Obj is Label lab) lab.Text = $"{sl.GetMeta("prefix")}  {v.Value:0.#}m";
        }

        public override void _Process(double delta)
        {
            if (!Visible) return;
            Sync();
            if (_stats != null && _draw != null)
                _stats.Text = $"{_draw.JunctionNodeCount} nodes · {_draw.RealJunctionCount} junctions";
            if (_riverStats != null && _river != null)
                _riverStats.Text = $"{_river.AnchorCount} placed · {_river.RiverSegmentCount} river segments";
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
        public bool DebugRiverButtonOn => _riverBtn?.ButtonPressed ?? false;
        /// <summary>Click the river button through its real handler, same contract as DebugClick.</summary>
        public void DebugClickRiver()
        {
            if (_riverBtn == null) return;
            _riverBtn.ButtonPressed = !_riverBtn.ButtonPressed;
            _riverBtn.EmitSignal(BaseButton.SignalName.Pressed);
        }
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
