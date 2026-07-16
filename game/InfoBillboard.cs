using Godot;

namespace UnturnedGodot
{
    // World-space look-at info panel for vehicles + deployables: the object's name, up to three stat BARS
    // (health / fuel / battery), and a prompt line -- replacing the old plain-text Label3D. The bars are the
    // source's SleekProgress look: a dark background box + a solid colour fill scaled by the value (0..1), with
    // the real ripped stat icon beside it (src Palette: health #bf1f1f red, fuel/battery #dcb413 yellow).
    // Rendered by an offscreen SubViewport (only while active) and shown on a billboarded, depth-ignoring Sprite3D.
    public partial class InfoBillboard : Node3D
    {
        public static readonly Color HealthColor = new Color(0xbf / 255f, 0x1f / 255f, 0x1f / 255f);   // Palette.COLOR_R
        public static readonly Color FuelColor = new Color(0xdc / 255f, 0xb4 / 255f, 0x13 / 255f);     // Palette.COLOR_Y
        public static readonly Color LoadColor = new Color(0x35 / 255f, 0xc0 / 255f, 0xe0 / 255f);     // electric cyan: generator power draw
        static readonly Color BgColor = new Color(0f, 0f, 0f, 0.55f);   // SleekProgress background box

        const int VW = 320, VH = 232, BarW = 210, BarH = 22, Pad = 14, IconSz = 26;

        SubViewport _vp;
        Sprite3D _sprite;
        Label _name, _prompt;
        struct Row { public Control Root; public TextureRect Icon; public ColorRect Bg, Fill; }
        Row[] _rows = new Row[3];

        static Texture2D LoadIcon(string file)
        {
            string p = ProjectSettings.GlobalizePath($"res://content/ui/{file}");
            if (!System.IO.File.Exists(p)) return null;
            var img = Image.LoadFromFile(p);
            return img != null ? ImageTexture.CreateFromImage(img) : null;
        }

        public override void _Ready()
        {
            _vp = new SubViewport
            {
                TransparentBg = true, Size = new Vector2I(VW, VH),
                RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,   // only render while active (SetActive)
                RenderTargetClearMode = SubViewport.ClearMode.Always,
            };
            AddChild(_vp);
            var root = new Control { Size = new Vector2(VW, VH) };
            _vp.AddChild(root);

            _name = new Label { Position = new Vector2(0, 4), Size = new Vector2(VW, 34), HorizontalAlignment = HorizontalAlignment.Center };
            _name.AddThemeFontSizeOverride("font_size", 30);
            _name.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.9f));
            _name.AddThemeConstantOverride("outline_size", 6);
            root.AddChild(_name);

            var icons = new[] { LoadIcon("ui_health.png"), LoadIcon("ui_fuel.png"), LoadIcon("ui_stamina.png") };
            for (int i = 0; i < 3; i++)
            {
                var rc = new Control { Position = new Vector2(Pad, 44 + i * (BarH + 10)), Size = new Vector2(VW - Pad * 2, BarH), Visible = false };
                var icon = new TextureRect { Texture = icons[i], Position = new Vector2(0, (BarH - IconSz) / 2f), Size = new Vector2(IconSz, IconSz), StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered };
                var bg = new ColorRect { Color = BgColor, Position = new Vector2(IconSz + 8, 0), Size = new Vector2(BarW, BarH) };
                var fill = new ColorRect { Position = new Vector2(IconSz + 8, 0), Size = new Vector2(BarW, BarH) };
                rc.AddChild(bg); rc.AddChild(fill); rc.AddChild(icon);
                root.AddChild(rc);
                _rows[i] = new Row { Root = rc, Icon = icon, Bg = bg, Fill = fill };
            }

            _prompt = new Label { Position = new Vector2(0, VH - 38), Size = new Vector2(VW, 34), HorizontalAlignment = HorizontalAlignment.Center };
            _prompt.AddThemeFontSizeOverride("font_size", 26);
            _prompt.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.9f));
            _prompt.AddThemeConstantOverride("outline_size", 6);
            root.AddChild(_prompt);

            _sprite = new Sprite3D
            {
                Texture = _vp.GetTexture(), Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                NoDepthTest = true, Shaded = false, PixelSize = 0.0042f, Visible = false,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
            };
            AddChild(_sprite);
        }

        public void SetActive(bool on)
        {
            if (_sprite == null) return;
            _sprite.Visible = on;
            _vp.RenderTargetUpdateMode = on ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
        }

        public void SetName(string text, Color color) { if (_name != null) { _name.Text = text; _name.Modulate = color; } }
        public void SetPrompt(string text, Color color) { if (_prompt != null) { _prompt.Text = text ?? ""; _prompt.Modulate = color; } }

        // index 0=health, 1=fuel, 2=battery. value 0..1. visible=false hides the row.
        public void SetBar(int i, float value, Color color, bool visible = true)
        {
            if (i < 0 || i > 2 || _rows[i].Root == null) return;
            _rows[i].Root.Visible = visible;
            if (!visible) return;
            _rows[i].Fill.Color = color;
            _rows[i].Fill.Size = new Vector2(BarW * Mathf.Clamp(value, 0f, 1f), BarH);
        }
    }
}
