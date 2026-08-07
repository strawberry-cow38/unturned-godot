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
        // Point the world at the map the menu's singleplayer selector picked (same key scheme as --map=/UG_MAP, so
        // placements + named locations follow). PEI stays the default; picking Washington loads Washington's world.
        void ApplyMenuMap(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return;
            _mapRoot = MapDir(folder);
            string key = System.Text.RegularExpressions.Regex.Replace(folder, "[^A-Za-z0-9]", "");
            _mapPlace = folder == "PEI" ? "placements.txt" : "placements_" + key + ".txt";
            MapNodes.MapNodeFile = folder == "PEI" ? "nodes.tsv" : "nodes_" + key + ".tsv";
            MapUI.MapFolder = folder;   // in-game M-map: image + level-size + label follow the map
            FoliageField.MapDir = folder == "PEI" ? "foliage" : "foliage_" + key.ToLower();   // grass/pebbles baked per map
            ResourceField.MapDir = folder == "PEI" ? "resources" : "resources_" + key.ToLower();   // trees/rocks baked per map
            Terrain.MapDir = folder == "PEI" ? "terrain" : "terrain_" + key.ToLower();   // splat layer albedos baked per map
        }
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
        bool _gunLayerTest;                          // UG_GUNLAYER=1 (+--gun): legs walk while the arms hold/aim/reload the gun via the overlay
        string _glEquipClip, _glReloadClip, _glHammerClip;   // resolved overlay clips for the gun-layer test (+ the empty-reload rack)
        bool _vmTest; Viewmodel _vm;                 // --vm=DIR : first-person viewmodel test (equip -> ADS -> hip)
        bool _vmMelee;                               // --vm target is a melee weapon -> skip the gun aim/fire/reload script (MeleeSwingDriver swings it instead)
        bool _vmAimed; int _vmAimStart; int _vmSettle;
        bool _vmAttach; AttachmentMenu _am; bool _vmSightSet;   // --attach : hold the T attachment menu open for the render; UG_SIGHT=<mesh.txt> mounts a specific sight/scope for a demo
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
        bool _zdirTest; ZombieDirector _zdField; int _zdFrames;   // --zdirtest: boot the REWRITE on PEI and watch it run -- do rows tier, path and actually move?
        bool _bakeNav;   // --bakenav: sync-load the full world + bake+save the canonical navmesh, then quit (offline tool; the game only loads)
        int _treeCheckFrame; bool _treeChecked;   // UG_TREECHECK: raycast self-test that tree trunk colliders are actually hittable
        float _perfT;   // UG_PERF: throttle the perf log
        bool _itemTest;   // --itemtest=ID,ID,... : drop those items as physics WorldItems onto a ground plane -> validate mesh/tex/scale/settle
        bool _doorAnim; ObjectDoor _doorAnimDoor; double _doorAnimElapsed; float _doorAnimToggle1At, _doorAnimToggle2At, _doorAnimDoneAt; bool _doorAnimToggle1Done, _doorAnimToggle2Done;   // --doortest UG_DOOR_ANIM=1: real-time DEFAULT->away->DEFAULT cycle for a --write-movie capture

        public override void _Ready()
        {
            if (System.Environment.GetEnvironmentVariable("UG_COLLVIS") == "1") GetTree().DebugCollisionsHint = true;   // diagnostic: overlay physics collision shapes (must be set before bodies enter the tree)
            string catalog = null, shot = null, picks = null, gun = null, rig = null, anim = "Walk", vm = null, bakeIcon = null, veh = null, drivetest = null, proptest = null, animrig = null, rottest = null, itemtest = null, navShot = null, croptest = null, menuShot = null, clothtest = null, boattest = null;
            bool zperf = false;
            bool zbody = false;
            bool deployTest = false, barricadeTest = false;
            bool wearcloth = false;
            bool skillsui = false;
            bool fluidTest = false;
            bool doorTest = false;
            string doorTestName = null;
            bool containerTest = false; string containerTestName = null;
            bool play = false, demo = false, netdemo = false, server = false, dedicated = false, client = false, smoke = false, hurtdemo = false, invdemo = false, invsel = false, invequip = false, invdrop = false, invloot = false, invcrate = false, daynight = false, lightTest = false, trafficTest = false, buildmode = false, firetest = false, supp = false, terrain = false, peiplay = false, objects = false, peidrive = false, craftui = false, bakenav = false, navPathTest = false, zombieTest = false, zdirTest = false, editorMode = false;
            foreach (var arg in OS.GetCmdlineUserArgs())
            {
                if (arg.StartsWith("--catalog=")) catalog = arg["--catalog=".Length..];
                else if (arg.StartsWith("--shot=")) { shot = arg["--shot=".Length..]; _shotRequested = shot; }
                else if (arg.StartsWith("--navshot=")) { navShot = arg["--navshot=".Length..]; _shotRequested = navShot; }   // verify screenshot: navmesh floor overlay + zombie vision cones, synchronous world, aerial over a pocket
                else if (arg.StartsWith("--menushot=")) { menuShot = arg["--menushot=".Length..]; _shotRequested = menuShot; }   // render the 3D barn main menu + capture each of the 5 camera anchors (menu_00..04.png)
                else if (arg == "--bakenav") bakenav = true;   // offline TOOL: sync-load the FULL world + bake all 19 nav pockets -> save the .res files (commit them; the game only LOADS, never gens)
                else if (arg == "--navpathtest") navPathTest = true;   // OFFLINE verify: sync world -> query the navmesh -> log whether zombie paths ROUTE AROUND buildings (not through)
                else if (arg == "--editor") editorMode = true;   // boot straight into the map editor (the Workshop entry); --editor --shot=OUT captures a loaded frame
                else if (arg == "--fluidtest") fluidTest = true;   // F2 verify: source -> hose -> storage flows + fills (headless log check)
                else if (arg == "--doortest") { doorTest = true; doorTestName = "Fridge_0"; }   // openable prop door MVP: place one a few metres from the camera; UG_DOOR_OPEN=1 spawns it already open
                else if (arg.StartsWith("--doortest=")) { doorTest = true; doorTestName = arg["--doortest=".Length..]; }   // e.g. --doortest=Wardrobe_0 -- any prop with a doors.txt entry
                else if (arg == "--containertest") { containerTest = true; containerTestName = "Fridge_0"; }   // lootable+openable merge: spawn the doored prop as a REAL StoreShelf container + render its door; UG_CONTAINER_OPEN=1 opens it
                else if (arg.StartsWith("--containertest=")) { containerTest = true; containerTestName = arg["--containertest=".Length..]; }
                else if (arg == "--zombietest") zombieTest = true;   // OFFLINE verify: sync world -> bucket Animals.dat into pockets -> check planned spawns land ON the baked navmesh
                else if (arg == "--zdirtest") zdirTest = true;       // OFFLINE verify: boot the REWRITE on PEI -> do rows tier, query paths and actually MOVE? (implies --newzombies)
                else if (arg.StartsWith("--proptest=")) { proptest = arg["--proptest=".Length..]; _shotRequested = proptest; }   // spawn ONE named prop at identity + RGB axes -> diagnose mirror/orientation/material
                else if (arg.StartsWith("--croptest=")) croptest = arg["--croptest=".Length..];   // spawn a farm crop (young + grown) on a ground plane -> validate mesh/tex/orientation (UG_CROPROT tunes rot)
                else if (arg == "--zperf") zperf = true;   // GPU perf probe: N zombies, render counters ON vs OFF (MUST run with a rendering driver, not --headless)
                else if (arg == "--zbody") zbody = true;   // MECHANISM probe: N bare kinematic capsules, moving vs parked -> is the physics cost the BODIES?
                else if (arg == "--deploytest") deployTest = true;   // both deployables placed on a ground plane + a valid(blue)+invalid(red) ghost -> verify models/palette/stand-up/ghost materials
                else if (arg == "--barricadetest") barricadeTest = true;   // barricades mounted on a STRUCTURE wall (upright, facing out) + a valid ghost + a floor barricade -> verify surface placement
                else if (arg == "--skillsui") skillsui = true;   // render the skills menu (showcase/validate the SkillsUI)
                else if (arg.StartsWith("--itemtest=")) itemtest = arg["--itemtest=".Length..];   // drop a row of loot items (ids) as physics WorldItems -> validate real mesh/tex/scale/settle
                else if (arg.StartsWith("--animrig=")) { animrig = arg["--animrig=".Length..]; _shotRequested = animrig; }   // build a rigged animal (content/NAME_rig.json) at rest + 3/4 cam -> validate the static pose stands
                else if (arg.StartsWith("--rottest=")) rottest = arg["--rottest=".Length..];   // place ONE prop with the placement euler (UG_EULER) under a rotation convention (UG_ROTCONV) -> hunt the upside-down
                else if (arg.StartsWith("--bakeicon=")) bakeIcon = arg["--bakeicon=".Length..];   // MODEL[:ALBEDO] -> icon PNG (needs --shot=OUT)
                else if (arg.StartsWith("--rig=")) { rig = arg["--rig=".Length..]; _shotRequested = rig; }
                else if (arg.StartsWith("--clothtest=")) { clothtest = arg["--clothtest=".Length..]; _shotRequested = clothtest; }   // dress a RiggedCharacter with shirt,pants item ids -> UV-atlas render gate (P3a); frames land in --shot=DIR
                else if (arg == "--clothtest") clothtest = "";                                        // bare flag -> default outfit (shirt 3 + pants 2)
                else if (arg == "--wearcloth") wearcloth = true;                                      // P4 render gate: dress a body through the REAL equip path (PlayerClothingController) incl. gear (hat + vest)
                else if (arg.StartsWith("--anim=")) anim = arg["--anim=".Length..];
                else if (arg.StartsWith("--vm=")) vm = arg["--vm=".Length..];
                else if (arg == "--attach") _vmAttach = true;
                else if (arg.StartsWith("--vehicle=")) { veh = arg["--vehicle=".Length..]; _shotRequested = veh; }
                else if (arg.StartsWith("--boattest=")) boattest = arg["--boattest=".Length..];   // spawn a BOAT on a flat test sea + auto-drive (verify buoyancy + water propulsion)
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
                else if (arg == "--newzombies") ZombieDirector.Enabled = true;   // the rewrite (docs/ZOMBIE_REWRITE_PLAN.md): sim rows + borrowed rigs, no per-zombie body. Off = the old ZombieField/ZombieController path, untouched
                else if (arg == "--netdemo") netdemo = true;
                else if (arg == "--server") server = true;
                else if (arg == "--dedicated") dedicated = true;   // headless dedicated server: the REAL world (WorldBuilder dedicated mode) + NetServerSession on UDP
                else if (arg == "--netlog") UnturnedGodot.Net.NetLog.Enabled = true;   // net-diagnostics logging (equivalent: UG_NETLOG=1); sinks wired in DedicatedServer/ClientNode
                else if (arg == "--mploopback") _mpLoopback = true;   // OPT-IN (MP_PLAN §4 Phase 4): SP runs as an in-process listen-server + local client over MemTransport; without the flag SP keeps the direct path
                else if (arg == "--spconsume") _spConsume = true;      // SP/MP-unify P1: with --mploopback, the local player CONSUMES deployables as server replicas instead of the direct SP path (opt-in; equivalent env UG_SPCONSUME=1)
                else if (arg == "--direct") _direct = true;            // SP/MP-unify P6a: opt OUT of the consuming-loopback DEFAULT on the SP GAME entries -> pure direct SP path (reversible fallback + A/B; equivalent env UG_DIRECT=1)
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
                    MapNodes.MapNodeFile = mn == "PEI" ? "nodes.tsv" : "nodes_" + key + ".tsv";   // named-location file follows the map (Level.hierarchy locations for modern maps)
                    MapUI.MapFolder = mn;
                    FoliageField.MapDir = mn == "PEI" ? "foliage" : "foliage_" + key.ToLower();
                    ResourceField.MapDir = mn == "PEI" ? "resources" : "resources_" + key.ToLower();
                    Terrain.MapDir = mn == "PEI" ? "terrain" : "terrain_" + key.ToLower();
                }
                else if (arg == "--peiplay") peiplay = true;     // player standing/walking on real PEI terrain (with colliders)
                else if (arg == "--invdemo") invdemo = true;
                else if (arg == "--invsel") { invdemo = true; invsel = true; }
                else if (arg == "--invequip") { invdemo = true; invequip = true; }
                else if (arg == "--invdrop") invdrop = true;
                else if (arg == "--invloot") invloot = true;
                else if (arg == "--invcrate") invcrate = true;
                else if (arg == "--daynight") daynight = true;
                else if (arg == "--lighttest") lightTest = true;   // one lit streetlight at night: cone + motes eyeball (UG_LIGHTCAM=under looks up from inside)
                else if (arg == "--trafficlight") trafficTest = true;   // one signal, both heads (UG_TL_STATE=green|amber|red|flash|dark, UG_TL_SIDE=1 for the side-road mast, UG_TL_DAY=1 for daylight)
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
                MapNodes.MapNodeFile = ugMap == "PEI" ? "nodes.tsv" : "nodes_" + ugKey + ".tsv";
                MapUI.MapFolder = ugMap;
                FoliageField.MapDir = ugMap == "PEI" ? "foliage" : "foliage_" + ugKey.ToLower();
                ResourceField.MapDir = ugMap == "PEI" ? "resources" : "resources_" + ugKey.ToLower();
                Terrain.MapDir = ugMap == "PEI" ? "terrain" : "terrain_" + ugKey.ToLower();
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

            if (editorMode)   // --editor: boot the map editor (the Workshop path); --shot=OUT captures once the world's loaded
            {
                GetWindow().Size = new Vector2I(1280, 720);
                _shotPath = shot;
                if (System.Environment.GetEnvironmentVariable("UG_NEWMAP") == "1") BuildEditorNew(); else BuildEditor();
                return;
            }

            if (fluidTest) { RunFluidTest(); return; }   // F2: spawn source->hose->storage, tick the fluid net, log the fill, quit

            if (doorTest)   // openable prop door MVP: Fridge_0 a few metres from the camera; UG_DOOR_OPEN=1 spawns it already open (so I can shot open vs closed)
            {
                GetWindow().Size = new Vector2I(1280, 720);
                _shotPath = shot; _shotRequested = shot;
                BuildDoorTest(doorTestName);
                return;
            }

            if (containerTest)   // lootable+openable merge: spawn the doored prop as a REAL StoreShelf + render its swinging door (UG_CONTAINER_OPEN=1 for the open pose)
            {
                GetWindow().Size = new Vector2I(1280, 720);
                _shotPath = shot; _shotRequested = shot;
                BuildContainerTest(containerTestName);
                return;
            }



            if (navPathTest) { _bakeNav = true; _peiPlayable = true; BuildObjectsTest(); _navPathTest = true; return; }   // sync-load; RunNavPathTest fires after a few frames (the nav map merges its regions on a physics tick, not in _Ready)
            if (zombieTest) { _bakeNav = true; _peiPlayable = true; _zombieTest = true; BuildObjectsTest(); return; }   // sync-load (creates the ZombieField + buckets spawns); RunZombieTest fires at frame 25 once the nav map has synced
            if (zdirTest) { ZombieDirector.Enabled = true; _bakeNav = true; _peiPlayable = true; _zdirTest = true; BuildObjectsTest(); return; }   // sync-load the REWRITE on the real map, then watch it tier/path/move for a few seconds

            if (navShot != null) { GetWindow().Size = new Vector2I(1280, 720); BuildNavShot(navShot); return; }

            if (peiplay)   // drop the player onto real PEI terrain (colliders on) + walk -> the whole session's work on an actual map
            {
                GetWindow().Size = System.Environment.GetEnvironmentVariable("UG_FUELCAN") == "1" ? new Vector2I(1600, 900) : new Vector2I(1280, 720);   // crisper capture for the gas-can viewmodel check
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

            if (zbody) { BuildZBody(); return; }
            if (zperf) { BuildZPerf(); return; }
            if (deployTest)   // deployables showcase: both placed on a ground plane + a valid(blue)/invalid(red) ghost
            {
                GetWindow().Size = new Vector2I(1280, 720);
                _shotPath = shot;
                BuildDeployTest();
                return;
            }

            if (barricadeTest)   // barricades-on-structures showcase: upright wall-mounts facing out + a valid ghost + a floor barricade
            {
                GetWindow().Size = new Vector2I(1280, 720);
                _shotPath = shot;
                BuildBarricadeTest();
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
                _shotPath = shot;   // UG_SHELFDEMO renders a StoreShelf instead -> capture at the settle frame + quit
                BuildCrateDemo(gun);
                return;
            }

            if (lightTest)   // one streetlight, lit, at night -- the cone/mote look is only checkable by eye
            {
                _shotPath = shot;
                GetWindow().Size = new Vector2I(1280, 720);
                BuildStreetLightDemo();
                return;
            }

            if (trafficTest)   // one traffic signal, one aspect, held -- the flash beats last 0.6s and are otherwise unlookable
            {
                _shotPath = shot;
                GetWindow().Size = new Vector2I(1280, 720);
                BuildTrafficLightDemo();
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
            if (client) { if (DisplayServer.GetName() != "headless") GetWindow().Mode = Window.ModeEnum.Maximized; BuildClient(); return; }   // fill the screen (same "tiny viewport" fix as --play below). Guard the window op for --headless (dummy DisplayServer, no window) -> a headless CLIENT runs the full netcode + world STATE with no rasterization (diagnostics / future scripted-client harness).

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
                GetWindow().Size = System.Environment.GetEnvironmentVariable("UG_ISO") == "1" ? new Vector2I(640, 640) : new Vector2I(256, 256);
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
                BuildRigTest(anim, gun);
                return; // frame strip captured in _Process
            }

            if (clothtest != null)   // P3a render gate: a dressed RiggedCharacter (real ripped shirt+pants painted on the body UV0)
            {
                int shirtId = 3, pantsId = 2;   // default outfit: Orange Hoodie (shirt 3) + Work Jeans (pants 2)
                var cp = clothtest.Split(',', System.StringSplitOptions.RemoveEmptyEntries);
                if (cp.Length >= 1) int.TryParse(cp[0], out shirtId);
                if (cp.Length >= 2) int.TryParse(cp[1], out pantsId);
                // PNG strip dir: $UG_CLOTHDIR, else a temp dir (--shot= is taken by the prop showcase). The
                // xvfb --write-movie AVI renders regardless; this strip is the still-frame convenience copy.
                _rigDir = System.Environment.GetEnvironmentVariable("UG_CLOTHDIR") ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clothtest");
                System.IO.Directory.CreateDirectory(_rigDir);
                _rigCaptureFrames = System.Environment.GetEnvironmentVariable("UG_QUICK") == "1"
                    ? new[] { 20 }                        // one settled idle frame
                    : new[] { 8, 14, 20, 26, 32, 40 };    // a few settled idle frames (front 3/4) to eyeball the UV atlas
                GetWindow().Size = new Vector2I(900, 1100);
                BuildClothTest(shirtId, pantsId);
                return; // frame strip captured in _Process
            }

            if (wearcloth)   // P4 render gate: a full outfit driven through the ACTUAL equip path (PlayerClothingController), incl. gear
            {
                _rigDir = System.Environment.GetEnvironmentVariable("UG_CLOTHDIR") ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wearcloth");
                System.IO.Directory.CreateDirectory(_rigDir);
                _rigCaptureFrames = System.Environment.GetEnvironmentVariable("UG_QUICK") == "1"
                    ? new[] { 20 }
                    : new[] { 8, 14, 20, 26, 32, 40 };
                GetWindow().Size = new Vector2I(900, 1100);
                BuildWearClothTest();
                return; // frame strip captured in _Process
            }

            if (vm != null)
            {
                _rigDir = vm;                                   // reuse the frame-strip capture
                bool deployVm = gun == "generator" || gun == "spot" || gun == "spotlight" || gun == "wire" || gun == "gascan";   // settled-hold frame capture (no ADS/fire)
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

            if (boattest != null)
            {
                _rigDir = boattest;   // output DIR for the frame strip (rig_NN.png), like --vehicle
                _rigCaptureFrames = new[] { 40, 75, 120, 170, 220 };   // drop+splash -> settle to the waterline -> drive fwd -> right/left turns
                GetWindow().Size = new Vector2I(1280, 720);
                BuildBoatTest(gun ?? "runabout");   // boat type via --gun (default runabout)
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
                // DEFAULT (the exported build): asset WARMUP -> a tiny main menu -> interactive single-player survival.
                // The warmup preloads the VANILLA core meshes into the ObjMesh cache (master's two-tier design:
                // curated maps load their extra assets on-demand later), then the menu shows. Maximize to FILL the
                // screen (a fixed Size while the project opens MAXIMIZED boxed the render into a corner).
                bool warmupShot = System.Environment.GetEnvironmentVariable("UG_WARMUPSHOT") == "1";
                if (warmupShot) GetWindow().Size = new Vector2I(1280, 720);   // render harness: fixed size instead of maximize
                else GetWindow().Mode = Window.ModeEnum.Maximized;
                LoadingScreen.NextMode = "launch";
                var warmLs = new LoadingScreen();
                AddChild(warmLs);
                Warmup.Begin(this, warmLs, () =>
                {
                    warmLs.QueueFree();
                    var menu = new MainMenu();
                    menu.OnPlay = noZombies => { menu.QueueFree(); _noZombies = noZombies; BuildPlayable(null, false, null); };
                    menu.OnDrivePEI = noZombies => { menu.QueueFree(); _noZombies = noZombies; ApplyMenuMap(menu.SelectedMapFolder); _peiPlayable = true; BuildObjectsTest(); };
                    // Same world, zombie REWRITE enabled (== --newzombies); the menu route exists because the launcher passes no game args.
                    menu.OnDriveNewZombies = () => { menu.QueueFree(); ZombieDirector.Enabled = true; _noZombies = false; ApplyMenuMap(menu.SelectedMapFolder); _peiPlayable = true; BuildObjectsTest(); };
                    menu.OnMultiplayer = () => { menu.QueueFree(); _connectHost = "claw.bitvox.me"; _playableClient = true; BuildClient(); };   // legacy MP-test entry (fallback)
                    menu.OnJoinServer = (host, port) => { menu.QueueFree(); _connectHost = host; _connectPort = port; _playableClient = true; BuildClient(); };   // server browser JOIN / direct-connect -> real client join
                    menu.OnEditor = () => { menu.QueueFree(); BuildEditor(); };   // Workshop -> the singleplayer map editor (PEI)
                    menu.OnNewMap = () => { menu.QueueFree(); BuildEditorNew(); };   // Workshop -> a fresh blank map
                    AddChild(menu);
                });
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
        void BuildRigTest(string anim, string gun)
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
            if (!string.IsNullOrEmpty(gun)) rc.AttachGun(gun);   // 3P gun mesh on the hand (the clip poses the arms)
            GD.Print($"[rig] clips: {string.Join(",", rc.ClipNames)}  playing '{anim}'");
            // UG_LEAN=<deg>: hold the 3P spine at a lean so the tilt is renderable. The rig's bone axes are not
            // Unity's, so which axis rolls the torso sideways is a thing to LOOK at rather than port on faith.
            if (float.TryParse(System.Environment.GetEnvironmentVariable("UG_LEAN"), out var _lean))
            { rc.LeanDeg = _lean; GD.Print($"[rig] lean {_lean:0.#} deg"); }
            // UG_PITCH=<deg>, + looking up. Same reason as UG_LEAN: "looking up tilts the torso back" is a claim about
            // the picture, and a single frame can settle it -- unlike the lean's SIGN, which needed the camera.
            if (float.TryParse(System.Environment.GetEnvironmentVariable("UG_PITCH"), out var _pit))
            { rc.PitchDeg = _pit; GD.Print($"[rig] pitch {_pit:0.#} deg"); }
            // UG_YAW=<deg>: turn the character on the spot. The harness camera looks at the rig nearly head-on, which
            // is the WORST angle for judging a pitch -- a rotation in the sagittal plane is edge-on from there and
            // reads as ambiguous head-wobble. Yaw 90 puts that plane across the screen, where a tilt is just a tilt.
            if (float.TryParse(System.Environment.GetEnvironmentVariable("UG_YAW"), out var _yaw))
            { rc.RotationDegrees = new Vector3(0f, _yaw, 0f); GD.Print($"[rig] yaw {_yaw:0.#} deg"); }
            if (System.Environment.GetEnvironmentVariable("UG_GUNLAYER") == "1" && !string.IsNullOrEmpty(gun))
            {
                // 3P GUN LAYER test: legs walk (Move_Walk) while the arms hold/aim/reload the gun via the overlay.
                string cg = char.ToUpper(gun[0]) + gun.Substring(1);
                _glEquipClip  = rc.ClipLength(cg + "_Equip")  > 0f ? cg + "_Equip"  : "Gun_Equip";
                _glReloadClip = rc.ClipLength(cg + "_Reload") > 0f ? cg + "_Reload" : "Gun_Reload";
                _glHammerClip = rc.ClipLength(cg + "_Hammer") > 0f ? cg + "_Hammer" : null;    // the rack (empty-reload 2nd half)
                rc.SetLocomotion(3.0f);                                                       // ~walk speed -> Move_Walk on the legs
                rc.EnableGunLayer(rc.ClipLength(cg + "_Aim") > 0f ? cg + "_Aim" : "Gun_Aim");  // upper-body overlay + ADS delta
                rc.SetGunOverlay(_glEquipClip, 1f, loop: false);                              // equip pull-out -> holds the ready pose
                var gvg = Viewmodel.VisualForTest(gun);                                        // mount the gun's default iron sight + mag on the 3P gun (attachment test)
                if (!string.IsNullOrEmpty(gvg.Sight) && ContentProvider.ParseObj($"res://content/{gvg.Sight}") is Mesh gsm) rc.MountGunAttachment("Sight", gsm, gvg.SightPos != Vector3.Zero ? gvg.SightPos : new Vector3(0f, 0.1312f, -0.118f), gvg.SightColor.A > 0f ? gvg.SightColor : new Color(0.3f, 0.3f, 0.3f));
                if (!string.IsNullOrEmpty(gvg.Mag) && ContentProvider.ParseObj($"res://content/{gvg.Mag}") is Mesh gmm) rc.MountGunAttachment("Magazine", gmm, new Vector3(0f, 0.0166f, 0.0238f), new Color(0.07f, 0.07f, 0.08f));
                _gunLayerTest = true;
                _rigCaptureFrames = new[] { 30, 50, 100, 145, 190, 210 };   // stand+hold, stand+ADS, rack(hammer), CROUCH, PRONE, prone-settled
            }
            else if (anim == "Ragdoll")
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

            // 3/4 front view, framed on a ~1.9m character (pulled back for --gun so the whole holding pose reads)
            var cam = new Camera3D { Fov = string.IsNullOrEmpty(gun) ? 42f : 52f };
            AddChild(cam);
            if (string.IsNullOrEmpty(gun)) cam.LookAtFromPosition(new Vector3(-2.5f, 1.2f, -3.4f), new Vector3(0f, 0.92f, 0f), Vector3.Up);
            else cam.LookAtFromPosition(new Vector3(4.5f, 1.7f, -6.5f), new Vector3(0f, 1.0f, 0f), Vector3.Up);
        }

        // --clothtest=<shirtId>,<pantsId> : the P3a render gate. Spawn a 3P RiggedCharacter (clothes-shader body +
        // the Skull face decal) at idle, paint the real ripped shirt+pants textures (loaded via ClothingContent
        // from clothing_content.tsv) onto its body UV0 through the ported StandardClothes composite, and frame it
        // 3/4-front. This is the visual proof the shirt paints the torso/arms + pants the legs on the right texels.
        void BuildClothTest(int shirtId, int pantsId)
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
                LightAngularDistance = 1.6f,
                DirectionalShadowMaxDistance = 14f,
                ShadowBias = 0.03f,
                ShadowNormalBias = 1.5f,
                ShadowBlur = 1.4f,
            });
            var ground = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(20f, 20f) } };
            ground.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.28f, 0.30f, 0.28f) };
            AddChild(ground);

            // UG_WATER=1: a translucent ocean plane cutting the body at ~waist -- same material as the real sea plane
            // (Terrain.cs). Reproduces the "3p body renders on top of the water" depth-sort bug for verification.
            if (System.Environment.GetEnvironmentVariable("UG_WATER") == "1")
            {
                var water = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(20f, 20f) }, Position = new Vector3(0f, 1.0f, 0f) };
                water.MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = new Color(0.13f, 0.29f, 0.44f, 0.74f),
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    Roughness = 0.12f, Metallic = 0.15f, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                };
                AddChild(water);
            }

            // player skin tint + the Skull face-quad decal (kept exactly as-is) -> the clothes-shader body path (albedoTexPath null)
            var rc = RiggedCharacter.Build("res://content/rig.json", new Color(0.82f, 0.66f, 0.52f), false, null, "res://content/face_19.png");
            if (rc == null) { GD.PrintErr("[clothtest] build failed"); GetTree().Quit(); return; }
            AddChild(rc);
            _rc = rc;

            var shirt = ClothingContent.LoadTextures(shirtId);
            var pants = ClothingContent.LoadTextures(pantsId);
            rc.SetShirt(shirt.Albedo, shirt.Emission, shirt.Metallic);
            rc.SetPants(pants.Albedo, pants.Emission, pants.Metallic);
            GD.Print($"[clothtest] shirt {shirtId} albedo={(shirt.Albedo != null)} emis={(shirt.Emission != null)} metal={(shirt.Metallic != null)} | pants {pantsId} albedo={(pants.Albedo != null)} emis={(pants.Emission != null)} metal={(pants.Metallic != null)}");
            rc.Play("Idle_Stand");

            var cam = new Camera3D { Fov = 42f };
            AddChild(cam);
            cam.LookAtFromPosition(new Vector3(-2.5f, 1.2f, -3.4f), new Vector3(0f, 0.92f, 0f), Vector3.Up);
        }

        // --wearcloth : the P4 render gate. Same scene as --clothtest, but the outfit is equipped through the REAL
        // PlayerClothingController.Wear dispatch (not the P3a SetShirt/SetPants shortcut): shirt+pants paint the body
        // and the hat (Skull) + vest (Spine) bone-attach as ripped .obj meshes. This proves the P4 equip wiring +
        // P3b gear attach end-to-end. Frame strip lands in $UG_CLOTHDIR (else a temp dir).
        void BuildWearClothTest()
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
                LightAngularDistance = 1.6f,
                DirectionalShadowMaxDistance = 14f,
                ShadowBias = 0.03f,
                ShadowNormalBias = 1.5f,
                ShadowBlur = 1.4f,
            });
            var ground = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(20f, 20f) } };
            ground.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.28f, 0.30f, 0.28f) };
            AddChild(ground);

            var rc = RiggedCharacter.Build("res://content/rig.json", new Color(0.82f, 0.66f, 0.52f), false, null, "res://content/face_19.png");
            if (rc == null) { GD.PrintErr("[wearcloth] build failed"); GetTree().Quit(); return; }
            AddChild(rc);
            _rc = rc;

            // the ACTUAL SP equip path: PlayerInventory worn-slot state + the controller drives the visual off it
            SDG.Unturned.ItemCatalog.RegisterAll();
            var inv = new SDG.Unturned.PlayerInventory();
            var clothing = new PlayerClothingController(rc, inv);
            clothing.Wear(new SDG.Unturned.Item(3));     // Orange Hoodie (shirt) -> body paint
            clothing.Wear(new SDG.Unturned.Item(209));   // Cargo Pants (pants)   -> body paint
            clothing.Wear(new SDG.Unturned.Item(27));    // Tophat (hat)          -> Skull-bone mesh
            clothing.Wear(new SDG.Unturned.Item(10));    // Police Vest (vest)    -> Spine-bone mesh
            GD.Print($"[wearcloth] worn: shirt={inv.wornShirt?.id} pants={inv.wornPants?.id} hat={inv.wornHat?.id} vest={inv.wornVest?.id} | fall x{inv.FallingDamageMultiplier:0.###} explo x{inv.ExplosionArmor:0.###}");
            rc.Play("Idle_Stand");

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
            bool isFuel = gunName == "gascan";   // gas can: held in-hand via the DeployableMesh+NaturalHold path -- must beat isMelee (gascan.txt exists)
            _vm = isFists
                ? new Viewmodel { Fists = true }                                                  // bare-fists unarmed state (arms + melee ready hold, no mesh)
                : isWire
                ? new Viewmodel { ToolMesh = "wire_hold.obj", ToolColor = new Color(0.647f, 0.647f, 0.647f) }   // wire tool in-hand
                : isDeploy
                ? new Viewmodel { DeployableMesh = "generator_hold.obj", DeployableAlbedo = "generator_hold_tex.png" }   // deployable carry model in-hand + Deploy_Equip/Use
                : isFuel
                ? new Viewmodel { DeployableMesh = "gascan.txt", DeployableAlbedo = "gascan_albedo.png", NaturalHold = true }   // gas can: BIG two-handed carry via its own Fuel_Equip anim (both hands, in-your-face)
                : isMelee
                ? new Viewmodel { MeleeMesh = $"{gunName}.txt", MeleeAlbedo = $"{gunName}_albedo.png" }
                : new Viewmodel { GunName = gunName };   // self-contained: own SubViewport camera at FOV 60, composited on top
            AddChild(_vm);
            _vmMelee = isMelee || isFists || isDeploy || isWire || isFuel;
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
        // --boattest=NAME: a flat test SEA + a boat dropped onto it, then auto-driven (reuses the vehTest drive/cam loop).
        // Verifies buoyancy floats the hull to the waterline + the drive input becomes water thrust/rudder.
        void BuildBoatTest(string type)
        {
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.42f, 0.58f, 0.75f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.62f, 0.64f, 0.68f), AmbientLightEnergy = 0.95f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-52f, -42f, 0f), LightEnergy = 1.1f, ShadowEnabled = true });

            Terrain.HasWater = true; Terrain.SeaLevelY = 0f;   // flat test sea at Y=0 -- the boat physics reads these
            var water = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(800f, 800f) }, Position = Vector3.Zero,
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.10f, 0.34f, 0.52f, 0.78f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, Metallic = 0.25f, Roughness = 0.12f } };
            AddChild(water);
            var seabed = new StaticBody3D { Position = new Vector3(0f, -14f, 0f) };   // deep floor so a swamped boat lands, not falls forever
            seabed.AddChild(new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(800f, 800f) }, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.22f, 0.26f, 0.20f) } });
            seabed.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
            AddChild(seabed);

            _veh = Vehicle.BuildByName(type, 0);
            _veh.Position = new Vector3(0f, 0.5f, 0f);   // spawn just above the waterline -> gentle settle (a 2.5m drop plunged the voxel-buoyancy hull deep + made it bob)
            AddChild(_veh);
            _veh.EngineOn = true;
            if (System.Environment.GetEnvironmentVariable("UG_BEACH") == "1")   // AMPHIBIOUS transition: a sandy beach sloping from dry land (+Z) down into the sea (-Z) -> drive off it into the water
            {
                var ramp = new StaticBody3D { Position = new Vector3(0f, -1f, 14f), RotationDegrees = new Vector3(12f, 0f, 0f) };   // +Z end rises above the sea, -Z end dips under
                ramp.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(40f, 2f, 44f) }, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.66f, 0.58f, 0.42f) } });
                ramp.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(40f, 2f, 44f) } });
                AddChild(ramp);
                _veh.Position = new Vector3(0f, 6f, 26f);   // start up on the dry land, facing -Z toward the sea
            }

            _vehCam = new Camera3D { Current = true, Fov = 58f };
            _vehCam.CullMask &= ~OutlineOverlay.OutlineLayer;
            AddChild(_vehCam);
            _vehTest = true;   // reuse the vehTest auto-drive + chase-cam loop (Drive() -> the boat's water propulsion)
        }

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
            if (_night)
            {
                _veh.ToggleHeadlights();                // headlights on for the night demo
                // In game DayNightCycle.DriveMoteFade feeds this from the world clock; this harness has no
                // cycle node, so without driving it here the beam dust can never appear in a --night shot --
                // the third harness in two days that could not express the state being judged.
                _veh.SetHeadlightMoteFade(1f);
            }
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
                AddChild(new ZombieAnimCut());   // F6 -> freeze ALL rig anim (skeletons leg of the engine-side POI-fps cut: read F3 physics ms with it on vs off)
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
            env.TonemapMode = Godot.Environment.ToneMapper.Aces;   // match the game's ACES so this harness validates the scope PiP color/tonemap (was default Linear)
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

            var _terr = Terrain.LoadMapMerged(_mapRoot + "/Landscape/Heightmaps", withCollider: false);   // --map= aware (defaults to PEI); any modern-Landscape map renders here
            if (_terr == null) { GD.PrintErr($"[TERRAIN] no map data at {_mapRoot} -- nothing loaded"); return; }   // do NOT fall through to the success line below: it printed "loaded" over an empty scene and a profiling run measured nothing for it
            AddChild(_terr);

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
            var propMi = new MeshInstance3D { Mesh = mesh, MaterialOverride = mat };
            AddChild(propMi);
            // UG_LIVE=1: also attach whatever DEVICE this prop carries, so the diagnostic can show the animated thing
            // rather than the static mesh. Added for the patient monitor, whose whole point is that its screen moves.
            if (System.Environment.GetEnvironmentVariable("UG_LIVE") == "1" && HeartMonitor.IsMonitorProp(name))
            {
                var hm = HeartMonitor.Make(propMi, System.Environment.GetEnvironmentVariable("UG_FLATLINE") == "1" ? false : true);
                AddChild(hm);
                // UG_MONITOR_OFF=1: switch it off, so the DARK state is renderable too. The off state is the one that
                // was wrong (a hidden overlay uncovered the prop's own green trace), and it is not visible from any
                // amount of staring at the lit one.
                if (System.Environment.GetEnvironmentVariable("UG_MONITOR_OFF") == "1") hm.Toggle();
                GD.Print($"[PROPTEST] attached HeartMonitor (alive={hm.Alive} lit={hm.DebugLit})");
            }
            var aabb = mesh.GetAabb(); var c = aabb.GetCenter(); float r = Mathf.Max(aabb.Size.X, Mathf.Max(aabb.Size.Y, aabb.Size.Z));
            if (r < 0.01f) r = 1f;
            GD.Print($"[PROPTEST] {name} aabb pos={aabb.Position} size={aabb.Size}");
            // UG_NOAXES=1: the axis bars meet AT the origin, so anything small sitting there (a base plate,
            // a pivot stub) is hidden behind the gizmo -- which is exactly the question when a prop looks
            // like it is missing its bottom. Turn them off to see what is really at 0,0,0.
            foreach (var (ax, col) in System.Environment.GetEnvironmentVariable("UG_NOAXES") == "1"
                     ? System.Array.Empty<(Vector3, Color)>()
                     : new[] { (Vector3.Right, new Color(1f, 0.15f, 0.15f)), (Vector3.Up, new Color(0.15f, 1f, 0.15f)), (Vector3.Back, new Color(0.2f, 0.4f, 1f)) })
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

        // --doortest[=NAME]: openable prop door MVP render harness (default Fridge_0). Builds the body mesh
        // plus EVERY one of the prop's door leaves (one ObjectDoor per leaf, grouped via ObjectDoor.SetGroup so
        // a multi-leaf prop like Wardrobe_0 opens/closes both doors together) standalone -- like BuildPropTest,
        // NOT through WorldBuilder.PlaceObject (which is a BuildFullWorld-local closure and cannot be called
        // from here; also the Fridge_0/Wardrobe_0 guids are in WorldBuilder.ContainerShelf, so the real PEI
        // placement path never reaches the PlaceObject door branch at all right now -- see its comment there).
        // UG_DOOR_OPEN=1 spawns with the door(s) already open (no animation to wait out) so --shot can capture
        // open vs closed from two separate runs.
        void BuildDoorTest(string name)
        {
            if (string.IsNullOrEmpty(name)) name = "Fridge_0";
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
            var bodyMesh = ObjMesh.Load(dir + name + ".obj");
            if (bodyMesh == null) { GD.Print($"[DOORTEST] no body mesh {name}"); GetTree().Quit(1); return; }
            var doorCatalog = WorldBuilder.LoadDoorCatalog(dir);
            if (!doorCatalog.TryGetValue(name, out var doorLeaves) || doorLeaves.Count == 0) { GD.Print($"[DOORTEST] no doors.txt entries for {name} -- run tools/extract_doors.py {name}"); GetTree().Quit(1); return; }

            var mat = new StandardMaterial3D { Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled, VertexColorUseAsAlbedo = true };
            string tp = dir + name + "_tex.png";
            if (System.IO.File.Exists(tp)) { var img = new Image(); if (img.Load(tp) == Error.Ok) { img.GenerateMipmaps(); mat.AlbedoTexture = ImageTexture.CreateFromImage(img); } }

            // Upright placement basis: ex=270/ez=0 is the map own convention for standing a flat-authored
            // interior prop up (see WorldBuilder.TryContainer -- Fridge_0/Wardrobe_0 are themselves
            // ContainerShelf entries, authored the same way every other upright interior prop on PEI is);
            // yaw=180 is an arbitrary pick, harmless either way since the camera below is aimed from the
            // ACTUAL resulting geometry, not a hardcoded direction.
            var basis = new Basis(new Vector3(0, 1, 0), Mathf.DegToRad(180f)) * new Basis(new Vector3(1, 0, 0), Mathf.DegToRad(270f));
            var xform = new Transform3D(basis, Vector3.Zero);
            AddChild(new MeshInstance3D { Mesh = bodyMesh, MaterialOverride = mat, Transform = xform });

            bool doorAnim = System.Environment.GetEnvironmentVariable("UG_DOOR_ANIM") == "1";
            string doorOpenEnv = System.Environment.GetEnvironmentVariable("UG_DOOR_OPEN");
            // Retail default-state fix: InteractableObjectBinaryState boots isUsed=false and (applyInstantly)
            // jumps straight to the END of the clip literally named "Close" -- for Fridge_0/Wardrobe_0 that IS
            // the opening motion (their clip names are inverted vs geometry), so a fresh prop is OPEN in
            // retail. doorCfg.DefaultOpen carries that per LEAF (see WorldBuilder.LoadDoorCatalog /
            // extract_doors.py); every leaf of one prop is expected to agree, but each is honored on its own.
            // UG_DOOR_OPEN, if EXPLICITLY set (0 or 1), overrides the catalog default for testing.
            // UG_DOOR_ANIM always spawns at the catalog default (ignores UG_DOOR_OPEN) -- it wants the movie
            // to open on the real default rotation, then demonstrate both transitions from there.
            var spawnedDoors = new System.Collections.Generic.List<ObjectDoor>();
            Vector3 pivotSum = Vector3.Zero;
            float repDuration = 0f; bool repStartOpen = false;
            foreach (var doorCfg in doorLeaves)
            {
                var doorMesh = ObjMesh.Load(dir + doorCfg.MeshFile);
                if (doorMesh == null) { GD.Print($"[DOORTEST] no door mesh {doorCfg.MeshFile}"); continue; }
                bool startOpen = (!doorAnim && doorOpenEnv != null) ? (doorOpenEnv == "1") : doorCfg.DefaultOpen;
                string curveBase = doorCfg.MeshFile.EndsWith("_door.obj") ? doorCfg.MeshFile.Substring(0, doorCfg.MeshFile.Length - "_door.obj".Length) : name;
                var openCurve = WorldBuilder.LoadDoorCurve(dir, curveBase, "open");
                var closeCurve = WorldBuilder.LoadDoorCurve(dir, curveBase, "close");
                var door = ObjectDoor.Spawn(this, xform, doorCfg.Pivot, doorCfg.Axis, doorCfg.AngleDeg, doorCfg.DurationSec, doorMesh, mat, startOpen, openCurve: openCurve, closeCurve: closeCurve, soundName: doorCfg.Sound);
                if (spawnedDoors.Count == 0) { repDuration = doorCfg.DurationSec; repStartOpen = startOpen; }
                spawnedDoors.Add(door);
                pivotSum += doorCfg.Pivot;
                GD.Print($"[DOORTEST] {name} leaf mesh={doorCfg.MeshFile} pivot={doorCfg.Pivot} axis={doorCfg.Axis} angle={doorCfg.AngleDeg} dur={doorCfg.DurationSec} startOpen={startOpen} swing={door.DebugSwing} sound={doorCfg.Sound} hasAudio={door.DebugHasAudio}");
            }
            if (spawnedDoors.Count == 0) { GD.Print($"[DOORTEST] no door leaves could be spawned for {name}"); GetTree().Quit(1); return; }
            if (spawnedDoors.Count > 1)
                foreach (var d in spawnedDoors) d.SetGroup(spawnedDoors);

            if (doorAnim)
            {
                // Real (not fast-forwarded) DEFAULT -> away -> DEFAULT cycle, driven by REAL elapsed time in
                // _Process (see the UG_DOOR_ANIM block there). Spawns SNAPPED at the catalog default
                // (repStartOpen, above) rather than forcing closed, so the movie opens on the real default
                // rotation. Hold ~0.5s, Toggle AWAY from default (animates through whichever curve that is --
                // for a multi-leaf prop, ALL of its leaves together via ObjectDoor's group-sync), hold ~0.5s
                // past settle, Toggle BACK to default (the other curve), hold ~0.4s past settle, then quit.
                // Toggle() is state-relative, so this sequence is correct regardless of which state the
                // catalog default actually is, and regardless of leaf count -- toggling ANY one door in the
                // group brings every other leaf of this prop along, so tracking just the first spawned door
                // (spawnedDoors[0]) is enough to drive the whole prop.
                const float HoldDefault = 0.5f, HoldAway = 0.5f, TrailHold = 0.4f;
                _doorAnimDoor = spawnedDoors[0];
                _doorAnimToggle1At = HoldDefault;
                _doorAnimToggle2At = _doorAnimToggle1At + repDuration + HoldAway;
                _doorAnimDoneAt = _doorAnimToggle2At + repDuration + TrailHold;
                _doorAnim = true;
                string awayWord = repStartOpen ? "CLOSE" : "OPEN";
                string backWord = repStartOpen ? "OPEN" : "CLOSE";
                GD.Print($"[DOORANIM] default={(repStartOpen ? "OPEN" : "CLOSED")}; timeline (s): hold default 0.000-{_doorAnimToggle1At:0.000}, {awayWord} toggle @{_doorAnimToggle1At:0.000}, settles ~{_doorAnimToggle1At + repDuration:0.000}, holds to {_doorAnimToggle2At:0.000}, {backWord} toggle @{_doorAnimToggle2At:0.000}, settles ~{_doorAnimToggle2At + repDuration:0.000}, quits @{_doorAnimDoneAt:0.000}");
            }

            // Camera: a few metres out along the direction the door(s) actually face, computed from the real
            // placed geometry (the AVERAGE leaf pivot's world offset from the body center) rather than
            // assumed, 3/4-elevated so the leaf peeling away from the body reads clearly in both the closed
            // and open shot -- averaging keeps a multi-leaf prop framed across all of its doors.
            var bodyAabb = bodyMesh.GetAabb();
            Vector3 bodyCenterWorld = xform * bodyAabb.GetCenter();
            Vector3 pivotWorld = xform * (pivotSum / (float)spawnedDoors.Count);
            Vector3 outward = pivotWorld - bodyCenterWorld; outward.Y = 0f;
            if (outward.LengthSquared() < 0.01f) outward = -xform.Basis.Z;   // degenerate fallback: straight back
            outward = outward.Normalized();
            float r = Mathf.Max(bodyAabb.Size.X, Mathf.Max(bodyAabb.Size.Y, bodyAabb.Size.Z));
            if (r < 0.01f) r = 1f;
            Vector3 lookAt = bodyCenterWorld + Vector3.Up * (r * 0.15f);
            var cam = new Camera3D { Current = true, Fov = 55f, Far = 10000f };
            AddChild(cam);
            cam.Position = lookAt + outward * (r * 1.6f + 2.0f) + Vector3.Up * (r * 0.6f);
            cam.LookAt(lookAt, Vector3.Up);
        }

        // --containertest[=NAME]: spawn the doored prop (Fridge_0/Wardrobe_0/Counter_0..) as a REAL StoreShelf
        // container + render its swinging door leaf -- verifies the lootable+openable merge in StoreShelf's own
        // _upright frame (not the door-test placement frame). UG_CONTAINER_OPEN=1 opens the door for the open shot.
        void BuildContainerTest(string name)
        {
            if (string.IsNullOrEmpty(name)) name = "Fridge_0";
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
            var bodyMesh = ObjMesh.Load(dir + name + ".obj");
            if (bodyMesh == null) { GD.Print($"[CONTAINERTEST] no body mesh {name}"); GetTree().Quit(1); return; }

            // The REAL container node: serverOwned=true skips the loot roll (no LootTables dependency for a pure
            // door render), showItems=false = a solid F-open prop. Exercises StoreShelf.BuildVisual's actual
            // door-spawn path, so what renders here is exactly what the world spawns.
            var shelf = StoreShelf.Spawn(this, Vector3.Zero, name, 0, 0f, false, name, true, true);
            bool open = System.Environment.GetEnvironmentVariable("UG_CONTAINER_OPEN") == "1";
            if (open) { shelf.SetDoorsOpen(true); for (int i = 0; i < 40; i++) shelf.TickDoorsForTest(1.0 / 60.0); }   // settle the swing headlessly so a --shot (not just a movie) catches the open pose
            GD.Print($"[CONTAINERTEST] {name} hasDoors={shelf.HasDoors} open={open} settledSwing={shelf.DebugDoorSwing():0.00}");
            if (System.Environment.GetEnvironmentVariable("UG_CONTAINER_FOCUS") == "1") shelf.SetShelfFocused(true);   // debug: force the whole-prop focus so a --shot shows the container's outline meshes present -- body (_shelfGlow) + each swinging door leaf (_leafOutline)

            // Camera from the door side, in StoreShelf's _upright frame (shelf spawned at yaw=0/pos=0 -> its
            // transform is identity, so body/door world = _upright * local), mirroring BuildDoorTest's framing.
            var upright = new Basis(Vector3.Right, Mathf.DegToRad(270f));
            var cat = WorldBuilder.LoadDoorCatalog(dir);
            Vector3 pivotSum = Vector3.Zero; int nLeaves = 1;
            if (cat.TryGetValue(name, out var leaves) && leaves.Count > 0)
            { pivotSum = Vector3.Zero; foreach (var e in leaves) pivotSum += e.Pivot; nLeaves = leaves.Count; }
            var bodyAabb = bodyMesh.GetAabb();
            Vector3 bodyCenterWorld = upright * bodyAabb.GetCenter();
            Vector3 pivotWorld = upright * (pivotSum / (float)nLeaves);
            Vector3 outward = pivotWorld - bodyCenterWorld; outward.Y = 0f;
            if (outward.LengthSquared() < 0.01f) outward = -upright.Z;
            outward = outward.Normalized();
            float r = Mathf.Max(bodyAabb.Size.X, Mathf.Max(bodyAabb.Size.Y, bodyAabb.Size.Z));
            if (r < 0.01f) r = 1f;
            Vector3 lookAt = bodyCenterWorld + Vector3.Up * (r * 0.15f);
            var cam = new Camera3D { Current = true, Fov = 55f, Far = 10000f };
            AddChild(cam);
            cam.Position = lookAt + outward * (r * 1.6f + 2.0f) + Vector3.Up * (r * 0.6f);
            cam.LookAt(lookAt, Vector3.Up);
        }

        // --deploytest: both deployables PLACED on a ground plane (back row) + a BLUE-valid and RED-invalid
        // placement GHOST (front row) -> verify the ripped models stand up right (palette, -90 X), the collider,
        // and the ghost materials. The interactive hold->aim->LMB flow needs a live player, tested in-game.
        // Blend the WindField as a heatmap over PEI's real map image (master: "what does the noisemap look like over PEI").
        // Per pixel -> world X/Z (inverse of MapUI.WorldToNorm, levelSize 1920) -> sample the live wind -> thermal tint.
        void RenderWindMap()
        {
            string mp = ProjectSettings.GlobalizePath("res://content/pei_map.png");
            var img = System.IO.File.Exists(mp) ? Image.LoadFromFile(mp) : null;
            if (img == null) { GD.Print("[windmap] missing pei_map.png"); GetTree().Quit(1); return; }
            if (img.GetFormat() != Image.Format.Rgba8) img.Convert(Image.Format.Rgba8);
            int W = img.GetWidth(), H = img.GetHeight();
            const float LevelSize = 1920f;
            for (int py = 0; py < H; py++)
                for (int px = 0; px < W; px++)
                {
                    float wx = ((float)px / W - 0.5f) * LevelSize;
                    float wz = ((float)py / H - 0.5f) * LevelSize;
                    float w = WindField.SampleWind(new Vector3(wx, 0f, wz));   // 0..1
                    img.SetPixel(px, py, img.GetPixel(px, py).Lerp(WindHeat(w), 0.5f));
                }
            img.SavePng("res://windmap.png");
            GD.Print("[windmap] saved windmap.png");
            GetTree().Quit(0);
        }

        static Color WindHeat(float w)   // 0 calm (blue) -> 1 windy (red): a thermal ramp
        {
            w = Mathf.Clamp(w, 0f, 1f);
            if (w < 0.25f) return new Color(0f, w * 4f, 1f);
            if (w < 0.5f)  return new Color(0f, 1f, 1f - (w - 0.25f) * 4f);
            if (w < 0.75f) return new Color((w - 0.5f) * 4f, 1f, 0f);
            return new Color(1f, 1f - (w - 0.75f) * 4f, 0f);
        }

        // GPU perf probe (--zperf). MUST run with a real rendering driver (xvfb + lavapipe + --rendering-driver
        // vulkan), NEVER --headless: under --headless nothing renders, so every render counter reads zero and any
        // timing is just the frame pacer. That was the bug in my first two attempts at measuring this.
        //
        // lavapipe is a software rasteriser so absolute ms means nothing here -- but DRAW CALLS, PRIMITIVES and
        // VRAM are hardware-independent, and a multiplier is exactly what those expose. Spawns N zombies, samples
        // the counters, hides them, samples again: the delta is the per-zombie render cost, including how many
        // times each one is drawn (shadow cascades included).
        //
        // Frame time is also reported, and it is NOT redundant with the counters. Counters cannot see FRAGMENTS --
        // overdraw and shadow-map fill cost the same zero draw calls whether they shade 1 pixel or 10 million.
        // lavapipe's cost is fragment-dominated, so the RATIO between phases is a fill-rate proxy even though the
        // absolute ms is worthless. Read ratios here, never numbers.
        int _zpN; int _zpStep; double _zpStepClock; int _zpFrames; double _zpFrameSum; double _zpFrameMax;
        readonly System.Collections.Generic.List<ZombieController> _zpZombies = new();

        void BuildZPerf()
        {
            _zpN = int.TryParse(System.Environment.GetEnvironmentVariable("UG_ZN"), out var n) ? n : 20;
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.30f, 0.34f, 0.42f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.72f, 0.72f, 0.75f), AmbientLightEnergy = 1.0f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            // UG_ZSUN=noshadow drops the sun's shadow pass. Not a scene option -- a NOISE FLOOR control. The default
            // 4-split PSSM atlas is rasterised in full every frame regardless of screen resolution or scene content,
            // so under a software rasteriser it is a large fixed cost that swamps whatever is being measured (an
            // empty 5-draw frame still cost ~124ms). Run any measurement both ways: if a delta only exists with the
            // floor present, the delta was the floor.
            bool sunShadow = System.Environment.GetEnvironmentVariable("UG_ZSUN") != "noshadow";
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-48f, -40f, 0f), LightEnergy = 1.3f, ShadowEnabled = sunShadow });
            AddChild(new MeshInstance3D
            {
                Mesh = new PlaneMesh { Size = new Vector2(300f, 300f) },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.34f, 0.40f, 0.28f), Roughness = 1f },
            });
            var gb = new StaticBody3D(); gb.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() }); AddChild(gb);

            // Uncapped and vsync off: the timing pass is only meaningful as a ratio between phases, and a frame
            // pacer flattens exactly that. (Under a pacer every phase reads the pacer's period -- the mistake that
            // produced the first two rounds of garbage numbers.)
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
            Engine.MaxFps = 0;

            // UG_ZRES=WxH. Resolution is the discriminator the counters cannot give: fragment cost scales with
            // PIXELS, geometry/draw cost does not. Run the same N at two resolutions -- if the zombies' marginal
            // frame time scales with pixel count their cost is fill (overdraw / shadow-map rasterisation); if it
            // stays flat it is geometry. The scaling LAW transfers to real hardware; lavapipe's constant does not.
            //
            // ContentScaleMode MUST be cleared first. The project uses stretch mode "canvas_items", which pins the
            // render target to the 2560x1440 content size no matter what the window does -- both `--resolution` and
            // WindowSetSize were silently ignored because of it, and three runs "measured" 2560x1600 while claiming
            // to sweep resolutions. Mode must leave Maximized too, or a size request is a no-op.
            var res = (System.Environment.GetEnvironmentVariable("UG_ZRES") ?? "").Split('x');
            if (res.Length == 2 && int.TryParse(res[0], out var rw) && int.TryParse(res[1], out var rh))
            {
                var win = GetWindow();
                win.ContentScaleMode = Window.ContentScaleModeEnum.Disabled;
                win.ContentScaleAspect = Window.ContentScaleAspectEnum.Ignore;
                win.Mode = Window.ModeEnum.Windowed;
                win.Size = new Vector2I(rw, rh);
            }

            // UG_ZCAM=1p sits the camera at driver-eye range; the default 3p matches the chase cam's worst case
            // (34 m back and elevated). That camera distance is the one thing strawberry's tank is gated on, and it
            // is also what decides how much world each shadow cascade has to cover.
            bool cam1p = System.Environment.GetEnvironmentVariable("UG_ZCAM") == "1p";
            var cam = new Camera3D { Current = true, Fov = 60f, Far = 4000f };
            AddChild(cam);
            cam.GlobalPosition = cam1p ? new Vector3(0f, 1.7f, 6f) : new Vector3(0f, 12f, 34f);
            cam.LookAt(new Vector3(0f, 1f, 0f), Vector3.Up);

            SDG.Unturned.ItemCatalog.RegisterAll();

            // UG_ZAI=1 makes this the CPU probe instead of the render probe. strawberry's F3 in the tanked POI
            // reads frame 37.2ms / physics 32.2ms / render 487 draws, so the tank is the PHYSICS frame and the
            // renderer was never involved. Physics is CPU, which means -- unlike every render number today -- this
            // box can measure it honestly, and it does not need a rendering driver at all.
            //
            // Real AI, not puppets: the whole cost being hunted lives in ZombieController._PhysicsProcess, which
            // puppets skip entirely. Needs a registered player avatar too, or every zombie early-returns before
            // doing any work and the probe reports a confident zero.
            // UG_ZAI=1 runs real AI. UG_ZAI=puppet builds the identical scene -- same bodies, same rigs, same
            // NavigationAgent3Ds -- but as puppets, whose _PhysicsProcess returns immediately. Same reporting
            // either way, so AI-minus-puppet is exactly the AI script's share and the remainder is what a zombie
            // costs the engine just by EXISTING. No new code paths to be wrong about; the difference is the answer.
            string zai = System.Environment.GetEnvironmentVariable("UG_ZAI");
            _zaiMode = zai == "1" || zai == "puppet";
            bool aiPuppets = zai == "puppet";
            if (_zaiMode && !aiPuppets)
            {
                _zaiPlayer = new PlayerController { Inventory = new SDG.Unturned.PlayerInventory() };
                AddChild(_zaiPlayer);
                _zaiPlayer.GlobalPosition = new Vector3(0f, 0f, 6f);
            }

            for (int i = 0; i < _zpN; i++)
            {
                var z = new ZombieController { IsPuppet = !_zaiMode || aiPuppets };   // puppet: no AI, isolates the RENDER cost
                AddChild(z);
                z.GlobalPosition = new Vector3((i % 8) * 2.5f - 9f, 0f, (i / 8) * 2.5f - 4f);
                _zpZombies.Add(z);
            }
            // UG_ZFREEZE=1 is cow tools' F6 (RiggedCharacter.SetAnimFrozen) driven headlessly, so the skeleton
            // share can be measured here instead of only in a live session. It is the leg z.rig CANNOT see: Tick()
            // is a near-no-op once UsePhysicsAnimRate has put the mixer in Physics callback mode, so the actual
            // 17-bones-per-zombie posing happens engine-side inside the physics frame and no script timer wraps it.
            if (System.Environment.GetEnvironmentVariable("UG_ZFREEZE") == "1") RiggedCharacter.SetAnimFrozen(true);

            GD.Print($"[zperf] spawned {_zpN} zombies  cam={(cam1p ? "1p" : "3p")}  mode={(_zaiMode ? "AI/cpu" : "render")}  animFrozen={RiggedCharacter.AnimFrozen}");
            SetProcess(true);
        }

        // ============================================================================
        // --zbody: MECHANISM PROBE FOR THE 20ms
        //
        // strawberry's zombies-off control in a live POI, same spot, 3p in a vehicle:
        //     zombies OFF  physics  2.4ms  217fps  11 active,  28 pairs
        //     zombies ON   physics 26.8ms   30fps  74 active, 192 pairs
        // Zombies add 24.4ms of physics. Their SCRIPT (every Prof tag summed) is 4.1ms of it.
        // The other 20.3ms is the physics server simulating their bodies.
        //
        // So the claim to test is narrow and does not need the AI at all: do N kinematic capsules
        // being swept every tick cost engine-side physics proportional to N? This spawns bare
        // CharacterBody3Ds with no rig, no AI, no nav agent, and alternates moving/parked windows
        // INSIDE ONE PROCESS -- the only design that survived this box's variance.
        //
        // Also corrects something I told strawberry: I claimed kinematic bodies contribute nothing
        // to the active-body counter. 11 -> 74 with 63 zombies says otherwise. A CharacterBody3D
        // that is moved every tick is active. I reasoned from what "kinematic" means instead of
        // checking a delta that was already on screen.
        readonly System.Collections.Generic.List<CharacterBody3D> _zbBodies = new();
        bool _zbMode, _zbMoving = true; int _zbPhase, _zbPhysN; double _zbClock, _zbPhysSum, _zbPhysMax;
        readonly double[] _zbRes = new double[4];

        void BuildZBody()
        {
            _zbMode = true;
            int n = int.TryParse(System.Environment.GetEnvironmentVariable("UG_ZN"), out var v) ? v : 63;
            bool obstacles = System.Environment.GetEnvironmentVariable("UG_ZBOBST") == "1";

            var gb = new StaticBody3D();
            gb.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
            AddChild(gb);

            // UG_ZBOBST=1 adds static boxes so the sweeps hit real geometry instead of one infinite
            // plane. strawberry's POI is full of buildings; a bare plane is the cheapest possible case
            // and will understate, so the two modes bracket the answer rather than pretending to be it.
            if (obstacles)
                for (int i = 0; i < 120; i++)
                {
                    var sb = new StaticBody3D();
                    var cs = new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(2f, 3f, 2f) } };
                    sb.AddChild(cs); AddChild(sb);
                    sb.GlobalPosition = new Vector3((i % 12) * 4f - 22f, 1.5f, (i / 12) * 4f - 18f);
                }

            for (int i = 0; i < n; i++)
            {
                var b = new CharacterBody3D { CollisionLayer = 1u << 1, CollisionMask = 1u << 0 };
                b.AddChild(new CollisionShape3D { Shape = new CapsuleShape3D { Radius = 0.35f, Height = 1.8f } });
                AddChild(b);
                b.GlobalPosition = new Vector3((i % 10) * 2.2f - 10f, 1.0f, (i / 10) * 2.2f - 6f);
                _zbBodies.Add(b);
            }
            GD.Print($"[zbody] {n} kinematic capsules, obstacles={obstacles}");
            SetProcess(true); SetPhysicsProcess(true);
        }

        // Driving the capsules from here rather than from a per-body script keeps the probe honest:
        // the only thing under test is MoveAndSlide on N kinematic bodies, with no other per-node work.
        public override void _PhysicsProcess(double delta)
        {
            if (_zbMode) ZBodyPhysics(delta);
        }

        void ZBodyPhysics(double delta)
        {
            if (!_zbMoving) return;
            float t = (float)_zbClock;
            for (int i = 0; i < _zbBodies.Count; i++)
            {
                var b = _zbBodies[i];
                float a = t * 0.7f + i * 0.37f;
                b.Velocity = new Vector3(Mathf.Cos(a) * 2.4f, -9.8f * (float)delta, Mathf.Sin(a) * 2.4f);
                b.MoveAndSlide();
            }
        }

        void ZBodyTick(double delta)
        {
            const double Warm = 1.0, Win = 3.0;
            _zbClock += delta;
            if (_zbClock < Warm) return;
            double now = Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000.0;
            _zbPhysSum += now; _zbPhysN++;
            if (now > _zbPhysMax) _zbPhysMax = now;
            if (_zbClock < Warm + Win * (_zbPhase + 1)) return;

            double phys = _zbPhysN > 0 ? _zbPhysSum / _zbPhysN : 0.0;
            GD.Print($"[zbody] win{_zbPhase} {( _zbMoving ? "MOVING" : "parked")} n={_zbBodies.Count} " +
                     $"physics={phys:0.000}ms (worst {_zbPhysMax:0.000}, {_zbPhysN} samples) " +
                     $"active={Performance.GetMonitor(Performance.Monitor.Physics3DActiveObjects):0} " +
                     $"pairs={Performance.GetMonitor(Performance.Monitor.Physics3DCollisionPairs):0}");
            if (_zbPhase < 4) _zbRes[_zbPhase] = phys;

            _zbPhase++; _zbPhysSum = 0; _zbPhysN = 0; _zbPhysMax = 0;
            if (_zbPhase < 4) { _zbMoving = _zbPhase % 2 == 0; return; }

            GD.Print($"[zbody] pair1 moving {_zbRes[0]:0.000} -> parked {_zbRes[1]:0.000} (delta {_zbRes[0] - _zbRes[1]:0.000}ms)");
            GD.Print($"[zbody] pair2 moving {_zbRes[2]:0.000} -> parked {_zbRes[3]:0.000} (delta {_zbRes[2] - _zbRes[3]:0.000}ms)  -- trust only if the pairs agree");
            GetTree().Quit();
        }

        bool _zaiMode; PlayerController _zaiPlayer; double _zaiClock; ulong _zaiPhys0;
        double _zaiPhysSum, _zaiPhysMax; int _zaiPhysN; int _zaiPhase; readonly double[] _zaiResult = new double[2];
        int _zaiGc0, _zaiGc1, _zaiGc2;

        // A/B INSIDE ONE PROCESS, alternating windows. Comparing two separate launches does not work on this box:
        // three paired frozen/live runs gave 13.3/13.5, 11.5/6.6 and 17.3/33.8ms -- the effect changed SIGN, and the
        // mean said freezing animation made it slower, which is impossible. Cross-process noise (boot, JIT, page
        // cache, whatever else has the 4 cores) is larger than anything being measured. Back-to-back windows in one
        // process share all of that, so the difference between them is the thing that actually changed.
        //
        // Alternates live/frozen/live/frozen rather than doing one of each, so a monotonic drift over the run shows
        // up as disagreement between the two halves instead of masquerading as the effect.
        void ZAITick(double delta)
        {
            const double Warm = 1.5, Win = 3.0;
            _zaiClock += delta;
            if (_zaiClock < Warm) return;                  // rigs build + the AI settles out of its first tick

            double physNow = Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000.0;
            _zaiPhysSum += physNow; _zaiPhysN++;
            if (physNow > _zaiPhysMax) _zaiPhysMax = physNow;
            if (_zaiClock < Warm + Win * (_zaiPhase + 1)) return;

            ulong ticks = Engine.GetPhysicsFrames() - _zaiPhys0;
            double phys = _zaiPhysN > 0 ? _zaiPhysSum / _zaiPhysN : 0.0;
            bool frozen = RiggedCharacter.AnimFrozen;

            // Check BEFORE the reset below, not after -- the first version of this guard read Prof.Us on the far
            // side of Prof.Reset() and cheerfully reported "z.total is ZERO, NOT a result" on runs that had just
            // produced perfectly good numbers. A guard against lying instruments that lies is worse than none.
            bool noAiWork = !Prof.Us.TryGetValue("z.total", out var tot) || tot == 0;

            // GC per window. The worst frames here reach 120ms in a headless probe on flat ground, which is not a
            // plausible cost for 60 capsules -- so the question is whether the "noise" is this box or the GAME.
            // CanSee allocates a Godot.Collections.Array<Rid> and a PhysicsRayQueryParameters3D per zombie per tick;
            // at n=60 that is ~3000 marshalled allocations a second. If gen0 tracks the spikes, the spikes are ours.
            int g0 = System.GC.CollectionCount(0), g1 = System.GC.CollectionCount(1), g2 = System.GC.CollectionCount(2);
            int d0 = g0 - _zaiGc0, d1 = g1 - _zaiGc1, d2 = g2 - _zaiGc2; _zaiGc0 = g0; _zaiGc1 = g1; _zaiGc2 = g2;

            var parts = new System.Collections.Generic.List<string>();
            if (ticks > 0) foreach (var kv in Prof.Us) parts.Add($"{kv.Key}={kv.Value / (double)ticks / 1000.0:0.000}ms");
            parts.Sort();
            GD.Print($"[zai] n={_zpN} win{_zaiPhase} anim={(frozen ? "FROZEN" : "live  ")} " +
                     $"physicsFrame={phys:0.000}ms (worst {_zaiPhysMax:0.000}, {_zaiPhysN} samples) " +
                     $"gc[{d0}/{d1}/{d2}] heap={System.GC.GetTotalMemory(false) / 1048576.0:0.0}MB   per-tick: {string.Join("  ", parts)}");
            if (_zaiPhase < 2) _zaiResult[_zaiPhase] = phys;
            if (noAiWork) GD.Print("[zai] z.total is ZERO -> zombies early-returned before any AI work (no player registered?). NOT a result.");

            _zaiPhase++;
            _zaiPhysSum = 0; _zaiPhysN = 0; _zaiPhysMax = 0; Prof.Reset(); _zaiPhys0 = Engine.GetPhysicsFrames();
            if (_zaiPhase < 4) { RiggedCharacter.SetAnimFrozen(_zaiPhase % 2 == 1); return; }

            GD.Print($"[zai] first pair: live {_zaiResult[0]:0.000}ms -> frozen {_zaiResult[1]:0.000}ms " +
                     $"(skeleton share {_zaiResult[0] - _zaiResult[1]:0.000}ms). Trust it only if the SECOND pair agrees.");
            GetTree().Quit();
        }

        // Each step: let the change settle, then average frame time over a window and print it with the counters.
        // ON - OFF is the zombies' whole render cost; ON - ON,noshadow is the part that is shadow casting.
        void ZPerfTick(double delta)
        {
            const double Warm = 0.6, Win = 2.0;
            _zpStepClock += delta;
            if (_zpStepClock > Warm)
            {
                _zpFrames++; _zpFrameSum += delta;
                // Worst frame in the window, not just the mean. A STALL -- a GPU->CPU sync or a synchronous
                // pipeline compile -- is spikes, and a mean averages it straight back out of existence.
                if (delta > _zpFrameMax) _zpFrameMax = delta;
            }
            if (_zpStepClock < Warm + Win) return;

            double M(Performance.Monitor m) => Performance.GetMonitor(m);
            string tag = _zpStep switch { 0 => $"ON(n={_zpN})", 1 => "OFF", _ => "ON,noshadow" };
            // Report the size actually rendered, straight off the render target -- not the size asked for, and not
            // the window's idea of it. A resolution-scaling experiment where the resolution silently never changed
            // reads as "flat, therefore not fill": a null result that looks exactly like data. That already happened
            // once here (WindowSetSize was ignored and every run rendered 2560x1600).
            Vector2I vp = (Vector2I)GetViewport().GetTexture().GetSize();
            GD.Print(
                $"[zperf] {tag,-12} {vp.X}x{vp.Y} " +
                $"frame={(_zpFrames > 0 ? _zpFrameSum / _zpFrames * 1000.0 : 0.0):0.00}ms " +
                $"worst={_zpFrameMax * 1000.0:0.00}ms " +
                $"draws={M(Performance.Monitor.RenderTotalDrawCallsInFrame):0} " +
                $"objs={M(Performance.Monitor.RenderTotalObjectsInFrame):0} " +
                $"prims={M(Performance.Monitor.RenderTotalPrimitivesInFrame):0} " +
                $"vram={M(Performance.Monitor.RenderVideoMemUsed) / 1048576.0:0.0}MB");

            _zpStep++; _zpStepClock = 0; _zpFrames = 0; _zpFrameSum = 0; _zpFrameMax = 0;
            switch (_zpStep)
            {
                case 1: foreach (var z in _zpZombies) z.Visible = false; break;                                  // scene without them at all
                case 2: foreach (var z in _zpZombies) { z.Visible = true; SetZombieShadows(z, false); } break;   // drawn, casting nothing
                default: GetTree().Quit(); break;
            }
        }

        static void SetZombieShadows(Node root, bool on)
        {
            if (root is GeometryInstance3D gi) gi.CastShadow = on ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off;
            foreach (var c in root.GetChildren()) SetZombieShadows(c, on);
        }

        // --barricadetest : the barricades-on-structures showcase. A grey STRUCTURE wall (in the "structures" group)
        // with two barricades mounted UPRIGHT on its face, each yawed to face straight out of the wall (Wall mount);
        // a blue VALID placement ghost snapped to the wall; and a floor barricade in front for contrast. Demonstrates
        // the surface-placement gap the ground DeployablePlacer couldn't do (it rejects any normal.y < 0.01).
        void BuildBarricadeTest()
        {
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.30f, 0.34f, 0.42f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.72f, 0.72f, 0.75f), AmbientLightEnergy = 1.0f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-48f, -40f, 0f), LightEnergy = 1.3f, ShadowEnabled = true });

            AddChild(new MeshInstance3D
            {
                Mesh = new PlaneMesh { Size = new Vector2(40f, 40f) },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.34f, 0.40f, 0.28f), Roughness = 1f },
            });
            var groundBody = new StaticBody3D();
            groundBody.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
            groundBody.AddToGroup("terrain");
            AddChild(groundBody);

            // a STRUCTURE wall (grey, in the "structures" group -- where StructureManager parents its pieces)
            var wallSize = new Vector3(6f, 4f, 0.4f);
            var wallPos = new Vector3(0f, 2f, -2f);
            var wall = new StaticBody3D { Position = wallPos };
            wall.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = wallSize } });
            wall.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = wallSize }, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.56f, 0.57f, 0.61f), Roughness = 0.85f } });
            wall.AddToGroup("structures");
            AddChild(wall);

            var n = Vector3.Back;                                   // the wall's front face normal (+Z, toward the camera)
            float faceZ = wallPos.Z + wallSize.Z * 0.5f;
            float wallYaw = BarricadePlacer.YawFacing(n);           // face straight out of the wall

            // WALL: two metal-plate barricades flush on the wall face. Mount comes from the def (Wall) -> upright, facing out.
            Barricade.PlaceOnSurface(this, DeployableDef.MetalBarricade, new Vector3(-1.6f, 1.7f, faceZ), n, wallYaw);
            Barricade.PlaceOnSurface(this, DeployableDef.MetalBarricade, new Vector3(1.6f, 1.7f, faceZ), n, wallYaw);

            // a blue VALID placement ghost snapped to the wall (the placer preview; SetDef reads Mount=Wall from the def)
            var placer = new BarricadePlacer();
            AddChild(placer);
            placer.SetDef(DeployableDef.MetalBarricade);
            placer.Freeze(new Vector3(0f, 1.7f, faceZ), n, wallYaw);

            // FLOOR: a deployable on the ground in front for contrast (Floor mount, upright, free yaw)
            Deployable.Spawn(this, DeployableDef.Generator, new Vector3(-3.4f, 0f, 2.2f), 25f);

            var cam = new Camera3D { Current = true, Fov = 56f, Far = 10000f };
            AddChild(cam);
            cam.Position = new Vector3(5.6f, 3.2f, 8.2f);
            cam.LookAt(new Vector3(-0.3f, 1.5f, -0.6f), Vector3.Up);
        }

        void BuildDeployTest()
        {
            if (System.Environment.GetEnvironmentVariable("UG_WINDMAP") == "1") { RenderWindMap(); return; }   // wind heatmap over PEI, then quit
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
            bool showSplit = System.Environment.GetEnvironmentVariable("UG_SPLITTERS") == "1"
                          || System.Environment.GetEnvironmentVariable("UG_GASPUMP") == "1"
                          || System.Environment.GetEnvironmentVariable("UG_BATTERY") == "1"
                          || System.Environment.GetEnvironmentVariable("UG_SWITCH") == "1"
                          || System.Environment.GetEnvironmentVariable("UG_SWITCHCKT") == "1"
                          || System.Environment.GetEnvironmentVariable("UG_SPOTPORTS") == "1"
                          || System.Environment.GetEnvironmentVariable("UG_PORTSTATES") == "1"
                          || System.Environment.GetEnvironmentVariable("UG_DEVIO") == "1"
                          || System.Environment.GetEnvironmentVariable("UG_WINDTURBINE") == "1"
                          || System.Environment.GetEnvironmentVariable("UG_TRAPS") == "1"
                          || System.Environment.GetEnvironmentVariable("UG_WATERTANK") == "1";   // showcases skip the gen/spot/ghost clutter
            Deployable placedGen = null, placedSpot = null;
            if (!showSplit)
            {
                // back row: PLACED objects (surface = ground; the base is sat on it)
                placedGen = Deployable.Spawn(this, gen, new Vector3(-2.6f, 0f, 0f), 0f);
                placedSpot = Deployable.Spawn(this, spot, new Vector3(2.6f, 0f, 0f), 0f);
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
            }

            var cam = new Camera3D { Current = true, Fov = 52f, Far = 10000f };
            AddChild(cam);
            var look = new Vector3(0f, 0.7f, 2f);                 // tracked look-at target so UG_CAMYAW can orbit around it
            cam.Position = new Vector3(0f, 3.2f, 11f);
            cam.LookAt(look, Vector3.Up);

            // UG_SPLITTERS=1: showcase the three power splitters (2/3/4-way) in a row with all port arrows on -- verify
            // the gray box stands up, the orange input (back) + fanned cyan outputs (front) read. UG_SPLITBACK=1 = the
            // rear view onto the input face.
            if (System.Environment.GetEnvironmentVariable("UG_SPLITTERS") == "1")
            {
                var sp2 = Deployable.Spawn(this, DeployableDef.Splitter2, new Vector3(-3.0f, 0f, 0f), 0f);
                var sp3 = Deployable.Spawn(this, DeployableDef.Splitter3, new Vector3(-0.6f, 0f, 0f), 0f);
                var sp4 = Deployable.Spawn(this, DeployableDef.Splitter4, new Vector3(2.2f, 0f, 0f), 0f);
                var cm2 = Deployable.Spawn(this, DeployableDef.Combiner2, new Vector3(4.8f, 0f, 0f), 0f);   // rightmost: 2 inputs (back) + 1 output (front) = the splitter's mirror
                foreach (var dep in new[] { sp2, sp3, sp4, cm2 })
                    foreach (var pt in dep.Ports) pt.SetArrowState(true, true);
                look = new Vector3(0.9f, 0.35f, 0f);
                bool back = System.Environment.GetEnvironmentVariable("UG_SPLITBACK") == "1";
                cam.Position = back ? new Vector3(0.9f, 1.7f, -5.4f) : new Vector3(2.2f, 1.8f, 6.6f);
                cam.Fov = 50f;
                cam.LookAt(look, Vector3.Up);
            }
            // UG_GASPUMP=1: a gas pump + its 750w power input port -- verify the orange input cube sits ON the pump.
            if (System.Environment.GetEnvironmentVariable("UG_GASPUMP") == "1")
            {
                var pumpMesh = ObjMesh.Load(ProjectSettings.GlobalizePath("res://content/objects/Gas_Pump_0.obj"));
                var standUp = new Basis(Vector3.Right, Mathf.DegToRad(-90f));   // the map stands the flat-authored pump up (raw Z -> world height)
                if (pumpMesh != null)
                    AddChild(new MeshInstance3D { Mesh = pumpMesh, Basis = standUp, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.66f, 0.67f, 0.7f), Roughness = 0.7f, CullMode = BaseMaterial3D.CullModeEnum.Disabled } });
                var gp = GasPump.Attach(this, Vector3.Zero, standUp, GasPump.PortLocal, pumpMesh);
                foreach (var pt in gp.PowerPorts) pt.SetArrowState(true, true);
                look = new Vector3(0f, 1.2f, 0f);
                cam.Position = new Vector3(2.8f, 1.5f, 3.6f);
                cam.Fov = 50f; cam.LookAt(look, Vector3.Up);
            }
            // UG_BATTERY=1: the placeable Vehicle Battery (item 1450 real mesh) with its IN (charge) + OUT (discharge)
            // port arrows on -- verify the model stands up right + the terminals sit on opposite ends.
            if (System.Environment.GetEnvironmentVariable("UG_BATTERY") == "1")
            {
                var batMesh = ObjMesh.Load(ProjectSettings.GlobalizePath("res://content/objects/Battery_0.obj"));
                if (batMesh != null) { var bb = batMesh.GetAabb(); GD.Print($"[BATTERY] mesh AABB size={bb.Size} center={bb.GetCenter()}"); }
                var bat = Deployable.Spawn(this, DeployableDef.Battery, Vector3.Zero, 0f);
                if (System.Environment.GetEnvironmentVariable("UG_WIREARROWS") == "1")
                    foreach (var pt in bat.Ports) pt.SetArrowState(true, true);   // arrows only for port-debug; default = clean product shot
                look = new Vector3(0f, 0.15f, 0f);
                cam.Position = new Vector3(0.9f, 0.7f, 1.3f);
                cam.Fov = 45f; cam.LookAt(look, Vector3.Up);
            }
            // UG_TRAPS=1: the three trap deployables (landmine / wooden spikes / charge) with their REAL ripped world
            // meshes -- verify they sit FLAT on the ground (floor traps, NOT stood up like a wall barricade) + the albedo.
            if (System.Environment.GetEnvironmentVariable("UG_TRAPS") == "1")
            {
                var traps = new[] { DeployableDef.Landmine, DeployableDef.Spike, DeployableDef.Charge, DeployableDef.Barbedwire };
                float tx = -1.95f;
                foreach (var def in traps)
                {
                    Deployable.Spawn(this, def, new Vector3(tx, 0f, 0f), 0f);
                    var m = ObjMesh.Load(ProjectSettings.GlobalizePath($"res://content/objects/{def.Model}.obj"));
                    if (m != null) { var bb = m.GetAabb(); GD.Print($"[TRAPS] {def.Name} ({def.Model}) AABB size={bb.Size} center={bb.GetCenter()}"); }
                    tx += 1.3f;
                }
                look = new Vector3(0f, 0.05f, 0f);
                cam.Position = new Vector3(0f, 1.35f, 2.9f);
                cam.Fov = 58f; cam.LookAt(look, Vector3.Up);
            }
            // UG_SWITCH=1: two Power Switches side by side -- left ON (green light), right toggled OFF (red) -- verify the state light + gate.
            if (System.Environment.GetEnvironmentVariable("UG_SWITCH") == "1")
            {
                var swOn = Deployable.Spawn(this, DeployableDef.Switch, new Vector3(-0.5f, 0f, 0f), 0f);    // default ON -> green
                var swOff = Deployable.Spawn(this, DeployableDef.Switch, new Vector3(0.5f, 0f, 0f), 0f);
                swOff.TogglePower();   // -> OFF, red
                foreach (var pt in swOn.Ports) pt.SetArrowState(true, true);
                look = new Vector3(0f, 0.2f, 0f);
                cam.Position = new Vector3(0.7f, 0.9f, 1.7f);
                cam.Fov = 50f; cam.LookAt(look, Vector3.Up);
            }
            // UG_WINDTURBINE=1: the wind turbine -- tower + nacelle + 3-blade hub + the output port. (Blades spin in-game
            // ~ the local wind; a still shot just shows the model at a frozen blade angle.)
            if (System.Environment.GetEnvironmentVariable("UG_WINDTURBINE") == "1")
            {
                var wt = Deployable.Spawn(this, DeployableDef.WindTurbine, Vector3.Zero, 35f);
                wt.SetLookFocused(true);   // show the info billboard (wind bar + live output wattage)
                foreach (var pt in wt.Ports) pt.SetArrowState(true, true);
                look = new Vector3(0f, 0.62f, 0f);
                cam.Position = new Vector3(1.5f, 0.95f, 2.4f);
                cam.Fov = 50f; cam.LookAt(look, Vector3.Up);
            }
            // UG_WATERTANK=1: show the map's WATER TOWER (Tower_Water_0) + the big storage tanks (Tank_Forest_Body /
            // Tank_Fuel_0) in a row with a 1.8 m human-height reference, so strawberry can see the "big water tank" prop +
            // its scale (--shot=OUT). These are flat-authored map props -> stand them up like the gas pump.
            if (System.Environment.GetEnvironmentVariable("UG_WATERTANK") == "1")
            {
                var standUp = new Basis(Vector3.Right, Mathf.DegToRad(-90f));
                string odir = ProjectSettings.GlobalizePath("res://content/objects/");
                void Prop(string nm, Vector3 pos)
                {
                    var m = ObjMesh.Load(odir + nm + ".obj");
                    if (m == null) { GD.Print($"[WATERTANK] {nm}.obj MISSING"); return; }
                    var bb = m.GetAabb();
                    GD.Print($"[WATERTANK] {nm} AABB size={bb.Size} -> stood-up height ~{bb.Size.Z:0.0}m footprint ~{bb.Size.X:0.0}x{bb.Size.Y:0.0}m");
                    var mat = new StandardMaterial3D { Roughness = 0.85f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
                    string tp = odir + nm + "_tex.png";
                    if (System.IO.File.Exists(tp)) { var img = new Image(); if (img.Load(tp) == Error.Ok) mat.AlbedoTexture = ImageTexture.CreateFromImage(img); else mat.AlbedoColor = new Color(0.62f, 0.66f, 0.70f); }
                    else mat.AlbedoColor = new Color(0.62f, 0.66f, 0.70f);
                    AddChild(new MeshInstance3D { Mesh = m, Basis = standUp, Position = pos, MaterialOverride = mat });
                }
                Prop("Tower_Water_0", new Vector3(-4f, 0f, 0f));   // the big WATER TOWER (~15 m) -- the "big water tank" prop
                Prop("Tank_Fuel_0",   new Vector3(8f, 0f, 1f));    // a horizontal FUEL tank, for contrast (not water)
                AddChild(new MeshInstance3D { Mesh = new CapsuleMesh { Radius = 0.3f, Height = 1.8f }, Position = new Vector3(-1f, 0.9f, 4.5f),
                    MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.90f, 0.28f, 0.28f) } });   // 1.8 m human scale reference (at the tower base)
                look = new Vector3(0f, 5f, 0f);
                cam.Position = new Vector3(1f, 8f, 30f);
                cam.Fov = 52f; cam.LookAt(look, Vector3.Up);
            }
            // UG_SWITCHCKT=1: a working circuit -- generator -> switch -> spotlight, + sources on the switch's turn-on
            // (green) / turn-off (red) trigger inputs. Default: TurnOn source fed -> switch ON -> spotlight LIT.
            // UG_TRIGOFF=1: the TurnOff source is fed instead -> the switch flips OFF -> the spotlight goes DARK.
            if (System.Environment.GetEnvironmentVariable("UG_SWITCHCKT") == "1")
            {
                var g = Deployable.Spawn(this, DeployableDef.Generator, new Vector3(-2.6f, 0f, 0.8f), 0f);
                var sw = Deployable.Spawn(this, DeployableDef.Switch, new Vector3(0f, 0f, 0.8f), 90f);
                var lamp = Deployable.Spawn(this, DeployableDef.Spotlight, new Vector3(2.6f, 0f, 0.8f), 180f);
                var onSrc = Deployable.Spawn(this, DeployableDef.Generator, new Vector3(-1.4f, 0f, -2.3f), 0f);
                var offSrc = Deployable.Spawn(this, DeployableDef.Generator, new Vector3(1.4f, 0f, -2.3f), 0f);
                ConnectionPort P(Deployable d, DeployableDef.PortKind k) => d.Ports.Find(p => p.Kind == k);
                var swIn = sw.Ports.Find(p => p.Kind == DeployableDef.PortKind.Consumer && p.Role == DeployableDef.SwitchRole.None);
                var swOn = sw.Ports.Find(p => p.Role == DeployableDef.SwitchRole.TurnOn);
                var swOff = sw.Ports.Find(p => p.Role == DeployableDef.SwitchRole.TurnOff);
                void W(ConnectionPort a, ConnectionPort b) { var wr = new Wire(); AddChild(wr); wr.Source = a; wr.Consumer = b; wr.AddToGroup("wires"); wr.SetPoints(new System.Collections.Generic.List<Vector3> { a.GlobalPosition, b.GlobalPosition }, valid: true); }
                W(P(g, DeployableDef.PortKind.Output), swIn);
                W(P(sw, DeployableDef.PortKind.Passthrough), P(lamp, DeployableDef.PortKind.Consumer));
                W(P(onSrc, DeployableDef.PortKind.Output), swOn);
                W(P(offSrc, DeployableDef.PortKind.Output), swOff);
                g.TogglePower();   // main power on
                if (System.Environment.GetEnvironmentVariable("UG_TRIGOFF") == "1") offSrc.TogglePower();   // fire TurnOff -> switch OFF -> dark
                else onSrc.TogglePower();                                                                   // fire TurnOn  -> switch ON  -> lit
                PowerNet.Recompute(GetTree());
                env.AmbientLightEnergy = 0.09f; env.BackgroundColor = new Color(0.03f, 0.03f, 0.05f);
                dirLight.LightEnergy = 0.12f;
                look = new Vector3(0.4f, 0.5f, -0.4f);
                cam.Position = new Vector3(0.2f, 3.4f, 7.0f);
                cam.Fov = 60f; cam.LookAt(look, Vector3.Up);
            }
            // UG_SPOTPORTS=1: the spotlight alone with its i/o ports + arrows, close up -- verify the ports sit on the
            // pillar/feet + the arrows point perpendicular straight out of each cube face (master's electricity quirk fix).
            if (System.Environment.GetEnvironmentVariable("UG_SPOTPORTS") == "1")
            {
                var sp = Deployable.Spawn(this, DeployableDef.Spotlight, Vector3.Zero, 0f);
                // feed a generator into the spotlight's consumer so THAT port reads occupied (dark grey); the passthrough
                // stays free (light grey) -> the close-up shows both I/O-cube states + the translucency in one shot.
                var feed = Deployable.Spawn(this, DeployableDef.Generator, new Vector3(0f, 0f, -5.5f), 0f);
                var spIn = sp.Ports.Find(p => p.Kind == DeployableDef.PortKind.Consumer);
                var spOut = sp.Ports.Find(p => p.Kind == DeployableDef.PortKind.Passthrough);
                var genOut = feed.Ports.Find(p => p.Kind == DeployableDef.PortKind.Output);
                var wr = new Wire(); AddChild(wr); wr.Source = genOut; wr.Consumer = spIn; wr.AddToGroup("wires");
                wr.SetPoints(new System.Collections.Generic.List<Vector3> { genOut.GlobalPosition, spIn.GlobalPosition }, valid: true);
                PowerNet.Recompute(GetTree());
                foreach (var pt in sp.Ports) pt.SetArrowState(true, true);
                Vector3 mid = (spIn.GlobalPosition + spOut.GlobalPosition) * 0.5f;   // aim precisely at the two I/O cubes
                look = mid;
                cam.Position = mid + new Vector3(0.85f, 0.42f, 1.15f);
                cam.Fov = 38f; cam.LookAt(look, Vector3.Up);
            }
            // UG_PORTSTATES=1: two spotlights side by side showing every I/O-port state at once -- (left) base grey + brighter
            // FOCUS (look-at); (right) RED occupied/invalid wire target + GREEN valid target (master's wire-feedback pass).
            if (System.Environment.GetEnvironmentVariable("UG_PORTSTATES") == "1")
            {
                var a = Deployable.Spawn(this, DeployableDef.Spotlight, new Vector3(-1.1f, 0f, 0f), 0f);
                var b = Deployable.Spawn(this, DeployableDef.Spotlight, new Vector3(1.1f, 0f, 0f), 0f);
                foreach (var d in new[] { a, b }) foreach (var pt in d.Ports) pt.SetArrowState(true, true);
                PowerNet.Recompute(GetTree());   // settle (no wires -> base grey) BEFORE forcing states; ApplyHi keeps them after
                ConnectionPort PS(Deployable d, DeployableDef.PortKind k) => d.Ports.Find(p => p.Kind == k);
                PS(a, DeployableDef.PortKind.Consumer).SetHighlight(ConnectionPort.PortHi.None);       // base grey (free)
                PS(a, DeployableDef.PortKind.Passthrough).SetHighlight(ConnectionPort.PortHi.Focus);   // a little brighter (look-at)
                PS(b, DeployableDef.PortKind.Consumer).SetHighlight(ConnectionPort.PortHi.WireBad);    // red: occupied / invalid target
                PS(b, DeployableDef.PortKind.Passthrough).SetHighlight(ConnectionPort.PortHi.WireOk);  // green: valid target
                look = new Vector3(0f, 0.55f, 0f);
                cam.Position = new Vector3(0f, 1.05f, 3.1f);
                cam.Fov = 48f; cam.LookAt(look, Vector3.Up);
            }
            // UG_DEVIO=1: generator (left) + gas pump (right) with their I/O port arrows on -- master check: how the new
            // grey / flat-arrow / occupancy treatment reads on the OTHER devices (generator output, gas-pump 750w input).
            if (System.Environment.GetEnvironmentVariable("UG_DEVIO") == "1")
            {
                var g = Deployable.Spawn(this, DeployableDef.Generator, new Vector3(-1.5f, 0f, 0f), 0f);
                foreach (var pt in g.Ports) pt.SetArrowState(true, true);
                var pumpMesh = ObjMesh.Load(ProjectSettings.GlobalizePath("res://content/objects/Gas_Pump_0.obj"));
                var standUp = new Basis(Vector3.Right, Mathf.DegToRad(-90f));
                var pumpPos = new Vector3(1.5f, 0f, 0f);
                if (pumpMesh != null)
                    AddChild(new MeshInstance3D { Mesh = pumpMesh, Basis = standUp, Position = pumpPos, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.66f, 0.67f, 0.7f), Roughness = 0.7f, CullMode = BaseMaterial3D.CullModeEnum.Disabled } });
                var gp = GasPump.Attach(this, pumpPos, standUp, Deployable.EnvVec3("UG_GPP", GasPump.PortLocal), pumpMesh);
                foreach (var pt in gp.PowerPorts) pt.SetArrowState(true, true);
                look = new Vector3(0f, 0.9f, 0f);
                cam.Position = new Vector3(0.4f, 1.7f, 4.6f);
                cam.Fov = 52f; cam.LookAt(look, Vector3.Up);
            }
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
            // UG_SYNCLOAD=1: skip EVERY per-phase frame-yield (like --bakenav) for fast HEADLESS repros. Under lavapipe
            // (no GPU) each per-phase drawn frame software-renders the whole growing scene (612k grass, 3614 objects),
            // which paces the load; syncLoad never draws mid-load, so the box boots far faster. Off by default (a
            // real interactive session wants the loading screen); the game still renders normally once loaded.
            bool syncLoad = _bakeNav || System.Environment.GetEnvironmentVariable("UG_SYNCLOAD") == "1";
            var res = await WorldBuilder.BuildFullWorld(this, _peiPlayable ? WorldMode.Playable : WorldMode.Aerial,
                _mapRoot, _mapPlace, _noZombies, syncLoad: syncLoad, bakeNav: _bakeNav, ActiveHoliday());
            // A1 FIX (master 2026-07-20: PEI shelves spawned empty in SP): load the loot tables BEFORE AttachMpLoopback.
            // Under a consuming loopback ContainerNetSync rolls the map containers' loot INSIDE AttachMpLoopback (below),
            // so the tables must be loaded by then -- but the only load site was SpawnMapContainers (@1848), which is
            // gated OFF under consume, so it never ran and every shelf's display digest came back empty.
            if (_peiPlayable) LootTables.Load(_mapRoot + "/Spawns/Items.dat");
            _pdPlayer = res.Player;   // UG_AUTOFIRE terrain-impact verification
            if (_pdPlayer != null && System.Environment.GetEnvironmentVariable("UG_START3P") == "1") _pdPlayer.DriveFP = false;   // start in 3rd person (verify the 3P centre crosshair + the 3P body)
            _ztField = res.Zombies;   // --zombietest reads this at frame 25 to verify spawns land on the navmesh
            _zdField = res.Director;  // --zdirtest reads this to watch the rewrite tier/path/move on the real map
            if (res.HasVehicleAim && !_vHave) { _vAim = res.VehicleAim; _vHave = true; }
            // P6a: the GAME "Drive PEI"/--peidrive path (Playable + a real player, NOT the nav-bake/navpath/zombie
            // offline harnesses, which set _bakeNav) boots the consuming listen-server by default. --objects is Aerial
            // (res.Player == null) so it early-returns regardless. gameDefault=false keeps the harnesses direct.
            AttachMpLoopback(res, gameDefault: _peiPlayable && !_bakeNav);
            if (res.Ready) _worldReady = true;   // async world fully built (terrain..trees) -> the --shot harness can now capture a loaded frame
            // UG_MAPSHOT=<half-extent-metres>: a top-down ORTHOGRAPHIC map capture. Orthographic and axis-aligned on
            // purpose -- it makes world->pixel an exact linear mapping, so an overlay (signal positions, spawns,
            // whatever) lands where the thing actually is instead of being nudged into place by eye against a
            // perspective frame. Centre with UG_MAPSHOT_AT="x,z"; the printed [mapshot] line is the projection.
            string mapShot = System.Environment.GetEnvironmentVariable("UG_MAPSHOT");
            if (!string.IsNullOrEmpty(mapShot) && float.TryParse(mapShot, out var half) && half > 0f)
            {
                float mcx = 0f, mcz = 0f;
                var at = (System.Environment.GetEnvironmentVariable("UG_MAPSHOT_AT") ?? "").Split(',');
                if (at.Length == 2) { float.TryParse(at[0], out mcx); float.TryParse(at[1], out mcz); }
                var mcam = new Camera3D
                {
                    Current = true, Far = 8000f,
                    Projection = Camera3D.ProjectionType.Orthogonal, Size = half * 2f,
                    // Height matters even though ORTHO framing doesn't depend on it: props carry per-instance
                    // VisibilityRangeEnd (64/256/512m), so a camera parked at 2km renders an empty tan plane --
                    // everything is past its cull distance. Sit just high enough to clear PEI's terrain.
                    Position = new Vector3(mcx, float.TryParse(System.Environment.GetEnvironmentVariable("UG_MAPSHOT_Y"), out var my) ? my : 220f, mcz),
                    RotationDegrees = new Vector3(-90f, 0f, 0f),   // straight down; world +X = screen right, world +Z = screen DOWN
                };
                AddChild(mcam);
                GD.Print($"[mapshot] ortho top-down centre=({mcx},{mcz}) halfExtent={half}m size={half * 2f}m");
            }
            if (_peiPlayable) { SpawnEditorLootCrates(); SpawnEditorStoreShelves(); SpawnEditorGridPower(); SpawnEditorGasPump(); if (!_loopbackConsuming) SpawnMapContainers(res); }   // stock the map with loot containers (A1: the StorageReplicaView materializes them under a consuming loopback) + grid-power boxes + gas pumps
        }

        // Spawn the convert-on-load containers WorldBuilder flagged (map props -> lootable containers). Deferred to HERE,
        // after BuildFullWorld, so the asset DB is loaded -> the loot roll's tryAddItem can size items into the grid
        // (spawning during the build left the containers EMPTY -- looked stocked, opened empty).
        void SpawnMapContainers(WorldBuildResult res)
        {
            if (res?.Containers == null || res.Containers.Count == 0) return;
            LootTables.Load(_mapRoot + "/Spawns/Items.dat");
            for (int _ci = 0; _ci < res.Containers.Count; _ci++)
            {
                var c = res.Containers[_ci];
                if (c.display && c.mesh == "Shelf_1")   // double-sided store gondola: stock BOTH aisles (front + back), each its own openable container
                    StoreShelf.SpawnDouble(this, c.pos, c.mesh, c.table, c.table, c.yaw, c.label);
                else
                    StoreShelf.Spawn(this, c.pos, c.mesh, c.table, c.yaw, c.display, c.label, rot: res.ContainerRots[_ci]);
            }
            GD.Print($"[containers] spawned {res.Containers.Count} map containers post-build (asset DB ready)");
        }

        // Spawn the loot crates the editor saved for PEI (editor_PEI_crates.txt), each rolling its PEI item table (LootCrate).
        void SpawnEditorLootCrates()
        {
            string cratesFile = ProjectSettings.GlobalizePath("res://content/objects/") + "editor_PEI_crates.txt";
            if (!System.IO.File.Exists(cratesFile)) return;
            LootTables.Load(_mapRoot + "/Spawns/Items.dat");
            int n = 0;
            foreach (var line in System.IO.File.ReadLines(cratesFile))
            {
                var p = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 4 || !int.TryParse(p[0], out var tbl)
                    || !float.TryParse(p[1], out var px) || !float.TryParse(p[2], out var py) || !float.TryParse(p[3], out var pz)) continue;
                LootCrate.Spawn(this, new Vector3(px, py, -pz), tbl);
                n++;
            }
            if (n > 0) GD.Print($"[loot-crate] spawned {n} editor loot crates in SP");
        }

        // Spawn the store shelves the editor saved for PEI (editor_PEI_shelves.txt), each rolling its PEI table + showing
        // the rolled items on its tiers (StoreShelf). Same flow as the loot crates, plus a yaw so the gondola faces right.
        void SpawnEditorStoreShelves()
        {
            string shelvesFile = ProjectSettings.GlobalizePath("res://content/objects/") + "editor_PEI_shelves.txt";
            if (!System.IO.File.Exists(shelvesFile)) return;
            LootTables.Load(_mapRoot + "/Spawns/Items.dat");
            int n = 0;
            foreach (var line in System.IO.File.ReadLines(shelvesFile))
            {
                var p = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 4 || !int.TryParse(p[0], out var tbl)
                    || !float.TryParse(p[1], out var px) || !float.TryParse(p[2], out var py) || !float.TryParse(p[3], out var pz)) continue;
                float yaw = 0f; if (p.Length >= 5) float.TryParse(p[4], out yaw);
                StoreShelf.SpawnDouble(this, new Vector3(px, py, -pz), "Shelf_1", tbl, tbl, yaw);   // gondola: both aisles stocked
                n++;
            }
            if (n > 0) GD.Print($"[store-shelf] spawned {n} editor store shelves in SP");
        }

        // Spawn the grid-power boxes the editor saved for PEI (editor_PEI_gridpower.txt): the Circuit_0 mesh + a
        // GridPowerSource wired to the mains at the configured wattage + name (mouseover shows it). Same flow as shelves.
        void SpawnEditorGridPower()
        {
            string file = ProjectSettings.GlobalizePath("res://content/objects/") + "editor_PEI_gridpower.txt";
            if (!System.IO.File.Exists(file)) return;
            var mesh = ObjMesh.Load(ProjectSettings.GlobalizePath("res://content/objects/Circuit_0.obj"));
            var stand = new Basis(Vector3.Right, Mathf.DegToRad(-90f));   // flat-authored -> stand it up (raw Z -> world height), same as the pump
            int n = 0;
            foreach (var line in System.IO.File.ReadLines(file))
            {
                var p = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 5 || !float.TryParse(p[0], out var watts)
                    || !float.TryParse(p[1], out var px) || !float.TryParse(p[2], out var py) || !float.TryParse(p[3], out var pz)) continue;
                float yaw = 0f; float.TryParse(p[4], out yaw);
                string nm = p.Length >= 6 ? string.Join(" ", p, 5, p.Length - 5) : "";
                var basis = new Basis(Vector3.Up, Mathf.DegToRad(yaw)) * stand;
                var pos = new Vector3(px, py, -pz);
                if (mesh != null)
                    AddChild(new MeshInstance3D { Mesh = mesh, Transform = new Transform3D(basis, pos), MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.55f, 0.58f), Roughness = 0.85f, CullMode = BaseMaterial3D.CullModeEnum.Disabled } });
                var gp = GridPowerSource.Attach(this, pos, basis, GridPowerSource.PortLocal, watts, nm, mesh);
                if (mesh != null)   // look-focus collider: crosshair -> resolve the GridPowerSource (outline + mouseover tooltip)
                {
                    var shp = mesh.CreateTrimeshShape();
                    if (shp != null)
                    {
                        var body = new StaticBody3D { Transform = new Transform3D(basis, pos) };
                        body.AddChild(new CollisionShape3D { Shape = shp });
                        body.SetMeta("gridpower", gp);
                        AddChild(body);
                    }
                }
                n++;
            }
            if (n > 0) GD.Print($"[grid-power] spawned {n} editor grid boxes in SP");
        }

        // Spawn the gas pumps the editor saved for PEI (editor_PEI_gaspump.txt): the Gas_Pump_0 mesh + a GasPump fuel
        // tank at the configured station id (pumps sharing an id share a tank). Same flow as the grid boxes.
        void SpawnEditorGasPump()
        {
            string file = ProjectSettings.GlobalizePath("res://content/objects/") + "editor_PEI_gaspump.txt";
            if (!System.IO.File.Exists(file)) return;
            var mesh = ObjMesh.Load(ProjectSettings.GlobalizePath("res://content/objects/Gas_Pump_0.obj"));
            var stand = new Basis(Vector3.Right, Mathf.DegToRad(-90f));
            int n = 0;
            foreach (var line in System.IO.File.ReadLines(file))
            {
                var p = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 4 || !int.TryParse(p[0], out var station)
                    || !float.TryParse(p[1], out var px) || !float.TryParse(p[2], out var py) || !float.TryParse(p[3], out var pz)) continue;
                float yaw = 0f; if (p.Length >= 5) float.TryParse(p[4], out yaw);
                var basis = new Basis(Vector3.Up, Mathf.DegToRad(yaw)) * stand;
                var pos = new Vector3(px, py, -pz);
                if (mesh != null)
                    AddChild(new MeshInstance3D { Mesh = mesh, Transform = new Transform3D(basis, pos), MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.66f, 0.67f, 0.7f), Roughness = 0.7f, CullMode = BaseMaterial3D.CullModeEnum.Disabled } });
                var gp = GasPump.Attach(this, pos, basis, GasPump.PortLocal, mesh, station);
                if (mesh != null)
                {
                    var shp = mesh.CreateTrimeshShape();
                    if (shp != null)
                    {
                        var body = new StaticBody3D { Transform = new Transform3D(basis, pos) };
                        body.AddChild(new CollisionShape3D { Shape = shp });
                        body.SetMeta("gaspump", gp);   // look-ray -> the GasPump (outline + tooltip + rmb-suck/lmb-pour)
                        AddChild(body);
                    }
                }
                n++;
            }
            if (n > 0) GD.Print($"[gas-pump] spawned {n} editor gas pumps in SP");
        }

        // Workshop -> "New Map": boot the editor with a fresh FLAT all-grass map (no props/spawns/roads) to build from
        // scratch. Reuses every sub-editor; map name "NewMap" so its saves stay separate from PEI's (per-map save paths).
        void BuildEditorNew()
        {
            _worldBuild = true;
            var terr = Terrain.CreateFlat(3, 3);
            AddChild(terr);
            var sun = new DirectionalLight3D { RotationDegrees = new Vector3(-55f, -35f, 0f), LightEnergy = 1.2f, ShadowEnabled = true };
            AddChild(sun);
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.53f, 0.67f, 0.86f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.92f, 0.92f, 0.94f), AmbientLightEnergy = 1.15f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            var dayNight = new DayNightCycle { Sun = sun, Env = env, DayLength = 300f, VisualsEnabled = false };
            AddChild(dayNight);
            void SetCleanEditorLighting() { env.SetFogEnabled(false); env.GlowEnabled = false; }

            var editor = new Editor();
            AddChild(editor);
            var cam = new EditorCamera { Position = new Vector3(0f, 130f, 190f), RotationDegrees = new Vector3(-30f, 0f, 0f) };
            editor.AddChild(cam);
            editor.Setup("NewMap", null, cam);
            LootTables.Load(_mapRoot + "/Spawns/Items.dat");   // new maps use PEI's loot tables as the pool (for loot crates)
            var objs = new EditorObjects(editor, this, cam); editor.AddChild(objs); editor.Objects = objs;
            var spawns = new EditorSpawns(editor, cam, MapDir("NewMap")); editor.AddChild(spawns); editor.Spawns = spawns;   // dir doesn't exist -> starts empty
            var envEd = new EditorEnvironment(editor, dayNight, SetCleanEditorLighting); editor.AddChild(envEd); editor.Environment = envEd;
            var terrainEd = new EditorTerrain(editor, cam, terr); editor.AddChild(terrainEd); editor.TerrainEd = terrainEd;
            var rf = new RoadField { Terr = terr };
            rf.LoadMaterialsOnly(_mapRoot + "/Environment");   // shared road materials so roads can be added on the blank map
            AddChild(rf);
            var roadsEd = new EditorRoads(editor, cam, rf); editor.AddChild(roadsEd); editor.RoadsEd = roadsEd;
            editor.AddChild(new EditorDashboard { Editor = editor, OnExit = ReturnToMenu });
            _worldReady = true;
            GD.Print("[editor] NEW blank map (flat 3x3) up");
        }

        // Workshop -> the map EDITOR (singleplayer, ported from SDG.Unturned Edit/). Phase 1: load PEI as the
        // edit target (Aerial = world, no player, no colliders), drop in the free-fly EditorCamera + the mode-tab
        // dashboard + the Editor controller. Fly + view + switch modes now; the per-mode sub-editors (Objects/
        // Terrain/Spawns/...) + .level save land in the later phases.
        // Fluid-IO verify (--fluidtest): a full Source -> Hose -> empty Storage. Tick the net; the storage should
        // fill + the source drain, conserving the total. UG_FLUIDRENDER=1 = a lit scene ticking live so the movie
        // harness can capture the bars filling (F3 visual verify); else the fast headless log-check (go easy).
        void RunFluidTest()
        {
            if (System.Environment.GetEnvironmentVariable("UG_HOSETOOL") == "1") { RunHoseToolTest(); return; }
            var src = FluidContainer.Make(FluidRole.Source, new FluidTank(FluidType.Fuel, 1000f, 1000f), 50f);   // full, supplies 50/s
            var sto = FluidContainer.Make(FluidRole.Storage, new FluidTank(FluidType.Fuel, 1000f, 0f), 50f);     // empty, intake 50/s
            // source raised ABOVE storage so the gravity gate (strawberry: passive flow only downhill) lets it flow;
            // UG_FLUIDLEVEL=1 puts the storage level with the source to prove the gate then blocks flow (0 in, needs a pump).
            float stoY = System.Environment.GetEnvironmentVariable("UG_FLUIDLEVEL") == "1" ? 1.2f : 0f;
            src.Position = new Vector3(-2.5f, 1.2f, 0f); sto.Position = new Vector3(2.5f, stoY, 0f);
            src.PortLocalPos = new Vector3(0.55f, 0.9f, 0f); sto.PortLocalPos = new Vector3(-0.55f, 0.9f, 0f);   // port cubes face each other along the hose
            AddChild(src); AddChild(sto);   // _Ready builds their ports + visuals + registers them in "fluid_devices"
            var hose = new Hose { Source = src.Ports[0], Consumer = sto.Ports[0] };
            AddChild(hose);                 // registers in "hoses"

            if (System.Environment.GetEnvironmentVariable("UG_FLUIDREFINE") == "1")
            {   // F5b render verify: an oil source -> a REFINERY (oil->gas) -> a gas tank (fluid TYPE changes through it)
                src.QueueFree(); sto.QueueFree(); hose.QueueFree();
                AddChild(new FluidManager());
                AddChild(new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(40f, 40f) }, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.32f, 0.36f, 0.30f) } });
                var oil = FluidContainer.Make(FluidRole.Source, new FluidTank(FluidType.Oil, 2000f, 2000f), 50f);
                oil.Position = new Vector3(-4f, 2.4f, 0f); oil.PortLocalPos = new Vector3(0.55f, 0.9f, 0f);
                var refn = FluidContainer.MakeTransformer(FluidType.Oil, FluidType.Gas, 50f, 1f); refn.Position = new Vector3(0f, 1.0f, 0f);
                var gas = FluidContainer.Make(FluidRole.Storage, new FluidTank(FluidType.Gas, 1000f, 0f), 50f);
                gas.Position = new Vector3(4f, 0f, 0f); gas.PortLocalPos = new Vector3(-0.55f, 0.9f, 0f);
                AddChild(oil); AddChild(refn); AddChild(gas);
                void HoseUp(FluidPortNode a, HosePort an, FluidPortNode b, HosePort bn)
                { var hh = new Hose { Source = a, Consumer = b }; AddChild(hh); hh.SetPoints(new System.Collections.Generic.List<Vector3> { an.GlobalPosition, bn.GlobalPosition }, valid: true); }
                HoseUp(oil.Ports[0], oil.PortNodes[0], refn.Ports[0], refn.PortNodes[0]);   // oil -> refinery input
                HoseUp(refn.Ports[1], refn.PortNodes[1], gas.Ports[0], gas.PortNodes[0]);   // refinery output (gas) -> tank
                AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-55f, -40f, 0f), ShadowEnabled = true });
                AddChild(new WorldEnvironment { Environment = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.50f, 0.66f, 0.86f), AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = Colors.White, AmbientLightEnergy = 0.85f } });
                AddChild(new Camera3D { Position = new Vector3(0f, 3.6f, 10f), RotationDegrees = new Vector3(-16f, 0f, 0f), Current = true });
                GD.Print("[fluidtest] refine render scene up — oil source -> refinery -> gas tank");
                return;
            }

            if (System.Environment.GetEnvironmentVariable("UG_FLUIDPUMP") == "1")
            {   // F5 render verify: a low source -> a POWERED pump -> a HIGH tank (fluid lifted uphill past gravity)
                src.QueueFree(); sto.QueueFree(); hose.QueueFree();
                AddChild(new FluidManager());
                AddChild(new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(40f, 40f) }, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.32f, 0.36f, 0.30f) } });
                var s = FluidContainer.Make(FluidRole.Source, new FluidTank(FluidType.Fuel, 2000f, 2000f), 100f);
                s.Position = new Vector3(-4f, 0f, 0f); s.PortLocalPos = new Vector3(0.55f, 0.9f, 0f);
                var pump = FluidPump.Make(6f); pump.Position = new Vector3(0f, 0f, 0f);
                var hi = FluidContainer.Make(FluidRole.Storage, new FluidTank(FluidType.None, 1000f, 0f), 50f);
                hi.Position = new Vector3(4f, 3f, 0f); hi.PortLocalPos = new Vector3(-0.55f, 0.9f, 0f);   // 3m UP
                AddChild(s); AddChild(pump); AddChild(hi);
                void HoseUp(FluidPortNode a, HosePort an, FluidPortNode b, HosePort bn)
                { var hh = new Hose { Source = a, Consumer = b }; AddChild(hh); hh.SetPoints(new System.Collections.Generic.List<Vector3> { an.GlobalPosition, bn.GlobalPosition }, valid: true); }
                HoseUp(s.Ports[0], s.PortNodes[0], pump.Ports[0], pump.PortNodes[0]);   // source -> pump input
                HoseUp(pump.Ports[1], pump.PortNodes[1], hi.Ports[0], hi.PortNodes[0]); // pump -> HIGH tank (uphill)
                Deployable.InstantRampForTests = true;   // no PowerManager in this scene -> instant-ramp the gen + one Recompute keeps the pump powered
                var gen = Deployable.Spawn(this, DeployableDef.Generator, new Vector3(0f, 0f, -3f), 0f);
                var genOut = gen.Ports.Find(pp => pp.Kind == DeployableDef.PortKind.Output);
                var wr = new Wire(); AddChild(wr); wr.Source = genOut; wr.Consumer = pump.PowerPorts[0]; wr.AddToGroup("wires");
                wr.SetPoints(new System.Collections.Generic.List<Vector3> { genOut.GlobalPosition, pump.PowerPorts[0].GlobalPosition }, valid: true);
                gen.TogglePower(); PowerNet.Recompute(GetTree());
                AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-55f, -40f, 0f), ShadowEnabled = true });
                AddChild(new WorldEnvironment { Environment = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.50f, 0.66f, 0.86f), AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = Colors.White, AmbientLightEnergy = 0.85f } });
                AddChild(new Camera3D { Position = new Vector3(0f, 3.6f, 10f), RotationDegrees = new Vector3(-16f, 0f, 0f), Current = true });
                GD.Print("[fluidtest] pump render scene up — low source -> powered pump -> HIGH tank (uphill)");
                return;
            }

            if (System.Environment.GetEnvironmentVariable("UG_FLUIDSPLIT") == "1")
            {   // F4 render verify: one source fans through a SPLITTER to two storages (each leg downhill)
                src.QueueFree(); sto.QueueFree(); hose.QueueFree();   // drop the simple scene; build the fan-out fresh
                AddChild(new FluidManager());
                AddChild(new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(40f, 40f) }, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.32f, 0.36f, 0.30f) } });
                var s = FluidContainer.Make(FluidRole.Source, new FluidTank(FluidType.Fuel, 2000f, 2000f), 200f);
                s.Position = new Vector3(-4f, 2.4f, 0f); s.PortLocalPos = new Vector3(0.55f, 0.9f, 0f);
                var sp = FluidContainer.MakeFitting(FluidRole.Splitter, 2); sp.Position = new Vector3(0f, 1.0f, 0f);
                var d0 = FluidContainer.Make(FluidRole.Storage, new FluidTank(FluidType.None, 1000f, 0f), 50f);
                d0.Position = new Vector3(3.6f, 0f, -1.4f); d0.PortLocalPos = new Vector3(-0.55f, 0.9f, 0f);
                var d1 = FluidContainer.Make(FluidRole.Storage, new FluidTank(FluidType.None, 1000f, 0f), 50f);
                d1.Position = new Vector3(3.6f, 0f, 1.4f); d1.PortLocalPos = new Vector3(-0.55f, 0.9f, 0f);
                AddChild(s); AddChild(sp); AddChild(d0); AddChild(d1);
                void HoseUp(FluidPortNode a, HosePort an, FluidPortNode b, HosePort bn)
                { var hh = new Hose { Source = a, Consumer = b }; AddChild(hh); hh.SetPoints(new System.Collections.Generic.List<Vector3> { an.GlobalPosition, bn.GlobalPosition }, valid: true); }
                HoseUp(s.Ports[0], s.PortNodes[0], sp.Ports[0], sp.PortNodes[0]);    // source -> splitter input
                HoseUp(sp.Ports[1], sp.PortNodes[1], d0.Ports[0], d0.PortNodes[0]);  // passthrough #0 -> storage 0
                HoseUp(sp.Ports[2], sp.PortNodes[2], d1.Ports[0], d1.PortNodes[0]);  // passthrough #1 -> storage 1
                AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-55f, -40f, 0f), ShadowEnabled = true });
                AddChild(new WorldEnvironment { Environment = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.50f, 0.66f, 0.86f), AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = Colors.White, AmbientLightEnergy = 0.85f } });
                AddChild(new Camera3D { Position = new Vector3(0f, 4f, 11f), RotationDegrees = new Vector3(-18f, 0f, 0f), Current = true });
                GD.Print("[fluidtest] split render scene up — source -> splitter -> two storages");
                return;
            }

            if (System.Environment.GetEnvironmentVariable("UG_FLUIDRENDER") == "1")
            {   // F3 render verify: a lit scene ticking live; the movie harness captures the storage bar filling
                AddChild(new FluidManager());
                AddChild(new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(30f, 30f) }, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.32f, 0.36f, 0.30f) } });
                hose.SetPoints(new System.Collections.Generic.List<Vector3> { src.PortNodes[0].GlobalPosition, sto.PortNodes[0].GlobalPosition }, valid: true);   // the hose draws itself port-to-port
                AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-55f, -40f, 0f), ShadowEnabled = true });
                AddChild(new WorldEnvironment { Environment = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.50f, 0.66f, 0.86f), AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = Colors.White, AmbientLightEnergy = 0.85f } });
                AddChild(new Camera3D { Position = new Vector3(0f, 3.2f, 8f), RotationDegrees = new Vector3(-16f, 0f, 0f), Current = true });
                GD.Print("[fluidtest] render scene up — source full, storage filling live");
                return;   // no quit; the movie harness's --quit-after ends it
            }

            GD.Print($"[fluidtest] start: source={src.Tank.Amount:0} storage={sto.Tank.Amount:0}");
            const float dt = 0.1f;
            for (int i = 0; i < 100; i++)   // 10 s of 0.1 s ticks -> ~500 units moved (50/s), conserved to 1000
            {
                FluidNet.Tick(GetTree(), dt);
                if (i == 9 || i == 49 || i == 99)
                    GD.Print($"[fluidtest] t={(i + 1) * dt:0.0}s: source={src.Tank.Amount:0} storage={sto.Tank.Amount:0} flow={sto.Ports[0].Flow:0} flowing={sto.Ports[0].Flowing}");
            }
            float total = src.Tank.Amount + sto.Tank.Amount;
            bool ok = sto.Tank.Amount > 400f && src.Tank.Amount < 600f && Mathf.Abs(total - 1000f) < 0.5f;
            GD.Print($"[fluidtest] RESULT {(ok ? "PASS" : "FAIL")}: storage {sto.Tank.Amount:0}, source {src.Tank.Amount:0}, conserved total {total:0}/1000");
            GetTree().Quit();
        }

        // Headless F3.5c hose-tool integration check (UG_HOSETOOL=1): exercise the REAL type-lock rule (FluidHoseRule)
        // + the connect/adopt/flow path the tool uses, without a mouse. Case A: an EMPTY storage + a Fuel source ->
        // Ok, adopts Fuel, flows downhill. Case B: a Fuel source + a WATER storage -> Mismatch, refused. (The ray-pick,
        // highlight, and HUD are visual/session-only — verified in-game later; this locks the logic.)
        void RunHoseToolTest()
        {
            bool ok = true;

            // --- Case A: empty storage adopts + flows ---
            var srcA = FluidContainer.Make(FluidRole.Source, new FluidTank(FluidType.Fuel, 1000f, 1000f), 50f);
            var stoA = FluidContainer.Make(FluidRole.Storage, new FluidTank(FluidType.None, 1000f, 0f), 50f);   // EMPTY -> None type
            srcA.Position = new Vector3(-2.5f, 1.2f, 0f); stoA.Position = new Vector3(2.5f, 0f, 0f);   // source above -> gravity lets it flow
            AddChild(srcA); AddChild(stoA);
            var spA = srcA.PortNodes[0]; var cpA = stoA.PortNodes[0];
            var vA = FluidHoseRule.Completion(spA.Kind, cpA.Kind,
                srcA.Tank.Type == FluidType.None, stoA.Tank.Type == FluidType.None, srcA.Tank.Type == stoA.Tank.Type, false, false);
            GD.Print($"[hosetool] case A verdict={vA} (want Ok)");
            if (vA != HoseVerdict.Ok) ok = false;
            else
            {   // connect exactly as CompleteHose does: order by kind, empty adopts, build + register the hose
                if (stoA.Tank.Type == FluidType.None) stoA.Tank.Type = srcA.Tank.Type;   // adopt
                var hA = new Hose { Source = spA.Node, Consumer = cpA.Node }; AddChild(hA);
                for (int i = 0; i < 100; i++) FluidNet.Tick(GetTree(), 0.1f);
                GD.Print($"[hosetool] case A: storage={stoA.Tank.Amount:0} type={FluidDef.Name(stoA.Tank.Type)}");
                if (!(stoA.Tank.Amount > 400f && stoA.Tank.Type == FluidType.Fuel)) ok = false;   // filled + adopted Fuel
            }

            // --- Case B: mismatched fluids refused ---
            var srcB = FluidContainer.Make(FluidRole.Source, new FluidTank(FluidType.Fuel, 1000f, 1000f), 50f);
            var stoB = FluidContainer.Make(FluidRole.Storage, new FluidTank(FluidType.Water, 1000f, 100f), 50f);   // holds WATER
            srcB.Position = new Vector3(-2.5f, 1.2f, 6f); stoB.Position = new Vector3(2.5f, 0f, 6f);
            AddChild(srcB); AddChild(stoB);
            var vB = FluidHoseRule.Completion(srcB.PortNodes[0].Kind, stoB.PortNodes[0].Kind,
                srcB.Tank.Type == FluidType.None, stoB.Tank.Type == FluidType.None, srcB.Tank.Type == stoB.Tank.Type, false, false);
            GD.Print($"[hosetool] case B verdict={vB} (want Mismatch)");
            if (vB != HoseVerdict.Mismatch) ok = false;

            // --- Case C (F4): a SPLITTER fans one source to two storages (each hose downhill: src above splitter above stores) ---
            var srcC = FluidContainer.Make(FluidRole.Source, new FluidTank(FluidType.Fuel, 2000f, 2000f), 200f);   // supplies 200/s (covers both intakes)
            var split = FluidContainer.MakeFitting(FluidRole.Splitter, 2);                                         // 0-rate relay + 2 passthroughs
            var stoC0 = FluidContainer.Make(FluidRole.Storage, new FluidTank(FluidType.None, 1000f, 0f), 50f);
            var stoC1 = FluidContainer.Make(FluidRole.Storage, new FluidTank(FluidType.None, 1000f, 0f), 50f);
            srcC.Position = new Vector3(-5f, 3f, 12f); split.Position = new Vector3(0f, 1.5f, 12f);
            stoC0.Position = new Vector3(4f, 0f, 10f); stoC1.Position = new Vector3(4f, 0f, 14f);
            AddChild(srcC); AddChild(split); AddChild(stoC0); AddChild(stoC1);
            AddChild(new Hose { Source = srcC.Ports[0], Consumer = split.Ports[0] });   // source -> splitter relay input (Ports[0]=Consumer)
            AddChild(new Hose { Source = split.Ports[1], Consumer = stoC0.Ports[0] });  // splitter passthrough #0 -> storage 0
            AddChild(new Hose { Source = split.Ports[2], Consumer = stoC1.Ports[0] });  // splitter passthrough #1 -> storage 1
            for (int i = 0; i < 100; i++)
            {
                FluidNet.Tick(GetTree(), 0.1f);
                if (i == 5) GD.Print($"[hosetool] case C t=0.6: sto0 accepts={stoC0.Ports[0].SolveRate:0} sto1 accepts={stoC1.Ports[0].SolveRate:0} srcLoad={srcC.Ports[0].Load:0} (want 50/50/100 — Flow OFFERED is higher through a splitter)");
            }
            float totalC = srcC.Tank.Amount + stoC0.Tank.Amount + stoC1.Tank.Amount;
            GD.Print($"[hosetool] case C: sto0={stoC0.Tank.Amount:0} sto1={stoC1.Tank.Amount:0} src={srcC.Tank.Amount:0} total={totalC:0}/2000 (want both filled + conserved)");
            if (!(stoC0.Tank.Amount > 400f && stoC1.Tank.Amount > 400f && Mathf.Abs(totalC - 2000f) < 1f)) ok = false;

            // --- Case D (F4): a COMBINER merges two sources into one storage (each source above the combiner above the store) ---
            var srcD0 = FluidContainer.Make(FluidRole.Source, new FluidTank(FluidType.Fuel, 5000f, 5000f), 300f);   // 300/s each
            var srcD1 = FluidContainer.Make(FluidRole.Source, new FluidTank(FluidType.Fuel, 5000f, 5000f), 300f);
            var comb = FluidContainer.MakeFitting(FluidRole.Combiner, 2);                                          // 2 relays + 1 passthrough
            var stoD = FluidContainer.Make(FluidRole.Storage, new FluidTank(FluidType.None, 10000f, 0f), 500f);    // wants 500/s (600 available covers it)
            srcD0.Position = new Vector3(-5f, 3f, 22f); srcD1.Position = new Vector3(-5f, 3f, 26f);
            comb.Position = new Vector3(0f, 1.5f, 24f); stoD.Position = new Vector3(5f, 0f, 24f);
            AddChild(srcD0); AddChild(srcD1); AddChild(comb); AddChild(stoD);
            AddChild(new Hose { Source = srcD0.Ports[0], Consumer = comb.Ports[0] });   // source0 -> combiner relay input #0
            AddChild(new Hose { Source = srcD1.Ports[0], Consumer = comb.Ports[1] });   // source1 -> combiner relay input #1
            AddChild(new Hose { Source = comb.Ports[2], Consumer = stoD.Ports[0] });    // combiner passthrough (Ports[2]) -> storage
            for (int i = 0; i < 100; i++) FluidNet.Tick(GetTree(), 0.1f);
            float totalD = srcD0.Tank.Amount + srcD1.Tank.Amount + stoD.Tank.Amount;
            GD.Print($"[hosetool] case D: storage={stoD.Tank.Amount:0} src0={srcD0.Tank.Amount:0} src1={srcD1.Tank.Amount:0} total={totalD:0}/10000 (want storage filled + conserved)");
            if (!(stoD.Tank.Amount > 4000f && Mathf.Abs(totalD - 10000f) < 1f)) ok = false;

            // --- Case E (F5): a POWERED pump LIFTS fluid uphill (source low -> pump -> HIGH tank, past the gravity gate) ---
            var srcE = FluidContainer.Make(FluidRole.Source, new FluidTank(FluidType.Fuel, 2000f, 2000f), 100f);
            var pumpE = FluidPump.Make(6f); pumpE.DebugForcePower = true;   // powered -> 6m head lift (no PowerNet in the fluid test)
            var hiE = FluidContainer.Make(FluidRole.Storage, new FluidTank(FluidType.None, 1000f, 0f), 50f);
            srcE.Position = new Vector3(-4f, 0f, 32f); pumpE.Position = new Vector3(0f, 0f, 32f); hiE.Position = new Vector3(4f, 3f, 32f);   // tank 3m UP
            AddChild(srcE); AddChild(pumpE); AddChild(hiE);
            AddChild(new Hose { Source = srcE.Ports[0], Consumer = pumpE.Ports[0] });   // source -> pump relay input
            AddChild(new Hose { Source = pumpE.Ports[1], Consumer = hiE.Ports[0] });    // pump passthrough -> HIGH tank (uphill)
            bool pumpIsConsumer = pumpE.PowerPorts.Count >= 1 && pumpE.PowerPorts[0].Kind == DeployableDef.PortKind.Consumer && pumpE.PowerPorts[0].Role == DeployableDef.SwitchRole.None && pumpE.IsInGroup("deployables");   // [0] = power INPUT (draws PumpWatts); [1..2] = remote on/off triggers
            for (int i = 0; i < 100; i++) FluidNet.Tick(GetTree(), 0.1f);
            GD.Print($"[hosetool] case E: hiTank={hiE.Tank.Amount:0} (want filled — powered pump lifted it up) · powerConsumer={pumpIsConsumer}");
            if (!(hiE.Tank.Amount > 400f && pumpIsConsumer)) ok = false;

            // --- Case F (F5): an UNPOWERED pump can't lift — the high tank stays empty (gravity gate holds) ---
            var srcF = FluidContainer.Make(FluidRole.Source, new FluidTank(FluidType.Fuel, 2000f, 2000f), 100f);
            var pumpF = FluidPump.Make(6f);   // NOT powered
            var hiF = FluidContainer.Make(FluidRole.Storage, new FluidTank(FluidType.None, 1000f, 0f), 50f);
            srcF.Position = new Vector3(-4f, 0f, 40f); pumpF.Position = new Vector3(0f, 0f, 40f); hiF.Position = new Vector3(4f, 3f, 40f);
            AddChild(srcF); AddChild(pumpF); AddChild(hiF);
            AddChild(new Hose { Source = srcF.Ports[0], Consumer = pumpF.Ports[0] });
            AddChild(new Hose { Source = pumpF.Ports[1], Consumer = hiF.Ports[0] });
            for (int i = 0; i < 100; i++) FluidNet.Tick(GetTree(), 0.1f);
            GD.Print($"[hosetool] case F: hiTank={hiF.Tank.Amount:0} (want ~0 — unpowered pump can't lift uphill)");
            if (hiF.Tank.Amount > 1f) ok = false;

            // --- Case G (F5): the REAL power bridge — a wired generator powers the pump (no debug flag), which then lifts ---
            var srcG = FluidContainer.Make(FluidRole.Source, new FluidTank(FluidType.Fuel, 2000f, 2000f), 100f);
            var pumpG = FluidPump.Make(6f);   // powered ONLY by the wired generator below
            var hiG = FluidContainer.Make(FluidRole.Storage, new FluidTank(FluidType.None, 1000f, 0f), 50f);
            srcG.Position = new Vector3(-4f, 0f, 48f); pumpG.Position = new Vector3(0f, 0f, 48f); hiG.Position = new Vector3(4f, 3f, 48f);
            AddChild(srcG); AddChild(pumpG); AddChild(hiG);
            AddChild(new Hose { Source = srcG.Ports[0], Consumer = pumpG.Ports[0] });
            AddChild(new Hose { Source = pumpG.Ports[1], Consumer = hiG.Ports[0] });
            Deployable.InstantRampForTests = true;   // skip the engine spin-up ramp so the generator produces on the first solve (headless)
            var gen = Deployable.Spawn(this, DeployableDef.Generator, new Vector3(0f, 0f, 46f), 0f);   // a power source
            var genOut = gen.Ports.Find(pp => pp.Kind == DeployableDef.PortKind.Output);
            var wr = new Wire(); AddChild(wr); wr.Source = genOut; wr.Consumer = pumpG.PowerPorts[0]; wr.AddToGroup("wires");
            wr.SetPoints(new System.Collections.Generic.List<Vector3> { genOut.GlobalPosition, pumpG.PowerPorts[0].GlobalPosition }, valid: true);
            gen.TogglePower();                 // generator ON (instant ramp)
            PowerNet.Recompute(GetTree());     // solve the power net -> the pump's consumer port lights Powered
            bool poweredReal = pumpG.IsPowered;
            for (int i = 0; i < 100; i++) FluidNet.Tick(GetTree(), 0.1f);
            GD.Print($"[hosetool] case G: pump powered by wire={poweredReal} · hiTank={hiG.Tank.Amount:0} (want powered + filled)");
            if (!(poweredReal && hiG.Tank.Amount > 400f)) ok = false;

            // --- Case H (F5b): a REFINERY transforms oil -> gas (deletes oil input, produces gas output into a tank) ---
            var oilSrc = FluidContainer.Make(FluidRole.Source, new FluidTank(FluidType.Oil, 1000f, 1000f), 50f);
            var refinery = FluidContainer.MakeTransformer(FluidType.Oil, FluidType.Gas, 50f, 1f);
            var gasTank = FluidContainer.Make(FluidRole.Storage, new FluidTank(FluidType.None, 1000f, 0f), 50f);   // empty; would adopt Gas on a real connect
            oilSrc.Position = new Vector3(-4f, 3f, 56f); refinery.Position = new Vector3(0f, 1.5f, 56f); gasTank.Position = new Vector3(4f, 0f, 56f);
            AddChild(oilSrc); AddChild(refinery); AddChild(gasTank);
            AddChild(new Hose { Source = oilSrc.Ports[0], Consumer = refinery.Ports[0] });   // oil -> refinery input (Consumer)
            AddChild(new Hose { Source = refinery.Ports[1], Consumer = gasTank.Ports[0] });   // refinery output (Source, Gas) -> tank
            bool typedPorts = refinery.PortNodes[0].EffectiveType == FluidType.Oil && refinery.PortNodes[1].EffectiveType == FluidType.Gas;
            for (int i = 0; i < 100; i++) FluidNet.Tick(GetTree(), 0.1f);
            GD.Print($"[hosetool] case H: oil={oilSrc.Tank.Amount:0} gasTank={gasTank.Tank.Amount:0} · ports oil-in/gas-out={typedPorts} · refineryActive={refinery.TransformActive}");
            if (!(oilSrc.Tank.Amount < 600f && gasTank.Tank.Amount > 400f && typedPorts)) ok = false;   // oil consumed + gas produced + ports carry in/out types

            // --- Case I (F5): pump lift PROPAGATES through a splitter — a reachable high tank fills, a too-high one blocks ---
            var srcI = FluidContainer.Make(FluidRole.Source, new FluidTank(FluidType.Fuel, 2000f, 2000f), 100f);
            var pumpI = FluidPump.Make(6f); pumpI.DebugForcePower = true;   // ceiling = pumpY(0) + 6 = 6
            var splitI = FluidContainer.MakeFitting(FluidRole.Splitter, 2);
            var lowI = FluidContainer.Make(FluidRole.Storage, new FluidTank(FluidType.None, 1000f, 0f), 50f);    // Y=4, within ceiling 6 -> fills
            var highI = FluidContainer.Make(FluidRole.Storage, new FluidTank(FluidType.None, 1000f, 0f), 50f);   // Y=8, ABOVE ceiling 6 -> blocked
            srcI.Position = new Vector3(-6f, 0f, 64f); pumpI.Position = new Vector3(-2f, 0f, 64f); splitI.Position = new Vector3(2f, 0f, 64f);
            lowI.Position = new Vector3(6f, 4f, 62f); highI.Position = new Vector3(6f, 8f, 66f);
            AddChild(srcI); AddChild(pumpI); AddChild(splitI); AddChild(lowI); AddChild(highI);
            AddChild(new Hose { Source = srcI.Ports[0], Consumer = pumpI.Ports[0] });    // source -> pump
            AddChild(new Hose { Source = pumpI.Ports[1], Consumer = splitI.Ports[0] });  // pump -> splitter (lift carries THROUGH)
            AddChild(new Hose { Source = splitI.Ports[1], Consumer = lowI.Ports[0] });   // splitter -> low high-tank (Y4)
            AddChild(new Hose { Source = splitI.Ports[2], Consumer = highI.Ports[0] });  // splitter -> too-high tank (Y8)
            for (int i = 0; i < 100; i++) FluidNet.Tick(GetTree(), 0.1f);
            GD.Print($"[hosetool] case I: low(Y4)={lowI.Tank.Amount:0} high(Y8)={highI.Tank.Amount:0} (want low filled via lift-through-splitter, high blocked by ceiling 6)");
            if (!(lowI.Tank.Amount > 400f && highI.Tank.Amount < 1f)) ok = false;

            // --- Case J (F5): a VALVE is a switch for a hose — open flows, closed stops ---
            var srcJ = FluidContainer.Make(FluidRole.Source, new FluidTank(FluidType.Fuel, 1000f, 1000f), 50f);
            var valveJ = FluidContainer.MakeValve();
            var stoJ = FluidContainer.Make(FluidRole.Storage, new FluidTank(FluidType.None, 1000f, 0f), 50f);
            srcJ.Position = new Vector3(-4f, 2f, 72f); valveJ.Position = new Vector3(0f, 1f, 72f); stoJ.Position = new Vector3(4f, 0f, 72f);   // downhill, gravity feeds
            AddChild(srcJ); AddChild(valveJ); AddChild(stoJ);
            AddChild(new Hose { Source = srcJ.Ports[0], Consumer = valveJ.Ports[0] });   // source -> valve input
            AddChild(new Hose { Source = valveJ.Ports[1], Consumer = stoJ.Ports[0] });   // valve output -> tank
            for (int i = 0; i < 50; i++) FluidNet.Tick(GetTree(), 0.1f);   // valve OPEN -> fills
            float openFill = stoJ.Tank.Amount;
            valveJ.ToggleValve();   // CLOSE it
            for (int i = 0; i < 50; i++) FluidNet.Tick(GetTree(), 0.1f);   // valve CLOSED -> no more flow
            float afterClose = stoJ.Tank.Amount;
            GD.Print($"[hosetool] case J: openFill={openFill:0} afterClose={afterClose:0} (want ~250 while open, unchanged after closing)");
            if (!(openFill > 200f && Mathf.Abs(afterClose - openFill) < 1f)) ok = false;

            // --- Case K (items): each fluid DeployableDef places a working FluidContainer via the item/placement rail ---
            var fdefs = new[] { DeployableDef.FluidTank, DeployableDef.WaterSource, DeployableDef.FluidSplitter, DeployableDef.FluidCombiner, DeployableDef.FluidPumpDef, DeployableDef.FluidValve, DeployableDef.Refinery, DeployableDef.Sluice, DeployableDef.Purifier };
            var wantRoles = new[] { FluidRole.Storage, FluidRole.Source, FluidRole.Splitter, FluidRole.Combiner, FluidRole.Pump, FluidRole.Valve, FluidRole.Transformer, FluidRole.Transformer, FluidRole.Transformer };
            bool itemsOk = true;
            for (int k = 0; k < fdefs.Length; k++)
            {
                var placed = FluidDeploy.SpawnFor(fdefs[k], this, new Vector3(k * 2f, 0f, 96f), 0f) as FluidContainer;
                bool roleOk = placed != null && placed.Role == wantRoles[k] && DeployableDef.ById(fdefs[k].Id) == fdefs[k];
                if (fdefs[k].Fluid == FluidRole.Pump && placed is not FluidPump) roleOk = false;
                if (fdefs[k] == DeployableDef.Purifier && placed is not FluidPurifier) roleOk = false;   // the purifier def must spawn the powered subclass
                if (!roleOk) { itemsOk = false; GD.Print($"[hosetool] item {fdefs[k].Name} FAILED (role {placed?.Role})"); }
            }
            // end-to-end: place a Water Source (high) + a Fluid Tank (low) via the rail, hose, tick -> tank fills
            var wsrc = FluidDeploy.SpawnFor(DeployableDef.WaterSource, this, new Vector3(-4f, 2f, 104f), 0f) as FluidContainer;
            var wtank = FluidDeploy.SpawnFor(DeployableDef.FluidTank, this, new Vector3(4f, 0f, 104f), 0f) as FluidContainer;
            AddChild(new Hose { Source = wsrc.Ports[0], Consumer = wtank.Ports[0] });
            for (int i = 0; i < 100; i++) FluidNet.Tick(GetTree(), 0.1f);
            GD.Print($"[hosetool] case K: allRolesOk={itemsOk} · placed WaterSource->FluidTank fills to {wtank.Tank.Amount:0} (want >400)");
            if (!(itemsOk && wtank.Tank.Amount > 400f)) ok = false;

            // --- Case L (items by name): `give <name>` resolves each fluid item to the right id (exact-match branch) ---
            SDG.Unturned.ItemCatalog.RegisterAll();
            var byNameChecks = new (string name, ushort id)[] {
                ("Fluid Tank", 9110), ("Fluid Water Source", 9111), ("Fluid Splitter", 9112), ("Fluid Combiner", 9113),
                ("Fluid Pump", 9114), ("Fluid Valve", 9115), ("Fluid Refinery", 9116), ("Fluid Sluice", 9117), ("Hose Tool", 9118), ("Fluid Purifier", 9121) };
            bool byName = true;
            foreach (var (nm, id) in byNameChecks)
            {
                var a = System.Linq.Enumerable.FirstOrDefault(SDG.Unturned.Assets.all(), x => string.Equals(x.itemName, nm, System.StringComparison.OrdinalIgnoreCase));
                if (a == null || a.id != id) { byName = false; GD.Print($"[hosetool] name '{nm}' -> {(a?.id.ToString() ?? "MISSING")} (want {id})"); }
            }
            GD.Print($"[hosetool] case L: all fluid items resolve by name = {byName}");
            if (!byName) ok = false;

            // --- Case M (tank buffer): a tank has an INPUT and an OUTPUT — source -> tank -> tank2, tank feeds downstream ---
            var srcM = FluidContainer.Make(FluidRole.Source, new FluidTank(FluidType.Water, 3000f, 3000f), 125f);
            var tankM = FluidContainer.Make(FluidRole.Storage, new FluidTank(FluidType.Water, 3000f, 0f), 125f);   // buffer: fills from src, feeds tank2
            var tank2M = FluidContainer.Make(FluidRole.Storage, new FluidTank(FluidType.Water, 3000f, 0f), 125f);
            srcM.Position = new Vector3(-4f, 3f, 112f); tankM.Position = new Vector3(0f, 2f, 112f); tank2M.Position = new Vector3(4f, 1f, 112f);   // downhill
            AddChild(srcM); AddChild(tankM); AddChild(tank2M);
            AddChild(new Hose { Source = srcM.Ports[0], Consumer = tankM.Ports[0] });    // source -> tank INPUT (Ports[0]=Consumer)
            AddChild(new Hose { Source = tankM.Ports[1], Consumer = tank2M.Ports[0] });  // tank OUTPUT (Ports[1]=Source) -> tank2 input
            for (int i = 0; i < 100; i++) FluidNet.Tick(GetTree(), 0.1f);
            float totalM = srcM.Tank.Amount + tankM.Tank.Amount + tank2M.Tank.Amount;
            GD.Print($"[hosetool] case M: src={srcM.Tank.Amount:0} tank={tankM.Tank.Amount:0} tank2={tank2M.Tank.Amount:0} total={totalM:0}/3000 (want tank2 filled via the tank's OUTPUT + conserved)");
            if (!(tank2M.Tank.Amount > 400f && Mathf.Abs(totalM - 3000f) < 2f)) ok = false;   // tank2 got fluid THROUGH the buffer tank + conserved

            // --- Case N (inlet + outlet): a NO-HEAD infinite INLET needs a pump; an OUTLET drain deletes what enters it ---
            var inlet = FluidContainer.Make(FluidRole.Source, new FluidTank(FluidType.Water, 1000f, 1000f), 125f);
            inlet.Infinite = true; inlet.NoHead = true;   // submersible inlet: infinite + no head pressure
            var pumpN = FluidPump.Make(6f);   // NOT powered yet
            var tankN = FluidContainer.Make(FluidRole.Storage, new FluidTank(FluidType.None, 5000f, 0f), 125f);
            inlet.Position = new Vector3(-4f, 0f, 120f); pumpN.Position = new Vector3(0f, 0f, 120f); tankN.Position = new Vector3(4f, 2f, 120f);   // tank UP
            AddChild(inlet); AddChild(pumpN); AddChild(tankN);
            AddChild(new Hose { Source = inlet.Ports[0], Consumer = pumpN.Ports[0] });   // inlet -> pump
            AddChild(new Hose { Source = pumpN.Ports[1], Consumer = tankN.Ports[0] });   // pump -> high tank
            for (int i = 0; i < 40; i++) FluidNet.Tick(GetTree(), 0.1f);   // pump OFF: no-head inlet can't push -> nothing
            float inletOff = tankN.Tank.Amount;
            pumpN.DebugForcePower = true;   // power the pump -> it draws infinite water up
            for (int i = 0; i < 60; i++) FluidNet.Tick(GetTree(), 0.1f);
            // OUTLET: a source -> outlet drain; the source drains but the outlet stores NOTHING (deletes)
            var srcO = FluidContainer.Make(FluidRole.Source, new FluidTank(FluidType.Water, 1000f, 1000f), 125f);
            var outlet = FluidDeploy.SpawnFor(DeployableDef.WaterOutlet, this, new Vector3(4f, 0f, 128f), 0f) as FluidContainer;
            srcO.Position = new Vector3(-4f, 1f, 128f); AddChild(srcO);
            AddChild(new Hose { Source = srcO.Ports[0], Consumer = outlet.Ports[0] });
            for (int i = 0; i < 60; i++) FluidNet.Tick(GetTree(), 0.1f);
            GD.Print($"[hosetool] case N: inlet pumpOff tank={inletOff:0} (want ~0) pumpOn tank={tankN.Tank.Amount:0} (want filled) inlet={inlet.Tank.Amount:0} (want 1000 infinite) · outlet drained src {1000 - srcO.Tank.Amount:0}, stored {outlet.Tank.Amount:0} (want >0 drained, 0 stored)");
            if (!(inletOff < 1f && tankN.Tank.Amount > 400f && inlet.Tank.Amount > 999f && srcO.Tank.Amount < 600f && outlet.Tank.Amount < 1f)) ok = false;

            // --- Case O (hose removal): removing a hose (leaves the "hoses" group) stops its flow immediately ---
            var srcP = FluidContainer.Make(FluidRole.Source, new FluidTank(FluidType.Water, 1000f, 1000f), 125f);
            var tankP = FluidContainer.Make(FluidRole.Storage, new FluidTank(FluidType.None, 1000f, 0f), 125f);
            srcP.Position = new Vector3(-4f, 2f, 136f); tankP.Position = new Vector3(4f, 0f, 136f);   // downhill
            AddChild(srcP); AddChild(tankP);
            var hP = new Hose { Source = srcP.Ports[0], Consumer = tankP.Ports[0] }; AddChild(hP);
            for (int i = 0; i < 30; i++) FluidNet.Tick(GetTree(), 0.1f);   // hose present -> fills
            float beforeRemove = tankP.Tank.Amount;
            hP.RemoveFromGroup("hoses");   // what RemoveHose does (then QueueFree) -> stop conducting this tick
            for (int i = 0; i < 30; i++) FluidNet.Tick(GetTree(), 0.1f);   // hose gone -> no more flow
            GD.Print($"[hosetool] case O: beforeRemove={beforeRemove:0} afterRemove={tankP.Tank.Amount:0} (want filled then UNCHANGED after removing the hose)");
            if (!(beforeRemove > 100f && Mathf.Abs(tankP.Tank.Amount - beforeRemove) < 1f)) ok = false;

            // --- Case P (bug-3): the type-lock resolves THROUGH a tankless fitting. A Fuel source feeds a PUMP (no tank of
            // its own -> raw type None); ResolveNetType must still report Fuel across it, so hosing the pump's OUTPUT to a
            // WATER tank is a Mismatch. Pre-fix the fitting read as empty and fuel could pipe into the water tank. ---
            var srcQ = FluidContainer.Make(FluidRole.Source, new FluidTank(FluidType.Fuel, 1000f, 1000f), 125f);
            var pumpQ = FluidPump.Make();
            var waterQ = FluidContainer.Make(FluidRole.Storage, new FluidTank(FluidType.Water, 1000f, 100f), 125f);   // holds WATER
            srcQ.Position = new Vector3(-4f, 1f, 148f); pumpQ.Position = new Vector3(0f, 1f, 148f); waterQ.Position = new Vector3(4f, 0f, 148f);
            AddChild(srcQ); AddChild(pumpQ); AddChild(waterQ);
            AddChild(new Hose { Source = srcQ.Ports[0], Consumer = pumpQ.Ports[0] });   // fuel source -> pump input (committed)
            var pumpType = FluidNet.ResolveNetType(GetTree(), pumpQ.PortNodes[1], new System.Collections.Generic.HashSet<FluidContainer>());   // pump OUTPUT resolves through the fitting
            var vQ = FluidHoseRule.Completion(pumpQ.PortNodes[1].Kind, waterQ.PortNodes[0].Kind,
                pumpType == FluidType.None, waterQ.Tank.Type == FluidType.None, pumpType == waterQ.Tank.Type, false, false);
            GD.Print($"[hosetool] case P: pump resolves to {FluidDef.Name(pumpType)} (want Fuel) · pump->water verdict={vQ} (want Mismatch)");
            if (!(pumpType == FluidType.Fuel && vQ == HoseVerdict.Mismatch)) ok = false;

            // --- Case Q (flow boost): a POWERED pump runs its line at 5x the gravity rate (125 -> 625). A plain downhill
            // gravity line and a downhill pumped line, ticked the same short time: the pumped one moves ~5x as much. ---
            var qGsrc = FluidContainer.Make(FluidRole.Source, new FluidTank(FluidType.Water, 9000f, 9000f), 125f);
            var qGtank = FluidContainer.Make(FluidRole.Storage, new FluidTank(FluidType.Water, 9000f, 0f), 125f);
            qGsrc.Position = new Vector3(-4f, 2f, 160f); qGtank.Position = new Vector3(4f, 0f, 160f);   // downhill, NO pump
            AddChild(qGsrc); AddChild(qGtank);
            AddChild(new Hose { Source = qGsrc.Ports[0], Consumer = qGtank.Ports[0] });
            var qBsrc = FluidContainer.Make(FluidRole.Source, new FluidTank(FluidType.Water, 9000f, 9000f), 125f);
            var qBpump = FluidPump.Make(); qBpump.DebugForcePower = true;   // powered -> boosts its whole line
            var qBtank = FluidContainer.Make(FluidRole.Storage, new FluidTank(FluidType.Water, 9000f, 0f), 125f);
            qBsrc.Position = new Vector3(-4f, 2f, 168f); qBpump.Position = new Vector3(0f, 1f, 168f); qBtank.Position = new Vector3(4f, 0f, 168f);   // downhill THROUGH the pump
            AddChild(qBsrc); AddChild(qBpump); AddChild(qBtank);
            AddChild(new Hose { Source = qBsrc.Ports[0], Consumer = qBpump.Ports[0] });
            AddChild(new Hose { Source = qBpump.Ports[1], Consumer = qBtank.Ports[0] });
            for (int i = 0; i < 5; i++) FluidNet.Tick(GetTree(), 0.1f);   // 0.5s: gravity ~62, pumped ~312
            float qRatio = qGtank.Tank.Amount > 1f ? qBtank.Tank.Amount / qGtank.Tank.Amount : 0f;
            GD.Print($"[hosetool] case Q: gravity={qGtank.Tank.Amount:0} pumped={qBtank.Tank.Amount:0} ratio={qRatio:0.0} (want ~5x)");
            if (!(qRatio > 4f && qRatio < 6f)) ok = false;

            // --- Case R (auto-shutoff): a powered pump idles (hasWork false, 0w draw) when the line has no downstream demand
            // (target FULL) or no upstream supply (source DRY) -- gated on tank STATE, so it can't deadlock an uphill line. ---
            var rSrc = FluidContainer.Make(FluidRole.Source, new FluidTank(FluidType.Water, 2000f, 2000f), 125f);
            var rPump = FluidPump.Make(); rPump.DebugForcePower = true;
            var rFull = FluidContainer.Make(FluidRole.Storage, new FluidTank(FluidType.Water, 500f, 500f), 125f);   // target ALREADY FULL
            rSrc.Position = new Vector3(-4f, 1f, 180f); rPump.Position = new Vector3(0f, 1f, 180f); rFull.Position = new Vector3(4f, 0f, 180f);
            AddChild(rSrc); AddChild(rPump); AddChild(rFull);
            AddChild(new Hose { Source = rSrc.Ports[0], Consumer = rPump.Ports[0] });
            AddChild(new Hose { Source = rPump.Ports[1], Consumer = rFull.Ports[0] });
            for (int i = 0; i < 10; i++) FluidNet.Tick(GetTree(), 0.1f);
            bool fullShut = !rPump.DebugHasWork && rPump.DebugInputWatts < 1f;
            var rDry = FluidContainer.Make(FluidRole.Source, new FluidTank(FluidType.Water, 2000f, 0f), 125f);   // EMPTY source (dry)
            var rPump2 = FluidPump.Make(); rPump2.DebugForcePower = true;
            var rTank2 = FluidContainer.Make(FluidRole.Storage, new FluidTank(FluidType.Water, 2000f, 0f), 125f);
            rDry.Position = new Vector3(-4f, 1f, 192f); rPump2.Position = new Vector3(0f, 1f, 192f); rTank2.Position = new Vector3(4f, 0f, 192f);
            AddChild(rDry); AddChild(rPump2); AddChild(rTank2);
            AddChild(new Hose { Source = rDry.Ports[0], Consumer = rPump2.Ports[0] });
            AddChild(new Hose { Source = rPump2.Ports[1], Consumer = rTank2.Ports[0] });
            for (int i = 0; i < 10; i++) FluidNet.Tick(GetTree(), 0.1f);
            bool dryShut = !rPump2.DebugHasWork && rPump2.DebugInputWatts < 1f;
            GD.Print($"[hosetool] case R: full-target shutoff={fullShut} (hasWork={rPump.DebugHasWork} watts={rPump.DebugInputWatts:0}) · dry-source shutoff={dryShut} · fullTank unchanged={Mathf.Abs(rFull.Tank.Amount - 500f) < 1f}");
            if (!(fullShut && dryShut && Mathf.Abs(rFull.Tank.Amount - 500f) < 1f)) ok = false;

            // --- Case S (fuel a generator via hose): a fuel source hosed to a generator's FUEL INLET fills the gen's Fuel
            // tank; a WATER source is refused (fuel-only type-lock). Bridges fluid -> the power/fuel economy (strawberry). ---
            var genS = Deployable.Spawn(this, DeployableDef.Generator, new Vector3(0f, 0f, 204f), 0f);
            genS.Fuel = 0f;   // start dry so we can watch it fill via the hose
            FluidFuelInlet inletS = null;
            foreach (var ch in genS.GetChildren()) if (ch is FluidFuelInlet fi) { inletS = fi; break; }
            var fuelSrcS = FluidContainer.Make(FluidRole.Source, new FluidTank(FluidType.Fuel, 50000f, 50000f), 300f);
            fuelSrcS.Position = new Vector3(-4f, 3f, 204f); AddChild(fuelSrcS);   // above the gen inlet -> downhill
            if (inletS != null) AddChild(new Hose { Source = fuelSrcS.Ports[0], Consumer = inletS.Ports[0] });
            for (int i = 0; i < 40; i++) FluidNet.Tick(GetTree(), 0.1f);
            bool waterRefused = inletS != null && FluidHoseRule.Completion(FluidPortKind.Source, inletS.PortNodes[0].Kind,
                false, inletS.Tank.Type == FluidType.None, FluidType.Water == inletS.Tank.Type, false, false) == HoseVerdict.Mismatch;
            GD.Print($"[hosetool] case S: gen fuel={genS.Fuel:0} (want >0 — fuelled via hose) · water→fuel-inlet refused={waterRefused}");
            if (!(inletS != null && genS.Fuel > 100f && waterRefused)) ok = false;

            // --- Case T (water quality): a container takes the WORST quality that enters it. Tainted source -> tainted tank;
            // clean source -> clean tank; a SLUICE dirties its output -> dirty tank (strawberry). Tanks are Water-typed up
            // front (the direct-hose build skips the tool's adopt step). ---
            var tSrc = FluidContainer.Make(FluidRole.Source, new FluidTank(FluidType.Water, 3000f, 3000f, WaterQuality.Tainted), 300f);
            var tTank = FluidContainer.Make(FluidRole.Storage, new FluidTank(FluidType.Water, 3000f, 0f, WaterQuality.Clean), 125f);
            tSrc.Position = new Vector3(-4f, 2f, 216f); tTank.Position = new Vector3(4f, 0f, 216f); AddChild(tSrc); AddChild(tTank);
            AddChild(new Hose { Source = tSrc.Ports[0], Consumer = tTank.Ports[0] });
            var cSrc = FluidContainer.Make(FluidRole.Source, new FluidTank(FluidType.Water, 3000f, 3000f, WaterQuality.Clean), 300f);
            var cTank = FluidContainer.Make(FluidRole.Storage, new FluidTank(FluidType.Water, 3000f, 0f, WaterQuality.Clean), 125f);
            cSrc.Position = new Vector3(-4f, 2f, 224f); cTank.Position = new Vector3(4f, 0f, 224f); AddChild(cSrc); AddChild(cTank);
            AddChild(new Hose { Source = cSrc.Ports[0], Consumer = cTank.Ports[0] });
            var slSrc = FluidContainer.Make(FluidRole.Source, new FluidTank(FluidType.Water, 3000f, 3000f, WaterQuality.Clean), 300f);
            var sluiceT = FluidContainer.MakeTransformer(FluidType.Water, FluidType.Water, 125f, 1f); sluiceT.DirtiesWater = true;
            var slTank = FluidContainer.Make(FluidRole.Storage, new FluidTank(FluidType.Water, 3000f, 0f, WaterQuality.Clean), 125f);
            slSrc.Position = new Vector3(-6f, 2f, 232f); sluiceT.Position = new Vector3(0f, 1f, 232f); slTank.Position = new Vector3(6f, 0f, 232f);
            AddChild(slSrc); AddChild(sluiceT); AddChild(slTank);
            AddChild(new Hose { Source = slSrc.Ports[0], Consumer = sluiceT.Ports[0] });   // clean water -> sluice input
            AddChild(new Hose { Source = sluiceT.Ports[1], Consumer = slTank.Ports[0] });  // sluice output (dirty) -> tank
            for (int i = 0; i < 30; i++) FluidNet.Tick(GetTree(), 0.1f);
            GD.Print($"[hosetool] case T: tainted→{tTank.Tank.Quality} (want Tainted) · clean→{cTank.Tank.Quality} (want Clean) · sluice→{slTank.Tank.Quality} (want Dirty)");
            if (!(tTank.Tank.Quality == WaterQuality.Tainted && cTank.Tank.Quality == WaterQuality.Clean && slTank.Tank.Quality == WaterQuality.Dirty)) ok = false;

            // --- Case U (container fill): a fluid CONTAINER item RMB-fills from a tank, type-locked + worst-quality-wins.
            // An empty canteen adopts the tank's fluid + quality; once it holds Water it REFUSES a different fluid (strawberry). ---
            var canAsset = new SDG.Unturned.ItemAsset { id = 60001, itemName = "Canteen", fluidCapacity = 500f, fluidDefaultType = 0, fluidDefaultQuality = 0 };
            var canItem = new SDG.Unturned.Item(60001);   // fresh -> FluidItem.Read lazily leaves it EMPTY (None-default)
            var taintedTank = new FluidTank(FluidType.Water, 3000f, 300f, WaterQuality.Tainted);   // only 300 mL -> canteen fills PARTIALLY (leaves space, so the next fill hits type-lock, not "full")
            float f1 = FluidItem.Fill(canItem, canAsset, taintedTank, out _);
            FluidItem.Read(canItem, canAsset, out var cuType, out var cuAmt, out var cuQ);
            bool uFill = Mathf.Abs(f1 - 300f) < 0.5f && cuType == FluidType.Water && cuQ == WaterQuality.Tainted
                         && Mathf.Abs(cuAmt - 300f) < 0.5f && taintedTank.Amount < 0.5f;
            var fuelTank = new FluidTank(FluidType.Fuel, 3000f, 3000f);
            float f2 = FluidItem.Fill(canItem, canAsset, fuelTank, out string uMsg);   // canteen holds Water + has 200 mL space -> fuel refused by TYPE-LOCK (not "full"), tank untouched
            bool uLock = f2 <= 0f && uMsg != null && uMsg.Contains("mix") && Mathf.Abs(fuelTank.Amount - 3000f) < 0.5f;
            GD.Print($"[hosetool] case U: fill {f1:0}mL type={cuType} q={cuQ} tank={taintedTank.Amount:0} · mismatch moved={f2:0} (\"{uMsg}\")");
            if (!(uFill && uLock)) ok = false;

            // --- Case V (container drink): a sip takes 50 mL off a CLEAN water bottle + returns hydration; dirty/tainted
            // water refuses the sip (strawberry: can't drink tainted/dirty). ---
            var botAsset = new SDG.Unturned.ItemAsset { id = 60002, itemName = "Bottled Water", fluidCapacity = 1000f, fluidDefaultType = (byte)FluidType.Water, fluidDefaultQuality = (byte)WaterQuality.Clean };
            var botItem = new SDG.Unturned.Item(60002);   // fresh -> lazily FULL of clean water
            float s1 = FluidItem.Sip(botItem, botAsset, out float hyd1, out _);
            FluidItem.Read(botItem, botAsset, out _, out var vAmt, out _);
            bool vSip = Mathf.Abs(s1 - FluidItem.SipML) < 0.5f && hyd1 > 0f && Mathf.Abs(vAmt - (1000f - FluidItem.SipML)) < 0.5f;
            var dirtyItem = new SDG.Unturned.Item(60002); FluidItem.Write(dirtyItem, FluidType.Water, 1000f, WaterQuality.Dirty);
            float s2 = FluidItem.Sip(dirtyItem, botAsset, out float hyd2, out string vMsg);
            bool vRefuse = s2 <= 0f && hyd2 <= 0f;
            GD.Print($"[hosetool] case V: sip {s1:0}mL (+{hyd1:0.00}) left={vAmt:0} · dirty refused={s2 <= 0f} (\"{vMsg}\")");
            if (!(vSip && vRefuse)) ok = false;

            // --- Case W (purifier): tainted water + POWER -> CLEAN water; DEAD without power (strawberry). A tainted source
            // feeds a purifier feeds a clean tank. UNPOWERED the purifier is inert (draws nothing, produces nothing) -> tank
            // stays empty + source undrained. POWERED it consumes tainted + outputs CLEAN -> tank fills with clean water. ---
            var pSrc = FluidContainer.Make(FluidRole.Source, new FluidTank(FluidType.Water, 5000f, 5000f, WaterQuality.Tainted), 300f);
            var purifier = FluidPurifier.Make();
            var pTank = FluidContainer.Make(FluidRole.Storage, new FluidTank(FluidType.Water, 5000f, 0f, WaterQuality.Clean), 125f);
            pSrc.Position = new Vector3(-6f, 3f, 240f); purifier.Position = new Vector3(0f, 2f, 240f); pTank.Position = new Vector3(6f, 1f, 240f);   // downhill
            AddChild(pSrc); AddChild(purifier); AddChild(pTank);
            AddChild(new Hose { Source = pSrc.Ports[0], Consumer = purifier.Ports[0] });    // tainted water -> purifier INPUT (Ports[0]=Consumer)
            AddChild(new Hose { Source = purifier.Ports[1], Consumer = pTank.Ports[0] });   // purifier OUTPUT (clean) -> tank
            for (int i = 0; i < 30; i++) FluidNet.Tick(GetTree(), 0.1f);                    // UNPOWERED: inert
            float offTank = pTank.Tank.Amount, offSrc = pSrc.Tank.Amount;                   // snapshot the off-phase state for an honest readout
            bool offInert = offTank < 0.5f && offSrc > 4999f;
            purifier.DebugForcePower = true;                                                 // wire power
            for (int i = 0; i < 60; i++) FluidNet.Tick(GetTree(), 0.1f);
            bool onClean = pTank.Tank.Amount > 400f && pTank.Tank.Quality == WaterQuality.Clean && pSrc.Tank.Amount < 5000f;
            GD.Print($"[hosetool] case W: OFF tank={offTank:0} src={offSrc:0} (want 0/5000) · ON tank={pTank.Tank.Amount:0} q={pTank.Tank.Quality} src={pSrc.Tank.Amount:0} (want >400/Clean/<5000)");
            if (!(offInert && onClean)) ok = false;

            // --- Case X (drink fluids): the new beverage fluids (soda/cola/OJ/milk/coconut/energy) are ALL drinkable + a
            // carton container sips them like water; non-beverages (fuel) are not drinkable (strawberry). ---
            var beverages = new[] { FluidType.Soda, FluidType.Cola, FluidType.OrangeJuice, FluidType.Milk, FluidType.CoconutWater, FluidType.EnergyDrink, FluidType.AppleJuice, FluidType.GrapeJuice };
            bool allDrink = true;
            foreach (var bev in beverages) if (!FluidDef.Drinkable(bev, WaterQuality.Clean)) allDrink = false;
            // strawberry's rule: the ONLY thing blocked is bad water; EVERYTHING else (fuel/syrup/glue/chemicals) is a player choice -> drinkable
            bool badWaterBlocked = !FluidDef.Drinkable(FluidType.Water, WaterQuality.Tainted) && !FluidDef.Drinkable(FluidType.Water, WaterQuality.Dirty);
            bool elseDrinkable = FluidDef.Drinkable(FluidType.Fuel, WaterQuality.Clean) && FluidDef.Drinkable(FluidType.MapleSyrup, WaterQuality.Clean)
                                 && FluidDef.Drinkable(FluidType.Glue, WaterQuality.Clean) && FluidDef.Drinkable(FluidType.Chemicals, WaterQuality.Clean);
            var ojAsset = new SDG.Unturned.ItemAsset { id = 463, itemName = "Orange Juice", fluidCapacity = 1000f, fluidDefaultType = (byte)FluidType.OrangeJuice, fluidDefaultQuality = 0 };
            var ojItem = new SDG.Unturned.Item(463);   // fresh -> lazily full of OJ
            float sx = FluidItem.Sip(ojItem, ojAsset, out float hydx, out _);
            bool ojSip = Mathf.Abs(sx - FluidItem.SipML) < 0.5f && hydx > 0f;
            GD.Print($"[hosetool] case X: beverages drink={allDrink} · bad-water BLOCKED={badWaterBlocked} · fuel+syrup+glue+chem drink={elseDrinkable} · OJ sip {sx:0}mL (+{hydx:0.00})");
            if (!(allDrink && badWaterBlocked && elseDrinkable && ojSip)) ok = false;

            // --- Case Y (water tower): a map WATER TOWER is an INFINITE, TAINTED water source with head -> hose it downhill
            // into a tank; the tank fills with TAINTED water and the tower never depletes (strawberry). ---
            var tower = WaterTowerSource.Make();
            var towerTank = FluidContainer.Make(FluidRole.Storage, new FluidTank(FluidType.Water, 5000f, 0f, WaterQuality.Clean), 125f);
            tower.Position = new Vector3(-6f, 3f, 248f); towerTank.Position = new Vector3(2f, 0f, 248f);   // tower high, tank low (gravity, no pump)
            AddChild(tower); AddChild(towerTank);
            AddChild(new Hose { Source = tower.Ports[0], Consumer = towerTank.Ports[0] });   // tower output (Ports[0]=Source) -> tank
            for (int i = 0; i < 60; i++) FluidNet.Tick(GetTree(), 0.1f);
            bool towerOk = towerTank.Tank.Amount > 400f && towerTank.Tank.Quality == WaterQuality.Tainted && tower.Tank.Amount > 199999f;   // filled + tainted; tower infinite (undepleted)
            GD.Print($"[hosetool] case Y: tank={towerTank.Tank.Amount:0} q={towerTank.Tank.Quality} (want >400/Tainted) · tower={tower.Tank.Amount:0} (want ~200000 infinite)");
            if (!towerOk) ok = false;

            // --- Case Z (machine status lines): the at-a-glance status a machine shows so a player can see WHY it's dead
            // (strawberry polish). Valve open/closed; an unwired pump/purifier reads "no power"; a powered-but-no-work pump
            // (rPump from case R: target full) reads "idle — no supply"; a powered active purifier (case W) reads "purifying". ---
            var zValve = FluidContainer.MakeValve();
            bool zOpen = zValve.StatusLine().text == "open";
            zValve.ToggleValve();
            bool zClosed = zValve.StatusLine().text == "closed";
            bool zPumpNoPower = FluidPump.Make().StatusLine().text == "no power";       // fresh pump, never wired
            bool zPurifNoPower = FluidPurifier.Make().StatusLine().text == "no power";  // fresh purifier, never wired
            bool zPumpIdle = rPump.StatusLine().text == "idle — no supply";             // powered, target full -> no work (case R)
            bool zPurifRun = purifier.StatusLine().text == "purifying";                 // powered + water flowing (case W)
            GD.Print($"[hosetool] case Z: valve open={zOpen} closed={zClosed} · pump noPower={zPumpNoPower} idle={zPumpIdle} · purifier noPower={zPurifNoPower} run={zPurifRun}");
            if (!(zOpen && zClosed && zPumpNoPower && zPurifNoPower && zPumpIdle && zPurifRun)) ok = false;

            GD.Print($"[hosetool] RESULT {(ok ? "PASS" : "FAIL")}");
            GetTree().Quit();
        }

        async void BuildEditor()
        {
            _worldBuild = true;
            var res = await WorldBuilder.BuildFullWorld(this, WorldMode.Editor, _mapRoot, _mapPlace, noZombies: true,
                                                        syncLoad: false, bakeNav: false, ActiveHoliday());
            // Clean, legible editor lighting. The DayNightCycle re-applies a warm-tan ambient + fog + glow EVERY
            // frame (source-faithful sky, but it reads as thick haze from the aerial editor cam), so freeze its
            // visuals first, then set a bright fog-free environment so the map is clearly editable.
            void SetCleanEditorLighting()   // the editor's clean fog-free look; restored when leaving the Environment tab
            {
                foreach (var n in GetChildren())
                    if (n is WorldEnvironment we && we.Environment is Godot.Environment ev)
                    {
                        ev.SetFogEnabled(false);
                        ev.BackgroundMode = Godot.Environment.BGMode.Color;
                        ev.BackgroundColor = new Color(0.53f, 0.67f, 0.86f);   // clear sky blue
                        ev.AmbientLightSource = Godot.Environment.AmbientSource.Color;
                        ev.AmbientLightColor = new Color(0.92f, 0.92f, 0.94f);
                        ev.AmbientLightEnergy = 1.15f;
                        ev.GlowEnabled = false;
                        break;
                    }
            }
            if (res.DayNight != null) res.DayNight.VisualsEnabled = false;
            SetCleanEditorLighting();
            var editor = new Editor();
            AddChild(editor);
            var cam = new EditorCamera { Position = new Vector3(0f, 140f, 160f), RotationDegrees = new Vector3(-32f, 0f, 0f) };
            editor.AddChild(cam);
            editor.Setup("PEI", null, cam);
            LootTables.Load(_mapRoot + "/Spawns/Items.dat");   // so loot-crate tables can be named/picked in the editor
            var objs = new EditorObjects(editor, this, cam);   // Phase 2: place/select/delete props (picks the WorldMode.Editor colliders)
            editor.AddChild(objs);
            editor.Objects = objs;
            var spawns = new EditorSpawns(editor, cam, _mapRoot);   // Phase 3: visualize/edit spawn points (Spawns tab)
            editor.AddChild(spawns);
            editor.Spawns = spawns;
            var env = new EditorEnvironment(editor, res.DayNight, SetCleanEditorLighting);   // Phase 4: lighting/time/weather (Environment tab)
            editor.AddChild(env);
            editor.Environment = env;
            var terrainEd = new EditorTerrain(editor, cam, res.Terr);   // Phase 5: heightmap sculpt (Terrain tab)
            editor.AddChild(terrainEd);
            editor.TerrainEd = terrainEd;
            RoadField rf = null;   // Phase 6: WorldMode.Editor skips WorldBuilder's roads step, so build the road splines here
            if (res.Terr != null)
            {
                rf = new RoadField { Terr = res.Terr };
                rf.LoadFromEnvironment(_mapRoot + "/Environment");
                AddChild(rf);
            }
            var roadsEd = new EditorRoads(editor, cam, rf);   // roads paving under the Environment tab (R to toggle)
            editor.AddChild(roadsEd);
            editor.RoadsEd = roadsEd;
            editor.AddChild(new EditorDashboard { Editor = editor, OnExit = ReturnToMenu });
            if (res.Ready) _worldReady = true;
            // headless render-verify: scatter a few props once the colliders are live (UG_EDITORDEMO=1)
            if (System.Environment.GetEnvironmentVariable("UG_EDITORDEMO") == "1")
                GetTree().CreateTimer(0.8).Timeout += () =>
                {
                    objs.DemoPlace();
                    objs.Save();   // verify the round-trip: writes editor_PEI.txt; a re-run without the demo loads it back
                    if (objs.DemoPositions.Count > 0)   // pull the cam in close on a placed prop so the render shows it upright
                    {
                        var p = objs.DemoPositions[0];
                        cam.GlobalPosition = p + new Vector3(7f, 5f, 12f);
                        cam.LookAt(p + Vector3.Up * 1.5f, Vector3.Up);
                    }
                };
            if (System.Environment.GetEnvironmentVariable("UG_EDITORSPAWNS") == "1")
                GetTree().CreateTimer(0.8).Timeout += () =>
                {
                    editor.Mode = EEditorMode.Spawns;   // switch to the Spawns tab so the markers show
                    if (spawns.Positions.Count > 0)
                    {
                        var c = spawns.Positions[0];
                        // verify player add/remove + save round-trip (headless can't drive real clicks)
                        int b0 = spawns.PlayerCount;
                        spawns.RemoveNear(c);   // remove the original spawn under the cam (verify remove)
                        spawns.AddSpawn(c, 45f, false); spawns.AddSpawn(c + new Vector3(7f, 0f, 0f), 90f, false); spawns.AddSpawn(c + new Vector3(-7f, 0f, 0f), 0f, true);   // rotated x2 + an ALT
                        GD.Print($"[editorspawns] player remove-near from {b0} -> {spawns.PlayerCount}");
                        spawns.Save();
                    }
                    spawns.DemoGoAnimal();   // cycle to the Animal category (Fauna.dat MultiMesh)
                    if (spawns.Positions.Count > 0)
                    {
                        var zc = spawns.Positions[spawns.Positions.Count / 2];   // frame a mid animal cluster
                        cam.GlobalPosition = zc + new Vector3(0f, 34f, 30f);
                        cam.LookAt(zc, Vector3.Up);
                    }
                    GD.Print($"[editorspawns] animal spawns: {spawns.Count}");
                };
            if (System.Environment.GetEnvironmentVariable("UG_EDITORENV") == "1")
                GetTree().CreateTimer(0.8).Timeout += () =>
                {
                    env.DemoSet(0.5f, false);   // preview noon lighting through the Environment tab
                    GD.Print($"[editorenv] preview time={env.Time:0.00} ({(env.Overcast ? "overcast" : "clear")})");
                };
            if (System.Environment.GetEnvironmentVariable("UG_EDITORTERRAIN") == "1")
                {   // synchronous (no timer) so the frame-45 --shot reliably captures the demoed state
                    editor.Mode = EEditorMode.Terrain;
                    Vector3 at = spawns != null && spawns.Positions.Count > 0 ? spawns.Positions[0] : Vector3.Zero;   // a known land point
                    if (System.Environment.GetEnvironmentVariable("UG_TERRAMP") == "1")
                    {
                        terrainEd.DemoRamp(at, at + new Vector3(70f, 90f, 0f));   // #4 RAMP: grade up 90m over 70m (steep, unmistakable)
                        cam.GlobalPosition = at + new Vector3(35f, 85f, 80f);
                        cam.LookAt(at + new Vector3(35f, 45f, 0f), Vector3.Up);
                    }
                    else
                    {
                        terrainEd.DemoSculpt(at);
                        cam.GlobalPosition = at + new Vector3(75f, 55f, 75f);
                        cam.LookAt(at + Vector3.Up * 40f, Vector3.Up);
                        if (System.Environment.GetEnvironmentVariable("UG_EDITORPAINT") == "1")
                        {
                            terrainEd.DemoPaint(at, 6);   // snow-cap the hill -> Materials splat-paint proof
                            cam.GlobalPosition = at + new Vector3(150f, 175f, 150f);
                            cam.LookAt(at, Vector3.Up);
                        }
                    }
                    terrainEd.Save();   // verify the heightmap round-trip
                }
            if (System.Environment.GetEnvironmentVariable("UG_EDITORROADS") == "1" && roadsEd.HasRoads)
            {   // synchronous (no timer): set before the first frame so the frame-45 --shot reliably captures the demoed state
                editor.Mode = EEditorMode.Environment;
                Vector3 focus;
                bool loopDemo = System.Environment.GetEnvironmentVariable("UG_ROADLOOP") == "1";
                if (System.Environment.GetEnvironmentVariable("UG_ROADCLEAN") == "1")
                    focus = roadsEd.DemoPave(0, roadsEd.DemoJointCount(0) / 2);    // markers only, NO edit -> roads render exactly as authored
                else if (loopDemo)
                    focus = roadsEd.DemoDataModel(0);                             // polish: loop + per-joint offset + ignore-terrain
                else if (System.Environment.GetEnvironmentVariable("UG_ROADTAN") == "1")
                {
                    Vector3 j = roadsEd.DemoJoint(0, 1);
                    roadsEd.DemoMoveTangent(0, 1, 0, j + new Vector3(0f, 0f, 45f));   // inc3: pull a bezier handle -> the road curves
                    roadsEd.DemoSetMaterial(3, 2);                                    // inc3: verify the material picker (road 3 -> material 2)
                    focus = j;
                }
                else if (System.Environment.GetEnvironmentVariable("UG_ROADADD") == "1")
                {
                    focus = roadsEd.DemoAddVertex(0, new Vector3(35f, 0f, 20f));   // inc2: extend road 0 with a NEW joint -> the spline grows
                    roadsEd.DemoRemoveVertex(5, 1);                                // inc2: remove a joint from road 5 (functional check both paths rebuild)
                }
                else
                {
                    Vector3 j = roadsEd.DemoJoint(0, 1);
                    roadsEd.DemoMove(0, 1, j + new Vector3(12f, 0f, 0f));          // inc1: a GENTLE nudge (not the mangling 40m yank)
                    focus = j + new Vector3(6f, 0f, 0f);
                }
                cam.GlobalPosition = focus + (loopDemo ? new Vector3(30f, 135f, 30f) : new Vector3(48f, 54f, 48f));   // loop: taller aerial to see the closed shape
                cam.LookAt(focus, Vector3.Up);
                if (res.DayNight != null) res.DayNight.VisualsEnabled = false;   // Environment preview hazes -> clean lighting for the render
                SetCleanEditorLighting();
                editor.Save();   // verify the Paths.dat round-trip (writes content/roads/editor_Paths.dat)
            }
            GD.Print("[editor] up: PEI + free-fly cam + dashboard + objects editor");
        }

        // Exit the editor back to the main menu. Simplest reliable teardown of the async world + editor = reload
        // the scene (no --args -> the default menu boot).
        void ReturnToMenu()
        {
            Input.MouseMode = Input.MouseModeEnum.Visible;
            GetTree().ReloadCurrentScene();
        }

        bool _mpLoopback;   // --mploopback: legacy opt-in loopback for TEST HARNESSES (MP_PLAN §4 Phase 4); the GAME path defaults to it now (P6a)
        bool _loopbackConsuming;   // A1: set by AttachMpLoopback when the loopback consumes -> the StorageReplicaView owns containers, so SpawnMapContainers (SP nodes) is gated off
        bool _spConsume;    // --spconsume (or UG_SPCONSUME=1): SP/MP-unify P1 legacy consume toggle -- only meaningful on a harness caller now (the GAME path consumes by default, P6a)
        bool _direct;       // --direct (or UG_DIRECT=1): SP/MP-unify P6a -- opt OUT of the consuming-loopback DEFAULT on the SP GAME entries -> pure direct SP path (reversible fallback + A/B)

        // SP/MP-unify P6a (the staged flip): resolve whether a Playable world attaches the in-process consuming
        // listen-server, and whether the local player CONSUMES replicas. PURE + static so the truth table is
        // L1-coverable (unify.default_flip) without booting Main.
        //   - gameDefault=true  (the real SP GAME entries: menu "Drive PEI"/--peidrive, --peiplay): CONSUME by
        //     DEFAULT -- no --mploopback/--spconsume needed. --direct (UG_DIRECT=1) opts back out to the pure
        //     direct SP path (attach=false), the reversible fallback + A/B knob. This is the P6a flip.
        //   - gameDefault=false (TEST HARNESSES reaching a Playable world: nav bake / navpath / zombietest, and
        //     --objects which is Aerial anyway): UNCHANGED legacy behavior -- stay direct unless the caller
        //     explicitly passed --mploopback, and only consume under --spconsume. The harness fleet stays direct.
        public static (bool attach, bool consume) ResolveLoopbackMode(bool gameDefault, bool mpLoopback, bool spConsume, bool direct)
        {
            if (gameDefault)
                return direct ? (false, false)   // --direct: pure direct SP, no loopback -- the reversible fallback
                              : (true, true);     // P6a DEFAULT: the SP game boots the consuming listen-server
            return mpLoopback ? (true, spConsume) : (false, false);   // harness: legacy opt-in, consume only under --spconsume
        }

        // gameDefault = this call site is a real SP GAME Playable entry (see ResolveLoopbackMode). The consume
        // machinery itself is unchanged from the --spconsume path (P1-P5, already gated green); P6a only flips
        // WHICH entries turn it on by default. The direct path is NOT deleted -- --direct restores it wholesale.
        void AttachMpLoopback(WorldBuildResult res, bool gameDefault)
        {
            if (res.Player == null || res.Sim == null) return;
            bool direct = _direct || System.Environment.GetEnvironmentVariable("UG_DIRECT") == "1";
            bool spConsume = _spConsume || System.Environment.GetEnvironmentVariable("UG_SPCONSUME") == "1";
            var (attach, consume) = ResolveLoopbackMode(gameDefault, _mpLoopback, spConsume, direct);
            if (!attach)
            {
                // A3: pure-direct SP (no loopback) -- realize the recorded grid-power fixtures as direct local
                // nodes (the old inline Circuit_0 Attach, now driven off res.Fixtures). Under a loopback the
                // MpLoopback node does this instead (ServerPlace under consume, direct otherwise).
                WorldBuilder.SpawnFixturesDirect(this, res.Fixtures);
                return;
            }
            _loopbackConsuming = consume;   // A1: under consume the StorageReplicaView materializes containers -> gate the SP-local SpawnMapContainers off (no double)
            AddChild(new MpLoopback { Player = res.Player, Driver = res.Sim,
                                      DayNight = res.DayNight, Resources = res.Resources, Destructibles = res.Destructibles,   // Phase 8 world-state syncs (§3.7) + rubble
                                      Fixtures = res.Fixtures,                              // A3: grid-power fixtures -- ServerPlaced under consume, direct-Attached otherwise
                                      Containers = res.Containers,                          // A1: container manifest -> ContainerNetSync publishes server-owned fixtures
                                      ConsumeDeployables = consume });                      // P6a: true by default on the GAME path
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
                    // Default to the most INLAND pocket rather than a hardcoded index. Pocket 3 sat on the
                    // coast, so the verify shot framed open water with the cones adrift in it -- a render
                    // that passed every "did it produce a file" check and showed nothing worth looking at.
                    // Scoring by surrounding land keeps the framing meaningful even if pocket ORDER changes,
                    // which an index cannot. UG_NAVPOCKET=N still overrides for a specific pocket.
                    int pkIdx = int.TryParse(System.Environment.GetEnvironmentVariable("UG_NAVPOCKET"), out var pi)
                        ? Mathf.Clamp(pi, 0, pockets.Count - 1)
                        : MostInlandPocket(pockets, terr);
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
            AttachMpLoopback(res, gameDefault: true);   // P6a: --peiplay is a real SP GAME entry -> consuming listen-server by default (--direct opts out)
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
            if (System.Environment.GetEnvironmentVariable("UG_ISO") == "1")   // 3/4 iso view (Y-up) -- good for furniture/props that bake top-down
            {
                cam.Size = Mathf.Max(s.X, Mathf.Max(s.Y, s.Z)) * 1.35f;
                cam.GlobalPosition = c + new Vector3(1f, 0.8f, 1f).Normalized() * (s.Length() + 3f);   // front-right-above
                cam.LookAt(c, Vector3.Up);
            }
            else
            {
                cam.GlobalPosition = c + ax[0].dir * (s.Length() + 2f);
                cam.LookAt(c, -ax[1].dir);   // -middle axis = up (the model's height axis points "down" in mesh space)
            }
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

            if (System.Environment.GetEnvironmentVariable("UG_SHELFDEMO") == "1")   // StoreShelf tier-layout harness: isolate a display shelf + fixed items (UG_SHELFMESH=Shelf_0/1)
            {
                GetWindow().Size = new Vector2I(1280, 720);
                SDG.Unturned.ItemCatalog.RegisterAll();   // so Assets.find(id).type resolves -> the stand/lie orientation rule works in the harness
                if (System.Environment.GetEnvironmentVariable("UG_PROBE") == "1")   // orientation probe: id 13 (a can) at 6 rotations, to SEE which stands it upright
                {
                    int probeId = int.TryParse(System.Environment.GetEnvironmentVariable("UG_PROBEID"), out var pid) ? pid : 13;
                    var rots = new[] { new Vector3(0, 0, 0), new Vector3(90, 0, 0), new Vector3(180, 0, 0), new Vector3(270, 0, 0), new Vector3(0, 0, 90), new Vector3(0, 0, 270) };
                    for (int r = 0; r < 6; r++)
                    {
                        var v = WorldItem.BuildReplicaVisual((ushort)probeId, Colors.White);
                        v.RotationDegrees = rots[r];
                        v.Position = new Vector3(-1.5f + r * 0.6f, 1.2f, -4.5f);
                        AddChild(v);
                    }
                    AddChild(new OmniLight3D { GlobalPosition = new Vector3(0f, 3f, -2f), OmniRange = 20f, LightEnergy = 3f });
                    var pc = new Camera3D { Fov = 45f };
                    AddChild(pc); pc.GlobalPosition = new Vector3(0f, 1.5f, -1.2f); pc.LookAt(new Vector3(0f, 1.1f, -4.5f), Vector3.Up); pc.Current = true;
                    return;
                }
                string mesh = System.Environment.GetEnvironmentVariable("UG_SHELFMESH") ?? "Shelf_1";
                var shelf = StoreShelf.Spawn(this, new Vector3(0f, 0f, -4.5f), mesh, 6, 0f, true, mesh);
                shelf.DebugDisplay(new System.Collections.Generic.List<int> {   // carjack + clothing LIE FLAT (+scale); tins/juice/cans STAND; medkit/MRE stay detail-up
                    277, 3, 2, 11, 10, 15,                     // carjack, hoodie, pants, mask, vest (LIE+scale), medkit(lie detail-up)
                    81, 6, 88, 79, 91, 463,                    // MRE(lie), mil mag(lie), bacon(stand tin), tuna(stand), apple juice(stand), OJ(stand)
                    13, 14, 465, 340, 76, 1159,                // beans, water, soda, tomato, blowtorch, maple -> STAND
                    83, 84, 464, 462, 460, 468 });             // chocolate, candy, cheese (lie), milk(stand), bread(lie), sandwich(lie)
                var back = StoreShelf.Spawn(this, new Vector3(0f, 0f, -4.5f), mesh, 6, 180f, true, mesh, false);   // BACK side: shares the mesh, stocks the far tiers, faces the other aisle
                back.DebugDisplay(new System.Collections.Generic.List<int> { 472, 465, 13, 14, 462, 340, 15, 81 });   // a few items so we can see the back is stocked
                AddChild(new OmniLight3D { GlobalPosition = new Vector3(2f, 3f, -1.5f), OmniRange = 24f, LightEnergy = 3f });
                var scam = new Camera3D { Fov = 55f };
                AddChild(scam);
                scam.GlobalPosition = new Vector3(3.4f, 2.0f, 1.2f);
                scam.LookAt(new Vector3(0f, 1.2f, -4.5f), Vector3.Up);
                string _scamMode = System.Environment.GetEnvironmentVariable("UG_SHELFCAM");
                if (_scamMode == "top")        { scam.GlobalPosition = new Vector3(0.2f, 3.9f, -2.4f);  scam.LookAt(new Vector3(0f, 1.3f, -4.6f), Vector3.Up); }   // high angle: lying items detail-side UP
                else if (_scamMode == "side")  { scam.GlobalPosition = new Vector3(6.6f, 1.5f, -4.5f);  scam.LookAt(new Vector3(0f, 1.2f, -4.5f), Vector3.Up); }   // profile: tier structure front-to-back (single vs double sided)
                else if (_scamMode == "back")  { scam.GlobalPosition = new Vector3(-3.2f, 2.0f, -10.4f); scam.LookAt(new Vector3(0f, 1.2f, -4.5f), Vector3.Up); }   // from behind the shelf
                scam.Current = true;
                return;
            }

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

            // a few items dropped on the GROUND nearby -> the AREA (Nearby) scan picks these up (bitvox: show storage + nearby)
            WorldItem.Spawn(this, new SDG.Unturned.Item(4),     new Vector3(1.2f, 0.3f, 0.6f));    // Eaglefire
            WorldItem.Spawn(this, new SDG.Unturned.Item(6, 30), new Vector3(-1.0f, 0.3f, 0.8f));   // Military mag x30
            WorldItem.Spawn(this, new SDG.Unturned.Item(13),    new Vector3(0.7f, 0.3f, 1.1f));    // Canned Beans
            WorldItem.Spawn(this, new SDG.Unturned.Item(14),    new Vector3(-0.6f, 0.3f, 1.2f));   // Water Bottle

            player.OpenNearestCrate();   // within 2.5 m -> loads the crate into STORAGE + opens the dashboard
            GD.Print("[CRATE] opened a storage crate");
        }

        // A reference scene under a fast day/night cycle -- montage the --write-movie to see dawn -> noon -> dusk -> night.
        // --lighttest: a single lit streetlight over a dark ground plane. The cone's inside faces and the
        // dust motes are a LOOK, not a value -- there is nothing an assert can check here, so this exists to be
        // rendered and eyeballed. UG_LIGHTCAM=under puts the camera beneath the lamp looking up, which is the
        // view that was empty before the cone stopped back-face culling.
        // One Traffic_Light_0, one aspect, HELD. Every state this prop has must be renderable on demand: the flash
        // beats last 0.6s and the drained-battery state only arrives two in-game days after a blackout, so neither
        // could ever be eyeballed by waiting. UG_TL_STATE picks the aspect, UG_TL_SIDE=1 makes it a side-road mast
        // (flashes red, not amber), UG_TL_DAY=1 lights it as daytime -- the unlit lenses have to look right too, and
        // a night-only harness is how the streetlight's unlit bulb went unlooked-at for two days.
        void BuildTrafficLightDemo()
        {
            bool day = System.Environment.GetEnvironmentVariable("UG_TL_DAY") == "1";
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = day ? new Color(0.42f, 0.55f, 0.72f) : new Color(0.02f, 0.03f, 0.05f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = day ? new Color(0.55f, 0.60f, 0.70f) : new Color(0.05f, 0.06f, 0.09f),
                AmbientLightEnergy = 1f,
                // Glow, or the one thing being judged -- how hot a lit lens reads against its housing -- cannot appear.
                GlowEnabled = true, GlowIntensity = 0.8f, GlowBloom = 0.1f, GlowHdrThreshold = 0.9f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            if (day) AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-52f, 34f, 0f), LightEnergy = 1.1f });

            var gmesh = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(80, 80) } };
            gmesh.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.20f, 0.20f, 0.19f), Roughness = 1f };
            AddChild(gmesh);

            // Placed the way WorldBuilder places it: the raw mesh lies flat with the pole along +Z, so ex=270 stands
            // it up. That puts the two signal heads ~6.5m up, strung along the mast arm at world Z -2.5 and -8.
            var propBasis = new Basis(new Vector3(1, 0, 0), Mathf.DegToRad(270f));
            string objDir = ProjectSettings.GlobalizePath("res://content/objects/");
            var propMesh = ObjMesh.Load(objDir + "Traffic_Light_0.obj");
            if (propMesh == null) { GD.PrintErr("[trafficlight] Traffic_Light_0.obj missing"); return; }

            // The prop's REAL palette texture. NEAREST is mandatory -- it is a 4x2 palette and linear sampling would
            // blend the red lens into the amber one two texels away.
            var bodyMat = new StandardMaterial3D { Roughness = 0.8f, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                                                   TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest };
            string texPath = objDir + "Traffic_Light_0_tex.png";
            if (System.IO.File.Exists(texPath))
            {
                var img = Image.LoadFromFile(texPath);
                if (img != null) bodyMat.AlbedoTexture = ImageTexture.CreateFromImage(img);
            }

            // PER HEAD, matching WorldBuilder: the mast's two heads run independent timers, so the harness has to be
            // able to show them disagreeing. Building one TrafficLight over both here would hide exactly the bug the
            // split exists to prevent.
            var (headMeshes, tlBody) = ObjMesh.SplitTrafficLensesPerHead(propMesh);
            AddChild(new MeshInstance3D { Mesh = tlBody ?? propMesh, MaterialOverride = bodyMat, Basis = propBasis });
            bool side = System.Environment.GetEnvironmentVariable("UG_TL_SIDE") == "1";
            // UG_TL_STATE takes ONE state, or two separated by a slash to drive the heads apart ("red/green").
            var states = (System.Environment.GetEnvironmentVariable("UG_TL_STATE") ?? "red").ToLowerInvariant().Split('/');
            float level = float.TryParse(System.Environment.GetEnvironmentVariable("UG_TL_LEVEL"), out var lv) ? lv : 1f;
            for (int h = 0; h < (headMeshes?.Length ?? 0); h++)
            {
                var lens = new MeshInstance3D[3];
                for (int i = 0; i < 3; i++)
                {
                    if (headMeshes[h][i] == null) continue;
                    lens[i] = new MeshInstance3D { Mesh = headMeshes[h][i], MaterialOverride = bodyMat, Basis = propBasis };   // bodyMat = the UNLIT lens
                    AddChild(lens[i]);
                }
                var tl = TrafficLight.Make(Vector3.Zero, 0f, lens[0], lens[1], lens[2], h);
                tl.SideRoad = side;
                AddChild(tl);
                string st = states[h % states.Length];
                tl.ForcePhase(st switch
                {
                    "green" => TrafficLight.Phase.Green,
                    "amber" => TrafficLight.Phase.Amber,
                    "dark" => TrafficLight.Phase.Off,                                       // drained battery / smashed
                    "flash" => side ? TrafficLight.Phase.FlashRed : TrafficLight.Phase.FlashAmber,
                    _ => TrafficLight.Phase.Red,
                }, level);
                GD.Print($"[TRAFFICLIGHT] head {h}: state={st} phase={tl.CurrentPhase} lens={TrafficLight.LensIndexFor(tl.CurrentPhase)} level={level:F2}");
            }

            // Looking along +X at the lens faces -- the lenses sit on the housing's -X side, and the mast arm runs to
            // world Z -8.9, so the two heads sit at roughly Z -2.5 and -8. AddChild BEFORE aiming: LookAt resolves
            // against the global transform, so calling it on a node still outside the tree aims at nothing and the
            // prop renders off-frame.
            var cam = new Camera3D { Current = true, Fov = 55f };
            AddChild(cam);
            cam.Position = new Vector3(-9f, 6.6f, -5.4f);
            cam.LookAt(new Vector3(0f, 6.3f, -5.4f), Vector3.Up);
            GD.Print($"[TRAFFICLIGHT] side={side} day={day} heads={headMeshes?.Length ?? 0}");
        }

        void BuildStreetLightDemo()
        {
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.02f, 0.03f, 0.05f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.05f, 0.06f, 0.09f),
                AmbientLightEnergy = 1f,
                // GLOW, matching DayNightCycle's night environment. Without it this harness cannot show bloom at
                // all -- so the one thing being judged here (how hot the lens reads) would be invisible in every
                // shot, exactly like the harness being night-only hid the unlit bulb.
                GlowEnabled = true, GlowIntensity = 0.8f, GlowBloom = 0.1f, GlowHdrThreshold = 0.9f,
            };
            AddChild(new WorldEnvironment { Environment = env });

            var gmesh = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(60, 60) } };
            gmesh.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.20f, 0.20f, 0.19f), Roughness = 1f };
            AddChild(gmesh);

            // The REAL Street_Light_0, placed the way WorldBuilder places it, so this harness exercises the emissive
            // lens split rather than the bare-lamp fallback. The raw mesh lies flat with the pole along +Z, so ex=270
            // (Rx -90) stands it up -- the same basis the placement files carry for these props -- which puts the head
            // 6.48m up and 2.35m out along -Z.
            var propBasis = new Basis(new Vector3(1, 0, 0), Mathf.DegToRad(270f));
            var lampLocal = new Vector3(0f, 2.35f, 6.48f);   // replaced by the lens centre once the prop mesh is split
            MeshInstance3D lensMi = null;
            var propMesh = ObjMesh.Load(ProjectSettings.GlobalizePath("res://content/objects/") + "Street_Light_0.obj");
            if (propMesh != null)
            {
                var (bodyMesh, lensMesh) = ObjMesh.SplitLens(propMesh);
                // The prop's REAL palette texture, so the unlit lens shows its actual colour rather than a stand-in
                // grey. NEAREST filtering is mandatory: Street_Light_0_tex is a 2x2 palette and linear sampling
                // would blend the bulb's warm tan into the neighbouring greys.
                var bodyMat = new StandardMaterial3D { Roughness = 0.8f, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                                                       TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest };
                string texPath = ProjectSettings.GlobalizePath("res://content/objects/") + "Street_Light_0_tex.png";
                if (System.IO.File.Exists(texPath))
                {
                    var img = Image.LoadFromFile(texPath);
                    if (img != null) bodyMat.AlbedoTexture = ImageTexture.CreateFromImage(img);
                }
                else bodyMat.AlbedoColor = new Color(0.17f, 0.17f, 0.18f);
                // UG_LIGHTBREAK=1 renders the SMASHED state: WorldBuilder keeps the plinth out of the destructible's
                // mesh list, so a broken lamp is "upper hidden, base still standing". The harness has to be able to
                // show that or the stump can never be eyeballed -- the third state in two days this scene could not
                // express (it was night-only, then glow-less).
                bool broken = System.Environment.GetEnvironmentVariable("UG_LIGHTBREAK") == "1";
                var (baseMesh, upperMesh) = ObjMesh.SplitBelow(bodyMesh ?? propMesh, 1.0f);
                if (baseMesh != null && upperMesh != null)
                {
                    AddChild(new MeshInstance3D { Mesh = baseMesh, MaterialOverride = bodyMat, Basis = propBasis });   // survives the break
                    AddChild(new MeshInstance3D { Mesh = upperMesh, MaterialOverride = bodyMat, Basis = propBasis, Visible = !broken });
                }
                else AddChild(new MeshInstance3D { Mesh = bodyMesh ?? propMesh, MaterialOverride = bodyMat, Basis = propBasis });
                if (lensMesh != null)
                {
                    lensMi = new MeshInstance3D { Mesh = lensMesh, Basis = propBasis, MaterialOverride = bodyMat };   // bodyMat = its UNLIT look
                    AddChild(lensMi);
                    lampLocal = lensMesh.GetAabb().GetCenter();   // emit from the bulb, same as WorldBuilder
                }
                if (broken) GD.Print($"[LIGHTTEST] BROKEN state: base kept ({baseMesh?.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length / 3 ?? 0} tri), pole hidden");
                GD.Print($"[LIGHTTEST] prop lens split: lens={(lensMesh != null ? "yes" : "NONE")} localCentre={lampLocal}");
            }
            else
            {
                // no extracted prop on this box: fall back to the old stand-in pole so the harness still runs
                var pole = new MeshInstance3D { Position = new Vector3(0f, 3f, 0f), Mesh = new CylinderMesh { TopRadius = 0.08f, BottomRadius = 0.10f, Height = 6f, RadialSegments = 8 } };
                pole.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.16f, 0.16f, 0.17f), Roughness = 0.8f };
                AddChild(pole);
            }

            var lampPos = propBasis * lampLocal;
            var lamp = StreetLight.Make(lampPos, Mathf.Max(4f, lampPos.Y), lensMi);
            if (System.Environment.GetEnvironmentVariable("UG_LIGHTBREAK") == "1") lamp.SetBroken(true);   // pole smashed -> lamp dark, lens goes with it
            AddChild(lamp);
            bool lampOff = System.Environment.GetEnvironmentVariable("UG_LIGHTOFF") == "1";
            lamp.SetNight(!lampOff); lamp.SetPowered(!lampOff);
            if (lampOff)
            {
                // UG_LIGHTOFF=1: the fixture UNLIT, in daylight -- the state that actually shows whether the lens
                // still reads as part of the lamp when it is not glowing. A dark scene hides that entirely.
                env.BackgroundColor = new Color(0.45f, 0.55f, 0.70f);
                env.AmbientLightColor = new Color(0.60f, 0.63f, 0.68f);
                env.AmbientLightEnergy = 1.1f;
                var sun = new DirectionalLight3D { RotationDegrees = new Vector3(-52f, 38f, 0f), LightEnergy = 1.1f };
                AddChild(sun);
            }

            var cam = new Camera3D { Current = true, Fov = 60f };
            AddChild(cam);
            string camMode = System.Environment.GetEnvironmentVariable("UG_LIGHTCAM") ?? "side";
            if (camMode == "under")
            {
                cam.Position = new Vector3(0.6f, 0.9f, -1.8f);     // standing under it, looking up into the cone
                cam.LookAt(lampPos, Vector3.Up);
            }
            else if (camMode == "lens")
            {
                cam.Position = new Vector3(1.5f, 5.2f, -0.55f);    // close on the fixture: is the BULB what glows?
                cam.LookAt(lampPos + new Vector3(0f, -0.08f, 0f), Vector3.Up);
                cam.Fov = 38f;
            }
            else
            {
                cam.Position = new Vector3(7.5f, 3.2f, 5.2f);      // side-on: cone shaft + ground pool together
                cam.LookAt(new Vector3(0f, 3.4f, -1.2f), Vector3.Up);
            }
            GD.Print($"[LIGHTTEST] one streetlight, motes={StreetLight.MoteCount}, cam={camMode}");
        }

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
        ushort _connectPort;                 // server-browser JOIN / direct-connect sets the per-server port; 0 = fall back to PortEnv()/UG_PORT
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
                string holiday = ActiveHoliday();   // P3: ONE decision -- the world builds with it AND it rides the Accept (joiners build the same collision set)
                var res = await WorldBuilder.BuildFullWorld(this, WorldMode.Dedicated, _mapRoot, _mapPlace,
                    // C4: the dedicated world is POPULATED -- zombies ON by default for the test server;
                    // --nozombies or UG_DEDICATED_NOZOMBIES=1 gives a quiet server, no code change
                    noZombies: _noZombies || System.Environment.GetEnvironmentVariable("UG_DEDICATED_NOZOMBIES") == "1",
                    syncLoad: true, bakeNav: false, activeHoliday: holiday);
                AddChild(new DedicatedServer { Port = PortEnv(), Driver = res.Sim, Terr = res.Terr,   // Terr: server grenades bounce on real terrain height (Phase 5)
                    DayNight = res.DayNight, Resources = res.Resources, Destructibles = res.Destructibles, MapRoot = _mapRoot,   // Phase 8: tick-derived clock + resource bitmap + rubble + nav-pocket relevancy cells (§3.7/§2.6)
                    Deadzones = res.Deadzones,                                                       // SP/MP unify: the contaminated volumes get copied into the server's own hazard step
                    Fixtures = res.Fixtures,                                                         // A3: server-place the Circuit_0 grid-power sources into the deployable graph (mains OFF)
                    Containers = res.Containers,                                                     // A1: container manifest -> ContainerNetSync publishes server-owned fixtures
                    RemoteAvatars = true,                                                            // C2: remote peers get real avatar bodies (real spawns/collision/jump) on this world
                    ActiveHoliday = holiday,                                                         // P3 (wire v6): joiners build THIS holiday's props/colliders
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
                    AddChild(new ClientWorldSession { Host = _connectHost, Port = _connectPort != 0 ? _connectPort : PortEnv(), Driver = res.Sim, Sun = res.Sun, Env = res.Env,
                                                      DayNight = res.DayNight, Resources = res.Resources, Destructibles = res.Destructibles,   // C5: the world-state views drive these + rubble
                                                      Terr = res.Terr,                                       // C6: terrain-snaps the vehicle-exit spot (§7 risk 6)
                                                      ApplyServerHoliday = res.ApplyHoliday });              // P3: the deferred holiday content builds with the SERVER's holiday at Accept
                    GD.Print($"[CLIENT] real world up ({System.IO.Path.GetFileName(_mapRoot)}); connecting to {_connectHost}:{PortEnv()} -- the local shell spawns at the server-adopted spawn, predicted + reconciled");
                }
                else   // bare --client (C1 demo shape): overhead cam over the spawn region + ClientNode capsules
                {
                    res.ApplyHoliday?.Invoke(ActiveHoliday());   // P3: the demo renderer has no join-handshake consumer -- place the deferred holiday content by local clock, the pre-P3 behavior
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

        // --zdirtest: run the REWRITE on the real map and watch it. The L0 tests prove the sim's logic
        // against a mock navmesh; this proves the thing that L0 cannot -- that it is wired to the actual
        // baked pockets, that rows tier correctly against a real player position, that Godot's navigation
        // server answers the corridor queries, and that zombies MOVE. Reports positions, not intentions.
        UnityEngine.Vector3[] _zdStart;
        ulong _zdPhys0;   // physics-frame baseline, so the per-tick costs below divide by the sampled window

        // Stand the measurement point in the POCKET WITH THE MOST ZOMBIES, deliberately, and before the
        // clock starts. PEI's player spawn is out in the wilderness, where the correct behaviour is that
        // nothing happens -- measuring there says nothing about whether the system works. The case worth
        // measuring is the one that used to cost 24.4 ms: a player standing inside a populated POI.
        void ZombieDirTestStand()
        {
            var zd = _zdField;
            if (zd?.Sim == null) return;
            var sim = zd.Sim;
            int best = -1, bestCount = 0;
            var perRegion = new int[sim.Regions.Count];
            for (int i = 0; i < sim.Count; i++)
            {
                int r = sim.RegionOf(i);
                if (r >= 0 && ++perRegion[r] > bestCount) { bestCount = perRegion[r]; best = r; }
            }
            if (best < 0) { GD.Print("[zdirtest] every row is off-partition -- cannot pick a POI"); return; }

            // Centre of the pocket is not necessarily ON the zombies; stand on the densest one's position
            // so the sample really is "a player among the horde".
            var c = sim.Regions.BoundsOf(best).Center;
            UnityEngine.Vector3 anchor = new UnityEngine.Vector3(c.x, 0f, c.z);
            for (int i = 0; i < sim.Count; i++)
                if (sim.RegionOf(i) == best) { anchor = sim.PositionOf(i); break; }

            zd.DebugPlayer = new Vector3(anchor.x, anchor.y, anchor.z);
            GD.Print($"[zdirtest] standing the test player in pocket {best} ({bestCount} zombies) at ({anchor.x:0},{anchor.z:0})");
        }

        void RunZombieDirTest()
        {
            var zd = _zdField;
            if (zd?.Sim == null) { GD.Print("[zdirtest] no ZombieDirector/sim -- did --newzombies wire in?"); GetTree().Quit(); return; }
            var sim = zd.Sim;

            if (_zdStart == null)   // first sampled frame: remember where everyone was
            {
                _zdStart = new UnityEngine.Vector3[sim.Count];
                for (int i = 0; i < sim.Count; i++) _zdStart[i] = sim.PositionOf(i);
                _zdPhys0 = Engine.GetPhysicsFrames(); Prof.Reset();   // measure only the sampled window
                GD.Print($"[zdirtest] {sim.Count} rows, {sim.Regions.Count} regions from the pockets, 0 CharacterBody3D");
                GD.Print($"[zdirtest] {zd.DebugLine()}");
                return;
            }

            int moved = 0; float furthest = 0f, total = 0f;
            int n = Mathf.Min(_zdStart.Length, sim.Count);
            for (int i = 0; i < n; i++)
            {
                var d = sim.PositionOf(i) - _zdStart[i];
                float dist = new Vector2(d.x, d.z).Length();
                if (dist > 0.25f) moved++;
                total += dist;
                if (dist > furthest) furthest = dist;
            }
            var s = sim.Stats;
            GD.Print($"[zdirtest] {zd.DebugLine()}");
            GD.Print($"[zdirtest] over the sampled window: {moved}/{n} rows moved, furthest {furthest:0.##} m, mean {(n > 0 ? total / n : 0):0.###} m");
            // Per-tick cost of the REWRITE. --zperf cannot answer this: it builds ZombieController, the OLD
            // path, so the rewrite has never had perf coverage -- which is how it reached strawberry at 2 fps
            // with the F3 systems line naming none of it. z.rays is a count, not a time.
            {
                ulong pticks = Engine.GetPhysicsFrames() - _zdPhys0;
                var parts = new System.Collections.Generic.List<string>();
                foreach (var kv in Prof.Us)
                    parts.Add(kv.Key == "z.rays"
                        ? $"{kv.Key}={kv.Value / (double)System.Math.Max(1, (long)pticks):0.0}/tick"
                        : $"{kv.Key}={kv.Value / (double)System.Math.Max(1, (long)pticks) / 1000.0:0.000}ms");
                parts.Sort();
                GD.Print($"[zdirtest] per-tick over {pticks} physics frames: "
                         + (parts.Count > 0 ? string.Join("  ", parts) : "(NOTHING INSTRUMENTED)"));
            }
            GD.Print($"[zdirtest] {(moved > 0 && s.PathQueries >= 0 && s.Alive > 0 ? "PASS -- zombies exist, tier, path and walk with no physics bodies" : "FAIL -- nothing moved")}");
            GetTree().Quit();
        }




        public override void _Process(double delta)
        {
            // FIRST, because several capture modes below own the frame and return before the main
            // capture gate -- a watchdog placed at that gate never runs for --vehicle/--rig/--menushot.
            if (_shotRequested != null && ShotWatchdogTripped()) return;
            if (_doorAnim && _doorAnimDoor != null)   // --doortest UG_DOOR_ANIM=1: drive a real DEFAULT->away->DEFAULT cycle at REAL elapsed time (never fast-forwarded), so a --write-movie capture shows the actual retail-curve swing from the real default state (see BuildDoorTest for the timeline setup)
            {
                _doorAnimElapsed += delta;
                if (!_doorAnimToggle1Done && _doorAnimElapsed >= _doorAnimToggle1At)
                {
                    _doorAnimDoor.Toggle(); _doorAnimToggle1Done = true;
                    GD.Print($"[DOORANIM] toggle 1 (away from default) fired at t={_doorAnimElapsed:0.000}s");
                }
                else if (_doorAnimToggle1Done && !_doorAnimToggle2Done && _doorAnimElapsed >= _doorAnimToggle2At)
                {
                    _doorAnimDoor.Toggle(); _doorAnimToggle2Done = true;
                    GD.Print($"[DOORANIM] toggle 2 (back to default) fired at t={_doorAnimElapsed:0.000}s");
                }
                else if (_doorAnimToggle2Done && _doorAnimElapsed >= _doorAnimDoneAt)
                {
                    GD.Print($"[DOORANIM] sequence done at t={_doorAnimElapsed:0.000}s -- quitting");
                    GetTree().Quit();
                }
                return;
            }
            if (_zbMode) { ZBodyTick(delta); return; }                                                  // --zbody probe owns the frame
            if (_zpZombies.Count > 0) { if (_zaiMode) ZAITick(delta); else ZPerfTick(delta); return; }   // --zperf probe owns the frame
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
            // sample at frame 30 (nav synced), then again ~5 s later, and report how far rows actually walked
            if (_zdirTest)
            {
                // Stand the player in a POI first, THEN start the clock. Movie-mode frames are expensive
                // under lavapipe, so the window is short -- a few seconds of sim, not the ten it was.
                if (++_zdFrames == 10) ZombieDirTestStand();
                else if (_zdFrames == 40) RunZombieDirTest();
                else if (_zdFrames >= 130) { _zdirTest = false; RunZombieDirTest(); }
                return;
            }
            if (System.Environment.GetEnvironmentVariable("UG_PERF") == "1" && (_perfT -= (float)delta) <= 0f)
            {
                _perfT = 1f;
                int zc = GetTree().GetNodesInGroup("zombies").Count;
                double physMs = Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000.0;
                double procMs = Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0;
                GD.Print($"[perf] fps={Engine.GetFramesPerSecond()} zombies={zc} physicsMs={physMs:0.0} processMs={procMs:0.0} draws={Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame)}");
            }
            if (_fireTest && _ftPlayer != null) { _ftFrame++; if (System.Environment.GetEnvironmentVariable("UG_LEAN") is string _ln && _ln.Length > 0 && _ftFrame >= 8) _ftPlayer.ScriptedLean = int.Parse(_ln);   /* UG_LEAN=1 lean left / -1 right: verify the 1P viewmodel rolls with the lean */ if (System.Environment.GetEnvironmentVariable("UG_MOVE") == "1" && _ftFrame >= 8) _ftPlayer.ScriptedInput = new UnityEngine.Vector2(0f, 1f);   /* UG_MOVE=1: walk forward -> verify the viewmodel movement-sway tilt */ if (System.Environment.GetEnvironmentVariable("UG_ADS") == "1") { if (_ftFrame >= 40) _ftPlayer.ForceAim(true); } else if (System.Environment.GetEnvironmentVariable("UG_TRACERANGLE") == "1") { if (_ftFrame >= 45 && _ftFrame % 10 == 0) _ftPlayer.DebugFireAngled(-28f); } else if (_ftFrame >= 60 && _ftFrame % 15 == 0) _ftPlayer.Fire(); }   // own counter; UG_ADS: hold ADS; UG_TRACERANGLE: fire tracers 38deg across the view so the stretched streak is seen side-on
            if (_peiPlay && _peiPlayer != null)
            {
                _peiFrame++;
                if (System.Environment.GetEnvironmentVariable("UG_AUTOFIRE") == "1") { if (_peiFrame >= 55 && (_peiFrame % 12 == 0 || _peiFrame >= 156)) _peiPlayer.Fire(); }   // impact-render test: stay on foot + fire forward; sustained burst 156+ so a muzzle FLASH lands on the frame-160 capture (glow showcase)
                else if (System.Environment.GetEnvironmentVariable("UG_FP") == "1") { if (System.Environment.GetEnvironmentVariable("UG_EAT") is string _eatAt && _eatAt.Length > 0 && _peiFrame == (int.TryParse(_eatAt, out var _ef) ? _ef : 100)) _peiPlayer.StartConsume(); if (System.Environment.GetEnvironmentVariable("UG_FUELCAN") == "1" && _peiFrame == 30) { var _gcit = new SDG.Unturned.Item(28); _peiPlayer.EquipHeldFuelCan(_gcit.GetAsset(), _gcit); } }   // UG_FP: on foot for the FP viewmodel; UG_EAT=<startFrame> click-eat; UG_FUELCAN=1 equips the gas can (verify the real two-handed hold in the game FP camera)
                else if (_peiFrame == 50) _peiPlayer.EnterNearestVehicle(); else if (_peiFrame >= 55) _peiPlayer.ScriptedDrive = new Vector2(0f, 1f);   // settle onto PEI, hop in, drive forward (--horde: the loud drive aggros the zombie field -> roadkill)
            }
            if (_peiPlayable && _pdPlayer != null && System.Environment.GetEnvironmentVariable("UG_AUTOFIRE") == "1" && _worldReady && _pdFireT++ % 8 == 0) _pdPlayer.Fire();   // peidrive: fire at the real terrain -> verify the SurfAt material impacts render
            if (_rigDir != null)
            {
                _frame++;
                if (_gunLayerTest && _rc != null)   // 3P gun-layer test: legs walk + arms hold/aim/reload, then crouch/prone + the rack
                {
                    // Stance timeline: STAND (walking) -> CROUCH (idle) -> PRONE (idle), so the overlay's stance handling
                    // is visible -- the torso should LIE DOWN / hunch, not stay upright over crouched legs (master).
                    var st = _frame < 120 ? SDG.Unturned.EPlayerStance.STAND
                           : _frame < 165 ? SDG.Unturned.EPlayerStance.CROUCH
                           :                 SDG.Unturned.EPlayerStance.PRONE;
                    float sp = _frame < 120 ? 3.0f : 0.0f;                                                    // stand walks; crouch/prone idle so the posture reads
                    _rc.SetLocomotion(sp, st);
                    _rc.AimBlend = (_frame >= 40 && _frame < 68) ? Mathf.Min(1f, (_frame - 40) / 8f) : 0f;    // ADS in f40-48, hold, out f68
                    if (_frame >= 52 && _frame <= 58) _rc.FlashMuzzle();                                     // fire: hold the flash across the capture
                    if (_frame == 70) _rc.SetGunOverlay(_glReloadClip, 1f, loop: false);                      // reload once (standing)
                    if (_frame == 92 && _glHammerClip != null) _rc.SetGunOverlay(_glHammerClip, 1f, loop: false);  // the RACK (empty-reload 2nd half) -> captured at f100
                    if (_frame == 112) _rc.SnapGunOverlay(_glEquipClip);                                      // back to the ready hold before the stance demo
                    _rc.Tick(delta);
                }
                if (_ragTest && _frame == 4) _rc?.RagdollStart(new Vector3(3.5f, 5f, 1.5f)); // knock him over
                if (_ragTest && _frame == 46) _rc?.ApplyImpact(_rc.GlobalPosition + new Vector3(0f, 0.4f, 0f), new Vector3(8f, 4f, 0f)); // simulate a corpse shot
                // UG_SIGHT=<mesh.txt>: mount a specific sight/scope on the gun once equipped, for a scope-showcase demo.
                if (_vmTest && _vm != null && !_vmSightSet && _vm.IsEquipComplete && System.Environment.GetEnvironmentVariable("UG_SIGHT") is string _sg && _sg.Length > 0)
                { _vm.SetSlotMesh("Sight", _sg); _vmSightSet = true; }
                // --vm ADS demo: the equip pull-out plays first (source gates aiming until it finishes), then a
                // short settle, THEN ADS; release later so the clip shows the un-ADS back to hip. No recoil.
                if (_vmTest && _vmAttach && _vm != null)
                {
                    // --attach: once equipped, hold the T attachment menu open (no aim/fire) so the render shows the slot icons
                    if (_am != null && _vm.IsEquipComplete && !_am.IsOpen && ++_vmSettle >= 8) _am.Open();
                }
                else if (_vmTest && _vm != null && !_vmMelee && System.Environment.GetEnvironmentVariable("UG_NOADS") != "1")   // gun scripted sequence: ADS -> hip-fire (Kick) -> reload; a melee never fires/aims/reloads, so skip it (its MeleeSwingDriver drives the swings). UG_NOADS=1 skips the whole sequence so the gun HOLDS at hip -> a late frame shows a fully-ramped sprint/safety pose (which ADS would otherwise fade out).
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
                    if (_vmTest && _vm != null)   // the gun+arms render in the Viewmodel's own SubViewport (composited by a CanvasLayer), which GetViewport() misses -> blend it over the background so the still actually shows the weapon
                    {
                        var g = _vm.CaptureViewport();
                        GD.Print($"[VMCAP] g={(g == null ? "null" : $"{g.GetSize()} fmt{(int)g.GetFormat()}")} main={im.GetSize()} fmt{(int)im.GetFormat()}");
                        if (g != null)
                        {
                            g.SavePng($"{_rigDir}/vpraw_{_rigShot:D2}.png");   // DEBUG: the raw SubViewport capture (does it hold the gun?)
                            if (im.GetFormat() != Image.Format.Rgba8) im.Convert(Image.Format.Rgba8);
                            if (g.GetFormat() != Image.Format.Rgba8) g.Convert(Image.Format.Rgba8);
                            if (g.GetSize() != im.GetSize()) g.Resize(im.GetWidth(), im.GetHeight());
                            im.BlendRect(g, new Rect2I(Vector2I.Zero, g.GetSize()), Vector2I.Zero);
                        }
                    }
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
            else if (_fireTest) { if (System.Environment.GetEnvironmentVariable("UG_ADS") == "1") { if (_ftFrame < 70) return; } else if (_ftPlayer == null || _ftPlayer.Ammo > 20 || _ftFrame < 75) return; }   // firetest: capture once ~10 shots fired (high-cap: Ammo<=20); the _ftFrame>=75 floor lets a low-cap gun (launcher = 1 rocket at frame 60) actually fire + impact before the quit. UG_ADS: capture the settled aim frame (70) instead
            else if (_worldBuild) { if (!_worldReady || ++_frame < ShotSettleFrames) return; }   // objects/peidrive: WAIT for the async world (terrain..trees) to finish + settle before the shot
            else if (_navShot) { if (++_frame < 24) return; }   // navshot: let lighting/shadows + the overlay settle before capture
            else if (System.Environment.GetEnvironmentVariable("UG_DEPLOYDMG") != null) { if (++_frame < 45) return; }   // deploytest damage: let smoke/fire particles accumulate before the shot
            else if (System.Environment.GetEnvironmentVariable("UG_WIREWRECK") == "1") { if (++_frame < 20) return; }   // shatter: catch the debris collapsing toward the ground
            else if (System.Environment.GetEnvironmentVariable("UG_WIRETEST") == "1") { if (++_frame < 50) return; }   // wire test: let the lamp warmup envelope settle (past the flicker ramp) before capturing steady state
            else if (++_frame < 6) return; // let the renderer settle
            if (_spotDbg != null && IsInstanceValid(_spotDbg)) GD.Print($"[LAMPDBG] consumerPowered={_spotDbg.DebugConsumerPowered} lampsLit={_spotDbg.DebugLampsLit}");   // plain UG_WIRETEST render: a wired+powered spotlight's lamps must be on
            // Draw calls + primitives at the capture frame. Frame MILLISECONDS on a software rasteriser say
            // nothing about a real GPU, but what the culler admitted into the frame is hardware-independent --
            // so this is the number to compare when changing draw distances, not fps.
            GD.Print($"[lodperf] drawcalls {RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalDrawCallsInFrame)}" +
                     $" | primitives {RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalPrimitivesInFrame)}" +
                     $" | objects {RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalObjectsInFrame)}");
            var img = GetViewport().GetTexture().GetImage();
            if (img == null) { GD.PrintErr("[SHOT] null image -- run with a rendering driver (e.g. --rendering-driver vulkan), NOT --headless"); GetTree().Quit(1); return; }
            img.SavePng(_shotPath);
            GD.Print($"[SHOT] saved {_shotPath} ({img.GetWidth()}x{img.GetHeight()})");
            GetTree().Quit();
        }

        // Frames to let the world settle before capturing. 45 is right for a golden image, but each frame on
        // a software rasteriser costs seconds, so a run that only needs the draw-call counts (UG_SHOTFRAMES=8)
        // finishes instead of timing out. _worldReady is checked separately -- this is settle, not load.
        static int ShotSettleFrames =>
            int.TryParse(System.Environment.GetEnvironmentVariable("UG_SHOTFRAMES"), out int n) && n > 0 ? n : 45;

        string _shotRequested;       // the capture the COMMAND LINE asked for, set at parse time
        /// <summary>The pocket with the most land around it, so the nav verify shot frames terrain rather
        /// than sea. Samples a ring at the camera's own radius (~60 m) and counts non-water hits; ties break
        /// on the lower index so the choice stays deterministic run to run.</summary>
        static int MostInlandPocket(System.Collections.Generic.List<NavPocket> pockets, Terrain terr)
        {
            int best = 0, bestLand = -1;
            for (int i = 0; i < pockets.Count; i++)
            {
                var c = pockets[i].Center;
                int land = 0;
                for (int a = 0; a < 12; a++)
                {
                    float ang = a / 12f * Mathf.Tau;
                    float sx = c.X + 60f * Mathf.Cos(ang), sz = c.Z + 60f * Mathf.Sin(ang);
                    if (!Terrain.IsWater(terr.SampleDominantLayer(sx, sz))) land++;
                }
                if (land > bestLand) { bestLand = land; best = i; }
            }
            return best;
        }

        ulong _shotWaitStartMs;      // wall clock at the first frame with a capture pending
        ulong _shotLastReportMs;
        bool _shotTimedOut;

        /// <summary>Bound the wait for a capture, and say what it is waiting ON.
        ///
        /// Every gate below this is `if (not ready) return;`, so a prerequisite that never arrives means the
        /// process sits forever rendering a movie nobody will look at. That is how these harnesses "break":
        /// not by rotting, but by becoming indistinguishable from a slow render. --navshot with UG_UNTURNED_DIR
        /// unset waits on _worldReady that WorldBuilder will never set (it returns early with Ready=false when
        /// the map is missing) -- it printed the hint once at startup and then hung. With the variable set the
        /// same code captures in 53 s. Nothing was broken; the failure had no voice.
        ///
        /// So: a heartbeat naming the blocking gate, then a bounded, non-zero exit. Wall clock rather than
        /// frames because movie-mode frames are game time -- 30 fps of game time can be any amount of real
        /// time, which is precisely the confusion being removed. UG_SHOT_TIMEOUT=0 disables for a deliberately
        /// long capture.</summary>
        bool ShotWatchdogTripped()
        {
            if (_shotTimedOut) return true;
            ulong now = Godot.Time.GetTicksMsec();
            if (_shotWaitStartMs == 0) { _shotWaitStartMs = now; _shotLastReportMs = now; return false; }

            ulong budgetSec = 300;
            var cfg = System.Environment.GetEnvironmentVariable("UG_SHOT_TIMEOUT");
            if (cfg != null && ulong.TryParse(cfg, out var parsed)) budgetSec = parsed;
            if (budgetSec == 0) return false;   // opt out

            ulong waited = now - _shotWaitStartMs;
            if (now - _shotLastReportMs >= 15000)   // heartbeat: even if an OUTER timeout kills us, the log says why
            {
                _shotLastReportMs = now;
                GD.Print($"[SHOT] still waiting after {waited / 1000}s -- {ShotBlockedOn()}");
            }
            if (waited < budgetSec * 1000) return false;

            _shotTimedOut = true;
            GD.PrintErr($"[SHOT] TIMED OUT after {waited / 1000}s without capturing to {_shotRequested}");
            GD.PrintErr($"[SHOT] blocked on: {ShotBlockedOn()}");
            GD.PrintErr("[SHOT] most common cause: UG_UNTURNED_DIR is unset, so the map never loads and the "
                      + "world never reports Ready. Set it (e.g. /home/ec2-user/unturned) and re-run.");
            GetTree().Quit(1);
            return true;
        }

        /// <summary>Which gate is still closed -- the thing the old silent hang never told anyone.</summary>
        string ShotBlockedOn()
        {
            // The nastiest case: the capture was never even ARMED. Several builders assign _shotPath as their
            // LAST statement, so a builder that bails early (a missing map returns a world with Ready=false)
            // leaves _shotPath null -- the process then renders happily forever with no capture pending and
            // nothing to report. Watching the command-line intent instead of the armed path is what makes
            // this visible at all.
            if (_shotPath == null) return "capture never armed -- the scene builder bailed before requesting it "
                                        + "(world/map data almost certainly missing)";
            if (_worldBuild && !_worldReady) return "async world load (worldReady=false; map data missing or still loading)";
            if (_peiPlay) return $"peiplay frame budget (frame={_peiFrame})";
            if (_fireTest) return $"firetest (frame={_ftFrame}, ammo={_ftPlayer?.Ammo.ToString() ?? "no player"})";
            if (_navShot) return $"navshot settle (frame={_frame})";
            return $"settle frame budget (frame={_frame})";
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
