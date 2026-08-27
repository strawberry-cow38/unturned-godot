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
        public System.Action OnPlay;              // legacy flat-terrain survival build (test flags only)
        public System.Action OnDrivePEI;          // the real PEI world; the dashboard's Play opens this
        public System.Action OnMultiplayer;       // legacy top-level hard-connect to the MP test server (kept as a fallback)
        public System.Action<string, ushort> OnJoinServer;   // server browser JOIN / direct-connect: real client join to host:port
        public System.Action OnEditor;            // Workshop -> the singleplayer map editor (PEI)
        public System.Action OnPlayground;        // Playground -> the gun range: dummies at marked distances, floating damage numbers
        // Workshop -> a custom map by NAME. The same entry point does new-and-load: the sub-editors read
        // `editor_<name>_*` when they start, so "create" and "open" differ only in whether those files exist.
        // Two entry points would be two ways to start a map, and they drift.
        public System.Action<string> OnOpenMap;
        public System.Action<string> OnPlayMap;   // Workshop -> open a custom map and drop straight into play

        // --- camera anchors (framings of the barn). Tuned against the render; index 0 = Title (idle). ---
        // pos + look-at, world space. Title is a pulled-back 3/4 hero shot; each tab reframes the barn.
        // The whole menu takes place INSIDE the barn (interior ~ X[-7.5,7.5] Z[-10.5,10.5] Y[0,15.9],
        // gable ends at Z=+-10.5). Camera sits near the back gable and looks down the length toward the
        // far gable; each tab reframes that interior. Barn material is CullMode.Disabled so the walls
        // read from the inside.
        static readonly (Vector3 pos, Vector3 look)[] Anchors =
        {
            // This barn only reads from above (door + brown floor + framing posts show on a down-angle;
            // at standing height it's a green-ground red box) AND has support columns at x~+-1.5..2 that
            // the centred camera threads between. So every anchor stays in the centre aisle (|x|<1) and
            // above the y~5-7 loft/beam band -- variety comes from dolly (z), height, and pitch, not from
            // swinging sideways (that clips a column).
            (new Vector3( 0f,   8.5f,  8f),   new Vector3( 0f,   1.2f, -8f)),   // 0 Title  -- mid-distance elevated door hero (idle default)
            (new Vector3( 0.9f, 8f,    6.5f), new Vector3( 0f,   1.2f, -8.5f)), // 1 Play   -- pushed in, door bigger
            (new Vector3(-0.9f, 8.2f,  9.3f), new Vector3( 0f,   1.4f, -8f)),   // 2 Survivors -- pulled back, wider
            (new Vector3( 0f,   9.5f,  5f),   new Vector3( 0f,   0.2f, -8f)),   // 3 Configuration -- highest, steep top-down
            (new Vector3( 0f,   7.8f,  7.5f), new Vector3( 0.3f, 2.2f, -9f)),   // 4 Workshop -- flatter pitch, door + far wall/loft
        };

        Camera3D _cam;
        int _targetTab;              // which anchor the camera is gliding toward (0 = title)
        bool _reachedTitle;          // MenuUI.hasReachedTitleCameraTransform: first pan to Title is slow, then snappy
        Control _stubPanel;          // the "coming to Cow.0" placeholder for unimplemented tabs
        Control _playPanel;          // Play submenu: PEI / PEI no-zombies (our real modes)
        Control _workshopPanel;      // Workshop submenu: Editor (PEI)
        Control _serversPanel;       // Multiplayer submenu: the server browser (MainMenuServers.cs)

        // --- UG_MENUREAL: build the REAL extracted Menu_Base diorama (trees/barn/off-roader/props from the
        //     release scene) instead of the single placeholder barn. Five framings so a --menushot sweep gives
        //     five angles to pick the hero shot from. UG_MENUEYE/UG_MENULOOK ("x,y,z") override view 0. ---
        bool _menuReal;
        // The REAL retail menu camera anchors, extracted from the harness scene Menu.unity's named Transforms
        // (Title/Play/Survivors/Configuration/Workshop -> MenuUI.cs targetCameraTransform) into this diorama's
        // world space (F*M). They're INTERIOR framings -- the camera lives in the barn loft. MenuUI lerps between
        // them (deltaTime*4; the first approach to Title is a slow deltaTime*1 cinematic pan).
        static readonly (Vector3 pos, Vector3 look)[] RealViews =
        {
            (new Vector3(0.00f, 5.49f, -5.99f), new Vector3(-0.08f, 5.27f, -5.02f)),   // 0 Title (idle)
            (new Vector3(0.53f, 2.18f,  5.10f), new Vector3( 0.40f, 2.18f,  6.09f)),   // 1 Play
            (new Vector3(-2.00f, 1.74f, 1.14f), new Vector3(-2.91f, 1.56f,  1.52f)),   // 2 Survivors
            (new Vector3(3.37f, 2.36f,  2.85f), new Vector3( 3.91f, 1.97f,  3.60f)),   // 3 Configuration
            (new Vector3(0.22f, 2.90f, -1.36f), new Vector3( 1.12f, 2.67f, -1.75f)),   // 4 Workshop
        };
        static Vector3 ParseV3(string env, Vector3 def)
        {
            var s = System.Environment.GetEnvironmentVariable(env);
            if (string.IsNullOrEmpty(s)) return def;
            var p = s.Split(',');
            return (p.Length == 3 && float.TryParse(p[0], out var x) && float.TryParse(p[1], out var y) && float.TryParse(p[2], out var z))
                ? new Vector3(x, y, z) : def;
        }

        static string G(string res) => ProjectSettings.GlobalizePath(res);

        public override void _Ready()
        {
            BuildWorld();
            BuildUI();
            // --menushot / debug: open the Play submenu at load so a render captures it (UG_MENUOPEN=map|options|advanced)
            var _open = System.Environment.GetEnvironmentVariable("UG_MENUOPEN");
            if (_open == "map" || _open == "play" || _open == "options" || _open == "advanced") TogglePlayPanel();
            if (_open == "servers") ToggleServersPanel();
            if (_open == "advanced") ToggleAdvanced();
            if (_menuReal)   // extracted diorama: start settled on Title, then glide to hovered anchors (MenuUI.Update port)
            {
                _targetTab = 0; _reachedTitle = true;
                _cam.Position = ParseV3("UG_MENUEYE", RealViews[0].pos);
                _cam.LookAt(ParseV3("UG_MENULOOK", RealViews[0].look), Vector3.Up);
                return;
            }
            // start the camera pulled back toward the near gable + a touch higher, then slow-pan in
            // (the vanilla intro) -- kept inside the barn so it doesn't clip through the back wall
            var t = Anchors[0];
            _cam.Position = t.pos + new Vector3(1.5f, 1.5f, 1.8f);
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
                // flat ambient (not sky IBL): the roof occludes the sky so downward-facing interior faces
                // -- the rafters + ceiling -- would go pure black. A uniform fill lifts them off zero.
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.52f, 0.53f, 0.55f),
                AmbientLightEnergy = 1.35f,
                TonemapMode = Godot.Environment.ToneMapper.Filmic,
                SsaoEnabled = true,
            };
            // Fog gives the placeholder-barn exterior a pastoral haze, but the REAL menu (Menu_NoHoliday
            // RenderSettings m_Fog: 0) has NO fog -- inside the barn it reads as a glowing haze over everything.
            if (System.Environment.GetEnvironmentVariable("UG_MENUREAL") != "1")
            {
                env.SetFogEnabled(true);
                env.FogDensity = 0.0012f;
                env.FogLightColor = new Color(0.72f, 0.80f, 0.86f);
            }
            // "reverse-AO" haze fix (diffed against the working gameplay Environment, WorldBuilder ~1825):
            // that one runs NO SSAO and ambient @1.0; mine ran SSAO + 0.52@1.35. The real difference is the
            // ENCLOSED interior -- flat color-ambient lights every barn surface full-strength with nothing to
            // occlude it, so the upper walls wash toward the sky colour (tinyclaw's read). Drop the ambient for
            // the interior and drop SSAO (the double-sided menu meshes only confuse it).
            if (System.Environment.GetEnvironmentVariable("UG_MENUREAL") == "1")
            {
                env.AmbientLightColor = new Color(0.28f, 0.30f, 0.33f);
                env.AmbientLightEnergy = 1.0f;
                env.SsaoEnabled = false;
            }
            AddChild(new WorldEnvironment { Environment = env });

            var sun = new DirectionalLight3D
            {
                RotationDegrees = new Vector3(-42f, 138f, 0f),
                LightColor = new Color(1f, 0.96f, 0.87f),
                LightEnergy = 1.25f,
                ShadowEnabled = true,
            };
            AddChild(sun);

            // interior fill: the roof occludes the sky, so the inside would be a black box. A warm omni
            // hung near the ridge fakes the "light coming through the barn" glow so the space reads.
            AddChild(new OmniLight3D
            {
                Position = new Vector3(0f, 8.5f, 0f),
                LightColor = new Color(1f, 0.90f, 0.74f),
                LightEnergy = 3.0f,
                OmniRange = 34f,
                OmniAttenuation = 0.6f,
            });

            // grassy ground
            var ground = new MeshInstance3D
            {
                Mesh = new PlaneMesh { Size = new Vector2(600f, 600f) },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.35f, 0.44f, 0.26f), Roughness = 1f },
            };
            AddChild(ground);

            // UG_MENUREAL: assemble the real extracted Menu_Base diorama instead of the single placeholder barn.
            _menuReal = System.Environment.GetEnvironmentVariable("UG_MENUREAL") == "1";
            if (_menuReal) { LoadMenuScene(); }
            else {
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
            }

            _cam = new Camera3D { Current = true, Fov = 60f };
            AddChild(_cam);
        }

        // The real main-menu diorama, extracted from the release scene Assets/Game/Sources/MainMenu/Menu_Base.unity.
        // Pipeline (scratchpad tools): parse the Unity scene -> per-object world transform; decode each referenced
        // Mesh .asset -> a raw-Unity .obj under content/menu/mesh. Each placement's transform already carries the
        // Unity->Godot Z-flip (world = F*M_unity), so the meshes load RAW (ObjMesh CONV=1) exactly like every
        // other content prop and ObjMesh's winding-reverse lands the faces outward.
        void LoadMenuScene()
        {
            string jf = G("res://content/menu/menu_scene.json");
            if (!System.IO.File.Exists(jf)) { GD.PrintErr("[menu] menu_scene.json missing"); return; }
            var arr = Json.ParseString(System.IO.File.ReadAllText(jf)).AsGodotArray();
            var gray = new StandardMaterial3D { AlbedoColor = new Color(0.62f, 0.62f, 0.60f), Roughness = 1f,
                                                CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            bool noTrees = System.Environment.GetEnvironmentVariable("UG_MENUNOTREES") == "1";   // dev: showcase the props
            // per-placement albedo: each object's extracted Texture2D (content/menu/tex), cached by name. Unturned
            // albedos are small palette textures -> Nearest; foliage/leaves are alpha-cutout sheets -> AlphaScissor.
            var matCache = new System.Collections.Generic.Dictionary<string, StandardMaterial3D>();
            StandardMaterial3D MatFor(string tex)
            {
                if (string.IsNullOrEmpty(tex)) return gray;
                if (matCache.TryGetValue(tex, out var hit)) return hit;
                string tp = G($"res://content/menu/tex/{tex}");
                var img = new Image();
                if (!System.IO.File.Exists(tp) || img.Load(tp) != Error.Ok) { matCache[tex] = gray; return gray; }
                bool leafy = tex.Contains("Foliage") || tex.Contains("Leaves");
                var mat = new StandardMaterial3D
                {
                    AlbedoTexture = ImageTexture.CreateFromImage(img),
                    Roughness = 1f,
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                    TextureFilter = leafy ? BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps
                                          : BaseMaterial3D.TextureFilterEnum.Nearest,
                };
                if (leafy) { mat.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor; mat.AlphaScissorThreshold = 0.5f; }
                matCache[tex] = mat;
                return mat;
            }
            int placed = 0, missed = 0;
            foreach (var e in arr)
            {
                var d = e.AsGodotDictionary();
                string nm = d["name"].AsString();   // drop editor gizmos that carry a mesh in the scene but aren't visual
                if (nm is "Radius" or "Icon" or "Icon2" or "Target" or "Effect" or "Skeleton") { missed++; continue; }
                string mn = d["mesh"].AsString();
                if (noTrees && (mn.StartsWith("Birch") || mn.StartsWith("Pine") || mn.StartsWith("Maple") || mn.Contains("Foliage"))) { missed++; continue; }
                string op = G($"res://content/menu/mesh/{mn}.obj");
                if (!System.IO.File.Exists(op)) { missed++; continue; }   // ObjMesh.Load THROWS on a missing file
                var mesh = ObjMesh.Load(op);
                if (mesh == null) { missed++; continue; }
                Vector3 V(string k) { var a = d[k].AsGodotArray(); return new Vector3(a[0].AsSingle(), a[1].AsSingle(), a[2].AsSingle()); }
                var basis = new Basis(V("xaxis"), V("yaxis"), V("zaxis"));
                string tex = d.ContainsKey("tex") ? d["tex"].AsString() : "";   // "" (null in json) -> gray fallback
                AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = MatFor(tex), Transform = new Transform3D(basis, V("origin")) });
                placed++;
            }
            GD.Print($"[menu] extracted diorama: placed {placed}, missing-mesh {missed}");
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
            MenuButton(layer, "multiplayer",   "Multiplayer",   230f, false, 1, () => ToggleServersPanel());   // -> the server browser (MainMenuServers.cs)
            MenuButton(layer, "survivors",     "Survivors",     290f, false, 2, () => ShowStub("Survivors"));
            // Configuration -> the GRAPHICS panel (master asked for it here and in the pause menu). Retail's
            // Configuration menu is where graphics live, so this replaces the stub rather than adding a sixth button.
            MenuButton(layer, "configuration", "Configuration", 350f, false, 3, () => ToggleGraphicsPanel());
            MenuButton(layer, "workshop",      "Workshop",      410f, false, 4, () => ToggleWorkshopPanel());
            MenuButton(layer, "playground",    "Playground",    470f, false, 1, () => OnPlayground?.Invoke());   // gun range; no icon file yet -> MenuButton just skips the icon
            MenuButton(layer, "exit",          "Exit",          -70f, true,  0, () => GetTree().Quit());

            BuildMapSelector(layer);   // Play -> retail singleplayer map selector + gameplay options (MainMenuPlay.cs)
            BuildServersPanel(layer);  // Multiplayer -> the server browser (MainMenuServers.cs)
            BuildWorkshopPanel(layer);
            BuildStubPanel(layer);
            BuildGraphicsPanel(layer);
        }

        Control _graphicsPanel;

        void BuildGraphicsPanel(CanvasLayer layer)
        {
            // Same screen position as the other side panels, and the SAME GraphicsPanel builder the pause menu uses
            // -- the settings themselves live in GraphicsOptions, so the two views cannot drift apart.
            _graphicsPanel = new PanelContainer { Position = new Vector2(240f, 150f), Visible = false };
            ((PanelContainer)_graphicsPanel).AddChild(GraphicsPanel.Build(this, () => _graphicsPanel.Visible = false));
            layer.AddChild(_graphicsPanel);
        }

        void ToggleGraphicsPanel()
        {
            bool show = !_graphicsPanel.Visible;
            HideAllPanels();
            _graphicsPanel.Visible = show;
        }

        /// <summary>Close every side panel. Added when Configuration stopped being a stub: each Toggle* already hid
        /// the others by hand, so a new panel meant editing four call sites and forgetting one meant two panels
        /// stacked on top of each other.</summary>
        void HideAllPanels()
        {
            if (_playPanel != null) _playPanel.Visible = false;
            if (_stubPanel != null) _stubPanel.Visible = false;
            if (_advancedPanel != null) _advancedPanel.Visible = false;
            if (_graphicsPanel != null) _graphicsPanel.Visible = false;
            if (_workshopPanel != null) _workshopPanel.Visible = false;
            if (_serversPanel != null) _serversPanel.Visible = false;
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

        // Play -> the retail singleplayer map selector + per-map gameplay options lives in MainMenuPlay.cs
        // (BuildMapSelector assigns _playPanel), so TogglePlayPanel/ShowStub/ToggleWorkshopPanel still drive it.

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
            // HideAllPanels rather than hand-hiding a list -- every hand-written list here omitted the GRAPHICS
            // panel, and all four panels sit at the same screen position, so Configuration -> Play left both
            // visible and overlapping with whichever is later in child order eating the clicks. That is the
            // failure HideAllPanels' own doc comment names as its reason for existing. Review 2026-08-16.
            HideAllPanels();
            _playPanel.Visible = show;
            if (_advancedPanel != null) _advancedPanel.Visible = false;   // advanced starts collapsed each open
            _targetTab = 1;   // hold the Play framing while the panel is open
        }

        // Multiplayer -> the server browser (MainMenuServers.cs; BuildServersPanel assigns _serversPanel).
        void ToggleServersPanel()
        {
            bool show = !_serversPanel.Visible;
            HideAllPanels();   // see TogglePlayPanel -- the hand-written list omitted the graphics panel
            _serversPanel.Visible = show;
            if (_playPanel != null) _playPanel.Visible = false;
            if (_stubPanel != null) _stubPanel.Visible = false;
            if (_workshopPanel != null) _workshopPanel.Visible = false;
            if (_advancedPanel != null) _advancedPanel.Visible = false;
            _targetTab = 1;   // hold the Play framing while the browser is open
            if (show && _selectedServer == null && OfficialServers.Length > 0) SelectServer(OfficialServers[0]);   // highlight the top row so info/JOIN reflect it
            if (show && !_serversAutoRefreshed) { _serversAutoRefreshed = true; RefreshServers(); }   // auto-query live ping/count on first open
        }

        // Workshop submenu -- vanilla has Editor / Manage / browse; ours ships the Editor (PEI) for now.
        LineEdit _newMapName;
        VBoxContainer _mapList;

        void BuildWorkshopPanel(CanvasLayer layer)
        {
            // Anchored higher than before because the panel now grows with the saved-map list. At the old
            // y=410 a third map pushed the buttons off the bottom of the screen.
            _workshopPanel = new PanelContainer { Position = new Vector2(240f, 150f), Visible = false };
            var box = new VBoxContainer();
            box.AddThemeConstantOverride("separation", 10);
            ((PanelContainer)_workshopPanel).AddChild(box);
            var head = new Label { Text = "WORKSHOP" };
            head.AddThemeFontSizeOverride("font_size", 22);
            box.AddChild(head);

            box.AddChild(SubButton("Editor — Prince Edward Island", () => OnEditor?.Invoke()));

            box.AddChild(new HSeparator());
            box.AddChild(Dim("NEW MAP"));
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 6);
            _newMapName = new LineEdit { PlaceholderText = "map name", CustomMinimumSize = new Vector2(212f, 40f) };
            _newMapName.TextSubmitted += _ => CreateMap();
            row.AddChild(_newMapName);
            var create = new Button { Text = "Create", CustomMinimumSize = new Vector2(102f, 40f) };
            create.Pressed += CreateMap;
            row.AddChild(create);
            box.AddChild(row);

            box.AddChild(new HSeparator());
            box.AddChild(Dim("SAVED MAPS  —  open to edit, or play it"));
            _mapList = new VBoxContainer();
            _mapList.AddThemeConstantOverride("separation", 4);
            box.AddChild(_mapList);

            layer.AddChild(_workshopPanel);
            RefreshMapList();
        }

        static Label Dim(string t)
        {
            var l = new Label { Text = t };
            l.AddThemeFontSizeOverride("font_size", 13);
            l.AddThemeColorOverride("font_color", new Color(0.72f, 0.78f, 0.84f));
            return l;
        }

        /// <summary>Create refuses rather than guessing. An empty or unusable name (see EditorMaps.Sanitise --
        /// the name becomes a FILE PATH) leaves the field alone and says so, instead of silently opening
        /// something called "NewMap" that the user then can't find. Names are made unique too, so a second
        /// "Test" is "Test 2" and never quietly opens on top of the first one's files.</summary>
        void CreateMap()
        {
            var typed = EditorMaps.Sanitise(_newMapName.Text);
            if (typed == null)
            {
                _newMapName.Text = "";
                _newMapName.PlaceholderText = "letters, numbers, - and _ only";
                return;
            }
            OnOpenMap?.Invoke(EditorMaps.Unique(typed));
        }

        /// <summary>Rebuild the saved-map rows. Called when the panel is opened, not only at construction --
        /// coming back from the editor after saving a new map must show it.</summary>
        void RefreshMapList()
        {
            if (_mapList == null) return;
            foreach (var c in _mapList.GetChildren()) ((Node)c).QueueFree();
            var maps = EditorMaps.List();
            if (maps.Count == 0) { _mapList.AddChild(Dim("  (none yet — create one above)")); return; }
            foreach (var name in maps)
            {
                var n = name;
                var r = new HBoxContainer();
                r.AddThemeConstantOverride("separation", 6);
                var open = new Button { Text = n, CustomMinimumSize = new Vector2(212f, 38f), Alignment = HorizontalAlignment.Left };
                open.Pressed += () => OnOpenMap?.Invoke(n);
                r.AddChild(open);
                var play = new Button { Text = "▶ Play", CustomMinimumSize = new Vector2(102f, 38f) };
                play.Pressed += () => OnPlayMap?.Invoke(n);
                r.AddChild(play);
                _mapList.AddChild(r);
            }
        }

        void ToggleWorkshopPanel()
        {
            bool show = !_workshopPanel.Visible;
            if (show) RefreshMapList();   // a map saved since the menu was built has to appear without a restart
            HideAllPanels();   // see TogglePlayPanel
            _workshopPanel.Visible = show;
            if (_playPanel != null) _playPanel.Visible = false;
            if (_stubPanel != null) _stubPanel.Visible = false;
            if (_serversPanel != null) _serversPanel.Visible = false;
            if (_advancedPanel != null) _advancedPanel.Visible = false;
            _targetTab = 4;   // hold the Workshop framing while the panel is open
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
            HideAllPanels();   // see TogglePlayPanel -- this list omitted BOTH the graphics and workshop panels
            _stubPanel.Visible = true;
            ((Label)_stubPanel.GetNode("VBoxContainer/head")).Text = name.ToUpper();
        }

        // ---------------------------------------------------------------- camera glide (MenuUI.Update port)
        public override void _Process(double delta)
        {
            if (_cam == null) return;
            var anchorSet = _menuReal ? RealViews : Anchors;   // the real diorama pans between the extracted retail anchors
            var t = anchorSet[_targetTab];
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
        public void ShowTab(int tab)
        {
            var anchorSet = _menuReal ? RealViews : Anchors;
            _targetTab = Mathf.Clamp(tab, 0, anchorSet.Length - 1); if (tab != 0) _reachedTitle = true;
        }
    }
}
