using Godot;
using SDG.NetPak;
using SDG.Unturned;   // UnturnedDat (DatParser etc.)

namespace UnturnedGodot
{
    // Phase-0 smoke + GATE + (opt-in) catalog check + (opt-in) SHOT showcase.
    //   default      : smoke (ported core runs in-engine) + GATE (ripped prop by GUID) + optional catalog.
    //   -- --shot=P  : build a lit showcase of real ripped props and save a PNG to P (visual eyeball).
    //   -- --catalog=M : point ContentProvider at the full external manifest M.
    public partial class Main : Node
    {
        const string GateGuid = "fb9428c7b8df82e4eb9642dacfaf9567"; // Aprix_Mask_0, ripped from core.masterbundle

        string _shotPath;
        Vector3 _zAim; bool _zHave;   // first PEI zombie's position, for the UG_ZAERIAL demo cam
        Vector3 _vAim; bool _vHave;   // first real (Police/Fire/Ambulance) vehicle, for the demo cam
        bool _noZombies;   // --nozombies: a quiet test environment (skip the horde spawner)
        int _frame;
        string _rigDir;                              // --rig=DIR : capture a frame strip here
        int[] _rigCaptureFrames = { 4, 12, 20, 28, 36, 44 };
        int _rigShot;
        RiggedCharacter _rc;                         // montage: cycle through several clips
        string[] _rigList = System.Array.Empty<string>();
        int _rigMontageIdx = -1;
        const int MontageFramesPerClip = 55;
        bool _ragTest;                               // --anim=Ragdoll : trigger the death ragdoll mid-capture
        bool _vmTest; Viewmodel _vm;                 // --vm=DIR : first-person viewmodel test (equip -> ADS -> hip)
        bool _vmAimed; int _vmAimStart; int _vmSettle;
        bool _vmAttach; AttachmentMenu _am;          // --attach : hold the T attachment menu open for the render
        bool _vehTest; Vehicle _veh; Camera3D _vehCam; int _vehVariant; bool _night, _demo, _crash, _roadkill, _chain;   // --vehicle=DIR [--variant=N] [--night] [--demo] [--crash] [--roadkill] [--chain]
        bool _driveTest, _swarm, _drivethru, _nade; PlayerController _dtPlayer;      // --drivetest=DIR [--swarm|--drivethru|--nade] : enter/drive a jeep; swarm = mob it; drivethru = loud drive wakes zombies; nade = grenade the parked car
        bool _fireTest; PlayerController _ftPlayer; int _ftFrame;   // --firetest [--supp] : player fires near a distant zombie -> gunshot alert (suppressed = none)
        bool _peiPlay; PlayerController _peiPlayer; int _peiFrame; bool _peiHorde;   // --peiplay [--horde] : drive a jeep on real PEI (--horde = a zombie horde swarms it, vehicle<->zombie loop on real ground)
        bool _peiPlayable;   // menu "Drive PEI": BuildObjectsTest spawns a player+jeep with REAL controls instead of the aerial cam

        public override void _Ready()
        {
            string catalog = null, shot = null, picks = null, gun = null, rig = null, anim = "Walk", vm = null, bakeIcon = null, veh = null, drivetest = null, proptest = null;
            bool play = false, demo = false, netdemo = false, server = false, client = false, smoke = false, hurtdemo = false, invdemo = false, invsel = false, invequip = false, invdrop = false, invloot = false, invcrate = false, daynight = false, buildmode = false, meleedemo = false, falldemo = false, pronetest = false, brokentest = false, grenadetest = false, firetest = false, supp = false, terrain = false, peiplay = false, objects = false, peidrive = false;
            foreach (var arg in OS.GetCmdlineUserArgs())
            {
                if (arg.StartsWith("--catalog=")) catalog = arg["--catalog=".Length..];
                else if (arg.StartsWith("--shot=")) shot = arg["--shot=".Length..];
                else if (arg.StartsWith("--proptest=")) proptest = arg["--proptest=".Length..];   // spawn ONE named prop at identity + RGB axes -> diagnose mirror/orientation/material
                else if (arg.StartsWith("--bakeicon=")) bakeIcon = arg["--bakeicon=".Length..];   // MODEL[:ALBEDO] -> icon PNG (needs --shot=OUT)
                else if (arg.StartsWith("--rig=")) rig = arg["--rig=".Length..];
                else if (arg.StartsWith("--anim=")) anim = arg["--anim=".Length..];
                else if (arg.StartsWith("--vm=")) vm = arg["--vm=".Length..];
                else if (arg == "--attach") _vmAttach = true;
                else if (arg.StartsWith("--vehicle=")) veh = arg["--vehicle=".Length..];
                else if (arg.StartsWith("--drivetest=")) drivetest = arg["--drivetest=".Length..];
                else if (arg.StartsWith("--variant=")) _vehVariant = int.Parse(arg["--variant=".Length..]);
                else if (arg == "--night") _night = true;   // dark env + headlights on (headlight demo)
                else if (arg == "--demo") _demo = true;      // scripted honk + damage->explosion (destruction demo); off = clean drive
                else if (arg == "--crash") _crash = true;    // a wall ahead to ram (collision-damage demo)
                else if (arg == "--roadkill") _roadkill = true;   // idle zombies ahead to run over (roadkill demo)
                else if (arg == "--chain") _chain = true;         // a 2nd car + zombies beside _veh -> blow _veh -> chain reaction (source vehicle-explosion damage)
                else if (arg == "--swarm") _swarm = true;         // with --drivetest: a horde mobs the parked car + swipes it (source targetPassengerVehicle)
                else if (arg == "--drivethru") _drivethru = true; // with --drivetest: driving past distant zombies wakes them (source DRIVING stealth radius)
                else if (arg == "--nade") _nade = true;           // with --drivetest: lob a grenade onto the parked jeep (source Grenade Vehicle_Damage)
                else if (arg == "--horde") _peiHorde = true;       // with --peiplay: a zombie ring converges on the jeep -> vehicle<->zombie combat on real PEI
                else if (arg.StartsWith("--pick=")) picks = arg["--pick=".Length..];
                else if (arg.StartsWith("--gun=")) gun = arg["--gun=".Length..];
                else if (arg == "--demo") demo = true;
                else if (arg == "--play") play = true;
                else if (arg == "--nozombies") _noZombies = true;   // no-zombie test environment
                else if (arg == "--netdemo") netdemo = true;
                else if (arg == "--server") server = true;
                else if (arg == "--client") client = true;
                else if (arg == "--smoke") smoke = true;
                else if (arg == "--hurtdemo") hurtdemo = true;
                else if (arg == "--firetest") firetest = true;   // player fires near a distant zombie: verify the gunshot alert (+ --supp = suppressed -> no alert)
                else if (arg == "--supp") supp = true;           // with --firetest: attach the suppressor
                else if (arg == "--terrain") terrain = true;     // load a real map's Landscape heightmap terrain (PEI Tile_0_0)
                else if (arg == "--objects") objects = true;     // place PEI's real Level/Objects.dat objects (fences/props/rocks) on the terrain
                else if (arg == "--peidrive") peidrive = true;    // playable PEI: terrain + all objects/trees + player+jeep with real controls (same as the menu's "Drive PEI")
                else if (arg == "--peiplay") peiplay = true;     // player standing/walking on real PEI terrain (with colliders)
                else if (arg == "--invdemo") invdemo = true;
                else if (arg == "--invsel") { invdemo = true; invsel = true; }
                else if (arg == "--invequip") { invdemo = true; invequip = true; }
                else if (arg == "--invdrop") invdrop = true;
                else if (arg == "--invloot") invloot = true;
                else if (arg == "--invcrate") invcrate = true;
                else if (arg == "--meleedemo") meleedemo = true;
                else if (arg == "--falldemo") falldemo = true;
                else if (arg == "--pronetest") pronetest = true;
                else if (arg == "--brokentest") brokentest = true;
                else if (arg == "--grenadetest") grenadetest = true;
                else if (arg == "--daynight") daynight = true;
                else if (arg == "--build") buildmode = true;
                else if (arg == "--invdragtest") { RunDragTest(); GetTree().Quit(); return; }
                else if (arg == "--invusetest") { RunUseTest(); GetTree().Quit(); return; }
            }

            if (hurtdemo)   // first-person: a zombie hits the player so the hurt flash + camera flinch are visible
            {
                GetWindow().Size = new Vector2I(1280, 720);
                BuildHurtDemo(gun);
                return;
            }

            if (firetest)   // player fires away from a zombie 25 m off -> it should hear the shot (gunshot alert) UNLESS a suppressor is on
            {
                GetWindow().Size = new Vector2I(1280, 720);
                _fireTest = true;
                BuildFireTest(supp);
                return;
            }

            if (terrain)   // load a real Unturned map's terrain (PEI Landscape heightmap tile) -> a Godot mesh, replacing the flat test-plane
            {
                GetWindow().Size = new Vector2I(1280, 720);
                _shotPath = shot;   // wire the general frame-6 capture (else --shot renders the movie forever + hangs)
                BuildTerrainTest();
                return;
            }

            if (objects)   // real PEI placed objects (Objects.dat) on the terrain, viewed over the densest cluster
            {
                GetWindow().Size = new Vector2I(1280, 720);
                _shotPath = shot;
                BuildObjectsTest();
                return;
            }

            if (peidrive)  // playable PEI (also reached from the main menu's "Drive PEI" button)
            {
                GetWindow().Size = new Vector2I(1280, 720);
                _shotPath = shot;
                _peiPlayable = true;
                BuildObjectsTest();
                return;
            }

            if (peiplay)   // drop the player onto real PEI terrain (colliders on) + walk -> the whole session's work on an actual map
            {
                GetWindow().Size = new Vector2I(1280, 720);
                _peiPlay = true;
                _shotPath = shot;   // captured at a LATE frame (below) so the drop+enter+drive plays out first
                BuildPeiPlay();
                return;
            }

            if (proptest != null)   // diagnostic: one prop at identity + RGB axis refs (X=red,Y=green,Z=blue) + 3/4 cam
            {
                GetWindow().Size = new Vector2I(900, 900);
                _shotPath = shot;
                BuildPropTest(proptest);
                return;
            }

            if (invdemo)    // open the inventory dashboard over the player, populated with real items
            {
                GetWindow().Size = new Vector2I(2560, 1440);   // match the movie size so the UI lays out full-frame
                BuildInventoryDemo(gun, invsel, invequip);
                return;
            }

            if (invdrop)    // drop items into the world + a pickup check
            {
                GetWindow().Size = new Vector2I(1280, 720);
                BuildDropDemo(gun);
                return;
            }

            if (invloot)    // scatter loot around the world (LootSpawner) + an overview
            {
                GetWindow().Size = new Vector2I(1280, 720);
                BuildLootDemo(gun);
                return;
            }

            if (meleedemo)  // melee self-test: a zombie in reach, the player swings until it drops (log-verifiable)
            {
                BuildMeleeDemo(gun);
                return;
            }

            if (falldemo)   // fall-damage self-test: drop the player from height, expect damage on landing
            {
                BuildFallDemo(gun);
                return;
            }

            if (pronetest)  // stance self-test: force each stance, check the stealth detection radius (incl. new PRONE)
            {
                BuildProneTest(gun);
                return;
            }

            if (brokentest) // broken-legs self-test: fall breaks legs -> blocks sprint -> Medkit mends -> sprint restored
            {
                BuildBrokenTest(gun);
                return;
            }

            if (grenadetest) // grenade explosion self-test: check the distance falloff on zombies at known ranges
            {
                BuildGrenadeTest(gun);
                return;
            }

            if (invcrate)   // place a storage crate + open it -> dashboard shows the crate grid
            {
                GetWindow().Size = new Vector2I(2560, 1440);
                BuildCrateDemo(gun);
                return;
            }

            if (daynight)   // a fast day/night cycle over a reference scene (--write-movie for the montage, --shot=P for one frame)
            {
                _shotPath = shot;
                GetWindow().Size = new Vector2I(1280, 720);
                BuildDayNightDemo();
                return;
            }

            if (buildmode)  // script a small structure (floor + walls) to show the build system
            {
                GetWindow().Size = new Vector2I(1280, 720);
                BuildBuildDemo(gun);
                return;
            }

            if (server) { BuildServer(); return; }              // headless dedicated server
            if (client) { GetWindow().Size = new Vector2I(1280, 720); BuildClient(); return; }

            if (netdemo)
            {
                GetWindow().Size = new Vector2I(1280, 720);
                BuildNetDemo();
                return;
            }

            if (play || demo)
            {
                // Interactive play fills the screen (maximized). Setting a fixed Size while the project opens
                // MAXIMIZED (window/size/mode=2) left the render boxed in a corner of the big window -- the "tiny
                // viewport" bug. Demo uses a fixed windowed size so --write-movie records a known frame.
                if (demo) { GetWindow().Mode = Window.ModeEnum.Windowed; GetWindow().Size = new Vector2I(1920, 1080); }
                else GetWindow().Mode = Window.ModeEnum.Maximized;
                BuildPlayable(catalog, demo, gun);
                return; // interactive, or demo records via --write-movie
            }

            if (bakeIcon != null)   // render an item model to a flat icon (ItemTool.captureIcon-style) -> --shot=OUT
            {
                _shotPath = shot;
                GetWindow().Size = new Vector2I(256, 256);
                BuildBakeIcon(bakeIcon);
                return; // capture happens a few frames later in _Process
            }

            if (shot != null)
            {
                _shotPath = shot;
                GetWindow().Size = new Vector2I(1280, 720);
                BuildShowcase(catalog, picks);
                return; // capture happens a few frames later in _Process
            }

            if (rig != null)
            {
                _rigDir = rig;
                GetWindow().Size = new Vector2I(900, 1100);
                BuildRigTest(anim);
                return; // frame strip captured in _Process
            }

            if (vm != null)
            {
                _rigDir = vm;                                   // reuse the frame-strip capture
                _rigCaptureFrames = new[] { 10, 66, 89, 92, 95, 120 };  // equip -> ADS -> fire+1 (muzzle flash + tracer) -> reload
                _vmTest = true;
                GetWindow().Size = new Vector2I(2560, 1440);
                BuildViewmodelTest(gun ?? "eaglefire");   // --gun=<name> picks the gun (eaglefire | maplestrike)
                if (_vmAttach) _rigCaptureFrames = new[] { 40, 50, 60, 70, 80, 90 };   // menu open (post-equip) for each frame
                return;
            }

            if (veh != null)
            {
                _rigDir = veh;
                _rigCaptureFrames = new[] { 60, 130, 200, 270, 320, 360 };   // spread across the driving course (also keeps the movie running the full length)
                _vehTest = true;
                GetWindow().Size = new Vector2I(1280, 720);
                BuildVehicleTest(gun ?? "jeep");   // --gun=quad to test the quad
                return;
            }

            if (drivetest != null)
            {
                _rigDir = drivetest;
                _rigCaptureFrames = new[] { 20, 45, 70, 100, 140, 180 };   // walk-up (FP) -> enter -> chase drive
                _driveTest = true;
                GetWindow().Size = new Vector2I(1280, 720);
                BuildDriveTest();
                return;
            }

            if (!smoke)
            {
                // DEFAULT (the exported build): a tiny main menu -> interactive single-player survival. Maximize to
                // FILL the screen (a fixed Size while the project opens MAXIMIZED boxed the render into a corner).
                GetWindow().Mode = Window.ModeEnum.Maximized;
                var menu = new MainMenu();
                menu.OnPlay = noZombies => { menu.QueueFree(); _noZombies = noZombies; BuildPlayable(null, false, null); };
                menu.OnDrivePEI = () => { menu.QueueFree(); _peiPlayable = true; BuildObjectsTest(); };
                AddChild(menu);
                return;
            }

            // --- smoke: ported core runs in-engine ---
            var w = new NetPakWriter { buffer = new byte[64] };
            w.Reset(); w.WriteBits(0xABCu, 12); w.Flush();
            var r = new NetPakReader();
            r.SetBuffer(w.buffer); r.ReadBits(12, out uint got);
            var dict = new DatParser().Parse("Health 55\nName Test_Item");
            var v = new UnityEngine.Vector3(1f, 2f, 3f);
            Godot.Vector3 gv = v.ToGodot();
            GD.Print($"[UnturnedGodot] core live in Godot {Engine.GetVersionInfo()["string"]}: " +
                     $"NetPak 0x{got:X}==0xABC:{got == 0xABCu} | Dat keys={dict.Count} hasHealth={dict.ContainsKey("Health")} | " +
                     $"adapter {v}->{gv}");

            // --- GATE: resolve a real ripped prop by its original Unity GUID ---
            var content = new ContentProvider();
            AddChild(content);
            content.LoadManifest();
            var mesh = content.LoadMesh(GateGuid);
            if (mesh == null) GD.PrintErr($"[GATE] FAILED: could not resolve GUID {GateGuid}");
            else
            {
                AddChild(new MeshInstance3D { Mesh = mesh });
                var aabb = mesh.GetAabb();
                var arrays = mesh.SurfaceGetArrays(0);
                int vcount = arrays.Count > 0 && arrays[(int)Mesh.ArrayType.Vertex].VariantType != Variant.Type.Nil
                    ? ((Vector3[])arrays[(int)Mesh.ArrayType.Vertex]).Length : 0;
                GD.Print($"[GATE] PASS: ContentProvider({content.Count} guid) -> mesh by GUID {GateGuid[..8]}.. " +
                         $"instantiated. verts={vcount} aabb.size=({aabb.Size.X:F3},{aabb.Size.Y:F3},{aabb.Size.Z:F3})");
            }

            // --- optional CATALOG check ---
            if (catalog != null)
            {
                var cat = new ContentProvider();
                AddChild(cat);
                cat.LoadManifest(catalog);
                int tried = 0, ok = 0; long tv = 0, tt = 0;
                foreach (var guid in cat.Guids)
                {
                    if (tried >= 200) break;
                    tried++;
                    var m = cat.LoadMesh(guid);
                    if (m == null || m.GetSurfaceCount() == 0) continue;
                    var vv = (Vector3[])m.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex];
                    if (vv is { Length: > 0 }) { ok++; tv += vv.Length; tt += vv.Length / 3; }
                }
                GD.Print($"[CATALOG] manifest={cat.Count} GUIDs; sampled {tried} -> {ok} loaded OK, {tv} verts / {tt} tris.");
            }

            GetTree().Quit();
        }

        // --rig=DIR : show the real skeletal-animated character playing an Unturned clip,
        // capturing a frame strip across the cycle so the animation is eyeball-verifiable.
        void BuildRigTest(string anim)
        {
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.42f, 0.55f, 0.72f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.55f, 0.57f, 0.6f),
                AmbientLightEnergy = 0.8f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D
            {
                RotationDegrees = new Vector3(-42f, -38f, 0f),
                LightEnergy = 1.25f,
                ShadowEnabled = true,
                LightAngularDistance = 1.6f,            // soft penumbra instead of jagged edges
                DirectionalShadowMaxDistance = 14f,     // concentrate shadow res near the character
                ShadowBias = 0.03f,
                ShadowNormalBias = 1.5f,
                ShadowBlur = 1.4f,
            });
            var ground = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(20f, 20f) } };
            ground.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.28f, 0.30f, 0.28f) };
            AddChild(ground);
            var gbody = new StaticBody3D { CollisionLayer = 1u << 0 };   // ragdoll bones land on this
            gbody.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
            AddChild(gbody);

            var rc = RiggedCharacter.Build("res://content/rig.json", new Color(0.82f, 0.66f, 0.52f));
            if (rc == null) { GD.PrintErr("[rig] build failed"); GetTree().Quit(); return; }
            AddChild(rc);
            _rc = rc;
            GD.Print($"[rig] clips: {string.Join(",", rc.ClipNames)}  playing '{anim}'");
            if (anim == "Ragdoll")
            {
                _ragTest = true;
                _rigCaptureFrames = new[] { 8, 24, 42, 50, 58, 78 };   // collapse, then a corpse-shot impact at f46
                rc.Play("Idle_Stand");
            }
            else
            {
                _rigList = anim.Split(',', System.StringSplitOptions.RemoveEmptyEntries);
                if (_rigList.Length > 1)
                {
                    _rigCaptureFrames = new int[_rigList.Length];
                    for (int i = 0; i < _rigList.Length; i++) _rigCaptureFrames[i] = i * MontageFramesPerClip + MontageFramesPerClip / 2;
                    _rigMontageIdx = 0;
                    rc.Play(_rigList[0]);
                }
                else rc.Play(anim);
            }

            // 3/4 front view, framed on a ~1.9m character
            var cam = new Camera3D { Fov = 42f };
            AddChild(cam);
            cam.LookAtFromPosition(new Vector3(-2.5f, 1.2f, -3.4f), new Vector3(0f, 0.92f, 0f), Vector3.Up);
        }

        // --vm=DIR : render the first-person viewmodel through its own camera (the demo uses a separate cam,
        // so the viewmodel never shows there). Floor + backdrop wall + FP camera + Viewmodel; kick at f20.
        void BuildViewmodelTest(string gunName)
        {
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.42f, 0.55f, 0.72f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.6f, 0.6f, 0.62f),
                AmbientLightEnergy = 0.9f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-45f, -30f, 0f), LightEnergy = 1.1f, ShadowEnabled = true });
            var floor = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(40f, 40f) } };
            floor.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.30f, 0.33f, 0.30f) };
            AddChild(floor);
            var wall = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(24f, 7f, 0.5f) }, Position = new Vector3(0f, 3.5f, -7f) };
            wall.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.52f, 0.46f, 0.40f) };
            AddChild(wall);
            var cam = new Camera3D { Current = true, Fov = 70f, Position = new Vector3(0f, 1.6f, 2f) };
            AddChild(cam);
            _vm = new Viewmodel { GunName = gunName };   // self-contained: own SubViewport camera at FOV 60, composited on top
            AddChild(_vm);
            if (_vmAttach) { _am = new AttachmentMenu(); AddChild(_am); _am.VM = _vm; }   // --attach: show the T menu over the gun
        }

        // --vehicle=DIR : drop the jeep onto a ground plane, chase cam, auto-drive after it settles.
        void BuildVehicleTest(string type)
        {
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = _night ? new Color(0.02f, 0.02f, 0.05f) : new Color(0.42f, 0.55f, 0.72f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = _night ? new Color(0.05f, 0.05f, 0.09f) : new Color(0.6f, 0.6f, 0.62f),
                AmbientLightEnergy = _night ? 0.25f : 0.9f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-50f, -40f, 0f), LightEnergy = _night ? 0.06f : 1.1f, ShadowEnabled = true });

            var ground = new StaticBody3D();
            var gmesh = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(400f, 400f) } };
            gmesh.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.34f, 0.40f, 0.30f) };
            ground.AddChild(gmesh);
            ground.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
            AddChild(ground);

            if (_crash)   // a concrete wall 14m dead ahead to ram (collision-damage demo)
            {
                var wall = new StaticBody3D { CollisionLayer = 1 << 0 };
                var wsz = new Vector3(12f, 4f, 1f);
                wall.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = wsz } });
                wall.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = wsz }, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.55f, 0.57f) } });
                wall.Position = new Vector3(0f, 2f, -22f);   // far enough that the jeep builds up a good ramming speed
                AddChild(wall);
            }

            _veh = Vehicle.BuildByName(type, _vehVariant);
            _veh.Position = new Vector3(0f, 1.2f, 0f);   // drop onto the plane so the suspension settles
            AddChild(_veh);

            if (_roadkill)   // idle zombies straight ahead (-Z) in the auto-drive path to run over
            {
                for (int i = 0; i < 3; i++)
                {
                    var z = new ZombieController { Speciality = ZombieController.ESpeciality.NORMAL };   // Target null -> stands still
                    z.Position = new Vector3(i % 2 == 0 ? -0.6f : 0.6f, 0.9f, -12f - i * 3f);
                    AddChild(z);
                }
            }

            if (_chain)   // a 2nd jeep + a few zombies beside _veh: when _veh blows, the blast chains to the car (500) + wipes the zombies (200)
            {
                CharacterModel.LoadBundled();
                var jeep2 = Vehicle.BuildByName("jeep");
                jeep2.Position = _veh.Position + new Vector3(4f, 0f, 0f);   // ~4 m away, well inside the 8 m blast
                AddChild(jeep2);
                for (int i = 0; i < 3; i++)
                {
                    var z = new ZombieController { Speciality = ZombieController.ESpeciality.NORMAL };   // Target null -> stands still
                    z.Position = _veh.Position + new Vector3(-2f + i * 1.2f, -0.3f, 2.5f);   // clustered near _veh
                    AddChild(z);
                }
            }

            _vehCam = new Camera3D { Current = true, Fov = 60f };
            AddChild(_vehCam);

            _veh.EngineOn = true;                      // engine running -> fuel gauge ticks down
            if (_demo) { _veh.Fuel = _veh.FuelMax * 0.62f; _veh.Health = _veh.HealthMax * 0.85f; _veh.Battery = 4200f; }   // --demo: varied gauge levels (else full/spawn)
            AddChild(new HUD { Vehicle = _veh });       // vehicle status HUD (no Player, so the on-foot HUD stays hidden)
            if (_night) _veh.ToggleHeadlights();        // headlights on for the night demo
        }

        // --drivetest=DIR : a player beside a jeep; scripts entering + driving to verify enter/exit + the chase cam.
        void BuildDriveTest()
        {
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.42f, 0.55f, 0.72f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.6f, 0.6f, 0.62f),
                AmbientLightEnergy = 0.9f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-50f, -40f, 0f), LightEnergy = 1.1f, ShadowEnabled = true });
            var ground = new StaticBody3D();
            var gmesh = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(400f, 400f) } };
            gmesh.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.34f, 0.40f, 0.30f) };
            ground.AddChild(gmesh);
            ground.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
            AddChild(ground);

            var jeep = Vehicle.BuildByName("jeep");
            jeep.GlobalPosition = new Vector3(3f, 1.2f, 0f);
            jeep.AddToGroup("vehicles");
            AddChild(jeep);

            _dtPlayer = new PlayerController { CaptureMouse = false };
            _dtPlayer.LoadGun("res://content/eaglefire.dat");
            AddChild(_dtPlayer);
            _dtPlayer.GlobalPosition = new Vector3(0.8f, 1.0f, 0f);   // right beside the jeep (within enter range)

            if (_swarm)   // zombies lock onto the on-foot player, then keep hunting as he enters the car + swipe it (source targetPassengerVehicle) -> health drops -> smoke -> explode
            {
                CharacterModel.LoadBundled();
                var hud = new HUD { Player = _dtPlayer }; AddChild(hud); _dtPlayer.Hud = hud;   // vehicle health bar shows the drain
                Vector3 pc = _dtPlayer.GlobalPosition;
                for (int i = 0; i < 6; i++)
                {
                    float ang = -1.0f + i * 0.4f;   // front-biased arc so the chase cam catches the mob
                    var z = new ZombieController { Target = _dtPlayer, Speciality = ZombieController.ESpeciality.NORMAL };
                    AddChild(z);
                    z.GlobalPosition = pc + new Vector3(Mathf.Sin(ang) * 6f, 0f, -Mathf.Cos(ang) * 6f);   // ~6 m out (inside the 12 m stand-detect radius)
                    z.LookAt(new Vector3(pc.X, z.GlobalPosition.Y, pc.Z), Vector3.Up);                    // FACE the player so TrySense fires (sneak facing-rule)
                }
            }

            if (_drivethru)   // DRIVING-detection: drive PAST zombies out of on-foot range + facing away -> only the loud car (up to 48 m at speed) can wake them (source DRIVING stealth radius)
            {
                CharacterModel.LoadBundled();
                var hud = new HUD { Player = _dtPlayer }; AddChild(hud); _dtPlayer.Hud = hud;
                foreach (var (sx, sz) in new (float x, float z)[] { (12f, -16f), (-12f, -24f), (12f, -34f), (-12f, -44f) })
                {
                    var z = new ZombieController { Target = _dtPlayer, Speciality = ZombieController.ESpeciality.NORMAL };
                    AddChild(z);
                    z.GlobalPosition = new Vector3(3f + sx, 1.0f, sz);                        // ~12 m to the SIDE of the drive path, far ahead (well beyond the 12 m on-foot radius)
                    z.LookAt(new Vector3(3f + sx * 2f, z.GlobalPosition.Y, sz), Vector3.Up);  // face AWAY from the path -> on-foot facing-rule can't sense them; only the driving alert can
                }
            }

            if (_nade)   // lob a grenade onto the PARKED jeep -> detonates on it -> health drops (source Grenade Vehicle_Damage 100)
            {
                var g = new Grenade { Thrower = _dtPlayer };
                AddChild(g);
                g.GlobalPosition = jeep.GlobalPosition + Vector3.Up * 0.6f;   // resting on the jeep; 2.5s fuse -> boom on the car
            }
        }

        void BuildShowcase(string catalog, string picks)
        {
            // sky-ish background + ambient so unlit grey props read clearly
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.42f, 0.55f, 0.72f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.55f, 0.57f, 0.6f),
                AmbientLightEnergy = 0.7f,
            };
            AddChild(new WorldEnvironment { Environment = env });

            AddChild(new DirectionalLight3D
            {
                RotationDegrees = new Vector3(-52f, -46f, 0f),
                LightEnergy = 1.3f,
                ShadowEnabled = true,
            });

            var ground = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(50f, 50f) } };
            ground.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.28f, 0.30f, 0.28f) };
            AddChild(ground);

            int n = 0;
            if (catalog != null)
            {
                var cp = new ContentProvider();
                AddChild(cp);
                cp.LoadManifest(catalog);
                var texManifest = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(catalog), "texture_manifest.json");
                cp.LoadTextureManifest(texManifest);

                // pick list: named items (recognizable), else a sample of textured props.
                var guids = new System.Collections.Generic.List<string>();
                if (picks != null)
                    foreach (var name in picks.Split(','))
                    {
                        var g = cp.FindGuidByName(name.Trim());
                        if (g != null) guids.Add(g);
                        else GD.Print($"[SHOT] pick not found: {name}");
                    }
                else
                    foreach (var g in cp.TexturedGuids) { guids.Add(g); if (guids.Count >= 10) break; }

                int cols = Mathf.Max(1, Mathf.Min(guids.Count, 5));
                float spacing = 2.6f;
                var greyMat = new StandardMaterial3D { AlbedoColor = new Color(0.78f, 0.74f, 0.68f) };
                int textured = 0;
                foreach (var guid in guids)
                {
                    var mesh = cp.LoadMesh(guid);
                    if (mesh == null || mesh.GetSurfaceCount() == 0) continue;

                    Material mat = greyMat;
                    var texPath = cp.GetTexturePath(guid);
                    if (texPath != null)
                    {
                        var img = Image.LoadFromFile(texPath);
                        if (img != null) { mat = new StandardMaterial3D { AlbedoTexture = ImageTexture.CreateFromImage(img) }; textured++; }
                    }

                    var aabb = mesh.GetAabb();
                    float big = Mathf.Max(aabb.Size.X, Mathf.Max(aabb.Size.Y, aabb.Size.Z));
                    float s = big > 0.001f ? 2.0f / big : 1f; // normalize biggest dim to ~2 m
                    int col = n % cols, row = n / cols;
                    var mi = new MeshInstance3D { Mesh = mesh, MaterialOverride = mat, Scale = new Vector3(s, s, s) };
                    AddChild(mi);
                    mi.Position = new Vector3((col - (cols - 1) / 2f) * spacing, -aabb.Position.Y * s, -row * 3.0f);
                    n++;
                }

                // frame the lineup tightly: close + slightly angled down.
                float width = cols * spacing;
                var cam = new Camera3D { Current = true, Fov = 60f };
                AddChild(cam);
                cam.Position = new Vector3(0f, 1.7f, width * 0.55f + 1.0f);
                cam.LookAt(new Vector3(0f, 1.0f, -0.3f), Vector3.Up);

                GD.Print($"[SHOT] showcase: {n} props ({textured} textured){(picks != null ? " [picked]" : "")}");
            }
        }

        // The playable vertical slice: ground + player (ported movement + hitscan gun) + chasing zombies +
        // HUD. `--play` = interactive; `--demo` = a scripted DemoDirector drives it for a --write-movie clip.
        void BuildPlayable(string catalog, bool demo, string gunPath)
        {
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.42f, 0.55f, 0.72f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.55f, 0.57f, 0.6f),
                AmbientLightEnergy = 0.6f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            var sun = new DirectionalLight3D { RotationDegrees = new Vector3(-52f, -46f, 0f), LightEnergy = 1.2f, ShadowEnabled = true };
            AddChild(sun);
            var dn = new DayNightCycle { Sun = sun, Env = env, DayLength = 300f };   // a 5-minute day/night cycle
            AddChild(dn);
            AddChild(new RainOverlay { Cycle = dn, Raining = GD.Randf() < 0.35f });   // ~a third of runs start rainy

            var ground = new StaticBody3D { CollisionLayer = 1 << 0 };
            ground.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
            var gmesh = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(240, 240) } };
            gmesh.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.30f, 0.34f, 0.28f) };
            ground.AddChild(gmesh);
            AddChild(ground);

            CharacterModel.LoadBundled();  // real ripped character for the zombies
            BuildCrates();                 // bundled ripped-prop scenery

            var player = new PlayerController();
            // load the gun FIRST so the gun name is set before _Ready builds the per-gun viewmodel
            player.LoadGun(gunPath ?? "res://content/eaglefire.dat");
            AddChild(player);                       // _Ready builds its camera + collider + viewmodel
            player.LinkWorldLighting(sun, env);     // FP gun takes the world's day/night lighting
            player.GlobalPosition = new Vector3(0, 1.0f, 0);

            { var hud = new HUD { Player = player }; AddChild(hud); player.Hud = hud; }
            AddChild(new LootSpawner());   // scatter loot to find in the world

            var jeep = Vehicle.BuildByName("jeep");   // a drivable jeep parked nearby -- walk up + press E to get in
            jeep.GlobalPosition = new Vector3(7f, 1.5f, 4f);
            jeep.AddToGroup("vehicles");
            AddChild(jeep);

            if (demo)
            {
                player.Camera.Current = false;
                var overview = new Camera3D { Current = true, Fov = 62f };
                AddChild(overview);
                overview.Position = new Vector3(8f, 3.6f, 8f);
                overview.LookAt(new Vector3(0, 1.0f, -4f), Vector3.Up);
                AddChild(new DemoDirector { Player = player, SpawnRoot = this });
                GD.Print("[PLAY] demo: player + scripted director vs chasing zombies (recording)");
            }
            else
            {
                if (!_noZombies) AddChild(new HordeSpawner { Target = player });
                var pause = new PauseMenu();   // ESC -> live viewmodel FOV/offset tuning sliders
                AddChild(pause);
                player.PauseMenu = pause;
                var attach = new AttachmentMenu();   // T -> weapon-attachment menu (iron sights removable, etc.)
                AddChild(attach);
                player.AttachMenu = attach;
                GD.Print(_noZombies ? "[PLAY] interactive: NO-ZOMBIE test environment"
                                    : "[PLAY] interactive: WASD move / mouse look / LMB fire / Space jump");
            }
        }

        // First-person hurt-feedback demo: keep the player's own camera current and drop a zombie point-blank in front
        // so it lands hits — the red flash (HUD overlay) and the camera flinch ride the FP view for a --write-movie clip.
        void BuildHurtDemo(string gunPath)
        {
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.42f, 0.55f, 0.72f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.55f, 0.57f, 0.6f),
                AmbientLightEnergy = 0.6f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-52f, -46f, 0f), LightEnergy = 1.2f, ShadowEnabled = true });

            var ground = new StaticBody3D { CollisionLayer = 1 << 0 };
            ground.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
            var gmesh = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(240, 240) } };
            gmesh.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.30f, 0.34f, 0.28f) };
            ground.AddChild(gmesh);
            AddChild(ground);

            CharacterModel.LoadBundled();

            var player = new PlayerController();
            player.LoadGun(gunPath ?? "res://content/eaglefire.dat");
            AddChild(player);                       // _Ready builds the FP camera (stays Current) + viewmodel
            player.GlobalPosition = new Vector3(0, 1.0f, 0);
            { var hud = new HUD { Player = player }; AddChild(hud); player.Hud = hud; }

            // a normal zombie 1.2 m dead ahead (-Z): inside ATTACK_PLAYER_SQ, so it startles then bites on its cadence
            var z = new ZombieController { Target = player, Speciality = ZombieController.ESpeciality.NORMAL };
            AddChild(z);
            z.GlobalPosition = player.GlobalPosition + new Vector3(0f, 0.2f, -1.2f);
            // face it at the player so TrySense fires -- otherwise the source's sneak-from-behind rule (a standing player
            // behind the zombie's facing goes undetected) leaves it oblivious to a point-blank spawn
            z.LookAt(new Vector3(player.GlobalPosition.X, z.GlobalPosition.Y, player.GlobalPosition.Z), Vector3.Up);
            GD.Print("[HURT] first-person: zombie point-blank, recording flash + flinch");
        }

        // --firetest [--supp]: the player fires AWAY from a zombie 25 m off. The zombie is out of its 12 m stand-detect
        // radius (won't sense the player), but inside the 48 m gunshot alert -> it should hear an UNsuppressed shot and
        // print [ALERT]; with a suppressor attached the shot is silent (source UseableGun ~936) -> no [ALERT]. Behavioral
        // proof of the suppressor effect (+ a reusable firing-mechanics harness).
        void BuildFireTest(bool suppressed)
        {
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.42f, 0.55f, 0.72f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.55f, 0.57f, 0.6f),
                AmbientLightEnergy = 0.6f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-52f, -46f, 0f), LightEnergy = 1.2f, ShadowEnabled = true });

            var ground = new StaticBody3D { CollisionLayer = 1 << 0 };
            ground.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
            var gmesh = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(240, 240) } };
            gmesh.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.30f, 0.34f, 0.28f) };
            ground.AddChild(gmesh);
            AddChild(ground);

            CharacterModel.LoadBundled();

            var player = new PlayerController();
            player.LoadGun("res://content/eaglefire.dat");
            AddChild(player);
            player.GlobalPosition = new Vector3(0, 1.0f, 0);
            player.RotationDegrees = new Vector3(0, 180f, 0);   // face +Z, AWAY from the zombie (only the shot NOISE should reach it, not the bullet)
            { var hud = new HUD { Player = player }; AddChild(hud); player.Hud = hud; }
            _ftPlayer = player;
            if (suppressed) player.SetSuppressor(true);

            var z = new ZombieController { Target = player, Speciality = ZombieController.ESpeciality.NORMAL };
            AddChild(z);
            z.GlobalPosition = new Vector3(0, 1.0f, -25f);
            GD.Print($"[FIRETEST] suppressed={suppressed} -- firing away from a zombie 25 m off; expect [ALERT] ONLY when unsuppressed");
        }

        // --terrain: load PEI's Landscape Tile_0_0 heightmap into a Godot terrain mesh (the first real WORLD step; replaces
        // the flat test-plane). Aerial camera over the 1024 m tile so the real terrain shape is visible.
        void BuildTerrainTest()
        {
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.5f, 0.6f, 0.75f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.6f, 0.6f, 0.62f),
                AmbientLightEnergy = 0.8f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-45f, -55f, 0f), LightEnergy = 1.15f, ShadowEnabled = true });

            AddChild(Terrain.LoadMapMerged(@"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Maps\PEI\Landscape\Heightmaps", withCollider: false));

            var cam = new Camera3D { Current = true, Fov = 55f, Far = 16000f };
            AddChild(cam);
            cam.Position = new Vector3(0f, 5200f, 1f);
            cam.LookAt(Vector3.Zero, new Vector3(0f, 0f, -1f));   // STRAIGHT TOP-DOWN; screen-up = world -Z (= Unity +Z = north) to match the map chart's orientation
            GD.Print("[TERRAIN] loaded the whole PEI island (merged, seamless)");
        }

        // --proptest=NAME diagnostic: one prop at identity with RGB axis refs (X=red +right, Y=green +up, Z=blue +back)
        // so I can read its orientation/chirality up close and spot a mirror vs the real game.
        void BuildPropTest(string name)
        {
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.32f, 0.36f, 0.44f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.7f, 0.7f, 0.72f), AmbientLightEnergy = 0.9f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-45f, -35f, 0f), LightEnergy = 1.2f });
            string dir = ProjectSettings.GlobalizePath("res://content/objects/");
            var mesh = ObjMesh.Load(dir + name + ".obj");
            if (mesh == null) { GD.Print($"[PROPTEST] no mesh {name}"); GetTree().Quit(); return; }
            var mat = new StandardMaterial3D { Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            string tp = dir + name + "_tex.png";
            if (System.IO.File.Exists(tp)) { var img = new Image(); if (img.Load(tp) == Error.Ok) { img.GenerateMipmaps(); mat.AlbedoTexture = ImageTexture.CreateFromImage(img); } }
            AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = mat });
            var aabb = mesh.GetAabb(); var c = aabb.GetCenter(); float r = Mathf.Max(aabb.Size.X, Mathf.Max(aabb.Size.Y, aabb.Size.Z));
            if (r < 0.01f) r = 1f;
            foreach (var (ax, col) in new[] { (Vector3.Right, new Color(1f, 0.15f, 0.15f)), (Vector3.Up, new Color(0.15f, 1f, 0.15f)), (Vector3.Back, new Color(0.2f, 0.4f, 1f)) })
            {
                var bar = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.06f, 0.06f, 0.06f) * r + ax.Abs() * r * 1.2f }, MaterialOverride = new StandardMaterial3D { AlbedoColor = col } };
                bar.Position = ax * r * 0.6f;
                AddChild(bar);
            }
            var cam = new Camera3D { Current = true, Fov = 50f, Far = 10000f };
            AddChild(cam);
            cam.Position = c + new Vector3(r * 1.15f, r * 0.85f, r * 1.15f);
            cam.LookAt(c, Vector3.Up);
            GD.Print($"[PROPTEST] {name} aabb size={aabb.Size} center={c}");
        }

        // --objects: PEI's real placed objects (Level/Objects.dat) instanced on the terrain. placements.txt = every
        // object's guid+transform; guid_mesh.txt maps the top types to extracted object.prefab meshes.
        void BuildObjectsTest()
        {
            float F(string s) => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.5f, 0.6f, 0.75f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.6f, 0.6f, 0.62f),
                AmbientLightEnergy = 0.85f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-48f, -50f, 0f), LightEnergy = 1.15f, ShadowEnabled = true });
            var terr = Terrain.LoadMapMerged(@"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Maps\PEI\Landscape\Heightmaps", withCollider: true);
            AddChild(terr);

            string dir = ProjectSettings.GlobalizePath("res://content/objects/");
            var g2m = new System.Collections.Generic.Dictionary<string, string>();
            foreach (var line in System.IO.File.ReadLines(dir + "guid_mesh.txt"))
            {
                var p = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (p.Length >= 2) g2m[p[0]] = p[1];
            }
            var cache = new System.Collections.Generic.Dictionary<string, ArrayMesh>();
            var folCache = new System.Collections.Generic.Dictionary<string, ArrayMesh>();   // separate tree-leaf meshes (own leaf material)
            var shapeCache = new System.Collections.Generic.Dictionary<string, ConcavePolygonShape3D>();   // one trimesh collider per unique prop mesh, shared across instances
            var matCache = new System.Collections.Generic.Dictionary<string, StandardMaterial3D>();
            StandardMaterial3D MatFor(string nm)
            {
                if (matCache.TryGetValue(nm, out var mm)) return mm;
                mm = new StandardMaterial3D { Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
                string tp = dir + nm + "_tex.png";
                if (System.IO.File.Exists(tp))
                {
                    var img = new Image();
                    if (img.Load(tp) == Error.Ok)
                    {
                        // leaf/foliage cutout: if the albedo carries real transparency (>1% of texels), alpha-scissor it
                        if (img.GetFormat() == Image.Format.Rgba8)
                        {
                            var data = img.GetData(); int tr = 0;
                            for (int i = 3; i < data.Length; i += 4) if (data[i] < 200) tr++;
                            if (tr > data.Length / 400) { mm.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor; mm.AlphaScissorThreshold = 0.5f; }
                        }
                        img.GenerateMipmaps();
                        mm.AlbedoTexture = ImageTexture.CreateFromImage(img);
                    }
                    else mm.AlbedoColor = new Color(0.60f, 0.55f, 0.47f);
                }
                else mm.AlbedoColor = new Color(0.60f, 0.55f, 0.47f);
                matCache[nm] = mm;
                return mm;
            }
            var cellCount = new System.Collections.Generic.Dictionary<Vector2I, int>();
            var cellSum = new System.Collections.Generic.Dictionary<Vector2I, Vector3>();
            Vector2I bestCell = Vector2I.Zero; int bestN = 0; int placed = 0;
            foreach (var line in System.IO.File.ReadLines(dir + "placements.txt"))
            {
                var p = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 10 || !g2m.TryGetValue(p[0], out var name)) continue;
                if (!cache.TryGetValue(name, out var mesh)) { mesh = ObjMesh.Load(dir + name + ".obj"); cache[name] = mesh; }
                if (mesh == null) continue;
                float px = F(p[1]), py = F(p[2]), pz = F(p[3]), ex = F(p[4]), ey = F(p[5]), ez = F(p[6]), sx = F(p[7]), sy = F(p[8]), sz = F(p[9]);
                var gpos = new Vector3(px, py, -pz);
                // negate-Z LAYOUT (keeps the map orientation) with a RAW mesh (un-mirrored geometry): rotation = old C_z-conjugated euler
                var rot = new Basis(new Vector3(0, 1, 0), Mathf.DegToRad(-ey)) * new Basis(new Vector3(1, 0, 0), Mathf.DegToRad(-ex)) * new Basis(new Vector3(0, 0, 1), Mathf.DegToRad(ez));
                var basis = rot.Scaled(new Vector3(sx, sy, sz));
                AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = MatFor(name), Transform = new Transform3D(basis, gpos) });
                // tree foliage: a SEPARATE leaf mesh with its own leaf material (so the trunk keeps its bark texture)
                if (!folCache.TryGetValue(name, out var fmesh))
                {
                    string fp = dir + name + "_foliage.obj";
                    fmesh = System.IO.File.Exists(fp) ? ObjMesh.Load(fp) : null;
                    folCache[name] = fmesh;
                }
                if (fmesh != null) AddChild(new MeshInstance3D { Mesh = fmesh, MaterialOverride = MatFor(name + "_foliage"), Transform = new Transform3D(basis, gpos) });
                if (_peiPlayable)   // walkable collision: trimesh of the VISUAL mesh (trees collide on the trunk only; the separate leaf mesh has no collider, so you walk through foliage)
                {
                    if (!shapeCache.TryGetValue(name, out var shp)) { shp = mesh.CreateTrimeshShape(); shapeCache[name] = shp; }
                    if (shp != null) { var body = new StaticBody3D { Transform = new Transform3D(basis, gpos) }; body.AddChild(new CollisionShape3D { Shape = shp }); AddChild(body); }
                }
                placed++;
                var cell = new Vector2I(Mathf.FloorToInt(px / 96f), Mathf.FloorToInt(pz / 96f));
                cellCount.TryGetValue(cell, out int cc); cellCount[cell] = cc + 1;
                cellSum.TryGetValue(cell, out Vector3 cs); cellSum[cell] = cs + gpos;
                if (cc + 1 > bestN) { bestN = cc + 1; bestCell = cell; }
            }
            var focus = placed > 0 ? cellSum[bestCell] / bestN : Vector3.Zero;
            GD.Print($"[OBJECTS] placed {placed} objects ({cache.Count} meshes); densest cluster {bestN} near {focus}");

            // aerial over the busiest cluster so the full populated PEI (all ~360 types) reads at once, no gaps
            if (_peiPlayable)
            {
                // menu "Drive PEI": drop the player + jeep on open grass with REAL controls (WASD + mouse look, E to enter/drive the jeep)
                float sx = 0f, sz = -350f, spawnYaw = 0f;
                // player spawn: PEI's REAL regular spawn points (Spawns/Players.dat = u8 ver, u8 count, per point Vector3 + u8 angle*2 + bool isAlt if v>3;
                // source LevelPlayers.getSpawn picks a random NON-alt spawn). Falls back to the inland-grass scan if the file's missing.
                bool gotSpawn = false;
                {
                    string ppath = @"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Maps\PEI\Spawns\Players.dat";
                    if (System.IO.File.Exists(ppath))
                    {
                        var pd = System.IO.File.ReadAllBytes(ppath); int pp = 0;
                        byte pver = pd[pp++]; byte pcount = pd[pp++];
                        var regs = new System.Collections.Generic.List<(float x, float z, float yaw)>();
                        for (int i = 0; i < pcount; i++)
                        {
                            float px = System.BitConverter.ToSingle(pd, pp); pp += 8;   // point.x (skip point.y)
                            float pz = System.BitConverter.ToSingle(pd, pp); pp += 4;   // point.z
                            float pang = pd[pp++] * 2f;
                            bool isAlt = pver > 3 && pd[pp++] != 0;
                            if (!isAlt) regs.Add((px, -pz, -pang));   // regular spawn -> port negate-Z, negate yaw
                        }
                        if (regs.Count > 0) { var pick = regs[new RandomNumberGenerator { Seed = 7 }.RandiRange(0, regs.Count - 1)]; sx = pick.x; sz = pick.z; spawnYaw = pick.yaw; gotSpawn = true; }
                    }
                }
                if (!gotSpawn)   // fallback: most-inland grass
                {
                    int bestMargin = -1; float bestDist = float.MaxValue;
                    for (float cz = -1800f; cz <= 1800f; cz += 50f)
                        for (float cx = -1800f; cx <= 1800f; cx += 50f)
                        {
                            if (terr.SampleDominantLayer(cx, cz) != 2) continue;
                            int margin = 0;
                            for (int r = 32; r <= 160; r += 32)
                            {
                                bool ring = true;
                                for (int a = 0; a < 8 && ring; a++)
                                    if (Terrain.IsWater(terr.SampleDominantLayer(cx + r * Mathf.Cos(a * Mathf.Pi / 4f), cz + r * Mathf.Sin(a * Mathf.Pi / 4f)))) ring = false;
                                if (!ring) break;
                                margin = r;
                            }
                            float dist = Mathf.Sqrt(cx * cx + cz * cz);
                            if (margin > bestMargin || (margin == bestMargin && dist < bestDist)) { bestMargin = margin; bestDist = dist; sx = cx; sz = cz; }
                        }
                }
                if (System.Environment.GetEnvironmentVariable("UG_TOWNSPAWN") == "1") { sx = focus.X; sz = focus.Z; }   // demo: spawn in the busiest town (near zombies) instead of open grass
                CharacterModel.LoadBundled();
                var player = new PlayerController { CaptureMouse = true };
                player.LoadGun("res://content/eaglefire.dat");
                AddChild(player);
                player.GlobalPosition = new Vector3(sx, terr.SampleHeight(sx, sz) + 3f, sz);
                player.RotationDegrees = new Vector3(0f, spawnYaw, 0f);   // face the spawn point's angle
                player.Spawn = player.GlobalPosition;   // respawn on this above-ground point, NOT the default (0,1,0) which is underground on PEI
                if (System.Environment.GetEnvironmentVariable("UG_OOBTEST") == "1") player.GlobalPosition = new Vector3(sx, -2000f, sz);   // test hook: drop below the map -> should trip the OOB kill
                { var hud = new HUD { Player = player }; AddChild(hud); player.Hud = hud; }
                var jeep = Vehicle.BuildByName("jeep");
                AddChild(jeep);
                jeep.GlobalPosition = new Vector3(sx + 2.2f, terr.SampleHeight(sx + 2.2f, sz) + 1.5f, sz);

                // ZOMBIE SPAWNS: PEI's navmesh regions from Environment/Bounds.dat (source LevelNavigation: River =
                // u8 version, u8 count, then count x [center Vector3][size Vector3]). Drop a few zombies per region on
                // LAND at terrain height; the radius-gated AI keeps far ones idle until you approach (source-accurate sensing).
                {
                    string bpath = @"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Maps\PEI\Environment\Bounds.dat";
                    int nz = 0;
                    if (System.IO.File.Exists(bpath))
                    {
                        var bd = System.IO.File.ReadAllBytes(bpath); int o = 0;
                        byte bver = bd[o++];
                        if (bver > 0)
                        {
                            byte bcount = bd[o++];
                            var rng = new RandomNumberGenerator { Seed = 90210 };
                            for (int bi = 0; bi < bcount; bi++)
                            {
                                float cx = System.BitConverter.ToSingle(bd, o); o += 8;   // center.x (skip center.y)
                                float cz = System.BitConverter.ToSingle(bd, o); o += 4;   // center.z
                                float bsx = System.BitConverter.ToSingle(bd, o); o += 8;  // size.x (skip size.y)
                                float bsz = System.BitConverter.ToSingle(bd, o); o += 4;  // size.z
                                for (int k = 0; k < 4; k++)
                                {
                                    float wx = cx + rng.RandfRange(-bsx * 0.5f, bsx * 0.5f);
                                    float gz = -(cz + rng.RandfRange(-bsz * 0.5f, bsz * 0.5f));   // Unity Z -> port negate-Z
                                    if (Terrain.IsWater(terr.SampleDominantLayer(wx, gz))) continue;   // no zombies in the ocean
                                    var z = new ZombieController { Target = player, Speciality = ZombieController.ESpeciality.NORMAL };
                                    AddChild(z);
                                    z.GlobalPosition = new Vector3(wx, terr.SampleHeight(wx, gz) + 1f, gz);
                                    if (!_zHave) { _zAim = z.GlobalPosition; _zHave = true; }   // remember the first zombie so the demo cam can look at it
                                    nz++;
                                }
                            }
                        }
                    }
                    GD.Print($"[zombies] spawned {nz} across PEI's navmesh regions");
                }

                // VEHICLE SPAWNS: Spawns/Vehicles.dat (source LevelVehicles River: u8 ver, [SteamID if 1<v<3], u8 tableCount,
                // per table [color 3B, name str, tableID u16 if v>3, u8 tierCount, per tier: name str, chance f32, u8 spawnCount, per spawn u16],
                // u16 pointCount, per point: u8 type, Vector3, u8 angle*2). Place a vehicle per LAND spawn (car tables 0-5); jeep placeholder for all.
                {
                    string vpath = @"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Maps\PEI\Spawns\Vehicles.dat";
                    int nv = 0;
                    if (System.IO.File.Exists(vpath))
                    {
                        var vd = System.IO.File.ReadAllBytes(vpath); int vp = 0;
                        byte U8() => vd[vp++];
                        ushort U16() { var v = System.BitConverter.ToUInt16(vd, vp); vp += 2; return v; }
                        float F32() { var v = System.BitConverter.ToSingle(vd, vp); vp += 4; return v; }
                        void RStr() { int n = U8(); vp += n; }
                        byte ver = U8();
                        if (ver > 1 && ver < 3) vp += 8;   // SteamID
                        byte tcount = U8();
                        for (int t = 0; t < tcount; t++)
                        {
                            vp += 3; RStr();                 // color + table name
                            if (ver > 3) vp += 2;            // tableID
                            byte tiers = U8();
                            for (int ti = 0; ti < tiers; ti++) { RStr(); vp += 4; byte sc = U8(); vp += sc * 2; }
                        }
                        ushort pcount = U16();
                        for (int i = 0; i < pcount; i++)
                        {
                            byte type = U8();
                            float px = F32(); vp += 4; float pz = F32();   // point.x, skip point.y, point.z
                            float ang = U8() * 2f;
                            if (type > 5) continue;          // skip air/boat/tank (6-11) until those models exist
                            float gz = -pz;                  // Unity Z -> port negate-Z
                            var vpos = new Vector3(px, terr.SampleHeight(px, gz) + 1.2f, gz);
                            var vyaw = new Basis(Vector3.Up, Mathf.DegToRad(-ang));   // spawn yaw (negate for negate-Z)
                            string vn = type == 1 ? "Veh_Police" : type == 2 ? "Veh_Firetruck" : type == 4 ? "Veh_Ambulance" : null;
                            if (vn != null)   // real static vehicle mesh (Police / Firetruck / Ambulance) + collider
                            {
                                if (!cache.TryGetValue(vn, out var vm)) { vm = ObjMesh.Load(dir + vn + ".obj"); cache[vn] = vm; }
                                if (vm != null)
                                {
                                    var rpos = new Vector3(px, terr.SampleHeight(px, gz) - vm.GetAabb().Position.Y, gz);   // sit the mesh's bottom on the terrain
                                    AddChild(new MeshInstance3D { Mesh = vm, MaterialOverride = MatFor(vn), Transform = new Transform3D(vyaw, rpos) });
                                    if (!shapeCache.TryGetValue(vn, out var vs)) { vs = vm.CreateTrimeshShape(); shapeCache[vn] = vs; }
                                    if (vs != null) { var vb = new StaticBody3D { Transform = new Transform3D(vyaw, rpos) }; vb.AddChild(new CollisionShape3D { Shape = vs }); AddChild(vb); }
                                    if (!_vHave) { _vAim = rpos; _vHave = true; }
                                    nv++;
                                }
                            }
                            else   // Civilian / Military / Farm -> drivable jeep placeholder
                            {
                                var veh = Vehicle.BuildByName("jeep");
                                AddChild(veh);
                                veh.GlobalPosition = vpos;
                                veh.RotationDegrees = new Vector3(0f, -ang, 0f);
                                nv++;
                            }
                        }
                    }
                    GD.Print($"[vehicles] spawned {nv} at PEI's car spawns (Police/Fire/Ambulance real meshes; jeep for Civilian/Military/Farm)");
                }
                // LOOT: PEI's 2470 item spawn points (Spawns/Jars.dat), region/distance-streamed around the player (LootField).
                {
                    var loot = new LootField { Player = player, Terr = terr };
                    loot.LoadFromPei(@"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Maps\PEI");
                    AddChild(loot);
                }
                if (System.Environment.GetEnvironmentVariable("UG_ZAERIAL") == "1")   // demo cam: look down on the spawn town so the zombies are visible
                {
                    var acam = new Camera3D { Current = true, Fov = 62f, Far = 20000f };
                    AddChild(acam);
                    var ctr = _vHave ? _vAim : player.GlobalPosition;   // prefer a real vehicle; else the spawn town
                    acam.Position = ctr + (_vHave ? new Vector3(0f, 9f, 11f) : new Vector3(0f, 50f, 44f));
                    acam.LookAt(ctr, Vector3.Up);
                }
                GetWindow().Mode = Window.ModeEnum.Maximized;
                GD.Print($"[PEI] playable: spawned on grass ({sx:0},{sz:0}); WASD move, E enter jeep, drive PEI");
            }
            else
            {
                Vector3 sumAll = Vector3.Zero; foreach (var v in cellSum.Values) sumAll += v;
                var ctr = placed > 0 ? sumAll / placed : Vector3.Zero;
                var cam = new Camera3D { Current = true, Fov = 55f, Far = 20000f };
                AddChild(cam);
                cam.Position = new Vector3(ctr.X, 2200f, ctr.Z + 1f);
                cam.LookAt(new Vector3(ctr.X, 0f, ctr.Z), new Vector3(0f, 0f, -1f));   // straight down, screen-up = world -Z (north), to match the game chart
            }
        }

        // --peiplay: drop the player onto REAL PEI terrain (colliders on), spawned on land via SampleHeight, scripted to walk.
        void BuildPeiPlay()
        {
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.5f, 0.6f, 0.75f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.6f, 0.6f, 0.62f),
                AmbientLightEnergy = 0.75f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-48f, -50f, 0f), LightEnergy = 1.15f, ShadowEnabled = true });

            var terr = Terrain.LoadMapMerged(@"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Maps\PEI\Landscape\Heightmaps", withCollider: true);
            AddChild(terr);

            CharacterModel.LoadBundled();
            var player = new PlayerController();
            player.LoadGun("res://content/eaglefire.dat");
            AddChild(player);
            // auto-pick a grassy, well-inland spawn so the jeep drives on real green PEI land, not the coastal water-splat
            float sx = 0f, sz = -350f; int bestMargin = -1; float bestDist = float.MaxValue;
            for (float cz = -1800f; cz <= 1800f; cz += 50f)
                for (float cx = -1800f; cx <= 1800f; cx += 50f)
                {
                    if (terr.SampleDominantLayer(cx, cz) != 2) continue;   // grass underfoot
                    int margin = 0;
                    for (int r = 32; r <= 160; r += 32)   // how far land extends in all 8 directions (driving room)
                    {
                        bool ring = true;
                        for (int a = 0; a < 8 && ring; a++)
                            if (Terrain.IsWater(terr.SampleDominantLayer(cx + r * Mathf.Cos(a * Mathf.Pi / 4f), cz + r * Mathf.Sin(a * Mathf.Pi / 4f)))) ring = false;
                        if (!ring) break;
                        margin = r;
                    }
                    float dist = Mathf.Sqrt(cx * cx + cz * cz);
                    if (margin > bestMargin || (margin == bestMargin && dist < bestDist)) { bestMargin = margin; bestDist = dist; sx = cx; sz = cz; }
                }
            float gy = terr.SampleHeight(sx, sz);
            player.GlobalPosition = new Vector3(sx, gy + 3f, sz);   // drop 3 m onto the real ground
            player.Spawn = player.GlobalPosition;   // respawn above ground, NOT the default (0,1,0) which is underground on PEI
            { var hud = new HUD { Player = player }; AddChild(hud); player.Hud = hud; }
            _peiPlayer = player;

            // a jeep right beside the player, dropped onto the terrain -> hop in + drive PEI
            var jeep = Vehicle.BuildByName("jeep");   // auto-joins the "vehicles" group in Build
            AddChild(jeep);                            // in the tree FIRST, else GlobalPosition no-ops (!is_inside_tree)
            float jjx = sx + 2.2f;
            jeep.GlobalPosition = new Vector3(jjx, terr.SampleHeight(jjx, sz) + 1.5f, sz);

            GD.Print($"[PEIPLAY] grass spawn ({sx:0},{sz:0}) groundY {gy:0}, inland-margin {bestMargin}m, layer {terr.SampleDominantLayer(sx, sz)} + jeep beside");

            if (_peiHorde)   // a zombie field the jeep drives into -> the loud engine aggros them (source 48*speed), roadkill + swarm on real PEI
            {
                const int N = 18;
                for (int i = 0; i < N; i++)
                {
                    float ang = i * 2.39996f;                          // golden-angle scatter -> even disc fill
                    float r = 9f + 26f * (i / (float)N);               // 9..35 m filled disc around the spawn (jeep plows through the forward slice)
                    float zx = sx + r * Mathf.Cos(ang), zz = sz + r * Mathf.Sin(ang);
                    var z = new ZombieController { Target = player, Speciality = ZombieController.ESpeciality.NORMAL };
                    AddChild(z);                                       // in the tree first, else GlobalPosition no-ops
                    z.GlobalPosition = new Vector3(zx, terr.SampleHeight(zx, zz) + 1.5f, zz);
                }
                GD.Print($"[PEIPLAY] +{N} zombies scattered around the jeep (loud drive aggros -> roadkill + swarm)");
            }
        }

        // Headless self-test of PlayerInventory.TryDrag (the ported move/swap): asserts move-to-empty, out-of-bounds
        // rejection, and drop-onto-item swap, printing PASS/FAIL per case. Verifies the drag MODEL without the mouse.
        static void RunDragTest()
        {
            SDG.Unturned.ItemCatalog.RegisterAll();
            var inv = new SDG.Unturned.PlayerInventory();
            var pk = inv.items[2];                              // pockets 5x3
            pk.tryAddItem(new SDG.Unturned.Item(15));          // Medkit 2x2 -> (0,0)
            pk.tryAddItem(new SDG.Unturned.Item(14));          // Bottled Water 1x1 -> (2,0)
            pk.tryAddItem(new SDG.Unturned.Item(95));          // Bandage 1x1 -> (3,0)
            int pass = 0, fail = 0;
            void Check(string n, bool ok) { if (ok) { pass++; GD.Print($"[DRAGTEST] PASS  {n}"); } else { fail++; GD.Print($"[DRAGTEST] FAIL  {n}"); } }

            // 1. move Water (2,0) -> empty (4,2)
            Check("move-to-empty returns true", inv.TryDrag(2, 2, 0, 2, 4, 2, 0));
            Check("water now at (4,2)", pk.getItem(4, 2)?.item.id == 14);
            Check("old cell (2,0) freed", pk.getItem(2, 0) == null);

            // 2. out-of-bounds target rejected, no change
            Check("OOB move returns false", !inv.TryDrag(2, 4, 2, 2, 10, 10, 0));
            Check("water still at (4,2)", pk.getItem(4, 2)?.item.id == 14);

            // 3. drop Water (4,2) onto Bandage (3,0) -> swap
            Check("swap returns true", inv.TryDrag(2, 4, 2, 2, 3, 0, 0));
            Check("water swapped to (3,0)", pk.getItem(3, 0)?.item.id == 14);
            Check("bandage swapped to (4,2)", pk.getItem(4, 2)?.item.id == 95);

            // 4. cross-page move Water (3,0) -> backpack, after wearing a bag
            inv.wearBackpack(new SDG.Unturned.Item(253));      // 8x7
            Check("cross-page move returns true", inv.TryDrag(2, 3, 0, SDG.Unturned.PlayerInventory.BACKPACK, 5, 5, 0));
            Check("water now in backpack (5,5)", inv.items[SDG.Unturned.PlayerInventory.BACKPACK].getItem(5, 5)?.item.id == 14);
            Check("water gone from pockets", pk.getItem(3, 0) == null);

            GD.Print($"[DRAGTEST] RESULT {pass} passed, {fail} failed");
        }

        // Headless self-test of PlayerController.Consume (the consumable effects). Asserts the real .dat values land
        // on the vitals: Medkit +75 hp + stops bleeding, Canned Beans +food, Bottled Water +water.
        static void RunUseTest()
        {
            SDG.Unturned.ItemCatalog.RegisterAll();
            var p = new PlayerController { Health = 20f, Food = 0.1f, Water = 0.1f, Bleeding = true };
            int pass = 0, fail = 0;
            void Check(string n, bool ok) { if (ok) { pass++; GD.Print($"[USETEST] PASS  {n}"); } else { fail++; GD.Print($"[USETEST] FAIL  {n}"); } }

            p.Consume(SDG.Unturned.Assets.find(15));   // Medkit: +75 health, stop bleeding
            Check("medkit -> health 20+75=95", Mathf.Abs(p.Health - 95f) < 0.01f);
            Check("medkit -> bleeding cleared", !p.Bleeding);
            p.Consume(SDG.Unturned.Assets.find(13));   // Canned Beans: +10 health, +55 food
            Check("beans -> food 0.1+0.55=0.65", Mathf.Abs(p.Food - 0.65f) < 0.01f);
            Check("beans -> health capped at 100", Mathf.Abs(p.Health - 100f) < 0.01f);   // 95+10 -> clamp 100
            p.Consume(SDG.Unturned.Assets.find(14));   // Bottled Water: +55 water
            Check("water -> water 0.1+0.55=0.65", Mathf.Abs(p.Water - 0.65f) < 0.01f);

            GD.Print($"[USETEST] RESULT {pass} passed, {fail} failed");
        }

        // Melee self-test: a NORMAL zombie (100 HP) stands ~1.4 m ahead; a driver swings the player's melee every
        // frame (cooldown gates it to ~0.45 s, 45 dmg). Log-verifiable headless -- expect three [melee] hits then a
        // kill. Proximity-based, so unlike the fast-bullet raycast it registers reliably.
        void BuildMeleeDemo(string gunPath)
        {
            var ground = new StaticBody3D { CollisionLayer = 1 << 0 };
            ground.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
            AddChild(ground);

            var player = new PlayerController { CaptureMouse = false };
            player.LoadGun(gunPath ?? "res://content/eaglefire.dat");
            AddChild(player);                       // _Ready builds the FP camera used to aim the swing
            player.GlobalPosition = new Vector3(0, 1.0f, 0);

            var z = new ZombieController { Target = player, Speciality = ZombieController.ESpeciality.NORMAL };
            AddChild(z);
            z.GlobalPosition = player.GlobalPosition + new Vector3(0f, 0.2f, -1.4f);   // dead ahead, in reach

            AddChild(new MeleeTestDriver { P = player });
            GD.Print("[MELEE] demo: NORMAL zombie ~1.4m ahead (100 HP); swinging (45/hit, ~0.45s cd)");
        }

        // Fall-damage self-test: drop the player from 40 m onto the ground plane. Expect PlayerLife.onLanded to fire
        // on the landing frame (impact speed well over the 22 m/s threshold) and cut health. Log-verifiable headless.
        void BuildFallDemo(string gunPath)
        {
            var ground = new StaticBody3D { CollisionLayer = 1 << 0 };
            ground.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
            AddChild(ground);

            var player = new PlayerController { CaptureMouse = false };
            player.LoadGun(gunPath ?? "res://content/eaglefire.dat");
            AddChild(player);
            player.GlobalPosition = new Vector3(0, 40f, 0);   // 40 m up -> a hard landing

            AddChild(new FallTestDriver { P = player });
            GD.Print("[FALL] demo: player dropped from 40 m; expect fall damage on landing");
        }

        // Stance self-test: force each stance via ScriptedStance and read GetStealthDetectionRadius (the value the
        // zombie AI senses the player by). Verifies the new PRONE case (DETECT_PRONE=3) + regresses the others.
        void BuildProneTest(string gunPath)
        {
            var ground = new StaticBody3D { CollisionLayer = 1 << 0 };
            ground.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
            AddChild(ground);

            var player = new PlayerController { CaptureMouse = false };
            player.LoadGun(gunPath ?? "res://content/eaglefire.dat");
            AddChild(player);
            player.GlobalPosition = new Vector3(0, 1.0f, 0);

            AddChild(new PronetestDriver { P = player });
            GD.Print("[PRONE] stance stealth-radius self-test: STAND/CROUCH/PRONE/SPRINT (stationary)");
        }

        // Broken-legs self-test: drop the player 40 m (breaks legs on landing), confirm a forced SPRINT is demoted
        // (radius 12 not 20), heal with a Medkit (Bones_Modifier Heal), confirm sprint works again (radius 20).
        void BuildBrokenTest(string gunPath)
        {
            SDG.Unturned.ItemCatalog.RegisterAll();   // so Assets.find(15) resolves the Medkit (with useHealBroken)
            var ground = new StaticBody3D { CollisionLayer = 1 << 0 };
            ground.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
            AddChild(ground);

            var player = new PlayerController { CaptureMouse = false };
            player.LoadGun(gunPath ?? "res://content/eaglefire.dat");
            AddChild(player);
            player.GlobalPosition = new Vector3(0, 40f, 0);   // 40 m drop -> legs break on landing

            AddChild(new BrokenTestDriver { P = player });
            GD.Print("[BROKEN] self-test: 40m fall -> legs break -> sprint blocked -> Medkit mends -> sprint restored");
        }

        // Grenade explosion self-test: NORMAL zombies at increasing ranges from a blast point; detonate radius-8 /
        // 175-damage and confirm each zombie's health matches the linear falloff 175*(1 - range/8). Player parked far
        // away so the zombies stay idle (out of sense range) and take no self-damage.
        void BuildGrenadeTest(string gunPath)
        {
            var ground = new StaticBody3D { CollisionLayer = 1 << 0 };
            ground.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
            AddChild(ground);

            var player = new PlayerController { CaptureMouse = false };
            player.LoadGun(gunPath ?? "res://content/eaglefire.dat");
            AddChild(player);
            player.GlobalPosition = new Vector3(100, 1f, 0);

            var ranges = new float[] { 4f, 6f, 7.5f, 9f };
            var zs = new ZombieController[ranges.Length];
            for (int i = 0; i < ranges.Length; i++)
            {
                var z = new ZombieController { Target = player, Speciality = ZombieController.ESpeciality.NORMAL };
                AddChild(z);
                z.GlobalPosition = new Vector3(ranges[i], 0f, 0f);
                zs[i] = z;
            }
            // a separate zombie by the (parked) player, killed via an actual FUSED thrown grenade -- exercises the
            // full Grenade fly+fuse+detonate chain, not just a direct Explode() call.
            var zThrow = new ZombieController { Target = player, Speciality = ZombieController.ESpeciality.NORMAL };
            AddChild(zThrow);
            zThrow.GlobalPosition = new Vector3(100f, 0f, -2f);

            AddChild(new GrenadeTestDriver { P = player, Zs = zs, ZThrow = zThrow });
            GD.Print("[GRENADE] explosion falloff self-test: zombies at r=4/6/7.5/9, blast r=8 dmg=175 (linear falloff)");
        }

        // Render an item's 3D model to a flat icon (ItemTool.captureIcon-style: ortho camera + flat unshaded albedo).
        // Orient by the model's AABB -- camera along the SHORTEST extent, up = the MIDDLE extent, so the LONGEST lies
        // horizontal (guns end up side-on, as in the real inventory). Magenta bg -> keyed to alpha after capture.
        // spec = "MODEL.txt" or "MODEL.txt:ALBEDO.png".
        void BuildBakeIcon(string spec)
        {
            string modelsStr = spec, albedo = null;
            int colon = spec.IndexOf(':');
            if (colon >= 0) { modelsStr = spec[..colon]; albedo = spec[(colon + 1)..]; }
            var models = modelsStr.Split('+', System.StringSplitOptions.RemoveEmptyEntries);   // gun+sight+mag = assembled

            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(1f, 0f, 1f),   // magenta key colour
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = Colors.White, AmbientLightEnergy = 1f,
            };
            AddChild(new WorldEnvironment { Environment = env });

            var mat = new StandardMaterial3D
            {
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,   // ripped meshes are CW-wound; show both faces
                TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,   // runtime ImageTexture has no mipmaps -> Nearest (else samples black)
                Metallic = 0f, Roughness = 0.6f,                   // matte so the dark albedo lights up (ItemTool renders lit, not flat)
            };
            if (albedo != null)
            {
                string p = ProjectSettings.GlobalizePath($"res://content/{albedo}");
                if (System.IO.File.Exists(p))
                {
                    var img = Image.LoadFromFile(p);
                    if (img != null) { mat.AlbedoTexture = ImageTexture.CreateFromImage(img); GD.Print($"[BAKE] tex OK {img.GetWidth()}x{img.GetHeight()}"); }
                    else GD.Print("[BAKE] tex img NULL");
                }
                else GD.Print($"[BAKE] tex NOT FOUND: {p}");
            }
            if (mat.AlbedoTexture == null) mat.AlbedoColor = new Color(0f, 1f, 0f);   // GREEN = texture-load fallback

            Aabb aabb = default; bool firstMesh = true;
            foreach (var m in models)   // combine gun + attachments (sight/mag) into one assembled icon
            {
                var mesh = ContentProvider.ParseObj($"res://content/{m}");
                if (mesh == null) continue;
                AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = mat });
                var mb = mesh.GetAabb();
                aabb = firstMesh ? mb : aabb.Merge(mb); firstMesh = false;
            }
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-25f, 90f, 0f), LightEnergy = 1.7f });   // key from the camera side (+X)
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(25f, 70f, 0f), LightEnergy = 0.7f });    // soft fill

            Vector3 c = aabb.Position + aabb.Size * 0.5f, s = aabb.Size;
            var ax = new (float e, Vector3 dir)[] { (s.X, Vector3.Right), (s.Y, Vector3.Up), (s.Z, Vector3.Back) };
            System.Array.Sort(ax, (a, b) => a.e.CompareTo(b.e));   // [0]=shortest [1]=middle [2]=longest
            var cam = new Camera3D { Projection = Camera3D.ProjectionType.Orthogonal, Size = ax[2].e * 1.18f };
            AddChild(cam);
            cam.GlobalPosition = c + ax[0].dir * (s.Length() + 2f);
            cam.LookAt(c, -ax[1].dir);   // -middle axis = up (the model's height axis points "down" in mesh space)
            cam.Current = true;
            GD.Print($"[BAKE] {modelsStr} aabb={s} longest={ax[2].e:F2} orthoSize={cam.Size:F2}");
        }

        // Opens the inventory dashboard over a player (populated with real items) for a --write-movie / screenshot.
        // selectDemo also pops the selection panel for an item so it can be captured.
        void BuildInventoryDemo(string gunPath, bool selectDemo = false, bool equipDemo = false)
        {
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.42f, 0.55f, 0.72f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.55f, 0.57f, 0.6f),
                AmbientLightEnergy = 0.6f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-52f, -46f, 0f), LightEnergy = 1.2f });

            var ground = new StaticBody3D { CollisionLayer = 1 << 0 };
            ground.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
            var gmesh = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(120, 120) } };
            gmesh.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.30f, 0.34f, 0.28f) };
            ground.AddChild(gmesh);
            AddChild(ground);

            var player = new PlayerController { CaptureMouse = false };
            player.LoadGun(gunPath ?? "res://content/eaglefire.dat");
            AddChild(player);                    // _Ready builds + populates the inventory and its dashboard
            player.GlobalPosition = new Vector3(0, 1.0f, 0);
            { var hud = new HUD { Player = player }; AddChild(hud); player.Hud = hud; }
            if (equipDemo) { player.OpenInventory(); player.DemoEquip(1, 0, 0); }   // equip the SECONDARY Maplestrike -> held
            else if (selectDemo) player.DemoSelect(2, 0, 0);   // pop the selection panel for the Medkit in pockets
            else player.OpenInventory();
            GD.Print("[INV] inventory dashboard open, real items populated");
        }

        // Drops a spread of items into the world (rarity markers + names) and runs a pickup check, viewed from an
        // overview camera for a --write-movie / screenshot.
        void BuildDropDemo(string gunPath)
        {
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.42f, 0.55f, 0.72f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.6f, 0.62f, 0.65f),
                AmbientLightEnergy = 0.7f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-52f, -46f, 0f), LightEnergy = 1.2f, ShadowEnabled = true });

            var ground = new StaticBody3D { CollisionLayer = 1 << 0 };
            ground.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
            var gmesh = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(60, 60) } };
            gmesh.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.30f, 0.34f, 0.28f) };
            ground.AddChild(gmesh);
            AddChild(ground);

            var player = new PlayerController { CaptureMouse = false };
            player.LoadGun(gunPath ?? "res://content/eaglefire.dat");
            AddChild(player);
            player.GlobalPosition = new Vector3(0, 1.0f, 0);
            player.Camera.Current = false;   // use an overview instead of the FP cam

            // drop a spread of real items in front of the player
            player.DropWorldItem(new SDG.Unturned.Item(15), new Vector3(-1.4f, 0.1f, -3.0f));   // Medkit
            player.DropWorldItem(new SDG.Unturned.Item(95), new Vector3(-0.5f, 0.1f, -3.6f));   // Bandage
            player.DropWorldItem(new SDG.Unturned.Item(14), new Vector3(0.5f, 0.1f, -3.2f));    // Bottled Water
            player.DropWorldItem(new SDG.Unturned.Item(13), new Vector3(1.4f, 0.1f, -3.8f));    // Canned Beans
            player.DropWorldItem(new SDG.Unturned.Item(363), new Vector3(0f, 0.1f, -1.4f));     // Maplestrike (within 2m)

            var overview = new Camera3D { Current = true, Fov = 58f };
            AddChild(overview);
            overview.Position = new Vector3(0f, 3.4f, 1.6f);
            overview.LookAt(new Vector3(0f, 0.3f, -3.0f), Vector3.Up);

            player.TryPickup();   // the Maplestrike at -1.4 is within reach -> [pickup]
            GD.Print("[DROP] dropped 5 world items; ran a pickup check");
        }

        // Scatters loot around the world (LootSpawner) and views it from a high overview for a screenshot.
        void BuildLootDemo(string gunPath)
        {
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.42f, 0.55f, 0.72f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.6f, 0.62f, 0.65f),
                AmbientLightEnergy = 0.75f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-55f, -40f, 0f), LightEnergy = 1.2f, ShadowEnabled = true });

            var ground = new StaticBody3D { CollisionLayer = 1 << 0 };
            ground.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
            var gmesh = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(120, 120) } };
            gmesh.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.30f, 0.34f, 0.28f) };
            ground.AddChild(gmesh);
            AddChild(ground);

            var player = new PlayerController { CaptureMouse = false };
            player.LoadGun(gunPath ?? "res://content/eaglefire.dat");
            AddChild(player);
            player.GlobalPosition = new Vector3(0, 1.0f, 0);
            player.Camera.Current = false;
            AddChild(new LootSpawner());

            var overview = new Camera3D { Current = true, Fov = 62f };
            AddChild(overview);
            overview.Position = new Vector3(0f, 26f, 20f);
            overview.LookAt(new Vector3(0f, 0f, -3f), Vector3.Up);
            GD.Print("[LOOT] scattered loot around the world");
        }

        // Places a storage crate in front of the player, seeds it with loot, and opens it -> the dashboard shows the
        // crate's grid alongside the inventory (for a --write-movie / screenshot).
        void BuildCrateDemo(string gunPath)
        {
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.42f, 0.55f, 0.72f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.6f, 0.62f, 0.65f),
                AmbientLightEnergy = 0.75f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-52f, -46f, 0f), LightEnergy = 1.2f });

            var ground = new StaticBody3D { CollisionLayer = 1 << 0 };
            ground.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
            var gmesh = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(60, 60) } };
            gmesh.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.30f, 0.34f, 0.28f) };
            ground.AddChild(gmesh);
            AddChild(ground);

            var player = new PlayerController { CaptureMouse = false };
            player.LoadGun(gunPath ?? "res://content/eaglefire.dat");
            AddChild(player);                    // _Ready builds the inventory + dashboard
            player.GlobalPosition = new Vector3(0, 1.0f, 0);

            // a crate 1.2 m in front, seeded with loot
            var crate = StorageCrate.Spawn(this, new Vector3(0f, 0f, -1.2f), 5, 4);
            crate.Add(new SDG.Unturned.Item(4));      // Eaglefire
            crate.Add(new SDG.Unturned.Item(15));     // Medkit
            crate.Add(new SDG.Unturned.Item(95, 4));  // Bandage x4
            crate.Add(new SDG.Unturned.Item(13, 3));  // Canned Beans x3

            player.OpenNearestCrate();   // within 2.5 m -> loads the crate into STORAGE + opens the dashboard
            GD.Print("[CRATE] opened a storage crate");
        }

        // A reference scene under a fast day/night cycle -- montage the --write-movie to see dawn -> noon -> dusk -> night.
        void BuildDayNightDemo()
        {
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.42f, 0.55f, 0.72f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.6f, 0.62f, 0.65f),
                AmbientLightEnergy = 0.85f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            var sun = new DirectionalLight3D { ShadowEnabled = true };
            AddChild(sun);
            var cyc = new DayNightCycle { Sun = sun, Env = env, DayLength = 5f, Time = 0.5f };   // fast; start at noon
            AddChild(cyc);
            AddChild(new RainOverlay { Cycle = cyc, Raining = true });   // demo the rain too

            var ground = new StaticBody3D { CollisionLayer = 1 << 0 };
            ground.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
            var gmesh = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(80, 80) } };
            gmesh.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.32f, 0.36f, 0.30f) };
            ground.AddChild(gmesh);
            AddChild(ground);

            for (int i = 0; i < 5; i++)   // boxes to catch the light + cast shadows
            {
                var b = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(1f, 1.5f, 1f) } };
                b.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.62f, 0.56f, 0.5f) };
                b.Position = new Vector3((i - 2) * 2.5f, 0.75f, -3f);
                AddChild(b);
            }

            var cam = new Camera3D { Current = true, Fov = 62f };
            AddChild(cam);
            cam.Position = new Vector3(0f, 2.5f, 6f);
            cam.LookAt(new Vector3(0f, 1.4f, -4f), Vector3.Up);   // boxes + horizon/sky
            GD.Print("[DAYNIGHT] cycle demo");
        }

        // Scripts a small structure (floor tiles + walls) to show the build system, viewed from an overview.
        void BuildBuildDemo(string gunPath)
        {
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.42f, 0.55f, 0.72f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.6f, 0.62f, 0.65f),
                AmbientLightEnergy = 0.8f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-50f, -55f, 0f), LightEnergy = 1.2f, ShadowEnabled = true });

            var ground = new StaticBody3D { CollisionLayer = 1 << 0 };
            ground.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
            var gmesh = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(60, 60) } };
            gmesh.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.30f, 0.34f, 0.28f) };
            ground.AddChild(gmesh);
            AddChild(ground);

            var bt = new BuildTool();
            AddChild(bt);
            bt.Type = 0;   // 2x2 floor of tiles
            bt.Spawn(new Vector3(-1.5f, 0.1f, -1.5f), 0); bt.Spawn(new Vector3(1.5f, 0.1f, -1.5f), 0);
            bt.Spawn(new Vector3(-1.5f, 0.1f, 1.5f), 0);  bt.Spawn(new Vector3(1.5f, 0.1f, 1.5f), 0);
            bt.Type = 1;   // three walls
            bt.Spawn(new Vector3(-3f, 1.5f, 0f), 90f);
            bt.Spawn(new Vector3(3f, 1.5f, 0f), 90f);
            bt.Spawn(new Vector3(0f, 1.5f, -3f), 0f);

            var overview = new Camera3D { Current = true, Fov = 60f };
            AddChild(overview);
            overview.Position = new Vector3(6f, 4.5f, 7f);
            overview.LookAt(new Vector3(0f, 1f, 0f), Vector3.Up);
            GD.Print("[BUILD] scripted a small structure (floor + walls)");
        }

        // A few bundled ripped crates as cover/scenery (portable res:// assets).
        void BuildCrates()
        {
            var crate = ContentProvider.ParseObj("res://content/crate.txt");
            if (crate == null) return;
            var aabb = crate.GetAabb();
            float big = Mathf.Max(aabb.Size.X, Mathf.Max(aabb.Size.Y, aabb.Size.Z));
            float s = big > 0.01f ? 2.2f / big : 1f;
            var mat = new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.45f, 0.32f) };
            foreach (var pos in new[] { new Vector3(7, 0, -6), new Vector3(-8, 0, -4), new Vector3(6, 0, 8), new Vector3(-6, 0, 9), new Vector3(11, 0, 2) })
            {
                var mi = new MeshInstance3D { Mesh = crate, MaterialOverride = mat, Scale = new Vector3(s, s, s) };
                AddChild(mi);
                mi.Position = pos + new Vector3(0, -aabb.Position.Y * s, 0);
            }
        }

        const ushort NetPort = 47872;

        // Headless dedicated server process (+ a scripted bot player).
        void BuildServer()
        {
            var srv = new Net.NetServer(NetPort);
            var bot = new Net.NetClient("127.0.0.1", NetPort);
            AddChild(new ServerNode { Server = srv, Bot = bot });
            GD.Print($"[SERVER] dedicated NetServer + bot on udp {NetPort}");
        }

        // Rendering client process: connects to the dedicated server, renders the synced players.
        void BuildClient()
        {
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.42f, 0.55f, 0.72f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.55f, 0.57f, 0.6f),
                AmbientLightEnergy = 0.6f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-52f, -46f, 0f), LightEnergy = 1.2f, ShadowEnabled = true });
            var ground = new StaticBody3D { CollisionLayer = 1 << 0 };
            ground.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
            var gm = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(80, 80) } };
            gm.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.30f, 0.34f, 0.28f) };
            ground.AddChild(gm);
            AddChild(ground);
            var cam = new Camera3D { Current = true, Fov = 62f };
            AddChild(cam);
            cam.Position = new Vector3(0f, 9f, 14f);
            cam.LookAt(new Vector3(0f, 1f, 0f), Vector3.Up);

            ScatterScenery(); // real ripped Unturned props so the arena isn't a bare plane

            var cli = new Net.NetClient("127.0.0.1", NetPort);
            AddChild(new ClientNode { Client = cli });
            GD.Print($"[CLIENT] connected to 127.0.0.1:{NetPort}; local player = real PlayerController (synced)");
        }

        // Scatter a few real ripped props (textured) around the arena as static scenery.
        void ScatterScenery()
        {
            const string manifest = @"C:\claude-workspace\ripped-mb\converted\manifest.json";
            if (!System.IO.File.Exists(manifest)) return;
            var cp = new ContentProvider();
            AddChild(cp);
            cp.LoadManifest(manifest);
            cp.LoadTextureManifest(@"C:\claude-workspace\ripped-mb\converted\texture_manifest.json");
            CharacterModel.LoadBundled(); // the real ripped character mesh for players + zombies

            (string name, float x, float z, float s)[] scenery =
            {
                ("Crate", -9f, -7f, 2.2f), ("Crate_0", 9f, -6f, 2.2f), ("Crate", 7f, 8f, 2.0f),
                ("Brickoven_Fire_0", -10f, 6f, 2.6f), ("Kiln_Fire_0", 11f, 3f, 2.6f),
                ("Berry_0", -6f, 10f, 1.6f), ("Crate_0", -12f, -1f, 2.0f),
            };
            foreach (var (name, x, z, s) in scenery)
            {
                var g = cp.FindGuidByName(name);
                if (g == null) continue;
                var mesh = cp.LoadMesh(g);
                if (mesh == null || mesh.GetSurfaceCount() == 0) continue;
                Material mat = new StandardMaterial3D { AlbedoColor = new Color(0.7f, 0.68f, 0.62f) };
                var tp = cp.GetTexturePath(g);
                if (tp != null) { var img = Image.LoadFromFile(tp); if (img != null) mat = new StandardMaterial3D { AlbedoTexture = ImageTexture.CreateFromImage(img) }; }
                var aabb = mesh.GetAabb();
                float big = Mathf.Max(aabb.Size.X, Mathf.Max(aabb.Size.Y, aabb.Size.Z));
                float sc = big > 0.001f ? s / big : 1f;
                var mi = new MeshInstance3D { Mesh = mesh, MaterialOverride = mat, Scale = new Vector3(sc, sc, sc) };
                AddChild(mi);
                mi.Position = new Vector3(x, -aabb.Position.Y * sc, z);
            }
        }

        // In-process 2-player network demo: a real NetServer + two real NetClients over loopback UDP,
        // rendering a capsule per synced player (see NetDemoNode). Records via --write-movie.
        void BuildNetDemo()
        {
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.42f, 0.55f, 0.72f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.55f, 0.57f, 0.6f),
                AmbientLightEnergy = 0.6f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-52f, -46f, 0f), LightEnergy = 1.2f, ShadowEnabled = true });

            var ground = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(80, 80) } };
            ground.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.30f, 0.34f, 0.28f) };
            AddChild(ground);

            var cam = new Camera3D { Current = true, Fov = 62f };
            AddChild(cam);
            cam.Position = new Vector3(0f, 9f, 14f);
            cam.LookAt(new Vector3(0f, 1f, 0f), Vector3.Up);

            const ushort port = 47871;
            var server = new Net.NetServer(port);
            var local = new Net.NetClient("127.0.0.1", port);
            var bot = new Net.NetClient("127.0.0.1", port);
            AddChild(new NetDemoNode { Server = server, Local = local, Bot = bot });
            GD.Print("[NETDEMO] real NetServer + 2 NetClients on loopback UDP; rendering server-synced players");
        }

        public override void _Process(double delta)
        {
            if (_fireTest && _ftPlayer != null) { _ftFrame++; if (_ftFrame >= 60 && _ftFrame % 15 == 0) _ftPlayer.Fire(); }   // own counter -- the _frame demo loop below is gated on _rigDir
            if (_peiPlay && _peiPlayer != null) { _peiFrame++; if (_peiFrame == 50) _peiPlayer.EnterNearestVehicle(); else if (_peiFrame >= 55) _peiPlayer.ScriptedDrive = new Vector2(0f, 1f); }   // settle onto PEI, hop in, drive forward (--horde: the loud drive aggros the zombie field -> roadkill)
            if (_rigDir != null)
            {
                _frame++;
                if (_ragTest && _frame == 4) _rc?.RagdollStart(new Vector3(3.5f, 5f, 1.5f)); // knock him over
                if (_ragTest && _frame == 46) _rc?.ApplyImpact(_rc.GlobalPosition + new Vector3(0f, 0.4f, 0f), new Vector3(8f, 4f, 0f)); // simulate a corpse shot
                // --vm ADS demo: the equip pull-out plays first (source gates aiming until it finishes), then a
                // short settle, THEN ADS; release later so the clip shows the un-ADS back to hip. No recoil.
                if (_vmTest && _vmAttach && _vm != null)
                {
                    // --attach: once equipped, hold the T attachment menu open (no aim/fire) so the render shows the slot icons
                    if (_am != null && _vm.IsEquipComplete && !_am.IsOpen && ++_vmSettle >= 8) _am.Open();
                }
                else if (_vmTest && _vm != null)
                {
                    if (!_vmAimed && _vm.IsEquipComplete && ++_vmSettle >= 8)
                    { _vm.SetAiming(true); _vmAimed = true; _vmAimStart = _frame; }
                    if (_vmAimed && _frame == _vmAimStart + 30) _vm.SetAiming(false);
                    // after un-ADS, fire a few HIP shots so the test also exercises recoil shake + case ejection
                    // (real Eaglefire Shake_Min/Max_* — Z-heavy back-punch)
                    if (_frame == 88 || _frame == 91 || _frame == 94)
                        _vm.Kick(new Vector3(-0.0025f, 0.0025f, -0.01f), new Vector3(0.0025f, -0.0025f, -0.02f), 3.5f, 1f);
                    // then a reload, so the test shows the real Gun_Reload arm anim (and its return to ready)
                    if (_frame == 100) _vm.SetReloading(true);
                    if (_frame == 150) _vm.SetReloading(false);
                }
                if (_vehTest && _veh != null)
                {
                    // settle, then auto-drive a course for the video: straight -> right curve -> left curve
                    float throttle = (!_chain && _frame > 30) ? 1f : 0f;   // --chain: stay parked so the blast reaches the neighbours
                    float steer = _frame < 120 ? 0f : (_frame < 235 ? 0.45f : -0.45f);
                    _veh.Drive(throttle, steer, false);
                    if (_chain && _frame == 20) _veh.TakeDamage(9999f);   // detonate _veh -> ~4 s later it blows -> chains to the car + horde
                    if (_roadkill && _frame == 35) _veh.Honk();   // honk before reaching them -> verify the horn's noise alert (source tellHorn AlertTool.alert 32)
                    if (_demo && (_frame == 45 || _frame == 80 || _frame == 115)) _veh.Honk();   // --demo: a few horn honks
                    if (_demo && _frame >= 40 && _frame < 100 && _frame % 8 == 0) _veh.TakeDamage(90f);   // --demo: damage -> smoke -> explode
                    if (_vehCam != null)   // chase cam: behind the jeep's heading (flattened), above -- shows the red taillights at night
                    {
                        var vt = _veh.GlobalTransform;
                        var fwd = -vt.Basis.Z; fwd.Y = 0f;
                        fwd = fwd.LengthSquared() > 0.001f ? fwd.Normalized() : Vector3.Forward;
                        _vehCam.GlobalPosition = vt.Origin - fwd * 7.5f + Vector3.Up * 3.2f;
                        _vehCam.LookAt(vt.Origin + Vector3.Up * 0.7f, Vector3.Up);
                    }
                }
                if (_driveTest && _dtPlayer != null)
                {
                    if (_frame == 25 && !_nade) _dtPlayer.EnterNearestVehicle();                          // hop in (skip for --nade: keep the jeep parked to grenade it)
                    if (_frame >= 30) _dtPlayer.ScriptedDrive = _swarm ? Vector2.Zero : _drivethru ? new Vector2(0f, 1f) : new Vector2(_frame > 130 ? 0.5f : 0f, 1f);  // swarm: sit still; drivethru: straight full-throttle; else forward then curve
                }
                if (_rigList.Length > 1)   // montage: switch clip every window
                {
                    int want = Mathf.Min(_frame / MontageFramesPerClip, _rigList.Length - 1);
                    if (want != _rigMontageIdx) { _rigMontageIdx = want; _rc?.Play(_rigList[want]); }
                }
                if (_rigShot < _rigCaptureFrames.Length && _frame == _rigCaptureFrames[_rigShot])
                {
                    var im = GetViewport().GetTexture().GetImage();
                    string p = $"{_rigDir}/rig_{_rigShot:D2}.png";
                    im.SavePng(p);
                    GD.Print($"[RIG] saved {p} (frame {_frame})");
                    _rigShot++;
                    if (_rigShot >= _rigCaptureFrames.Length) GetTree().Quit();
                }
                return;
            }
            if (_shotPath == null) return;
            if (_peiPlay) { if (_peiFrame < (_peiHorde ? 130 : 160)) return; }   // peiplay: drop(~25f)+enter(50f)+drive(55f+); --horde captures mid-plow through the zombie field
            else if (++_frame < 6) return; // let the renderer settle
            var img = GetViewport().GetTexture().GetImage();
            img.SavePng(_shotPath);
            GD.Print($"[SHOT] saved {_shotPath} ({img.GetWidth()}x{img.GetHeight()})");
            GetTree().Quit();
        }
    }

    // Drives the melee self-test: after a few settle frames, swings every physics tick (the cooldown gates it to
    // ~0.45 s). Quits when the zombie dies (Kills > 0) or after a timeout, so the run self-terminates for log-check.
    public partial class MeleeTestDriver : Node3D
    {
        public PlayerController P;
        int _frames; bool _done;

        public override void _PhysicsProcess(double delta)
        {
            _frames++;
            if (_frames > 5 && P != null) P.MeleeAttack();
            if (!_done && P != null && P.Kills > 0)
            {
                _done = true;
                GD.Print($"[MELEE] zombie killed by melee -- Kills={P.Kills} at frame {_frames}");
                GetTree().Quit();
            }
            if (_frames > 200) { GD.Print("[MELEE] timeout: no kill within 200 frames"); GetTree().Quit(); }
        }
    }

    // Drives the fall-damage self-test: records starting health, then quits when the landing cuts it (the [fall] line
    // reports the impact speed + damage) or after a timeout.
    public partial class FallTestDriver : Node3D
    {
        public PlayerController P;
        int _frames; float _startHp = -1f;

        public override void _PhysicsProcess(double delta)
        {
            _frames++;
            if (_startHp < 0f && P != null) _startHp = P.Health;
            if (P != null && _startHp > 0f && P.Health < _startHp)
            {
                GD.Print($"[FALL] health {_startHp} -> {P.Health} after landing (frame {_frames})");
                GetTree().Quit();
            }
            if (_frames > 300) { GD.Print($"[FALL] timeout: no fall damage, health={P?.Health}"); GetTree().Quit(); }
        }
    }

    // Drives the stance self-test: sets each stance via ScriptedStance (let it apply a few frames), then logs the
    // resulting stealth detection radius against the source constants.
    public partial class PronetestDriver : Node3D
    {
        public PlayerController P;
        int _i, _wait;
        readonly SDG.Unturned.EPlayerStance[] _stances =
            { SDG.Unturned.EPlayerStance.STAND, SDG.Unturned.EPlayerStance.CROUCH, SDG.Unturned.EPlayerStance.PRONE, SDG.Unturned.EPlayerStance.SPRINT };
        readonly float[] _expect = { 12f, 6f, 3f, 20f };

        public override void _PhysicsProcess(double delta)
        {
            if (P == null) return;
            if (_wait > 0) { _wait--; return; }
            if (_i > 0)   // the stance set last visit has applied; read + check its radius
            {
                var st = _stances[_i - 1]; float r = P.GetStealthDetectionRadius(); float e = _expect[_i - 1];
                GD.Print($"[PRONE] {st} radius={r:F1} expect={e:F1} {(Mathf.Abs(r - e) < 0.01f ? "PASS" : "FAIL")}");
            }
            if (_i >= _stances.Length) { GetTree().Quit(); return; }
            P.ScriptedStance = _stances[_i];
            _i++; _wait = 3;
        }
    }

    // Drives the broken-legs self-test through its phases (see BuildBrokenTest).
    public partial class BrokenTestDriver : Node3D
    {
        public PlayerController P;
        int _phase, _wait, _frames;
        void Chk(string n, bool ok) => GD.Print($"[BROKEN] {n} -> {(ok ? "PASS" : "FAIL")}");

        public override void _PhysicsProcess(double delta)
        {
            if (P == null) return;
            _frames++;
            if (_wait > 0) { _wait--; return; }
            switch (_phase)
            {
                case 0:   // wait for the 40 m drop to land + break legs
                    if (P.Broken) { Chk($"fall broke legs (health={P.Health})", true); P.ScriptedStance = SDG.Unturned.EPlayerStance.SPRINT; _wait = 3; _phase = 1; }
                    else if (_frames > 200) { Chk("fall broke legs (TIMEOUT)", false); GetTree().Quit(); }
                    break;
                case 1:   // broken + forcing SPRINT -> demoted to STAND (radius 12, not the SPRINT 20)
                    Chk($"broken legs block sprint (radius {P.GetStealthDetectionRadius():F0})", Mathf.Abs(P.GetStealthDetectionRadius() - 12f) < 0.01f);
                    P.ScriptedStance = null;
                    P.Consume(SDG.Unturned.Assets.find(15));   // Medkit: Bones_Modifier Heal
                    _wait = 2; _phase = 2;
                    break;
                case 2:   // mended
                    Chk($"Medkit mended legs (Broken={P.Broken})", !P.Broken);
                    P.ScriptedStance = SDG.Unturned.EPlayerStance.SPRINT; _wait = 3; _phase = 3;
                    break;
                case 3:   // no longer broken + SPRINT -> radius 20 (sprint restored)
                    Chk($"sprint restored after heal (radius {P.GetStealthDetectionRadius():F0})", Mathf.Abs(P.GetStealthDetectionRadius() - 20f) < 0.01f);
                    GetTree().Quit();
                    break;
            }
        }
    }

    // Drives the grenade explosion self-test: captures each zombie's range at detonation, blasts, then checks its
    // health against the source linear falloff 175*(1 - range/8).
    public partial class GrenadeTestDriver : Node3D
    {
        public PlayerController P;
        public ZombieController[] Zs;
        public ZombieController ZThrow;
        int _frames;
        float[] _rangeAtBlast;

        public override void _PhysicsProcess(double delta)
        {
            _frames++;
            if (_frames == 4)   // detonate directly at origin (the 4 falloff zombies)
            {
                _rangeAtBlast = new float[Zs.Length];
                for (int i = 0; i < Zs.Length; i++) _rangeAtBlast[i] = Zs[i].GlobalPosition.DistanceTo(Vector3.Zero);
                P.Explode(Vector3.Zero, 8f, 175f, 175f, 100f);
            }
            else if (_frames == 6)   // check the falloff, then arm a real FUSED grenade on ZThrow
            {
                for (int i = 0; i < Zs.Length; i++)
                {
                    float r = _rangeAtBlast[i];
                    float expDmg = r > 8f ? 0f : 175f * (1f - r / 8f);
                    float expHp = 100f - expDmg;
                    float hp = Zs[i].Dead ? 0f : Zs[i].Health;
                    bool ok = Mathf.Abs(hp - expHp) < 0.6f;
                    GD.Print($"[GRENADE] range={r:F2} dmg~{expDmg:F1} -> health={hp:F1} expect~{expHp:F1} -> {(ok ? "PASS" : "FAIL")}");
                }
                var g = new Grenade { Thrower = P, Fuse = 0.2f, Vel = Vector3.Zero };   // point-blank, short fuse
                P.GetParent().AddChild(g);
                g.GlobalPosition = ZThrow.GlobalPosition + Vector3.Up * 0.2f;
            }
            else if (_frames == 20)   // by now the fuse fired -> ZThrow should be dead
            {
                GD.Print($"[GRENADE] fused throw killed point-blank zombie -> {(ZThrow.Dead ? "PASS" : "FAIL")}");
                GetTree().Quit();
            }
        }
    }
}
