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
        Button _drawFloor, _drawRoof, _room, _del, _found, _stairs;
        CheckBox _glaze, _indestructible;
        Label _hpLbl;
        OptionButton _doorDrop;

        /// <summary>The glass controls edit the SELECTED opening when there is one and set the default for the
        /// next placement when there is not. Asked in one place so the four controls cannot disagree about
        /// which they are doing.</summary>
        bool HasSelectedOpening => _b.SelectedWall != null && _b.SelectedOpening >= 0;

        static string HpText(float hp) =>
            hp <= 1.01f ? "HP: 1 (retail glass — one shot)" : $"HP: {hp:0}";

        // What the glass controls were last shown as, so _Process only touches them when something changed.
        // Writing ButtonPressed every frame would re-enter the Toggled handler and push an undo step per
        // frame -- the control would fight the user for the checkbox.
        (WallSurface W, int I, bool G, bool Ind, string Door, bool CanDoor) _glassShown = (null, -2, false, false, "\0", false);

        /// <summary>Make the glass controls show the SELECTED opening. Without this they keep displaying the
        /// last thing that was set, and this file's own rule about the material dropdown applies: a control
        /// that disagrees with what it claims to describe is worse than no control, because it looks
        /// authoritative. Polled rather than event-driven because selection is plain fields on the tool and
        /// every other readout in this editor is polled the same way.</summary>
        public override void _Process(double delta)
        {
            SyncToolButtons();
            var w = _b.SelectedWall;
            int i = _b.SelectedOpening;
            bool has = w != null && IsInstanceValid(w) && i >= 0 && i < w.Openings.Count;
            var o = has ? w.Openings[i] : default;
            bool glazed = has ? o.Glazed : (_b.GlazeNew ?? true);
            bool ind = has ? o.GlassIndestructible : _b.ActiveGlassIndestructible;
            // A door only goes in a floor-pinned opening, so the dropdown greys out on a window rather than
            // silently accepting a door that PlannedOpening/SetOpeningDoor would never show.
            string door = has ? o.DoorProp : _b.ActiveDoorProp;
            bool canDoor = !has || EditorBuildings.Archetypes[Mathf.PosMod(o.Archetype, EditorBuildings.Archetypes.Length)].FloorPinned;
            var now = (has ? w : null, has ? i : -1, glazed, ind, door, canDoor);
            if (now == _glassShown) return;
            _glassShown = now;
            if (_doorDrop != null)
            {
                _doorDrop.Disabled = !canDoor;
                // Select, not Selected=: the id IS the index here, but going through the lookup keeps this
                // honest if the list ever gains a separator or a filtered entry.
                int want = 0;
                for (int d = 0; d < DoorProps.Length; d++) if (DoorProps[d].Prop == door) { want = d; break; }
                if (_doorDrop.Selected != want) _doorDrop.Select(want);   // Select() does NOT fire ItemSelected, so this cannot write back
            }
            // SetPressedNoSignal, not ButtonPressed: assigning it fires Toggled, which would write the value
            // straight back onto the opening and push an undo step for a change nobody made.
            _glaze?.SetPressedNoSignal(glazed);
            _indestructible?.SetPressedNoSignal(ind);
            if (_hpLbl != null) _hpLbl.Text = HpText(has ? (o.GlassHp > 0f ? o.GlassHp : 1f) : _b.ActiveGlassHp);
        }

        /// <summary>Tint swatches. 0 = the pane's own default, which is first so "no opinion" is the easy pick.
        /// The rest are the glass colours that actually turn up in buildings rather than a full colour wheel --
        /// a picker with 16M answers to a question with about six is not more capable, just slower.</summary>
        static readonly (string Label, int Rgb)[] GlassTints =
        {
            ("default blue-grey", 0),
            ("clear",     0xDCE8EC),
            ("green",     0x8FBFA0),
            ("bronze",    0xB08A5A),
            ("smoked",    0x6B6E72),
            ("blue",      0x6A9BC8),
            ("amber",     0xD8A65A),
        };

        /// <summary>What the door dropdown offers. Derived from the def table rather than typed out, so a door
        /// added there shows up here, and filtered to the `Door_` FORM on purpose:
        ///   - Gate is an X-tilt garage door and Hatch is a floor hatch (see wooden_door_anims.txt -- Gate's
        ///     hinge axis is (1,0,0), Hatch's is (-1,0,0) with a z offset). Neither is a wall-opening swing door.
        ///   - Doubledoor's two-hinge panel split is not built, so it refuses to place.
        /// Offering an untested door is exactly how the last door bug shipped, so the list is the four that
        /// strawberry_cow has actually seen swing.</summary>
        static readonly (string Label, string Prop)[] DoorProps = BuildDoorProps();

        static (string, string)[] BuildDoorProps()
        {
            var list = new System.Collections.Generic.List<(string, string)> { ("— no door (open hole)", null) };
            foreach (var d in DeployableDef.WoodDoors)
                if (d.DoorProp != null && d.DoorProp.StartsWith("Door_")) list.Add((d.Name, d.DoorProp));
            return list.ToArray();
        }

        enum Tool { None, Wall, Room, Floor, Roof, Opening, Delete, Foundation, Stairs }

        /// <summary>Exactly one tool is active. Selection used to be done by each button clearing the others
        /// by hand, in five places, and every one of them cleared a DIFFERENT subset -- the opening presets
        /// only turned off wall-draw, so arming a window while the room tool was live left both armed and the
        /// next click did whichever the input handler reached first. strawberry_cow: "prevent multiple tools
        /// being selected at once, ie wall and an opening." One place that sets all of them is the only way
        /// this stays true as tools get added.</summary>
        /// <summary>Panel clicks set the tool THROUGH EditorBuildings, which owns it. The panel used to set
        /// the six mode flags itself, which meant the keyboard could put the editor in a state the buttons
        /// disagreed with -- press 1 with the room tool live and both were armed, with the room button still
        /// lit. Button state is now SYNCED from the live tool in _Process instead of being set here, so
        /// however the tool changed, the UI tells the truth about it.</summary>
        void SetTool(Tool t, int archetype = -1)
            => _b.SelectTool(t switch
            {
                Tool.Wall       => EditorBuildings.BuildTool.Wall,
                Tool.Room       => EditorBuildings.BuildTool.Room,
                Tool.Floor      => EditorBuildings.BuildTool.Floor,
                Tool.Roof       => EditorBuildings.BuildTool.Roof,
                Tool.Foundation => EditorBuildings.BuildTool.Foundation,
                Tool.Delete     => EditorBuildings.BuildTool.Delete,
                Tool.Stairs     => EditorBuildings.BuildTool.Stairs,
                Tool.Opening    => EditorBuildings.BuildTool.Opening,
                _               => EditorBuildings.BuildTool.None,
            }, archetype);

        /// <summary>Light the button for whatever tool is actually live -- panel click, keyboard shortcut or
        /// a tool that disarmed itself. Reads the authority rather than remembering what it last set.</summary>
        void SyncToolButtons()
        {
            var t = _b.Tool;
            if (_draw != null)      _draw.ButtonPressed      = t == EditorBuildings.BuildTool.Wall;
            if (_room != null)      _room.ButtonPressed      = t == EditorBuildings.BuildTool.Room;
            if (_drawFloor != null) _drawFloor.ButtonPressed = t == EditorBuildings.BuildTool.Floor;
            if (_drawRoof != null)  _drawRoof.ButtonPressed  = t == EditorBuildings.BuildTool.Roof;
            if (_del != null)       _del.ButtonPressed       = t == EditorBuildings.BuildTool.Delete;
            if (_stairs != null)    _stairs.ButtonPressed    = t == EditorBuildings.BuildTool.Stairs;
            if (_found != null)     _found.ButtonPressed     = t == EditorBuildings.BuildTool.Foundation;
            for (int i = 0; i < _arch.Count; i++)
                _arch[i].ButtonPressed = t == EditorBuildings.BuildTool.Opening && i == _b.ArmedArchetype;
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
            _found = ToolButton(second, EditorIcons.Glyph.Foundation, 44, "Draw foundation",
                                "drag a rectangle — a skirt, no walls needed first",
                                () => SetTool(_found.ButtonPressed ? Tool.Foundation : Tool.None));
            _stairs = ToolButton(second, EditorIcons.Glyph.Stairs, 44, "Stairs",
                                 "click the floor — a flight to the storey above, step count derived so it lands flush",
                                 () => SetTool(_stairs.ButtonPressed ? Tool.Stairs : Tool.None));
            _del = ToolButton(second, EditorIcons.Glyph.Delete, 44, "Delete / cut",
                              "click a wall to remove it, or drag along one to cut a piece out",
                              () => SetTool(_del.ButtonPressed ? Tool.Delete : Tool.None));
            IconAction(second, EditorIcons.Glyph.Import, 44, "Import",
                       "port the retail building selected below onto the stage", () => DoImport());
            IconAction(second, EditorIcons.Glyph.Delete, 44, "Clear plot",
                       "wipe every wall, floor, roof and foundation — Ctrl+Z brings it all back",
                       () => Say(_b.ClearPlot() is int cn && cn > 0 ? $"cleared {cn} surface(s) — Ctrl+Z to undo"
                                                                    : "the plot is already empty"));
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
            // Rooms first, because it is the one that is usually right. The plain auto-floor below fits ONE
            // slab to the bounding box of every wall on the stage, which is correct for a single box and
            // wrong the moment the plan is L-shaped or there are two buildings -- so it stays, but second.
            IconAction(autos, EditorIcons.Glyph.Room, 40, "Auto floor rooms (H)",
                       "floor each ENCLOSED room, and foundation the walls round it",
                       () =>
                       {
                           int n = _b.AutoFitRooms();
                           Say(n > 0 ? $"{n} surface(s) fitted to enclosed rooms"
                                     : "no enclosed rooms on this storey — close the walls first");
                       });
            IconAction(autos, EditorIcons.Glyph.Floor, 40, "Auto floor (whole plot)",
                       "one slab over everything drawn — ignores rooms",
                       () => Say(_b.AddSlab(SurfaceKind.Floor) != null ? "floor added" : "draw some walls first"));
            IconAction(autos, EditorIcons.Glyph.Foundation, 40, "Auto foundation",
                       $"a skirt under every wall — retail sinks {WallOpenings.FoundationDepth:0.#} m",
                       () => Say(_b.AddFoundation() is int fn && fn > 0 ? $"foundation under {fn} wall(s)"
                                                                        : "draw some walls first"));
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

            // ---- glazing -------------------------------------------------------------------------------
            // These are the settings a NEW opening is stamped with, and they retarget to the selected opening
            // the moment there is one -- so the same four controls both set the default and edit what you
            // clicked, instead of the tool having a second panel that says the same words.
            box.AddChild(new HSeparator());
            box.AddChild(Dim("GLASS — window / tall win are glazed by default"));

            _glaze = new CheckBox { Text = "glass in the opening", ButtonPressed = true, FocusMode = FocusModeEnum.None };
            _glaze.Toggled += on =>
            {
                if (HasSelectedOpening) _b.SetOpeningGlass(_b.SelectedWall, _b.SelectedOpening, glazed: on);
                else _b.GlazeNew = on;                 // no selection -> this is the default for the next one
            };
            box.AddChild(_glaze);

            _indestructible = new CheckBox { Text = "indestructible", FocusMode = FocusModeEnum.None };
            _indestructible.Toggled += on =>
            {
                if (HasSelectedOpening) _b.SetOpeningGlass(_b.SelectedWall, _b.SelectedOpening, indestructible: on);
                else _b.ActiveGlassIndestructible = on;
            };
            box.AddChild(_indestructible);

            _hpLbl = new Label { Text = HpText(_b.ActiveGlassHp) };
            box.AddChild(_hpLbl);
            var hp = new HSlider
            {
                // 1 is retail glass: one shot. The range above it is for the "reinforced" case strawberry
                // asked for, and it is a slider rather than a number box because the useful values are few.
                MinValue = 1, MaxValue = 20, Step = 1, Value = Mathf.Max(1f, _b.ActiveGlassHp),
                CustomMinimumSize = new Vector2(240, 0), FocusMode = FocusModeEnum.None,
            };
            hp.ValueChanged += v =>
            {
                _hpLbl.Text = HpText((float)v);
                if (HasSelectedOpening) _b.SetOpeningGlass(_b.SelectedWall, _b.SelectedOpening, hp: (float)v);
                else _b.ActiveGlassHp = (float)v;
            };
            box.AddChild(hp);

            box.AddChild(Dim("tint"));
            var tints = new GridContainer { Columns = 7 };
            tints.AddThemeConstantOverride("h_separation", 3);
            foreach (var (label, rgb) in GlassTints)
            {
                int packed = rgb;
                var sw = new Button
                {
                    CustomMinimumSize = new Vector2(30, 24), TooltipText = label, FocusMode = FocusModeEnum.None,
                };
                // A swatch has to LOOK like its colour -- a row of identical grey buttons labelled in a tooltip
                // is not a colour picker.
                var sb = new StyleBoxFlat { BgColor = packed == 0 ? GlassPane.DefaultHue : WallSurface.TintFromRgb(packed) };
                foreach (var st in new[] { "normal", "hover", "pressed" }) sw.AddThemeStyleboxOverride(st, sb);
                sw.Pressed += () =>
                {
                    if (HasSelectedOpening) _b.SetOpeningGlass(_b.SelectedWall, _b.SelectedOpening, tint: packed);
                    else _b.ActiveGlassTint = packed;
                };
                tints.AddChild(sw);
            }
            box.AddChild(tints);

            // ---- door ----------------------------------------------------------------------------------
            // Same double duty as the glass controls: edits the selected opening, or sets what the next
            // floor-pinned one gets. Greys out on a window, because a door there would be silently dropped.
            box.AddChild(new HSeparator());
            box.AddChild(Dim("DOOR — door / garage / porch openings only"));
            _doorDrop = new OptionButton { CustomMinimumSize = new Vector2(240, 0), FocusMode = FocusModeEnum.None };
            foreach (var (label, _) in DoorProps) _doorDrop.AddItem(label);
            _doorDrop.ItemSelected += id =>
            {
                var prop = DoorProps[Mathf.Clamp((int)id, 0, DoorProps.Length - 1)].Prop;
                if (HasSelectedOpening) _b.SetOpeningDoor(_b.SelectedWall, _b.SelectedOpening, prop);
                else _b.ActiveDoorProp = prop;
            };
            box.AddChild(_doorDrop);

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
