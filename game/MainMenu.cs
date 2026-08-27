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
        // The whole menu takes place INSIDE the barn (interior ~ X[-7.5,7.5] Z[-10.5,10.5] Y[-4.89,+11.04],
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
        int _forceTab = -1;          // >=0: the --menushot harness (ShowTab) is forcing an anchor; the live menu stays -1 and follows the open submenu
        bool _reachedTitle;          // MenuUI.hasReachedTitleCameraTransform: first pan to Title is slow, then snappy
        // Retail's Play menu is a LIST OF BUTTONS (MenuPlayUI: Singleplayer / Servers / Connect /
        // Bookmarks / Lobbies), and Singleplayer is what opens the map selector. The port had that
        // flattened -- "Play" went straight to the map selector and Multiplayer sat as a sixth
        // top-level button retail does not have. This is the missing middle layer.
        Control _playMenuPanel;
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
        static readonly (Vector3 pos, Quaternion rot)[] RealViews =
        {
            // Retail's own anchors, read out of MenuOverridableObjects (&113) in the ripped Menu.unity and
            // converted Unity->Godot (position z negated, quaternion (x,y,-z,-w)).
            //
            // Stored as QUATERNIONS, not as a look-at point. A look point cannot express ROLL, and three of
            // these carry some -- Workshop 1.90 deg, Survivors 0.90 deg -- which Basis.LookingAt silently
            // flattened to zero. Control for the conversion: the forward vectors these produce agree with the
            // old hand-entered look points to 0.11-0.35 deg, which is the quantisation of a 2-decimal look
            // point at 1 m. Same direction, roll recovered.
            (new Vector3(0.0000f, 5.4905f, -5.9909f), new Quaternion(-0.002670f, 0.992978f, 0.110306f, 0.042664f)),   // 0 Title (idle)
            (new Vector3(0.5273f, 2.1790f,  5.1001f), new Quaternion( 0.000000f, 0.998066f, 0.000000f, 0.062158f)),   // 1 Play
            (new Vector3(-2.0023f, 1.7429f, 1.1436f), new Quaternion(-0.044532f, 0.829203f, 0.081010f, 0.551251f)),   // 2 Survivors
            (new Vector3(3.3740f, 2.3604f,  2.8481f), new Quaternion(-0.061019f, -0.932827f, -0.189541f, 0.300306f)), // 3 Configuration
            (new Vector3(0.2250f, 2.9024f, -1.3593f), new Quaternion( 0.105851f, 0.542197f, 0.049556f, -0.832083f)),  // 4 Workshop
        };

        // MenuOverridableObjects.initialCamera -- "Point of view when menu first loads. Blends into Title
        // Camera." A SIXTH pose the port never had, 9.3 m from Title, outside the barn looking in. Retail
        // opens here and drifts to Title at the slow deltaTime*1 rate; that is the whole cinematic intro.
        static readonly Vector3 InitialPos = new Vector3(0.1989f, 9.9996f, -14.1008f);
        static readonly Quaternion InitialRot = new Quaternion(0.001128f, -0.982858f, -0.184262f, -0.006016f);
        /// <summary>Unity's Quaternion.Lerp: componentwise lerp along the SHORT arc, then normalise.
        /// Godot ships Slerp but not this, and MenuUI uses Lerp -- see the glide in _Process.</summary>
        /// <summary>The retail main-menu sky: Skybox_MainMenu.mat, verbatim.
        ///
        /// Menu.unity's RenderSettings point m_SkyboxMaterial at
        /// Assets/Game/Sources/Scenes/Skybox_MainMenu.mat (shader Skybox/Sky, keyword WITH_CLOUDS), and
        /// LevelLighting.resetForMainMenu only clears atmospheric fog -- nothing replaces it. It is a NIGHT
        /// sky: black zenith, grey-blue equator, and the sun sitting ON the horizon at +X in orange. The port
        /// was inventing a bright pastoral midday blue instead, and that is what shows through the barn
        /// doorway and the loft window in every frame.
        ///
        /// The shader itself is already ported -- DayNightCycle.SkyShaderCode is a faithful port of
        /// Skybox-Sky.shader -- so this only has to feed it the material's own values.</summary>
        static ShaderMaterial MenuSkyMaterial()
        {
            var m = new ShaderMaterial { Shader = new Shader { Code = DayNightCycle.SkyShaderCode } };
            m.SetShaderParameter("sky_color", new Color(0f, 0f, 0f));                       // _SkyColor
            m.SetShaderParameter("equator_color", new Color(0.4099f, 0.4291f, 0.4906f));    // _EquatorColor
            m.SetShaderParameter("ground_color", new Color(0.2075f, 0.2075f, 0.2075f));     // _GroundColor
            m.SetShaderParameter("sun_color", new Color(1f, 0.5f, 0f));                     // _SunColor
            m.SetShaderParameter("sun_direction", new Vector3(-1f, 0f, 0f));                // _SunDirection: on the horizon
            m.SetShaderParameter("moon_direction", new Vector3(1f, 0f, 0f));
            m.SetShaderParameter("moon_light_direction", new Vector3(0f, -1f, 0f));
            m.SetShaderParameter("moon_color", new Color(0.749f, 0.804f, 0.808f));
            m.SetShaderParameter("sqr_moon_radius", 0.01f);                                 // _SqrMoonRadius
            m.SetShaderParameter("sun_inner", 0.995f);                                      // _SunInnerThreshold
            m.SetShaderParameter("sun_outer", 0.993f);                                      // _SunOuterThreshold
            m.SetShaderParameter("stars_cutoff", 0f);                                       // _StarsCutoff
            m.SetShaderParameter("cloud_rim_color", new Color(0.2170f, 0.2170f, 0.2170f));  // _CloudRimColor
            m.SetShaderParameter("cloud_intensity", 1f);                                    // _CloudIntensity
            m.SetShaderParameter("cloud_params", new Vector4(0.6f, 10f, 0f, 0f));
            m.SetShaderParameter("ambient_ground", new Color(0.2075f, 0.2075f, 0.2075f));
            m.SetShaderParameter("ambient_equator", new Color(0.4099f, 0.4291f, 0.4906f));
            m.SetShaderParameter("clouds_tex", DayNightCycle.LoadTex("res://content/sky_clouds.png"));
            m.SetShaderParameter("stars_tex", DayNightCycle.LoadTex("res://content/sky_stars.png"));
            return m;
        }

        static Quaternion Nlerp(Quaternion a, Quaternion b, float t)
        {
            if (a.Dot(b) < 0f) b = new Quaternion(-b.X, -b.Y, -b.Z, -b.W);   // short arc, as Unity does
            return new Quaternion(
                Mathf.Lerp(a.X, b.X, t), Mathf.Lerp(a.Y, b.Y, t),
                Mathf.Lerp(a.Z, b.Z, t), Mathf.Lerp(a.W, b.W, t)).Normalized();
        }

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
            // BEFORE BuildWorld: it now branches on this for the environment and the sun. It used to be set
            // further down, after BuildWorld had already run, so anything in BuildWorld reading the field
            // rather than the env var directly would silently take the placeholder path.
            _menuReal = System.Environment.GetEnvironmentVariable("UG_MENUREAL") == "1";
            BuildWorld();
            BuildUI();
            // --menushot / debug: open the Play submenu at load so a render captures it (UG_MENUOPEN=map|options|advanced)
            var _open = System.Environment.GetEnvironmentVariable("UG_MENUOPEN");
            if (_open == "map" || _open == "play" || _open == "options" || _open == "advanced") TogglePlayPanel();
            if (_open == "servers") ToggleServersPanel();
            if (_open == "advanced") ToggleAdvanced();
            if (_menuReal)
            {
                // Open on initialCamera and let the slow deltaTime*1 pan carry us to Title, which is what
                // retail does (MenuUI.cs:979-991 snaps to source.initialCamera when the decoration scene
                // finishes loading, then Update lerps toward Title at k=1 until a submenu is opened).
                // This used to set _reachedTitle = true and snap straight onto Title, which made the k=1
                // branch below dead code on this path -- the cinematic intro the commit message advertised
                // could never run.
                _targetTab = 0; _reachedTitle = false;
                _cam.Position = ParseV3("UG_MENUEYE", InitialPos);
                _cam.Quaternion = InitialRot;
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
            // Retail values, not tuned ones. Menu.unity's RenderSettings are the ones that govern (Menu_Base
            // and Menu_NoHoliday load ADDITIVELY, and the active scene's settings win): m_AmbientMode 1
            // (trilight) with sky/equator/ground ALL 0.39215687 neutral and m_AmbientIntensity 1.
            //
            // The earlier 0.28/0.30/0.33 was the right diagnosis overshooting the answer -- the haze really
            // was unoccluded ambient, but the correct number was sitting in the scene the whole time, and the
            // diff that motivated the guess was against WorldBuilder's BuildPlaygroundWorld (the flat mapless
            // gun range), not the gameplay world.
            if (_menuReal)
            {
                const float amb = 0.39215687f;
                env.AmbientLightColor = new Color(amb, amb, amb);
                env.AmbientLightEnergy = 1.0f;
                // AmbientLightSkyContribution defaults to 1.0 -- "all the light that affects the scene is
                // provided by the Sky". With a BLACK-zenith night sky that is close to no ambient at all,
                // which silently discards the 0.392 set right above. Setting the source to Color is not
                // enough on its own; the blend is a separate knob. Measured, not assumed: see below.
                env.AmbientLightSkyContribution = 0f;
                env.SsaoEnabled = false;                       // retail's AO defaults OFF (GraphicsSettingsData)
                env.TonemapMode = Godot.Environment.ToneMapper.Linear;   // retail applies no tonemapper here
                env.Sky = new Sky { SkyMaterial = MenuSkyMaterial() };
            }
            AddChild(new WorldEnvironment { Environment = env });

            // No sun on the real path. Menu_Base holds exactly 6 Light components -- 4 point + 2 spot, the
            // two Spotlight barricades -- and NO directional; Menu.unity's m_Sun is fileID 0 and its only
            // Light sits on the inactive Inspect camera. Retail lights that barn with the barricades and
            // ambient, nothing else. The invented sun was also the scene's only shadow caster, which is what
            // made the interior read as a black box that then needed the lamps cranked ~11x to compensate.
            if (!_menuReal)
            {
                var sun = new DirectionalLight3D
                {
                    RotationDegrees = new Vector3(-42f, 138f, 0f),
                    LightColor = new Color(1f, 0.96f, 0.87f),
                    LightEnergy = 1.25f,
                    ShadowEnabled = true,
                };
                AddChild(sun);
            }

            // interior fill for the PLACEHOLDER barn only: the roof occludes the sky so the inside would be a
            // black box, and a warm omni fakes the light. The real diorama uses its own 6 extracted lamps
            // (LoadMenuLamps) instead of this eyeballed fill -- source-accurate, not tuned by eye.
            if (System.Environment.GetEnvironmentVariable("UG_MENUREAL") != "1")
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
            if (_menuReal) { LoadMenuScene(); LoadMenuLamps(); LoadMenuHero(); }
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

            // FOV 90, not the 60 authored in the scene. OptionsSettings computes the menu camera's vertical
            // FOV as MIN_FOV + MAX_FOV * fov = 60 + 40 * 0.75 (MAX_FOV is the SPAN, not a ceiling), and
            // MenuUI.customStart -> OptionsSettings.apply() writes it before the menu is interactive because
            // !Level.isLoaded. So the scene's 60 is an authored default retail overwrites, and it happens to
            // be the slider MINIMUM -- 60 vs 90 vertical is 3.0x the frame area.
            // near/far match the retail menu cameras (0.08 / 1024) rather than Godot's 0.05 / 4000.
            _cam = new Camera3D { Current = true, Fov = 90f, Near = 0.08f, Far = 1024f };
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
            bool noTrees = System.Environment.GetEnvironmentVariable("UG_MENUNOTREES") == "1";   // dev: showcase the props
            // Per-placement albedo = the material's _Color * its _MainTex (content/menu/tex). _MainTex defaults to
            // white, so an _MainTex-less material is still coloured by _Color, not grey. Palette albedos -> Nearest;
            // foliage sheets -> alpha-cutout at the retail _Cutoff (Leaves override 0.5->0.2) with real mipmaps
            // (GenerateMipmaps, else the LinearWithMipmaps filter is a no-op and the canopy vanishes at distance).
            // Cache keyed by tex+colour+cutoff; a failed texture NEVER caches a grey stand-in under the texture's name.
            var matCache = new System.Collections.Generic.Dictionary<string, StandardMaterial3D>();
            StandardMaterial3D MatFor(string tex, Color albedo, float cutoff, bool leafy)
            {
                string key = $"{tex}|{albedo}|{cutoff}|{leafy}";
                if (matCache.TryGetValue(key, out var hit)) return hit;
                var mat = new StandardMaterial3D { AlbedoColor = albedo, Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
                if (!string.IsNullOrEmpty(tex))
                {
                    string tp = G($"res://content/menu/tex/{tex}");
                    var img = new Image();
                    if (System.IO.File.Exists(tp) && img.Load(tp) == Error.Ok)
                    {
                        if (leafy) img.GenerateMipmaps();
                        mat.AlbedoTexture = ImageTexture.CreateFromImage(img);
                        mat.TextureFilter = leafy ? BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps : BaseMaterial3D.TextureFilterEnum.Nearest;
                    }
                    else GD.PrintErr($"[menu] texture load failed, _Color only: {tex}");
                }
                if (leafy)
                {
                    mat.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
                    mat.AlphaScissorThreshold = cutoff > 0f ? cutoff : 0.2f;
                }
                matCache[key] = mat;
                return mat;
            }
            int placed = 0, skipGizmo = 0, skipTree = 0, skipNoMesh = 0, errored = 0;
            foreach (var e in arr)
            {
                try
                {
                    var d = e.AsGodotDictionary();
                    string nm = d["name"].AsString();   // defensive: gizmos are already filtered upstream (plan_scene)
                    if (nm is "Radius" or "Icon" or "Icon2" or "Target" or "Effect" or "Skeleton") { skipGizmo++; continue; }
                    string mn = d["mesh"].AsString();
                    if (noTrees && (mn.StartsWith("Birch") || mn.StartsWith("Pine") || mn.StartsWith("Maple") || mn.Contains("Foliage"))) { skipTree++; continue; }
                    string op = G($"res://content/menu/mesh/{mn}.obj");
                    if (!System.IO.File.Exists(op)) { GD.PrintErr($"[menu] mesh missing: {mn}"); skipNoMesh++; continue; }   // ObjMesh.Load THROWS on a missing file
                    var mesh = ObjMesh.Load(op);
                    if (mesh == null) { GD.PrintErr($"[menu] mesh empty: {mn}"); skipNoMesh++; continue; }
                    Vector3 V(string k) { var a = d[k].AsGodotArray(); return new Vector3(a[0].AsSingle(), a[1].AsSingle(), a[2].AsSingle()); }
                    var basis = new Basis(V("xaxis"), V("yaxis"), V("zaxis"));
                    string tex = (d.ContainsKey("tex") && d["tex"].VariantType != Variant.Type.Nil) ? d["tex"].AsString() : "";
                    Color albedo = Colors.White;
                    if (d.ContainsKey("color")) { var ca = d["color"].AsGodotArray(); albedo = new Color(ca[0].AsSingle(), ca[1].AsSingle(), ca[2].AsSingle()); }
                    float cutoff = (d.ContainsKey("cutoff") && d["cutoff"].VariantType != Variant.Type.Nil) ? d["cutoff"].AsSingle() : 0f;
                    bool leafy = mn.Contains("Foliage") || tex.Contains("Foliage") || tex.Contains("Leaves");
                    AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = MatFor(tex, albedo, cutoff, leafy), Transform = new Transform3D(basis, V("origin")) });
                    placed++;
                }
                catch (System.Exception ex) { GD.PrintErr($"[menu] placement error: {ex.Message}"); errored++; }
            }
            GD.Print($"[menu] diorama: placed {placed} | skipped gizmo {skipGizmo}, tree {skipTree}, no-mesh {skipNoMesh}, errored {errored}");
        }

        // The real menu's own 6 lamps (Light docs extracted from Menu_Base into content/menu/menu_lamps.json:
        // 4 warm point lamps + 2 spots on the +X wall). Source-accurate interior light instead of an eyeballed
        // fill. UG_LAMPSCALE multiplies energy if the Unity->Godot intensity needs a nudge (default 1).
        void LoadMenuLamps()
        {
            string jf = G("res://content/menu/menu_lamps.json");
            if (!System.IO.File.Exists(jf)) { GD.PrintErr("[menu] menu_lamps.json missing"); return; }
            // Godot energy vs Unity intensity relate differently at different RANGES (godot's falloff vs unity's), so
            // ONE global factor can't hold: the range-4 point lamps and range-64 spots need factors ~11x apart
            // (measured -- a single 4.0 blew out the point-lit workbench ~11x). Per-family. UG_LAMPSCALE_PT/_SP override.
            // The point family runs INVERSE-SQUARE (decay=2, see the OmniAttenuation note below). 0.65 = the old decay-1
            // workbench fit (0.39) x the lamp->workbench distance (~1.66m = y2.56 lamp - ~0.9 table): re-derived ONCE so
            // the workbench holds its brightness under decay=2, instead of stacking a 2nd falloff fit on top of 0.39.
            float ptScale = ParseF(System.Environment.GetEnvironmentVariable("UG_LAMPSCALE_PT"), 0.65f);
            float spScale = ParseF(System.Environment.GetEnvironmentVariable("UG_LAMPSCALE_SP"), 4.2f);
            var arr = Json.ParseString(System.IO.File.ReadAllText(jf)).AsGodotArray();
            int lamps = 0;
            foreach (var e in arr)
            {
                try
                {
                    var d = e.AsGodotDictionary();
                    Vector3 V(string k) { var a = d[k].AsGodotArray(); return new Vector3(a[0].AsSingle(), a[1].AsSingle(), a[2].AsSingle()); }
                    var col = V("color");
                    var c = new Color(col.X, col.Y, col.Z);
                    float intensity = d["intensity"].AsSingle();
                    float range = d["range"].AsSingle();
                    int type = (int)d["type"].AsInt64();
                    // retail's 6 menu lamps are all m_Shadows.m_Type=0 -> shadows stay OFF (Godot lights default off).
                    if (type == 0)   // Unity Spot -> aims along +fwd (Godot spot emits -Z)
                    {
                        // Godot SpotAngle is the HALF cone; Unity m_SpotAngle is the FULL cone -> halve it.
                        var s = new SpotLight3D { Position = V("pos"), LightColor = c, LightEnergy = intensity * spScale,
                                                  SpotRange = range, SpotAngle = d["spot"].AsSingle() * 0.5f };
                        AddChild(s);
                        s.LookAt(V("pos") + V("fwd"), Vector3.Up);
                    }
                    else if (type == 2)   // Unity Point
                        // decay=2 (~inverse-square) -- the RIGHT FAMILY, not an exact match. Godot's default (1) decays far
                        // too slowly, so a range-4 lamp at y2.56 stays bright at the loft floor above and lights it through
                        // the (unoccluded G4.6) floor -- the "mystery loft light". Unity's built-in point falloff is actually
                        // 1/(1+25(d/r)^2), whose +1 flattens it near the lamp, so decay=2 carries a KNOWN RESIDUAL vs retail
                        // (over-bright within ~0.5m of a fixture, ~3.3x span over 0.5-3m) -- but it's a curve error now, not a
                        // fitted constant, and far better than the unusable decay=1. So the loft dims as a CONSEQUENCE of the
                        // curve, not a 2nd fit; ptScale above is re-derived once to hold the workbench. (2.5 was TUNED and
                        // drifted the ground floor dim -- A/B-confirmed. UG_OMNIATTEN overrides.)
                        AddChild(new OmniLight3D { Position = V("pos"), LightColor = c, LightEnergy = intensity * ptScale, OmniRange = range,
                                                   OmniAttenuation = ParseF(System.Environment.GetEnvironmentVariable("UG_OMNIATTEN"), 2.0f) });
                    else
                        GD.PrintErr($"[menu] unhandled lamp type {type} (Menu_Base has only Point/Spot)");
                    lamps++;
                }
                catch (System.Exception ex) { GD.PrintErr($"[menu] lamp error: {ex.Message}"); }
            }
            GD.Print($"[menu] placed {lamps} real lamps (point x{ptScale}, spot x{spScale})");
        }

        // The Hero -- the skinned survivor the whole diorama is arranged around (the Survivors camera frames it).
        // Menu.unity's `Hero` GameObject / MenuOverridableObjects.playerCharacterTransform sits at (-2.951, 0.087,
        // 2.129) (F*M), Unity yaw -38.75 (the transform's +90 X is a Unity-prefab stand-up artifact the port's
        // already-upright RiggedCharacter doesn't need, so apply position + yaw only). Reuse the in-game 3rd-person
        // body + its own Idle_Stand loop. UG_HEROYAW tunes the facing against the render.
        void LoadMenuHero()
        {
            try
            {
                var hero = RiggedCharacter.Build("res://content/rig.json", new Color(0.82f, 0.66f, 0.52f));
                hero.Position = new Vector3(-2.951f, 0.087f, 2.129f);
                AddChild(hero);
                // Face the Survivors customization camera (retail frames the survivor there). The port's
                // RiggedCharacter faces -Z at yaw 0, and Godot's LookAt points -Z at the target, so this turns the
                // survivor toward that camera. UG_HEROYAW adds a fine-tune offset (deg).
                var svCam = new Vector3(RealViews[2].pos.X, hero.Position.Y, RealViews[2].pos.Z);
                hero.LookAt(svCam, Vector3.Up);
                hero.RotateY(Mathf.DegToRad(ParseF(System.Environment.GetEnvironmentVariable("UG_HEROYAW"), 0f)));
                hero.PlayLoop(hero.IdleClip);
                GD.Print("[menu] placed Hero (RiggedCharacter, Idle_Stand)");
            }
            catch (System.Exception ex) { GD.PrintErr($"[menu] hero failed: {ex.Message}"); }
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
            // The dashboard is retail's five: Play / Survivors / Configuration / Workshop / Exit
            // (MenuDashboardUI opens exactly MenuPlayUI, MenuSurvivorsUI, MenuConfigurationUI,
            // MenuWorkshopUI). Multiplayer and Playground used to sit up here as extra top-level
            // buttons; both are now inside Play, where retail keeps servers -- which also disposes of
            // the missing icon_multiplayer.png, since retail has no such button to give an icon to.
            MenuButton(layer, "play",          "Play",          170f, false, () => TogglePlayMenu());
            MenuButton(layer, "survivors",     "Survivors",     230f, false, () => ShowStub("Survivors"));
            // Configuration -> the GRAPHICS panel (master asked for it here and in the pause menu). Retail's
            // Configuration menu is where graphics live, so this replaces the stub rather than adding a sixth button.
            MenuButton(layer, "configuration", "Configuration", 290f, false, () => ToggleGraphicsPanel());
            MenuButton(layer, "workshop",      "Workshop",      350f, false, () => ToggleWorkshopPanel());
            MenuButton(layer, "exit",          "Exit",          -70f, true,  () => GetTree().Quit());

            BuildPlayMenuPanel(layer); // Play -> retail's Play MENU (Singleplayer / Multiplayer / Playground)
            BuildMapSelector(layer);   // Play > Singleplayer -> map selector + gameplay options (MainMenuPlay.cs)
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
            if (_playMenuPanel != null) _playMenuPanel.Visible = false;
            if (_playPanel != null) _playPanel.Visible = false;
            if (_stubPanel != null) _stubPanel.Visible = false;
            if (_advancedPanel != null) _advancedPanel.Visible = false;
            if (_graphicsPanel != null) _graphicsPanel.Visible = false;
            if (_workshopPanel != null) _workshopPanel.Visible = false;
            if (_serversPanel != null) _serversPanel.Visible = false;
        }

        // (no `tab` parameter: the camera framing is derived from which panel is OPEN, in _Process.
//  It lingered after that change reading like it still selected a framing, and nothing used it.)
        void MenuButton(CanvasLayer layer, string icon, string text, float y, bool fromBottom, System.Action onClick)
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
            b.Pressed += () => onClick();   // camera follows which submenu is OPEN (see _Process), NOT hover
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

        /// <summary>Retail's Play menu (MenuPlayUI): a column of buttons, not the map selector.
        /// Singleplayer opens the selector (MenuPlaySingleplayerUI), Servers the browser. Connect /
        /// Bookmarks / Lobbies are retail buttons this port has no backend for and so does not show;
        /// Playground has no retail equivalent and lives here because it is a way to start playing.</summary>
        void BuildPlayMenuPanel(CanvasLayer layer)
        {
            var panel = new PanelContainer { Position = new Vector2(240f, 200f), Visible = false };
            var margin = new MarginContainer();
            foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
                margin.AddThemeConstantOverride(side, 14);
            panel.AddChild(margin);
            var box = new VBoxContainer();
            box.AddThemeConstantOverride("separation", 8);
            margin.AddChild(box);
            box.AddChild(Header("PLAY", 24));
            void Row(string text, string tip, System.Action go)
            {
                var b = new Button { Text = text, CustomMinimumSize = new Vector2(300f, 44f), TooltipText = tip,
                                     Alignment = HorizontalAlignment.Left };
                b.AddThemeFontSizeOverride("font_size", 18);
                b.Pressed += () => go();
                box.AddChild(b);
            }
            Row("  Singleplayer", "Pick a map and play on your own.", TogglePlayPanel);
            Row("  Multiplayer",  "Browse and join servers.",         ToggleServersPanel);
            Row("  Playground",   "The gun range -- no retail equivalent.", () => OnPlayground?.Invoke());
            layer.AddChild(panel);
            _playMenuPanel = panel;
        }

        void TogglePlayMenu()
        {
            bool show = !_playMenuPanel.Visible;
            HideAllPanels();
            _playMenuPanel.Visible = show;
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
            // Camera framing follows which submenu is OPEN, not mouse hover (MenuUI.Update picks targetCameraTransform
            // by the active page). Master: "the camera shift should only occur with each submenu selected." _forceTab
            // lets the --menushot harness sweep the anchors regardless of panel state.
            _targetTab = _forceTab >= 0 ? _forceTab
                       : (_playMenuPanel?.Visible == true || _playPanel?.Visible == true || _serversPanel?.Visible == true) ? 1
                       : _stubPanel?.Visible == true ? 2
                       : _graphicsPanel?.Visible == true ? 3
                       : _workshopPanel?.Visible == true ? 4
                       : 0;
            float d = (float)delta;
            if (_menuReal)
            {
                var t = RealViews[_targetTab];
                // Quaternion.Lerp (nlerp), NOT Slerp -- MenuUI.cs:693/698/706 uses Quaternion.Lerp. Same
                // endpoints, different curve: nlerp lags at the start of a wide arc then catches up, and the
                // two diverge by up to 3.9 deg mid-glide on Title->Workshop (a 119 deg arc). Godot's
                // Quaternion has no Lerp, so this is the componentwise lerp + normalise that nlerp is.
                float w = _targetTab == 0 && !_reachedTitle ? d * 1f : d * 4f;
                _cam.Position = _cam.Position.Lerp(t.pos, w);
                _cam.Quaternion = Nlerp(_cam.Quaternion, t.rot, w);
                // MenuUI.hasReachedTitleCameraTransform is set ONLY when the target is not the title camera
                // (MenuUI.cs:703) -- i.e. the first time you open any submenu. Arriving at Title never sets
                // it, so the opening pan stays slow the whole way in. The old 0.4 m proximity trigger cut
                // roughly the last 1.4 s of that pan by flipping it to the k=4 rate early.
                if (_targetTab != 0) _reachedTitle = true;
            }
            else
            {
                var t = Anchors[_targetTab];
                var target = new Transform3D(Basis.LookingAt(t.look - t.pos, Vector3.Up), t.pos);
                float w = _targetTab == 0 && !_reachedTitle ? d * 1f : d * 4f;
                _cam.Position = _cam.Position.Lerp(target.Origin, w);
                _cam.Quaternion = Nlerp(_cam.Quaternion, target.Basis.GetRotationQuaternion(), w);
                if (_targetTab != 0) _reachedTitle = true;
            }
        }

        // harness hook: jump the camera target to a tab (used by --menushot to capture each framing)
        public void ShowTab(int tab)
        {
            int n = _menuReal ? RealViews.Length : Anchors.Length;
            _forceTab = Mathf.Clamp(tab, 0, n - 1);   // render-harness override; the live menu follows panel state
            _targetTab = _forceTab;
            // SNAP, don't glide. The harness captures a fixed number of frames after switching, and the
            // camera now starts at initialCamera 9.3 m from Title, so a lerp lands it mid-pan and the golden
            // records wherever it happened to be. Setting _reachedTitle alone only picks the faster rate --
            // it does not arrive. The live menu never takes this path (_forceTab stays -1).
            _reachedTitle = true;
            if (_cam != null)
            {
                if (_menuReal) { _cam.Position = RealViews[_targetTab].pos; _cam.Quaternion = RealViews[_targetTab].rot; }
                else
                {
                    var a = Anchors[_targetTab];
                    _cam.Position = a.pos;
                    _cam.Quaternion = new Transform3D(Basis.LookingAt(a.look - a.pos, Vector3.Up), a.pos).Basis.GetRotationQuaternion();
                }
            }
        }
    }
}
