using Godot;

namespace UnturnedGodot
{
    // 1:1 port of the Unturned in-game HUD (PlayerLifeUI). Vitals live in a bottom-left box 20% of the screen wide
    // (lifeBox: PositionScale_Y=1, SizeScale_X=0.2). They stack TOP-DOWN — health, food, water, stamina — 30px per
    // row: a 20x20 icon at x=5 and a bar at x=30 filling the box width (SizeOffset_X=-40), the bar 5px below the icon
    // top. Each bar is a SleekProgress: a background tinted (colour @ 0.5 alpha) full-width + a foreground tinted
    // (colour, full alpha) whose width is the vital fraction. Colours are the real Palette values. Icons are the real
    // ui/player/icons/playerlife textures from core.masterbundle. Above the vitals sit the situational status icons
    // (bleeding/broken/etc.), hidden unless the condition is met — exactly as the source. No menus.
    public partial class HUD : CanvasLayer
    {
        public PlayerController Player;

        // Palette.cs: COLOR_R (health), COLOR_O (food), COLOR_B (water), COLOR_Y (stamina), COLOR_G (virus), cyan (oxygen).
        static readonly Color CR = new Color(0.7490196f, 0.12156863f, 0.12156863f);
        static readonly Color CO = new Color(57f / 85f, 0.5019608f, 5f / 51f);
        static readonly Color CB = new Color(10f / 51f, 0.59607846f, 40f / 51f);
        static readonly Color CY = new Color(44f / 51f, 0.7058824f, 0.07450981f);
        static readonly Color CG = new Color(0.24f, 0.71f, 0.29f);   // Palette COLOR_G (virus / infection)

        const float IconSz = 20f, IconX = 5f, BarX = 30f, BarH = 10f, RowH = 30f, TopPad = 5f;

        readonly System.Collections.Generic.List<(ColorRect fill, System.Func<float> val)> _vitals = new();
        readonly System.Collections.Generic.List<(Control ic, Control bg, System.Func<bool> show)> _vitalRows = new();   // situational vitals (virus): whole row hidden unless its condition holds
        readonly System.Collections.Generic.List<(Control box, System.Func<bool> on)> _status = new();
        Label _ammo;
        Label _fireMode;
        ColorRect _pain;   // PlayerUI colorOverlayImage: full-screen COLOR_R tint, alpha = the player's painAlpha

        // PlayerUI messageBox (VEHICLE_ENTER): bottom-centre dark box with the vehicle's fuel/health/battery bars, shown while driving
        public Vehicle Vehicle;   // bound driven vehicle; the box is visible while this is non-null
        Control _vehBox; Label _vehTitle, _vehRpmGear; ColorRect _vehFuel, _vehHealth, _vehBattery;
        readonly System.Collections.Generic.List<Control> _playerOnly = new();   // on-foot HUD, hidden when Player == null (vehicle-only render)

        public override void _Ready()
        {
            Layer = 10;   // draw over the viewmodel composite (CanvasLayer 5)

            var root = new Control();
            root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            root.MouseFilter = Control.MouseFilterEnum.Ignore;
            AddChild(root);

            // hurt flash (PlayerUI colorOverlayImage): a red screen tint added first so it sits UNDER the vitals/status.
            // The source draws it as COLOR_R with alpha = painAlpha; the tint is fully red the moment any pain exists, so
            // only the alpha animates. Starts invisible (alpha 0) and is driven each frame from Player.PainAlpha.
            _pain = new ColorRect { Color = new Color(CR.R, CR.G, CR.B, 0f) };
            _pain.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _pain.MouseFilter = Control.MouseFilterEnum.Ignore;
            root.AddChild(_pain);
            _playerOnly.Add(_pain);

            // lifeBox: bottom-left, right edge at 20% of the screen, sized to the 4 always-on vitals
            var lifeBox = new Control();
            lifeBox.AnchorLeft = 0f; lifeBox.AnchorRight = 0.2f; lifeBox.AnchorTop = 1f; lifeBox.AnchorBottom = 1f;
            lifeBox.OffsetLeft = 0f; lifeBox.OffsetRight = 0f;
            lifeBox.OffsetTop = -(TopPad + 5f * RowH); lifeBox.OffsetBottom = -6f;   // 5 rows: 4 always-on vitals + the situational virus meter
            lifeBox.MouseFilter = Control.MouseFilterEnum.Ignore;
            root.AddChild(lifeBox);
            _playerOnly.Add(lifeBox);

            // top-down: health, food, water, stamina (the always-visible vitals; virus/oxygen are situational)
            AddVital(lifeBox, 0, "hud_health.png",  CR, () => Player != null ? Player.Health / Mathf.Max(1f, Player.MaxHealth) : 1f);
            AddVital(lifeBox, 1, "hud_food.png",    CO, () => Player != null ? Player.Food    : 1f);
            AddVital(lifeBox, 2, "hud_water.png",   CB, () => Player != null ? Player.Water   : 1f);
            AddVital(lifeBox, 3, "hud_stamina.png", CY, () => Player != null ? Player.Stamina : 1f);
            AddVital(lifeBox, 4, "hud_virus.png",   CG, () => Player != null ? Player.Infection : 0f, () => Player != null && Player.Infection > 0.001f);   // situational INFECTION meter (master)

            // status icons (PlayerLifeUI.statusIconsContainer): a row of 40x40 boxes above the vitals, each shown
            // ONLY on its condition — bleeding after a hit; broken/starved need the survival sim so they stay hidden.
            AddStatus(root, 0, "hud_bleeding.png", () => Player != null && Player.Bleeding);
            AddStatus(root, 1, "hud_broken.png",   () => Player != null && Player.Broken);
            AddStatus(root, 2, "hud_starved.png",  () => Player != null && (Player.Food <= 0f || Player.Water <= 0f));
            // virus is now the situational infection METER in the vitals (above), not a binary status icon (master)

            // ammo count, bottom-right
            _ammo = new Label();
            _ammo.AddThemeFontSizeOverride("font_size", 56);   // master: 2x bigger (was 28)
            _ammo.AddThemeFontOverride("font", new FontVariation { VariationEmbolden = 0.75f });   // master: bold
            _ammo.AddThemeColorOverride("font_outline_color", Colors.Black);
            _ammo.AddThemeConstantOverride("outline_size", 5);   // readable over any background at the bigger size
            _ammo.AnchorLeft = 1; _ammo.AnchorRight = 1; _ammo.AnchorTop = 1; _ammo.AnchorBottom = 1;
            _ammo.OffsetLeft = -360; _ammo.OffsetRight = -22; _ammo.OffsetTop = -92; _ammo.OffsetBottom = -22;
            _ammo.HorizontalAlignment = HorizontalAlignment.Right;
            _ammo.MouseFilter = Control.MouseFilterEnum.Ignore;
            root.AddChild(_ammo);

            // fire mode, just ABOVE the ammo count
            _fireMode = new Label();
            _fireMode.AddThemeFontSizeOverride("font_size", 18);
            _fireMode.AnchorLeft = 1; _fireMode.AnchorRight = 1; _fireMode.AnchorTop = 1; _fireMode.AnchorBottom = 1;
            _fireMode.OffsetLeft = -190; _fireMode.OffsetRight = -22; _fireMode.OffsetTop = -120; _fireMode.OffsetBottom = -94;
            _fireMode.HorizontalAlignment = HorizontalAlignment.Right;
            _fireMode.Modulate = new Color(1f, 1f, 1f, 0.7f);
            _fireMode.MouseFilter = Control.MouseFilterEnum.Ignore;
            root.AddChild(_fireMode);
            _playerOnly.Add(_fireMode);
            _playerOnly.Add(_ammo);

            // vehicle status box (PlayerUI messageBox on VEHICLE_ENTER): 300-wide dark box, bottom-centre, 80px above the
            // screen bottom. Rows pack from y=45 at 30px: Fuel (COLOR_Y), Health (COLOR_R), Battery (COLOR_Y, Stamina icon).
            _vehBox = new Control();
            _vehBox.AnchorLeft = 0.5f; _vehBox.AnchorRight = 0.5f; _vehBox.AnchorTop = 1f; _vehBox.AnchorBottom = 1f;
            _vehBox.OffsetLeft = -150f; _vehBox.OffsetRight = 150f; _vehBox.OffsetTop = -210f; _vehBox.OffsetBottom = -80f;
            _vehBox.MouseFilter = Control.MouseFilterEnum.Ignore; _vehBox.Visible = false;
            root.AddChild(_vehBox);

            var vbg = new ColorRect { Color = new Color(0.08f, 0.08f, 0.08f, 0.9f) };   // ESleekTint.BACKGROUND (dark box)
            vbg.SetAnchorsPreset(Control.LayoutPreset.FullRect); vbg.MouseFilter = Control.MouseFilterEnum.Ignore;
            _vehBox.AddChild(vbg);

            _vehTitle = new Label();
            _vehTitle.AddThemeFontSizeOverride("font_size", 18);
            _vehTitle.AnchorRight = 1f; _vehTitle.OffsetLeft = 5; _vehTitle.OffsetRight = -5; _vehTitle.OffsetTop = 5; _vehTitle.OffsetBottom = 45;
            _vehTitle.MouseFilter = Control.MouseFilterEnum.Ignore;
            _vehBox.AddChild(_vehTitle);

            _vehRpmGear = new Label();   // RPM + gear read-out, right-aligned on the title row (master)
            _vehRpmGear.AddThemeFontSizeOverride("font_size", 15);
            _vehRpmGear.AnchorRight = 1f; _vehRpmGear.OffsetLeft = 5; _vehRpmGear.OffsetRight = -8; _vehRpmGear.OffsetTop = 12; _vehRpmGear.OffsetBottom = 45;
            _vehRpmGear.HorizontalAlignment = HorizontalAlignment.Right; _vehRpmGear.MouseFilter = Control.MouseFilterEnum.Ignore;
            _vehBox.AddChild(_vehRpmGear);

            _vehFuel = AddVehBar(45, "icon_fuel.png", CY);        // fuel = COLOR_Y (yellow)
            _vehHealth = AddVehBar(75, "icon_health.png", CR);    // health = COLOR_R (red)
            _vehBattery = AddVehBar(105, "icon_stamina.png", CY); // battery = COLOR_Y, "Stamina" icon (source)
        }

        // one messageBox row: a 20x20 icon at x=5 and a SleekProgress bar at x=30 (right edge box-10, 5px below the icon top)
        ColorRect AddVehBar(float y, string icon, Color color)
        {
            var ic = new TextureRect { Texture = LoadTex($"res://content/{icon}"), StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered };
            ic.OffsetLeft = IconX; ic.OffsetRight = IconX + IconSz; ic.OffsetTop = y; ic.OffsetBottom = y + IconSz;
            ic.MouseFilter = Control.MouseFilterEnum.Ignore;
            _vehBox.AddChild(ic);

            var bg = new ColorRect { Color = new Color(color.R, color.G, color.B, 0.5f) };   // SleekProgress background (colour @ 0.5 alpha)
            bg.AnchorLeft = 0; bg.AnchorRight = 1; bg.OffsetLeft = BarX; bg.OffsetRight = -10; bg.OffsetTop = y + 5; bg.OffsetBottom = y + 5 + BarH;
            bg.MouseFilter = Control.MouseFilterEnum.Ignore;
            _vehBox.AddChild(bg);

            var fill = new ColorRect { Color = color };   // foreground, width = state fraction
            fill.AnchorLeft = 0; fill.AnchorRight = 1f; fill.AnchorTop = 0; fill.AnchorBottom = 1;
            fill.MouseFilter = Control.MouseFilterEnum.Ignore;
            bg.AddChild(fill);
            return fill;
        }

        void AddVital(Control box, int i, string icon, Color color, System.Func<float> val, System.Func<bool> show = null)
        {
            float y = TopPad + i * RowH;
            // icon: 20x20 at x=5, at the row top
            var ic = new TextureRect { Texture = LoadTex($"res://content/{icon}"), StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered };
            ic.AnchorLeft = 0; ic.AnchorTop = 0; ic.AnchorRight = 0; ic.AnchorBottom = 0;
            ic.OffsetLeft = IconX; ic.OffsetRight = IconX + IconSz; ic.OffsetTop = y; ic.OffsetBottom = y + IconSz;
            ic.MouseFilter = Control.MouseFilterEnum.Ignore;
            box.AddChild(ic);

            // bar at x=30, right edge = box width - 10 (SizeOffset_X=-40 from x=30), 5px below the icon top
            var bg = new ColorRect { Color = new Color(color.R, color.G, color.B, 0.5f) };   // SleekProgress background
            bg.AnchorLeft = 0; bg.AnchorRight = 1; bg.AnchorTop = 0; bg.AnchorBottom = 0;
            bg.OffsetLeft = BarX; bg.OffsetRight = -10; bg.OffsetTop = y + 5; bg.OffsetBottom = y + 5 + BarH;
            bg.MouseFilter = Control.MouseFilterEnum.Ignore;
            box.AddChild(bg);

            var fill = new ColorRect { Color = color };   // SleekProgress foreground, width = state fraction
            fill.AnchorLeft = 0; fill.AnchorRight = 1f; fill.AnchorTop = 0; fill.AnchorBottom = 1;
            fill.OffsetLeft = 0; fill.OffsetRight = 0; fill.OffsetTop = 0; fill.OffsetBottom = 0;
            fill.MouseFilter = Control.MouseFilterEnum.Ignore;
            bg.AddChild(fill);
            _vitals.Add((fill, val));
            if (show != null) _vitalRows.Add((ic, bg, show));   // situational: whole row hidden unless `show` is true
        }

        // a 40x40 status box (SleekBoxIcon): dark background + centred icon, shown only on its condition
        void AddStatus(Control root, int i, string icon, System.Func<bool> on)
        {
            var box = new Control();
            Anchor(box, 8f + i * 46f, 132f, 40f, 40f);
            var bg = new ColorRect { Color = new Color(0f, 0f, 0f, 0.5f) };
            bg.SetAnchorsPreset(Control.LayoutPreset.FullRect); bg.MouseFilter = Control.MouseFilterEnum.Ignore;
            box.AddChild(bg);
            var ic = new TextureRect { Texture = LoadTex($"res://content/{icon}"), StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered };
            ic.SetAnchorsPreset(Control.LayoutPreset.FullRect); ic.MouseFilter = Control.MouseFilterEnum.Ignore;
            box.AddChild(ic);
            box.Visible = false;
            root.AddChild(box);
            _status.Add((box, on));
        }

        // anchor a control to the screen bottom-left: `up` px above the bottom, at x, sized w x h
        static void Anchor(Control c, float x, float up, float w, float h)
        {
            c.AnchorLeft = 0; c.AnchorRight = 0; c.AnchorTop = 1; c.AnchorBottom = 1;
            c.OffsetLeft = x; c.OffsetRight = x + w; c.OffsetBottom = -up; c.OffsetTop = -up - h;
            c.MouseFilter = Control.MouseFilterEnum.Ignore;
        }

        public override void _Process(double delta)
        {
            foreach (var c in _playerOnly) c.Visible = Player != null;   // hide the on-foot HUD in a vehicle-only view

            foreach (var (fill, val) in _vitals)
                fill.AnchorRight = Mathf.Clamp(val(), 0f, 1f);   // foreground.SizeScale_X = state
            foreach (var (ic, bg, show) in _vitalRows) { bool s = show(); ic.Visible = s; bg.Visible = s; }   // situational vitals (virus) shown only on condition
            foreach (var (box, on) in _status)
                box.Visible = on();
            if (Player != null)
            {
                _ammo.Text = $"{Player.Ammo} / {(Player.Gun?.AmmoMax ?? Player.Ammo)}";
                _fireMode.Text = Player.FiremodeName;
                _pain.Color = new Color(CR.R, CR.G, CR.B, Player.PainAlpha);   // colorOverlayImage.TintColor.a = painAlpha
            }

            _vehBox.Visible = Vehicle != null;   // messageBox: shown while in a vehicle
            if (Vehicle != null)
            {
                _vehFuel.AnchorRight = Mathf.Clamp(Vehicle.FuelNorm, 0f, 1f);
                _vehHealth.AnchorRight = Mathf.Clamp(Vehicle.HealthNorm, 0f, 1f);
                _vehBattery.AnchorRight = Mathf.Clamp(Vehicle.BatteryNorm, 0f, 1f);
                _vehTitle.Text = Vehicle.DisplayName;
                _vehRpmGear.Text = $"{Vehicle.EngineRpm:0} rpm · {Vehicle.GearLabel}";
            }
        }

        static Texture2D LoadTex(string res)
        {
            string p = ProjectSettings.GlobalizePath(res);
            if (System.IO.File.Exists(p)) { var img = Image.LoadFromFile(p); if (img != null) return ImageTexture.CreateFromImage(img); }
            return null;
        }
    }
}
