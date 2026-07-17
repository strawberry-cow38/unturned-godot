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
        Deployable _spotDbg;    // UG_WIRETEST: spotlight, probed for lamp-lit state at the shot frame
        Vector3 _vAim; bool _vHave;   // first real (Police/Fire/Ambulance) vehicle, for the demo cam
        bool _noZombies;   // --nozombies: a quiet test environment (skip the horde spawner)
        // Unturned install root -> Maps\<name>. The real map terrain (Landscape heightmaps) is read live from a local
        // Unturned install (not shipped in-repo). Override the Steam location with the UG_UNTURNED_DIR env var for
        // NON-default installs, e.g. UG_UNTURNED_DIR="D:\SteamLibrary\steamapps\common\Unturned".
        static string MapDir(string name) =>
            (System.Environment.GetEnvironmentVariable("UG_UNTURNED_DIR")?.TrimEnd('\\', '/')
             ?? @"C:\Program Files (x86)\Steam\steamapps\common\Unturned") + "/Maps/" + name;   // forward slashes: valid on Windows too, and required on the Linux dedicated server
        string _mapRoot = MapDir("PEI");   // --map=NAME switches the whole map (terrain + objects + spawns)
        string _mapPlace = "placements.txt";   // per-map baked object placements in content/objects/ (non-PEI = placements_<key>.txt)
        int _frame;
        MainMenu _menuShotMenu; string _menuShotDir; int _menuShotIdx;   // --menushot=DIR: render the 3D barn menu + capture each camera anchor
        string _rigDir;                              // --rig=DIR : capture a frame strip here
        int[] _rigCaptureFrames = { 4, 12, 20, 28, 36, 44 };
        int _rigShot;
        RiggedCharacter _rc;                         // montage: cycle through several clips
        string[] _rigList = System.Array.Empty<string>();
        int _rigMontageIdx = -1;
        const int MontageFramesPerClip = 55;
        bool _ragTest;                               // --anim=Ragdoll : trigger the death ragdoll mid-capture
        bool _vmTest; Viewmodel _vm;                 // --vm=DIR : first-person viewmodel test (equip -> ADS -> hip)
        bool _vmMelee;                               // --vm target is a melee weapon -> skip the gun aim/fire/reload script (MeleeSwingDriver swings it instead)
        bool _vmAimed; int _vmAimStart; int _vmSettle;
        bool _vmAttach; AttachmentMenu _am;          // --attach : hold the T attachment menu open for the render
        bool _vehTest; Vehicle _veh; Camera3D _vehCam; int _vehVariant; bool _night, _demo, _crash, _roadkill, _chain, _hitch, _backunder, _pivots; Vehicle _buTrailer; int _buCoupledFrame = 999999;   // --vehicle=DIR [--variant=N] [--night] [--demo] [--crash] [--roadkill] [--chain] [--hitch] [--backunder] [--pivots]
        readonly System.Collections.Generic.List<(Node3D mark, Vehicle veh, Vector3 local)> _pivotMarks = new();   // --pivots: arrow markers pinned to each coupling point
        bool _driveTest, _swarm, _drivethru, _nade; PlayerController _dtPlayer;      // --drivetest=DIR [--swarm|--drivethru|--nade] : enter/drive a jeep; swarm = mob it; drivethru = loud drive wakes zombies; nade = grenade the parked car
        bool _fireTest; PlayerController _ftPlayer; int _ftFrame;   // --firetest [--supp] : player fires near a distant zombie -> gunshot alert (suppressed = none)
        bool _peiPlay; PlayerController _peiPlayer; int _peiFrame; bool _peiHorde;   // --peiplay [--horde] : drive a jeep on real PEI (--horde = a zombie horde swarms it, vehicle<->zombie loop on real ground)
        PlayerController _pdPlayer; int _pdFireT;   // --peidrive on-foot player -> UG_AUTOFIRE terrain-impact verification
        bool _peiPlayable;   // menu "Drive PEI": BuildObjectsTest spawns a player+jeep with REAL controls instead of the aerial cam
        bool _worldBuild, _worldReady;   // BuildObjectsTest (objects/peidrive) async load -> the --shot harness waits for _worldReady before capturing
        bool _navShot;   // --navshot: nav-debug verify screenshot (waits for load + navmesh overlay + zombie cones)
        bool _navPathTest;   // --navpathtest: after a few frames (nav synced), query the navmesh + report routing
        bool _zombieTest; ZombieField _ztField;   // --zombietest: after a few frames, verify planned pocket spawns land ON the baked navmesh
        bool _bakeNav;   // --bakenav: sync-load the full world + bake+save the canonical navmesh, then quit (offline tool; the game only loads)
        int _treeCheckFrame; bool _treeChecked;   // UG_TREECHECK: raycast self-test that tree trunk colliders are actually hittable
        float _perfT;   // UG_PERF: throttle the perf log
        bool _itemTest;   // --itemtest=ID,ID,... : drop those items as physics WorldItems onto a ground plane -> validate mesh/tex/scale/settle

        public override void _Ready()
        {
            if (System.Environment.GetEnvironmentVariable("UG_COLLVIS") == "1") GetTree().DebugCollisionsHint = true;   // diagnostic: overlay physics collision shapes (must be set before bodies enter the tree)
            string catalog = null, shot = null, picks = null, gun = null, rig = null, anim = "Walk", vm = null, bakeIcon = null, veh = null, drivetest = null, proptest = null, animrig = null, rottest = null, itemtest = null, navShot = null, croptest = null, menuShot = null;
            bool deployTest = false;
            bool skillsui = false;
            bool play = false, demo = false, netdemo = false, server = false, dedicated = false, client = false, smoke = false, hurtdemo = false, invdemo = false, invsel = false, invequip = false, invdrop = false, invloot = false, invcrate = false, daynight = false, buildmode = false, firetest = false, supp = false, terrain = false, peiplay = false, objects = false, peidrive = false, craftui = false, bakenav = false, navPathTest = false, zombieTest = false;
            foreach (var arg in OS.GetCmdlineUserArgs())
            {
                if (arg.StartsWith("--catalog=")) catalog = arg["--catalog=".Length..];
                else if (arg.StartsWith("--shot=")) shot = arg["--shot=".Length..];
                else if (arg.StartsWith("--navshot=")) navShot = arg["--navshot=".Length..];   // verify screenshot: navmesh floor overlay + zombie vision cones, synchronous world, aerial over a pocket
                else if (arg.StartsWith("--menushot=")) menuShot = arg["--menushot=".Length..];   // render the 3D barn main menu + capture each of the 5 camera anchors (menu_00..04.png)
                else if (arg == "--bakenav") bakenav = true;   // offline TOOL: sync-load the FULL world + bake all 19 nav pockets -> save the .res files (commit them; the game only LOADS, never gens)
                else if (arg == "--navpathtest") navPathTest = true;   // OFFLINE verify: sync world -> query the navmesh -> log whether zombie paths ROUTE AROUND buildings (not through)
                else if (arg == "--zombietest") zombieTest = true;   // OFFLINE verify: sync world -> bucket Animals.dat into pockets -> check planned spawns land ON the baked navmesh
                else if (arg.StartsWith("--proptest=")) proptest = arg["--proptest=".Length..];   // spawn ONE named prop at identity + RGB axes -> diagnose mirror/orientation/material
                else if (arg.StartsWith("--croptest=")) croptest = arg["--croptest=".Length..];   // spawn a farm crop (young + grown) on a ground plane -> validate mesh/tex/orientation (UG_CROPROT tunes rot)
                else if (arg == "--deploytest") deployTest = true;   // both deployables placed on a ground plane + a valid(blue)+invalid(red) ghost -> verify models/palette/stand-up/ghost materials
                else if (arg == "--skillsui") skillsui = true;   // render the skills menu (showcase/validate the SkillsUI)
                else if (arg.StartsWith("--itemtest=")) itemtest = arg["--itemtest=".Length..];   // drop a row of loot items (ids) as physics WorldItems -> validate real mesh/tex/scale/settle
                else if (arg.StartsWith("--animrig=")) animrig = arg["--animrig=".Length..];   // build a rigged animal (content/NAME_rig.json) at rest + 3/4 cam -> validate the static pose stands
                else if (arg.StartsWith("--rottest=")) rottest = arg["--rottest=".Length..];   // place ONE prop with the placement euler (UG_EULER) under a rotation convention (UG_ROTCONV) -> hunt the upside-down
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
                else if (arg == "--hitch") _hitch = true;         // with --gun=semi: back a trailer under the cab + couple it (verify the fifth-wheel hitch + articulation)
                else if (arg == "--backunder") { _backunder = true; _hitch = false; }   // with --gun=semi: spawn a PARKED trailer behind + reverse the cab UNDER it, couple on proximity (verify the drive-under + phase-through)
                else if (arg == "--pivots") { _pivots = true; _hitch = false; }   // with --gun=semi: show cab + trailer SEPARATE with a labeled arrow at each coupling pivot (fifth wheel / kingpin)
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
                else if (arg == "--dedicated") dedicated = true;   // headless dedicated server: the REAL world (WorldBuilder dedicated mode) + NetServerSession on UDP
                else if (arg == "--netlog") UnturnedGodot.Net.NetLog.Enabled = true;   // net-diagnostics logging (equivalent: UG_NETLOG=1); sinks wired in DedicatedServer/ClientNode
                else if (arg == "--mploopback") _mpLoopback = true;   // OPT-IN (MP_PLAN §4 Phase 4): SP runs as an in-process listen-server + local client over MemTransport; without the flag SP keeps the direct path
                else if (arg == "--client") client = true;   // bare demo/test client: real world + the C1 overhead cam + ClientNode capsules (no player shell)
                else if (arg.StartsWith("--connect=")) { client = true; _playableClient = true; _connectHost = arg["--connect=".Length..]; }   // join a dedicated server by IP -- C3: the PLAYABLE client (ClientWorldSession: predicted first-person shell)
                else if (arg == "--smoke") smoke = true;
                else if (arg == "--hurtdemo") hurtdemo = true;
                else if (arg == "--firetest") firetest = true;   // player fires near a distant zombie: verify the gunshot alert (+ --supp = suppressed -> no alert)
                else if (arg == "--supp") supp = true;           // with --firetest: attach the suppressor
                else if (arg == "--terrain") terrain = true;     // load a real map's Landscape heightmap terrain (PEI Tile_0_0)
                else if (arg == "--craftui") craftui = true;     // open the crafting menu over a stocked inventory (UI verify)
                else if (arg == "--objects") objects = true;     // place PEI's real Level/Objects.dat objects (fences/props/rocks) on the terrain
                else if (arg == "--peidrive") peidrive = true;    // playable PEI: terrain + all objects/trees + player+jeep with real controls (same as the menu's "Drive PEI")
                else if (arg.StartsWith("--map="))                // load a DIFFERENT map (e.g. --map="cow tools"): terrain + objects + spawns all follow _mapRoot
                {
                    string mn = arg["--map=".Length..];
                    _mapRoot = MapDir(mn);
                    string key = System.Text.RegularExpressions.Regex.Replace(mn, "[^A-Za-z0-9]", "");
                    _mapPlace = mn == "PEI" ? "placements.txt" : "placements_" + key + ".txt";
                }
                else if (arg == "--peiplay") peiplay = true;     // player standing/walking on real PEI terrain (with colliders)
                else if (arg == "--invdemo") invdemo = true;
                else if (arg == "--invsel") { invdemo = true; invsel = true; }
                else if (arg == "--invequip") { invdemo = true; invequip = true; }
                else if (arg == "--invdrop") invdrop = true;
                else if (arg == "--invloot") invloot = true;
                else if (arg == "--invcrate") invcrate = true;
                else if (arg == "--daynight") daynight = true;
                else if (arg == "--build") buildmode = true;
                else if (arg == "--extractblueprints") { RunExtractBlueprints(); GetTree().Quit(); return; }   // walk retail item .dats -> content/blueprints.tsv catalog
                else if (arg == "--tests" || arg.StartsWith("--tests="))   // L1 in-engine test host (phase 2): boot once, run all GameTests, self-quit 0/1. `--tests=power.*` globs.
                {
                    AddChild(new Testing.TestHost { Filter = arg.StartsWith("--tests=") ? arg["--tests=".Length..] : "*" });
                    return;
                }
            }

            // UG_MAP env var = map name; robust for names with SPACES that get mangled through `--map=` user-args
            // (e.g. master's "cow tools"). Mirrors the --map= logic. Set $env:UG_MAP before launching godot.
            var ugMap = System.Environment.GetEnvironmentVariable("UG_MAP");
            if (!string.IsNullOrEmpty(ugMap))
            {
                _mapRoot = MapDir(ugMap);
                string ugKey = System.Text.RegularExpressions.Regex.Replace(ugMap, "[^A-Za-z0-9]", "");
                _mapPlace = ugMap == "PEI" ? "placements.txt" : "placements_" + ugKey + ".txt";
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
                _shotPath = shot;   // --shot: capture at a late frame (below) with live impacts down-range
                BuildFireTest(supp, gun);
                return;
            }

            if (craftui)   // open the crafting menu over a stocked inventory -> render the recipe list
            {
                GetWindow().Size = new Vector2I(1280, 720);
                _shotPath = shot;
                BuildCraftUI();
                return;
            }

            if (terrain)   // load a real Unturned map's terrain (PEI Landscape heightmap tile) -> a Godot mesh, replacing the flat test-plane
            {
                GetWindow().Size = new Vector2I(1280, 720);
                _shotPath = shot;   // wire the general frame-6 capture (else --shot renders the movie forever + hangs)
                BuildTerrainTest();
                return;
            }

            if (bakenav)   // offline navmesh bake tool: sync full-world load (peiPlayable=true -> object COLLIDERS get built -> buildings carve the mesh) -> bake + save
            {
                _bakeNav = true; _peiPlayable = true;
                BuildObjectsTest();
                string navShotOut = System.Environment.GetEnvironmentVariable("UG_NAVSHOT");
                if (navShotOut == null) { GetTree().Quit(); return; }   // pure bake -> quit
                // verify shot (UG_NAVSHOT): overlay the just-baked BUILDING-AWARE meshes + aerial cam over a pocket, so
                // the holes-around-buildings read visually. _peiPlayer/HUD are hidden so it's a clean nav overview.
                if (_peiPlayer != null) _peiPlayer.Visible = false;
                var _pk = ZombieNav.LoadPockets(_mapRoot);
                if (System.Environment.GetEnvironmentVariable("UG_NAVOVERLAY") != "0") ZombieNav.BuildOrLoad(this, _pk, overlay: true, save: false, bakeIfMissing: false);   // UG_NAVOVERLAY=0 -> plain world render (eyeball road/prop textures)
                int _pi = int.TryParse(System.Environment.GetEnvironmentVariable("UG_NAVPOCKET"), out var _p) ? Mathf.Clamp(_p, 0, _pk.Count - 1) : 7;
                if (_pk.Count > 0)
                {
                    var c = _pk[_pi].Center; var look = new Vector3(c.X, 32f, c.Z);
                    if (System.Environment.GetEnvironmentVariable("UG_NAVLOOK") is string _lk) { var _lp = _lk.Split(','); if (_lp.Length == 2 && float.TryParse(_lp[0], out var _lx) && float.TryParse(_lp[1], out var _lz)) look += new Vector3(_lx, 0f, _lz); }   // UG_NAVLOOK=x,z world offset to the look point
                    var cam = new Camera3D { Fov = 60f, Current = true };
                    AddChild(cam);
                    var _off = System.Environment.GetEnvironmentVariable("UG_NAVLOW") == "1" ? new Vector3(0f, 14f, 34f) : new Vector3(0f, 80f, 65f);   // UG_NAVLOW=1 -> low/close angle
                    if (float.TryParse(System.Environment.GetEnvironmentVariable("UG_NAVYAW"), out var _yaw)) _off = _off.Rotated(Vector3.Up, Mathf.DegToRad(_yaw));   // UG_NAVYAW=deg -> orbit the cam around the look point (+90 = face west)
                    cam.GlobalPosition = look + _off;
                    cam.LookAt(look, Vector3.Up);
                }
                _shotPath = navShotOut; _navShot = true;
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

            if (navPathTest) { _bakeNav = true; _peiPlayable = true; BuildObjectsTest(); _navPathTest = true; return; }   // sync-load; RunNavPathTest fires after a few frames (the nav map merges its regions on a physics tick, not in _Ready)
            if (zombieTest) { _bakeNav = true; _peiPlayable = true; _zombieTest = true; BuildObjectsTest(); return; }   // sync-load (creates the ZombieField + buckets spawns); RunZombieTest fires at frame 25 once the nav map has synced

            if (navShot != null) { GetWindow().Size = new Vector2I(1280, 720); BuildNavShot(navShot); return; }

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

            if (deployTest)   // deployables showcase: both placed on a ground plane + a valid(blue)/invalid(red) ghost
            {
                GetWindow().Size = new Vector2I(1280, 720);
                _shotPath = shot;
                BuildDeployTest();
                return;
            }

            if (croptest != null)   // farm crop showcase: young + grown on a ground plane -> validate mesh/tex/orientation
            {
                GetWindow().Size = new Vector2I(900, 900);
                _shotPath = shot;
                BuildCropTest(croptest);
                return;
            }

            if (skillsui)   // render the skills menu (a sample PlayerSkills with some XP + levels)
            {
                GetWindow().Size = new Vector2I(720, 760);
                _shotPath = shot;
                BuildSkillsUiShot();
                return;
            }

            if (itemtest != null)   // drop a row of loot items as physics WorldItems -> validate real mesh/tex/scale/gravity
            {
                GetWindow().Size = new Vector2I(1280, 720);
                _shotPath = shot;
                _itemTest = true;
                BuildItemTest(itemtest);
                return;
            }

            if (animrig != null)   // build a rigged animal from content/NAME_rig.json at its REST pose + 3/4 cam -> does it stand?
            {
                GetWindow().Size = new Vector2I(900, 900);
                _shotPath = shot;
                BuildAnimRig(animrig);
                return;
            }

            if (rottest != null)   // place ONE prop under a candidate placement-rotation convention -> find the upright one
            {
                GetWindow().Size = new Vector2I(900, 900);
                _shotPath = shot;
                BuildRotTest(rottest);
                return;
            }

            if (invdemo)    // open the inventory dashboard over the player, populated with real items
            {
                GetWindow().Size = new Vector2I(2560, 1440);   // match the movie size so the UI lays out full-frame
                _shotPath = shot;   // --shot=OUT -> capture the dashboard at the settle frame + quit (else the demo runs forever)
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

            if (dedicated) { BuildDedicated(); return; }        // headless dedicated server: real world + NetServerSession (MP_PLAN §4 Phase 3)
            if (server) { BuildServer(); return; }              // headless demo server (bare arena + a scripted bot)
            if (client) { GetWindow().Mode = Window.ModeEnum.Maximized; BuildClient(); return; }   // fill the screen (same "tiny viewport" fix as --play below): a fixed Size while the project opens MAXIMIZED boxed the render in a corner

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
                bool deployVm = gun == "generator" || gun == "spot" || gun == "spotlight" || gun == "wire";
                _rigCaptureFrames = System.Environment.GetEnvironmentVariable("UG_HAMMER") == "1"
                    ? new[] { 52, 56, 60, 64, 68, 72 }          // UG_HAMMER: the rack window (PlayHammer at f50) -> verify the gun ROTATES through the charge
                    : deployVm
                    ? new[] { 20, 25, 30, 40, 50, 60 }          // deployable: Deploy_Equip raise settles by ~f14 -> capture the neutral carry hold
                    : new[] { 10, 66, 89, 92, 95, 120 };        // equip -> ADS -> fire+1 (muzzle flash + tracer) -> reload
                _vmTest = true;
                GetWindow().Size = System.Environment.GetEnvironmentVariable("UG_VMSMALL") == "1" ? new Vector2I(1280, 720) : new Vector2I(2560, 1440);
                BuildViewmodelTest(gun ?? "eaglefire");   // --gun=<name> picks the gun (eaglefire | maplestrike)
                if (_vmAttach) _rigCaptureFrames = new[] { 40, 50, 60, 70, 80, 90 };   // menu open (post-equip) for each frame
                return;
            }

            if (veh != null)
            {
                _rigDir = veh;
                _rigCaptureFrames = System.Environment.GetEnvironmentVariable("UG_QUICK") == "1"
                    ? new[] { 48 }                                    // UG_QUICK: ONE settled+moving frame then quit -> ~20s instead of simulating the full course to frame 340 (~2min)
                    : new[] { 45, 90, 150, 210, 280, 340 };           // spread across the driving course (also keeps the movie running the full length)
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

            if (menuShot != null)   // render the 3D barn menu + capture each camera anchor (menu_00..04.png), then quit
            {
                GetWindow().Size = new Vector2I(1280, 720);
                var m = new MainMenu { OnDrivePEI = _ => { }, OnPlay = _ => { } };
                _menuShotMenu = m; _menuShotDir = menuShot;
                AddChild(m);
                return;
            }

            if (!smoke)
            {
                // DEFAULT (the exported build): a tiny main menu -> interactive single-player survival. Maximize to
                // FILL the screen (a fixed Size while the project opens MAXIMIZED boxed the render into a corner).
                GetWindow().Mode = Window.ModeEnum.Maximized;
                var menu = new MainMenu();
                menu.OnPlay = noZombies => { menu.QueueFree(); _noZombies = noZombies; BuildPlayable(null, false, null); };
                menu.OnDrivePEI = noZombies => { menu.QueueFree(); _noZombies = noZombies; _peiPlayable = true; BuildObjectsTest(); };
                menu.OnMultiplayer = () => { menu.QueueFree(); _connectHost = "claw.bitvox.me"; _playableClient = true; BuildClient(); };   // in-game MP-test entry (replaces the launcher checkbox): same path as --connect=claw.bitvox.me
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
            // melee weapons ship <name>.txt (root-mesh rip) with no <name>_gun.txt -> show them via the melee viewmodel path
            bool isMelee = System.IO.File.Exists(ProjectSettings.GlobalizePath($"res://content/{gunName}.txt")) && !System.IO.File.Exists(ProjectSettings.GlobalizePath($"res://content/{gunName}_gun.txt"));
            bool isFists = gunName == "fists" || gunName == "unarmed";
            bool isDeploy = gunName == "generator" || gunName == "spot" || gunName == "spotlight";
            bool isWire = gunName == "wire";
            _vm = isFists
                ? new Viewmodel { Fists = true }                                                  // bare-fists unarmed state (arms + melee ready hold, no mesh)
                : isWire
                ? new Viewmodel { ToolMesh = "wire_hold.obj", ToolColor = new Color(0.647f, 0.647f, 0.647f) }   // wire tool in-hand
                : isDeploy
                ? new Viewmodel { DeployableMesh = "generator_hold.obj", DeployableAlbedo = "generator_hold_tex.png" }   // deployable carry model in-hand + Deploy_Equip/Use
                : isMelee
                ? new Viewmodel { MeleeMesh = $"{gunName}.txt", MeleeAlbedo = $"{gunName}_albedo.png" }
                : new Viewmodel { GunName = gunName };   // self-contained: own SubViewport camera at FOV 60, composited on top
            AddChild(_vm);
            _vmMelee = isMelee || isFists || isDeploy || isWire;
            if (isMelee) AddChild(new MeleeSwingDriver { VM = _vm });   // periodic swings so the --vm render shows the melee swing anim
            if (isDeploy) AddChild(new DeployUseDriver { VM = _vm });   // periodic place motion so the --vm render shows the Deploy_Use anim
            if (_vmAttach) { _am = new AttachmentMenu(); AddChild(_am); _am.VM = _vm; }   // --attach: show the T menu over the gun
        }

        // --pivots: a bright downward arrow + pole + label, pinned each frame to a coupling point (fifth wheel / kingpin)
        Node3D MakePivotArrow(Color c, string label)
        {
            var mat = new StandardMaterial3D { AlbedoColor = c, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
            var root = new Node3D();
            root.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.1f, Height = 0.2f }, MaterialOverride = mat });                                                   // the exact pivot point
            root.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.18f, BottomRadius = 0f, Height = 0.35f }, MaterialOverride = mat, Position = new Vector3(0f, 0.175f, 0f) });  // arrowhead: tip DOWN at the point
            root.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.05f, 1.4f, 0.05f) }, MaterialOverride = mat, Position = new Vector3(0f, 1.05f, 0f) });      // pole above
            root.AddChild(new Label3D { Text = label, Position = new Vector3(0f, 1.95f, 0f), Modulate = c, FontSize = 64, OutlineSize = 14, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled });  // floating label
            return root;
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

            if (_hitch && _veh.CanTow)   // --hitch: place a trailer with its kingpin under the cab's fifth-wheel, then couple (test the rig)
            {
                var trailer = Vehicle.BuildByName("trailer");
                AddChild(trailer);
                trailer.Position = (_veh.Position + _veh.FifthWheelLocal) - trailer.KingpinLocal;   // line the kingpin up under the fifth-wheel plate
                GD.Print(_veh.CoupleTo(trailer) ? "[hitch] coupled OK" : "[hitch] couple FAILED (out of reach)");
            }
            if (_backunder && _veh.CanTow)   // --backunder: park a trailer ~4m behind the cab's rear, then the cab reverses UNDER it (see the vehTest loop) + couples on proximity
            {
                _buTrailer = Vehicle.BuildByName("trailer");
                AddChild(_buTrailer);
                // face the same way as the cab; drop it OFF-CENTER (X+0.8) ~4m behind so the cab reverses to close the gap AND the magnetize has to pull the kingpin sideways onto the fifth wheel (tests the centre-pull)
                _buTrailer.Position = new Vector3(0.8f, 1.2f, _veh.Position.Z + _veh.FifthWheelLocal.Z - _buTrailer.KingpinLocal.Z + 4.0f);
            }
            if (_pivots && _veh.CanTow)   // --pivots: cab + trailer SEPARATE, a labeled arrow pinned to each coupling point
            {
                var trailer = Vehicle.BuildByName("trailer");
                trailer.Position = new Vector3(0f, 1.2f, 13f);   // behind the cab, clearly separate (a ~3m gap between the two pivots)
                AddChild(trailer);
                var cabArrow = MakePivotArrow(new Color(0.2f, 1f, 0.3f), "fifth wheel");   // green = cab's pivot
                AddChild(cabArrow); _pivotMarks.Add((cabArrow, _veh, _veh.FifthWheelLocal));
                var trArrow = MakePivotArrow(new Color(1f, 0.35f, 0.95f), "kingpin");        // magenta = trailer's pivot
                AddChild(trArrow); _pivotMarks.Add((trArrow, trailer, trailer.KingpinLocal));
                // (the static cam is positioned in the vehTest loop, once _vehCam exists)
            }

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
            _vehCam.CullMask &= ~OutlineOverlay.OutlineLayer;   // the mask cam renders the vehicle silhouette, not this one
            AddChild(_vehCam);
            if (!string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("UG_VFOCUS")))   // preview the vehicle look-at outline + info panel
            {
                AddChild(new OutlineOverlay());
                _veh.SetLookFocused(true);
            }

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

            var jeep = Vehicle.BuildByName("jeep");   // a drivable jeep parked nearby -- walk up + press F to get in
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
                if (!_noZombies) AddChild(new HordeSpawner { Target = player, MaxAlive = int.TryParse(System.Environment.GetEnvironmentVariable("UG_HORDE"), out var _h) ? _h : 8 });   // UG_HORDE overrides the horde size (perf repro)
                var pause = new PauseMenu();   // ESC -> pause menu (freezes the sim)
                AddChild(pause);
                player.PauseMenu = pause;
                AddChild(new Profiler());   // F3 -> perf overlay (fps/frame/worst-frame/timings/draw-calls/mem) for stutter diagnosis (master)
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
        void BuildFireTest(bool suppressed, string gun)
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
            player.LoadGun($"res://content/{gun ?? "eaglefire"}.dat");   // --gun=<name> to fire-test a specific gun (launcher_rocket -> verify the rocket blast)
            AddChild(player);
            player.GlobalPosition = new Vector3(0, 1.0f, 0);
            player.RotationDegrees = new Vector3(0, System.Environment.GetEnvironmentVariable("UG_HITZOMBIE") == "1" ? 0f : 180f, 0);   // default: face +Z AWAY from the zombie (noise-only, suppressor-alert test). UG_HITZOMBIE: face -Z AT it -> hit it -> verify the flesh/blood impact
            { var hud = new HUD { Player = player }; AddChild(hud); player.Hud = hud; }
            _ftPlayer = player;
            if (suppressed) player.SetSuppressor(true);

            var z = new ZombieController { Target = player, Speciality = ZombieController.ESpeciality.NORMAL };
            AddChild(z);
            z.GlobalPosition = new Vector3(0, 1.0f, System.Environment.GetEnvironmentVariable("UG_HITZOMBIE") == "1" ? -6f : -25f);   // UG_HITZOMBIE: point-blank so shots connect -> verify blood
            GD.Print($"[FIRETEST] suppressed={suppressed} -- firing away from a zombie 25 m off; expect [ALERT] ONLY when unsuppressed");
        }

        // --craftui: open the crafting menu over a player with a stocked inventory so the recipe list renders.
        void BuildCraftUI()
        {
            SDG.Unturned.ItemCatalog.RegisterAll();
            BlueprintRegistry.Load();
            var inv = new SDG.Unturned.PlayerInventory();
            inv.tryAddItem(new SDG.Unturned.Item(67, 200));   // Metal Scrap x200
            inv.tryAddItem(new SDG.Unturned.Item(76, 1));     // Blowtorch (tool)
            var ui = new CraftingUI { Inv = inv };
            AddChild(ui);
            ui.Open();
            GD.Print("[CRAFTUI] opened crafting menu over a stocked inventory");
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

            { var _t = Terrain.LoadMapMerged(_mapRoot + "/Landscape/Heightmaps", withCollider: false); if (_t != null) AddChild(_t); }   // --map= aware (defaults to PEI); any modern-Landscape map renders here

            var cam = new Camera3D { Current = true, Fov = 55f, Far = 16000f };
            AddChild(cam);
            cam.Position = new Vector3(0f, 5200f, 1f);
            cam.LookAt(Vector3.Zero, new Vector3(0f, 0f, -1f));   // STRAIGHT TOP-DOWN; screen-up = world -Z (= Unity +Z = north) to match the map chart's orientation
            GD.Print($"[TERRAIN] loaded {System.IO.Path.GetFileName(_mapRoot)} (merged, seamless)");
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
            var mat = new StandardMaterial3D { Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled, VertexColorUseAsAlbedo = true };
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

        // --deploytest: both deployables PLACED on a ground plane (back row) + a BLUE-valid and RED-invalid
        // placement GHOST (front row) -> verify the ripped models stand up right (palette, -90 X), the collider,
        // and the ghost materials. The interactive hold->aim->LMB flow needs a live player, tested in-game.
        void BuildDeployTest()
        {
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.30f, 0.34f, 0.42f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.72f, 0.72f, 0.75f), AmbientLightEnergy = 1.0f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            var dirLight = new DirectionalLight3D { RotationDegrees = new Vector3(-48f, -40f, 0f), LightEnergy = 1.3f, ShadowEnabled = true };
            AddChild(dirLight);
            AddChild(new MeshInstance3D
            {
                Mesh = new PlaneMesh { Size = new Vector2(40f, 40f) },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.34f, 0.40f, 0.28f), Roughness = 1f },
            });
            var groundBody = new StaticBody3D();   // a real collider under the plane so the aim raycast has something to hit
            groundBody.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
            AddChild(groundBody);

            var gen = DeployableDef.Generator; var spot = DeployableDef.Spotlight;
            // back row: PLACED objects (surface = ground; the base is sat on it)
            var placedGen = Deployable.Spawn(this, gen, new Vector3(-2.6f, 0f, 0f), 0f);
            var placedSpot = Deployable.Spawn(this, spot, new Vector3(2.6f, 0f, 0f), 0f);
            if (System.Environment.GetEnvironmentVariable("UG_WIREARROWS") == "1")   // force the in/out port arrows on to verify their geometry/colour
                foreach (var dep in new[] { placedGen, placedSpot })
                    foreach (var pt in dep.Ports) pt.SetArrowState(true, true);
            if (System.Environment.GetEnvironmentVariable("UG_WIRETEST") == "1" && placedGen.Ports.Count > 0 && placedSpot.Ports.Count > 0)
            {   // wire generator-output -> mid node -> spotlight-consumer, power the generator, verify rendering + power flow
                var outp = placedGen.Ports[0];
                var cons = placedSpot.Ports.Find(p => p.Kind == DeployableDef.PortKind.Consumer);
                var pass = placedSpot.Ports.Find(p => p.Kind == DeployableDef.PortKind.Passthrough);
                var w = new Wire(); AddChild(w);
                w.Source = outp; w.Consumer = cons; w.AddToGroup("wires");
                w.SetPoints(new System.Collections.Generic.List<Vector3> { outp.GlobalPosition, new Vector3(0f, 1.6f, -1.2f), cons.GlobalPosition }, valid: true);
                _spotDbg = placedSpot;   // lamp-lit probe at the shot frame
                if (System.Environment.GetEnvironmentVariable("UG_WIREOFF") != "1") placedGen.TogglePower();   // turn the generator ON (UG_WIREOFF=1 leaves it off -> lamps must stay dark)
                PowerNet.Recompute(GetTree());
                GD.Print($"[POWERTEST] gen.IsPowered={placedGen.IsPowered} output={outp.Live:0}w consumer.recv={cons.Live:0}w powered={cons.Powered} passthrough={pass?.Live:0}w draw={outp.Draw:0}w load={placedGen.LoadFraction:0.00}");
                if (System.Environment.GetEnvironmentVariable("UG_WIREWRECK") == "1")   // destroy the spotlight -> its wire + port cubes must vanish (strawberry)
                {
                    placedSpot.DebugStage("wreck"); PowerNet.Recompute(GetTree());
                    GD.Print($"[WRECKTEST] wired spotlight wrecked -> wires+cubes should be gone (visual)");
                }
            }
            // front row: placement GHOSTS -- generator VALID (blue), spotlight INVALID (red)
            Ghost(gen, true, new Vector3(-2.6f, 0f, 4.2f), 0f);
            Ghost(spot, false, new Vector3(2.6f, 0f, 4.2f), 0f);

            var cam = new Camera3D { Current = true, Fov = 52f, Far = 10000f };
            AddChild(cam);
            var look = new Vector3(0f, 0.7f, 2f);                 // tracked look-at target so UG_CAMYAW can orbit around it
            cam.Position = new Vector3(0f, 3.2f, 11f);
            cam.LookAt(look, Vector3.Up);
            if (System.Environment.GetEnvironmentVariable("UG_WIRETEST") == "1")
            {   // drop to near-night + aim at the powered spotlight so the lit lamps + beam actually read
                env.AmbientLightEnergy = 0.05f; env.BackgroundColor = new Color(0.02f, 0.02f, 0.04f);
                dirLight.LightEnergy = 0.06f;
                look = new Vector3(2.6f, 1.0f, 0f);
                cam.Position = new Vector3(2.6f, 2.3f, 6.8f);
                cam.LookAt(look, Vector3.Up);
                if (System.Environment.GetEnvironmentVariable("UG_LOADBAR") == "1")   // instead aim at the powered generator + focus it -> HP/fuel/LOAD bars
                {
                    look = new Vector3(-2.6f, 0.95f, 0f);
                    cam.Position = new Vector3(-2.6f, 1.7f, 4.4f);
                    cam.LookAt(look, Vector3.Up);
                    cam.CullMask &= ~OutlineOverlay.OutlineLayer;
                    CallDeferred(Node.MethodName.AddChild, new OutlineOverlay());
                    placedGen.SetLookFocused(true);
                }
            }

            // (the scripted open-ground aim probe that lived here is now the L1 test deploy.placer_aim)

            // UG_DEPLOYFOCUS=1: verify the look-at outline + HP/fuel billboard on the placed generator (as if looked at)
            if (System.Environment.GetEnvironmentVariable("UG_DEPLOYFOCUS") == "1")
            {
                look = new Vector3(-2.6f, 0.9f, 0f);
                cam.Position = new Vector3(-2.6f, 1.6f, 4.6f);
                cam.LookAt(look, Vector3.Up);
                cam.CullMask &= ~OutlineOverlay.OutlineLayer;   // main cam must NOT draw the silhouette layer (only the overlay's mask cam does)
                CallDeferred(Node.MethodName.AddChild, new OutlineOverlay());
                placedGen.SetLookFocused(true);
            }
            // UG_DEPLOYDMG=smoke|heavy|fire|wreck: force the generator to a damage stage to verify the smoke/fire/wreck visuals
            if (System.Environment.GetEnvironmentVariable("UG_DEPLOYDMG") is string dmgStage)
            {
                look = new Vector3(-2.6f, 1.2f, 0f);
                cam.Position = new Vector3(-2.6f, 2.4f, 6.0f);
                cam.LookAt(look, Vector3.Up);
                placedGen.DebugStage(dmgStage);
            }
            // UG_CAMYAW=<deg>: orbit the camera horizontally around its look target so one scene can be shot from
            // several angles (a break that hides from the front shows from the side). Applied last, over whatever
            // per-mode framing ran above. UG_CAMPITCH raises/lowers the eye by the same orbit for a higher/lower view.
            ApplyCamOrbit(cam, look);
            GD.Print("[DEPLOYTEST] generator+spotlight placed; blue+red ghosts");
        }

        // Orbit a camera around its look target so one scene can be captured from several angles.
        // UG_CAMYAW=<deg> swings the eye horizontally around the target; UG_CAMPITCH=<deg> raises/lowers it.
        // Both default to 0 (no change), so an unset scene renders exactly as before. Re-aims at the target after.
        static void ApplyCamOrbit(Camera3D cam, Vector3 look)
        {
            float yaw = ReadDeg("UG_CAMYAW"), pitch = ReadDeg("UG_CAMPITCH");
            if (Mathf.Abs(yaw) < 0.01f && Mathf.Abs(pitch) < 0.01f) return;
            var offset = cam.Position - look;
            if (Mathf.Abs(yaw) > 0.01f) offset = offset.Rotated(Vector3.Up, Mathf.DegToRad(yaw));
            if (Mathf.Abs(pitch) > 0.01f)
            {   // tilt about the horizontal axis perpendicular to the (post-yaw) view direction
                var flat = new Vector3(offset.X, 0f, offset.Z);
                var axis = flat.LengthSquared() > 1e-6f ? flat.Normalized().Cross(Vector3.Up) : Vector3.Right;
                offset = offset.Rotated(axis.Normalized(), Mathf.DegToRad(pitch));
            }
            cam.Position = look + offset;
            cam.LookAt(look, Vector3.Up);
        }

        static float ReadDeg(string name) =>
            System.Environment.GetEnvironmentVariable(name) is string s
            && float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : 0f;

        void Ghost(DeployableDef def, bool valid, Vector3 surface, float yaw)
        {
            var g = Deployable.BuildMesh(def, out Aabb ab);
            g.MaterialOverride = valid ? DeployablePlacer.ValidMat : DeployablePlacer.InvalidMat;
            AddChild(g);
            g.GlobalTransform = new Transform3D(DeployableDef.StandBasis(yaw), surface + Vector3.Up * DeployableDef.GroundLift(ab));
            if (System.Environment.GetEnvironmentVariable("UG_WIREARROWS") == "1")   // mirror DeployablePlacer: in/out port arrows on the ghost (blueprint blue/red)
            {
                var mat = ConnectionPort.ArrowMaterial(valid ? ConnectionPort.ArrowBlue : ConnectionPort.ArrowRed);
                foreach (var p in def.Ports) g.AddChild(ConnectionPort.MakeArrow(p, mat, p.Pos));
            }
        }

        // --croptest=NAME: a farm crop showcase -- the YOUNG (Foliage_0) crop left, the GROWN (Foliage_1) crop right,
        // both on a dirt plane, 3/4 cam. Validates the extracted crop meshes/textures + growth-stage swap + orientation.
        void BuildCropTest(string name)
        {
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.5f, 0.6f, 0.72f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.72f, 0.72f, 0.74f), AmbientLightEnergy = 1.0f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-50f, -40f, 0f), LightEnergy = 1.2f });
            // dirt ground plane
            AddChild(new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(6f, 6f) },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.38f, 0.32f, 0.2f), Roughness = 1f } });
            CropRegistry.Load();   // dirt _Color per crop from content/crops.tsv (tools/batch_crops.py)
            var young = CropNode.Spawn(name); young.Position = new Vector3(-0.5f, 0f, 0f); young.SetGrown(false); AddChild(young);
            var grown = CropNode.Spawn(name); grown.Position = new Vector3(0.5f, 0f, 0f); grown.SetGrown(true); AddChild(grown);
            var cam = new Camera3D { Current = true, Fov = 45f, Far = 1000f };
            AddChild(cam);
            cam.Position = new Vector3(0f, 0.85f, 2.0f);
            cam.LookAt(new Vector3(0f, 0.2f, 0f), Vector3.Up);
            GD.Print($"[CROPTEST] {name}: young(Foliage_0) left, grown(Foliage_1) right");
        }

        // --skillsui: render the SkillsUI with a sample PlayerSkills (some XP + a few leveled) to showcase/validate it.
        void BuildSkillsUiShot()
        {
            var skills = new SDG.Unturned.PlayerSkills();
            skills.AwardExperience(500);
            skills.TryUpgrade((int)SDG.Unturned.EPlayerSpeciality.SUPPORT, (int)SDG.Unturned.EPlayerSupport.CRAFTING);
            skills.TryUpgrade((int)SDG.Unturned.EPlayerSpeciality.SUPPORT, (int)SDG.Unturned.EPlayerSupport.AGRICULTURE);
            skills.TryUpgrade((int)SDG.Unturned.EPlayerSpeciality.SUPPORT, (int)SDG.Unturned.EPlayerSupport.AGRICULTURE);
            skills.TryUpgrade((int)SDG.Unturned.EPlayerSpeciality.OFFENSE, (int)SDG.Unturned.EPlayerOffense.SHARPSHOOTER);
            var ui = new SkillsUI { SkillsSource = skills };
            AddChild(ui);
            ui.Open();
            GD.Print("[skillsui] opened skills menu with a sample PlayerSkills");
        }





        // --itemtest=ID,ID,...: drop those loot items as real physics WorldItems from a small height onto a ground plane,
        // to eyeball the extracted mesh + primary albedo + best-fit box AND that they FALL + settle (gravity, no float).
        void BuildItemTest(string ids)
        {
            SDG.Unturned.ItemCatalog.RegisterAll();   // so new Item(id).GetAsset() resolves the real name + rarity colour (glow/label)
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.30f, 0.34f, 0.40f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.55f, 0.56f, 0.60f), AmbientLightEnergy = 0.35f,   // low ambient + strong sun (like in-game) so inverted-winding/normals actually SHOW (high ambient masks it)
            };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-50f, -40f, 0f), LightEnergy = 1.5f, ShadowEnabled = true });

            // ground: a wide static box on the world layer (bit0) so the items rest on it + a matching visible slab
            var ground = new StaticBody3D { CollisionLayer = 1u << 0, CollisionMask = 0 };
            if (System.Environment.GetEnvironmentVariable("UG_TRIMESH") == "1")   // repro the real terrain: a THIN trimesh surface (items tunnel through it w/o CCD)
                ground.AddChild(new CollisionShape3D { Shape = new PlaneMesh { Size = new Vector2(24f, 8f) }.CreateTrimeshShape() });
            else
                ground.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(24f, 1f, 8f) }, Position = new Vector3(0, -0.5f, 0) });
            ground.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(24f, 1f, 8f) }, Position = new Vector3(0, -0.5f, 0),
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.22f, 0.24f, 0.22f), Roughness = 1f } });
            AddChild(ground);

            bool norot = !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("UG_NOROT"));
            bool focus = !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("UG_FOCUS"));   // UG_FOCUS=1 -> highlight the middle item (look-at outline + name preview)
            // (the look-END sphere is player-driven -- not shown in this static harness; UG_FOCUS previews the outline directly)
            WorldItem.NoDropRotation = norot;   // UG_NOROT=1 -> hold each item at IDENTITY (frozen) to read the raw model orientation
            var parts = ids.Split(',', System.StringSplitOptions.RemoveEmptyEntries);
            const float span = 1.7f;
            float x0 = -(parts.Length - 1) * span * 0.5f;
            var spawned = new System.Collections.Generic.List<WorldItem>();
            for (int i = 0; i < parts.Length; i++)
            {
                if (!ushort.TryParse(parts[i].Trim(), out var id)) continue;
                var wi = WorldItem.Spawn(this, new Item(id), new Vector3(x0 + i * span, norot ? 0.7f : 1.2f, 0f));   // drop from 1.2 m -> it must FALL to the plane (norot: hold at 0.7 for the shot)
                if (norot) wi.Freeze = true;   // freeze at identity so physics doesn't settle it -> see the authored up-orientation
                spawned.Add(wi);
                AddChild(new Label3D { Text = id.ToString(), Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, FontSize = 40, PixelSize = 0.006f,
                    Position = new Vector3(x0 + i * span, 1.85f, 0f), Modulate = new Color(1f, 1f, 0.6f) });
            }
            if (focus && spawned.Count > 0) spawned[spawned.Count / 2].SetFocused(true);   // preview the look-at highlight on the middle item

            var cam = new Camera3D { Current = true, Fov = 52f, Far = 10000f };
            cam.CullMask &= ~OutlineOverlay.OutlineLayer;   // the mask cam renders the item silhouettes, not this one
            AddChild(cam);
            float w = Mathf.Max(3f, parts.Length * span);
            cam.Position = new Vector3(0f, 1.5f, w * 0.85f + 1.2f);
            cam.LookAt(new Vector3(0f, 0.15f, 0f), Vector3.Up);
            CallDeferred(Node.MethodName.AddChild, new OutlineOverlay());   // screen-space outline overlay (so UG_FOCUS previews it)
            GD.Print($"[ITEMTEST] dropped {parts.Length} items: {ids}");
        }

        // --animrig=NAME: build a rigged animal from content/NAME_rig.json at its REST pose (no clips) + RGB axes + auto-framed
        // 3/4 cam. Validates the skeleton/skin extraction -> does the deer STAND (vs the splayed raw bind-pose mesh)?
        void BuildAnimRig(string name)
        {
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.32f, 0.36f, 0.44f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.75f, 0.75f, 0.75f), AmbientLightEnergy = 1.0f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-45f, -35f, 0f), LightEnergy = 1.2f });
            var rc = RiggedCharacter.Build($"res://content/{name}_rig.json", new Color(0.52f, 0.36f, 0.22f), false, null, null);
            if (rc == null) { GD.PrintErr($"[ANIMRIG] FAILED to build {name}"); GetTree().Quit(); return; }
            AddChild(rc);
            { var clip = System.Environment.GetEnvironmentVariable("UG_CLIP"); if (!string.IsNullOrEmpty(clip)) rc.Play(clip); }   // UG_CLIP=Run/Walk/Idle to preview a clip (else rest pose)
            var aabb = rc.Body != null ? rc.Body.GetAabb() : new Aabb(Vector3.Zero, Vector3.One);
            var c = aabb.GetCenter(); float r = Mathf.Max(aabb.Size.X, Mathf.Max(aabb.Size.Y, aabb.Size.Z)); if (r < 0.01f) r = 1.5f;
            foreach (var (ax, col) in new[] { (Vector3.Right, new Color(1f, 0.15f, 0.15f)), (Vector3.Up, new Color(0.15f, 1f, 0.15f)), (Vector3.Back, new Color(0.2f, 0.4f, 1f)) })
            {
                var bar = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.05f, 0.05f, 0.05f) * r + ax.Abs() * r * 1.1f }, MaterialOverride = new StandardMaterial3D { AlbedoColor = col } };
                bar.Position = ax * r * 0.55f;
                AddChild(bar);
            }
            var cam = new Camera3D { Current = true, Fov = 50f, Far = 10000f };
            AddChild(cam);
            cam.Position = c + new Vector3(r * 1.2f, r * 0.8f, r * 1.2f);
            cam.LookAt(c, Vector3.Up);
            GD.Print($"[ANIMRIG] {name} body aabb size={aabb.Size} center={c} bones={rc.Skeleton?.GetBoneCount()}");
        }

        // --rottest=NAME: place one prop under a candidate placement-rotation convention (UG_ROTCONV 0-3) with a chosen
        // euler (UG_EULER="ex,ey,ez", default = the PEI lighthouse's 270,194,0) + RGB axes, to hunt the upside-down.
        void BuildRotTest(string name)
        {
            float F(string s) => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.32f, 0.36f, 0.44f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.75f, 0.75f, 0.75f), AmbientLightEnergy = 1.0f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-45f, -35f, 0f), LightEnergy = 1.2f });
            string dir = ProjectSettings.GlobalizePath("res://content/objects/");
            var mesh = ObjMesh.Load(dir + name + ".obj");
            if (mesh == null) { GD.PrintErr($"[ROTTEST] no mesh {name}"); GetTree().Quit(); return; }
            var mat = new StandardMaterial3D { Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled, VertexColorUseAsAlbedo = true };
            string tp = dir + name + "_tex.png";
            if (System.IO.File.Exists(tp)) { var img = new Image(); if (img.Load(tp) == Error.Ok) { img.GenerateMipmaps(); mat.AlbedoTexture = ImageTexture.CreateFromImage(img); } }
            var es = (System.Environment.GetEnvironmentVariable("UG_EULER") ?? "270,194,0").Split(',');
            float ex = F(es[0]), ey = F(es[1]), ez = F(es[2]);
            int conv = int.TryParse(System.Environment.GetEnvironmentVariable("UG_ROTCONV"), out var rc) ? rc : 0;
            var Y = new Vector3(0, 1, 0); var X = new Vector3(1, 0, 0); var Z = new Vector3(0, 0, 1);
            float D(float d) => Mathf.DegToRad(d);
            Basis ConvBasis(float px, float py, float pz)
            {
                Basis Ru = new Basis(Y, D(py)) * new Basis(X, D(px)) * new Basis(Z, D(pz));   // Unity ZXY euler
                switch (conv)
                {
                    case 1: return new Basis(Y, D(180f - py)) * new Basis(X, D(px)) * new Basis(Z, D(pz)); // shipped (roll-buggy)
                    case 2: return Ru;                                                                      // all positive
                    case 3: return new Basis(Y, D(py)) * new Basis(X, D(-px)) * new Basis(Z, D(-pz));
                    case 5: return new Basis(new Vector3(Ru.X.X, Ru.X.Y, -Ru.X.Z), new Vector3(Ru.Y.X, Ru.Y.Y, -Ru.Y.Z), new Vector3(Ru.Z.X, Ru.Z.Y, -Ru.Z.Z)); // C*Ru (raw-mesh reflection)
                    case 7: { var qu = new Quaternion(Y, D(py)) * new Quaternion(X, D(px)) * new Quaternion(Z, D(pz)); return new Basis(new Quaternion(qu.X, qu.Y, -qu.Z, -qu.W)); } // Unity quat -> ToGodot
                    case 8: return new Basis(Y, D(180f - py)) * new Basis(X, D(px)) * new Basis(Z, D(-pz)); // conv1 but NEGATE roll (mesh frame flips pitch+roll) -- =conv1 at ez=0
                    case 9: return new Basis(Y, D(180f - py)) * new Basis(X, D(-px)) * new Basis(Z, D(-pz)); // rigorous negate-Z conj + 180 yaw: -pitch, -roll
                    default: return new Basis(Y, D(-py)) * new Basis(X, D(-px)) * new Basis(Z, D(pz)); // 0 = old upside-down
                }
            }
            if (System.Environment.GetEnvironmentVariable("UG_CLOCKS") != null)   // clock-row: the 4 Alberton bank clocks (c0 correct + 3 rolled) side-by-side to hunt the roll-safe conv
            {
                var clocks = new[] { new[] { 270f, 0f, 0f }, new[] { 45f, 270f, 90f }, new[] { 45f, 90f, 270f }, new[] { 325f, 270f, 90f } };
                for (int i = 0; i < clocks.Length; i++)
                {
                    var e = clocks[i];
                    var root = new Node3D { Transform = new Transform3D(ConvBasis(e[0], e[1], e[2]), new Vector3(i * 3f, 0f, 0f)) };
                    AddChild(root);
                    root.AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = mat });
                    foreach (var (ax, col) in new[] { (X, new Color(1f, 0.2f, 0.2f)), (Y, new Color(0.2f, 1f, 0.2f)), (Z, new Color(0.3f, 0.5f, 1f)) })
                        root.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.06f, 0.06f, 0.06f) + ax.Abs() * 1.4f }, MaterialOverride = new StandardMaterial3D { AlbedoColor = col }, Position = ax * 0.7f });
                }
                var ccam = new Camera3D { Current = true, Fov = 60f, Far = 10000f };
                AddChild(ccam); ccam.Position = new Vector3(4.5f, 2.5f, 8f); ccam.LookAt(new Vector3(4.5f, 0f, 0f), Vector3.Up);
                GD.Print($"[CLOCKROW] conv={conv} (leftmost=c0 correct, next 3 = rolled)");
                return;
            }
            var rot = ConvBasis(ex, ey, ez);
            var xf = new Transform3D(rot, Vector3.Zero);
            AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = mat, Transform = xf });
            foreach (var (ax, col) in new[] { (X, new Color(1f, 0.15f, 0.15f)), (Y, new Color(0.15f, 1f, 0.15f)), (Z, new Color(0.2f, 0.4f, 1f)) })
            {
                var bar = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.5f, 0.5f, 0.5f) + ax.Abs() * 20f }, MaterialOverride = new StandardMaterial3D { AlbedoColor = col } };
                bar.Position = ax * 10f; AddChild(bar);
            }
            var taabb = xf * mesh.GetAabb(); var c = taabb.GetCenter(); float r = Mathf.Max(taabb.Size.X, Mathf.Max(taabb.Size.Y, taabb.Size.Z)); if (r < 0.01f) r = 5f;
            var cam = new Camera3D { Current = true, Fov = 55f, Far = 10000f };
            AddChild(cam); cam.Position = c + new Vector3(r * 1.1f, r * 0.6f, r * 1.1f); cam.LookAt(c, Vector3.Up);
            GD.Print($"[ROTTEST] {name} conv={conv} euler=({ex},{ey},{ez}) tAABB={taabb.Size} center={c}");
        }

        // active holiday (src HolidayUtil schedule + -Holiday override -> UG_HOLIDAY). Gates the ~285 in-season
        // Christmas/Halloween props placed on PEI so they don't show year-round.
        static string ActiveHoliday()
        {
            var o = System.Environment.GetEnvironmentVariable("UG_HOLIDAY");
            if (!string.IsNullOrEmpty(o)) return o.ToUpperInvariant();
            var n = System.DateTime.Now;
            if ((n.Month == 12 && n.Day >= 7) || (n.Month == 1 && n.Day <= 2)) return "CHRISTMAS";
            if ((n.Month == 10 && n.Day >= 20) || (n.Month == 11 && n.Day <= 1)) return "HALLOWEEN";
            if (n.Month == 2 && n.Day == 14) return "VALENTINES";
            if (n.Month == 4 && n.Day == 1) return "APRIL_FOOLS";
            if (n.Month == 6) return "PRIDE_MONTH";
            if (n.Month == 7 && n.Day == 7) return "UNTURNED_ANNIVERSARY";
            return "NONE";
        }

        // --objects: PEI's real placed objects (Level/Objects.dat) instanced on the terrain. placements.txt = every
        // object's guid+transform; guid_mesh.txt maps the top types to extracted object.prefab meshes.
        // UG_TREECHECK: raycast horizontally through the first ~40 tree trunks at several heights -> prove the collider is
        // actually hittable (i.e., Jolt didn't drop the shape). Prints a WORKS/BROKEN verdict.
        void DoTreeCheck()
        {
            var space = GetViewport().World3D.DirectSpaceState;
            var trees = GetTree().GetNodesInGroup("tree");
            int tested = 0, hit = 0;
            foreach (Node nd in trees)
            {
                if (nd is not StaticBody3D body) continue;
                if (tested >= 60) break;
                var cs = body.GetChildOrNull<CollisionShape3D>(0);
                if (cs == null) continue;
                Vector3 c = body.GlobalTransform * cs.Position;   // exact trunk-collider centre (no height guessing)
                var q = PhysicsRayQueryParameters3D.Create(c + new Vector3(1.3f, 0f, 0f), c - new Vector3(1.3f, 0f, 0f), 1u << 0);   // short ray through it -> won't grab a neighbour
                var r = space.IntersectRay(q);
                tested++;
                bool h = r.Count > 0 && r["collider"].As<Node>() is Node hn && hn.IsInGroup("tree");
                if (h) hit++;
                if (tested <= 5)
                {
                    var cyl = cs.Shape as CylinderShape3D;
                    string what = r.Count > 0 ? ((Node)r["collider"].As<Node>()).Name : "MISS";
                    GD.Print($"[treecheck#{tested}] bodyPos={body.GlobalPosition} centre={c} r={cyl?.Radius:0.00} h={cyl?.Height:0.00} enabled={!cs.Disabled} ray->{what}");
                }
            }
            GD.Print($"[treecheck] {hit}/{tested} tree trunks solid -> collision {(tested > 0 && hit >= tested - 2 ? "WORKS" : "PARTIAL/BROKEN")}");
        }

        // The real-world assembly now lives in WorldBuilder.BuildFullWorld (MP_PLAN §4 Phase 3: one world
        // path for SP/client/dedicated); this wrapper keeps the flag plumbing + capture fields identical.
        // With _bakeNav the build runs fully synchronously (zero awaits), so the --bakenav/--navpathtest/
        // --zombietest call sites can keep using the built world immediately after this returns.
        async void BuildObjectsTest()
        {
            _worldBuild = true;   // --shot waits for _worldReady (below) so the async world (incl. Trees) is fully loaded before the screenshot
            var res = await WorldBuilder.BuildFullWorld(this, _peiPlayable ? WorldMode.Playable : WorldMode.Aerial,
                _mapRoot, _mapPlace, _noZombies, syncLoad: _bakeNav, bakeNav: _bakeNav, ActiveHoliday());
            _pdPlayer = res.Player;   // UG_AUTOFIRE terrain-impact verification
            _ztField = res.Zombies;   // --zombietest reads this at frame 25 to verify spawns land on the navmesh
            if (res.HasVehicleAim && !_vHave) { _vAim = res.VehicleAim; _vHave = true; }
            AttachMpLoopback(res);    // --mploopback only; default SP is untouched
            if (res.Ready) _worldReady = true;   // async world fully built (terrain..trees) -> the --shot harness can now capture a loaded frame
        }

        bool _mpLoopback;   // --mploopback: opt-in SP listen-server over MemTransport (MP_PLAN §4 Phase 4)
        void AttachMpLoopback(WorldBuildResult res)
        {
            if (!_mpLoopback || res.Player == null || res.Sim == null) return;
            AddChild(new MpLoopback { Player = res.Player, Driver = res.Sim,
                                      DayNight = res.DayNight, Resources = res.Resources });   // Phase 8 world-state syncs (§3.7)
        }

        // --navshot=OUT: a VERIFY screenshot for the zombie nav rework -- synchronous world (loads reliably offline),
        // the baked navmesh pockets painted as a translucent floor overlay, a ring of zombies with their vision cones
        // wireframed, aerial cam over a central pocket. Waits a few settle frames, saves the PNG, quits.
        void BuildNavShot(string outPath)
        {
            var env = new Godot.Environment { AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.72f, 0.70f, 0.62f), AmbientLightEnergy = 1f };
            AddChild(new WorldEnvironment { Environment = env });
            var sun = new DirectionalLight3D { LightEnergy = 1.3f, ShadowEnabled = true, RotationDegrees = new Vector3(-55f, 35f, 0f) };
            AddChild(sun);

            var terr = Terrain.LoadMapMerged(MapDir("PEI") + "/Landscape/Heightmaps", withCollider: true);
            if (terr == null) return;
            AddChild(terr);

            var pockets = ZombieNav.LoadPockets(MapDir("PEI"));
            ZombieNav.BuildOrLoad(this, pockets, overlay: true, save: false);   // verify shot: terrain-only, don't overwrite the canonical full-world bake

            var cam = new Camera3D { Current = true };
            AddChild(cam);
            if (System.Environment.GetEnvironmentVariable("UG_NAVFULL") == "1")   // zoomed-out full-island map of ALL 19 pockets (top-down, north up)
            {
                cam.Fov = 72f;
                cam.Position = new Vector3(0f, 1650f, 0f);
                cam.RotationDegrees = new Vector3(-90f, 0f, 0f);   // straight down: +X = east, -Z = north (map orientation)
                cam.Near = 1200f; cam.Far = 2200f;   // terrain is all ~1.4-1.7km away -> a tight near/far restores depth precision + kills the z-fighting that hid pockets at this zoom
            }
            else   // close-up over one pocket with a ring of zombies + their vision cones
            {
                CharacterModel.LoadBundled();
                Vector3 look = Vector3.Zero;
                if (pockets.Count > 0)
                {
                    int pkIdx = int.TryParse(System.Environment.GetEnvironmentVariable("UG_NAVPOCKET"), out var pi) ? Mathf.Clamp(pi, 0, pockets.Count - 1) : 3;   // UG_NAVPOCKET=N -> close up on pocket N
                var pk = pockets[pkIdx];
                    float cy = terr.SampleHeight(pk.Center.X, pk.Center.Z);
                    look = new Vector3(pk.Center.X, cy, pk.Center.Z);
                    for (int i = 0; i < 6; i++)
                    {
                        float ang = i / 6f * Mathf.Tau;
                        float zx = pk.Center.X + 9f * Mathf.Cos(ang), zz = pk.Center.Z + 9f * Mathf.Sin(ang);
                        var z = new ZombieController { Speciality = ZombieController.ESpeciality.NORMAL };
                        AddChild(z);
                        z.GlobalPosition = new Vector3(zx, terr.SampleHeight(zx, zz) + 0.05f, zz);
                        z.LookAt(new Vector3(look.X, z.GlobalPosition.Y, look.Z), Vector3.Up);   // face the pocket centre so the cones point inward
                        z.AddChild(NavDebug.ConeWire(18f, 55f, new Color(1f, 0.9f, 0.2f)));
                    }
                }
                cam.Fov = 62f;
                cam.GlobalPosition = look + new Vector3(0f, 60f, 36f);
                cam.LookAt(look, Vector3.Up);
            }
            _shotPath = outPath; _navShot = true;
            GD.Print($"[NAVSHOT] terrain + {pockets.Count} nav pockets (overlay) + zombie cones; capturing -> {outPath}");
        }

        // --peiplay: the world assembly lives in WorldBuilder.BuildPeiPlayWorld (MP_PLAN §4 Phase 3);
        // this wrapper keeps the capture plumbing (_peiPlayer drives the scripted drop/enter/drive).
        void BuildPeiPlay()
        {
            var res = WorldBuilder.BuildPeiPlayWorld(this, MapDir("PEI"), _peiHorde);
            _peiPlayer = res.Player;
            AttachMpLoopback(res);   // --mploopback only; default SP is untouched
        }






        // --extractblueprints: walk the retail item .dats -> content/blueprints.tsv (the blueprint catalog the
        // BlueprintRegistry loads, since the port bundles only a few item .dats). Reuses the verified BlueprintDef parse.
        static void RunExtractBlueprints()
        {
            SDG.Unturned.ItemCatalog.RegisterAll();
            string baseDir = @"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\Items";
            string outPath = ProjectSettings.GlobalizePath("res://content/blueprints.tsv");
            if (!System.IO.Directory.Exists(baseDir)) { GD.Print($"[BPEXTRACT] no Items dir {baseDir}"); return; }
            var lines = new System.Collections.Generic.List<string>();
            int items = 0, bps = 0;
            foreach (var datPath in System.IO.Directory.GetFiles(baseDir, "*.dat", System.IO.SearchOption.AllDirectories))
            {
                if (System.IO.Path.GetFileName(datPath).Equals("English.dat", System.StringComparison.OrdinalIgnoreCase)) continue;
                string text;
                try { text = System.IO.File.ReadAllText(datPath); } catch { continue; }
                if (!text.Contains("Blueprints")) continue;
                SDG.Unturned.IDatDictionary d;
                try { d = new SDG.Unturned.DatParser().Parse(text); } catch { continue; }
                string ownerId = d.GetString("ID");
                if (string.IsNullOrEmpty(ownerId)) continue;
                var list = BlueprintDef.ParseAll(d, ownerId);
                if (list.Count == 0) continue;
                items++;
                foreach (var bp in list) { lines.Add(bp.ToTsv()); bps++; }
            }
            System.IO.File.WriteAllLines(outPath, lines);
            GD.Print($"[BPEXTRACT] {items} craftable items, {bps} blueprints -> content/blueprints.tsv");
        }


        // The melee/fall/stance/broken-legs/grenade self-tests that lived here as frame-scripted drivers are now L1
        // GameTests: player.stance_stealth_radius, player.fall_damage, player.broken_legs_mend, combat.melee_kill,
        // combat.grenade_falloff (game/testing/tests/) -- run via `./test.sh` or `godot --headless -- --tests`.

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
        // UG_PORT: run a second dedicated server / client pair beside a live one on the same box (dev
        // smoke, C4) -- overrides the port for --dedicated and --connect; unset = the standard 47872.
        static ushort PortEnv() => ushort.TryParse(System.Environment.GetEnvironmentVariable("UG_PORT"), out var p) && p != 0 ? p : NetPort;
        string _connectHost = "127.0.0.1";   // --connect=<ip>: the dedicated server to join (default = same-machine loopback)
        bool _playableClient;                // --connect= (vs bare --client): attach the C3 ClientWorldSession (predicted shell) instead of the ClientNode demo renderer

        // Headless DEDICATED server (MP_PLAN §4 Phase 3): the REAL world via WorldBuilder (dedicated mode --
        // no camera/HUD/viewmodel/local player) + a NetServerSession over UdpServerTransport. The world's
        // SimDriver ticks the whole thing at 50 Hz with replication registered LAST (§2.5). syncLoad: a
        // server has no loading screen to paint -- block until the world stands, then start serving.
        async void BuildDedicated()
        {
            // async void swallows exceptions silently -- a bad map path used to leave the server dead with no
            // log and no bound socket. Surface anything that goes wrong + hard-exit so systemd restarts cleanly.
            try
            {
                var res = await WorldBuilder.BuildFullWorld(this, WorldMode.Dedicated, _mapRoot, _mapPlace,
                    // C4: the dedicated world is POPULATED -- zombies ON by default for the test server;
                    // --nozombies or UG_DEDICATED_NOZOMBIES=1 gives a quiet server, no code change
                    noZombies: _noZombies || System.Environment.GetEnvironmentVariable("UG_DEDICATED_NOZOMBIES") == "1",
                    syncLoad: true, bakeNav: false, activeHoliday: ActiveHoliday());
                AddChild(new DedicatedServer { Port = PortEnv(), Driver = res.Sim, Terr = res.Terr,   // Terr: server grenades bounce on real terrain height (Phase 5)
                    DayNight = res.DayNight, Resources = res.Resources, MapRoot = _mapRoot,          // Phase 8: tick-derived clock + resource bitmap + nav-pocket relevancy cells (§3.7/§2.6)
                    RemoteAvatars = true,                                                            // C2: remote peers get real avatar bodies (real spawns/collision/jump) on this world
                    AllowCheats = System.Environment.GetEnvironmentVariable("UG_DEDICATED_NOCHEATS") != "1" });   // test server: give/xp/skill console cheats ON (useful for testing); set UG_DEDICATED_NOCHEATS=1 to lock them off, no code change (review C1 toggle)
                _worldReady = res.Ready;
                GD.Print($"[DEDICATED] world up (terrain={(res.Terr != null ? "real map" : "fallback plane")}); listening on udp {PortEnv()}");
            }
            catch (System.Exception e)
            {
                GD.PrintErr($"[DEDICATED] world build FAILED: {e}");
                GetTree().Quit(1);
            }
        }

        // Headless demo server process (+ a scripted bot player) -- the visible 2-process demo, now riding
        // NetSession + the replication framing (NetWorldServer) instead of the deleted NetGame prototype.
        void BuildServer()
        {
            AddChild(new ServerNode { Port = NetPort });
            GD.Print($"[SERVER] demo NetWorldServer + scripted bot on udp {NetPort}");
        }

        // Rendering client process (PEI_CLIENT_PLAN §3 Phases C1+C3): the REAL map world through the ONE
        // WorldBuilder path (Client mode -- terrain/objects/colliders + roads/foliage/trees + day-night,
        // no local player), then the net client joins the dedicated server. --connect= (the playable
        // client) attaches ClientWorldSession: a real first-person PlayerController shell spawns at the
        // server-adopted spawn, predicted + reconciled -- its camera IS the view (no overhead cam). Bare
        // --client keeps the C1 demo shape: overhead cam + ClientNode's capsule renderer (used by the
        // --server 2-process demo; no player shell).
        async void BuildClient()
        {
            // async void swallows exceptions silently (the trap BuildDedicated hit) -- surface anything that breaks.
            try
            {
                var res = await WorldBuilder.BuildFullWorld(this, WorldMode.Client, _mapRoot, _mapPlace,
                    noZombies: true, syncLoad: false, bakeNav: false, activeHoliday: ActiveHoliday());
                if (res.Terr == null)
                {
                    // FAIL-FAST (C1): a client without the retail map cannot render the world the server is
                    // simulating -- say exactly what to fix; never silently fall back to the old demo arena.
                    GD.PrintErr($"[CLIENT] map not found at {_mapRoot} -- set UG_UNTURNED_DIR to a local Unturned install (or install Unturned). NOT joining.");
                    var layer = new CanvasLayer { Layer = 200 };   // above the LoadingScreen (128) the aborted build left up
                    var bg = new ColorRect { Color = new Color(0.04f, 0.05f, 0.07f) };
                    bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
                    layer.AddChild(bg);
                    var msg = new Label
                    {
                        Text = "PEI map not found -- set UG_UNTURNED_DIR to your Unturned install (or install Unturned)",
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    msg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
                    msg.AddThemeFontSizeOverride("font_size", 26);
                    msg.AddThemeColorOverride("font_color", new Color(1f, 0.45f, 0.40f));
                    layer.AddChild(msg);
                    AddChild(layer);
                    return;
                }
                CharacterModel.LoadBundled();   // remote players render as the real ripped character mesh (this call lived only in the dead ScatterScenery)
                _worldReady = res.Ready;
                if (_playableClient)   // --connect= (C3): the predicted first-person shell -- its camera is the view once the join snapshot seeds the spawn
                {
                    AddChild(new ClientWorldSession { Host = _connectHost, Port = PortEnv(), Driver = res.Sim, Sun = res.Sun, Env = res.Env,
                                                      DayNight = res.DayNight, Resources = res.Resources,   // C5: the world-state views drive these
                                                      Terr = res.Terr });                                    // C6: terrain-snaps the vehicle-exit spot (§7 risk 6)
                    GD.Print($"[CLIENT] real world up ({System.IO.Path.GetFileName(_mapRoot)}); connecting to {_connectHost}:{PortEnv()} -- the local shell spawns at the server-adopted spawn, predicted + reconciled");
                }
                else   // bare --client (C1 demo shape): overhead cam over the spawn region + ClientNode capsules
                {
                    var cam = new Camera3D { Current = true, Fov = 62f, Far = 20000f };
                    AddChild(cam);
                    var ctr = res.HasPlayerSpawn ? res.PlayerSpawn : Vector3.Zero;   // hover the real spawn region, not the origin (open water on PEI)
                    cam.Position = ctr + new Vector3(0f, 50f, 44f);
                    cam.LookAt(ctr, Vector3.Up);
                    AddChild(new ClientNode { Host = _connectHost, Port = NetPort });
                    GD.Print($"[CLIENT] real world up ({System.IO.Path.GetFileName(_mapRoot)}); connecting to {_connectHost}:{NetPort} over NetSession; players rendered from server snapshots");
                }
            }
            catch (System.Exception e)
            {
                GD.PrintErr($"[CLIENT] world build FAILED: {e}");
            }
        }

        // In-process 2-player network demo: a real NetWorldServer + two NetWorldClients over loopback UDP,
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

            AddChild(new NetDemoNode { Port = 47871 });
            GD.Print("[NETDEMO] NetWorldServer + 2 NetWorldClients on loopback UDP (NetSession + snapshot/command planes); rendering server-synced players");
        }

        // --navpathtest: once the nav map has synced (a few frames), query the baked navmesh + report whether paths route around obstacles.
        void RunNavPathTest()
        {
            var map = GetViewport().World3D.NavigationMap;
            GD.Print($"[navpath] map active={NavigationServer3D.MapIsActive(map)} regions={NavigationServer3D.MapGetRegions(map).Count}");
            var pk = ZombieNav.LoadPockets(_mapRoot);
            int routed = 0, ok = 0;
            for (int i = 0; i < pk.Count; i++)
            {
                var c = pk[i].Center; float hx = pk[i].HalfExtent.X, hz = pk[i].HalfExtent.Z; float qy = 40f;   // query near terrain (navmesh ~25-70m; Center.Y is the bounds mid ~140)
                foreach (var ab in new[] { (new Vector3(c.X - hx * 0.6f, qy, c.Z), new Vector3(c.X + hx * 0.6f, qy, c.Z)),
                                           (new Vector3(c.X, qy, c.Z - hz * 0.6f), new Vector3(c.X, qy, c.Z + hz * 0.6f)) })
                {
                    var A = NavigationServer3D.MapGetClosestPoint(map, ab.Item1);
                    var B = NavigationServer3D.MapGetClosestPoint(map, ab.Item2);
                    var path = NavigationServer3D.MapGetPath(map, A, B, true);
                    if (path.Length >= 2)
                    {
                        float plen = 0f; for (int k = 1; k < path.Length; k++) plen += path[k - 1].DistanceTo(path[k]);
                        float straight = A.DistanceTo(B);
                        bool routes = path.Length > 2 && plen > straight * 1.12f;
                        ok++; if (routes) routed++;
                        GD.Print($"[navpath] pocket {i}: pts={path.Length} len={plen:0.#} straight={straight:0.#} snapY={A.Y:0.#} -> {(routes ? "ROUTES AROUND" : "straight/open")}");
                    }
                    else GD.Print($"[navpath] pocket {i}: NO PATH (snapA={A} snapB={B})");
                }
            }
            GD.Print($"[navpath] {routed} queries ROUTED AROUND obstacles, {ok} valid paths -> zombie pathfinding {(ok > 0 ? "WORKS on the baked navmesh" : "FAILED")}");
            GetTree().Quit();
        }

        // --zombietest: verify the pocket-based spawner puts zombies ON the baked navmesh (so the Phase-2 agent can path from spawn).
        void RunZombieTest()
        {
            var map = GetViewport().World3D.NavigationMap;
            GD.Print($"[zombietest] map active={NavigationServer3D.MapIsActive(map)} regions={NavigationServer3D.MapGetRegions(map).Count} pockets={_ztField?.PocketCount ?? 0}");
            if (_ztField == null) { GD.Print("[zombietest] no ZombieField (zombies disabled?)"); GetTree().Quit(); return; }
            var plan = _ztField.DebugPlanSpawns();
            int n = plan.Count, onNav = 0; float worst = 0f, sum = 0f;
            foreach (var (pk, pos) in plan)
            {
                var snap = NavigationServer3D.MapGetClosestPoint(map, pos);
                float d = new Vector2(snap.X - pos.X, snap.Z - pos.Z).Length();   // horizontal distance to nearest navmesh poly
                if (d <= 1.5f) onNav++;
                sum += d; if (d > worst) worst = d;
            }
            GD.Print($"[zombietest] planned {n} zombie spawns; {onNav}/{n} within 1.5m of the baked navmesh ({(n > 0 ? 100f * onNav / n : 0):0.#}%), avg snap {(n > 0 ? sum / n : 0):0.##}m, worst {worst:0.#}m");
            GD.Print($"[zombietest] {(n > 0 && onNav >= n * 0.85f ? "PASS -- zombies spawn on the navmesh, ready to pathfind" : "CHECK -- many spawns off-navmesh (bucketing or navmesh gap?)")}");
            GetTree().Quit();
        }




        public override void _Process(double delta)
        {
            if (_menuShotDir != null && _menuShotMenu != null)   // step the menu camera through its 5 anchors, capture each
            {
                _frame++;
                // switch to anchor i, then capture ~45 frames later once the glide has settled (title gets a longer slow pan)
                int[] switchAt = { 0, 20, 40, 60, 80 };
                int[] shotAt = { 15, 35, 55, 75, 95 };
                if (_menuShotIdx < switchAt.Length && _frame == switchAt[_menuShotIdx]) _menuShotMenu.ShowTab(_menuShotIdx);
                if (_menuShotIdx < shotAt.Length && _frame == shotAt[_menuShotIdx])
                {
                    var mi = GetViewport().GetTexture().GetImage();
                    string p = $"{_menuShotDir}/menu_{_menuShotIdx:D2}.png";
                    mi.SavePng(p);
                    GD.Print($"[MENUSHOT] saved {p} (frame {_frame})");
                    _menuShotIdx++;
                    if (_menuShotIdx >= shotAt.Length) GetTree().Quit();
                }
                return;
            }
            if (_navPathTest) { if (++_frame >= 25) { _navPathTest = false; RunNavPathTest(); } return; }   // let the nav map sync a few frames, then query
            if (_zombieTest) { if (++_frame >= 25) { _zombieTest = false; RunZombieTest(); } return; }   // let the nav map sync, then verify pocket spawns land on it
            if (System.Environment.GetEnvironmentVariable("UG_PERF") == "1" && (_perfT -= (float)delta) <= 0f)
            {
                _perfT = 1f;
                int zc = GetTree().GetNodesInGroup("zombies").Count;
                double physMs = Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000.0;
                double procMs = Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0;
                GD.Print($"[perf] fps={Engine.GetFramesPerSecond()} zombies={zc} physicsMs={physMs:0.0} processMs={procMs:0.0} draws={Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame)}");
            }
            if (_fireTest && _ftPlayer != null) { _ftFrame++; if (_ftFrame >= 60 && _ftFrame % 15 == 0) _ftPlayer.Fire(); }   // own counter -- the _frame demo loop below is gated on _rigDir
            if (_peiPlay && _peiPlayer != null)
            {
                _peiFrame++;
                if (System.Environment.GetEnvironmentVariable("UG_AUTOFIRE") == "1") { if (_peiFrame >= 55 && (_peiFrame % 12 == 0 || _peiFrame >= 156)) _peiPlayer.Fire(); }   // impact-render test: stay on foot + fire forward; sustained burst 156+ so a muzzle FLASH lands on the frame-160 capture (glow showcase)
                else if (System.Environment.GetEnvironmentVariable("UG_FP") == "1") { if (System.Environment.GetEnvironmentVariable("UG_EAT") is string _eatAt && _eatAt.Length > 0 && _peiFrame == (int.TryParse(_eatAt, out var _ef) ? _ef : 100)) _peiPlayer.StartConsume(); }   // UG_FP: on foot for the FP viewmodel; UG_EAT=<startFrame> click-eat (frame-160 capture lands that many frames into the eat/drink)
                else if (_peiFrame == 50) _peiPlayer.EnterNearestVehicle(); else if (_peiFrame >= 55) _peiPlayer.ScriptedDrive = new Vector2(0f, 1f);   // settle onto PEI, hop in, drive forward (--horde: the loud drive aggros the zombie field -> roadkill)
            }
            if (_peiPlayable && _pdPlayer != null && System.Environment.GetEnvironmentVariable("UG_AUTOFIRE") == "1" && _worldReady && _pdFireT++ % 8 == 0) _pdPlayer.Fire();   // peidrive: fire at the real terrain -> verify the SurfAt material impacts render
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
                else if (_vmTest && _vm != null && !_vmMelee)   // gun scripted sequence: ADS -> hip-fire (Kick) -> reload; a melee never fires/aims/reloads, so skip it (its MeleeSwingDriver drives the swings)
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
                    if (System.Environment.GetEnvironmentVariable("UG_HAMMER") == "1" && _frame == 50) _vm.PlayHammer();   // verify the rack rotates the gun (bone-follow)
                }
                if (_pivots)   // --pivots: pin the arrows to the live coupling points; no driving
                {
                    if (_vehCam != null)
                    {
                        if (System.Environment.GetEnvironmentVariable("UG_PIVCLOSE") == "1")   // zoom TIGHT on the TRAILER's coupler (~Z6.4 world) to place the kingpin precisely
                        { _vehCam.GlobalPosition = new Vector3(3.6f, 1.1f, 5.6f); _vehCam.LookAt(new Vector3(0f, 0.62f, 6.4f), Vector3.Up); }
                        else
                        { _vehCam.GlobalPosition = new Vector3(24f, 8.5f, 8f); _vehCam.LookAt(new Vector3(0f, 1.2f, 7f), Vector3.Up); }   // pulled-back 3/4 view framing both models
                    }
                    foreach (var (mark, veh, local) in _pivotMarks)
                        if (IsInstanceValid(mark) && IsInstanceValid(veh)) mark.GlobalPosition = veh.ToGlobal(local);
                }
                else if (_vehTest && _veh != null)
                {
                    // settle, then auto-drive a course for the video: straight -> right curve -> left curve
                    if (_backunder)   // reverse straight back UNDER the parked trailer, couple in reach, then PULL FORWARD to prove the rig drives
                    {
                        if (_veh.CoupledTrailer == null)
                        {
                            _veh.Drive(-0.55f, 0f, false);
                            if (_buTrailer != null && _veh.CoupleTo(_buTrailer)) { _buCoupledFrame = _frame; GD.Print($"[backunder] coupled OK at frame {_frame}"); }
                        }
                        else _veh.Drive(_frame > _buCoupledFrame + 50 ? 1f : 0f, _frame > _buCoupledFrame + 160 ? 0.4f : 0f, false);   // hitched -> HOLD ~50 frames (see if the magnetize centered the off-center trailer at rest) then drive forward
                    }
                    else
                    {
                    float throttle = (!_chain && _frame > 30) ? 1f : 0f;   // --chain: stay parked so the blast reaches the neighbours
                    float steer = _frame < 120 ? 0f : (_frame < 235 ? 0.45f : -0.45f);
                    _veh.Drive(throttle, steer, false);
                    }
                    if (_chain && _frame == 20) _veh.TakeDamage(9999f);   // detonate _veh -> ~4 s later it blows -> chains to the car + horde
                    if (_roadkill && _frame == 35) _veh.Honk();   // honk before reaching them -> verify the horn's noise alert (source tellHorn AlertTool.alert 32)
                    if (_demo && (_frame == 45 || _frame == 80 || _frame == 115)) _veh.Honk();   // --demo: a few horn honks
                    if (_demo && _frame >= 40 && _frame < 100 && _frame % 8 == 0) _veh.TakeDamage(90f);   // --demo: damage -> smoke -> explode
                    if (_vehCam != null)   // chase cam: behind the jeep's heading (flattened), above -- shows the red taillights at night
                    {
                        var vt = _veh.GlobalTransform;
                        var fwd = -vt.Basis.Z; fwd.Y = 0f;
                        fwd = fwd.LengthSquared() > 0.001f ? fwd.Normalized() : Vector3.Forward;
                        if (System.Environment.GetEnvironmentVariable("UG_SIDE") == "1")   // diagnostic PURE side profile (collider vs mesh height — pair with UG_COLLVIS=1); UG_CAMDIST=N pulls it back + shifts along the body to frame a long rig
                        {
                            var right = new Vector3(fwd.Z, 0f, -fwd.X);   // fwd rotated -90 about Y
                            float sd = 12f; var sde = System.Environment.GetEnvironmentVariable("UG_CAMDIST");
                            if (!string.IsNullOrEmpty(sde) && float.TryParse(sde, out var sdv)) sd = sdv;
                            var center = vt.Origin - fwd * (sd * 0.35f) + Vector3.Up * 1.1f;   // shift toward the trailer so the whole cab+trailer fits
                            _vehCam.GlobalPosition = center + right * sd + Vector3.Up * 0.3f;
                            _vehCam.LookAt(center, Vector3.Up);
                        }
                        else if (!string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("UG_VSIDE")))   // diagnostic 3/4 front-side profile (see body + wheel placement); =2 flips to the STARBOARD side
                        {
                            var right = new Vector3(fwd.Z, 0f, -fwd.X);   // fwd rotated -90 about Y
                            if (System.Environment.GetEnvironmentVariable("UG_VSIDE") == "2") right = -right;
                            _vehCam.GlobalPosition = vt.Origin + fwd * 7.5f + right * 5.5f + Vector3.Up * 2.6f;
                            _vehCam.LookAt(vt.Origin + Vector3.Up * 1.2f, Vector3.Up);
                        }
                        else
                        {
                            float cd = 7.5f; var cde = System.Environment.GetEnvironmentVariable("UG_CAMDIST");   // UG_CAMDIST=N pulls the rear chase cam back (to frame a long cab+trailer rig)
                            if (!string.IsNullOrEmpty(cde) && float.TryParse(cde, out var cdv)) cd = cdv;
                            _vehCam.GlobalPosition = vt.Origin - fwd * cd + Vector3.Up * (3.2f + cd * 0.15f);
                            _vehCam.LookAt(vt.Origin + Vector3.Up * 0.7f - fwd * (cd * 0.5f), Vector3.Up);   // aim at the rig's midpoint so both cab + trailer rear are framed
                        }
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
            if (_worldReady && !_treeChecked && System.Environment.GetEnvironmentVariable("UG_TREECHECK") == "1" && ++_treeCheckFrame > 15) { _treeChecked = true; DoTreeCheck(); }
            if (_shotPath == null) return;
            if (_peiPlay) { if (_peiFrame < (_peiHorde ? 130 : 160)) return; }   // peiplay: drop(~25f)+enter(50f)+drive(55f+); --horde captures mid-plow through the zombie field
            else if (_itemTest) { if (++_frame < 90) return; }   // itemtest: let the dropped items FALL + settle onto the plane before the shot
            else if (_driveTest) { if (++_frame < 120) return; }   // drivetest: let the car spawn+enter+drive (+ --demo damage->explosion) play out before the shot
            else if (_fireTest) { if (_ftPlayer == null || _ftPlayer.Ammo > 20 || _ftFrame < 75) return; }   // firetest: capture once ~10 shots fired (high-cap: Ammo<=20); the _ftFrame>=75 floor lets a low-cap gun (launcher = 1 rocket at frame 60) actually fire + impact before the quit
            else if (_worldBuild) { if (!_worldReady || ++_frame < 45) return; }   // objects/peidrive: WAIT for the async world (terrain..trees) to finish + settle before the shot
            else if (_navShot) { if (++_frame < 24) return; }   // navshot: let lighting/shadows + the overlay settle before capture
            else if (System.Environment.GetEnvironmentVariable("UG_DEPLOYDMG") != null) { if (++_frame < 45) return; }   // deploytest damage: let smoke/fire particles accumulate before the shot
            else if (System.Environment.GetEnvironmentVariable("UG_WIREWRECK") == "1") { if (++_frame < 20) return; }   // shatter: catch the debris collapsing toward the ground
            else if (System.Environment.GetEnvironmentVariable("UG_WIRETEST") == "1") { if (++_frame < 50) return; }   // wire test: let the lamp warmup envelope settle (past the flicker ramp) before capturing steady state
            else if (++_frame < 6) return; // let the renderer settle
            if (_spotDbg != null && IsInstanceValid(_spotDbg)) GD.Print($"[LAMPDBG] consumerPowered={_spotDbg.DebugConsumerPowered} lampsLit={_spotDbg.DebugLampsLit}");   // plain UG_WIRETEST render: a wired+powered spotlight's lamps must be on
            var img = GetViewport().GetTexture().GetImage();
            if (img == null) { GD.PrintErr("[SHOT] null image -- run with a rendering driver (e.g. --rendering-driver vulkan), NOT --headless"); GetTree().Quit(); return; }
            img.SavePng(_shotPath);
            GD.Print($"[SHOT] saved {_shotPath} ({img.GetWidth()}x{img.GetHeight()})");
            GetTree().Quit();
        }
    }

    // Drives the melee self-test: after a few settle frames, swings every physics tick (the cooldown gates it to
    // ~0.45 s). Quits when the zombie dies (Kills > 0) or after a timeout, so the run self-terminates for log-check.
    public partial class MeleeSwingDriver : Node3D
    {
        public Viewmodel VM;
        int _f;
        public override void _PhysicsProcess(double delta)
        {
            _f++;
            if (VM == null) return;
            if (VM.HasStartSwing) { if (_f == 25) { VM.StartTorch(); VM.SetTorchSparks(true); } }   // Repeated tool: play Start_Swing once + emit the real nozzle sparks (continuous while "held")
            else if (_f % 35 == 25) VM.SwingMelee();                   // normal melee: periodic weak swings for the --vm render
        }
    }

    // --vm --gun=generator: periodically play the Deploy_Use place motion so the render shows both the hold + place.
    public partial class DeployUseDriver : Node3D
    {
        public Viewmodel VM;
        int _f;
        public override void _PhysicsProcess(double delta)
        {
            _f++;
            // default: STAY in the neutral carry hold (verify the hold framing). UG_DEPLOYPLACE=1 -> periodic place motion.
            if (VM != null && System.Environment.GetEnvironmentVariable("UG_DEPLOYPLACE") == "1" && _f % 60 == 40) VM.PlayDeployUse();
        }
    }

    // (The MeleeTest/FallTest/Pronetest/BrokenTest/GrenadeTest frame-scripted drivers that lived here are now L1
    //  GameTests under game/testing/tests/ -- see PlayerTests.cs + CombatTests.cs.)
}
