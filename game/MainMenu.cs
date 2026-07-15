using Godot;

namespace UnturnedGodot
{
    // The real Unturned main menu, ported from the release source (SmartlyDressedGames/U3-SDK:
    // Unturned/Menu/MenuUI.cs + Unturned/UI/Menu/MenuDashboardUI.cs). It's ONE 3D scene -- a barn on a
    // grassy field -- with a Camera that lerps between five named anchors (Title / Play / Survivors /
    // Configuration / Workshop) as you move through the menu (MenuUI.Update: targetCameraTransform picked
    // by which page is open, then Lerp'd at deltaTime*4; the very first approach to Title is a slow
    // cinematic pan). The dashboard is a left-hand column of icon buttons: Play, Survivors, Configuration,
    // Workshop (top) + Exit (bottom), each 200x50 (MenuDashboardUI ctor).
    //
    // What we actually have wired: PLAY -> our real PEI world (OnDrivePEI). Survivors/Configuration/Workshop
    // are stubs for now (they glide the camera to their anchor + show a "coming to Cow.0" placeholder). Exit
    // quits. OnPlay (legacy flat-terrain survival) is kept for the --flag test harnesses.
    public partial class MainMenu : Node3D
    {
        public System.Action<bool> OnPlay;        // legacy flat-terrain survival build (test flags only)
        public System.Action<bool> OnDrivePEI;    // bool = noZombies -- the real PEI world; the dashboard's Play opens this

        // --- camera anchors (framings of the barn). Tuned against the render; index 0 = Title (idle). ---
        // pos + look-at, world space. Title is a pulled-back 3/4 hero shot; each tab reframes the barn.
        static readonly (Vector3 pos, Vector3 look)[] Anchors =
        {
            (new Vector3( 19f,  9f,  27f), new Vector3(0f, 5f, 0f)),   // 0 Title  -- wide 3/4 hero
            (new Vector3(-19f,  7f,  23f), new Vector3(0f, 6f, 0f)),   // 1 Play   -- swing left
            (new Vector3( 21f,  6f,  19f), new Vector3(0f, 5f, 0f)),   // 2 Survivors -- swing right along the long wall
            (new Vector3( 11f, 17f,  24f), new Vector3(0f, 3f, 0f)),   // 3 Configuration -- high overview
            (new Vector3( -3f,  2.5f,25f), new Vector3(0f, 9f, 0f)),   // 4 Workshop -- low hero, looking up at the whole barn
        };

        Camera3D _cam;
        int _targetTab;              // which anchor the camera is gliding toward (0 = title)
        bool _reachedTitle;          // MenuUI.hasReachedTitleCameraTransform: first pan to Title is slow, then snappy
        Control _stubPanel;          // the "coming to Cow.0" placeholder for unimplemented tabs
        Control _playPanel;          // Play submenu: PEI / PEI no-zombies (our real modes)

        static string G(string res) => ProjectSettings.GlobalizePath(res);

        public override void _Ready()
        {
            BuildWorld();
            BuildUI();
            // start the camera pulled further back + higher than Title and slow-pan in (the vanilla intro)
            var t = Anchors[0];
            _cam.Position = t.pos + new Vector3(6f, 4f, 9f);
            _cam.LookAt(t.look, Vector3.Up);
        }

        // ---------------------------------------------------------------- world (barn + ground + sky + sun)
        void BuildWorld()
        {
            // sky + ambient: a bright pastoral day, sun low-ish for long soft shadows
            var sky = new ProceduralSkyMaterial
            {
                SkyTopColor = new Color(0.38f, 0.60f, 0.86f),
                SkyHorizonColor = new Color(0.72f, 0.80f, 0.86f),
                GroundHorizonColor = new Color(0.72f, 0.80f, 0.86f),
                GroundBottomColor = new Color(0.44f, 0.47f, 0.40f),
                SunAngleMax = 30f, SunCurve = 0.12f,
            };
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Sky,
                Sky = new Sky { SkyMaterial = sky },
                AmbientLightSource = Godot.Environment.AmbientSource.Sky,
                AmbientLightSkyContribution = 1f,
                TonemapMode = Godot.Environment.ToneMapper.Filmic,
                SsaoEnabled = true,
            };
            env.SetFogEnabled(true);
            env.FogDensity = 0.0012f;
            env.FogLightColor = new Color(0.72f, 0.80f, 0.86f);
            AddChild(new WorldEnvironment { Environment = env });

            var sun = new DirectionalLight3D
            {
                RotationDegrees = new Vector3(-42f, 138f, 0f),
                LightColor = new Color(1f, 0.96f, 0.87f),
                LightEnergy = 1.25f,
                ShadowEnabled = true,
            };
            AddChild(sun);

            // grassy ground
            var ground = new MeshInstance3D
            {
                Mesh = new PlaneMesh { Size = new Vector2(600f, 600f) },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.35f, 0.44f, 0.26f), Roughness = 1f },
            };
            AddChild(ground);

            // the hero barn -- real ripped Barn_0 (content/objects), flat 4x2 palette texture, nearest filter
            var mesh = ObjMesh.Load(G("res://content/objects/Barn_0.obj"));
            if (mesh != null)
            {
                var mat = new StandardMaterial3D { Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
                string tp = G("res://content/objects/Barn_0_tex.png");
                if (System.IO.File.Exists(tp))
                {
                    var img = new Image();
                    if (img.Load(tp) == Error.Ok)
                    {
                        mat.AlbedoTexture = ImageTexture.CreateFromImage(img);
                        mat.TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest;   // 4x2 palette: keep cells crisp
                    }
                }
                var barn = new MeshInstance3D { Mesh = mesh, MaterialOverride = mat };
                // Barn_0 is authored lying on its back (long axis Y). Stand it up, then sit its base on the
                // ground (min.Y=0) and centre its footprint over the origin, computed from the rotated AABB.
                float rx = ParseF(System.Environment.GetEnvironmentVariable("UG_BARNROT"), -90f);   // debug knob; -90 stands it up
                barn.RotationDegrees = new Vector3(rx, 0f, 0f);
                var ab = mesh.GetAabb();
                var b = barn.Basis;
                Vector3 mn = new Vector3(1e9f, 1e9f, 1e9f), mx = -mn;
                for (int i = 0; i < 8; i++)
                {
                    var c = ab.Position + ab.Size * new Vector3(i & 1, (i >> 1) & 1, (i >> 2) & 1);
                    var w = b * c;
                    mn = new Vector3(Mathf.Min(mn.X, w.X), Mathf.Min(mn.Y, w.Y), Mathf.Min(mn.Z, w.Z));
                    mx = new Vector3(Mathf.Max(mx.X, w.X), Mathf.Max(mx.Y, w.Y), Mathf.Max(mx.Z, w.Z));
                }
                barn.Position = new Vector3(-(mn.X + mx.X) * 0.5f, -mn.Y, -(mn.Z + mx.Z) * 0.5f);
                AddChild(barn);
            }
            else GD.PrintErr("[menu] Barn_0.obj failed to load");

            _cam = new Camera3D { Current = true, Fov = 60f };
            AddChild(_cam);
        }

        static float ParseF(string s, float def) => float.TryParse(s, out var v) ? v : def;

        // ---------------------------------------------------------------- UI (dashboard: title + button column)
        void BuildUI()
        {
            var layer = new CanvasLayer { Layer = 50 };
            AddChild(layer);

            // title wordmark, top-left (vanilla shows the Unturned logo here)
            var title = new Label { Text = "UNTURNED", Position = new Vector2(22f, 40f) };
            title.AddThemeFontSizeOverride("font_size", 60);
            title.AddThemeColorOverride("font_color", new Color(0.95f, 0.94f, 0.90f));
            title.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.6f));
            title.AddThemeConstantOverride("shadow_offset_x", 2);
            title.AddThemeConstantOverride("shadow_offset_y", 2);
            layer.AddChild(title);
            var tag = new Label { Text = "cow.0", Position = new Vector2(26f, 108f) };
            tag.AddThemeFontSizeOverride("font_size", 22);
            tag.AddThemeColorOverride("font_color", new Color(0.85f, 0.78f, 0.55f));
            layer.AddChild(tag);

            // the five dashboard buttons (positions from MenuDashboardUI ctor: Play 170, Survivors 230,
            // Configuration 290, Workshop 350; Exit anchored to the bottom). Hover glides the camera to that
            // tab's anchor; click runs the action.
            MenuButton(layer, "play",          "Play",          170f, false, 1, () => TogglePlayPanel());
            MenuButton(layer, "survivors",     "Survivors",     230f, false, 2, () => ShowStub("Survivors"));
            MenuButton(layer, "configuration", "Configuration", 290f, false, 3, () => ShowStub("Configuration"));
            MenuButton(layer, "workshop",      "Workshop",      350f, false, 4, () => ShowStub("Workshop"));
            MenuButton(layer, "exit",          "Exit",          -70f, true,  0, () => GetTree().Quit());

            BuildPlayPanel(layer);
            BuildStubPanel(layer);
        }

        void MenuButton(CanvasLayer layer, string icon, string text, float y, bool fromBottom, int tab, System.Action onClick)
        {
            var b = new Button
            {
                Text = "  " + text,
                Position = new Vector2(22f, fromBottom ? y : y),
                Size = new Vector2(200f, 50f),
                Alignment = HorizontalAlignment.Left,
                ExpandIcon = false,
            };
            if (fromBottom)
            {
                b.SetAnchor(Side.Top, 1f); b.SetAnchor(Side.Bottom, 1f);
                b.Position = new Vector2(22f, y);
            }
            string ip = G($"res://content/menu/icon_{icon}.png");
            if (System.IO.File.Exists(ip))
            {
                var img = new Image();
                if (img.Load(ip) == Error.Ok) b.Icon = ImageTexture.CreateFromImage(img);
            }
            b.AddThemeFontSizeOverride("font_size", 20);
            b.MouseEntered += () => _targetTab = tab;      // camera glides to this tab's framing while hovered
            b.MouseExited += () => { if (_playPanel == null || !_playPanel.Visible) _targetTab = 0; };
            b.Pressed += () => onClick();
            layer.AddChild(b);
        }

        // Play submenu -- vanilla Play opens Singleplayer; ours offers the two PEI modes we actually ship.
        void BuildPlayPanel(CanvasLayer layer)
        {
            _playPanel = new PanelContainer { Position = new Vector2(240f, 170f), Visible = false };
            var box = new VBoxContainer();
            box.AddThemeConstantOverride("separation", 10);
            ((PanelContainer)_playPanel).AddChild(box);
            var head = new Label { Text = "SINGLEPLAYER" };
            head.AddThemeFontSizeOverride("font_size", 22);
            box.AddChild(head);
            box.AddChild(SubButton("Prince Edward Island", () => OnDrivePEI?.Invoke(false)));
            box.AddChild(SubButton("Prince Edward Island — No Zombies", () => OnDrivePEI?.Invoke(true)));
            layer.AddChild(_playPanel);
        }

        Button SubButton(string text, System.Action onClick)
        {
            var b = new Button { Text = text, CustomMinimumSize = new Vector2(320f, 46f), Alignment = HorizontalAlignment.Left };
            b.AddThemeFontSizeOverride("font_size", 18);
            b.Pressed += () => onClick();
            return b;
        }

        void TogglePlayPanel()
        {
            bool show = !_playPanel.Visible;
            _playPanel.Visible = show;
            if (_stubPanel != null) _stubPanel.Visible = false;
            _targetTab = 1;   // hold the Play framing while the panel is open
        }

        void BuildStubPanel(CanvasLayer layer)
        {
            _stubPanel = new PanelContainer { Position = new Vector2(240f, 200f), Visible = false };
            var box = new VBoxContainer();
            box.AddThemeConstantOverride("separation", 8);
            ((PanelContainer)_stubPanel).AddChild(box);
            var l = new Label { Name = "head" };
            l.AddThemeFontSizeOverride("font_size", 22);
            box.AddChild(l);
            var sub = new Label { Text = "not implemented yet — coming to Cow.0", Name = "sub" };
            sub.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.8f));
            box.AddChild(sub);
            layer.AddChild(_stubPanel);
        }

        void ShowStub(string name)
        {
            _playPanel.Visible = false;
            _stubPanel.Visible = true;
            ((Label)_stubPanel.GetNode("VBoxContainer/head")).Text = name.ToUpper();
        }

        // ---------------------------------------------------------------- camera glide (MenuUI.Update port)
        public override void _Process(double delta)
        {
            if (_cam == null) return;
            var t = Anchors[_targetTab];
            var target = new Transform3D(Basis.LookingAt(t.look - t.pos, Vector3.Up), t.pos);
            float d = (float)delta;
            if (_targetTab == 0)
            {
                float w = _reachedTitle ? d * 4f : d * 1f;   // first approach to Title is the slow cinematic pan
                _cam.Position = _cam.Position.Lerp(target.Origin, w);
                _cam.Quaternion = _cam.Quaternion.Slerp(target.Basis.GetRotationQuaternion(), w);
                if (_cam.Position.DistanceTo(target.Origin) < 0.4f) _reachedTitle = true;
            }
            else
            {
                _reachedTitle = true;
                _cam.Position = _cam.Position.Lerp(target.Origin, d * 4f);
                _cam.Quaternion = _cam.Quaternion.Slerp(target.Basis.GetRotationQuaternion(), d * 4f);
            }
        }

        // harness hook: jump the camera target to a tab (used by --menushot to capture each framing)
        public void ShowTab(int tab) { _targetTab = Mathf.Clamp(tab, 0, Anchors.Length - 1); if (tab != 0) _reachedTitle = true; }
    }
}
