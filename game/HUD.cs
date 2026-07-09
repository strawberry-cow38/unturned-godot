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

        const float IconSz = 20f, IconX = 5f, BarX = 30f, BarH = 10f, RowH = 30f, TopPad = 5f;

        readonly System.Collections.Generic.List<(ColorRect fill, System.Func<float> val)> _vitals = new();
        readonly System.Collections.Generic.List<(Control box, System.Func<bool> on)> _status = new();
        Label _ammo;

        public override void _Ready()
        {
            Layer = 10;   // draw over the viewmodel composite (CanvasLayer 5)

            var root = new Control();
            root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            root.MouseFilter = Control.MouseFilterEnum.Ignore;
            AddChild(root);

            // lifeBox: bottom-left, right edge at 20% of the screen, sized to the 4 always-on vitals
            var lifeBox = new Control();
            lifeBox.AnchorLeft = 0f; lifeBox.AnchorRight = 0.2f; lifeBox.AnchorTop = 1f; lifeBox.AnchorBottom = 1f;
            lifeBox.OffsetLeft = 0f; lifeBox.OffsetRight = 0f;
            lifeBox.OffsetTop = -(TopPad + 4f * RowH); lifeBox.OffsetBottom = -6f;
            lifeBox.MouseFilter = Control.MouseFilterEnum.Ignore;
            root.AddChild(lifeBox);

            // top-down: health, food, water, stamina (the always-visible vitals; virus/oxygen are situational)
            AddVital(lifeBox, 0, "hud_health.png",  CR, () => Player != null ? Player.Health / Mathf.Max(1f, Player.MaxHealth) : 1f);
            AddVital(lifeBox, 1, "hud_food.png",    CO, () => 0.82f);   // food/water/stamina aren't simulated yet -> placeholders
            AddVital(lifeBox, 2, "hud_water.png",   CB, () => 0.68f);
            AddVital(lifeBox, 3, "hud_stamina.png", CY, () => 0.95f);

            // status icons (PlayerLifeUI.statusIconsContainer): a row of 40x40 boxes above the vitals, each shown
            // ONLY on its condition — bleeding after a hit; broken/starved need the survival sim so they stay hidden.
            AddStatus(root, 0, "hud_bleeding.png", () => Player != null && Player.Bleeding);
            AddStatus(root, 1, "hud_broken.png",   () => false);
            AddStatus(root, 2, "hud_starved.png",  () => false);

            // ammo count, bottom-right
            _ammo = new Label();
            _ammo.AddThemeFontSizeOverride("font_size", 28);
            _ammo.AnchorLeft = 1; _ammo.AnchorRight = 1; _ammo.AnchorTop = 1; _ammo.AnchorBottom = 1;
            _ammo.OffsetLeft = -190; _ammo.OffsetRight = -22; _ammo.OffsetTop = -52; _ammo.OffsetBottom = -22;
            _ammo.HorizontalAlignment = HorizontalAlignment.Right;
            _ammo.MouseFilter = Control.MouseFilterEnum.Ignore;
            root.AddChild(_ammo);
        }

        void AddVital(Control box, int i, string icon, Color color, System.Func<float> val)
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
            foreach (var (fill, val) in _vitals)
                fill.AnchorRight = Mathf.Clamp(val(), 0f, 1f);   // foreground.SizeScale_X = state
            foreach (var (box, on) in _status)
                box.Visible = on();
            if (Player != null)
                _ammo.Text = $"{Player.Ammo} / {(Player.Gun?.AmmoMax ?? Player.Ammo)}";
        }

        static Texture2D LoadTex(string res)
        {
            string p = ProjectSettings.GlobalizePath(res);
            if (System.IO.File.Exists(p)) { var img = Image.LoadFromFile(p); if (img != null) return ImageTexture.CreateFromImage(img); }
            return null;
        }
    }
}
