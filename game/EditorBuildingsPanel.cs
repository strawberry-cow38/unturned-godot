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

            box.AddChild(Dim("Wall"));
            _draw = new Button { Text = "Draw wall", ToggleMode = true };
            _draw.Pressed += () => SetTool(_draw.ButtonPressed ? Tool.Wall : Tool.None);
            box.AddChild(_draw);

            _room = new Button { Text = "Draw room", ToggleMode = true,
                                 TooltipText = "drag a rectangle — four walls on the grid, shared edges merged" };
            _room.Pressed += () => SetTool(_room.ButtonPressed ? Tool.Room : Tool.None);
            box.AddChild(_room);

            _del = new Button { Text = "Delete / cut", ToggleMode = true,
                                TooltipText = "click a wall to remove it, or drag along one to cut a piece out" };
            _del.Pressed += () => SetTool(_del.ButtonPressed ? Tool.Delete : Tool.None);
            box.AddChild(_del);

            // No "paint back side" checkbox: click the face you mean and the ghost shows which one you have.
            box.AddChild(Dim("click a wall face to select that side — the picker paints it"));

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
            var slabs = new HBoxContainer();
            var addFloor = new Button { Text = "Add floor", CustomMinimumSize = new Vector2(120, 0),
                                        TooltipText = "a slab under the walls you have drawn" };
            addFloor.Pressed += () => Say(_b.AddSlab(SurfaceKind.Floor) != null ? "floor added" : "draw some walls first");
            var addRoof = new Button { Text = "Add roof", CustomMinimumSize = new Vector2(120, 0),
                                       TooltipText = "flat at 0 pitch — 80% of retail roof area is flat" };
            addRoof.Pressed += () =>
            {
                int n = _b.AddGableRoof(_b.ActiveRoofPitch);
                Say(n > 0 ? (_b.ActiveRoofPitch <= 0.1f ? "flat roof added" : $"gable roof at {_b.ActiveRoofPitch:0.#}°")
                          : "draw some walls first");
            };
            slabs.AddChild(addFloor);
            slabs.AddChild(addRoof);
            box.AddChild(slabs);

            // Draw the slab yourself. Add roof/Add floor fit one to the bounding box of every wall present,
            // which is a guess you cannot argue with -- an L-shaped plan gets a slab over the courtyard.
            var drawSlabs = new HBoxContainer();
            _drawFloor = new Button { Text = "Draw floor", ToggleMode = true, CustomMinimumSize = new Vector2(120, 0),
                                      TooltipText = "drag a rectangle instead of auto-fitting to the walls" };
            _drawRoof = new Button { Text = "Draw roof", ToggleMode = true, CustomMinimumSize = new Vector2(120, 0),
                                     TooltipText = "drag a rectangle instead of auto-fitting to the walls" };
            _drawFloor.Pressed += () => SetTool(_drawFloor.ButtonPressed ? Tool.Floor : Tool.None);
            _drawRoof.Pressed += () => SetTool(_drawRoof.ButtonPressed ? Tool.Roof : Tool.None);
            drawSlabs.AddChild(_drawFloor);
            drawSlabs.AddChild(_drawRoof);
            box.AddChild(drawSlabs);

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
            var found = new Button { Text = $"Add foundation ({WallOpenings.FoundationDepth:0.#}m)",
                                     TooltipText = "a skirt under the walls — retail sinks 5-6m" };
            found.Pressed += () =>
            {
                int n = _b.AddFoundation();
                Say(n > 0 ? $"foundation under {n} wall(s)" : "draw some walls first");
            };
            box.AddChild(found);

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
            var impBtn = new Button { Text = "Import (replaces stage)" };
            impBtn.Pressed += () =>
            {
                if (imp.Selected < 0) return;
                string nm = imp.GetItemText(imp.Selected);
                int n = _b.ImportRetail(nm);
                Say(n > 0 ? $"imported {nm}: {n} walls" : $"could not import {nm}");
            };
            box.AddChild(impBtn);

            box.AddChild(new HSeparator());
            box.AddChild(Dim("Bake — becomes a prop in the Level tab"));
            _name = new LineEdit { PlaceholderText = "building name", CustomMinimumSize = new Vector2(240, 0) };
            box.AddChild(_name);
            var bake = new Button { Text = "Bake to prop" };
            bake.Pressed += DoBake;
            _name.TextSubmitted += _ => DoBake();
            box.AddChild(bake);
            _bakeMsg = Dim("");
            box.AddChild(_bakeMsg);
        }

        LineEdit _name;
        Label _bakeMsg;
        Label _pitchLbl;

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
