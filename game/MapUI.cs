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
        // MAP-AWARE (was PEI-hardcoded): Main sets MapFolder when it resolves the map, and the image / level-size /
        // label all follow. levelSize = ELevelSize SIZE - 2*BORDER (source Level.cs). BOTH shipped maps are MEDIUM
        // (2048-128=1920): PEI, and Washington -- Washington has a 4096 LANDSCAPE (16 tiles) but its playable/map
        // level is MEDIUM, confirmed by aligning the town nodes to Map.png (a LARGE 3968 scaled them 2x too small).
        // The town dots come from MapNodes, which is already map-aware.
        public static string MapFolder = "PEI";
        static (string img, float size, string label) Info() => MapFolder switch
        {
            "Washington" => ("washington_map.png", 1920f, "Washington"),   // MEDIUM level (2048-2*64) despite a 4096 landscape -- verified by aligning the town nodes to the Map.png
            "Yukon"      => ("yukon_map.png",      1920f, "Yukon"),         // MEDIUM level -- town nodes span ~+-830 (Mount Logan..Off Limits), fits 1920; verify M-map alignment in-render
            _            => ("pei_map.png",        1920f, "PEI"),
        };

        Control _root;
        TextureRect _map;    // Map.png, square + centered
        Polygon2D _arrow;    // local player marker (position + facing)
        Label _coord;
        readonly System.Collections.Generic.List<(Vector2 norm, Control dot, Label lbl)> _towns = new();

        public override void _Ready()
        {
            TickHub.AddProcess(this, HubProcess); SetProcess(false);   // PERF: hub-ticked (see TickHub.AddProcess)
            Layer = 90;   // under the F1 console (100)
            _root = new Control { Visible = false, MouseFilter = Control.MouseFilterEnum.Stop };   // eat clicks so the map doesn't shoot the gun underneath
            _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(_root);

            var dim = new ColorRect { MouseFilter = Control.MouseFilterEnum.Stop };   // frosted-glass backdrop, same as the other menu screens
            dim.Material = new ShaderMaterial { Shader = new Shader { Code = InventoryUI.BACKDROP_BLUR } };
            dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _root.AddChild(dim);
            Current = this;

            _map = new TextureRect { ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = Control.MouseFilterEnum.Ignore };
            var tex = LoadMap();
            if (tex != null) _map.Texture = tex;
            _root.AddChild(_map);

            foreach (var (name, pos) in MapNodes.Locations)
            {
                var dot = new ColorRect { Color = UITheme.Accent, Size = new Vector2(5, 5), MouseFilter = Control.MouseFilterEnum.Ignore };
                _map.AddChild(dot);
                var lbl = new Label { Text = name, MouseFilter = Control.MouseFilterEnum.Ignore };
                lbl.AddThemeFontSizeOverride("font_size", UITheme.FontSmall);
                lbl.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f));
                lbl.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f));
                lbl.AddThemeConstantOverride("outline_size", 4);
                _map.AddChild(lbl);
                _towns.Add((WorldToNorm(pos), dot, lbl));
            }

            _arrow = new Polygon2D { Color = new Color(0.25f, 0.9f, 1f) };
            _arrow.Polygon = new Vector2[] { new(0, -11), new(7, 8), new(0, 3), new(-7, 8) };   // points up (north) at rotation 0
            _map.AddChild(_arrow);

            _navbar = MenuNavbar.Build(_root, MenuNavbar.Tab.Information, t => Player?.ShowMenu(t), () => Close());   // the Information tab of the unified menu hosts the map
            _coord = new Label();
            _coord.AddThemeFontSizeOverride("font_size", UITheme.FontBody);
            _coord.AddThemeColorOverride("font_color", new Color(0.82f, 1f, 0.82f));
            _coord.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f));
            _coord.AddThemeConstantOverride("outline_size", 4);
            _root.AddChild(_coord);

            GetViewport().SizeChanged += Layout;
            Layout();

            if (System.Environment.GetEnvironmentVariable("UG_MAPOPEN") == "1")   // debug: open the M-map at start + log node projections so a render can verify alignment
            {
                _root.Visible = true;
                GD.Print($"[mapdbg] folder={MapFolder} levelSize={Info().size} nodes={MapNodes.Locations.Count}");
                foreach (var (nm, pos) in MapNodes.Locations)
                {
                    var n = WorldToNorm(pos);
                    GD.Print($"[mapnode] {nm} world=({pos.X:0},{pos.Z:0}) norm=({n.X:0.000},{n.Y:0.000})");
                }
            }
        }

        void Layout()
        {
            var vp = GetViewport().GetVisibleRect().Size;
            float top = MenuNavbar.Height + 36f;   // under the shared navbar + the coord line
            float s = Mathf.Min(vp.X * 0.9f, vp.Y - top - 24f);
            _map.Position = new Vector2((vp.X - s) * 0.5f, top);
            _map.Size = new Vector2(s, s);
            foreach (var (norm, dot, lbl) in _towns)
            {
                dot.Position = norm * s - new Vector2(2.5f, 2.5f);
                lbl.Position = norm * s + new Vector2(5f, -7f);
            }
            _coord.Position = new Vector2(_map.Position.X, _map.Position.Y - 24f);
        }

        public override void _Process(double delta) => HubProcess(delta);   // forwarder for direct callers; the engine's callback is off (SetProcess(false) in _Ready) -- TickHub ticks HubProcess
        public void HubProcess(double delta)
        {
            if (!_root.Visible || Player == null) return;
            var pos = Player.GlobalPosition;
            _arrow.Position = WorldToNorm(pos) * _map.Size;
            _arrow.Rotation = Player.MapFacingAngle();
            _coord.Text = $"{Info().label}    X {pos.X:0}  Z {pos.Z:0}    ({Keybinds.Get(GameAction.Map).Label} / Esc to close)";
        }

        public override void _Input(InputEvent e)
        {
            // The M key is routed by PlayerController.ShowMenu (unified menu) so opening the map closes the other tabs; Esc closes here.
            if (e is InputEventKey { Pressed: true, Keycode: Key.Escape } && _root.Visible) { Close(); GetViewport().SetInputAsHandled(); }
        }

        public static MapUI Current;   // the live map screen (one per world); PlayerController routes the Information tab here
        MenuNavbar _navbar;
        public bool IsOpen => _root != null && _root.Visible;
        public void Toggle() { if (_root.Visible) Close(); else Open(); }
        public void Open() { _root.Visible = true; Layout(); _navbar?.SetActive(MenuNavbar.Tab.Information); Input.MouseMode = Input.MouseModeEnum.Visible; }
        public void Close(bool captureMouse = true) { if (_root == null) return; _root.Visible = false; if (captureMouse) Input.MouseMode = Input.MouseModeEnum.Captured; }
        public override void _ExitTree() { if (Current == this) Current = null; }

        static Vector2 WorldToNorm(Vector3 p) { float ls = Info().size; return new Vector2(p.X / ls + 0.5f, 0.5f + p.Z / ls); }

        static Texture2D LoadMap()
        {
            string p = ProjectSettings.GlobalizePath("res://content/" + Info().img);
            if (!System.IO.File.Exists(p)) { GD.Print($"[map] missing content/{Info().img}"); return null; }
            var img = Image.LoadFromFile(p);
            return img == null ? null : ImageTexture.CreateFromImage(img);
        }
    }
}
