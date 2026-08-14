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
        Label _weaponName;   // held weapon, tinted by its rarity (master)
        Label _alert; float _alertLeft;   // centre-screen transient error (unknown gun); _alertLeft = seconds remaining

        /// <summary>The live HUD, so a non-UI system can surface a player-facing error without threading a reference
        /// through every call site. Null under --headless / in a test sandbox, which is why every caller null-checks
        /// rather than this being a hard dependency -- an error report must never be the thing that crashes.</summary>
        public static HUD Current;

        /// <summary>Flash a message across the centre of the screen for `seconds`, then fade. Static so the equip path
        /// can report a content problem it can't fix; a no-op when there is no HUD (dedicated server, tests).</summary>
        public static void Alert(string text, float seconds = 3f)
        {
            GD.Print($"[alert] {text}");   // always logged, HUD or not -- a headless run still records it
            if (Current == null || !GodotObject.IsInstanceValid(Current) || Current._alert == null) return;
            Current._alert.Text = text;
            Current._alertLeft = seconds;
        }
        ColorRect _pain;   // PlayerUI colorOverlayImage: full-screen COLOR_R tint, alpha = the player's painAlpha
        Control _crosshair3p;   // centre-screen crosshair, shown only in third person (master) -- the 1P has the viewmodel reticle, the 3P had nothing marking the aim

        // PlayerUI messageBox (VEHICLE_ENTER): bottom-centre dark box with the vehicle's fuel/health/battery bars, shown while driving
        public Vehicle Vehicle;   // bound driven vehicle; the box is visible while this is non-null
        Control _vehBox; Label _vehTitle, _vehRpmGear; ColorRect _vehFuel, _vehHealth, _vehBattery;
        readonly System.Collections.Generic.List<Control> _playerOnly = new();   // on-foot HUD, hidden when Player == null (vehicle-only render)

        public override void _Ready()
        {
            Layer = 10;   // draw over the viewmodel composite (CanvasLayer 5)
            Current = this;

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
            AddVital(lifeBox, 4, "hud_virus.png",   CG, () => Player != null ? 1f - Player.Infection : 1f, null);   // INFECTION meter: ALWAYS shown, starts FULL (healthy), depletes as infection rises (master)

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

            // WEAPON NAME above the fire mode, in the item's RARITY colour (master). Same colour table the inventory
            // tile and the ground look-at rim use (ItemTool.RarityColorUI) rather than a second one -- a HUD that
            // disagreed with the bag about an item's rarity would be worse than showing no colour at all.
            _weaponName = new Label();
            _weaponName.AddThemeFontSizeOverride("font_size", 20);
            _weaponName.AddThemeFontOverride("font", new FontVariation { VariationEmbolden = 0.5f });
            _weaponName.AddThemeColorOverride("font_outline_color", Colors.Black);
            _weaponName.AddThemeConstantOverride("outline_size", 4);   // rarity colours run dark (common grey); the outline keeps it readable on pale terrain
            _weaponName.AnchorLeft = 1; _weaponName.AnchorRight = 1; _weaponName.AnchorTop = 1; _weaponName.AnchorBottom = 1;
            _weaponName.OffsetLeft = -420; _weaponName.OffsetRight = -22; _weaponName.OffsetTop = -146; _weaponName.OffsetBottom = -120;
            _weaponName.HorizontalAlignment = HorizontalAlignment.Right;
            _weaponName.MouseFilter = Control.MouseFilterEnum.Ignore;
            root.AddChild(_weaponName);

            _playerOnly.Add(_fireMode);
            _playerOnly.Add(_ammo);
            _playerOnly.Add(_weaponName);

            // CENTRE-SCREEN ALERT (strawberry: "unknown gun should spit an error center screen and fallback to
            // unarmed"). A transient line just above the crosshair -- high enough to read without covering what you
            // are aiming at, and it holds then fades rather than vanishing on the next frame, because the thing it
            // reports is a content problem the player can't act on and shouldn't have to catch.
            // Deliberately NOT in _playerOnly: a content error is worth showing even on a HUD with no player bound.
            _alert = new Label { Text = "", HorizontalAlignment = HorizontalAlignment.Center };
            _alert.AddThemeFontSizeOverride("font_size", 26);
            _alert.AddThemeColorOverride("font_color", new Color(0.98f, 0.42f, 0.35f));   // error red, readable on grass and on sky
            _alert.AddThemeColorOverride("font_outline_color", Colors.Black);
            _alert.AddThemeConstantOverride("outline_size", 6);
            _alert.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
            _alert.AnchorLeft = 0f; _alert.AnchorRight = 1f; _alert.AnchorTop = 0.5f; _alert.AnchorBottom = 0.5f;
            _alert.OffsetTop = -120f; _alert.OffsetBottom = -84f;
            _alert.MouseFilter = Control.MouseFilterEnum.Ignore;
            _alert.Modulate = new Color(1f, 1f, 1f, 0f);
            root.AddChild(_alert);

            // Centre-screen crosshair for THIRD person (master). Sits dead-centre with a small gap so it frames the aim
            // point without covering it; the 3P camera toes in on the converged aim, so screen-centre is where the shot
            // goes. Hidden by default -> _Process shows it only while the 3P camera is live. Thin lines, dark outline so
            // it reads on sky and on grass alike (strawberry earlier: "thinner crosshair lines").
            _crosshair3p = new Crosshair3PControl { Visible = false };
            _crosshair3p.SetAnchorsPreset(Control.LayoutPreset.Center);
            _crosshair3p.MouseFilter = Control.MouseFilterEnum.Ignore;
            root.AddChild(_crosshair3p);

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
            if (_crosshair3p != null) _crosshair3p.Visible = Player != null && Player.ThirdPersonActive;   // centre crosshair: third person only

            // centre-screen alert: hold at full opacity, then fade over the last second so it clears itself
            if (_alert != null && _alertLeft > 0f)
            {
                _alertLeft -= (float)delta;
                _alert.Modulate = new Color(1f, 1f, 1f, Mathf.Clamp(_alertLeft, 0f, 1f));
                if (_alertLeft <= 0f) _alert.Text = "";
            }

            foreach (var (fill, val) in _vitals)
                fill.AnchorRight = Mathf.Clamp(val(), 0f, 1f);   // foreground.SizeScale_X = state
            foreach (var (ic, bg, show) in _vitalRows) { bool s = show(); ic.Visible = s; bg.Visible = s; }   // situational vitals (virus) shown only on condition
            foreach (var (box, on) in _status)
                box.Visible = on();
            if (Player != null)
            {
                bool gun = Player.HasGunOut;                       // ammo counter + firemode ONLY when a gun is genuinely out (master: off for fists/melee/held item)
                _ammo.Visible = gun; _fireMode.Visible = gun; _weaponName.Visible = gun;
                if (gun)
                {
                    // "x + 1 / max" when a round rides the chamber (master). The +1 is REAL state, not decoration:
                    // Ammo can legitimately exceed AmmoMax, and rendering it as a flat "31 / 30" reads as a bug in
                    // the counter rather than as the extra round it actually is.
                    int max = Player.Gun?.AmmoMax ?? Player.Ammo;
                    int inMag = System.Math.Min(Player.Ammo, max);
                    int chambered = System.Math.Max(0, Player.Ammo - max);
                    _ammo.Text = chambered > 0 ? $"{inMag} + {chambered} / {max}" : $"{inMag} / {max}";
                    // shotguns: show the LOADED SHELL TYPE right above the ammo count so the readout reflects buckshot
                    // vs slug (master); other guns keep the fire mode. Slug tints green to match its round, buckshot amber.
                    var shell = Player.LoadedShellName;
                    _fireMode.Text = shell ?? Player.FiremodeName;
                    _fireMode.Modulate = shell == null ? new Color(1f, 1f, 1f, 0.7f)
                                       : shell.Contains("Slug") ? new Color(0.62f, 1f, 0.66f, 0.95f)
                                       : new Color(1f, 0.92f, 0.7f, 0.9f);
                    var asset = Player.HeldItemForTest != null ? SDG.Unturned.Assets.find(Player.HeldItemForTest.id) : null;
                    _weaponName.Text = asset?.itemName ?? Player.Gun?.Id ?? "";   // Id is the .dat stem, a readable fallback when the asset is missing
                    _weaponName.Modulate = asset != null ? SDG.Unturned.ItemTool.RarityColorUI(asset.rarity) : Colors.White;
                }
                _pain.Color = new Color(CR.R, CR.G, CR.B, Player.PainAlpha);   // colorOverlayImage.TintColor.a = painAlpha
            }

            _vehBox.Visible = Vehicle != null;   // messageBox: shown while in a vehicle
            if (Vehicle != null)
            {
                _vehFuel.AnchorRight = Mathf.Clamp(Vehicle.FuelNorm, 0f, 1f);
                _vehHealth.AnchorRight = Mathf.Clamp(Vehicle.HealthNorm, 0f, 1f);
                _vehBattery.AnchorRight = Mathf.Clamp(Vehicle.BatteryNorm, 0f, 1f);
                _vehTitle.Text = Vehicle.DisplayName;
                _vehRpmGear.Text = $"{Vehicle.LinearVelocity.Length() * 2.23694f:0} mph · {Vehicle.EngineRpm:0} rpm · {Vehicle.GearLabel}";   // MPH speedometer + rpm + gear (master)
            }
        }

        static Texture2D LoadTex(string res)
        {
            string p = ProjectSettings.GlobalizePath(res);
            if (System.IO.File.Exists(p)) { var img = Image.LoadFromFile(p); if (img != null) return ImageTexture.CreateFromImage(img); }
            return null;
        }

        // The third-person centre crosshair: four thin arms with a centre gap plus a centre dot, drawn about the node's
        // own origin (the node is anchored to screen centre). Each mark is laid as a dark outline rect with a white rect
        // on top, so the reticle keeps its contrast whether it sits over bright sky or dark ground.
        private sealed partial class Crosshair3PControl : Control
        {
            const float Gap = 3f, Len = 9f, Thick = 2f, Dot = 3f;   // gap from centre, arm length, arm thickness, centre dot
            static readonly Color Ink = Colors.White;                      // the mark (thin white lines)
            static readonly Color Edge = new Color(0f, 0f, 0f, 0.85f);     // a solid dark halo so it reads on sky, grass or a wall alike

            public override void _Draw()
            {
                // arms: up / down / left / right
                DrawArm(new Rect2(-Thick * 0.5f, -Gap - Len, Thick, Len));
                DrawArm(new Rect2(-Thick * 0.5f,  Gap,       Thick, Len));
                DrawArm(new Rect2(-Gap - Len, -Thick * 0.5f, Len, Thick));
                DrawArm(new Rect2( Gap,       -Thick * 0.5f, Len, Thick));
                DrawArm(new Rect2(-Dot * 0.5f, -Dot * 0.5f, Dot, Dot));   // centre dot
            }

            void DrawArm(Rect2 r)
            {
                DrawRect(r.Grow(1.5f), Edge, filled: true);   // dark halo (1.5px) under the mark for contrast
                DrawRect(r, Ink, filled: true);               // the white line itself, kept thin
            }
        }
    }
}
