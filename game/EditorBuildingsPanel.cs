using Godot;
using UnturnedSim;

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
        Button _drawFloor, _drawRoof, _room, _del;

        enum Tool { None, Wall, Room, Floor, Roof, Opening, Delete }

        /// <summary>Exactly one tool is active. Selection used to be done by each button clearing the others
        /// by hand, in five places, and every one of them cleared a DIFFERENT subset -- the opening presets
        /// only turned off wall-draw, so arming a window while the room tool was live left both armed and the
        /// next click did whichever the input handler reached first. strawberry_cow: "prevent multiple tools
        /// being selected at once, ie wall and an opening." One place that sets all of them is the only way
        /// this stays true as tools get added.</summary>
        void SetTool(Tool t, int archetype = -1)
        {
            _b.WallDrawMode = t == Tool.Wall;
            _b.RoomDrawMode = t == Tool.Room;
            _b.SlabDrawMode = t == Tool.Floor || t == Tool.Roof;
            _b.DeleteDrawMode = t == Tool.Delete;
            if (t == Tool.Floor) _b.SlabDrawKind = SurfaceKind.Floor;
            if (t == Tool.Roof) _b.SlabDrawKind = SurfaceKind.Roof;
            _b.Arm(t == Tool.Opening ? archetype : -1);

            if (_draw != null) _draw.ButtonPressed = t == Tool.Wall;
            if (_room != null) _room.ButtonPressed = t == Tool.Room;
            if (_drawFloor != null) _drawFloor.ButtonPressed = t == Tool.Floor;
            if (_drawRoof != null) _drawRoof.ButtonPressed = t == Tool.Roof;
            if (_del != null) _del.ButtonPressed = t == Tool.Delete;
            for (int i = 0; i < _arch.Count; i++)
                _arch[i].ButtonPressed = t == Tool.Opening && i == archetype;
        }
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

            // BUILD: the four things you reach for constantly, big, then the destructive one set apart.
            // Icons rather than words was the ask; the words survive as tooltips, so nothing is lost -- you
            // just stop reading a column of sentences to find the button you already know the shape of.
            box.AddChild(Dim("BUILD"));
            var build = new GridContainer { Columns = 4 };
            build.AddThemeConstantOverride("h_separation", 4);
            _draw = ToolButton(build, EditorIcons.Glyph.Wall, 60, "Draw wall",
                               "press and drag — snaps to the 3 m grid",
                               () => SetTool(_draw.ButtonPressed ? Tool.Wall : Tool.None));
            _room = ToolButton(build, EditorIcons.Glyph.Room, 60, "Draw room",
                               "drag a rectangle — four walls on the grid, shared edges merged",
                               () => SetTool(_room.ButtonPressed ? Tool.Room : Tool.None));
            _drawFloor = ToolButton(build, EditorIcons.Glyph.Floor, 60, "Draw floor",
                                    "drag the footprint of a floor slab",
                                    () => SetTool(_drawFloor.ButtonPressed ? Tool.Floor : Tool.None));
            _drawRoof = ToolButton(build, EditorIcons.Glyph.Roof, 60, "Draw roof",
                                   "drag a footprint — a pitched roof becomes a whole gable over it",
                                   () => SetTool(_drawRoof.ButtonPressed ? Tool.Roof : Tool.None));
            box.AddChild(build);

            var second = new GridContainer { Columns = 4 };
            second.AddThemeConstantOverride("h_separation", 4);
            _del = ToolButton(second, EditorIcons.Glyph.Delete, 44, "Delete / cut",
                              "click a wall to remove it, or drag along one to cut a piece out",
                              () => SetTool(_del.ButtonPressed ? Tool.Delete : Tool.None));
            IconAction(second, EditorIcons.Glyph.Foundation, 44, "Add foundation",
                       $"a skirt under the walls — retail sinks {WallOpenings.FoundationDepth:0.#} m",
                       () => Say(_b.AddFoundation() is int n && n > 0 ? $"foundation under {n} wall(s)"
                                                                      : "draw some walls first"));
            IconAction(second, EditorIcons.Glyph.Import, 44, "Import",
                       "port the retail building selected below onto the stage", () => DoImport());
            IconAction(second, EditorIcons.Glyph.Bake, 44, "Bake to prop",
                       "turn this building into a placeable prop in the Level tab", () => DoBake());
            box.AddChild(second);

            // No "paint back side" checkbox: click the face you mean and the ghost shows which one you have.
            box.AddChild(Dim("click a wall face to select that side"));

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

            // Flat is the DEFAULT roof because it is what retail overwhelmingly is: 80% of the sloped-and-flat
            // roof area across the 52 sampled buildings is flat. A pitched roof is the special case, not the
            // norm, and a flat one is this same slab.
            // Auto-fit stays available as the quick path -- it fits a slab to every wall present, which is
            // right for a simple box and a guess you cannot argue with on an L-shaped plan.
            var autos = new GridContainer { Columns = 4 };
            autos.AddThemeConstantOverride("h_separation", 4);
            IconAction(autos, EditorIcons.Glyph.Floor, 40, "Auto floor", "fit a slab under every wall",
                       () => Say(_b.AddSlab(SurfaceKind.Floor) != null ? "floor added" : "draw some walls first"));
            IconAction(autos, EditorIcons.Glyph.Roof, 40, "Auto roof", "fit a roof over every wall",
                       () =>
                       {
                           int n = _b.AddGableRoof(_b.ActiveRoofPitch);
                           Say(n > 0 ? (_b.ActiveRoofPitch <= 0.1f ? "flat roof added"
                                                                   : $"gable roof at {_b.ActiveRoofPitch:0.#}°")
                                     : "draw some walls first");
                       });
            box.AddChild(Dim("AUTO-FIT"));
            box.AddChild(autos);

            // Snapped to the measured retail pitches rather than free: those are where real roofs sit, and 0
            // (flat) is first because it is 80% of them.
            _pitchLbl = new Label { Text = PitchText(_b.ActiveRoofPitch) };
            box.AddChild(_pitchLbl);
            var pitch = new HSlider
            {
                MinValue = 0, MaxValue = EditorBuildings.RoofPitches.Length - 1, Step = 1,
                Value = System.Array.IndexOf(EditorBuildings.RoofPitches, _b.ActiveRoofPitch),
                CustomMinimumSize = new Vector2(240, 0), FocusMode = FocusModeEnum.None,
            };
            pitch.ValueChanged += v =>
            {
                _b.ActiveRoofPitch = EditorBuildings.RoofPitches[Mathf.Clamp((int)v, 0, EditorBuildings.RoofPitches.Length - 1)];
                _pitchLbl.Text = PitchText(_b.ActiveRoofPitch);
            };
            box.AddChild(pitch);

            // Measured, not guessed: all 52 retail buildings sink a hollow skirt 5-6 m down. Without one a
            // building on any slope has daylight under it.

            box.AddChild(new HSeparator());
            box.AddChild(Dim("OPENINGS — click a wall to place"));
            var grid = new GridContainer { Columns = 6 };
            grid.AddThemeConstantOverride("h_separation", 3);
            var og = new[] { EditorIcons.Glyph.Door, EditorIcons.Glyph.Window, EditorIcons.Glyph.TallWindow,
                             EditorIcons.Glyph.Garage, EditorIcons.Glyph.Porch, EditorIcons.Glyph.Vent };
            for (int i = 0; i < EditorBuildings.Archetypes.Length; i++)
            {
                int ai = i;
                var a = EditorBuildings.Archetypes[i];
                // Every opening glyph is the same wall square with a different hole, so the row reads as one
                // family -- the hole shape is the only thing that actually differs between these presets.
                var btn = new Button
                {
                    ToggleMode = true,
                    Icon = EditorIcons.Get(og[Mathf.Min(i, og.Length - 1)], 34),
                    ExpandIcon = true,
                    CustomMinimumSize = new Vector2(38, 38),
                    TooltipText = $"{a.Name} — {a.Width:0.#}×{a.Height:0.#} m, "
                                  + (a.FloorPinned ? "sits on the floor" : $"sill {a.Sill:0.##} m"),
                };
                btn.Pressed += () => SetTool(btn.ButtonPressed ? Tool.Opening : Tool.None, ai);
                _arch.Add(btn);
                grid.AddChild(btn);
            }
            box.AddChild(grid);

            box.AddChild(new HSeparator());
            box.AddChild(Dim("Material — a retail palette"));
            var drop = new OptionButton { CustomMinimumSize = new Vector2(240, 0) };
            for (int i = 0; i < WallMaterials.Count; i++) drop.AddItem($"{i}  {WallMaterials.At(i).Name}", i);
            // Seeded from the TOOL, not from 0: a dropdown that disagrees with the material the next wall will
            // actually get is worse than no dropdown, because it looks authoritative.
            if (WallMaterials.Count > 0) drop.Select(Mathf.PosMod(_b.ActiveMaterial, WallMaterials.Count));
            drop.ItemSelected += id => _b.SelectMaterial((int)id);
            box.AddChild(drop);
            box.AddChild(Dim(WallMaterials.Count > 0
                ? $"{WallMaterials.Count} sampled from the retail buildings"
                : "no palettes loaded — check content/wall_palettes.tsv"));

            box.AddChild(new HSeparator());
            box.AddChild(Dim("Import — port a retail building in"));
            var imp = new OptionButton { CustomMinimumSize = new Vector2(240, 0) };
            // The palette table IS the list of retail buildings, so the importer needs no second inventory of
            // what exists -- and a name in one and not the other is impossible by construction.
            for (int i = 0; i < WallMaterials.Count; i++) imp.AddItem(WallMaterials.At(i).Name, i);
            if (WallMaterials.Count > 0) imp.Select(0);
            box.AddChild(imp);
            _importPick = imp;

            box.AddChild(new HSeparator());
            box.AddChild(Dim("Bake — becomes a prop in the Level tab"));
            _name = new LineEdit { PlaceholderText = "building name", CustomMinimumSize = new Vector2(240, 0) };
            box.AddChild(_name);
            _name.TextSubmitted += _ => DoBake();
            _bakeMsg = Dim("");
            box.AddChild(_bakeMsg);
        }

        LineEdit _name;
        Label _bakeMsg;
        Label _pitchLbl;
        OptionButton _importPick;

        /// <summary>A toggle in the tool palette: icon, no text, the words in the tooltip. Returns the button
        /// so SetTool can drive its pressed state -- one place still owns "which tool is on".</summary>
        Button ToolButton(Container into, EditorIcons.Glyph g, int size, string name, string help,
                          System.Action onPress)
        {
            var b = new Button
            {
                ToggleMode = true,
                Icon = EditorIcons.Get(g, size - 8),
                ExpandIcon = true,
                CustomMinimumSize = new Vector2(size, size),
                TooltipText = $"{name} — {help}",
            };
            b.Pressed += onPress;
            into.AddChild(b);
            return b;
        }

        /// <summary>Same look, but it DOES something rather than arming a mode.</summary>
        void IconAction(Container into, EditorIcons.Glyph g, int size, string name, string help,
                        System.Action onPress)
        {
            var b = new Button
            {
                Icon = EditorIcons.Get(g, size - 8),
                ExpandIcon = true,
                CustomMinimumSize = new Vector2(size, size),
                TooltipText = $"{name} — {help}",
            };
            b.Pressed += onPress;
            into.AddChild(b);
        }

        void DoImport()
        {
            if (_importPick == null || _importPick.Selected < 0) { Say("pick a building below first"); return; }
            string nm = _importPick.GetItemText(_importPick.Selected);
            int n = _b.ImportRetail(nm);
            Say(n > 0 ? $"imported {nm}: {n} surfaces" : $"could not import {nm}");
        }

        static string PitchText(float p) => p <= 0.1f ? "Roof pitch: flat" : $"Roof pitch: {p:0.#}°";

        void DoBake()
        {
            // The result is reported IN the panel. A bake that only prints to the console is, from the chair,
            // a button that does nothing -- and the failure cases here are ordinary ones (no name, no walls)
            // that a user needs to be told about rather than left guessing at.
            if (_b.Walls.Count == 0) { Say("nothing to bake — draw a wall first"); return; }
            if (_name.Text.Trim().Length == 0) { Say("give it a name first"); return; }
            var made = _b.Bake(_name.Text);
            Say(made == null ? "bake failed — see the log" : $"baked '{made}' → it's in the Level tab's props list");
        }

        void Say(string t) { if (_bakeMsg != null) _bakeMsg.Text = t; }

        static Label Dim(string t)
        {
            var l = new Label { Text = t };
            l.AddThemeColorOverride("font_color", new Color(0.72f, 0.78f, 0.83f));
            l.AddThemeFontSizeOverride("font_size", 12);
            return l;
        }
    }
}
