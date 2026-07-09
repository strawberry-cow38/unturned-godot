using Godot;

namespace UnturnedGodot
{
    // Standard Unturned in-game HUD (PlayerLifeUI): the vitals in the bottom-left — each a 20x20 icon + a colored
    // progress bar (health red / food orange / water blue / stamina yellow), stacked up from the bottom — plus the
    // ammo count. Icons are the real ui/player/icons/playerlife/*.png from core.masterbundle; the layout (icon 20 at
    // x=5, bar at x=30 filling the width) + the Palette colours match the source. No menus, just the HUD.
    public partial class HUD : CanvasLayer
    {
        public PlayerController Player;

        // Palette.cs colours: COLOR_R (health), COLOR_O (food), COLOR_B (water), COLOR_Y (stamina).
        static readonly Color CR = new Color(0.7490196f, 0.12156863f, 0.12156863f);
        static readonly Color CO = new Color(57f / 85f, 0.5019608f, 5f / 51f);
        static readonly Color CB = new Color(10f / 51f, 0.59607846f, 40f / 51f);
        static readonly Color CY = new Color(44f / 51f, 0.7058824f, 0.07450981f);

        const float BarW = 220f, BarH = 10f, IconSz = 20f, LeftX = 16f, BarX = 44f, BaseUp = 30f, RowH = 26f;

        readonly System.Collections.Generic.List<(ColorRect fill, System.Func<float> val)> _vitals = new();
        Label _ammo, _kills;

        public override void _Ready()
        {
            Layer = 10;   // draw the HUD OVER the viewmodel composite (CanvasLayer 5) — the ammo sits over the gun

            var root = new Control();
            root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            root.MouseFilter = Control.MouseFilterEnum.Ignore;
            AddChild(root);

            // vitals: health (bottom) -> food -> water -> stamina, stacked up from the bottom-left
            AddVital(root, 0, "hud_health.png",  CR, () => Player != null ? Player.Health / Mathf.Max(1f, Player.MaxHealth) : 1f);
            AddVital(root, 1, "hud_food.png",    CO, () => 0.82f);   // food/water/stamina aren't simulated yet -> display placeholders
            AddVital(root, 2, "hud_water.png",   CB, () => 0.68f);
            AddVital(root, 3, "hud_stamina.png", CY, () => 0.95f);

            // ammo count, bottom-right
            _ammo = new Label();
            _ammo.AddThemeFontSizeOverride("font_size", 28);
            _ammo.AnchorLeft = 1; _ammo.AnchorRight = 1; _ammo.AnchorTop = 1; _ammo.AnchorBottom = 1;
            _ammo.OffsetLeft = -190; _ammo.OffsetRight = -22; _ammo.OffsetTop = -52; _ammo.OffsetBottom = -22;
            _ammo.HorizontalAlignment = HorizontalAlignment.Right;
            _ammo.MouseFilter = Control.MouseFilterEnum.Ignore;
            root.AddChild(_ammo);

            // small kills/deaths readout top-left (demo aid, not part of the vanilla HUD)
            _kills = new Label();
            _kills.AddThemeFontSizeOverride("font_size", 15);
            _kills.Position = new Vector2(16, 12);
            _kills.Modulate = new Color(1, 1, 1, 0.55f);
            _kills.MouseFilter = Control.MouseFilterEnum.Ignore;
            root.AddChild(_kills);
        }

        void AddVital(Control root, int i, string icon, Color color, System.Func<float> val)
        {
            float up = BaseUp + i * RowH;
            var ic = new TextureRect { Texture = LoadTex($"res://content/{icon}"), StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered };
            Anchor(ic, LeftX, up, IconSz, IconSz);
            root.AddChild(ic);
            // bar background (dark) + coloured fill, vertically centred on the icon
            var bg = new ColorRect { Color = new Color(0f, 0f, 0f, 0.5f) };
            Anchor(bg, BarX, up + (IconSz - BarH) / 2f, BarW, BarH);
            root.AddChild(bg);
            var fill = new ColorRect { Color = color };
            Anchor(fill, BarX, up + (IconSz - BarH) / 2f, BarW, BarH);
            root.AddChild(fill);
            _vitals.Add((fill, val));
        }

        // anchor a control to the screen's bottom-left: `up` px above the bottom edge, at x, sized w x h
        static void Anchor(Control c, float x, float up, float w, float h)
        {
            c.AnchorLeft = 0; c.AnchorRight = 0; c.AnchorTop = 1; c.AnchorBottom = 1;
            c.OffsetLeft = x; c.OffsetRight = x + w;
            c.OffsetBottom = -up; c.OffsetTop = -up - h;
            c.MouseFilter = Control.MouseFilterEnum.Ignore;
        }

        public override void _Process(double delta)
        {
            foreach (var (fill, val) in _vitals)
            {
                float s = Mathf.Clamp(val(), 0f, 1f);
                fill.OffsetRight = fill.OffsetLeft + BarW * s;   // fill grows left->right by the vital's fraction
            }
            if (Player != null)
            {
                _ammo.Text = $"{Player.Ammo} / {(Player.Gun?.AmmoMax ?? Player.Ammo)}";
                _kills.Text = $"kills {Player.Kills}   deaths {Player.Deaths}";
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
