using Godot;

namespace UnturnedGodot
{
    // Full-screen map (press M, Esc/M closes). Shows PEI's real Map.png with the town LOCATION nodes plotted
    // and a rotating arrow for the local player's position + facing.
    //
    // Source transform — PlayerDashboardInformationUI.ProjectWorldPositionToMap (level-size fallback, PEI has
    // no cartography volume):   nx = worldX/levelSize + 0.5 ;  ny = 0.5 - worldZ_unity/levelSize
    // PEI = MEDIUM (Level.size 2048, border 64) -> levelSize = 2048 - 64*2 = 1920.
    // Our world is Godot space (godotZ = -unityZ), so ny = 0.5 + godotZ/1920. The facing arrow uses the same
    // rule as the source (localPlayerImage.RotationAngle = player yaw), computed here from the look forward.
    public partial class MapUI : CanvasLayer
    {
        public PlayerController Player;
        const float LevelSize = 1920f;   // PEI MEDIUM: 2048 - 2*64

        Control _root;
        TextureRect _map;    // Map.png, square + centered
        Polygon2D _arrow;    // local player marker (position + facing)
        Label _coord;
        readonly System.Collections.Generic.List<(Vector2 norm, Control dot, Label lbl)> _towns = new();

        public override void _Ready()
        {
            Layer = 90;   // under the F1 console (100)
            _root = new Control { Visible = false, MouseFilter = Control.MouseFilterEnum.Stop };   // eat clicks so the map doesn't shoot the gun underneath
            _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(_root);

            var dim = new ColorRect { Color = new Color(0f, 0f, 0f, 0.82f), MouseFilter = Control.MouseFilterEnum.Stop };
            dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _root.AddChild(dim);

            _map = new TextureRect { ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = Control.MouseFilterEnum.Ignore };
            var tex = LoadMap();
            if (tex != null) _map.Texture = tex;
            _root.AddChild(_map);

            foreach (var (name, pos) in MapNodes.Locations)
            {
                var dot = new ColorRect { Color = new Color(1f, 0.92f, 0.35f), Size = new Vector2(5, 5), MouseFilter = Control.MouseFilterEnum.Ignore };
                _map.AddChild(dot);
                var lbl = new Label { Text = name, MouseFilter = Control.MouseFilterEnum.Ignore };
                lbl.AddThemeFontSizeOverride("font_size", 11);
                lbl.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f));
                lbl.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f));
                lbl.AddThemeConstantOverride("outline_size", 4);
                _map.AddChild(lbl);
                _towns.Add((WorldToNorm(pos), dot, lbl));
            }

            _arrow = new Polygon2D { Color = new Color(0.25f, 0.9f, 1f) };
            _arrow.Polygon = new Vector2[] { new(0, -11), new(7, 8), new(0, 3), new(-7, 8) };   // points up (north) at rotation 0
            _map.AddChild(_arrow);

            _coord = new Label();
            _coord.AddThemeFontSizeOverride("font_size", 13);
            _coord.AddThemeColorOverride("font_color", new Color(0.82f, 1f, 0.82f));
            _coord.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f));
            _coord.AddThemeConstantOverride("outline_size", 4);
            _root.AddChild(_coord);

            GetViewport().SizeChanged += Layout;
            Layout();
        }

        void Layout()
        {
            var vp = GetViewport().GetVisibleRect().Size;
            float s = Mathf.Min(vp.X, vp.Y) * 0.9f;
            _map.Position = new Vector2((vp.X - s) * 0.5f, (vp.Y - s) * 0.5f);
            _map.Size = new Vector2(s, s);
            foreach (var (norm, dot, lbl) in _towns)
            {
                dot.Position = norm * s - new Vector2(2.5f, 2.5f);
                lbl.Position = norm * s + new Vector2(5f, -7f);
            }
            _coord.Position = new Vector2(_map.Position.X, _map.Position.Y - 24f);
        }

        public override void _Process(double delta)
        {
            if (!_root.Visible || Player == null) return;
            var pos = Player.GlobalPosition;
            _arrow.Position = WorldToNorm(pos) * _map.Size;
            _arrow.Rotation = Player.MapFacingAngle();
            _coord.Text = $"PEI    X {pos.X:0}  Z {pos.Z:0}    (M / Esc to close)";
        }

        public override void _Input(InputEvent e)
        {
            if (e is not InputEventKey { Pressed: true } k) return;
            if (k.Keycode == Key.M)
            {
                if (GetViewport().GuiGetFocusOwner() is LineEdit) return;   // don't hijack typing in the F1 console
                Toggle();
                GetViewport().SetInputAsHandled();
            }
            else if (_root.Visible && k.Keycode == Key.Escape) { Close(); GetViewport().SetInputAsHandled(); }
        }

        void Toggle() { if (_root.Visible) Close(); else Open(); }
        void Open() { _root.Visible = true; Layout(); Input.MouseMode = Input.MouseModeEnum.Visible; }
        void Close() { _root.Visible = false; Input.MouseMode = Input.MouseModeEnum.Captured; }

        static Vector2 WorldToNorm(Vector3 p) => new Vector2(p.X / LevelSize + 0.5f, 0.5f + p.Z / LevelSize);

        static Texture2D LoadMap()
        {
            string p = ProjectSettings.GlobalizePath("res://content/pei_map.png");
            if (!System.IO.File.Exists(p)) { GD.Print("[map] missing content/pei_map.png"); return null; }
            var img = Image.LoadFromFile(p);
            return img == null ? null : ImageTexture.CreateFromImage(img);
        }
    }
}
