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
        int _optZombies    = 0;   // 0 Normal / 1 No Zombies / 2 New  (REAL)
        int _optLoot       = 1;   // 0 Sparse / 1 Normal / 2 Abundant (dummy)
        int _optDay        = 1;   // 0 Short / 1 Default / 2 Long / 3 Endless Day (dummy)
        int _optCombat     = 0;   // 0 PvE / 1 PvP                    (dummy)
        bool _optCheats     = false;
        bool _optPermadeath = false;

        string _selectedMap = "Prince Edward Island";
        bool _selectedPlayable = true;
        // the Steam Maps/<folder> name for the selected map -- Main reads this to point the world at the right map.
        // PEI's folder is "PEI" (its display name is "Prince Edward Island"); every other map's folder == its name.
        public string SelectedMapFolder = "PEI";

        static readonly string[] Difficulties = { "Easy", "Normal", "Hard" };
        static readonly string[] ZombieModes  = { "Normal", "No Zombies", "New Zombies" };
        static readonly string[] LootModes    = { "Sparse", "Normal", "Abundant" };
        static readonly string[] DayModes     = { "Short", "Default", "Long", "Endless Day" };
        static readonly string[] CombatModes  = { "PvE", "PvP" };

        // The per-map gameplay options above are plain fields that reset to their defaults every launch. Persist
        // them to user:// (Godot's writable per-user dir -- survives restart AND works in an exported build, unlike
        // the res:// content the in-editor Save targets) so a player's chosen mode sticks across restarts.
        const string MapSettingsPath = "user://map_settings.cfg";

        void LoadMapSettings()   // restore saved options BEFORE BuildMapSelector reads the fields into the rows
        {
            var cfg = new ConfigFile();
            if (cfg.Load(MapSettingsPath) != Error.Ok) return;   // nothing saved yet -> keep defaults
            _optDifficulty = cfg.GetValue("map", "difficulty", _optDifficulty).AsInt32();
            _optZombies    = cfg.GetValue("map", "zombies",    _optZombies).AsInt32();
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
            ("Yukon",                "yukon",      false, "Harsh, freezing territory in North-West Canada. Barren frozen wasteland scattered with camps and cabins. Tourist attractions include skiing, skating and train spotting. Recommended for experienced survivors."),
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
            var panel = new PanelContainer { Position = new Vector2(240f, 148f), Visible = false };
            var margin = new MarginContainer();
            foreach (var s in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
                margin.AddThemeConstantOverride(s, 14);
            panel.AddChild(margin);
            var cols = new HBoxContainer();
            cols.AddThemeConstantOverride("separation", 18);
            margin.AddChild(cols);

            // ---- left column: header + category tabs + scrollable map list
            var left = new VBoxContainer { CustomMinimumSize = new Vector2(360f, 0f) };
            left.AddThemeConstantOverride("separation", 8);
            cols.AddChild(left);
            left.AddChild(Header("SINGLEPLAYER", 24));

            var tabs = new HBoxContainer();
            tabs.AddThemeConstantOverride("separation", 4);
            left.AddChild(tabs);
            string[] cats = { "Official", "Curated", "Workshop", "Misc" };
            for (int i = 0; i < cats.Length; i++)
            {
                var tb = new Button { Text = cats[i], ToggleMode = true, ButtonPressed = i == 0, Disabled = i != 0 };
                tb.AddThemeFontSizeOverride("font_size", 14);
                tabs.AddChild(tb);
            }

            var scroll = new ScrollContainer
            {
                CustomMinimumSize = new Vector2(360f, 300f),
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            };
            left.AddChild(scroll);
            var list = new VBoxContainer { CustomMinimumSize = new Vector2(342f, 0f) };
            list.AddThemeConstantOverride("separation", 3);
            scroll.AddChild(list);
            foreach (var m in OfficialMaps) list.AddChild(MapRow(m.name, m.key, m.playable, m.desc));

            // ---- right column: preview + name + desc + gameplay options + play
            var right = new VBoxContainer { CustomMinimumSize = new Vector2(340f, 0f) };
            right.AddThemeConstantOverride("separation", 7);
            cols.AddChild(right);

            var preview = new Panel { CustomMinimumSize = new Vector2(340f, 190f) };
            var pv = new StyleBoxFlat { BgColor = new Color(0.10f, 0.11f, 0.10f) };
            pv.SetBorderWidthAll(1);
            pv.BorderColor = new Color(0f, 0f, 0f, 0.5f);
            preview.AddThemeStyleboxOverride("panel", pv);
            var pvlabel = new Label { Text = "MAP PREVIEW", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            pvlabel.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            pvlabel.AddThemeColorOverride("font_color", new Color(0.4f, 0.4f, 0.4f));
            preview.AddChild(pvlabel);   // fallback text behind the image (shows for maps with no preview art)
            _previewImage = new TextureRect
            {
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                Texture = LoadTex("mappreview_pei.png"),
            };
            _previewImage.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            preview.AddChild(_previewImage);
            right.AddChild(preview);

            _previewName = new Label { Text = _selectedMap };
            _previewName.AddThemeFontSizeOverride("font_size", 22);
            _previewName.AddThemeColorOverride("font_color", new Color(0.95f, 0.94f, 0.9f));
            right.AddChild(_previewName);
            _descLabel = new Label
            {
                Text = OfficialMaps[0].desc,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(340f, 34f),
            };
            _descLabel.AddThemeColorOverride("font_color", new Color(0.78f, 0.78f, 0.78f));
            _descLabel.AddThemeFontSizeOverride("font_size", 14);
            right.AddChild(_descLabel);

            right.AddChild(new HSeparator());
            right.AddChild(Header("GAMEPLAY OPTIONS", 16));
            right.AddChild(OptionRow("Difficulty", Difficulties, _optDifficulty, i => { _optDifficulty = i; SaveMapSettings(); }));
            right.AddChild(OptionRow("Zombies",    ZombieModes,  _optZombies,    i => { _optZombies = i; SaveMapSettings(); }));   // REAL
            right.AddChild(OptionRow("Loot",       LootModes,    _optLoot,       i => { _optLoot = i; SaveMapSettings(); }));
            right.AddChild(OptionRow("Day Cycle",  DayModes,     _optDay,        i => { _optDay = i; SaveMapSettings(); }));
            right.AddChild(OptionRow("Combat",     CombatModes,  _optCombat,     i => { _optCombat = i; SaveMapSettings(); }));
            right.AddChild(ToggleRow("Cheats",     _optCheats,     v => { _optCheats = v; SaveMapSettings(); }));
            right.AddChild(ToggleRow("Permadeath", _optPermadeath, v => { _optPermadeath = v; SaveMapSettings(); }));
            right.AddChild(AdvancedButton());   // reveals the full vanilla ModeConfigData options (MainMenuAdvanced.cs)

            right.AddChild(new Control { CustomMinimumSize = new Vector2(0f, 6f) });   // spacer
            var play = new Button { Text = "PLAY", CustomMinimumSize = new Vector2(340f, 52f) };
            play.AddThemeFontSizeOverride("font_size", 24);
            play.Pressed += PlaySelected;
            right.AddChild(play);

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

        void SelectMap(string name, string key, bool playable, string desc)
        {
            _selectedMap = name;
            _selectedPlayable = playable;
            SelectedMapFolder = name == "Prince Edward Island" ? "PEI" : name;   // display name -> Steam Maps/ folder

            if (_previewName != null) _previewName.Text = name;
            if (_descLabel != null) _descLabel.Text = desc;
            if (_previewImage != null) _previewImage.Texture = key != "" ? LoadTex($"mappreview_{key}.png") : null;
        }

        // labeled left/right value cycler (retail SleekButtonState). onChange fires with the new index.
        Control OptionRow(string label, string[] values, int initial, System.Action<int> onChange)
        {
            int idx = initial;
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
            if (!_selectedPlayable)
            {
                if (_descLabel != null) _descLabel.Text = _selectedMap + " isn't ported yet — only Prince Edward Island is playable right now.";
                return;
            }
            switch (_optZombies)
            {
                case 1: OnDrivePEI?.Invoke(true); break;     // No Zombies
                case 2: OnDriveNewZombies?.Invoke(); break;  // New Zombies (rewrite)
                default: OnDrivePEI?.Invoke(false); break;   // Normal
            }
        }
    }
}
