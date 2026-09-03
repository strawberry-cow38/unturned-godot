using Godot;

namespace UnturnedGodot
{
    // Play area -> retail-style SINGLEPLAYER screen (ripped from MenuPlaySingleplayerUI + SleekLevel):
    //   left  = category tabs (Official active; Curated/Workshop/Misc dummy) + a scrollable MAP LIST
    //           (Prince Edward Island is real+installed; the other official maps render greyed / "not
    //           installed" so the list reads like retail's Official tab).
    //   right = a preview box + selected map name/description + per-map GAMEPLAY OPTIONS
    //           (Difficulty / Zombies / Loot / Day Cycle / Combat / Cheats / Permadeath) + a big PLAY button.
    // Most options are DUMMY for feel; the wired one is Zombies (Normal / No Zombies / New Zombies -> our real
    // PEI modes), so PLAY launches the mode the player picked. BuildMapSelector assigns _playPanel, so the
    // existing TogglePlayPanel / ShowStub / ToggleWorkshopPanel mutual-hide logic drives it unchanged.
    public partial class MainMenu
    {
        // per-map gameplay config. Zombies is REAL (-> PlaySelected); the rest are cosmetic for now.
        int _optDifficulty = 1;   // 0 Easy / 1 Normal / 2 Hard      (dummy)
        int _optZombies    = 0;   // 0 Normal / 1 Off  -- REAL: applied in PlaySelected. The row and its wiring
                                  // both went out with the zombie rewrite, leaving the "Zombies is REAL" comment
                                  // below describing a control that no longer existed anywhere (master: "where
                                  // is the toggle?"). Restored as a VISIBLE row, not buried behind Config.
        int _optLoot       = 1;   // 0 Sparse / 1 Normal / 2 Abundant (dummy)
        int _optDay        = 1;   // 0 Short / 1 Default / 2 Long / 3 Endless Day (dummy)
        int _optCombat     = 0;   // 0 PvE / 1 PvP                    (dummy)
        bool _optCheats     = false;
        bool _optPermadeath = false;

        string _selectedMap = "Prince Edward Island";
        bool _selectedPlayable = true;

        // GENERATE MAP (strawberry 2026-08-22: "a 'generate map' option on the play tab of the main menu").
        // A pseudo-entry in the map list rather than a button off to one side, because that is what it is from
        // the player's side: another map you can pick and press PLAY on. The seed is exposed because it is the
        // whole point of a deterministic generator -- an island you liked is a number you can write down.
        bool _generateSelected;
        bool _playgroundSelected;   // Playground picked in the map list -> PLAY runs the gun range, not a survival map
        int _genSeed = 1234;
        Control _genRow;
        LineEdit _genSeedEdit;
        public System.Action<int> OnGenerateMap;
        const string GenerateMapName = "Generate Island";
        const string GenerateMapDesc = "A procedurally generated island: coastline, hills, and a network of towns, military bases and construction sites joined by roads, trails and rail. The same seed always builds the same island.";
        // the Steam Maps/<folder> name for the selected map -- Main reads this to point the world at the right map.
        // PEI's folder is "PEI" (its display name is "Prince Edward Island"); every other map's folder == its name.
        public string SelectedMapFolder = "PEI";

        static readonly string[] Difficulties = { "Easy", "Normal", "Hard" };
        static readonly string[] LootModes    = { "Sparse", "Normal", "Abundant" };
        static readonly string[] DayModes     = { "Short", "Default", "Long", "Endless Day" };
        static readonly string[] CombatModes  = { "PvE", "PvP" };
        static readonly string[] ZombieModes  = { "Normal", "Off" };

        // The per-map gameplay options above are plain fields that reset to their defaults every launch. Persist
        // them to user:// (Godot's writable per-user dir -- survives restart AND works in an exported build, unlike
        // the res:// content the in-editor Save targets) so a player's chosen mode sticks across restarts.
        const string MapSettingsPath = "user://map_settings.cfg";

        void LoadMapSettings()   // restore saved options BEFORE BuildMapSelector reads the fields into the rows
        {
            var cfg = new ConfigFile();
            if (cfg.Load(MapSettingsPath) != Error.Ok) return;   // nothing saved yet -> keep defaults
            _optDifficulty = cfg.GetValue("map", "difficulty", _optDifficulty).AsInt32();
            _optZombies    = Mathf.Clamp(cfg.GetValue("map", "zombies", _optZombies).AsInt32(), 0, ZombieModes.Length - 1);   // a pre-rewrite config can hold 2 ("New Zombies"), which no longer exists
            _optLoot       = cfg.GetValue("map", "loot",       _optLoot).AsInt32();
            _optDay        = cfg.GetValue("map", "day",        _optDay).AsInt32();
            _optCombat     = cfg.GetValue("map", "combat",     _optCombat).AsInt32();
            _optCheats     = cfg.GetValue("map", "cheats",     _optCheats).AsBool();
            _optPermadeath = cfg.GetValue("map", "permadeath", _optPermadeath).AsBool();
        }

        void SaveMapSettings()   // called on every option change so the choice persists immediately
        {
            var cfg = new ConfigFile();
            cfg.SetValue("map", "difficulty", _optDifficulty);
            cfg.SetValue("map", "zombies",    _optZombies);
            cfg.SetValue("map", "loot",       _optLoot);
            cfg.SetValue("map", "day",        _optDay);
            cfg.SetValue("map", "combat",     _optCombat);
            cfg.SetValue("map", "cheats",     _optCheats);
            cfg.SetValue("map", "permadeath", _optPermadeath);
            cfg.Save(MapSettingsPath);
        }

        // official Unturned maps. `key` = the ported icon/preview basename (content/menu/mapicon_<key>.png +
        // mappreview_<key>.png, copied from the retail install's Maps/<Name>/Icon.png + Preview.png). PEI is the
        // only world we actually ported (playable); the other real maps show their REAL icon + preview but aren't
        // playable yet. key "" = no art (kept for the "more maps exist" feel).
        static readonly (string name, string key, bool playable, string desc)[] OfficialMaps =
        {
            // descriptions are the REAL source text, extracted from each map's Maps/<Name>/English.dat (Description key).
            ("Prince Edward Island", "pei",        true,  "Sunny island off the East coast of Canada. Several small civilian towns with minor military presence. Tourist attractions include beaches, castles and sailing. Recommended for new survivors."),
            ("Washington",           "washington", true,  "Rainy state South-West of Canada. Several large civilian towns with extensive military presence. Tourist attractions include Seattle, golf and racing. Recommended for intermediate survivors."),
            ("Russia",               "russia",     false, "Multi-biome country neighboring Canada. Huge diversity of civilian destinations with varying military presence. Tourist attractions include historical monuments, picturesque countrysides and rock climbing. Recommended for experienced survivors."),
            ("Germany",              "germany",    false, "Mountainous country North of Canada. Modernized cities with active military presence. Tourist attractions include breathtaking vistas, hiking the alpine trails and the local celebration of Oktoberfest. Recommended for intermediate survivors."),
            ("Yukon",                "yukon",      true,  "Harsh, freezing territory in North-West Canada. Barren frozen wasteland scattered with camps and cabins. Tourist attractions include skiing, skating and train spotting. Recommended for experienced survivors."),
            ("Hawaii",               "",           false, "Not installed."),
            ("Greece",               "",           false, "Not installed."),
            ("A6 Polaris",           "",           false, "Not installed."),
        };

        Label _previewName, _descLabel;
        TextureRect _previewImage;

        // load a PNG from content/menu/ as a texture (optionally downscaled to maxSize px for the small row icons).
        Texture2D LoadTex(string file, int maxSize = 0)
        {
            string p = G($"res://content/menu/{file}");
            if (!System.IO.File.Exists(p)) return null;
            var img = new Image();
            if (img.Load(p) != Error.Ok) return null;
            if (maxSize > 0)
            {
                int w = img.GetWidth(), h = img.GetHeight();
                if (w > maxSize || h > maxSize)
                {
                    float s = (float)maxSize / Mathf.Max(w, h);
                    img.Resize(Mathf.Max(1, (int)(w * s)), Mathf.Max(1, (int)(h * s)), Image.Interpolation.Lanczos);
                }
            }
            return ImageTexture.CreateFromImage(img);
        }

        void BuildMapSelector(CanvasLayer layer)
        {
            LoadMapSettings();   // restore persisted gameplay options so the rows below open on the saved values
            var panel = new PanelContainer { Visible = false };
            panel.SetAnchorsPreset(Control.LayoutPreset.Center);   // centered like every other submenu
            panel.GrowHorizontal = Control.GrowDirection.Both; panel.GrowVertical = Control.GrowDirection.Both;
            var margin = new MarginContainer();
            foreach (var s in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
                margin.AddThemeConstantOverride(s, 14);
            panel.AddChild(margin);
            var cols = new HBoxContainer();
            cols.AddThemeConstantOverride("separation", 16);
            margin.AddChild(cols);

            // Retail MenuPlaySingleplayerUI is THREE columns (tinyclaw's SDK coords, PositionScale_X=0.5 = offset from
            // centre): LEFT = preview then Play / Difficulty / Config; CENTRE = the 4 tabs + the map list; RIGHT =
            // selected-map name + description. The rest of the gameplay options live behind Config (retail's configButton).

            // ---- LEFT column: preview, Play, Difficulty, Config
            var lcol = new VBoxContainer { CustomMinimumSize = new Vector2(340f, 0f) };
            lcol.AddThemeConstantOverride("separation", 7);
            cols.AddChild(lcol);
            var preview = new Panel { CustomMinimumSize = new Vector2(340f, 200f) };
            var pv = new StyleBoxFlat { BgColor = new Color(0.10f, 0.11f, 0.10f) };
            pv.SetBorderWidthAll(1);
            pv.BorderColor = new Color(0f, 0f, 0f, 0.5f);
            preview.AddThemeStyleboxOverride("panel", pv);
            _previewImage = new TextureRect
            {
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                Texture = LoadTex("mappreview_pei.png"),
            };
            _previewImage.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            preview.AddChild(_previewImage);
            lcol.AddChild(preview);
            var play = new Button { Text = "PLAY", CustomMinimumSize = new Vector2(340f, 46f) };
            play.AddThemeFontSizeOverride("font_size", 22);
            play.Pressed += PlaySelected;
            lcol.AddChild(play);
            lcol.AddChild(OptionRow("Difficulty", Difficulties, _optDifficulty, i => { _optDifficulty = i; SaveMapSettings(); }));
            lcol.AddChild(OptionRow("Zombies", ZombieModes, _optZombies, i => { _optZombies = i; SaveMapSettings(); }));
            lcol.AddChild(BuildResetDataButton());
            var cfgBox = new VBoxContainer { Visible = false };
            cfgBox.AddThemeConstantOverride("separation", 5);
            cfgBox.AddChild(OptionRow("Loot",       LootModes,   _optLoot,   i => { _optLoot = i; SaveMapSettings(); }));
            cfgBox.AddChild(OptionRow("Day Cycle",  DayModes,    _optDay,    i => { _optDay = i; SaveMapSettings(); }));
            cfgBox.AddChild(OptionRow("Combat",     CombatModes, _optCombat, i => { _optCombat = i; SaveMapSettings(); }));
            cfgBox.AddChild(ToggleRow("Cheats",     _optCheats,     v => { _optCheats = v; SaveMapSettings(); }));
            cfgBox.AddChild(ToggleRow("Permadeath", _optPermadeath, v => { _optPermadeath = v; SaveMapSettings(); }));
            cfgBox.AddChild(AdvancedButton());
            var cfgBtn = new Button { Text = "  Config", CustomMinimumSize = new Vector2(340f, 34f), Alignment = HorizontalAlignment.Left, ToggleMode = true };
            cfgBtn.AddThemeFontSizeOverride("font_size", 15);
            cfgBtn.Toggled += on => cfgBox.Visible = on;   // retail's configButton -> the gameplay config
            lcol.AddChild(cfgBtn);
            lcol.AddChild(cfgBox);
            lcol.AddChild(BuildSeedRow());   // Generate Island's seed field (SelectGenerated shows _genRow)
            var backBtn = new Button { Text = "\u25c4  Back", CustomMinimumSize = new Vector2(340f, 36f), Alignment = HorizontalAlignment.Left };
            backBtn.Pressed += BackToDashboard;   // dashboard is hidden while this is up -> its own way out
            lcol.AddChild(backBtn);

            // ---- CENTRE column: category tabs + scrollable map list
            var ccol = new VBoxContainer { CustomMinimumSize = new Vector2(420f, 0f) };
            ccol.AddThemeConstantOverride("separation", 6);
            cols.AddChild(ccol);
            var tabs = new HBoxContainer();
            tabs.AddThemeConstantOverride("separation", 3);
            ccol.AddChild(tabs);
            string[] cats = { "Official", "Curated", "Workshop", "Misc" };
            for (int i = 0; i < cats.Length; i++)
            {
                var tb = new Button { Text = cats[i], ToggleMode = true, ButtonPressed = i == 0, Disabled = i != 0, CustomMinimumSize = new Vector2(100f, 40f) };
                tb.AddThemeFontSizeOverride("font_size", 13);
                tabs.AddChild(tb);
            }
            var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(420f, 330f), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            ccol.AddChild(scroll);
            var list = new VBoxContainer { CustomMinimumSize = new Vector2(400f, 0f) };
            list.AddThemeConstantOverride("separation", 3);
            scroll.AddChild(list);
            var genBtn = new Button { Text = "  \u2699  " + GenerateMapName, CustomMinimumSize = new Vector2(400f, 46f), Alignment = HorizontalAlignment.Left };
            genBtn.AddThemeFontSizeOverride("font_size", 16);
            genBtn.AddThemeColorOverride("font_color", new Color(0.92f, 0.86f, 0.62f));
            genBtn.Pressed += () => SelectGenerated();
            list.AddChild(genBtn);
            var pgBtn = new Button { Text = "  \u25ce  Playground", CustomMinimumSize = new Vector2(400f, 46f), Alignment = HorizontalAlignment.Left };
            pgBtn.AddThemeFontSizeOverride("font_size", 16);
            pgBtn.AddThemeColorOverride("font_color", new Color(0.92f, 0.86f, 0.62f));
            pgBtn.Pressed += () => SelectPlayground();
            list.AddChild(pgBtn);
            list.AddChild(new HSeparator());
            foreach (var m in OfficialMaps) list.AddChild(MapRow(m.name, m.key, m.playable, m.desc));

            // ---- RIGHT column: selected-map name + description
            var rcol = new VBoxContainer { CustomMinimumSize = new Vector2(260f, 0f) };
            rcol.AddThemeConstantOverride("separation", 7);
            cols.AddChild(rcol);
            _previewName = new Label { Text = _selectedMap };
            _previewName.AddThemeFontSizeOverride("font_size", 20);
            _previewName.AddThemeColorOverride("font_color", new Color(0.95f, 0.94f, 0.9f));
            rcol.AddChild(_previewName);
            _descLabel = new Label
            {
                Text = OfficialMaps[0].desc,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(260f, 34f),
            };
            _descLabel.AddThemeColorOverride("font_color", new Color(0.78f, 0.78f, 0.78f));
            _descLabel.AddThemeFontSizeOverride("font_size", 14);
            rcol.AddChild(_descLabel);

            layer.AddChild(panel);
            _playPanel = panel;   // reuse the existing Play-panel plumbing (toggle / mutual-hide)
            BuildAdvancedPanel(layer);
        }

        Label Header(string text, int size)
        {
            var l = new Label { Text = text };
            l.AddThemeFontSizeOverride("font_size", size);
            l.AddThemeColorOverride("font_color", new Color(0.92f, 0.86f, 0.62f));
            return l;
        }

        // one map row: the map's REAL icon (from its Maps/<Name>/Icon.png) + name. Clicking selects it -> updates
        // the preview image + name + description. PEI is playable; the other real maps are selectable (icon +
        // preview show) but PLAY is gated -- only PEI's world is ported.
        Button MapRow(string name, string key, bool playable, string desc)
        {
            var b = new Button
            {
                Text = "  " + name + (playable ? "" : "   ·  not ported"),
                CustomMinimumSize = new Vector2(342f, 48f),
                Alignment = HorizontalAlignment.Left,
                ExpandIcon = false,
            };
            b.AddThemeFontSizeOverride("font_size", 16);
            var icon = key != "" ? LoadTex($"mapicon_{key}.png", 36) : null;
            if (icon != null) b.Icon = icon;
            b.Pressed += () => SelectMap(name, key, playable, desc);
            return b;
        }

        // The seed field + a randomiser. Only shown while the generated entry is selected -- on a retail map it
        // is a control that does nothing, which reads as broken rather than as inapplicable.
        Control BuildSeedRow()
        {
            var row = new HBoxContainer { CustomMinimumSize = new Vector2(340f, 32f), Visible = false };
            row.AddThemeConstantOverride("separation", 6);
            var name = new Label { Text = "Seed", CustomMinimumSize = new Vector2(110f, 0f), VerticalAlignment = VerticalAlignment.Center };
            name.AddThemeFontSizeOverride("font_size", 15);
            row.AddChild(name);
            _genSeedEdit = new LineEdit { Text = _genSeed.ToString(), CustomMinimumSize = new Vector2(160f, 30f), Alignment = HorizontalAlignment.Center };
            _genSeedEdit.TextChanged += t => { if (int.TryParse(t, out int v)) _genSeed = v; };
            row.AddChild(_genSeedEdit);
            var roll = new Button { Text = "\U0001F3B2", CustomMinimumSize = new Vector2(36f, 30f), TooltipText = "Random seed" };
            roll.Pressed += () =>
            {
                _genSeed = (int)(GD.Randi() & 0x7FFFFFFF);
                if (_genSeedEdit != null) _genSeedEdit.Text = _genSeed.ToString();
            };
            row.AddChild(roll);
            _genRow = row;
            return row;
        }

        void SelectGenerated()
        {
            _generateSelected = true;
            _playgroundSelected = false;
            _selectedMap = GenerateMapName;
            _selectedPlayable = true;
            if (_previewName != null) _previewName.Text = GenerateMapName;
            if (_descLabel != null) _descLabel.Text = GenerateMapDesc;
            if (_previewImage != null) _previewImage.Texture = null;
            if (_genRow != null) _genRow.Visible = true;
            DisarmReset();
        }

        // Playground -- master moved it out of the Play submenu into its own map here (the gun range, not a survival map).
        void SelectPlayground()
        {
            _playgroundSelected = true;
            _generateSelected = false;
            _selectedMap = "Playground";
            _selectedPlayable = true;
            if (_previewName != null) _previewName.Text = "Playground";
            if (_descLabel != null) _descLabel.Text = "The gun range -- an open sandbox to test weapons and mechanics. No survival, no map: just spawn in with everything.";
            if (_previewImage != null) _previewImage.Texture = LoadTex("mappreview_playground.png");   // null if not shipped
            if (_genRow != null) _genRow.Visible = false;
            DisarmReset();
        }

        void SelectMap(string name, string key, bool playable, string desc)
        {
            _generateSelected = false;
            _playgroundSelected = false;
            if (_genRow != null) _genRow.Visible = false;
            _selectedMap = name;
            _selectedPlayable = playable;
            SelectedMapFolder = name == "Prince Edward Island" ? "PEI" : name;   // display name -> Steam Maps/ folder

            if (_previewName != null) _previewName.Text = name;
            if (_descLabel != null) _descLabel.Text = desc;
            if (_previewImage != null) _previewImage.Texture = key != "" ? LoadTex($"mappreview_{key}.png") : null;
            DisarmReset();
        }

        // labeled left/right value cycler (retail SleekButtonState). onChange fires with the new index.
        Control OptionRow(string label, string[] values, int initial, System.Action<int> onChange)
        {
            // CLAMP. map_settings.cfg outlives the schema that wrote it: this box had zombies=2 saved from
            // when the row offered three modes (Normal / No Zombies / New Zombies), the row was later deleted,
            // and the value sat in the config. Restoring a two-value row then indexed [2] and threw
            // IndexOutOfRangeException before the menu finished building. Every OptionRow reads a persisted
            // int, so the guard belongs here rather than at one call site -- any row whose value list ever
            // shrinks has the same trap waiting.
            int idx = values.Length == 0 ? 0 : Mathf.Clamp(initial, 0, values.Length - 1);
            var row = new HBoxContainer { CustomMinimumSize = new Vector2(340f, 32f) };
            row.AddThemeConstantOverride("separation", 6);
            var name = new Label { Text = label, CustomMinimumSize = new Vector2(110f, 0f), VerticalAlignment = VerticalAlignment.Center };
            name.AddThemeFontSizeOverride("font_size", 15);
            row.AddChild(name);
            var prev = new Button { Text = "<", CustomMinimumSize = new Vector2(30f, 30f) };
            var val = new Label
            {
                Text = values[idx],
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                CustomMinimumSize = new Vector2(160f, 30f),
            };
            val.AddThemeFontSizeOverride("font_size", 15);
            val.AddThemeColorOverride("font_color", new Color(0.95f, 0.95f, 0.9f));
            var next = new Button { Text = ">", CustomMinimumSize = new Vector2(30f, 30f) };
            prev.Pressed += () => { idx = (idx - 1 + values.Length) % values.Length; val.Text = values[idx]; onChange(idx); };
            next.Pressed += () => { idx = (idx + 1) % values.Length; val.Text = values[idx]; onChange(idx); };
            row.AddChild(prev);
            row.AddChild(val);
            row.AddChild(next);
            return row;
        }

        // ---- Reset data (master 2026-09-03: "add a reset data button below the zombie on/off toggle, deletes
        // the save file"). Sits directly under Zombies, outside the Config fold, because it is not a gameplay
        // option -- it destroys a world.
        //
        // TWO CLICKS, deliberately. There is no confirm dialog in this menu and one misclick would delete
        // somebody's base permanently, so the button ARMS first and says what it is about to delete. It
        // disarms on any map change, so an armed click cannot land on a world you were not looking at.
        Button _resetBtn;
        bool _resetArmed;

        Control BuildResetDataButton()
        {
            _resetBtn = new Button { CustomMinimumSize = new Vector2(340f, 34f), Alignment = HorizontalAlignment.Left };
            _resetBtn.AddThemeFontSizeOverride("font_size", 15);
            _resetBtn.Pressed += OnResetPressed;
            RefreshResetButton();
            return _resetBtn;
        }

        void OnResetPressed()
        {
            if (!SaveExistsForSelectedMap()) { RefreshResetButton(); return; }
            if (!_resetArmed) { _resetArmed = true; RefreshResetButton(); return; }

            string path = WorldSaveDriver.PathFor(SelectedMapFolder);
            var dir = DirAccess.Open(path.GetBaseDir());
            bool ok = dir != null && dir.Remove(path.GetFile()) == Error.Ok;
            _resetArmed = false;
            RefreshResetButton();
            if (_descLabel != null)
                _descLabel.Text = ok ? $"Deleted the saved world for {_selectedMap}."
                                     : $"Could not delete the save at {path}.";
        }

        bool SaveExistsForSelectedMap()
            => !string.IsNullOrEmpty(SelectedMapFolder)
               && Godot.FileAccess.FileExists(WorldSaveDriver.PathFor(SelectedMapFolder));

        void RefreshResetButton()
        {
            if (_resetBtn == null) return;
            bool has = SaveExistsForSelectedMap();
            _resetBtn.Disabled = !has;
            _resetBtn.Text = !has ? "  Reset Data  (no save)"
                           : _resetArmed ? $"  Delete {_selectedMap}'s world?  Click again"
                           : "  Reset Data";
        }

        /// <summary>Called wherever the map selection changes: an armed delete must never survive the player
        /// switching maps, or the second click lands on the wrong world.</summary>
        void DisarmReset()
        {
            _resetArmed = false;
            RefreshResetButton();
        }

        Control ToggleRow(string label, bool initial, System.Action<bool> onChange)
        {
            var row = new HBoxContainer { CustomMinimumSize = new Vector2(340f, 32f) };
            row.AddThemeConstantOverride("separation", 6);
            var name = new Label { Text = label, CustomMinimumSize = new Vector2(110f, 0f), VerticalAlignment = VerticalAlignment.Center };
            name.AddThemeFontSizeOverride("font_size", 15);
            row.AddChild(name);
            var chk = new CheckButton { ButtonPressed = initial };
            chk.Toggled += (bool v) => onChange(v);
            row.AddChild(chk);
            return row;
        }

        // Launch the selected map with the chosen (real) zombie mode. Difficulty / loot / day / combat / cheats /
        // permadeath are cosmetic for now -- the one wired option is Zombies.
        void PlaySelected()
        {
            // Apply the one REAL gameplay option before any world build starts. WorldBuilder guards the SPAWN,
            // so this has to be set before the build, not after.
            WorldBuilder.ZombiesOverride = _optZombies == 1;
            if (_playgroundSelected) { OnPlayground?.Invoke(); return; }   // the gun range, not a survival map
            if (_generateSelected)
            {
                // Read the field rather than trusting _genSeed: TextChanged only fires on a parseable value, so
                // a field left mid-edit ("12x") would otherwise silently launch the previous seed.
                if (_genSeedEdit != null && !int.TryParse(_genSeedEdit.Text, out _genSeed))
                {
                    if (_descLabel != null) _descLabel.Text = "Seed must be a whole number.";
                    return;
                }
                OnGenerateMap?.Invoke(_genSeed);
                return;
            }
            if (!_selectedPlayable)
            {
                if (_descLabel != null) _descLabel.Text = _selectedMap + " isn't ported yet — only Prince Edward Island is playable right now.";
                return;
            }
            OnDrivePEI?.Invoke();
        }
    }
}
