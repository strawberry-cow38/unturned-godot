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

        string _shotPath; float _shotElapsed;   // UG_SHOTTIME: capture at an elapsed-time target (real-time frame counts drift off fixed-fps -- tinyclaw)
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
        bool _planeTest;   // UG_PLANETEST (with --boattest --gun=otter): scripted fixed-wing flight (throttle/pitch/roll injected) to verify the flight model in a render
        int _heliPhase, _heliPhaseTick;   // UG_HELITEST maneuver sequence: 0 climb, 1 cruise, 2 turn, 3 slide, 4 recover
        bool _heliTest;    // UG_HELITEST (with --vehicle --gun=minicopter|huey): scripted ROTARY flight -- see the loop in _PhysicsProcess for why this exists
        System.Collections.Generic.List<Vector3> _trP; System.Collections.Generic.List<float> _trD;
        System.Collections.Generic.List<(MeshInstance3D body, MeshInstance3D bf, MeshInstance3D bb, float off)> _trUnits;
        float _trS, _trRailY = 1.4f; bool _trAnim;
        readonly System.Collections.Generic.List<(Node3D mark, Vehicle veh, Vector3 local)> _pivotMarks = new();   // --pivots: arrow markers pinned to each coupling point
        bool _driveTest, _swarm, _drivethru, _nade; PlayerController _dtPlayer;      // --drivetest=DIR [--swarm|--drivethru|--nade] : enter/drive a jeep; swarm = mob it; drivethru = loud drive wakes zombies; nade = grenade the parked car
        bool _fireTest; PlayerController _ftPlayer; int _ftFrame;   // --firetest [--supp] : player fires near a distant zombie -> gunshot alert (suppressed = none)
        bool _peiPlay; PlayerController _peiPlayer; int _peiFrame; bool _peiHorde;   // --peiplay [--horde] : drive a jeep on real PEI (--horde = a zombie horde swarms it, vehicle<->zombie loop on real ground)
        int _tpFrame; double _tpPrims, _tpDraws, _tpMs; int _tpN;   // --- UG_TERRPERF terrain cost probe
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
            // VSYNC OFF GLOBALLY (strawberry 2026-08-10). With a pacer on, frame time is pinned to the display's
            // refresh interval, so the number you profile against is one the monitor chose and headroom reads as
            // zero -- the profiler frame time cannot tell 6 ms of work from 16 ms of work behind a 60 Hz wall.
            // Also set in project.godot; repeated here so it holds however that setting is read, and the ACTUAL
            // mode is logged rather than assumed. Skipped headless, which has no window to set it on.
            if (DisplayServer.GetName() != "headless")
            {
                DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
                GD.Print($"[display] vsync -> {DisplayServer.WindowGetVsyncMode()}");
            }
            string catalog = null, shot = null, picks = null, gun = null, rig = null, anim = "Walk", vm = null, bakeIcon = null, veh = null, drivetest = null, proptest = null, magnettest = null, animrig = null, rottest = null, itemtest = null, navShot = null, croptest = null, menuShot = null, clothtest = null, boattest = null, slingtest = null, trainshow = null, traintrack = null, ammoRadial = null, animaltest = null, treetest = null;
            bool zperf = false;
            bool zbody = false;
            bool deployTest = false, barricadeTest = false, barricadePlay = false;
            bool wearcloth = false;
            bool skillsui = false;
            bool fluidTest = false;
            bool tailCheck = false; string tailShot = null; string bellyShot = null;
            bool doorTest = false;
            string doorTestName = null;
            bool containerTest = false; string containerTestName = null;
            bool wallDemo = false;
            bool clockTest = false;
            bool play = false, demo = false, netdemo = false, server = false, dedicated = false, client = false, smoke = false, hurtdemo = false, invdemo = false, invsel = false, invequip = false, invdrop = false, invloot = false, invcrate = false, daynight = false, lightTest = false, trafficTest = false, buildmode = false, firetest = false, supp = false, terrain = false, peiplay = false, playground = false, objects = false, peidrive = false, craftmenu = false, bakenav = false, navPathTest = false, zombieTest = false, zdirTest = false, editorMode = false, impactTest = false, doorGallery = false, lampTest = false, beamTest = false, impTest = false, treeSweep = false, bakeLods = false, bakeLodsDry = false, netobserve = false;
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
                else if (arg.StartsWith("--proptest=")) { proptest = arg["--proptest=".Length..]; _shotRequested = proptest; }
                else if (arg.StartsWith("--animaltest=")) { animaltest = arg["--animaltest=".Length..]; _shotRequested = animaltest; }   // one animal rig posed as if walking -Z, to measure the RigYawFix (UG_ANIMALYAW spins it)
                else if (arg.StartsWith("--treetest=")) { treetest = arg["--treetest=".Length..]; _shotRequested = treetest; }   // standing tree beside a felled one (its dropped logs) -> render the harvest
                else if (arg == "--trainshow") trainshow = "1";   // assemble train_cargo_0 from its extracted pieces for a 3/4 shot
                else if (arg == "--traintrack") traintrack = "1";   // ride the train along a curved test track
                else if (arg.StartsWith("--slingtest=")) { slingtest = arg["--slingtest=".Length..]; _shotRequested = slingtest; }
                else if (arg.StartsWith("--magnettest=")) { magnettest = arg["--magnettest=".Length..]; _shotRequested = magnettest; }
                else if (arg == "--tailcheck") tailCheck = true;
                else if (arg.StartsWith("--bellyshot=")) { bellyShot = arg["--bellyshot=".Length..]; _shotRequested = bellyShot; }
                else if (arg.StartsWith("--tailshot=")) { tailShot = arg["--tailshot=".Length..]; _shotRequested = tailShot; }   // NAME:OUT -- close-up of one heli's tail from behind   // audit every heli: which side is the tail-rotor POST on, vs where the spec puts the hub   // sky-crane winch + electromagnet: dangle, energise, bite a load, lift it   // skycrane + shipping container: in-the-bay vs slung-beneath, side by side   // spawn ONE named prop at identity + RGB axes -> diagnose mirror/orientation/material
                else if (arg.StartsWith("--croptest=")) croptest = arg["--croptest=".Length..];   // spawn a farm crop (young + grown) on a ground plane -> validate mesh/tex/orientation (UG_CROPROT tunes rot)
                else if (arg == "--zperf") zperf = true;   // GPU perf probe: N zombies, render counters ON vs OFF (MUST run with a rendering driver, not --headless)
                else if (arg == "--zbody") zbody = true;   // MECHANISM probe: N bare kinematic capsules, moving vs parked -> is the physics cost the BODIES?
                else if (arg == "--deploytest") deployTest = true;   // both deployables placed on a ground plane + a valid(blue)+invalid(red) ghost -> verify models/palette/stand-up/ghost materials
                else if (arg == "--impacttest") impactTest = true;   // one bullet-impact FX per surface (concrete/metal/wood/dirt/grass/sand/water/blood) across a wall -> verify the reimplemented ImpactFx
                else if (arg == "--doorgallery") doorGallery = true;   // --shot=OUT : lineup of the 12 ripped WOODEN door barricade models (Door/Doubledoor/Gate/Hatch x Birch/Maple/Pine) for master to eyeball
                else if (arg == "--barricadetest") barricadeTest = true;   // barricades mounted on a STRUCTURE wall (upright, facing out) + a valid ghost + a floor barricade -> verify surface placement
                else if (arg == "--barricadeplay") barricadePlay = true;   // INTERACTIVE: fly (hold RMB) + LMB-place barricades on a structure room -- test placement feel ([1-3]=def, Tab=mount, R=rotate)
                else if (arg == "--skillsui") skillsui = true;   // render the skills menu (showcase/validate the SkillsUI)
                else if (arg.StartsWith("--itemtest=")) itemtest = arg["--itemtest=".Length..];   // drop a row of loot items (ids) as physics WorldItems -> validate real mesh/tex/scale/settle
                else if (arg.StartsWith("--ammoradial=")) { ammoRadial = arg["--ammoradial=".Length..]; _shotRequested = ammoRadial; }   // open the R-hold shotgun ammo radial (mock 12ga choices) -> screenshot the picker UI
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
                // NOTE: `--demo` is claimed higher up this same else-if chain (it sets _demo, the scripted
                // honk/explode script), so the second branch that used to live here could never run. `demo` stayed
                // false forever, which made the DemoDirector + overview camera + fixed 1920x1080 capture size
                // unreachable: `--play --demo --write-movie` recorded interactive play from the player camera at
                // the window size instead of the scripted demo. Both meanings now ride the ONE flag, so the two
                // cannot drift apart again. Review 2026-08-16.
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
                else if (arg == "--netobserve") netobserve = true;   // headless net-observer: full netcode + replica state, NO render world (combine with --connect= for the target; see BuildNetObserver)
                else if (arg == "--smoke") smoke = true;
                else if (arg == "--hurtdemo") hurtdemo = true;
                else if (arg == "--firetest") firetest = true;   // player fires near a distant zombie: verify the gunshot alert (+ --supp = suppressed -> no alert)
                else if (arg == "--supp") supp = true;           // with --firetest: attach the suppressor
                else if (arg == "--terrain") terrain = true;     // load a real map's Landscape heightmap terrain (PEI Tile_0_0)
                else if (arg == "--craftmenu") craftmenu = true; // open the CraftingMenu (browsable recipe index) over a stocked bag
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
                else if (arg == "--playground") playground = true;   // GUN PLAYGROUND: flat lane + player-shaped dummies at 10/25/50/100/200/300 m, floating damage numbers
                else if (arg == "--peiplay") peiplay = true;     // player standing/walking on real PEI terrain (with colliders)
                else if (arg == "--invdemo") invdemo = true;
                else if (arg == "--invsel") { invdemo = true; invsel = true; }
                else if (arg == "--invequip") { invdemo = true; invequip = true; }
                else if (arg == "--invdrop") invdrop = true;
                else if (arg == "--invloot") invloot = true;
                else if (arg == "--invcrate") invcrate = true;
                else if (arg == "--daynight") daynight = true;
                else if (arg == "--lighttest") lightTest = true;   // one lit streetlight at night: cone + motes eyeball (UG_LIGHTCAM=under looks up from inside)
                else if (arg == "--bakelods") bakeLods = true;        // OFFLINE tool: generate a lod1 for every prop retail shipped without one
                else if (arg == "--bakelods-dry") { bakeLods = true; bakeLodsDry = true; }
                else if (arg == "--treesweep") treeSweep = true;   // step the camera ACROSS the tree->imposter handover and count tree pixels at each distance
                else if (arg == "--imptest") impTest = true;   // bake the tree billboards and DUMP them side by side -- the only check that answers "does it look like a tree"
                else if (arg == "--lamptest") lampTest = true;   // one lit INDOOR light over dark ground: UG_LAMP=Light_0(ceiling,default)/Light_1/Lamp_0/Lamp_1, UG_LAMPOFF=1 unlit
                else if (arg == "--beamtest") beamTest = true;   // the lighthouse's sweeping beam at night (static frame)
                else if (arg == "--trafficlight") trafficTest = true;   // one signal, both heads (UG_TL_STATE=green|amber|red|flash|dark, UG_TL_SIDE=1 for the side-road mast, UG_TL_DAY=1 for daylight)
                else if (arg == "--build") buildmode = true;
                else if (arg == "--walls") wallDemo = true;   // building tool: generated walls + openings, no editor needed
                else if (arg == "--clocktest") clockTest = true;   // Clock_0 facing the camera, hands split off + spun to UG_TIME (verify the reach split + hand angles)
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

            if (craftmenu)   // open the CraftingMenu (the current in-game one) over a stocked bag -> render it
            {
                GetWindow().Size = new Vector2I(1280, 720);
                _shotPath = shot;
                BuildCraftMenu();
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

            if (doorGallery)   // --doorgallery --shot=OUT : a front-on lineup of the 12 ripped WOODEN door barricade models for master to eyeball
            {
                GetWindow().Size = new Vector2I(2560, 1440);
                _shotPath = shot; _shotRequested = shot;
                BuildDoorGallery();
                return;
            }



            if (navPathTest) { _bakeNav = true; _peiPlayable = true; BuildObjectsTest(); _navPathTest = true; return; }   // sync-load; RunNavPathTest fires after a few frames (the nav map merges its regions on a physics tick, not in _Ready)
            if (zombieTest) { _bakeNav = true; _peiPlayable = true; _zombieTest = true; BuildObjectsTest(); return; }   // sync-load (creates the ZombieField + buckets spawns); RunZombieTest fires at frame 25 once the nav map has synced
            if (zdirTest) { ZombieDirector.Enabled = true; _bakeNav = true; _peiPlayable = true; _zdirTest = true; BuildObjectsTest(); return; }   // sync-load the REWRITE on the real map, then watch it tier/path/move for a few seconds

            if (navShot != null) { GetWindow().Size = new Vector2I(1280, 720); BuildNavShot(navShot); return; }

            if (playground) { WorldBuilder.BuildPlaygroundWorld(this); return; }
            if (peiplay)   // drop the player onto real PEI terrain (colliders on) + walk -> the whole session's work on an actual map
            {
                GetWindow().Size = System.Environment.GetEnvironmentVariable("UG_FUELCAN") == "1" ? new Vector2I(1600, 900) : new Vector2I(1280, 720);   // crisper capture for the gas-can viewmodel check
                _peiPlay = true;
                _shotPath = shot;   // captured at a LATE frame (below) so the drop+enter+drive plays out first
                BuildPeiPlay();
                return;
            }

            if (bellyShot != null)   // look UP at the underside: the belly beacon's AABB has been right and its picture wrong three times
            {
                GetWindow().Size = new Vector2I(1100, 800);
                var bbits = bellyShot.Split(':');
                _shotPath = bbits.Length > 1 ? bbits[1] : bellyShot;
                BuildBellyShot(bbits[0]);
                return;
            }
            if (tailShot != null)   // eyeball ONE tail: the scan says which side has reach, this says what it IS
            {
                GetWindow().Size = new Vector2I(1100, 800);
                var bits = tailShot.Split(':');
                _shotPath = bits.Length > 1 ? bits[1] : tailShot;
                BuildTailShot(bits[0]);
                return;
            }
            if (tailCheck)   // audit the whole fleet's tail-rotor mounting side against the mesh
            {
                BuildTailCheck();
                return;
            }
            if (magnettest != null)   // sky-crane electromagnet: does the cable dangle, bite a load and actually lift it?
            {
                GetWindow().Size = new Vector2I(1600, 900);
                _shotPath = shot ?? magnettest;
                BuildMagnetTest();
                return;
            }
            if (slingtest != null)   // diagnostic: can a shipping container ride in the skycrane, or does it have to hang?
            {
                GetWindow().Size = new Vector2I(1600, 900);
                _shotPath = shot;
                BuildSlingTest();
                return;
            }
            if (proptest != null)   // diagnostic: one prop at identity + RGB axis refs (X=red,Y=green,Z=blue) + 3/4 cam
            {
                GetWindow().Size = new Vector2I(900, 900);
                _shotPath = shot;
                BuildPropTest(proptest);
                return;
            }
            if (animaltest != null) { GetWindow().Size = new Vector2I(1000, 720); _shotPath = shot; BuildAnimalTest(animaltest); return; }
            if (treetest != null) { GetWindow().Size = new Vector2I(1280, 800); _shotPath = shot; BuildTreeTest(treetest); return; }
            if (trainshow != null) { GetWindow().Size = new Vector2I(1600, 720); BuildTrainShow(); return; }
            if (traintrack != null) { GetWindow().Size = new Vector2I(1600, 900); BuildTrainTrack(); return; }

            if (zbody) { BuildZBody(); return; }
            if (zperf) { BuildZPerf(); return; }
            if (deployTest)   // deployables showcase: both placed on a ground plane + a valid(blue)/invalid(red) ghost
            {
                GetWindow().Size = new Vector2I(1280, 720);
                _shotPath = shot;
                BuildDeployTest();
                return;
            }

            if (impactTest)   // bullet-impact FX showcase: one per surface across a wall, captured mid-burst
            {
                GetWindow().Size = new Vector2I(1280, 720);
                _shotPath = shot;
                BuildImpactTest();
                return;
            }

            if (barricadeTest)   // barricades-on-structures showcase: upright wall-mounts facing out + a valid ghost + a floor barricade
            {
                GetWindow().Size = new Vector2I(1280, 720);
                _shotPath = shot;
                BuildBarricadeTest();
                return;
            }

            if (barricadePlay)   // INTERACTIVE barricade placement sandbox (fly + place). Live; --shot=OUT still captures the opening frame for a build check
            {
                GetWindow().Size = new Vector2I(1280, 720);
                _shotPath = shot;
                BuildBarricadePlay();
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

            if (ammoRadial != null)   // open the R-hold shotgun ammo radial with mock 12ga choices -> screenshot the picker UI
            {
                GetWindow().Size = new Vector2I(1280, 720);
                _shotPath = ammoRadial;
                SDG.Unturned.ItemCatalog.RegisterAll();   // so the shell assets + icons resolve
                BuildAmmoRadialDemo();
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

            if (bakeLods)   // offline: write the LODs retail never authored, then quit. No world, no renderer needed.
            {
                LodBaker.BakeAll(ProjectSettings.GlobalizePath("res://content/objects/"), bakeLodsDry);
                GetTree().Quit();
                return;
            }

            if (treeSweep)   // walk the handover band and prove a tree is never absent at any distance in it
            {
                GetWindow().Size = new Vector2I(640, 360);
                _ = BuildTreeSweep();
                return;
            }

            if (impTest)   // tree impostor bake: render the billboards to a sheet so a human can see whether they read as trees
            {
                GetWindow().Size = new Vector2I(1280, 720);
                _ = BuildImpostorTest(shot);   // _shotPath is armed INSIDE, after the bake -- see the note there
                return;
            }

            if (lampTest)   // one indoor light, lit, over a dark ground -- the fixture glow is only checkable by eye
            {
                _shotPath = shot;
                GetWindow().Size = new Vector2I(1280, 720);
                BuildLampTest();
                return;
            }

            if (beamTest)   // the lighthouse's sweeping beam over a night ground -- one static frame (the spin needs an eye)
            {
                _shotPath = shot;
                GetWindow().Size = new Vector2I(1280, 720);
                BuildBeamTest();
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

            if (clockTest)   // one Clock_0 facing the camera, hands spun to UG_TIME (default 0.375 = 09:00)
            {
                _shotPath = shot;
                GetWindow().Size = new Vector2I(1280, 720);
                BuildClockTest();
                return;
            }

            if (wallDemo)   // the building tool's geometry, straight from WallOpenings -> WallSurface
            {
                GetWindow().Size = new Vector2I(1280, 720);
                _shotPath = shot; _shotRequested = shot;
                BuildWallDemo();
                return;
            }

            if (buildmode)  // script a small structure (floor + walls) to show the build system
            {
                GetWindow().Size = new Vector2I(1280, 720);
                _shotPath = shot; _shotRequested = shot;   // --build never armed the capture: every other shot mode sets these two, this one did not, so --shot was silently ignored and the run just timed out
                BuildBuildDemo(gun);
                return;
            }

            if (dedicated) { BuildDedicated(); return; }        // headless dedicated server: real world + NetServerSession (MP_PLAN §4 Phase 3)
            if (server) { BuildServer(); return; }              // headless demo server (bare arena + a scripted bot)
            if (netobserve) { BuildNetObserver(); return; }     // headless net-observer -- MUST precede the client dispatch: --connect= also sets `client`, and that path world-builds WorldMode.Client (headless-unsafe)
            if (client) { if (DisplayServer.GetName() != "headless") GetWindow().Mode = Window.ModeEnum.Maximized; BuildClient(); return; }   // fill the screen (same "tiny viewport" fix as --play below). Guard the window op for --headless (dummy DisplayServer, no window) -> a headless CLIENT runs the full netcode + world STATE with no rasterization (diagnostics / future scripted-client harness).

            if (netdemo)
            {
                GetWindow().Size = new Vector2I(1280, 720);
                BuildNetDemo();
                return;
            }

            // `demo` is the SAME flag as _demo now (see the arg loop) -- the second --demo branch that used to set
            // this local was dead, so the scripted-demo build path had been unreachable since it was written.
            demo = _demo;
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
                    menu.OnPlayground = () => { menu.QueueFree(); WorldBuilder.BuildPlaygroundWorld(this); };   // Playground -> the gun range (same entry as --playground)
                    menu.OnOpenMap = name => { menu.QueueFree(); BuildEditorNew(name); };   // Workshop -> a custom map by name (creates or opens)
                    // Play a custom map: open it exactly as the editor does, then enter play immediately. NOT a
                    // second world-building path -- a "play build" that assembles the map its own way is how the
                    // thing you test stops being the thing you edited.
                    menu.OnPlayMap = name => { menu.QueueFree(); BuildEditorNew(name); _autoPlayMap = true; };
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

        // --animaltest=<deer|pig|cow>: one animal rig posed as if walking toward -Z (Godot forward, the way AnimalAgent's
        // LookAt aligns it). The red bar points -Z = travel; compare the model's head to it. UG_ANIMALYAW=<deg> spins the
        // rig on the spot to find AnimalAgent.RigYawFix. 3/4 aerial so head/tail + left/right both read.
        void BuildAnimalTest(string species)
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
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-55f, -35f, 0f), LightEnergy = 1.2f, ShadowEnabled = true });
            var ground = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(20f, 20f) } };
            ground.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.30f, 0.34f, 0.28f) };
            AddChild(ground);

            (string rig, string tex, float foot) def = species switch
            {
                "pig" => ("pig", "Animal_Pig_tex.png", 0.22f),
                "cow" => ("cow", "Animal_Cow_tex.png", 0.52f),
                _     => ("deer", "Animal_Deer_tex.png", 0.70f),
            };
            var rc = RiggedCharacter.Build($"res://content/{def.rig}_rig.json", Colors.White, false, $"res://content/objects/{def.tex}", null);
            if (rc == null) { GD.PrintErr("[animaltest] rig build failed"); GetTree().Quit(); return; }
            var holder = new Node3D();   // identity: holder -Z is world -Z = the travel direction AnimalAgent's LookAt produces
            AddChild(holder);
            float holderY = float.TryParse(System.Environment.GetEnvironmentVariable("UG_ANIMALFOOT"), out var _hf) ? _hf : def.foot;   // UG_ANIMALFOOT=0 -> rig origin ON the ground, so the render shows the feet's TRUE local offset
            holder.Position = new Vector3(0f, holderY, 0f);
            holder.AddChild(rc);
            float yaw = float.TryParse(System.Environment.GetEnvironmentVariable("UG_ANIMALYAW"), out var y) ? y : 0f;
            rc.RotationDegrees = new Vector3(0f, yaw, 0f);
            rc.Play("Idle");
            GD.Print($"[animaltest] {def.rig}: holder faces -Z (travel), rig yaw {yaw:0}. clips: {string.Join(",", rc.ClipNames)}");
            // FEET measurement: lowest world-Y of any rig mesh box vs the Y=0 ground. holder is at foot, so >0 = floats.
            float _minY = 1e9f;
            var _st = new System.Collections.Generic.Stack<Node>(); _st.Push(rc);
            while (_st.Count > 0) { var _n = _st.Pop(); foreach (var _c in _n.GetChildren()) _st.Push(_c);
                if (_n is VisualInstance3D _vi) { var _bb = _vi.GetAabb(); var _gt = _vi.GlobalTransform;
                    for (int _i = 0; _i < 8; _i++) { var _cor = _bb.Position + _bb.Size * new Vector3(_i & 1, (_i >> 1) & 1, (_i >> 2) & 1); _minY = Mathf.Min(_minY, (_gt * _cor).Y); } } }
            GD.Print($"[animalfeet] {def.rig}: feet world Y={_minY:0.000} (float above Y=0; foot={def.foot}) -> ground it with foot={(def.foot - _minY):0.000}");

            var arrow = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.12f, 0.12f, 1.4f) } };   // points -Z = travel
            arrow.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.95f, 0.2f, 0.2f) };
            arrow.Position = new Vector3(1.5f, 0.06f, -0.7f);
            AddChild(arrow);

            var xref = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(1.4f, 0.12f, 0.12f) } };   // blue = +X reference
            xref.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.2f, 0.4f, 0.95f) };
            xref.Position = new Vector3(0.7f, 0.06f, 1.5f);
            AddChild(xref);
            var cam = new Camera3D { Fov = 38f };
            AddChild(cam);
            if (System.Environment.GetEnvironmentVariable("UG_ANIMALCAM") == "side")
            {
                cam.Projection = Camera3D.ProjectionType.Orthogonal; cam.Size = 3.4f;   // ORTHO: heights map EXACTLY to screen (no perspective) -> read the feet vs the Y=0 ground line precisely
                cam.LookAtFromPosition(new Vector3(8f, 1.0f, 0f), new Vector3(0f, 1.0f, 0f), Vector3.Up);
            }
            else
                cam.LookAtFromPosition(new Vector3(0f, 9f, 0f), Vector3.Zero, new Vector3(0f, 0f, -1f));   // TOP-DOWN, unambiguous: -Z(travel, RED) to top, +X(BLUE) right
        }

        // --treetest=<birch|maple|pine>: a standing tree on the LEFT, a felled one on the RIGHT (visual hidden + a real
        // TreeTrunk.Chop dropping the wood-type logs onto a collidable ground) -> renders the harvest before -> after.
        void BuildTreeTest(string species)
        {
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.5f, 0.62f, 0.78f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.6f, 0.62f, 0.6f), AmbientLightEnergy = 0.85f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-48f, -40f, 0f), LightEnergy = 1.2f, ShadowEnabled = true });
            AddChild(new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(80f, 80f) }, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.32f, 0.4f, 0.26f), Roughness = 1f } });
            var groundBody = new StaticBody3D { CollisionLayer = 1u << 0 };   // the dropped logs land on this
            groundBody.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
            AddChild(groundBody);

            SDG.Unturned.ItemCatalog.RegisterAll();   // WorldItem.Spawn resolves the log's model
            string dir = ProjectSettings.GlobalizePath("res://content/resources/");
            string name = species == "pine" ? "Pine_0" : species == "maple" ? "Maple_0" : "Birch_0";
            ushort log = species == "pine" ? (ushort)41 : species == "maple" ? (ushort)39 : (ushort)37;

            AddChild(LoadTreeVisual(dir, name, new Vector3(-8f, 0f, 0f)));   // LEFT: standing
            var trunk = new TreeTrunk { Field = null, Index = 11, LogItem = log, Health = 10f, RewardMin = 6, RewardMax = 8, TreeName = name, ResDir = dir, TreeXf = new Transform3D(Basis.Identity, new Vector3(8f, 0f, 0f)) };
            AddChild(trunk); trunk.Position = new Vector3(8f, 0f, 0f);
            trunk.Chop(999f, new Vector3(8f, 1f, 0f), new Vector3(0.6f, 0f, -0.8f).Normalized());   // fell it -> stump stays + debris topples back-right (keeps the near logs visible) + logs drop
            GD.Print($"[treetest] {name}: left standing, right felled -> stump + debris + logs (log item {log})");

            var cam = new Camera3D { Fov = 36f, Far = 800f };
            AddChild(cam);
            cam.LookAtFromPosition(new Vector3(0f, 15f, 62f), new Vector3(0f, 10f, 0f), Vector3.Up);
        }

        // Load a tree's parts (bark + leaves) from content/resources/<name>_<i>.obj as an upright Node3D at pos.
        Node3D LoadTreeVisual(string dir, string name, Vector3 pos)
        {
            var root = new Node3D { Position = pos };   // resource-tree objs are already Y-up (ResourceField applies no stand-up) -> identity
            for (int i = 0; i < 2; i++)
            {
                var m = ObjMesh.Load(dir + name + "_" + i + ".obj");
                if (m == null) continue;
                var mat = new StandardMaterial3D { CullMode = BaseMaterial3D.CullModeEnum.Disabled, Roughness = 0.9f };
                var img = new Image();
                if (img.Load(dir + name + "_" + i + "_tex.png") == Error.Ok) { img.GenerateMipmaps(); mat.AlbedoTexture = ImageTexture.CreateFromImage(img); mat.TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps; }
                root.AddChild(new MeshInstance3D { Mesh = m, MaterialOverride = mat });
            }
            return root;
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
            bool night = System.Environment.GetEnvironmentVariable("UG_NIGHT") == "1";   // UG_NIGHT=1: dim sun+sky to verify the caustics FADE at night (don't glow nuclear)
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = night ? new Color(0.02f, 0.03f, 0.06f) : new Color(0.42f, 0.58f, 0.75f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = night ? new Color(0.05f, 0.06f, 0.11f) : new Color(0.62f, 0.64f, 0.68f), AmbientLightEnergy = night ? 0.3f : 0.95f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-52f, -42f, 0f), LightEnergy = night ? 0.04f : 1.1f, ShadowEnabled = true });

            bool planeGround = System.Environment.GetEnvironmentVariable("UG_PLANEGROUND") == "1";   // LAND-plane test: a solid runway at Y=0 instead of water (jet/wheeled planes)
            Terrain.HasWater = !planeGround; Terrain.SeaLevelY = 0f;   // flat test sea at Y=0 -- the boat physics reads these
            // UG_WATERFAR=1: shove the plane ~2.6k units out to fake the REAL map's large world coords (where the
            // sin-hash noise degraded into a grid) -- so the test reproduces the real-map condition, not a near-origin one.
            Vector3 farOff = System.Environment.GetEnvironmentVariable("UG_WATERFAR") == "1" ? new Vector3(2600f, 0f, 2600f) : Vector3.Zero;
            var water = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(800f, 800f), SubdivideWidth = 160, SubdivideDepth = 160 }, Position = farOff,   // 160 = ~5 m quads = the REAL map's density, so the boattest is an honest test (not a flattering fine mesh)
                MaterialOverride = new ShaderMaterial { Shader = GD.Load<Shader>("res://content/water.gdshader") } };   // wave/foam shader (master)
            AddChild(water);
            if (planeGround) water.Visible = false;   // runway test: no sea
            // caustics projected onto the underwater surfaces, seeded from the same wave noise (master 2026-08-16)
            var caustShader = GD.Load<Shader>("res://content/caustics_ground.gdshader");
            ShaderMaterial Caust(Color c) { var m = new ShaderMaterial { Shader = caustShader }; m.SetShaderParameter("base_color", c); m.SetShaderParameter("sea_level", Terrain.SeaLevelY); return m; }
            // seabed doubles as the RUNWAY for the land-plane test (raised to Y=0, grey tarmac); else the deep boat floor.
            // UG_PLANESLOPE tilts it into a SLOPE -> reproduce the real terrain (where the plane slides/freaks), not flat.
            var seabed = new StaticBody3D { Position = new Vector3(0f, planeGround ? 0f : -14f, 0f),
                RotationDegrees = System.Environment.GetEnvironmentVariable("UG_PLANESLOPE") == "1" ? new Vector3(0f, 0f, float.TryParse(System.Environment.GetEnvironmentVariable("UG_SLOPEDEG"), out var _sd) ? _sd : 11f) : Vector3.Zero };
            seabed.AddChild(new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(800f, 800f) },
                MaterialOverride = planeGround ? new StandardMaterial3D { AlbedoColor = new Color(0.30f, 0.30f, 0.33f) } : (Material)Caust(new Color(0.22f, 0.26f, 0.20f)) });
            seabed.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
            AddChild(seabed);
            if (System.Environment.GetEnvironmentVariable("UG_ROUGH") == "1")
            {   // a slightly-rough HEIGHTMAP under the taxi area -> reproduce the map terrain that the flat plane cannot (wheel chatter)
                int _N = 129; float _cell = 1.2f, _amp = 0.06f;
                var _hd = new float[_N * _N];
                for (int _j = 0; _j < _N; _j++) for (int _i = 0; _i < _N; _i++) _hd[_j * _N + _i] = _amp * (Mathf.Sin(_i * 2.3f + _j * 0.4f) + 0.6f * Mathf.Sin(_i * 4.7f - _j * 1.1f) + 0.5f * Mathf.Cos(_j * 3.1f + _i * 0.6f));
                var _hm = new HeightMapShape3D { MapWidth = _N, MapDepth = _N }; _hm.MapData = _hd;
                var _rough = new StaticBody3D { Position = new Vector3(0f, 0.3f, 60f) };
                _rough.AddChild(new CollisionShape3D { Shape = _hm, Scale = new Vector3(_cell, 1f, _cell) });
                AddChild(_rough);
            }
            if (System.Environment.GetEnvironmentVariable("UG_NOSEBUMP") == "1")
            {   // a RISE under the nose-wheel spawn spot (world ~0,*,57.2) to reproduce single-point seating burying the nose
                var bump = new StaticBody3D { Position = new Vector3(0f, 0.4f, 57.17f) };
                bump.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(1.4f, 0.8f, 1.4f) } });
                bump.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(1.4f, 0.8f, 1.4f) }, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.5f, 0.32f, 0.2f) } });
                AddChild(bump);
            }

            _veh = Vehicle.BuildByName(type, int.TryParse(System.Environment.GetEnvironmentVariable("UG_SHIPVARIANT"), out var _sv) ? _sv : 0);   // UG_SHIPVARIANT: pick the spawn paint variant -> show the random hull-bottom colours
            _veh.Position = new Vector3(0f, 0.5f, 0f);   // spawn just above the waterline -> gentle settle (a 2.5m drop plunged the voxel-buoyancy hull deep + made it bob)
            AddChild(_veh);
            _veh.EngineOn = true;
            if (System.Environment.GetEnvironmentVariable("UG_BEACH") == "1")   // AMPHIBIOUS transition: a sandy beach sloping from dry land (+Z) down into the sea (-Z) -> drive off it into the water
            {
                var ramp = new StaticBody3D { Position = new Vector3(0f, -1f, 14f), RotationDegrees = new Vector3(12f, 0f, 0f) };   // +Z end rises above the sea, -Z end dips under
                ramp.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(40f, 2f, 44f) }, MaterialOverride = Caust(new Color(0.66f, 0.58f, 0.42f)) });
                ramp.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(40f, 2f, 44f) } });
                AddChild(ramp);
                _veh.Position = new Vector3(0f, 6f, 26f);   // start up on the dry land, facing -Z toward the sea
            }

            _vehCam = new Camera3D { Current = true, Fov = 58f };
            _vehCam.CullMask &= ~OutlineOverlay.OutlineLayer;
            AddChild(_vehCam);
            _vehTest = true;   // reuse the vehTest auto-drive + chase-cam loop (Drive() -> the boat's water propulsion)

            if (System.Environment.GetEnvironmentVariable("UG_SHIPSHOW") == "1")
            {   // BIG SHIP showcase: keep the ship, kill auto-drive, hold a wide 3/4 aerial cam far enough back to
                // frame the whole 67.5m hull afloat (the boattest chase cam buries itself inside a hull this big).
                _vehTest = System.Environment.GetEnvironmentVariable("UG_SHIPDRIVE") == "1";   // UG_SHIPDRIVE=1 -> let it auto-drive (moving clip); else static float
                GetWindow().Size = new Vector2I(1280, 720);
                Vector3 shipCam = new Vector3(44f, 20f, 42f);   // UG_SHIPCAM="x,y,z" overrides -> iterate framing without a rebuild
                string _sc = System.Environment.GetEnvironmentVariable("UG_SHIPCAM");
                if (!string.IsNullOrEmpty(_sc)) { var a = _sc.Split(','); shipCam = new Vector3(float.Parse(a[0]), float.Parse(a[1]), float.Parse(a[2])); }
                Vector3 lookAt = new Vector3(0f, 6f, 0f);
                if (System.Environment.GetEnvironmentVariable("UG_SHIPREF") == "1")
                {   // STATIC reference hull at the retail PEI Alberton draft (keel 4.8m below sea) BESIDE the floating one -> "ours vs pei's" waterline compare
                    var refMi = new MeshInstance3D { Mesh = ContentProvider.ParseObj("res://content/ship_body.txt"), Position = new Vector3(34f, Terrain.SeaLevelY - 4.8f, 0f) };
                    var refMat = new StandardMaterial3D { CullMode = BaseMaterial3D.CullModeEnum.Disabled, TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest };
                    var refImg = new Image();
                    if (refImg.Load(ProjectSettings.GlobalizePath("res://content/ship_body_tex.png")) == Error.Ok) refMat.AlbedoTexture = ImageTexture.CreateFromImage(refImg);
                    refMi.MaterialOverride = refMat; AddChild(refMi);
                    shipCam = new Vector3(17f, 22f, 62f); lookAt = new Vector3(17f, 4f, 0f);   // frame BOTH: floating @X0, static ref @X34
                }
                _vehCam.LookAtFromPosition(shipCam, lookAt, Vector3.Up);
            }
            if (System.Environment.GetEnvironmentVariable("UG_PLANETEST") == "1")
            {   // FLYABLE-PLANE showcase: script the fixed-wing controls (full throttle, rotate, then bank) + a
                // world-up chase cam, so a --write-movie clip shows the water takeoff + the bank-to-turn flight model.
                _vehTest = false; _planeTest = true;
                GetWindow().Size = new Vector2I(1280, 720);
                _veh.Position = new Vector3(0f, planeGround ? 1.0f : 0.6f, 60f);   // runway: a touch above so it drops onto its wheels; water: on the sea. Long -Z runway ahead
                _veh.ResetPhysicsInterpolation();   // don't smear from the origin on frame 1 (the WorldItem lesson)
                _rigCaptureFrames = new[] { 60, 260, 460, 660, 860, 1000 };   // stretch the harness's auto-quit out; render this at --fixed-fps 50 (== the 50Hz physics) so every movie frame is exactly ONE physics tick -> perfectly even motion, no 30/50 sampling judder
            }
            if (System.Environment.GetEnvironmentVariable("UG_WATERSHOW") == "1")
            {   // clean open-water scroll showcase for a --write-movie clip: ditch the boat + auto-drive, hold a
                // static low camera skimming the sea so the swell + foam visibly SCROLL past (no boat/cam motion to mask it).
                if (_veh != null) { _veh.QueueFree(); _veh = null; }
                _vehTest = false;
                GetWindow().Size = new Vector2I(1280, 720);
                if (System.Environment.GetEnvironmentVariable("UG_SHOREEYE") == "1")
                    // EYE-HEIGHT looking across the sea to the horizon = the exact "standing at the shore" grazing view master flagged (busy+opaque low-angle)
                    _vehCam.LookAtFromPosition(new Vector3(0f, 1.8f, 0f) + farOff, new Vector3(0f, 1.4f, -100f) + farOff, Vector3.Up);
                else if (System.Environment.GetEnvironmentVariable("UG_BEACH") == "1")
                    // look DOWN at the shoreline (the ramp's waterline sits ~Z=14) so the shore-foam band + lapping read clearly
                    _vehCam.LookAtFromPosition(new Vector3(0f, 12f, 40f), new Vector3(0f, -1f, 6f), Vector3.Up);
                else
                    _vehCam.LookAtFromPosition(new Vector3(0f, 3.2f, 34f) + farOff, new Vector3(0f, 0.6f, -50f) + farOff, Vector3.Up);
            }
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

            // UG_HELITEST=1 (with --vehicle=DIR --gun=minicopter|scoutcopter|huey|hind|orca):
            // SCRIPTED ROTARY FLIGHT. Every other render mode drives a ground vehicle or a boat, so until this
            // existed NOTHING in the harness could fly a helicopter -- DriveHeli was reachable only from the L1
            // tests and from a human at the stick. That is why the minicopter was flight-tested by hand for a
            // whole night: there was no other way to SEE it move. A physics change could pass every test and
            // still look wrong, and nobody would find out until someone flew it.
            //
            // Driven off the aircraft's own STATE rather than a frame count, same as UG_PLANETEST: altitude and
            // speed decide the phase, so the flight is identical at any --fixed-fps and the clip does not
            // desync from the physics when the capture rate changes.
            if (System.Environment.GetEnvironmentVariable("UG_HELITEST") == "1" && _veh != null && _veh.IsHeli)
            {
                _heliTest = true; _vehTest = false;
                GetWindow().Size = new Vector2I(1280, 720);
                _veh.EngineOn = true; _veh.DebugInstantStart = true; _veh.SpawnRotorRunning();   // skip the spool-up: the clip is about FLIGHT
                _veh.DebugNoTurbulence = System.Environment.GetEnvironmentVariable("UG_HELITURB") != "1";   // steady by default; UG_HELITURB=1 to show the turbulence model
                _veh.Position = new Vector3(0f, 0.9f, 0f);
                _veh.ResetPhysicsInterpolation();   // or frame 1 smears in from the origin
                // Long enough to climb out, translate, and come round -- render at --fixed-fps 50 to match the
                // 50 Hz physics tick so every movie frame is exactly one tick (no 30/50 sampling judder).
                _rigCaptureFrames = new[] { 40, 150, 300, 450, 600, 750 };
            }

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
            // Weather is now SCHEDULED off PEI's real Weather_Types table instead of a one-shot coin flip at
            // world build -- forecast, fade in, hold, fade out, repeat (src LightingManager). The overlay is the
            // same shader; WeatherManager just owns whether and how hard it rains.
            var rain = new RainOverlay { Cycle = dn, Raining = false };
            AddChild(rain);
            WeatherManager.Attach(this, rain, dn);

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
                var freezeMode = new FreezeMode();   // ESC -> Freeze Mode: paused sim + freecam + single-tick stepping
                AddChild(freezeMode);
                var pause = new PauseMenu();   // ESC -> pause menu (freezes the sim)
                pause.Freeze = freezeMode;
                pause.WorldRoot = this;
                AddChild(pause);
                player.PauseMenu = pause;
                AddChild(new Profiler());   // console `profiler` -> perf overlay (fps/frame/worst-frame/timings/draw-calls/mem) for stutter diagnosis (master)
                AddChild(new ZombieAnimCut());   // F6 -> freeze ALL rig anim (skeletons leg of the engine-side POI-fps cut: read F3 physics ms with it on vs off)
                var attach = new AttachmentMenu();   // T -> weapon-attachment menu (iron sights removable, etc.)
                AddChild(attach);
                player.AttachMenu = attach;
                var ammoRadial = new AmmoRadial();   // R-hold -> shotgun ammo-type picker (buckshot / slug)
                AddChild(ammoRadial);
                player.AmmoRadial = ammoRadial;
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

            // UG_HITWALL: a concrete wall 18 m downrange in the player's default (+Z) fire direction, so the firetest
            // reproduces shooting a hard surface at PLAY DISTANCE -> diagnose the real in-game bullet-impact FX (the
            // --impacttest harness fired point-blank, which never actually exercised the frustum-cull path). Tagged
            // Concrete so it takes the debris burst + decal.
            if (System.Environment.GetEnvironmentVariable("UG_HITWALL") == "1")
            {
                var wall = new StaticBody3D { CollisionLayer = 1 << 0 };
                wall.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(12f, 8f, 0.5f) } });
                var wm = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(12f, 8f, 0.5f) } };
                wm.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.60f, 0.60f, 0.62f) };
                wall.AddChild(wm);
                AddChild(wall);
                wall.GlobalPosition = new Vector3(0, 2.5f, 18f);
                wall.SetMeta(PlayerController.SurfMeta, (int)PlayerController.Surf.Concrete);
                GD.Print("[FIRETEST] UG_HITWALL: concrete wall at +Z 18 m (player fires into it)");
            }

            // UG_HITGLASS: a full window-sized DESTRUCTIBLE glass pane 6 m downrange -> the player shoots it + it shatters
            // into Glass_0 shards. Close so the shatter reads clearly.
            //
            // UG_HITGLASS_HP exists because at the stock Health 1 THIS HARNESS CANNOT SHOW THE SHARDS. The pane
            // breaks on the first bullet (frame 60) and the shards live ~1.2s, but the firetest only captures
            // once ammo <= 20 and frame >= 75 -- it fires every 15 frames from 60, so that is frame ~195. The
            // capture lands ~135 frames after the glass is gone and photographs an empty space whether the
            // shards work or not. Give the pane enough health to survive to the capture frame and it is a real
            // verification instead of a picture of nothing.
            //
            // It is a knob rather than a computed number because a bullet deals the GUN's ObjectDamage, not 1,
            // so "survives nine shots" is not something to derive on paper -- the shatter frame is printed
            // below so one run tells you what to set.
            if (System.Environment.GetEnvironmentVariable("UG_HITGLASS") == "1")
            {
                float ghp = 1f;
                if (float.TryParse(System.Environment.GetEnvironmentVariable("UG_HITGLASS_HP"),
                                   System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out float hpv) && hpv > 0f)
                    ghp = hpv;
                var pane = GlassPane.Build(new Vector2(1.2f, 1.5f), hp: ghp);
                pane.OnShattered += () => GD.Print($"[FIRETEST] glass SHATTERED at frame {_ftFrame} (capture wants ~195)");
                AddChild(pane);
                pane.GlobalPosition = new Vector3(0f, 1.5f, 6f);
                GD.Print($"[FIRETEST] UG_HITGLASS: destructible glass pane at +Z 6 m, hp {ghp:0.#} (player shatters it)");
            }
            env.TonemapMode = Godot.Environment.ToneMapper.Aces;   // match the game's ACES so this harness validates the scope PiP color/tonemap (was default Linear)
            GD.Print($"[FIRETEST] suppressed={suppressed} -- firing away from a zombie 25 m off; expect [ALERT] ONLY when unsuppressed");
        }

        // --craftmenu: open the NEWER CraftingMenu (the browsable recipe index wired to the player as _craftMenu / Y)
        // over a bag stocked with metal scrap + a blowtorch + our tree logs, so the recipe list + craftability render.
        void BuildCraftMenu()
        {
            SDG.Unturned.ItemCatalog.RegisterAll();
            BlueprintRegistry.Load();
            var inv = new SDG.Unturned.PlayerInventory();
            inv.tryAddItem(new SDG.Unturned.Item(67, 200));   // Metal Scrap x200
            inv.tryAddItem(new SDG.Unturned.Item(76, 1));     // Blowtorch (tool, not consumed)
            inv.tryAddItem(new SDG.Unturned.Item(37, 40));    // Birch Log (our tree drops)
            inv.tryAddItem(new SDG.Unturned.Item(39, 40));    // Maple Log
            inv.tryAddItem(new SDG.Unturned.Item(41, 40));    // Pine Log
            var menu = new CraftingMenu { Inv = inv };
            AddChild(menu);
            menu.Open();
            if (System.Environment.GetEnvironmentVariable("UG_CRAFTQUEUE") == "1") menu.DebugQueueCraftable(3, 3);   // populate the queue for the shot
            GD.Print("[CRAFTMENU] opened the newer CraftingMenu over a stocked inventory");
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
        /// <summary>CAN A SHIPPING CONTAINER RIDE IN THE SKYCRANE? Two aircraft side by side, same container:
        /// left has it sat in the leg bay, right has it slung underneath on a line.
        ///
        /// Built because the numbers alone ("6.88 m of bay against a 7.50 m box") are the sort of answer that
        /// is easy to nod at and hard to picture. The left-hand aircraft shows the overhang at the scale it
        /// actually happens; the right-hand one shows what the alternative looks like in the air.</summary>
        void BuildSlingTest()
        {
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.46f, 0.58f, 0.72f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.72f, 0.74f, 0.78f), AmbientLightEnergy = 1.0f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-48f, -38f, 0f), LightEnergy = 1.25f, ShadowEnabled = true });
            var ground = new StaticBody3D { CollisionLayer = 1 << 0 };
            ground.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
            AddChild(ground);
            AddChild(new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(400f, 400f) }, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.36f, 0.42f, 0.30f), Roughness = 1f } });

            string dir = ProjectSettings.GlobalizePath("res://content/objects/");
            var cmesh = ObjMesh.Load(dir + "Container_0.obj");
            var cmat = new StandardMaterial3D { Roughness = 0.85f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            var cimg = new Image();
            if (cimg.Load(dir + "Container_0_tex.png") == Error.Ok) { cimg.GenerateMipmaps(); cmat.AlbedoTexture = ImageTexture.CreateFromImage(cimg); }
            var cab = cmesh.GetAabb();
            float cW = cab.Size.X, cH = cab.Size.Z, cL = cab.Size.Y;   // Z-up prop: mesh Z is height, mesh Y is length

            // The skycrane's own numbers, measured off skycrane_body.txt rather than eyeballed:
            const float LegBottom = -0.63f, LegZFrom = -4.15f, LegZTo = 2.73f, CockpitRearZ = -1.0f;
            // LOCAL to the heli, not world: 1.88 is a mesh coordinate off skycrane_body.txt, and the aircraft's
            // ORIGIN stands 0.63 m up (-LegBottom) so the gear reaches the deck. Using it as a world Y made every
            // clip/burial figure 0.63 m too pessimistic -- the "1.37 m buried" answer is really 0.73 m. The
            // [SLING/REAL] vertex scan below re-derives this from the actual mesh; keep them agreeing.
            const float BellyLocal = 1.88f;   // lowest fuselage over the container footprint (|X|<1.44, Z -0.70..6.80, above the struts)
            bool touch = System.Environment.GetEnvironmentVariable("UG_SLING_TOUCH") == "1";
            float yaw = float.TryParse(System.Environment.GetEnvironmentVariable("UG_SLING_YAW"), out var y0) ? y0 : 180f;
            float gap = float.TryParse(System.Environment.GetEnvironmentVariable("UG_SLING_GAP"), out var g0) ? g0 : 0.30f;
            // Doors aft (yaw 180), forward face gapped off the back of the cockpit, base on the ground.
            float frontZ = CockpitRearZ + gap;
            float centreZ = frontZ + cL * 0.5f;
            var basis = new Basis(Vector3.Up, Mathf.DegToRad(yaw)) * new Basis(Vector3.Right, Mathf.DegToRad(270f));
            // UG_SLING_TOUCH=1: drop the container until its TOP meets the underside above it. Note the
            // limiting surface is the BELLY at 1.88 over this footprint, not the tail boom (2.39) -- so
            // "touching the bottom of the tail" would leave it intersecting the fuselage further forward.
            // UG_SLING_RAISE: lift the container off the no-clip height. Above 0 it starts cutting into the
            // belly, and by exactly this much -- so the knob doubles as the readout for how much clearance
            // the aircraft would have to gain to carry it at that height.
            float raise = float.TryParse(System.Environment.GetEnvironmentVariable("UG_SLING_RAISE"), out var r0) ? r0 : 0f;
            // UG_SLING_FLY: lift the WHOLE assembly -- aircraft and load together -- clear of the deck, so how
            // far the container hangs below the airframe is visible against the sky instead of buried.
            float fly = float.TryParse(System.Environment.GetEnvironmentVariable("UG_SLING_FLY"), out var f0) ? f0 : 0f;
            float heliOriginY = -LegBottom + fly;          // where the aircraft's own origin ends up
            float deckY = fly, underY = heliOriginY + BellyLocal;   // gear plane, and the belly above it
            float baseY = (touch ? underY - cH : deckY) + raise;
            // THE MESH ORIGIN IS THE CONTAINER'S BASE, NOT ITS CENTRE -- mesh Z runs 0.000..3.243, so after the
            // Z-up->Y-up rotation the origin sits on the floor of the box. Adding cH/2 here (as if it were
            // centred, which it is in X and Y) lifted the whole thing 1.62 m and put the top at 3.50 against a
            // 1.88 underside. It looked plausible and the arithmetic downstream was all consistent with it;
            // strawberry caught it by eye. Place the ORIGIN at the base and let the mesh extend upward.
            var cinst = new MeshInstance3D { Mesh = cmesh, MaterialOverride = cmat, Transform = new Transform3D(basis, new Vector3(0f, baseY, centreZ)) };
            AddChild(cinst);
            // Report RELATIVE to the airframe, not to the world -- UG_SLING_FLY moves the aircraft too, so a
            // load measured against a deck that slid out from under it stays articulate and answers the wrong question.
            if (touch) GD.Print($"[SLING] base Y {baseY:0.00}, top {baseY + cH:0.00} vs underside {underY:0.00} -> {(baseY + cH > underY ? $"CLIPS by {baseY + cH - underY:0.00} m" : "clear")}; sits {deckY - baseY:0.00} m below the deck; ground clearance {baseY:0.00} m");

            // UG_SLING_TOUCH=1: raise the aircraft until the container's TOP meets its underside -- strawberry's
            // "lower the container so its top touches the bottom of the tail", done the way round that keeps the
            // box on the ground. The limiting surface is measured over the container's own footprint rather than
            // assumed to be the tail: it is actually the BELLY at Y 1.88 (Z 0..2.5); the boom behind it sits
            // higher at 2.39, so aiming at the tail would have buried the box in the fuselage.
            float lift = 0f;   // ghost-gear variant only; the container itself moves by `drop`
            var heli = Vehicle.BuildByName("skycrane");
            AddChild(heli);
            heli.GlobalPosition = new Vector3(0f, heliOriginY + lift, 0f);   // legs on the deck, plus any lift
            // HOLD IT UP PROPERLY. Freeze alone does not survive: the machine's own idle logic clears it
            // (Vehicle.cs "else if (!idle && Freeze) Freeze = false"), so the aircraft quietly fell out from under
            // the load -- 7.63 -> 5.85 in half a second -- while every build-frame number stayed true. At ground
            // level it just landed where I wanted it and the bug was invisible; only flying it up exposed it.
            // Disabling the script is what makes the freeze stick, since nothing is left running to undo it.
            heli.FreezeMode = RigidBody3D.FreezeModeEnum.Static; heli.Freeze = true;
            heli.GravityScale = 0f; heli.LinearVelocity = Vector3.Zero; heli.AngularVelocity = Vector3.Zero;
            heli.ProcessMode = Node.ProcessModeEnum.Disabled;
            if (touch) GD.Print($"[SLING] raised {lift:0.00} m so the container's top meets the underside -> legs {(-LegBottom) + lift:0.00} m tall (were {-LegBottom:0.00})");

            // GROUND TRUTH, read back off the scene rather than off my own intent. Every number above is a
            // prediction; these two are what actually gathered in the world. A placement that validates against
            // the value it was told to use cannot detect a frame mix-up, which is exactly what bit this harness.
            Aabb WorldAabb(Node n)
            {
                Aabb? acc = null;
                void Walk(Node k)
                {
                    if (k is MeshInstance3D mi && mi.Mesh != null && mi.Visible)
                    {
                        var a = mi.GlobalTransform * mi.Mesh.GetAabb();
                        acc = acc.HasValue ? acc.Value.Merge(a) : a;
                    }
                    foreach (var c in k.GetChildren()) Walk(c);
                }
                Walk(n);
                return acc ?? new Aabb();
            }
            var cA = cinst.GlobalTransform * cmesh.GetAabb();
            var hA = WorldAabb(heli);
            GD.Print($"[SLING/REAL] container Y {cA.Position.Y:0.00}..{cA.End.Y:0.00}  Z {cA.Position.Z:0.00}..{cA.End.Z:0.00}  X {cA.Position.X:0.00}..{cA.End.X:0.00}");
            GD.Print($"[SLING/REAL] heli      Y {hA.Position.Y:0.00}..{hA.End.Y:0.00}  Z {hA.Position.Z:0.00}..{hA.End.Z:0.00}  (origin Y {heli.GlobalPosition.Y:0.00})");
            GD.Print($"[SLING/REAL] container base is {hA.Position.Y - cA.Position.Y:0.00} m below the lowest point of the aircraft (its gear)");
            // RE-DERIVE the belly from real vertices instead of trusting the number typed at the top. Scan every
            // heli vertex that lies over the container footprint, drop anything at/below the gear plane, and take
            // the lowest survivor -- that IS the surface the load would hit.
            {
                float gearTop = hA.Position.Y + 0.75f;   // above the skids/struts, so they do not win the minimum
                float lo = float.MaxValue; string who = "none";
                void Scan(Node k)
                {
                    if (k is MeshInstance3D mi && mi.Mesh != null && mi.Visible)
                        for (int si = 0; si < mi.Mesh.GetSurfaceCount(); si++)
                        {
                            var arr = mi.Mesh.SurfaceGetArrays(si);
                            if (arr.Count == 0 || arr[(int)Mesh.ArrayType.Vertex].VariantType == Variant.Type.Nil) continue;
                            foreach (var lv in arr[(int)Mesh.ArrayType.Vertex].AsVector3Array())
                            {
                                var w = mi.GlobalTransform * lv;
                                if (w.X < cA.Position.X || w.X > cA.End.X) continue;
                                if (w.Z < cA.Position.Z || w.Z > cA.End.Z) continue;
                                if (w.Y <= gearTop || w.Y >= lo) continue;
                                lo = w.Y; who = mi.Name;
                            }
                        }
                    foreach (var c in k.GetChildren()) Scan(c);
                }
                Scan(heli);
                // Enumerate EVERY visual node with its own Y range. The union AABB above hides which node owns the
                // minimum, and a walker that only knows MeshInstance3D is blind to anything drawn another way --
                // so list node TYPES too, and let the render and the numbers be checked against each other.
                void List(Node k, string ind)
                {
                    string extra = "";
                    if (k is VisualInstance3D vi)
                    {
                        var a = vi.GlobalTransform * vi.GetAabb();
                        extra = $"  Y {a.Position.Y:0.00}..{a.End.Y:0.00}  vis={vi.Visible}";
                    }
                    GD.Print($"[SLING/NODE] {ind}{k.Name} <{k.GetType().Name}>{extra}");
                    foreach (var c in k.GetChildren()) List(c, ind + "  ");
                }
                List(heli, "");
                // AND AGAIN LATER. Everything above is measured on the BUILD frame, before physics has run once.
                // The aircraft is a RigidBody3D; if the freeze does not hold it, it falls out of the picture and
                // every build-frame number stays true while the render shows something else entirely.
                foreach (double t in new[] { 0.1, 0.5, 1.0 })
                {
                    var when = t;
                    GetTree().CreateTimer(when).Timeout += () =>
                        GD.Print($"[SLING/LATE {when:0.0}s] heli origin Y {heli.GlobalPosition.Y:0.00} (built at {heliOriginY:0.00}), frozen={heli.Freeze}; container base Y {cinst.GlobalPosition.Y:0.00}");
                }
                GD.Print($"[SLING/REAL] belly over the footprint: world Y {lo:0.00} (local {lo - heli.GlobalPosition.Y:0.00}) on \"{who}\"; harness assumed world {underY:0.00}");
                GD.Print($"[SLING/REAL] container top {cA.End.Y:0.00} -> {(cA.End.Y > lo ? $"CLIPS by {cA.End.Y - lo:0.00} m" : $"clear by {lo - cA.End.Y:0.00} m")}");
            }

            float overhang = (centreZ + cL * 0.5f) - LegZTo;
            GD.Print($"[SLING] container W {cW:0.00} H {cH:0.00} L {cL:0.00}; front face Z {frontZ:0.00} (cockpit rear {CockpitRearZ:0.00} + {gap:0.00} gap), rear face Z {centreZ + cL * 0.5f:0.00}");
            GD.Print($"[SLING] legs span Z {LegZFrom:0.00}..{LegZTo:0.00} -> container overhangs the gear by {overhang:0.00} m aft");

            // UG_SLING_LEGS=1: draw what the gear WOULD have to become to carry it -- extended aft to the
            // container's rear face. Ghosted rather than modelled, since this is a question, not a change.
            if (System.Environment.GetEnvironmentVariable("UG_SLING_LEGS") == "1")
            {
                float newTo = centreZ + cL * 0.5f + 0.4f, len = newTo - LegZFrom;
                var gm = new StandardMaterial3D { AlbedoColor = new Color(1f, 0.55f, 0.1f, 0.6f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha };
                foreach (float sx in new[] { -2.065f, 2.065f })
                {
                    AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.60f, 0.20f, len) }, MaterialOverride = gm, Position = new Vector3(sx, 0.10f, (LegZFrom + newTo) * 0.5f) });   // the longer skid
                    foreach (float lz in new[] { LegZFrom + 0.8f, newTo - 0.8f })   // the taller struts up to the hull
                        AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.34f, -LegBottom + lift, 0.34f) }, MaterialOverride = gm, Position = new Vector3(sx, (-LegBottom + lift) * 0.5f, lz) });
                }
                GD.Print($"[SLING] ghost gear: {len:0.00} m long (was {LegZTo - LegZFrom:0.00}), {-LegBottom + lift:0.00} m tall (was {-LegBottom:0.00})");
            }

            // DATUM: a thin translucent slab at the gear plane. The question is "how much sits below the aircraft",
            // and screen-Y across a perspective view cannot answer it -- I misread exactly that off the last render.
            // A plane the container visibly pierces makes the overhang legible instead of inferred.
            AddChild(new MeshInstance3D {
                Mesh = new BoxMesh { Size = new Vector3(11f, 0.03f, 15f) },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(1f, 0.5f, 0.1f, 0.35f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, CullMode = BaseMaterial3D.CullModeEnum.Disabled },
                Position = new Vector3(0f, deckY, 1.5f) });

            var cam = new Camera3D { Current = true, Fov = 42f, Far = 4000f };
            AddChild(cam);
            string mode = System.Environment.GetEnvironmentVariable("UG_SLING_CAM");
            // Side view sits JUST BELOW the gear plane looking slightly up, so the part of the load hanging under
            // the aircraft is silhouetted rather than foreshortened into the fuselage behind it.
            cam.Position = mode == "side" ? new Vector3(23f, fly - 0.8f, 2.5f) : new Vector3(16f, 7.5f + fly, -15f);
            cam.LookAt(new Vector3(0f, fly + 0.9f, 2.5f), Vector3.Up);
        }

        // --magnettest=OUT: the sky-crane's winch + electromagnet on a TEST STAND. The aircraft is held on a gantry
        // (velocity zeroed each tick, gravity off) rather than flown, so this measures the CABLE and the MAGNET without
        // the flight model in the loop -- deliberately NOT a flight test, and it should not be quoted as one. What it
        // does prove is the part that can silently not work: deploy -> dangle taut at cable length -> energise -> bite a
        // load -> and RAISE it, which is the only step whose failure looks exactly like success in a screenshot.
        //
        // Phased on elapsed time: settle, energise, then hoist the gantry and check the load's height went UP.
        partial class MagnetGantry : Node3D
        {
            public Vehicle Heli; public float HoldY; public RigidBody3D Load; public Camera3D Cam;
            public float T; public int Phase; public float LoadY0 = float.NaN; public bool Reported;
            public override void _PhysicsProcess(double delta)
            {
                if (Heli == null || !IsInstanceValid(Heli)) return;
                // NOTE: this rig deliberately does NOT fly the aircraft. An earlier version added a UG_MAG_FLY mode
                // that drove full collective on the real flight model -- and its CONTROL (no magnet at all) sank at
                // -27 m/s just like the subject, so it could not distinguish "the magnet is too heavy" from "the rig
                // is broken", which is the one thing a control exists to do. It was removed rather than left lying
                // around looking authoritative. Whether the crane can actually CARRY the thing is answered by the
                // vehicle.heli_sling suite, which flies the real model and has a magnet-free control pair.
                // Keep BOTH ends in frame: the aircraft climbs away from a load that starts on the ground, so a fixed
                // camera loses one or the other. Frame the midpoint and pull back with the separation.
                if (Cam != null && IsInstanceValid(Cam))
                {
                    float lowY = Heli.Sling != null && IsInstanceValid(Heli.Sling) ? Heli.Sling.GlobalPosition.Y - 2.5f : 0f;
                    float midY = (Heli.GlobalPosition.Y + 3f + lowY) * 0.5f;
                    float span = Mathf.Max(12f, (Heli.GlobalPosition.Y + 3f) - lowY);
                    Cam.Position = new Vector3(span * 1.55f, midY + span * 0.10f, span * 0.62f);
                    Cam.LookAt(new Vector3(0f, midY, 1.0f), Vector3.Up);
                }
                T += (float)delta;
                // The stand: pin the airframe where it is put. Runs every tick so the winch reaction cannot walk it.
                // Pin ATTITUDE as well as position. The cable pulls at an anchor offset from the CoM, so it torques
                // the airframe; zeroing angular velocity alone still lets the tilt accumulate a little each tick and
                // the stand ends up hanging visibly banked. A gantry holds a machine level -- that the cable wants to
                // tip it is a real result, but it belongs to a FLIGHT test, not to this one.
                Heli.GlobalTransform = new Transform3D(Basis.Identity, new Vector3(0f, HoldY, 0f));
                Heli.LinearVelocity = Vector3.Zero; Heli.AngularVelocity = Vector3.Zero;
                var mag = Heli.Sling;
                // Record the load's start height the INSTANT it is grabbed, not on a clock. Sampling at a fixed
                // time meant any change that shifted the grab later (the bridle settling the coil differently)
                // silently left LoadY0 = NaN and printed "DOES NOT LIFT" for a run that lifted perfectly well.
                if (float.IsNaN(LoadY0) && mag != null && IsInstanceValid(mag) && mag.Held != null) LoadY0 = mag.Held.GlobalPosition.Y;
                if (Phase == 0 && T > 1.2f)   // settled -> energise the coil
                {
                    // CONTROL (UG_MAG_NOENERGISE=1): skip the toggle. The rig must then report DOES NOT LIFT --
                    // otherwise the verdict is measuring something other than the magnet (the load shoved by the
                    // falling coil, say) and a PASS would look identical to a real one.
                    if (System.Environment.GetEnvironmentVariable("UG_MAG_NOENERGISE") != "1") Heli.ToggleSlingMagnet();
                    Phase = 1;
                    GD.Print($"[MAGNET] energised at t={T:0.00}; deployed={Heli.SlingDeployed}");
                }
                else if (Phase == 1 && T > 3.4f)   // give it time to bite, then hoist
                {
                    Phase = 2;
                    GD.Print($"[MAGNET] grab: held={(mag?.Held != null ? mag.Held.Name.ToString() : "NOTHING")}; loadY0={LoadY0:0.00}");
                }
                else if (Phase == 2 && T > 2.7f) { HoldY += 2.2f * (float)delta; }   // hoist the whole stand
                if (Phase == 2 && T > 5.0f && !Reported)
                {
                    Reported = true;
                    float lifted = (mag?.Held != null && !float.IsNaN(LoadY0)) ? mag.Held.GlobalPosition.Y - LoadY0 : float.NaN;
                    GD.Print($"[MAGNET] t={T:0.00} heli {HoldY:0.00}; magnet Y {(mag != null ? mag.GlobalPosition.Y : float.NaN):0.00}; held={(mag?.Held != null ? "YES" : "no")}; load RAISED {lifted:0.00} m");
                    GD.Print($"[MAGNET] VERDICT: {((mag?.Held != null && lifted > 0.5f) ? "LIFTS" : "DOES NOT LIFT")}");
                }
            }
        }

        // --tailcheck: for every helicopter, find where the tail-rotor MOUNTING POST actually is in the mesh and
        // compare it with where the spec puts the hub (strawberry: "check all helis for posts sticking out of their
        // tails, some still have the tail rotor on the wrong side").
        //
        // The post is the geometry that sticks out SIDEWAYS from the tail boom at the hub. The boom and the fin are
        // both roughly centred on X=0, so the giveaway is asymmetry: sample vertices in a box around the hub and ask
        // which side carries the outlying geometry. Reading it off the mesh rather than off a render, because a
        // render answers "does this look wrong" and this needs "which side, by how much, on which airframes".
        // --bellyshot=NAME:OUT -- look UP at one helicopter's underside, so the belly beacon can actually be
        // SEEN rather than inferred from its AABB. The tail camera cannot do this: it always aims at the tail
        // hub, which is 9 m aft of the belly fitting on a Hind, so the beacon is never in frame.
        //
        // This exists because the belly light has now been wrong three separate times -- the Orca built as two
        // overlapping lamps, the whole fleet rotated so the lens faced sideways, and the Hind's lamp measuring
        // 0.338 x 0.288 x 0.256 against a 0.368-square, 0.161-thin fitting everywhere else. Every one of those
        // was a number that looked fine next to a picture nobody had taken. UG_BELLY_CAM="x,y,z" overrides the
        // offset from the belly point; the beacon is force-LIT here so it reads at any exposure.
        void BuildBellyShot(string name)
        {
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.42f, 0.54f, 0.68f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.80f, 0.81f, 0.84f), AmbientLightEnergy = 1.15f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            // Lit from BELOW, because that is the side being inspected and the default key light leaves the
            // underside in its own shadow -- an unlit belly renders as a silhouette and hides the very thing here.
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(58f, -24f, 0f), LightEnergy = 1.25f });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-40f, 150f, 0f), LightEnergy = 0.45f });
            var v = Vehicle.BuildByName(name);
            AddChild(v);
            v.GlobalPosition = Vector3.Zero;
            v.Freeze = true; v.FreezeMode = RigidBody3D.FreezeModeEnum.Static;
            v.ProcessMode = Node.ProcessModeEnum.Disabled;

            Vector3 belly = Vector3.Zero;
            if (v.FindChild("BeaconBelly", false, false) is MeshInstance3D bm)
            {
                belly = bm.Position;
                var ab = bm.Mesh.GetAabb();
                GD.Print($"[BELLYSHOT] {name}: beacon at {belly} size {ab.Size} (X/Z square = a flush panel, thick Y = a lump)");
                // Force it ON: the flasher is driven by rotor rpm and this rig has no running rotor, so an
                // unlit beacon here would be the harness, not the fitting -- exactly the reading I would
                // otherwise have to guess at.
                if (bm.MaterialOverride is StandardMaterial3D mat)
                {
                    mat.EmissionEnabled = true;
                    mat.Emission = new Color(1f, 0.12f, 0.12f);
                    mat.EmissionEnergyMultiplier = 6f;
                    mat.AlbedoColor = new Color(1f, 0.35f, 0.35f);
                }
            }
            else GD.Print($"[BELLYSHOT] {name}: NO BeaconBelly node -- nothing to look at");

            // UG_TURRET_AIM="yaw,pitch" swings the mount before the shot, so "the barrel disappears" can be
            // looked at rather than reasoned about -- a chin turret at full depression is exactly the case where
            // geometry can end up inside its own airframe.
            string ta = System.Environment.GetEnvironmentVariable("UG_TURRET_AIM");
            if (!string.IsNullOrEmpty(ta) && v.Turrets.Length > 0)
            {
                var tp = ta.Split(',');
                if (tp.Length == 2)
                {
                    v.AimTurret(v.Turrets[0].Seat, float.Parse(tp[0]), float.Parse(tp[1]));
                    var mz = v.TurretMuzzle(v.Turrets[0].Seat);
                    GD.Print($"[BELLYSHOT] turret aimed ({tp[0]},{tp[1]}) muzzle {mz} barrel {v.TurretBarrelDir(v.Turrets[0].Seat)}");
                    if (v.FindChild($"TurretPitch{v.Turrets[0].Seat}", true, false) is Node3D pn)
                        foreach (var ch in pn.GetChildren())
                            if (ch is MeshInstance3D pm)
                                GD.Print($"[BELLYSHOT] pitch mesh '{pm.Name}' visible={pm.Visible} aabb {pm.Mesh?.GetAabb().Size} globalY {pm.GlobalPosition.Y:0.00}");
                }
            }

            // Report the CREW and the MOUNTS, so "I can't see a gunner" separates into "none was built" and
            // "one was built inside the floor" -- two different bugs that look identical in a render.
            foreach (var ch in v.GetChildren())
            {
                if (ch is TargetDummy td)
                    GD.Print($"[BELLYSHOT] crew '{td.Name}' local {td.Position} world {td.GlobalPosition} down={td.Down} hp={td.MaxHealth:0}");
                if (ch is Node3D n3 && n3.Name.ToString().StartsWith("TurretYaw"))
                {
                    GD.Print($"[BELLYSHOT] mount '{n3.Name}' local {n3.Position}");
                    foreach (var g2 in n3.GetChildren())
                        if (g2 is Node3D pn2 && pn2.Name.ToString().StartsWith("TurretPitch"))
                            foreach (var m2 in pn2.GetChildren())
                                GD.Print($"[BELLYSHOT]   gun child '{m2.GetType().Name}:{m2.Name}' mesh={(m2 is MeshInstance3D mi2 ? (mi2.Mesh == null ? "NULL" : mi2.Mesh.GetAabb().Size.ToString()) : "-")}");
                }
            }

            var cam = new Camera3D { Current = true, Fov = 42f, Far = 400f };
            AddChild(cam);
            Vector3 off = new Vector3(1.6f, -2.6f, 3.0f);   // below, offset, looking back and up at the belly
            string co = System.Environment.GetEnvironmentVariable("UG_BELLY_CAM");
            if (!string.IsNullOrEmpty(co))
            {
                var cp = co.Split(',');
                if (cp.Length == 3) off = new Vector3(float.Parse(cp[0]), float.Parse(cp[1]), float.Parse(cp[2]));
            }
            // UG_BELLY_LOOK="x,y,z" aims at an arbitrary vehicle-local point instead of the belly, so a nose
            // fitting can be framed side-on rather than glimpsed across the whole airframe.
            Vector3 look = belly;
            string bl = System.Environment.GetEnvironmentVariable("UG_BELLY_LOOK");
            if (!string.IsNullOrEmpty(bl))
            {
                var lp = bl.Split(',');
                if (lp.Length == 3) look = new Vector3(float.Parse(lp[0]), float.Parse(lp[1]), float.Parse(lp[2]));
            }
            cam.Position = look + off;
            cam.LookAt(look, Vector3.Up);
        }

        // --tailshot=NAME:OUT -- frame one helicopter's tail from BEHIND AND ABOVE, close in. The scan reports which
        // side carries the outlying geometry, but "reach at the hub" cannot tell a tail-rotor post from a horizontal
        // stabiliser, and the orca reads symmetric precisely because something spans both ways. So look.
        void BuildTailShot(string name)
        {
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.46f, 0.58f, 0.72f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.78f, 0.79f, 0.82f), AmbientLightEnergy = 1.0f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-42f, -30f, 0f), LightEnergy = 1.2f });
            var v = Vehicle.BuildByName(name);
            AddChild(v);
            v.GlobalPosition = Vector3.Zero;
            v.Freeze = true; v.FreezeMode = RigidBody3D.FreezeModeEnum.Static;
            v.ProcessMode = Node.ProcessModeEnum.Disabled;   // or it unfreezes itself and falls (see the sling harness)
            Vector3 hub = v.DebugTailHub;
            // A red pip exactly AT the spec's hub, so the render answers "is the hub where the post is" directly
            // instead of me measuring pixels off it afterwards.
            AddChild(new MeshInstance3D
            {
                Mesh = new SphereMesh { Radius = 0.13f, Height = 0.26f },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(1f, 0.1f, 0.1f), EmissionEnabled = true, Emission = new Color(1f, 0.1f, 0.1f), EmissionEnergyMultiplier = 0.8f },
                Position = hub,
            });
            GD.Print($"[TAILSHOT] {name}: hub {hub} (red pip)");
            // Report what the belly beacon actually IS. "It exists and flashes" is what the parts suite checks;
            // whether it is the same FITTING as the nav lights is a different claim and needs the mesh type.
            if (v.FindChild("BeaconBelly", false, false) is MeshInstance3D bm)
            {
                var nav = v.FindChild("NavLightPort", false, false) as MeshInstance3D;
                bool sameModel = nav != null && bm.Mesh == nav.Mesh;
                var ab = bm.Mesh.GetAabb();
                int nx = 0, px = 0;
                for (int si = 0; si < bm.Mesh.GetSurfaceCount(); si++)
                {
                    var arr = bm.Mesh.SurfaceGetArrays(si);
                    if (arr.Count == 0 || arr[(int)Mesh.ArrayType.Vertex].VariantType == Variant.Type.Nil) continue;
                    foreach (var lv in arr[(int)Mesh.ArrayType.Vertex].AsVector3Array())
                        if (lv.X < -0.02f) nx++; else if (lv.X > 0.02f) px++;
                }
                GD.Print($"[BEACON] {name}: {bm.Mesh.GetType().Name} size {ab.Size} centre {ab.GetCenter()} verts -X{nx}/+X{px} sameAsNav={sameModel}");
            }
            var cam = new Camera3D { Current = true, Fov = 40f, Far = 400f };
            AddChild(cam);
            // UG_TAIL_CAM="x,y,z" overrides the offset: a hub buried INSIDE the fuselage puts the default camera
            // inside the mesh and renders a flat wall of hull, which is its own diagnosis but shows nothing else.
            Vector3 off = new Vector3(0.35f, 0.85f, 2.3f);
            string co = System.Environment.GetEnvironmentVariable("UG_TAIL_CAM");
            if (!string.IsNullOrEmpty(co))
            {
                var cp = co.Split(',');
                if (cp.Length == 3) off = new Vector3(float.Parse(cp[0]), float.Parse(cp[1]), float.Parse(cp[2]));
            }
            cam.Position = hub + off;
            cam.LookAt(hub, Vector3.Up);
        }

        void BuildTailCheck()
        {
            string[] fleet = { "minicopter", "huey", "scoutcopter", "hind", "orca", "skycrane", "hummingbird" };
            GD.Print("[TAIL] airframe      specX  side   reach-X  reach+X   verts     nearest  verdict");
            foreach (var name in fleet)
            {
                var v = Vehicle.BuildByName(name);
                AddChild(v);
                v.GlobalPosition = Vector3.Zero;
                Vector3 hub = v.DebugTailHub;
                int negN = 0, posN = 0; float negX = 0f, posX = 0f, nearest = 9999f;
                void Scan(Node k)
                {
                    // EVERY visible mesh, not just nodes named Body*. The first cut filtered on that prefix and
                    // reported the scoutcopter's hub as 2.34 m off the mesh -- which is what "I only looked at some
                    // of the geometry" produces, and is indistinguishable from a genuinely misplaced hub.
                    if (k is MeshInstance3D mi && mi.Mesh != null && mi.Visible)
                        for (int si = 0; si < mi.Mesh.GetSurfaceCount(); si++)
                        {
                            var arr = mi.Mesh.SurfaceGetArrays(si);
                            if (arr.Count == 0 || arr[(int)Mesh.ArrayType.Vertex].VariantType == Variant.Type.Nil) continue;
                            foreach (var lv in arr[(int)Mesh.ArrayType.Vertex].AsVector3Array())
                            {
                                var w = v.ToLocal(mi.GlobalTransform * lv);
                                nearest = Mathf.Min(nearest, w.DistanceTo(hub));
                                if (Mathf.Abs(w.Z - hub.Z) > 0.9f || Mathf.Abs(w.Y - hub.Y) > 0.9f) continue;
                                if (w.X < -0.06f) { negN++; negX = Mathf.Min(negX, w.X); }
                                else if (w.X > 0.06f) { posN++; posX = Mathf.Max(posX, w.X); }
                            }
                        }
                    foreach (var c in k.GetChildren()) Scan(c);
                }
                Scan(v);
                // Which side actually carries the protrusion: more vertices AND reaching further out.
                // Decide on REACH -- how far the outlying geometry sticks out each way -- with the vertex counts kept
                // only as supporting evidence. Counting alone called a 2:1 split "symmetric" on two airframes.
                float reach = Mathf.Max(posX, -negX);
                string meshSide = reach < 0.12f ? "none" : (posX > -negX + 0.06f) ? "+X" : (-negX > posX + 0.06f) ? "-X" : "sym";
                string specSide = hub.X > 0.06f ? "+X" : hub.X < -0.06f ? "-X" : "0";
                string verdict = nearest > 1.2f ? $"HUB IS OFF THE MESH ({nearest:0.00} m to the nearest vertex)"
                               : meshSide == "none" || meshSide == "sym" ? "no protruding post -- faired or centred"
                               : meshSide == specSide ? "ok"
                               : $"MISMATCH: hub is {specSide} but the post is {meshSide}";
                GD.Print($"[TAIL] {name,-12} {hub.X,6:0.00}  {meshSide,-5} -X{negX,6:0.00} +X{posX,6:0.00}  n{negN,3}/{posN,-3} near{nearest,5:0.00}  {verdict}");
                v.QueueFree();
            }
            GetTree().Quit();
        }

        void BuildMagnetTest()
        {
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.46f, 0.58f, 0.72f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.72f, 0.74f, 0.78f), AmbientLightEnergy = 1.0f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-48f, -38f, 0f), LightEnergy = 1.25f, ShadowEnabled = true });
            var ground = new StaticBody3D { CollisionLayer = 1 << 0 };
            ground.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
            AddChild(ground);
            AddChild(new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(400f, 400f) }, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.36f, 0.42f, 0.30f), Roughness = 1f } });

            var heli = Vehicle.BuildByName("skycrane");
            AddChild(heli);
            float holdY = float.TryParse(System.Environment.GetEnvironmentVariable("UG_MAG_HOLD"), out var h0) ? h0 : 10.6f;
            heli.GlobalPosition = new Vector3(0f, holdY, 0f);
            heli.GravityScale = 0f;
            heli.DebugNoSling = System.Environment.GetEnvironmentVariable("UG_MAG_NOSLING") == "1";
            // One-off: where are the VISIBLE leg posts? The spec's Skids(...) numbers are COLLISION boxes, and I
            // used their centre as "in line with the leg posts" -- which strawberry says came out the wrong way.
            // Scan the actual mesh for gear-region geometry and report where it really sits in Z.
            {
                var bins = new System.Collections.Generic.SortedDictionary<int, int>();
                float lo = 1e9f, hi = -1e9f;
                void Scan(Node k)
                {
                    if (k is MeshInstance3D mi && mi.Mesh != null && mi.Visible)
                        for (int si = 0; si < mi.Mesh.GetSurfaceCount(); si++)
                        {
                            var arr = mi.Mesh.SurfaceGetArrays(si);
                            if (arr.Count == 0 || arr[(int)Mesh.ArrayType.Vertex].VariantType == Variant.Type.Nil) continue;
                            foreach (var lv in arr[(int)Mesh.ArrayType.Vertex].AsVector3Array())
                            {
                                var w = heli.ToLocal(mi.GlobalTransform * lv);
                                if (w.Y > 0.75f || Mathf.Abs(w.X) < 1.4f) continue;   // gear region only: low + outboard
                                int b = Mathf.RoundToInt(w.Z * 2f);
                                bins[b] = bins.TryGetValue(b, out var c) ? c + 1 : 1;
                                lo = Mathf.Min(lo, w.Z); hi = Mathf.Max(hi, w.Z);
                            }
                        }
                    foreach (var c in k.GetChildren()) Scan(c);
                }
                Scan(heli);
                var top = new System.Collections.Generic.List<string>();
                foreach (var kv in bins) if (kv.Value >= 4) top.Add($"Z{kv.Key / 2f:0.0}x{kv.Value}");
                GD.Print($"[GEAR] visible gear geometry Z {lo:0.00}..{hi:0.00}; clusters: {string.Join(" ", top)}");
            }
            GD.Print($"[MAGNET/SPEC] slingHook={heli.DebugSlingHook} cableLen={heli.DebugSlingLen:0.00} forceAnchor={heli.DebugSlingAnchorLocal} drawAnchor={heli.DebugSlingVisualAnchorLocal}");

            // UG_MAG_CONTAINER=1: use the real MagnetableContainer instead of the stand-in box, which exercises the
            // FIXED attach point (all three axes) rather than the generic seat-the-AABB path.
            if (System.Environment.GetEnvironmentVariable("UG_MAG_CONTAINER") == "1")
            {
                var mc = MagnetableContainer.Spawn(this, new Vector3(0f, 0.2f, 1.0f));
                if (System.Environment.GetEnvironmentVariable("UG_MAG_DOORS") == "1") mc.CallDeferred(nameof(MagnetableContainer.SetDoorsOpen), true);
                var g2 = new MagnetGantry { Heli = heli, HoldY = holdY, Load = mc };
                AddChild(g2);
                var cam2 = new Camera3D { Current = true, Fov = 46f, Far = 4000f };
                AddChild(cam2);
                // Camera choice must NOT key off the door STATE, or the open and shut shots come from different
                // viewpoints and cannot be compared -- which is exactly what happened the first time.
                bool doorShot = System.Environment.GetEnvironmentVariable("UG_MAG_DOORSHOT") == "1";
                if (doorShot)
                {
                    // Doors shot: hold a fixed camera on the CONTAINER. The tracking camera frames the aircraft and
                    // its magnet, which is the wrong subject when the thing being checked is whether the leaves swing.
                    cam2.Position = new Vector3(11f, 3.4f, -9.5f);
                    cam2.LookAt(new Vector3(0f, 1.6f, 0.6f), Vector3.Up);
                }
                else
                {
                    cam2.Position = new Vector3(26f, 7.5f, 10f);
                    cam2.LookAt(new Vector3(0f, 5.5f, 1.0f), Vector3.Up);
                    g2.Cam = cam2;   // track only when the aircraft is the subject
                }
                GD.Print($"[MAGNET] using a real MagnetableContainer ({MagnetableContainer.ContainerMass:0} kg)");
                return;
            }

            // The load: a plain box on the PROP layer, sized like the shipping container that started all this.
            var load = new RigidBody3D { Name = "Load", Mass = 800f, CollisionLayer = 1u << 6, CollisionMask = (1u << 0) | (1u << 5) };
            var lsize = new Vector3(2.88f, 3.24f, 3.00f);
            load.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = lsize } });
            load.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = lsize }, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.35f, 0.20f), Roughness = 0.9f } });
            AddChild(load);
            load.GlobalPosition = new Vector3(0f, lsize.Y * 0.5f, 1.0f);   // under the winch anchor's Z

            var gantry = new MagnetGantry { Heli = heli, HoldY = holdY, Load = load };
            AddChild(gantry);

            var cam = new Camera3D { Current = true, Fov = 46f, Far = 4000f };
            AddChild(cam);
            cam.Position = new Vector3(26f, 7.5f, 10f);
            cam.LookAt(new Vector3(0f, 5.5f, 1.0f), Vector3.Up);
            gantry.Cam = cam;
        }

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
            // UG_CAM=front -> straight-on down -Z at the X-Y face (see board/plank gaps); =top -> top-down; else 3/4 diagnostic.
            string _camMode = System.Environment.GetEnvironmentVariable("UG_CAM");
            cam.Position = c + (_camMode == "front" ? new Vector3(0f, 0f, r * 2.4f)
                              : _camMode == "top"   ? new Vector3(0f, r * 2.4f, r * 0.001f)
                              : new Vector3(r * 1.15f, r * 0.85f, r * 1.15f));
            cam.LookAt(c, _camMode == "top" ? Vector3.Back : Vector3.Up);
            GD.Print($"[PROPTEST] {name} aabb size={aabb.Size} center={c}");
        }

        // --trainshow : assemble train_cargo_0 from its extracted pieces (loco + 8 bogies + 3 cars +
        // headlights/steer/seat) at their source positions for a 3/4 render-movie shot (master).
        void BuildTrainShow()
        {
            var env = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.52f, 0.62f, 0.74f), AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.7f, 0.7f, 0.72f), AmbientLightEnergy = 0.9f };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-45f, -50f, 0f), LightEnergy = 1.2f, ShadowEnabled = true });
            Mesh Lm(string n) => ContentProvider.ParseObj($"res://content/{n}.txt");
            Material Tex(string t) { var m = new StandardMaterial3D { TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest, Metallic = 0f, Roughness = 0.75f, CullMode = BaseMaterial3D.CullModeEnum.Disabled }; var img = new Image(); if (img.Load(ProjectSettings.GlobalizePath($"res://content/{t}.png")) == Error.Ok) m.AlbedoTexture = ImageTexture.CreateFromImage(img); return m; }
            Material carMat = Tex("train_car_tex"), bogieMat = Tex("train_bogie_tex");
            // PAINTABLE LIVERY: recolour the body palette slot (blue) to a random livery, and the stripe slot
            // (orange) STAYS fixed orange (master). Demo body colour here; per-spawn xorshift in the real vehicle.
            Color livery = new Color(0.16f, 0.42f, 0.22f);
            var bodyMat = new StandardMaterial3D { TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest, Metallic = 0f, Roughness = 0.75f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            var _bimg = new Image();
            if (_bimg.Load(ProjectSettings.GlobalizePath("res://content/train_body_tex.png")) == Error.Ok) { _bimg.Convert(Image.Format.Rgba8); _bimg.SetPixel(0, 1, livery); bodyMat.AlbedoTexture = ImageTexture.CreateFromImage(_bimg); }
            void Am(Mesh m, Vector3 pp, Material mat) { if (m != null) AddChild(new MeshInstance3D { Mesh = m, Position = pp, MaterialOverride = mat }); }
            Mesh body = Lm("train_body"), bogie = Lm("train_bogie"), car = Lm("train_car"), head = Lm("train_headlights"), steer = Lm("train_steer"), seat = Lm("train_seat");
            Am(body, Vector3.Zero, bodyMat); Am(head, Vector3.Zero, bodyMat); Am(steer, Vector3.Zero, bodyMat); Am(seat, Vector3.Zero, bodyMat);
            foreach (var bz in new[] { -3.5f, 3.5f, 7.5f, 14.5f, 18.5f, 25.5f, 29.5f, 36.5f }) Am(bogie, new Vector3(0f, -0.40f, bz), bogieMat);
            foreach (var cz in new[] { 11f, 22f, 33f }) Am(car, new Vector3(0f, 0f, cz), carMat);
            var cam = new Camera3D { Current = true, Fov = 42f, Far = 10000f }; AddChild(cam);
            cam.Position = new Vector3(26f, 14f, -20f); cam.LookAt(new Vector3(0f, 1.2f, 13f), Vector3.Up);
        }

        // --traintrack : the assembled train riding a CURVED test track. Each unit rides 2 bogies snapped to the
        // rail; the body SPANS them so it angles through curves -- the bogie-follows-spline mechanic (local curve).
        void BuildTrainTrack()
        {
            var env = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.52f, 0.62f, 0.74f), AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.7f, 0.7f, 0.72f), AmbientLightEnergy = 0.9f };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-50f, -40f, 0f), LightEnergy = 1.2f, ShadowEnabled = true });
            AddChild(new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(400f, 400f) }, Position = new Vector3(15f, -0.05f, 25f), MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.30f, 0.34f, 0.27f) } });
            Vector3[] ctrl = { new(-40f, 0f, -30f), new(-40f, 0f, 5f), new(-20f, 0f, 40f), new(15f, 0f, 55f), new(45f, 0f, 55f), new(70f, 0f, 35f) };
            _trP = new(); _trD = new(); _trUnits = new(); var P = _trP; var D = _trD;
            Vector3 CR(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float t) { float t2 = t * t, t3 = t2 * t; return 0.5f * ((2f * b) + (-a + c) * t + (2f * a - 5f * b + 4f * c - d) * t2 + (-a + 3f * b - 3f * c + d) * t3); }
            { float dd = 0f; Vector3 prev = ctrl[1]; for (int i = 1; i < ctrl.Length - 2; i++) for (int k = 0; k < 40; k++) { float t = k / 40f; var pp = CR(ctrl[i - 1], ctrl[i], ctrl[i + 1], ctrl[i + 2], t); if (P.Count > 0) dd += pp.DistanceTo(prev); P.Add(pp); D.Add(dd); prev = pp; } }
            void Eval(float ss, out Vector3 pos, out Vector3 tan) { ss = Mathf.Clamp(ss, 0f, D[D.Count - 1]); int i = 0; while (i < D.Count - 2 && D[i + 1] < ss) i++; float seg = Mathf.Max(D[i + 1] - D[i], 1e-3f); float f = (ss - D[i]) / seg; pos = P[i].Lerp(P[i + 1], f); var tt = P[i + 1] - P[i]; tan = tt.LengthSquared() > 1e-6f ? tt.Normalized() : Vector3.Forward; }
            var im = new ImmediateMesh();
            im.SurfaceBegin(Mesh.PrimitiveType.Triangles, new StandardMaterial3D { AlbedoColor = new Color(0.17f, 0.15f, 0.13f), CullMode = BaseMaterial3D.CullModeEnum.Disabled });
            for (int i = 0; i < P.Count - 1; i++) { Vector3 t = (P[i + 1] - P[i]).Normalized(); Vector3 side = new Vector3(t.Z, 0f, -t.X) * 1.7f; Vector3 a = P[i] - side, b = P[i] + side, c = P[i + 1] - side, e = P[i + 1] + side; foreach (var v in new[] { a, b, c, b, e, c }) im.SurfaceAddVertex(v + Vector3.Up * 0.02f); }
            im.SurfaceEnd(); AddChild(new MeshInstance3D { Mesh = im });
            Mesh Lm(string n) => ContentProvider.ParseObj($"res://content/{n}.txt");
            Material Tex(string tn) { var m = new StandardMaterial3D { TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest, Roughness = 0.75f, CullMode = BaseMaterial3D.CullModeEnum.Disabled }; var img = new Image(); if (img.Load(ProjectSettings.GlobalizePath($"res://content/{tn}.png")) == Error.Ok) m.AlbedoTexture = ImageTexture.CreateFromImage(img); return m; }
            var carMat = Tex("train_car_tex"); var bogieMat = Tex("train_bogie_tex");
            var bodyMat = new StandardMaterial3D { TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest, Roughness = 0.75f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            { var bimg = new Image(); if (bimg.Load(ProjectSettings.GlobalizePath("res://content/train_body_tex.png")) == Error.Ok) { bimg.Convert(Image.Format.Rgba8); bimg.SetPixel(0, 1, new Color(0.16f, 0.42f, 0.22f)); bodyMat.AlbedoTexture = ImageTexture.CreateFromImage(bimg); } }
            Mesh body = Lm("train_body"), bogie = Lm("train_bogie"), car = Lm("train_car");
            _trRailY = 0.9f;
            void MakeUnit(Mesh m, Material mat, float off) {
                var mi = new MeshInstance3D { Mesh = m, MaterialOverride = mat }; AddChild(mi);
                var bf = new MeshInstance3D { Mesh = bogie, MaterialOverride = bogieMat }; AddChild(bf);
                var bb = new MeshInstance3D { Mesh = bogie, MaterialOverride = bogieMat }; AddChild(bb);
                _trUnits.Add((mi, bf, bb, off));
            }
            MakeUnit(body, bodyMat, 0f); MakeUnit(car, carMat, 11f); MakeUnit(car, carMat, 22f); MakeUnit(car, carMat, 33f);
            _trS = 45f; _trAnim = true;
            foreach (var u in _trUnits) PlaceTrainUnit(u, _trS - u.off);
            var cam = new Camera3D { Current = true, Fov = 52f, Far = 10000f }; AddChild(cam);
            cam.Position = new Vector3(38f, 9f, 40f); cam.LookAt(new Vector3(2f, 1.5f, 38f), Vector3.Up);
        }

        void EvalTrack(float ss, out Vector3 pos, out Vector3 tan) {
            ss = Mathf.Clamp(ss, 0f, _trD[_trD.Count - 1]); int i = 0; while (i < _trD.Count - 2 && _trD[i + 1] < ss) i++;
            float seg = Mathf.Max(_trD[i + 1] - _trD[i], 1e-3f); float f = (ss - _trD[i]) / seg; pos = _trP[i].Lerp(_trP[i + 1], f);
            var tt = _trP[i + 1] - _trP[i]; tan = tt.LengthSquared() > 1e-6f ? tt.Normalized() : Vector3.Forward;
        }
        void PlaceTrainUnit((MeshInstance3D body, MeshInstance3D bf, MeshInstance3D bb, float off) u, float sctr) {
            EvalTrack(sctr + 3.5f, out var pf, out var tf); EvalTrack(sctr - 3.5f, out var pb, out var tb);
            Vector3 c = (pf + pb) * 0.5f + Vector3.Up * _trRailY; Vector3 fwd = pf - pb; fwd = fwd.LengthSquared() > 1e-4f ? fwd.Normalized() : Vector3.Forward;
            u.body.GlobalTransform = new Transform3D(Basis.Identity, c).LookingAt(c + fwd, Vector3.Up);
            Vector3 cf = pf + Vector3.Up * (_trRailY - 0.4f); u.bf.GlobalTransform = new Transform3D(Basis.Identity, cf).LookingAt(cf + tf, Vector3.Up);
            Vector3 cb = pb + Vector3.Up * (_trRailY - 0.4f); u.bb.GlobalTransform = new Transform3D(Basis.Identity, cb).LookingAt(cb + tb, Vector3.Up);
        }
        void StepTrainAnim(float dt) {
            if (_trUnits == null || _trD == null || _trD.Count < 2) return;
            _trS += 9f * dt; if (_trS > _trD[_trD.Count - 1] + 5f) _trS = 45f;
            foreach (var u in _trUnits) PlaceTrainUnit(u, _trS - u.off);
        }

        // --doorgallery --shot=OUT : a lit, front-on LINEUP of the 12 ripped WOODEN door barricade models
        // (Door / Doubledoor / Gate / Hatch, each in Birch / Maple / Pine), grouped by form with the three wood
        // tints adjacent, a name label under each + the form name above -- so master can eyeball every wooden door
        // model at once. The meshes are barricade SkinnedMeshRenderer leaves (tools/extract_wooden_doors.py);
        // barricades are authored lying flat, so a +90 X stands them up (override UG_DOORROT="x,y,z", no rebuild).
        void BuildDoorGallery()
        {
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.44f, 0.56f, 0.72f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.62f, 0.64f, 0.67f),
                AmbientLightEnergy = 0.8f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-48f, -36f, 0f), LightEnergy = 1.15f, ShadowEnabled = true });

            var gmesh = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(160, 160) } };
            gmesh.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.34f, 0.37f, 0.33f), Roughness = 1f };
            AddChild(gmesh);

            // these rips are authored CONTAINER-STYLE (height on +Z, like the fridge/container leaves) -> +270 X
            // stands them up (like StoreShelf). NOT the +90 DeployableDef-table convention -- that maps +Z to -Y and
            // points them at the floor (my handoff bug: quoted the rule, not the asset). UG_DOORROT overrides.
            Vector3 rot = new Vector3(270f, 0f, 0f);
            var rr = System.Environment.GetEnvironmentVariable("UG_DOORROT");
            if (!string.IsNullOrEmpty(rr)) { var pp = rr.Split(','); if (pp.Length == 3 && float.TryParse(pp[0], out var rx) && float.TryParse(pp[1], out var ry) && float.TryParse(pp[2], out var rz)) rot = new Vector3(rx, ry, rz); }
            Basis standUp = Basis.FromEuler(new Vector3(Mathf.DegToRad(rot.X), Mathf.DegToRad(rot.Y), Mathf.DegToRad(rot.Z)));

            string odir = ProjectSettings.GlobalizePath("res://content/objects/");
            // Swing pose (UG_DOOROPEN=frac 0..1): swing single-hinge doors by frac*angle about their hinge, read from
            // tools/extract_wooden_door_anims.py's wooden_door_anims.txt. The Doubledoor (2 hinges) needs a panel split -> stays shut here.
            float openFrac = 0f; { var ov = System.Environment.GetEnvironmentVariable("UG_DOOROPEN"); if (!string.IsNullOrEmpty(ov)) float.TryParse(ov, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out openFrac); }
            float F(string s) => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
            var anims = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<(Vector3 pivot, Vector3 axis, float ang)>>();
            { string ap = odir + "wooden_door_anims.txt"; if (System.IO.File.Exists(ap)) foreach (var ln in System.IO.File.ReadAllLines(ap)) { var pp = ln.Split(' ', System.StringSplitOptions.RemoveEmptyEntries); if (pp.Length < 9) continue; if (!anims.ContainsKey(pp[0])) anims[pp[0]] = new System.Collections.Generic.List<(Vector3, Vector3, float)>(); anims[pp[0]].Add((new Vector3(F(pp[2]), F(pp[3]), F(pp[4])), new Vector3(F(pp[5]), F(pp[6]), F(pp[7])), F(pp[8]))); } }
            void PlaceDoor(string form, string wood, Vector3 pos)
            {
                string nm = form + "_" + wood;
                var m = ObjMesh.Load(odir + nm + ".obj");
                if (m == null) { GD.Print($"[DOORS] {nm}.obj MISSING"); return; }
                var lb = m.GetAabb();
                // The Gate is a GARAGE DOOR (master): wide + tilts UP (ripped anim axis = X-tilt). Its handle rips at the
                // TOP but a garage door's handle belongs at the BOTTOM (front/back, master), so flip it 180 deg in-plane
                // about the face normal -- stays wide + forward-facing, just moves the handle top->bottom.
                Basis su = form == "Gate" ? new Basis(new Vector3(0f, 0f, 1f), Mathf.DegToRad(180f)) * standUp : standUp;
                var mat = new StandardMaterial3D { Roughness = 0.85f, CullMode = BaseMaterial3D.CullModeEnum.Disabled, TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest };
                string tp = odir + nm + "_tex.png";
                if (System.IO.File.Exists(tp)) { var img = new Image(); if (img.Load(tp) == Error.Ok) mat.AlbedoTexture = ImageTexture.CreateFromImage(img); else mat.AlbedoColor = new Color(0.5f, 0.36f, 0.22f); }
                else mat.AlbedoColor = new Color(0.5f, 0.36f, 0.22f);
                // stood-up AABB: transform the 8 local corners by standUp -> sit the base on the ground, centre in X/Z.
                Vector3 mn = new Vector3(1e9f, 1e9f, 1e9f), mx = new Vector3(-1e9f, -1e9f, -1e9f);
                for (int cx = 0; cx < 2; cx++) for (int cy = 0; cy < 2; cy++) for (int cz = 0; cz < 2; cz++)
                {
                    Vector3 wc = su * (lb.Position + new Vector3(cx * lb.Size.X, cy * lb.Size.Y, cz * lb.Size.Z));
                    mn = new Vector3(Mathf.Min(mn.X, wc.X), Mathf.Min(mn.Y, wc.Y), Mathf.Min(mn.Z, wc.Z));
                    mx = new Vector3(Mathf.Max(mx.X, wc.X), Mathf.Max(mx.Y, wc.Y), Mathf.Max(mx.Z, wc.Z));
                }
                Vector3 c = (mn + mx) * 0.5f;
                GD.Print($"[DOORS] {nm} stood-up size={mx - mn} (w={mx.X - mn.X:0.00} h={mx.Y - mn.Y:0.00} d={mx.Z - mn.Z:0.00})");
                var placement = new Transform3D(su, new Vector3(pos.X - c.X, -mn.Y, pos.Z - c.Z));
                var world = placement;
                if (openFrac > 0f && anims.TryGetValue(form, out var hs) && hs.Count == 1)   // single-hinge -> swing the whole mesh about its hinge; Doubledoor (2 hinges) needs a panel split, stays shut here
                {
                    var h = hs[0];
                    var sb = new Basis(h.axis.Normalized(), Mathf.DegToRad(h.ang * openFrac));
                    world = placement * new Transform3D(sb, h.pivot - sb * h.pivot);   // rotate the mesh about its hinge (mesh-local) THEN place it
                }
                AddChild(new MeshInstance3D { Mesh = m, MaterialOverride = mat, Transform = world });
            }

            // 4x3 grid: columns = form (Door/Doubledoor/Gate/Hatch), rows = wood (Birch front / Maple / Pine back),
            // wide gates/doubledoors get their own column so nothing overlaps. Seen from a high 3/4 so no door hides
            // another; column labels (form) above the front row + row labels (wood) at the left, not a label per door.
            string[] forms = { "Door", "Doubledoor", "Gate", "Hatch" };
            string[] woods = { "Birch", "Maple", "Pine" };
            float[] colX = { -10f, -3.3f, 3.3f, 10f };
            float[] rowZ = { 6f, 0f, -6f };
            for (int wi = 0; wi < woods.Length; wi++)
                for (int fi = 0; fi < forms.Length; fi++)
                    PlaceDoor(forms[fi], woods[wi], new Vector3(colX[fi], 0f, rowZ[wi]));
            for (int fi = 0; fi < forms.Length; fi++)   // small form header floating above each column (clear of the wood row labels)
                AddChild(new Label3D { Text = forms[fi], FontSize = 120, PixelSize = 0.0065f, Modulate = new Color(1f, 0.92f, 0.58f), OutlineSize = 14, OutlineModulate = Colors.Black, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, Position = new Vector3(colX[fi], 5.9f, rowZ[0] + 1.2f) });
            for (int wi = 0; wi < woods.Length; wi++)   // wood label at the left end of each row (low, so it never meets a form header)
                AddChild(new Label3D { Text = woods[wi], FontSize = 100, PixelSize = 0.009f, Modulate = Colors.White, OutlineSize = 12, OutlineModulate = Colors.Black, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, Position = new Vector3(-12.2f, 1.9f, rowZ[wi]) });

            var cam = new Camera3D { Current = true, Fov = 44f, Far = 10000f };
            AddChild(cam);
            cam.Position = new Vector3(0f, 18.5f, 24f);
            cam.LookAt(new Vector3(0f, 0.5f, -1f), Vector3.Up);
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

            // A WOODEN barricade door (Door_Pine, Gate_Birch, ...) is not a container prop: it has no separate
            // body, its leaf IS the whole model, and its hinge lives in wooden_door_anims.txt rather than
            // doors.txt. Route those through DoorDeploy -- the SAME call the placement path makes -- so this
            // harness shows the thing production builds rather than a second construction that could agree
            // with itself while the real one is wrong.
            foreach (var wd in DeployableDef.WoodDoors)
            {
                if (wd.DoorProp != name) continue;
                var placed = DoorDeploy.SpawnFor(wd, this, Vector3.Zero, 0f);
                if (placed == null) { GD.Print($"[DOORTEST] {name}: DoorDeploy refused it (no hinge row / no mesh)"); GetTree().Quit(1); return; }
                if (System.Environment.GetEnvironmentVariable("UG_DOOR_OPEN") == "1")
                    foreach (var c in placed.GetChildren()) if (c is ObjectDoor od) od.SetInitialState(true);
                var wcam = new Camera3D { Current = true, Fov = 55f };
                AddChild(wcam);
                // Framed WIDE and from above rather than close and level. A door swinging 90 deg sweeps a
                // quarter circle, and from a tight three-quarter view an open door fills the frame at an angle
                // that reads the same whether it hinged about the vertical axis or tipped over about a
                // horizontal one -- the magnitude is identical and only the axis differs. The whole point of
                // looking is to tell those apart, so the shot has to contain the swept arc, not just the leaf.
                // UG_DOORCAM="x,y,z" to move it.
                var cp = new Vector3(6.5f, 5.5f, 6.5f);
                var cs = System.Environment.GetEnvironmentVariable("UG_DOORCAM");
                if (!string.IsNullOrEmpty(cs))
                {
                    var q = cs.Split(',');
                    if (q.Length == 3) cp = new Vector3(float.Parse(q[0], System.Globalization.CultureInfo.InvariantCulture),
                                                        float.Parse(q[1], System.Globalization.CultureInfo.InvariantCulture),
                                                        float.Parse(q[2], System.Globalization.CultureInfo.InvariantCulture));
                }
                wcam.Position = cp;
                wcam.LookAt(new Vector3(0f, 0.9f, 0f), Vector3.Up);
                // a ground plane, so "standing up" and "fallen over" are distinguishable at all
                var gnd = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(14f, 14f) } };
                gnd.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.30f, 0.34f, 0.30f) };
                AddChild(gnd);
                GD.Print($"[DOORTEST] wooden door {name} placed via DoorDeploy");
                return;
            }

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

        // --impacttest : fire ONE reimplemented ImpactFx per surface across a grey wall (concrete / metal / wood / dirt
        // / grass / sand, then a water plip + a blood spray), captured a few frames into the burst so the debris is
        // mid-flight -- the exact thing the old (culled, no-VisibilityAabb) bursts couldn't show.
        void BuildImpactTest()
        {
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.15f, 0.16f, 0.19f),   // dark so sparks/debris read
                AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.55f, 0.55f, 0.6f), AmbientLightEnergy = 1.0f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-45f, -35f, 0f), LightEnergy = 1.2f });

            var wallSize = new Vector3(22f, 8f, 0.6f);
            var wallPos = new Vector3(0f, 3f, -3f);
            var wall = new StaticBody3D { Position = wallPos };
            wall.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = wallSize } });
            wall.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = wallSize }, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.46f, 0.46f, 0.49f), Roughness = 0.9f } });
            AddChild(wall);
            float faceZ = wallPos.Z + wallSize.Z * 0.5f;

            var surfs = new[] { PlayerController.Surf.Concrete, PlayerController.Surf.Metal, PlayerController.Surf.Wood, PlayerController.Surf.Dirt, PlayerController.Surf.Grass, PlayerController.Surf.Sand };
            for (int i = 0; i < surfs.Length; i++)
                ImpactFx.Spawn(this, new Vector3(-7f + i * 2.1f, 3.4f, faceZ), Vector3.Back, surfs[i]);
            ImpactFx.WaterSplash(this, new Vector3(6.3f, 3.4f, faceZ), 1f);
            ImpactFx.Blood(this, new Vector3(8.4f, 3.4f, faceZ), Vector3.Forward);

            var cam = new Camera3D { Current = true, Fov = 62f, Far = 10000f };
            AddChild(cam);
            cam.Position = new Vector3(0.7f, 3.4f, 10.5f);
            cam.LookAt(new Vector3(0.7f, 3.3f, -1f), Vector3.Up);
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

        // --barricadeplay : an interactive sandbox to TEST barricade placement feel before the in-game held-item flow
        // is wired. A small structure room (walls + roof, all in "structures"), a free-fly EditorCamera (hold RMB), a
        // BarricadePlayground driving a BarricadePlacer off the screen-centre aim, and a centre crosshair. LMB places;
        // [1-3] cycle def, Tab cycles mount family, R rotates.
        void BuildBarricadePlay()
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
                Mesh = new PlaneMesh { Size = new Vector2(60f, 60f) },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.34f, 0.40f, 0.28f), Roughness = 1f },
            });
            var groundBody = new StaticBody3D();
            groundBody.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
            groundBody.AddToGroup("terrain");
            AddChild(groundBody);

            // a small structure room to place on: back wall + left wall + a roof (Sticky targets), all in "structures"
            void Slab(Vector3 pos, Vector3 size)
            {
                var b = new StaticBody3D { Position = pos };
                b.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
                b.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.56f, 0.57f, 0.61f), Roughness = 0.85f } });
                b.AddToGroup("structures");
                AddChild(b);
            }
            Slab(new Vector3(0f, 2f, -3f), new Vector3(8f, 4f, 0.4f));    // back wall
            Slab(new Vector3(-4f, 2f, 0f), new Vector3(0.4f, 4f, 6f));    // left wall
            Slab(new Vector3(0f, 4.1f, 0f), new Vector3(8f, 0.4f, 6f));   // roof (Sticky targets on its underside)

            var cam = new EditorCamera { Position = new Vector3(0f, 2f, 1.6f), RotationDegrees = new Vector3(-3f, 0f, 0f) };   // start within the barricade Range of the back wall so the opening ghost is placeable
            AddChild(cam);
            var pg = new BarricadePlayground();
            AddChild(pg);
            pg.Setup(cam);

            // centre crosshair so you can see where the ghost will land
            var layer = new CanvasLayer();
            AddChild(layer);
            var dot = new ColorRect { Color = new Color(1f, 1f, 1f, 0.85f), Size = new Vector2(6f, 6f) };
            dot.SetAnchorsPreset(Control.LayoutPreset.Center);
            dot.Position = new Vector2(-3f, -3f);
            layer.AddChild(dot);
            GD.Print("[barricadeplay] HOLD RMB to fly/look (WASD move, scroll=speed). LMB=place. [1-3]=def, Tab=mount family, R=rotate 90.");
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

        // --ammoradial=OUT: open the R-hold shotgun ammo radial with mock 12ga choices (buckshot + slug) over a dim
        // backdrop + screenshot it, so the picker UI can be eyeballed without a live gun. The general frame-6 capture
        // (armed via _shotPath) saves + quits.
        void BuildAmmoRadialDemo()
        {
            AddChild(new WorldEnvironment { Environment = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.15f, 0.16f, 0.20f) } });
            AddChild(new Camera3D { Current = true });   // a 3D cam so the viewport clears to the bg colour; the radial is a CanvasLayer on top
            var radial = new AmmoRadial();
            AddChild(radial);
            if (System.Environment.GetEnvironmentVariable("UG_MAGPIE") == "1")   // MAG pie demo: spare mags + remove + rack wedges
            {
                var mt = AttachmentMenu.LoadItemIcon(6, standUp: true);   // Military Magazine, stood up portrait
                var mags = new System.Collections.Generic.List<(Texture2D icon, string name, int rounds, SDG.Unturned.Item mag, string type)>
                {
                    (mt, "Military Magazine", 30, null, "FMJ"),
                    (mt, "Military Magazine", 24, null, "AP"),    // demo the ammo-TYPE field: FMJ / AP / HP loads (master)
                    (mt, "Military Magazine", 0, null, "HP"),     // an EMPTY mag -> greys out but still shows (master)
                };
                mags.Sort((a, b) => b.rounds.CompareTo(a.rounds));   // fuller mags higher, matching gameplay (master)
                radial.OpenMags(mags, true, true, "HP");   // chamber type "HP" -> proves the chamber tracks independently of the seated mags (master)
                GD.Print($"[AMMORADIAL] mag demo: {mags.Count} mags + remove + rack");
                return;
            }
            var choices = new System.Collections.Generic.List<(SDG.Unturned.ItemAsset asset, int count, bool selected)>();
            var buck = SDG.Unturned.Assets.find(113);    // 12 Gauge Shells (buckshot)
            var slug = SDG.Unturned.Assets.find(5000);   // 12 Gauge Slug
            var bean = SDG.Unturned.Assets.find(5002);   // 12 Gauge Beanbag
            if (buck != null) choices.Add((buck, 12, false));
            if (slug != null) choices.Add((slug, 6, true));   // slug shown as the currently-selected type
            if (bean != null) choices.Add((bean, 4, false));
            radial.OpenWith(choices, true);   // demo: show the unload segment too
            GD.Print($"[AMMORADIAL] demo: {choices.Count} choices (buck={buck?.itemName}, slug={slug?.itemName})");
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
        /// <summary>Open a CUSTOM map by name -- the same path whether it already exists or not.
        ///
        /// There is no separate "load": every sub-editor reads `editor_&lt;MapName&gt;_*` when it starts, so
        /// naming the map IS opening it. A blank name would have been the old hardcoded "NewMap", which
        /// meant every new map silently opened on top of the previous one's files.</summary>
        bool _autoPlayMap;   // Workshop 'Play' -> enter play as soon as the map finishes building

        void BuildEditorNew(string mapName = null)
        {
            mapName = EditorMaps.Sanitise(mapName) ?? "NewMap";
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
            var editor = new Editor();
            AddChild(editor);
            var cam = new EditorCamera { Position = new Vector3(0f, 130f, 190f), RotationDegrees = new Vector3(-30f, 0f, 0f) };
            editor.AddChild(cam);
            editor.Setup(mapName, null, cam);
            LootTables.Load(_mapRoot + "/Spawns/Items.dat");   // new maps use PEI's loot tables as the pool (for loot crates)
            var objs = new EditorObjects(editor, this, cam, objectsPreloaded: false); editor.AddChild(objs); editor.Objects = objs;
            var spawns = new EditorSpawns(editor, cam, MapDir(mapName)); editor.AddChild(spawns); editor.Spawns = spawns;   // dir doesn't exist -> starts empty
            var envEd = new EditorEnvironment(editor, dayNight); editor.AddChild(envEd); editor.Environment = envEd;
            var terrainEd = new EditorTerrain(editor, cam, terr); editor.AddChild(terrainEd); editor.TerrainEd = terrainEd;
            var rf = new RoadField { Terr = terr };
            rf.LoadMaterialsOnly(_mapRoot + "/Environment");   // shared road materials so roads can be added on the blank map
            AddChild(rf);
            var roadsEd = new EditorRoads(editor, cam, rf); editor.AddChild(roadsEd); editor.RoadsEd = roadsEd;
            var roadDrawEd = new EditorRoadDraw(editor, cam, rf); editor.AddChild(roadDrawEd); editor.RoadDrawEd = roadDrawEd;   // R = draw, Shift+R = legacy nodes
            editor.AddChild(new EditorDashboard { Editor = editor, OnExit = ReturnToMenu });
            var play = new EditorPlayMode();   // playtest button -- custom maps get it too, not just PEI
            editor.AddChild(play);
            play.Setup(editor, null, cam);
            // Workshop's per-map Play opens the editor and goes straight in, so the map you play is the
            // map the editor built -- one world-building path, not two that can disagree.
            if (_autoPlayMap) { _autoPlayMap = false; play.CallDeferred(nameof(EditorPlayMode.EnterPlay)); }
            _worldReady = true;
            GD.Print($"[editor] custom map '{mapName}' (flat 3x3 base) up");
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
            // EDITOR object source: once a prior editor Save has materialized editor_PEI.txt (the our-format map),
            // load objects from THAT instead of the retail placements -- so edits persist and the first open converts
            // retail->ours one-way (the retail placements file is never written). Same 10-field format either way.
            string editorObjFile = "editor_PEI.txt";
            string objPlace = System.IO.File.Exists(ProjectSettings.GlobalizePath("res://content/objects/") + editorObjFile) ? editorObjFile : _mapPlace;
            var res = await WorldBuilder.BuildFullWorld(this, WorldMode.Editor, _mapRoot, objPlace, noZombies: true,
                                                        syncLoad: false, bakeNav: false, ActiveHoliday());
            // THE EDITOR BOOTS UNDER THE REAL LIGHTING (strawberry 2026-08-19: "the global lighting etc etc
            // should always be on, not just with the environment tab open"). It used to freeze the day-night
            // visuals and paint a flat fog-free sky, so every tab except Environment dressed the map under a
            // light the game never renders -- fine for legibility, useless for judging what you just placed.
            // EditorEnvironment now keeps the real cycle applied in every tab; the clean look survives only
            // for the road-demo RENDER below, which needs a deterministic frame rather than a pretty one.
            var editor = new Editor();
            AddChild(editor);
            var cam = new EditorCamera { Position = new Vector3(0f, 140f, 160f), RotationDegrees = new Vector3(-32f, 0f, 0f) };
            var camEnv = System.Environment.GetEnvironmentVariable("UG_EDITOR_CAM");   // "x,y,z,pitch" headless verify override (e.g. aim at a town cluster)
            if (camEnv != null) { var q = camEnv.Split(','); if (q.Length >= 4 && float.TryParse(q[0], out var cx) && float.TryParse(q[1], out var cy) && float.TryParse(q[2], out var cz) && float.TryParse(q[3], out var cp)) { cam.Position = new Vector3(cx, cy, cz); cam.RotationDegrees = new Vector3(cp, 0f, 0f); } }
            editor.AddChild(cam);
            editor.Setup("PEI", null, cam);
            LootTables.Load(_mapRoot + "/Spawns/Items.dat");   // so loot-crate tables can be named/picked in the editor
            var objs = new EditorObjects(editor, this, cam, objectsPreloaded: true);   // Phase 1a: WorldBuilder wrapped the loaded map objects as editable; ingest them, don't re-load the main object sidecar (avoids double-load)
            editor.AddChild(objs);
            editor.Objects = objs;
            var buildings = new EditorBuildings();   // building tool: walls + openings, shares the Level tab with Objects
            editor.AddChild(buildings);
            buildings.Setup(editor, cam, cam);   // EditorCamera IS a Camera3D
            editor.Buildings = buildings;
            // UG_EDITTOOL opens straight onto the Buildings mode, so a capture can show that editor. Without
            // it the only way to see it in a screenshot is to already be clicking the tab. UG_EDITBAKE then
            // bakes what it drew and places the result back on the map -- the whole build->bake->place round
            // trip in one frame, which is the only way to SEE that a baked building is a real prop.
            if (System.Environment.GetEnvironmentVariable("UG_EDITTOOL") == "buildings")
            {
                editor.Mode = EEditorMode.Buildings;
                // UG_EDITIMPORT ports a retail building in instead of drawing the demo, so the translator can
                // be LOOKED at. The L1 test proves its numbers are wall-shaped; only a render shows whether
                // the building it rebuilt is the building it read.
                string imp = System.Environment.GetEnvironmentVariable("UG_EDITIMPORT");
                if (!string.IsNullOrEmpty(imp)) buildings.ImportRetail(imp);
                else if (buildings.Walls.Count == 0) DrawDemoBuilding(buildings);
                if (System.Environment.GetEnvironmentVariable("UG_EDITBAKE") == "1")
                {
                    // Name the bake after what it came from. It used to always bake "Demo_House", so baking
                    // an IMPORT overwrote the committed demo prefab with a ported building under the demo's
                    // name -- two different buildings sharing one file, and whichever ran last won.
                    string baked = buildings.Bake(string.IsNullOrEmpty(imp) ? "Demo_House" : imp + "_ported");
                    editor.Mode = EEditorMode.Level;
                    if (baked != null)
                    {
                        var at = WorldBuilder.InteractableAnchor(_mapRoot);
                        // Drop it ON the ground. A building's origin is its floor line and its foundation
                        // hangs 6 m below that, so placing at y=0 over water leaves the foundation dangling in
                        // mid-air -- which looks like the bake got the origin wrong when it did not.
                        var down = new PhysicsRayQueryParameters3D
                        {
                            From = at + new Vector3(0f, 300f, 0f), To = at - new Vector3(0f, 100f, 0f),
                            CollisionMask = 1u << 0,
                        };
                        var ground = cam.GetWorld3D().DirectSpaceState.IntersectRay(down);
                        if (ground.ContainsKey("position")) at = (Vector3)ground["position"];
                        // UG_EDITCOMPARE=retail places the SOURCE prop at the same spot with the same camera
                        // INSTEAD of the port, so the two captures differ in nothing but the building.
                        // Two buildings side by side in one frame does not work: 34 m apart they are seen
                        // from 20 degrees apart, present different elevations, and the comparison is worthless
                        // -- which is exactly the trap of judging a port against a picture of itself.
                        bool retailOnly = System.Environment.GetEnvironmentVariable("UG_EDITCOMPARE") == "retail"
                                          && !string.IsNullOrEmpty(imp);
                        string show = retailOnly ? imp : baked;
                        objs.SetPlaceType(show);
                        objs.Place(show, at, EditorObjects.Upright(0f));
                        cam.GlobalPosition = at + new Vector3(17f, 11f, 24f);
                        cam.LookAt(at + new Vector3(0f, 2.5f, 0f), Vector3.Up);
                        GD.Print($"[editor] baked+placed '{baked}' at {at}");
                    }
                }
            }
            var spawns = new EditorSpawns(editor, cam, _mapRoot);   // Phase 3: visualize/edit spawn points (Spawns tab)
            editor.AddChild(spawns);
            editor.Spawns = spawns;
            var env = new EditorEnvironment(editor, res.DayNight);   // Phase 4: lighting/time/weather -- the real day-night now runs in EVERY tab
            editor.AddChild(env);
            editor.Environment = env;
            var terrainEd = new EditorTerrain(editor, cam, res.Terr);   // Phase 5: heightmap sculpt (Terrain tab)
            editor.AddChild(terrainEd);
            editor.TerrainEd = terrainEd;
            // Foliage painting. Wired at construction rather than left as a class nobody instantiates -- an
            // unreachable tool is the same failure as the generated LODs nothing loaded, and I have made that
            // one twice today already.
            if (res.Foliage != null)
            {
                var folEd = new EditorFoliage(editor, cam, res.Foliage);
                editor.AddChild(folEd);
                editor.FoliageEd = folEd;
            }
            RoadField rf = null;   // Phase 6: WorldMode.Editor skips WorldBuilder's roads step, so build the road splines here
            if (res.Terr != null)
            {
                rf = new RoadField { Terr = res.Terr };
                rf.LoadFromEnvironment(_mapRoot + "/Environment");
                AddChild(rf);
            }
            var roadsEd = new EditorRoads(editor, cam, rf);   // LEGACY node paving under the Environment tab (Shift+R)
            var roadDrawEd = new EditorRoadDraw(editor, cam, rf); editor.AddChild(roadDrawEd); editor.RoadDrawEd = roadDrawEd;   // draw-a-road/rail (R)
            editor.AddChild(roadsEd);
            editor.RoadsEd = roadsEd;
            editor.AddChild(new EditorDashboard { Editor = editor, OnExit = ReturnToMenu });
            var playMode = new EditorPlayMode();   // "Test Build" button -> walk the drawn building as a player (master 2026-08-09)
            editor.AddChild(playMode);
            playMode.Setup(editor, buildings, cam);
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
                // RENDER-ONLY clean lighting: this is a golden-image path, so it wants a flat deterministic
                // frame, not the shipping sky. Deliberately NOT how the editor boots any more (see above).
                void SetCleanEditorLighting()
                {
                    foreach (var n in GetChildren())
                        if (n is WorldEnvironment we && we.Environment is Godot.Environment ev)
                        {
                            ev.SetFogEnabled(false);
                            ev.BackgroundMode = Godot.Environment.BGMode.Color;
                            ev.BackgroundColor = new Color(0.53f, 0.67f, 0.86f);
                            ev.AmbientLightSource = Godot.Environment.AmbientSource.Color;
                            ev.AmbientLightColor = new Color(0.92f, 0.92f, 0.94f);
                            ev.AmbientLightEnergy = 1.15f;
                            ev.GlowEnabled = false;
                            break;
                        }
                }
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
            if (System.Environment.GetEnvironmentVariable("UG_QUICKCRAFT") == "1")   // stock craftable mats + load blueprints so the quick-craft bar shows
            {
                SDG.Unturned.ItemCatalog.RegisterAll();
                BlueprintRegistry.Load();
                player.Inventory.tryAddItem(new SDG.Unturned.Item(67, 200));   // Metal Scrap
                player.Inventory.tryAddItem(new SDG.Unturned.Item(76, 1));     // Blowtorch (tool)
                if (System.Environment.GetEnvironmentVariable("UG_WORKBENCH") == "1")   // place a Workbench 2m from the player -> its recipes unlock in the quick-craft
                    Deployable.Spawn(this, DeployableDef.Workbench, new Vector3(2f, 0f, 0f), 0f);
            }
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

        // --beamtest: the lighthouse's sweeping BEAM over a dark night ground -- one static frame (the spin needs an eye).
        void BuildBeamTest()
        {
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.02f, 0.03f, 0.06f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.05f, 0.06f, 0.09f), AmbientLightEnergy = 1f,
                GlowEnabled = true, GlowIntensity = 0.9f, GlowBloom = 0.2f, GlowHdrThreshold = 0.8f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-32f, 40f, 0f), LightEnergy = 0.18f, LightColor = new Color(0.55f, 0.65f, 0.85f) });   // faint moonlight so the tower reads

            var gmesh = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(700, 700) } };
            gmesh.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.05f, 0.06f, 0.07f), Roughness = 1f };
            AddChild(gmesh);

            string objDir = ProjectSettings.GlobalizePath("res://content/objects/");
            var m = ObjMesh.Load(objDir + "Lighthouse_0.obj");
            if (m == null) { GD.PrintErr("[beamtest] Lighthouse_0.obj missing"); return; }
            var mat = new StandardMaterial3D { Roughness = 0.9f, CullMode = BaseMaterial3D.CullModeEnum.Disabled, TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest };
            string tex = objDir + "Lighthouse_0_tex.png";
            if (System.IO.File.Exists(tex)) { var img = Image.LoadFromFile(tex); if (img != null) mat.AlbedoTexture = ImageTexture.CreateFromImage(img); }
            var mi = new MeshInstance3D { Mesh = m, MaterialOverride = mat, RotationDegrees = new Vector3(270f, 194f, 0f) };   // the tower's real placement euler
            AddChild(mi);

            // Sit the tower base on the ground + the lamp room off its world AABB (same math WorldBuilder uses).
            var ab = m.GetAabb(); var basis = mi.Basis;
            float topY = float.MinValue, botY = float.MaxValue; Vector3 sum = Vector3.Zero;
            for (int i = 0; i < 8; i++) { var w = basis * ab.GetEndpoint(i); topY = Mathf.Max(topY, w.Y); botY = Mathf.Min(botY, w.Y); sum += w; }
            mi.Position = new Vector3(0f, -botY, 0f);
            var lampRoom = new Vector3(sum.X / 8f, (topY - botY) - 4.5f, sum.Z / 8f);   // gallery ring at roof-4.5 (tinyclaw)
            AddChild(LighthouseBeam.Make(lampRoom));
            GD.Print($"[BEAMTEST] Lighthouse_0 + beam, roof {(topY - botY):0.0}m, lampRoom Y={lampRoom.Y:0.0} (want ~roof-4.5)");

            var cam = new Camera3D { Current = true, Fov = 62f, Far = 900f };
            AddChild(cam);
            cam.Position = new Vector3(130f, 45f, 140f);
            cam.LookAt(new Vector3(0f, lampRoom.Y - 8f, 0f), Vector3.Up);
        }

        // --lamptest: a single lit INDOOR light fixture over a dark ground -- Light_0 (ceiling) by default, or
        // UG_LAMP=Light_1/Lamp_0/Lamp_1. The "on" look is the fixture MESH glowing warm + an OmniLight lighting the
        // room; UG_LAMPOFF=1 renders it unlit in daylight. Exists because master's ceiling light "never worked": the
        // real ceiling light Light_0 was never wired to a LampLight (fixed in WorldBuilder), and the only proof it
        // works now is seeing it lit.
        // --treesweep : step a camera across the tree -> billboard handover and COUNT tree pixels at each distance.
        //
        // I shipped the overlap fix on reasoning alone -- shared edge, jitter, hole -- after that same reasoning
        // produced the bug in the first place, and the regression test I wrote only checks the arithmetic of the
        // two ranges. Nothing had ever confirmed a tree stays on screen while you cross the band. This does: a
        // solid-colour sky with nothing in the world but trees, so every non-sky pixel IS tree, sampled every few
        // metres from inside the real mesh's range to well past where it ends.
        //
        // The control is the whole point. UG_TREEIMPOVERLAP=1 restores the exact shipped bug, and if the sweep
        // cannot make THAT show a hole then the sweep proves nothing about the fix either.
        //
        // IT CANNOT, AND THAT IS THE STANDING RESULT. Three versions of this -- dense clump at 5 m steps, isolated
        // tree at 1 m, isolated tree 280-520 m at 2 m -- and the buggy config produced a pixel series BYTE-
        // IDENTICAL to the fixed one (md5-equal over 121 distances, with the bake confirmed at 1124 billboards).
        // Two reasons, both worth keeping written down:
        //
        //   1. The cull is per 64 m CELL, measured against a cell-sized AABB, so real trees survive far past their
        //      nominal range. Across the whole band where the two configs differ (295-335 m) the real mesh is
        //      still drawn in BOTH, hiding the difference behind itself.
        //   2. The reported symptom is DYNAMIC -- flicker while moving. A camera parked at each distance samples
        //      stable states, and a hole that exists for the single frame where two nodes disagree cannot be
        //      found by standing still.
        //
        // So this harness measures continuity of the far field, which is worth having, but it is NOT evidence
        // that the overlap fix cured the flicker. That claim remains unverified by anything but reasoning.
        // Catching it needs a camera in MOTION across the band, sampling every frame, not a stepped sweep.
        async System.Threading.Tasks.Task BuildTreeSweep()
        {
            var sky = new Color(0.25f, 0.45f, 0.75f);
            AddChild(new WorldEnvironment { Environment = new Godot.Environment {
                BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = sky,
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.7f, 0.7f, 0.7f), AmbientLightEnergy = 1f } });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-50f, 35f, 0f), LightEnergy = 1.1f });

            var field = new ResourceField();
            AddChild(field);
            field.LoadResources("NONE");
            // A FLOOR UNDER THE ROUTE, ON BY DEFAULT -- because without one this harness invents dips.
            // The scene is trees against flat sky, so a camera pitching over a crest sees NOTHING and the
            // tree-pixel count collapses; the mode then reports that as a dropout. Measured 2026-08-13:
            //     sky only    dips=6  worstRatio=0.173   (frames 177 178 247 325 386 387)
            //     with ground dips=2  worstRatio=0.673   (frames 177 325)
            // So FOUR of the six events this harness has been reporting all along were its own empty scene,
            // and the headline "83% of the trees vanished for one frame" was 33% once there is a world below
            // the horizon. The two survivors are shallow and may still be legitimate view changes.
            // UG_SWEEP_NOGROUND=1 restores the old scene for comparison.
            if (System.Environment.GetEnvironmentVariable("UG_SWEEP_NOGROUND") != "1")
            {
                var gmat = new StandardMaterial3D { AlbedoColor = new Color(0.30f, 0.33f, 0.26f), Roughness = 1f };
                AddChild(new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(4000, 4000) }, MaterialOverride = gmat, Position = new Vector3(-400f, 0f, 350f) });
                GD.Print("[fly] ground plane ON (default; UG_SWEEP_NOGROUND=1 restores the old sky-only scene)");
            }

            await field.BuildTreeImpostorsAsync();
            foreach (var (name, realEnd, impBegin, impEnd) in field.DebugImpostorRangesForTest())
                GD.Print($"[sweep] {name}: imposter on {impBegin:0.#}m, real off {realEnd:0.#}m, out to {impEnd:0}m");

            // ISOLATION IS THE WHOLE MEASUREMENT. The first version of this aimed at the DENSEST clump, on the
            // reasoning that more trees is a stronger signal. It is the opposite: total non-sky pixels summed over
            // five trees spread across several 64m cells can never reach zero, because each cell hands over at a
            // different camera distance and the survivors mask the one that vanished. The control proved it --
            // overlap=1.0, the exact shipped bug, produced a curve indistinguishable from the fix. So: the most
            // ISOLATED tree, and pixels are then a proxy for that single tree being drawn at all.
            int best = -1, bestN = int.MaxValue;
            for (int i = 0; i < field.InstanceCount; i++)
            {
                if (field.DebugTrunk(i) == null) continue;   // not a tree
                var p = field.DebugInstanceXf(i).Origin;
                int n = 0;
                for (int j = 0; j < field.InstanceCount; j++)
                    if (j != i && field.DebugTrunk(j) != null && field.DebugInstanceXf(j).Origin.DistanceSquaredTo(p) < 14400f) n++;
                if (n < bestN) { bestN = n; best = i; }
            }
            if (best < 0) { GD.PrintErr("[sweep] no trees found"); return; }
            var target = field.DebugInstanceXf(best).Origin;
            GD.Print($"[sweep] target instance {best} at {target}, {bestN} tree neighbours within 120m (isolated)");

            var cam = new Camera3D { Current = true, Fov = 50f };
            AddChild(cam);

            // UG_SWEEP_FLY=1: drive the camera along a real route at real speed, sampling EVERY frame, and look
            // for a frame whose tree pixels collapse relative to its neighbours. strawberry saw the flicker in
            // exactly one place -- the road south out of Alberton over the hill above Pirate Cove -- so it flies
            // that, rather than a spot I picked.
            //
            // *** THE DIPS THIS REPORTS ARE NOT THE TREE->IMPOSTER HANDOVER. DO NOT READ THEM AS THAT BUG. ***
            // This mode was committed claiming it "reproduces the flicker". That claim was wrong and this comment
            // is the retraction. Run with the handover DELETED -- `UG_TREECULL=5 UG_TREEIMPOVERLAP=10`, which
            // carries real trees to ~2235m and never switches an imposter on, so no handover exists anywhere on
            // the route -- and the same six dips come back, three of them BIT-IDENTICAL (4405/4405, 153/153,
            // 87/87) with worstRatio 0.173 in both. An instrument that reports the same six events with and
            // without the mechanism it is pointed at is not measuring that mechanism.
            // What they probably are: the route passing genuine gaps in a scene with no terrain to fill them.
            // Unresolved. If you extend this, ALWAYS run the no-handover control alongside and diff the dip sets;
            // a run without that control cannot distinguish a real blink from the route simply looking at sky.
            if (System.Environment.GetEnvironmentVariable("UG_SWEEP_FLY") == "1")
            {
                var a = new Vector3(-574.05f, 33.28f, -71.58f);    // Alberton   (content/nodes.tsv)
                var b = new Vector3(-264.67f, 69.88f, 768.10f);    // Pirate Cove
                float t0 = EnvOr("UG_FLY_T0", 0f), t1 = EnvOr("UG_FLY_T1", 1f);
                int frames = (int)EnvOr("UG_FLY_FRAMES", 400f);
                var prev = new System.Collections.Generic.List<int>();
                var objs = new System.Collections.Generic.List<int>();
                var prims = new System.Collections.Generic.List<long>();
                for (int f = 0; f < frames; f++)
                {
                    float u = Mathf.Lerp(t0, t1, frames <= 1 ? 0f : f / (float)(frames - 1));
                    var pos = a.Lerp(b, u) + new Vector3(0f, 2.5f, 0f);   // eye height above the node line
                    cam.GlobalPosition = pos;
                    cam.LookAt(pos + (b - a).Normalized() * 50f, Vector3.Up);   // looking where you're driving
                    // TWO frames, matching the stepped sweep. With one, the readback can land on a frame the
                    // renderer has not finished for the new camera pose, which manufactures exactly the artefact
                    // this is hunting: a single frame far below both neighbours. UG_FLY_SETTLE=1 restores the
                    // one-frame version, because "is the dip mine or the game's" has to stay answerable.
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                    if (EnvOr("UG_FLY_SETTLE", 2f) >= 2f) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                    var im = GetViewport().GetTexture()?.GetImage();
                    int n = 0;
                    if (im != null)
                        for (int y = 0; y < im.GetHeight(); y += 2)
                            for (int x = 0; x < im.GetWidth(); x += 2)
                            {
                                var c = im.GetPixel(x, y);
                                if (Mathf.Abs(c.R - sky.R) + Mathf.Abs(c.G - sky.G) + Mathf.Abs(c.B - sky.B) > 0.06f) n++;
                            }
                    prev.Add(n);
                    // WHAT THE RENDERER ACTUALLY DREW, alongside the pixel count. The dip frames are the last
                    // surviving flicker evidence and the open question is WHY they dip: a cell blinking out of the
                    // draw list, or the same cells drawing while the view changes. Pixels cannot tell those apart.
                    //
                    // These are engine counters, not my reconstruction of Godot's culling. I nearly diffed a
                    // MODELLED visible-cell set instead -- recomputing each MultiMeshInstance's VisibilityRange
                    // test myself -- which would have told me about my model of the cull rather than the cull.
                    objs.Add((int)Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame));
                    prims.Add((long)Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame));
                }
                // A flicker is a SPIKE, not a trend: one frame far below both its neighbours. Comparing to the
                // neighbours rather than to a global mean is what separates "a tree blinked" from "the view is
                // opening up as we crest the hill", which changes the count massively and legitimately.
                int dips = 0; float worst = 1f;
                for (int i = 1; i < prev.Count - 1; i++)
                {
                    float nb = 0.5f * (prev[i - 1] + prev[i + 1]);
                    if (nb < 50f) continue;
                    float ratio = prev[i] / nb;
                    if (ratio < worst) worst = ratio;
                    // Threshold is tunable because 0.75 was PICKED, not derived, and every "finding" this mode
                    // reports is a function of it. Swept 2026-08-14 (with the ground plane, 400 frames):
                    //     <0.60  0 frames        <0.85  2        <0.95  4
                    //     <0.75  2 (177, 325)    <0.90  2
                    // I expected a continuum -- shallow wobbles that only look like events because the line sits
                    // at 0.75. It is not. Frames 177 (0.71x) and 325 (0.67x) sit alone, with NOTHING between them
                    // and 0.90 across 400 frames. A ~0.2-wide empty band either side of two points is a bimodal
                    // population, not an artefact of where I drew the line, so those two are genuine outliers and
                    // the only surviving candidates for the flicker strawberry reported. Do not dismiss them.
                    // A SPIKE IS BELOW *BOTH* NEIGHBOURS. Comparing against their MEAN was the bug, and it is the
                    // reason this mode's last two findings were wrong: a STEP -- the level halving and staying
                    // halved -- puts the edge frame at ~0.7x of the mean while it sits at 1.00x of the frame after
                    // it. Frames 177 and 325 are exactly that (53920 -> 29960 -> 29887, and 55565 -> 28023 ->
                    // 27739): the view opened onto fewer trees and stayed there. The renderer agrees -- objects
                    // 135/136/136 and 50/49/49 across the two triples, so nothing was culled, the same draws just
                    // covered fewer pixels.
                    //
                    // This rule is strictly STRICTER than the old one, so it cannot invent a finding; the only risk
                    // it carries is a missed real spike, which is why the mean ratio is still printed beside it.
                    bool belowBoth = prev[i] < prev[i - 1] * EnvOr("UG_FLY_DIP", 0.75f)
                                  && prev[i] < prev[i + 1] * EnvOr("UG_FLY_DIP", 0.75f);
                    if (!belowBoth && ratio < EnvOr("UG_FLY_DIP", 0.75f))
                        GD.Print($"[fly] step-edge (NOT a dip) at frame {i}: {prev[i - 1]} -> {prev[i]} -> {prev[i + 1]} "
                               + $"({ratio:0.00}x of the mean, but {prev[i] / (float)prev[i + 1]:0.00}x of the frame after)");
                    if (belowBoth)
                    {
                        dips++;
                        // Print the TRIPLE, not the dip alone. "Frame 177 drew fewer objects" means nothing without
                        // 176 and 178 to compare against, and the whole claim is a spike relative to neighbours.
                        GD.Print($"[fly] DIP frame {i}: {prev[i]} px vs neighbours {nb:0} ({ratio:0.00}x)");
                        for (int k = i - 1; k <= i + 1 && k < prev.Count; k++)
                            if (k >= 0)
                                GD.Print($"[fly]   f{k,-4} px={prev[k],-8} objects={objs[k],-6} prims={prims[k]}");
                        float objNb = 0.5f * (objs[i - 1] + objs[i + 1]);
                        float primNb = 0.5f * (prims[i - 1] + prims[i + 1]);
                        GD.Print($"[fly]   -> objects {objs[i] / Mathf.Max(objNb, 1f):0.000}x of neighbours, "
                               + $"prims {prims[i] / Mathf.Max(primNb, 1f):0.000}x  "
                               + $"({(objs[i] < objNb * 0.97f ? "A DRAW DISAPPEARED" : "same draws, the VIEW changed")})");
                    }
                }
                GD.Print($"[fly] overlap={ResourceField.ImpostorOverlap:0.###} frames={prev.Count} dips={dips} worstRatio={worst:0.000}");
                GD.Print("[fly] NOTE: dips here are NOT known to be the tree->imposter handover -- they survive with the "
                       + "handover removed (UG_TREECULL=5 UG_TREEIMPOVERLAP=10). A floor is now drawn by default because "
                       + "without one, 4 of 6 reported dips were the camera pitching into empty sky. Diff against a control before believing one.");
                GetTree().Quit();
                return;
            }

            var rows = new System.Collections.Generic.List<(float D, int Px)>();
            // Fine steps by default: a shared edge fails over a band narrower than the 5 m the first version used,
            // so a coarse sweep steps straight over the hole and reports health.
            float from = EnvOr("UG_SWEEP_FROM", 300f), to = EnvOr("UG_SWEEP_TO", 370f), step = EnvOr("UG_SWEEP_STEP", 1f);
            for (float d = from; d <= to; d += step)
            {
                cam.GlobalPosition = target + new Vector3(0f, 12f, d);
                cam.LookAt(target + new Vector3(0f, 10f, 0f), Vector3.Up);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                var img = GetViewport().GetTexture()?.GetImage();
                int px = 0;
                if (img != null)
                    for (int y = 0; y < img.GetHeight(); y += 2)
                        for (int x = 0; x < img.GetWidth(); x += 2)
                        {
                            var c = img.GetPixel(x, y);
                            // Anything that is not the flat sky is geometry. Generous threshold so a dim distant
                            // billboard still counts -- undercounting would invent a hole that isn't there.
                            if (Mathf.Abs(c.R - sky.R) + Mathf.Abs(c.G - sky.G) + Mathf.Abs(c.B - sky.B) > 0.06f) px++;
                        }
                rows.Add((d, px));
            }
            GD.Print($"[sweep] overlap={ResourceField.ImpostorOverlap:0.###}");
            int zeros = 0, minPx = int.MaxValue;
            foreach (var (d, px) in rows)
            {
                if (px == 0) zeros++;
                minPx = Mathf.Min(minPx, px);
                GD.Print($"[sweep] {d,5:0}m  {px,7} tree px");
            }
            GD.Print($"[sweep] RESULT distances={rows.Count} empty={zeros} min={minPx}");
            GetTree().Quit();
        }

        static float EnvOr(string n, float dflt)
            => float.TryParse(System.Environment.GetEnvironmentVariable(n), System.Globalization.NumberStyles.Float,
                              System.Globalization.CultureInfo.InvariantCulture, out float v) && v > 0f ? v : dflt;

        // --imptest --shot=OUT : bake the tree impostors and stand each billboard up in a row next to the REAL
        // tree it was baked from, so the two can be compared at a glance.
        //
        // This exists because the L1 suite CANNOT check this feature at all. L1 runs headless, a SubViewport
        // renders nothing headless, so the bake returns an empty image and the graceful path quietly skips every
        // species -- a green suite that proved nothing. The question here is "does this read as a tree", and only
        // an eye answers it.
        async System.Threading.Tasks.Task BuildImpostorTest(string shot)
        {
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.35f, 0.45f, 0.58f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.55f, 0.58f, 0.62f), AmbientLightEnergy = 1f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-48f, 40f, 0f), LightEnergy = 1.2f });

            var field = new ResourceField();
            AddChild(field);
            field.LoadResources("NONE");
            GD.Print($"[imptest] {field.InstanceCount} instances, {field.PendingImpostorTypesForTest} tree species queued");
            await field.BuildTreeImpostorsAsync();
            GD.Print($"[imptest] {field.ImpostorInstancesForTest} billboards built");

            // The field placed everything at its map position, which is nowhere near the camera. Hide it and
            // rebuild a tidy row from the same baked materials instead.
            field.Visible = false;

            var mats = field.DebugImpostorMaterialsForTest();
            float x = 0f;
            foreach (var (name, mat, w, h) in mats)
            {
                var quad = new QuadMesh { Size = new Vector2(w, h), Orientation = PlaneMesh.OrientationEnum.Z };
                AddChild(new MeshInstance3D { Mesh = quad, MaterialOverride = mat, Position = new Vector3(x, h * 0.5f, 0f) });
                GD.Print($"[imptest] {name}: quad {w:0.0} x {h:0.0} m");
                x += w * 1.25f;
            }
            if (mats.Count == 0) { GD.PrintErr("[imptest] NO impostor materials -- the bake produced nothing"); return; }

            var ground = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(400, 400) } };
            ground.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.28f, 0.34f, 0.24f), Roughness = 1f };
            AddChild(ground);

            float span = Mathf.Max(x, 12f), tall = 0f;
            foreach (var m in mats) tall = Mathf.Max(tall, m.H);
            var cam = new Camera3D { Current = true, Fov = 55f };
            AddChild(cam);
            cam.Position = new Vector3(span * 0.5f - 4f, tall * 0.55f, span * 0.95f + tall);
            cam.LookAt(new Vector3(span * 0.5f - 4f, tall * 0.45f, 0f), Vector3.Up);

            // Arm the capture only NOW. Setting it before the bake raced it: each species costs two frames, so
            // the shot fired after three of six and the other three looked like failed bakes -- no error, no
            // billboards, nothing to distinguish "broken" from "not finished yet".
            _shotPath = shot;
        }

        void BuildLampTest()
        {
            string which = System.Environment.GetEnvironmentVariable("UG_LAMP");
            if (string.IsNullOrEmpty(which)) which = "Light_0";
            bool off = System.Environment.GetEnvironmentVariable("UG_LAMPOFF") == "1";
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = off ? new Color(0.42f, 0.52f, 0.68f) : new Color(0.02f, 0.03f, 0.05f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = off ? new Color(0.55f, 0.60f, 0.68f) : new Color(0.035f, 0.045f, 0.06f),
                AmbientLightEnergy = 1f,
                GlowEnabled = true, GlowIntensity = 0.85f, GlowBloom = 0.15f, GlowHdrThreshold = 0.85f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            if (off) AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-52f, 34f, 0f), LightEnergy = 1.1f });

            var gmesh = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(16, 16) } };
            gmesh.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.30f, 0.30f, 0.29f), Roughness = 1f };
            AddChild(gmesh);

            // UG_LAMP_ROOM=1 -- a plain box room (ceiling + four walls) around the fixture.
            //
            // The bare-plane lamptest CANNOT SEE the thing that matters when a ceiling strip stops being an omni and
            // becomes a downward cone: what happens to the ceiling it is bolted to, and to the upper walls. With only
            // a floor in the scene both forms paint the same pool and the shot proves nothing. Build the surfaces the
            // difference lands on, or do not claim to have checked it.
            if (System.Environment.GetEnvironmentVariable("UG_LAMP_ROOM") == "1")
            {
                // ceilY must clear the fixture's own bounds. Light_0 at mountY 3.0 with the in-situ pitch reaches
                // Y=3.75, so the first version of this room -- ceiling at 3.2 -- ran the slab straight THROUGH the
                // prop, and a ceiling decal anchored to the fixture's top projected into empty air above it. The
                // render was byte-identical to having no decal, which reads as "the feature does nothing".
                const float half = 4f, ceilY = 3.85f;
                var wallMat = new StandardMaterial3D { AlbedoColor = new Color(0.52f, 0.50f, 0.47f), Roughness = 1f };
                void Surface(Vector3 pos, Vector3 rotDeg)
                {
                    AddChild(new MeshInstance3D
                    {
                        Mesh = new PlaneMesh { Size = new Vector2(half * 2f, ceilY + 1f) },
                        MaterialOverride = wallMat, Position = pos, RotationDegrees = rotDeg,
                    });
                }
                AddChild(new MeshInstance3D   // ceiling: a plane's face is +Y, so flip it to look down
                {
                    Mesh = new PlaneMesh { Size = new Vector2(half * 2f, half * 2f) },
                    MaterialOverride = wallMat, Position = new Vector3(0f, ceilY, 0f), RotationDegrees = new Vector3(180f, 0f, 0f),
                });
                Surface(new Vector3(0f, ceilY / 2f, -half), new Vector3(90f, 0f, 0f));    // back  (faces +Z, toward camera)
                Surface(new Vector3(-half, ceilY / 2f, 0f), new Vector3(90f, 90f, 0f));   // left  (faces +X)
                Surface(new Vector3(half, ceilY / 2f, 0f), new Vector3(90f, -90f, 0f));   // right (faces -X)
            }

            string objDir = ProjectSettings.GlobalizePath("res://content/objects/");
            var m = ObjMesh.Load(objDir + which + ".obj");
            if (m == null) { GD.PrintErr($"[lamptest] {which}.obj missing"); return; }
            var mat = new StandardMaterial3D { Roughness = 0.85f, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                                               TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest };
            string tex = objDir + which + "_tex.png";
            if (System.IO.File.Exists(tex)) { var img = Image.LoadFromFile(tex); if (img != null) mat.AlbedoTexture = ImageTexture.CreateFromImage(img); }

            bool ceiling = which == "Light_0" || which == "Light_1";
            float mountY = ceiling ? 3.0f : 0.0f;   // ceiling lights hang; floor/desk lamps stand on the ground
            // Light_0 lies FLAT on a ceiling -- all 34 world placements carry pitch ex=270 (tinyclaw), which turns its
            // 4-unit "height" into LENGTH and points the diffuser straight down. UG_LAMP_PITCH=270 renders it in-situ.
            float pitch = 0f; float.TryParse(System.Environment.GetEnvironmentVariable("UG_LAMP_PITCH"), out pitch);
            var mi = new MeshInstance3D { Mesh = m, MaterialOverride = mat, Position = new Vector3(0f, mountY, 0f), RotationDegrees = new Vector3(pitch, 0f, 0f) };
            AddChild(mi);
            LampLight.DebugNoOmni = System.Environment.GetEnvironmentVariable("UG_LAMP_NOOMNI") == "1";   // proof shot: only the emissive part, no room light
            LampLight.CeilingSpot = System.Environment.GetEnvironmentVariable("UG_LAMP_CEILSPOT") == "1";   // ceiling strip as a downward cone (default omni) -- A/B both forms from ONE build
            LampLight.DebugLightPose = System.Environment.GetEnvironmentVariable("UG_LAMP_POSE") == "1";
            if (float.TryParse(System.Environment.GetEnvironmentVariable("UG_LAMP_CEILANGLE"), out var ca) && ca > 0f) LampLight.CeilingSpotAngle = ca;   // sweep the cone width
            if (float.TryParse(System.Environment.GetEnvironmentVariable("UG_LAMP_ENERGY"), out var ce) && ce > 0f) LampLight.Energy = ce;
            if (float.TryParse(System.Environment.GetEnvironmentVariable("UG_LAMP_FILL"), out var cf) && cf >= 0f) LampLight.CeilingFillFraction = cf;   // 0 = cone only, to see what the fill is actually buying
            LampLight.CeilingDecal = System.Environment.GetEnvironmentVariable("UG_LAMP_CEILDECAL") == "1";   // fake the lit ceiling with a projected decal instead of a fill light
            if (float.TryParse(System.Environment.GetEnvironmentVariable("UG_LAMP_DECALSIZE"), out var ds) && ds > 0f) LampLight.CeilingDecalSize = ds;
            if (float.TryParse(System.Environment.GetEnvironmentVariable("UG_LAMP_DECALENERGY"), out var de) && de >= 0f) LampLight.CeilingDecalEnergy = de;
            var bulbSideStr = System.Environment.GetEnvironmentVariable("UG_BULB_SIDE");                 // DeskBulb render-pick: +1 / -1
            if (!string.IsNullOrEmpty(bulbSideStr) && float.TryParse(bulbSideStr, out var bs)) LampLight.DebugBulbSide = bs;
            var lamp = LampLight.Make(new Vector3(0f, mountY, 0f), mi, LampLight.KindFor(which));   // hand the fixture mesh in so the right part glows when lit (LampLight.KindFor = the one prop->kind table)
            AddChild(lamp);
            lamp.SetPowered(!off);
            if (System.Environment.GetEnvironmentVariable("UG_LAMP_OUTLINE") == "1") lamp.SetLookFocused(true);   // verify the whole-lamp look-outline (toggle lamps only)
            GD.Print($"[LAMPTEST] {which} + LampLight, powered={!off}, lit={lamp.LitForTest}");

            var cam = new Camera3D { Current = true, Fov = 60f };
            AddChild(cam);
            // UG_LAMP_ROOM reframes: the default ceiling shot looks UP at the diffuser, which is the one part of the
            // scene a downward cone deliberately stops lighting -- judging "does this still light the room" from it
            // reads as a total blackout when the floor may be fine. Room mode looks ACROSS instead, so floor, far
            // wall and ceiling are all in frame at once.
            if (ceiling && System.Environment.GetEnvironmentVariable("UG_LAMP_ROOM") == "1")
            {
                cam.Position = new Vector3(3.0f, 1.55f, 3.3f);
                cam.LookAt(new Vector3(-0.6f, 1.35f, -1.6f), Vector3.Up);
            }
            else if (ceiling)   // flat ceiling strip: eye level off to the side, look UP at the underside (diffuser)
            {
                cam.Position = new Vector3(3.4f, 0.9f, 3.0f);
                cam.LookAt(new Vector3(0f, mountY - 0.15f, 0f), Vector3.Up);
            }
            else if (which == "Lamp_1")   // standing/floor lamp (~2.3 tall): eye level, frame the shade
            {
                cam.Position = new Vector3(2.2f, 1.4f, 2.8f);
                cam.LookAt(new Vector3(0f, 1.2f, 0f), Vector3.Up);
            }
            else   // desk lamp (~0.88 tall): close in
            {
                cam.Position = new Vector3(0.95f, 0.62f, 1.25f);
                cam.LookAt(new Vector3(0f, 0.42f, 0f), Vector3.Up);
            }
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
            // UG_WEATHER=clear|rain|heavy drives the REAL WeatherManager over this reference scene so the weather
            // system can be render-verified; unset leaves the original forced-rain demo exactly as it was (the
            // existing daynight golden must not move).
            string wmode = System.Environment.GetEnvironmentVariable("UG_WEATHER");
            var dnRain = new RainOverlay { Cycle = cyc, Raining = wmode == null };   // demo the rain too
            AddChild(dnRain);
            if (wmode != null)
            {
                // Freeze the sky for the render: this demo runs a 5 s day, so between two runs the sun moves far
                // enough that a pixel diff measures the LIGHTING, not the weather (first comparison showed light
                // and heavy rain differing from clear by 53.9% vs 54.1% -- pure noise). Frozen, the only variable
                // left is the weather.
                cyc.Speed = 0f; cyc.Time = 0.5f;
                var wm = WeatherManager.Attach(this, dnRain, cyc, seed: 4242);
                // Hold the weather PERPETUALLY for a still frame. Stepping a scheduled shower here does not work:
                // this demo scene runs a 5 s day, so PEI's 0.05-0.15 cycle window is a sub-second shower that a
                // settle loop blows straight through (first attempt rendered blend=0.00 with the type re-rolled).
                if (wmode == "rain") wm.Sim.SetPerpetual(0);
                else if (wmode == "heavy") wm.Sim.SetPerpetual(1);                      // density only, no flash
                else if (wmode == "lightning") { wm.Sim.SetPerpetual(1); wm.Strike(); } // the flash, judged separately
                GD.Print($"[WEATHERSHOT] mode={wmode} stage={wm.Sim.Stage} blend={wm.Sim.BlendAlpha:0.00} active={wm.Sim.Active?.Name ?? "none"}");
            }

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

        // --clocktest: one Clock_0 stood upright facing the camera, its hands carved off by ClockDevice and spun to
        // UG_TIME. Verifies the reach split (hands vs dial+markers) AND the hand angles/direction before wiring it into
        // the world. UG_TIME 0..1 (0=midnight, .5=noon); default 0.375 = 09:00 so the two hands sit apart, easy to read.
        void BuildClockTest()
        {
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.28f, 0.30f, 0.34f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.85f, 0.85f, 0.85f),
                AmbientLightEnergy = 1.0f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-40f, -25f, 0f) });

            float ct = 0.375f;   // 09:00
            var te = System.Environment.GetEnvironmentVariable("UG_TIME");
            if (te != null && float.TryParse(te, out var tv)) ct = tv;
            var dn = new DayNightCycle { Time = ct, Speed = 0f };   // frozen at the test time
            AddChild(dn);

            var mesh = ObjMesh.Load(ProjectSettings.GlobalizePath("res://content/objects/Clock_0.obj"));
            var mat = new StandardMaterial3D { AlbedoColor = new Color(0.82f, 0.82f, 0.82f) };
            var basis = Basis.Identity;   // dial FLAT in XZ (face up +Y), viewed straight down from above -- NO rotation, so nothing mirrors the reading (tinyclaw: the mesh has no -Y geometry, +Y is the face)
            var mi = new MeshInstance3D { Mesh = mesh, MaterialOverride = mat, Transform = new Transform3D(basis, Vector3.Zero) };
            AddChild(mi);
            var handMat = new StandardMaterial3D { AlbedoColor = new Color(0.90f, 0.15f, 0.10f) };   // hands RED so the split + sweep read clearly in the shot
            var cd = ClockDevice.Make(mi, handMat, 0f);
            if (cd != null) AddChild(cd);

            var cam = new Camera3D { Current = true, Fov = 28f };
            AddChild(cam);
            cam.Position = new Vector3(0f, 3f, 0f);   // straight above, looking down -Y at the +Y face
            cam.LookAt(Vector3.Zero, new Vector3(0f, 0f, 1f));   // up-hint = mesh +Z (tinyclaw measured 12 o'clock at +Z) -> 12 at screen-top
            GD.Print($"[CLOCK] time={ct}");
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

            // A small base on the REAL 6 m lattice, one tier per row so the upgrade ladder is visible at a
            // glance. Everything goes through StructureManager.Place, so what this renders is what the
            // placement rules actually permit -- a demo that bypassed them would look right and prove nothing.
            var sm = new StructureManager { Name = "StructureManager" };
            AddChild(sm);
            var bt = new BuildTool();
            AddChild(bt);
            for (int tier = 0; tier < StructureCatalog.TierCount; tier++)
            {
                float z = (tier - 1) * StructureCatalog.EdgeLength;
                bt.Spawn(new Vector3(0f, 0f, z), EConstruct.Floor, tier);                       // 2 tiles of floor
                bt.Spawn(new Vector3(StructureCatalog.EdgeLength, 0f, z), EConstruct.Floor, tier);
                bt.Spawn(new Vector3(-StructureCatalog.HalfEdge, 0f, z), EConstruct.Wall, tier); // wall on the outer edge
                bt.Spawn(new Vector3(StructureCatalog.EdgeLength * 1.5f, 0f, z), EConstruct.Wall, tier);
            }

            // Pillars at the tile CORNERS and a roof over the middle tile. The corners are what the pillar
            // lattice exists for: aimed at the same points, the face rule would snap all four into the middle
            // of the tile they are supposed to hold up, so a render that shows them standing at the corners is
            // the check on it. Counted rather than assumed -- these go through the same CanPlace as everything
            // else, and silently placing nothing would still render a tidy-looking base.
            int pillars = 0;
            float midZ = 0f;
            foreach (float px in new[] { -StructureCatalog.HalfEdge, StructureCatalog.HalfEdge })
                foreach (float pz in new[] { midZ - StructureCatalog.HalfEdge, midZ + StructureCatalog.HalfEdge })
                    if (bt.Spawn(new Vector3(px, 0f, pz), EConstruct.Pillar, 2) != null) pillars++;
            bool roof = bt.Spawn(new Vector3(0f, StructureCatalog.WallHeight, midZ), EConstruct.Roof, 2) != null;
            GD.Print($"[BUILD] corner pillars placed: {pillars}/4, roof: {roof}");

            // INTEGRATION proof: a barricade mounted on a structure WALL. This is the whole point of merging the
            // two branches -- the ground DeployablePlacer rejected any surface with normal.y < 0.01, so before
            // this a barricade could only ever sit on the floor. Mounted through Barricade.PlaceOnSurface with
            // the wall's real outward face, the same call the held-item place flow makes.
            // a doorway on the front edge: same slot class as a wall, with a hole you can actually walk through
            float frontZ = StructureCatalog.EdgeLength + StructureCatalog.HalfEdge;
            bool doorway = bt.Spawn(new Vector3(0f, 0f, frontZ), EConstruct.Doorway, 2) != null;
            GD.Print($"[BUILD] doorway: {doorway}");

            int mounted = 0;
            foreach (var pc in StructureManager.Instance.All)
            {
                if (pc.Construct != EConstruct.Wall || pc.Tier != 2) continue;   // one, on a metal wall
                var n = StructureManager.FaceNormal(pc);
                float halfThick = StructureCatalog.Extents(EConstruct.Wall).Z * 0.5f;
                var at = pc.Pos + Vector3.Up * StructureCatalog.WallPivotOffset + n * (halfThick + 0.02f);
                Barricade.PlaceOnSurface(this, DeployableDef.MetalBarricade, at, n, BarricadePlacer.YawFacing(n));
                mounted++;
                break;
            }
            GD.Print($"[BUILD] wall-mounted barricades: {mounted}");

            // Framed off the LATTICE, not hardcoded metres. The old camera sat at (6, 4.5, 7) for a 3 m demo;
            // on the real 6 m tile that is INSIDE the base looking at the back of a wall, which is what the
            // first render of this showed. Deriving it from EdgeLength means the shot survives the next
            // geometry change instead of quietly framing the inside of something.
            float span = StructureCatalog.EdgeLength * StructureCatalog.TierCount;
            var overview = new Camera3D { Current = true, Fov = 55f };
            AddChild(overview);
            // Higher than a natural eye-level 3/4: at 0.85x span the perimeter walls stand between the camera
            // and the corner pillars, so the shot showed two of four and could not evidence the corner lattice
            // at all. Looking DOWN into the base is the only angle from which "a pillar at each corner" is a
            // checkable claim rather than a caption.
            overview.Position = new Vector3(span * 0.95f, span * 1.45f, span * 1.35f);
            overview.LookAt(new Vector3(StructureCatalog.HalfEdge, 1.0f, 0f), Vector3.Up);
            GD.Print("[BUILD] scripted a small structure (floors + walls + corner pillars + roof)");
        }

        // Building-tool demo: walls carrying openings at the MEASURED retail dimensions, so the first thing
        // anyone looks at is the real geometry rather than a mock-up. Every wall here goes through the same
        // WallOpenings.Solids the editor drag path uses -- there is no separate preview code.
        void BuildWallDemo()
        {
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.42f, 0.55f, 0.72f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.62f, 0.64f, 0.67f),
                AmbientLightEnergy = 0.9f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            // UG_WALLNOSHADOW / UG_WALLNOMESH exist to separate a GEOMETRY fault from a SHADING one. A thin
            // box at grazing incidence to the sun and a bowtie quad look identical in a beauty shot, and
            // guessing between them from one render is how you fix the wrong thing twice.
            AddChild(new DirectionalLight3D
            {
                RotationDegrees = new Vector3(-48f, -52f, 0f), LightEnergy = 1.25f,
                ShadowEnabled = System.Environment.GetEnvironmentVariable("UG_WALLNOSHADOW") != "1",
            });

            var ground = new StaticBody3D { CollisionLayer = 1 << 0 };
            ground.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
            var gm = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(120, 120) } };
            gm.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.31f, 0.35f, 0.29f) };
            ground.AddChild(gm);
            AddChild(ground);

            float H = UnturnedSim.WallOpenings.DoorHeight;          // 4.25
            float sill = UnturnedSim.WallOpenings.WindowSill;       // 1.00
            float wh = UnturnedSim.WallOpenings.WindowHeight;       // 2.75
            float pitch = UnturnedSim.WallOpenings.StoreyPitch;     // 4.75
            const float L = 12f, D = 9f;

            // UG_WALLMAT picks the retail palette; default 0. There are 52 sampled from the buildings.
            int matId = int.TryParse(System.Environment.GetEnvironmentVariable("UG_WALLMAT"), out var mi) ? mi : 0;
            GD.Print($"[walls] material {matId} of {WallMaterials.Count}: {WallMaterials.At(matId).Name}");

            WallSurface Wall(float len, Vector3 pos, float yaw)
            {
                var w = new WallSurface { Length = len, Height = H, Position = pos, RotationDegrees = new Vector3(0f, yaw, 0f), MaterialId = matId };
                AddChild(w);
                return w;
            }

            // UG_WALLSWATCH renders one panel per palette instead of the room -- the whole material range in a
            // single frame, which is the only way to see that the roles were sampled right across all 52 and
            // not just on the house they were derived from.
            if (System.Environment.GetEnvironmentVariable("UG_WALLSWATCH") == "1")
            {
                ground.Visible = false;          // a swatch is a chart, not a scene
                int n = WallMaterials.Count, cols = 7;
                const float PW = 11f, GAP = 2.0f;
                for (int i = 0; i < n; i++)
                {
                    int cx = i % cols, cy = i / cols;
                    const float LIFT = 3.0f;   // clear of the ground plane, so no row is half-buried
                    var m = WallMaterials.At(i);
                    var w = new WallSurface
                    {
                        Length = PW, Height = H, Thickness = m.Thickness, MaterialId = i,
                        Position = new Vector3(cx * (PW + GAP), LIFT + cy * (H + GAP), 0f),
                    };
                    AddChild(w);
                    w.Openings.Add(new UnturnedSim.WallOpening(1.5f, 0f, 2.5f, H - 0.5f));      // door
                    w.Openings.Add(new UnturnedSim.WallOpening(6.5f, sill, 2.81f, wh));         // window
                    w.Rebuild();
                    // name each panel: an id is useless if you cannot tell which building it came off
                    w.AddChild(new Label3D
                    {
                        Text = $"{i}  {m.Name}", FontSize = 96, PixelSize = 0.006f,
                        Modulate = new Color(1f, 1f, 1f), OutlineSize = 24,
                        Position = new Vector3(PW * 0.5f, -0.55f, 0.4f),
                        Billboard = BaseMaterial3D.BillboardModeEnum.Disabled,
                    });
                }
                float wide = cols * (PW + GAP) - GAP, tall = Mathf.Ceil(n / (float)cols) * (H + GAP) - GAP;
                // Frame the grid rather than guessing a distance: a swatch you have to squint at proves the
                // palettes loaded and nothing else, which is not what the shot is for.
                const float Fov = 50f;
                float vt = Mathf.Tan(Mathf.DegToRad(Fov) * 0.5f);
                var vp = GetViewport().GetVisibleRect().Size;
                float dist = Mathf.Max(tall * 0.5f / vt, wide * 0.5f / (vt * (vp.X / vp.Y))) * 1.06f;
                var target = new Vector3(wide / 2f, 3.0f + tall / 2f, 0f);
                var scam = new Camera3D { Current = true, Fov = Fov };
                AddChild(scam);
                scam.Position = target + new Vector3(0f, 0f, dist);
                scam.LookAt(target, Vector3.Up);
                GD.Print($"[walls] swatch: {n} palettes");
                return;
            }

            // A closed room, so corners are visible. Walls run along local +X; yaw -90 turns +X into +Z.
            //
            // Windows are GLAZED here and in DrawDemoBuilding, and the two have to agree. They are already
            // two hand-written copies of one room -- the note below DrawDemoBuilding says so -- and glazing
            // only one of them is exactly how that drift shows up: the first render of this scene came back
            // with empty holes because the OTHER copy was the one that got the glass.
            var front = Wall(L, new Vector3(-L / 2f, 0f, 0f), 0f);
            front.Openings.Add(DooredOpening(new UnturnedSim.WallOpening(1.0f, 0f, 2.5f, H - 0.5f)));   // person door, floor-pinned -- a real swinging door
            front.Openings.Add(GlazedOpening(new UnturnedSim.WallOpening(5.0f, sill, 3.31f, wh)));       // measured window widths
            front.Openings.Add(GlazedOpening(new UnturnedSim.WallOpening(9.0f, sill, 2.81f, wh), 0x8FBFA0));

            var back = Wall(L, new Vector3(-L / 2f, 0f, -D), 0f);
            back.Openings.Add(new UnturnedSim.WallOpening(2.0f, 0f, 8.0f, H - 0.25f));    // 8m garage: only reachable because walls are DRAWN, not 6m tiles

            var left = Wall(D, new Vector3(-L / 2f, 0f, -D), -90f);
            left.Openings.Add(GlazedOpening(new UnturnedSim.WallOpening(2.5f, sill, 3.31f, wh)));

            var right = Wall(D, new Vector3(L / 2f, 0f, -D), -90f);
            right.Openings.Add(GlazedOpening(new UnturnedSim.WallOpening(1.5f, sill, 2.81f, wh)));
            right.Openings.Add(GlazedOpening(new UnturnedSim.WallOpening(5.5f, sill, 2.81f, wh)));

            // second storey at the measured 4.75 pitch (4.25 opening + 0.50 slab)
            var up = Wall(L, new Vector3(-L / 2f, pitch, 0f), 0f);
            up.Openings.Add(GlazedOpening(new UnturnedSim.WallOpening(2.0f, sill, 2.81f, wh)));
            up.Openings.Add(GlazedOpening(new UnturnedSim.WallOpening(7.0f, sill, 3.31f, wh)));
            var upSide = Wall(D, new Vector3(-L / 2f, pitch, -D), -90f);
            upSide.Openings.Add(GlazedOpening(new UnturnedSim.WallOpening(3.0f, sill, 2.81f, wh)));

            // Rebuild AFTER the openings are added. WallSurface builds itself on _Ready, which fires the moment
            // it is added to the tree -- so anything that mutates Openings afterwards has to say so. Without
            // this every wall renders solid and looks like the partition is broken when it never ran.
            foreach (var w in new[] { front, back, left, right, up, upSide }) w.Rebuild();

            var cam = new Camera3D { Current = true, Fov = 52f };
            AddChild(cam);
            if (System.Environment.GetEnvironmentVariable("UG_WALLCLOSE") == "1")
            {   // close on the front-wall window: the frame/reveal detail, straight on. Every OTHER wall is
                // hidden -- at this range the far side of the room shows through the openings, and a jamb seen
                // edge-on through a window reads exactly like a broken frame on the near one.
                foreach (var w in new[] { back, left, right, up, upSide }) w.Visible = false;
                if (System.Environment.GetEnvironmentVariable("UG_WALLNOMESH") == "1")
                    front.GetNode<MeshInstance3D>("Mesh").Visible = false;   // trim alone, nothing to intersect
                if (System.Environment.GetEnvironmentVariable("UG_WALLDUMP") == "1")
                {
                    // Inspect the COMMITTED mesh, not a re-derivation of what it should be: the suspect step is
                    // what SurfaceTool does to the boxes, so a second copy of the box maths proves nothing.
                    //
                    // The ratio is the tell. Flat-shaded boxes cannot share a vertex between two faces, so
                    // indexing leaves roughly two verts per triangle; smoothed, every face meeting at a corner
                    // collapses onto one vertex and it drops below one. That is what a jamb necking like a
                    // turned spindle looks like from the data, and it is visible here long before it is
                    // obvious in a beauty shot.
                    foreach (var (label, node) in new[] { ("wall", "Mesh"), ("trim", "TrimMesh") })
                    {
                        var m = front.GetNode<MeshInstance3D>(node).Mesh;
                        if (m == null || m.GetSurfaceCount() == 0) { GD.Print($"[walldump] {label}: empty"); continue; }
                        var arr = m.SurfaceGetArrays(0);
                        int nv = ((Vector3[])arr[(int)Mesh.ArrayType.Vertex]).Length;
                        int nt = ((int[])arr[(int)Mesh.ArrayType.Index]).Length / 3;
                        float ratio = nt > 0 ? nv / (float)nt : 0f;
                        GD.Print($"[walldump] {label}: {nt} tris, {nv} verts, {ratio:F2} verts/tri"
                                 + (ratio < 1.5f ? "  <-- SMOOTHED, corners will bulge" : ""));
                    }
                }
                cam.Position = new Vector3(-0.5f, 2.4f, 6.5f);
                cam.LookAt(new Vector3(-0.5f, 2.4f, 0f), Vector3.Up);
            }
            else
            {
                cam.Position = new Vector3(13f, 7.5f, 24f);
                cam.LookAt(new Vector3(0f, 3.4f, -3f), Vector3.Up);
            }
            GD.Print($"[walls] 6 walls; front run partitions into {UnturnedSim.WallOpenings.Solids(L, H, front.Openings).Count} solids, garage wall into {UnturnedSim.WallOpenings.Solids(L, H, back.Openings).Count}");
        }

        // The same room the --walls demo builds, laid out on the Buildings stage so the editor capture shows a
        // real building rather than an empty plane. Deliberately the SAME numbers, so the tool and the demo
        // cannot drift into disagreeing about what a wall of a given size looks like.
        /// <summary>Mark an opening as glass-filled. Windows come glazed and doors/garage spans do not, which
        /// is the same rule the archetype presets apply -- so both demo rooms show what the tool actually
        /// produces instead of a special case. ONE helper because there are two hand-written copies of this
        /// room and they have already drifted once.</summary>
        static UnturnedSim.WallOpening GlazedOpening(UnturnedSim.WallOpening o, int tint = 0)
        { o.Glazed = true; o.GlassTint = tint; return o; }

        /// <summary>Hang a door in an opening. The door opening in both demo rooms carries one, so every editor
        /// render shows what the tool produces rather than an empty hole -- the same reason the windows are
        /// glazed there.</summary>
        static UnturnedSim.WallOpening DooredOpening(UnturnedSim.WallOpening o, string prop = "Door_Pine")
        { o.DoorProp = prop; return o; }

        static void DrawDemoBuilding(EditorBuildings b)
        {
            float H = UnturnedSim.WallOpenings.DoorHeight;
            float sill = UnturnedSim.WallOpenings.WindowSill;
            float wh = UnturnedSim.WallOpenings.WindowHeight;
            // Sits on the same clearance a hand-placed building does, so the demo shows what the tool
            // actually produces rather than a special case with its floor buried.
            var o = EditorBuildings.StageOrigin + new Vector3(0f, EditorBuildings.GroundClearance, 0f);
            const float L = 12f, D = 9f;
            b.ActiveMaterial = 24;                                   // House_00

            var front = b.AddWall(o + new Vector3(-L / 2f, 0f, 0f), 0f, L);
            front.Openings.Add(DooredOpening(new UnturnedSim.WallOpening(1.0f, 0f, 2.5f, H - 0.5f)));   // a real swinging door
            front.Openings.Add(GlazedOpening(new UnturnedSim.WallOpening(5.0f, sill, 3.31f, wh)));
            front.Openings.Add(GlazedOpening(new UnturnedSim.WallOpening(9.0f, sill, 2.81f, wh), 0x8FBFA0));

            var back = b.AddWall(o + new Vector3(-L / 2f, 0f, -D), 0f, L);
            back.Openings.Add(new UnturnedSim.WallOpening(2.0f, 0f, 8.0f, H - 0.25f));            // garage: no glass

            var left = b.AddWall(o + new Vector3(-L / 2f, 0f, -D), -90f, D);
            left.Openings.Add(GlazedOpening(new UnturnedSim.WallOpening(2.5f, sill, 3.31f, wh)));

            var right = b.AddWall(o + new Vector3(L / 2f, 0f, -D), -90f, D);
            right.Openings.Add(GlazedOpening(new UnturnedSim.WallOpening(1.5f, sill, 2.81f, wh)));
            right.Openings.Add(GlazedOpening(new UnturnedSim.WallOpening(5.5f, sill, 2.81f, wh)));

            foreach (var w in b.Walls) w.Rebuild();
            // Close the corners, which a user gets for free and this did not. Corner solving runs from the
            // draw-release handler, and the demo lays its walls by calling AddWall directly -- so every
            // screenshot of the editor has been showing a building with an open notch at every corner while
            // the same building drawn by hand came out solid. strawberry_cow, off a render: "corners arent
            // getting solved in ur render?"
            b.SolveCorners();
            var floor = b.AddSlab(UnturnedSim.SurfaceKind.Floor);
            // a stairwell, to show that a hole in a floor is the same opening as a hole in a wall
            if (floor != null)
            {
                floor.Openings.Add(new UnturnedSim.WallOpening(floor.Length - 3.6f, floor.Height * 0.5f - 1.4f, 2.8f, 2.8f));
                floor.Rebuild();
            }
            b.AddFoundation();
            b.AddGableRoof(20f);
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

        // Headless NET-OBSERVER (--netobserve): a diagnostics client that stands up ONLY netcode +
        // replica state and logs the client-side vehicle picture (NetObserver.cs). World scaffold =
        // the SAME WorldMode.Dedicated + syncLoad:true build the dedicated server uses, because the
        // Client world-build is headless-UNSAFE: syncLoad:false awaits RenderingServer.FramePostDraw
        // between load phases (WorldBuilder.Phase) and the --headless dummy renderer never presents a
        // frame -> the await never resumes and BuildClient hangs forever. The Dedicated path never
        // frame-yields (the live server proves it boots headless). noZombies always: the observer is
        // authority for NOTHING -- a local zombie/loot sim would be pure CPU waste on the shared box.
        // Cheapest run: leave UG_UNTURNED_DIR unset -> the flat-fallback scaffold (no map, no local
        // vehicle/loot spawns); replica observation reads the wire, it never needs local terrain.
        async void BuildNetObserver()
        {
            // async void swallows exceptions silently (the BuildDedicated trap) -- surface + hard-exit.
            try
            {
                var res = await WorldBuilder.BuildFullWorld(this, WorldMode.Dedicated, _mapRoot, _mapPlace,
                    noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: ActiveHoliday());
                _worldReady = res.Ready;
                AddChild(new NetObserver { Host = _connectHost, Port = PortEnv(), Driver = res.Sim });
                GD.Print($"[NETOBS] scaffold up (terrain={(res.Terr != null ? "real map" : "fallback plane")}); observing {_connectHost}:{PortEnv()}");
            }
            catch (System.Exception e)
            {
                GD.PrintErr($"[NETOBS] build FAILED: {e}");
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




        // UG_VERTEXLIGHT=1: apply vertex shading at boot, so the look can be captured in a render rather than
        // only toggled at runtime through the console. The perf question needs real hardware (this box is
        // software-rasterised) but the LOOK does not -- lavapipe draws the right image, just slowly.
        int _vertexLightQuiet = -1;   // -1 = not started; counts consecutive passes that changed nothing

        public override void _Process(double delta)
        {
            // Re-applied until two consecutive passes change nothing, rather than once on the first frame:
            // materials are still being created while the world builds, so a single early pass converts
            // whatever happened to exist yet and silently leaves the rest per-pixel -- which would make a
            // side-by-side look like "vertex lighting barely changes anything".
            if (_vertexLightQuiet < 2 && System.Environment.GetEnvironmentVariable("UG_VERTEXLIGHT") == "1")
            {
                GraphicsOptions.VertexShading = true;
                int n = GraphicsOptions.ApplyShading(GetTree()?.Root);
                _vertexLightQuiet = n == 0 ? Mathf.Max(0, _vertexLightQuiet) + 1 : 0;
                if (n > 0) GD.Print($"[vertexlight] {n} material(s) -> per-vertex");
            }
            // FIRST, because several capture modes below own the frame and return before the main
            // capture gate -- a watchdog placed at that gate never runs for --vehicle/--rig/--menushot.
            if (_shotRequested != null && ShotWatchdogTripped()) return;
            if (_trAnim) StepTrainAnim((float)delta);   // --traintrack: drive the train along the curve
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
            // --- UG_TERRPERF=1 : what does the TERRAIN cost? (strawberry: "dumbing down terrain verts at a
            // distance ... killing/reducing things the player cant see is a huge w")
            //
            // Toggles Terrain.Active off and back ON inside ONE run and reports each phase. Two separate runs would
            // not do: the frame-to-frame spread under lavapipe is wide enough to swallow the effect, so the only
            // readable signal is the same process measuring both states minutes apart. The trailing ON phase is the
            // control -- if it does not come back to the first ON reading, the machine drifted and the middle
            // number means nothing.
            //
            // WHAT THIS CAN AND CANNOT SEE: prims and draws are exact and hardware-independent -- they say how much
            // geometry terrain actually submits, which is the question LOD answers. The ms is lavapipe, a software
            // rasteriser, so its absolute value is meaningless on real hardware and only the RATIO hints at
            // anything. Do not quote the milliseconds as a frame-time saving.
            if (System.Environment.GetEnvironmentVariable("UG_TERRPERF") == "1" && Terrain.Active != null && _worldReady)
            {
                const int Phase = 40;    // frames per phase; the first 12 of each are discarded as settle
                int ph = _tpFrame / Phase;
                if (ph < 3)
                {
                    bool want = ph != 1;   // ON, OFF, ON
                    if (_tpFrame == 0) GD.Print($"[terrperf] probe engaged, terrain has {Terrain.Active.GetChildCount()} children");
                    if (Terrain.Active.Visible != want) Terrain.Active.Visible = want;
                    int inPhase = _tpFrame % Phase;
                    if (inPhase >= 12) { _tpPrims += Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame); _tpDraws += Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame); _tpMs += Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0; _tpN++; }
                    if (inPhase == Phase - 1)
                    {
                        GD.Print($"[terrperf] {(want ? "terrain ON " : "terrain OFF")} n={_tpN} prims={_tpPrims / _tpN:0} draws={_tpDraws / _tpN:0} processMs={_tpMs / _tpN:0.00} (lavapipe: ratio only)");
                        _tpPrims = _tpDraws = _tpMs = 0.0; _tpN = 0;
                    }
                    _tpFrame++;
                }
                else if (ph == 3) { GD.Print("[terrperf] done"); _tpFrame++; }
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
                    if (_vmAimed && _frame == _vmAimStart + 30 && System.Environment.GetEnvironmentVariable("UG_ADS") != "1") _vm.SetAiming(false);   // UG_ADS=1: HOLD ads for a sight/red-dot showcase render instead of releasing at +30
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
                else if (_heliTest && _veh != null)
                {
                    // SCRIPTED MANEUVER SEQUENCE: climb -> cruise -> coordinated turn -> sideways slide -> level.
                    //
                    // WHY BOTH STATE AND TICKS. The CLIMB is state-driven (it ends when the aircraft is actually
                    // up at height), because altitude is a physics outcome and a fixed tick count would cut the
                    // climb short the moment anyone retunes the collective. The MANEUVERS are tick-budgeted,
                    // because their point is screen time -- "bank for three seconds" is the actual requirement,
                    // and there is no physical target that ending a turn corresponds to. Mixing the two is
                    // deliberate rather than sloppy: each phase ends on whichever of the two it genuinely means.
                    var hb = _veh.GlobalTransform.Basis;
                    float altH = _veh.GlobalPosition.Y;
                    var velH = _veh.LinearVelocity;
                    float fwdSpd = velH.Dot(-hb.Z);
                    float latSpd = velH.Dot(hb.X);            // + = sliding right
                    const float TargetAlt = 26f;
                    float noseDeg = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(-hb.Z.Y, -1f, 1f)));
                    // SIGN: NEGATED so that positive == bank RIGHT, matching DriveHeli's command convention
                    // (roll +1 applies torque about -Z, which tips the body +X axis DOWN -> right wing down ->
                    // hb.X.Y is NEGATIVE for a right bank). Without this negation the measurement and the command
                    // disagree in sign, so `(target - measured) * gain` is POSITIVE FEEDBACK: it banks right, the
                    // error grows, it commands harder right. That is not theoretical -- it rolled to 46 deg,
                    // lost lift and flew itself into the ground from 26 m on the first run of this sequence.
                    // Pitch does NOT need this: the nose is -Z, so `Asin(-hb.Z.Y)` is already positive for nose-up,
                    // which is why the climb and cruise phases worked while the turn diverged.
                    float rollDeg = -Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(hb.X.Y, -1f, 1f)));

                    // Collective is a PROPORTIONAL HOLD on target altitude, never a hardcoded hover number --
                    // "the number that hovers" is exactly what a physics retune moves, so a constant here would
                    // make every future render show a phantom climb and read as a flight-model bug. Damped by
                    // vertical speed so it settles instead of porpoising.
                    float coll = Mathf.Clamp((TargetAlt - altH) * 0.22f - velH.Y * 0.30f, -1f, 1f);

                    // Phase advance. CLIMB holds until it is genuinely up and no longer rising fast.
                    if (_heliPhase == 0 && altH > TargetAlt * 0.92f && Mathf.Abs(velH.Y) < 1.5f) { _heliPhase = 1; _heliPhaseTick = 0; }
                    else if (_heliPhase == 1 && (fwdSpd > 10f || _heliPhaseTick > 260)) { _heliPhase = 2; _heliPhaseTick = 0; }
                    else if (_heliPhase == 2 && _heliPhaseTick > 220) { _heliPhase = 3; _heliPhaseTick = 0; }   // ~4.4s of turn
                    else if (_heliPhase == 3 && _heliPhaseTick > 200) { _heliPhase = 4; _heliPhaseTick = 0; }   // ~4.0s of slide
                    _heliPhaseTick++;

                    float pitchIn = 0f, rollIn = 0f, yawIn = 0f;
                    switch (_heliPhase)
                    {
                        case 1:   // CRUISE -- nose down to a held ~12 deg and let it accelerate
                            pitchIn = Mathf.Clamp((-12f - noseDeg) * 0.05f, -0.35f, 0.35f);
                            break;
                        case 2:   // COORDINATED TURN -- bank right AND feed yaw, so the nose follows the turn
                            pitchIn = Mathf.Clamp((-8f - noseDeg) * 0.05f, -0.3f, 0.3f);
                            rollIn = Mathf.Clamp((22f - rollDeg) * 0.05f, -0.4f, 0.4f);
                            yawIn = 0.35f;
                            break;
                        case 3:   // SIDEWAYS SLIDE -- roll LEFT with NO yaw. The lift vector tilts, so it
                                  // translates laterally while the nose keeps pointing where it was. That is what
                                  // makes this read as a slide rather than a turn, and it is why yaw is 0 here.
                            pitchIn = Mathf.Clamp((0f - noseDeg) * 0.05f, -0.3f, 0.3f);
                            rollIn = Mathf.Clamp((-18f - rollDeg) * 0.05f, -0.4f, 0.4f);
                            break;
                        case 4:   // RECOVER -- wings level, nose level, back to a hover
                            pitchIn = Mathf.Clamp((0f - noseDeg) * 0.05f, -0.3f, 0.3f);
                            rollIn = Mathf.Clamp((0f - rollDeg) * 0.06f, -0.4f, 0.4f);
                            break;
                    }
                    _veh.DriveHeli(coll, yawIn, pitchIn, rollIn, delta);

                    if (_frame % 60 == 0)
                        GD.Print($"[helitest] t={_frame} phase={_heliPhase} alt={altH:0.0}m fwd={fwdSpd:0.0} lat={latSpd:+0.0;-0.0;0.0} vy={velH.Y:+0.0;-0.0;0.0} nose={noseDeg:+0.0;-0.0;0.0} roll={rollDeg:+0.0;-0.0;0.0} coll={coll:0.00}");

                    if (_vehCam != null)
                    {   // Chase cam on a WORLD-UP basis: follows position and heading, never rolls with the
                        // airframe. A bank therefore reads as the aircraft banking rather than the world tilting,
                        // and because the heading is unchanged during the slide, the lateral drift is visible
                        // against the camera instead of being cancelled by it.
                        var ht = _veh.GetGlobalTransformInterpolated();
                        var fwdH = -ht.Basis.Z; fwdH.Y = 0f;
                        fwdH = fwdH.LengthSquared() > 0.001f ? fwdH.Normalized() : Vector3.Forward;
                        _vehCam.GlobalPosition = ht.Origin - fwdH * 12f + Vector3.Up * 4.5f;
                        _vehCam.LookAt(ht.Origin + fwdH * 3f, Vector3.Up);
                    }
                    return;
                }
                else if (_planeTest && _veh != null)
                {
                    // SCRIPTED FIXED-WING FLIGHT, driven off the plane's STATE (frame-rate-map independent):
                    // full throttle; hold level while it accelerates across the water; rotate/climb the moment it
                    // lifts off the pontoons; then bank once it has climbed out, to show the lift vector carry it
                    // into the turn (master's "realistic" bank-to-turn model).
                    float alt = _veh.GlobalPosition.Y;
                    float spd = _veh.LinearVelocity.Length();
                    if (System.Environment.GetEnvironmentVariable("UG_PLANETAXI") == "1")   // TAXI test: low throttle + full right rudder on the ground -> does it TURN + stay stable?
                    {
                        _veh.DrivePlane(0.35f, 1f, 0f, 0f, delta);
                        if (_vehCam != null) { var vt3 = _veh.GetGlobalTransformInterpolated(); _vehCam.GlobalPosition = vt3.Origin + new Vector3(0f, 14f, 14f); _vehCam.LookAt(vt3.Origin, Vector3.Up); }
                        return;
                    }
                    if (System.Environment.GetEnvironmentVariable("UG_PLANEIDLE") == "1")   // just SIT (no input) -> reproduce the "freak out on ground contact" report
                    {
                        _veh.DrivePlane(0f, 0f, 0f, 0f, delta);
                        if (_vehCam != null) { var vi = _veh.GetGlobalTransformInterpolated(); var fi = -vi.Basis.Z; fi.Y = 0f; fi = fi.LengthSquared() > 0.001f ? fi.Normalized() : Vector3.Forward; var ri = new Vector3(fi.Z, 0f, -fi.X); _vehCam.GlobalPosition = vi.Origin + ri * 8.5f + Vector3.Up * 0.8f; _vehCam.LookAt(vi.Origin + Vector3.Down * 0.4f, Vector3.Up); }   // LOW SIDE profile -> see the hull + gear vs the ground line
                        return;
                    }
                    if (System.Environment.GetEnvironmentVariable("UG_PLANEGEAR") == "1")
                    {   // GEAR RETRACTION demo: full-throttle takeoff + a SIDE, near-level cam so the wheels folding up read
                        var pbg = _veh.GlobalTransform.Basis;
                        float noseDegG = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(-pbg.Z.Y, -1f, 1f)));
                        float pitchG = Mathf.Clamp((9f - noseDegG) * 0.06f, -0.25f, 0.25f);
                        _veh.DrivePlane(1f, 0f, _veh.Afloat ? (spd > 11f ? 0.55f : 0f) : pitchG, 0f, delta);
                        if (_frame == 260) _veh.ToggleGear();   // gear is MANUAL now -> trigger the retract mid-render so the demo still shows the fold
                        if (_vehCam != null)
                        {
                            var vtG = _veh.GetGlobalTransformInterpolated();
                            var fwdG = -vtG.Basis.Z; fwdG.Y = 0f; fwdG = fwdG.LengthSquared() > 0.001f ? fwdG.Normalized() : Vector3.Forward;
                            var rightG = new Vector3(fwdG.Z, 0f, -fwdG.X);
                            _vehCam.GlobalPosition = vtG.Origin + rightG * 6.5f + fwdG * 0.5f + Vector3.Up * 0.2f;   // off to the side, near level -> the belly gear is visible
                            _vehCam.LookAt(vtG.Origin + Vector3.Down * 0.2f, Vector3.Up);
                        }
                        return;
                    }
                    if (System.Environment.GetEnvironmentVariable("UG_PLANETURN") == "1")
                    {   // hard continuous BANK -> the jet circles -> the WORLD-SPACE contrails curve into arcs; a
                        // high bird's-eye cam (fixed world orientation, follows position) shows the curved trails.
                        var pbt = _veh.GlobalTransform.Basis;
                        float noseDegT = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(-pbt.Z.Y, -1f, 1f)));
                        float rollDegT = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(pbt.X.Y, -1f, 1f)));
                        float pitchT = Mathf.Clamp((8f - noseDegT) * 0.05f, -0.2f, 0.25f);
                        float targetRollT = _frame < 170 ? 0f : 40f;   // roll into a hard sustained bank once airborne
                        float rollT = Mathf.Clamp((rollDegT - targetRollT) * 0.04f, -0.4f, 0.4f);
                        _veh.DrivePlane(1f, 0f, _veh.Afloat ? (spd > 11f ? 0.55f : 0f) : pitchT, rollT, delta);
                        if (_vehCam != null)
                        {
                            var vtT = _veh.GetGlobalTransformInterpolated();
                            var fwdT = -vtT.Basis.Z; fwdT.Y = 0f; fwdT = fwdT.LengthSquared() > 0.001f ? fwdT.Normalized() : Vector3.Forward;
                            _vehCam.GlobalPosition = vtT.Origin - fwdT * 15f + Vector3.Up * 7f;   // chase behind the flattened heading + elevated -> the curving trails sweep in from the side
                            _vehCam.LookAt(vtT.Origin + Vector3.Up * 0.5f, Vector3.Up);
                        }
                        return;
                    }
                    if (System.Environment.GetEnvironmentVariable("UG_PLANETOP") == "1")
                    {   // TOP-DOWN inspection: straight down over the cockpit, nose = up in frame
                        _veh.DrivePlane(0f, 0f, 0f, 0f, delta);
                        if (_vehCam != null)
                        {
                            var vt = _veh.GetGlobalTransformInterpolated(); var b = vt.Basis; var fwd = -b.Z;
                            _vehCam.GlobalPosition = vt.Origin + Vector3.Up * 8f + fwd * 1.5f;
                            _vehCam.LookAt(vt.Origin + fwd * 1.5f + Vector3.Up * 1.0f, fwd);
                        }
                        return;
                    }
                    if (System.Environment.GetEnvironmentVariable("UG_PLANECOCKPIT") == "1")
                    {   // COCKPIT INSPECTION: park the jet + hold a front-top-3/4 cam looking down into the cockpit
                        _veh.DrivePlane(0f, 0f, 0f, 0f, delta);
                        if (_vehCam != null)
                        {
                            var vt = _veh.GetGlobalTransformInterpolated(); var b = vt.Basis; var fwd = -b.Z;
                            _vehCam.GlobalPosition = vt.Origin + b.X * 3.5f + b.Y * 4.0f + fwd * 6.5f;
                            _vehCam.LookAt(vt.Origin + b.Y * 1.8f + fwd * 2.0f, Vector3.Up);
                        }
                        return;
                    }
                    if (System.Environment.GetEnvironmentVariable("UG_BELLYSHOT") == "1")
                    {   // BELLY inspection: pin the jet level + elevated, look up at the underside from below(0-40)/front-below(40-80)/side-below(80+)
                        var o = new Vector3(0f, 7f, 60f);
                        _veh.GlobalTransform = new Transform3D(Basis.Identity, o);
                        _veh.LinearVelocity = Vector3.Zero; _veh.AngularVelocity = Vector3.Zero;
                        if (_vehCam != null)
                        {
                            Vector3 cp; Vector3 up = Vector3.Up;
                            if (_frame < 40)      { cp = o + new Vector3(0.02f, -7f, 0f); up = Vector3.Forward; }   // straight below, nose = up in frame
                            else if (_frame < 80) { cp = o + new Vector3(0f, -3.2f, -8f); }                        // front-below (nose is -Z)
                            else                  { cp = o + new Vector3(8f, -3.2f, 0.5f); }                        // side-below
                            _vehCam.GlobalPosition = cp; _vehCam.LookAt(o, up);
                        }
                        return;
                    }
                    if (System.Environment.GetEnvironmentVariable("UG_CANOPYSHOT") == "1")
                    {   // CANOPY FIT: parked jet, cycle a close FRONT(0-40)/SIDE(40-80)/TOP(80+) cam on the cockpit
                        _veh.DrivePlane(0f, 0f, 0f, 0f, delta);
                        if (_vehCam != null)
                        {
                            var vt = _veh.GetGlobalTransformInterpolated(); var b = vt.Basis; var fwd = -b.Z;
                            var ck = vt.Origin + b.Y * 1.0f - b.Z * 4.5f;   // cockpit centre (vehicle-local ~ (0,1.0,-4.5))
                            if (_frame < 40)      { _vehCam.GlobalPosition = ck + fwd * 4.2f + b.Y * 0.5f; _vehCam.LookAt(ck, Vector3.Up); }
                            else if (_frame < 80) { _vehCam.GlobalPosition = ck + b.X * 4.2f + b.Y * 0.5f; _vehCam.LookAt(ck, Vector3.Up); }
                            else                  { _vehCam.GlobalPosition = ck + b.Y * 4.5f;              _vehCam.LookAt(ck, fwd); }
                        }
                        return;
                    }
                    if (System.Environment.GetEnvironmentVariable("UG_GEARCYCLE") == "1")
                    {   // GEAR direction check: pin the jet level+airborne, retract at frame 20, side cam (+X). nose = -Z = RIGHT in frame
                        var o = new Vector3(0f, 7f, 60f);
                        _veh.GlobalTransform = new Transform3D(Basis.Identity, o);
                        _veh.LinearVelocity = Vector3.Zero; _veh.AngularVelocity = Vector3.Zero; _veh.PlaneGroundMode = false;
                        if (_frame == 20) _veh.ToggleGear();
                        if (_vehCam != null) { _vehCam.GlobalPosition = o + new Vector3(8.5f, -0.6f, 0f); _vehCam.LookAt(o + new Vector3(0f, -0.6f, 0f), Vector3.Up); }
                        return;
                    }
                    if (System.Environment.GetEnvironmentVariable("UG_PLANEPARK") == "1")
                    {   // SPAWN-SETTLE + SLIDE diagnostic: zero input, let it sit on the (sloped) ground; side cam TRACKS it so drift is visible
                        float _tthr = 0f, _tstr = 0f;
                        if (System.Environment.GetEnvironmentVariable("UG_TAXI") == "1")
                        {   // scripted taxi: FORWARD+steer-right, coast, then REVERSE+steer-left
                            if (_frame < 8) { _tthr = 0f; _tstr = 0f; }           // settle at rest
                            else { _tthr = 1f; _tstr = 0.4f; }                    // taxi forward + gentle steer (UG_ROUGH to test chatter)
                        }
                        _veh.DrivePlane(_tthr, _tstr, 0f, 0f, delta);
                        if (_frame == 1)
                        {
                            if (System.Environment.GetEnvironmentVariable("UG_SEATSPAWN") == "1") _veh.PlaceOnGround(new Vector3(_veh.GlobalPosition.X, 0f, _veh.GlobalPosition.Z));
                            if (float.TryParse(System.Environment.GetEnvironmentVariable("UG_LANDSPEED"), out var _ls)) _veh.LinearVelocity = -_veh.GlobalTransform.Basis.Z * _ls;
                        }
                        if (_vehCam != null)
                        {
                            if (System.Environment.GetEnvironmentVariable("UG_TAXI") == "1") { var _vt = _veh.GetGlobalTransformInterpolated(); _vehCam.GlobalPosition = _vt.Origin + new Vector3(0f, 13f, 0f); _vehCam.LookAt(_vt.Origin, new Vector3(0f, 0f, -1f)); }   // CLOSE top-down TRACKER, world-fixed (up=-Z): yaw jitter = nose wobbling L/R
                            else { var vt = _veh.GetGlobalTransformInterpolated(); _vehCam.GlobalPosition = vt.Origin + new Vector3(9f, 1.8f, 0f); _vehCam.LookAt(vt.Origin + new Vector3(0f, -0.2f, 0f), Vector3.Up); }
                        }
                        if (System.Environment.GetEnvironmentVariable("UG_PLANEDBG") == "1") GD.Print($"[park] f={_frame} spd={_veh.LinearVelocity.Length():F2} yawv={_veh.AngularVelocity.Y:F3} rollv={_veh.AngularVelocity.Z:F3} steer={_veh.Steering:F3}");
                        return;
                    }
                    if (System.Environment.GetEnvironmentVariable("UG_PLANEBURN") == "1")
                    {   // AFTERBURNER beauty shot: FULL throttle the whole time (burners maxed) + a close, level,
                        // 3/4-rear camera locked on the exhaust cones, gently swinging around dead-astern.
                        var pbb = _veh.GlobalTransform.Basis;
                        float noseDegB = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(-pbb.Z.Y, -1f, 1f)));
                        float pitchB = Mathf.Clamp((10f - noseDegB) * 0.06f, -0.25f, 0.25f);   // hold ~10 deg climb once airborne
                        _veh.DrivePlane(1f, 0f, _veh.Afloat ? (spd > 11f ? 0.55f : 0f) : pitchB, 0f, delta);
                        if (_vehCam != null)
                        {
                            var vtB = _veh.GetGlobalTransformInterpolated();
                            var fwdB = -vtB.Basis.Z; fwdB.Y = 0f; fwdB = fwdB.LengthSquared() > 0.001f ? fwdB.Normalized() : Vector3.Forward;
                            var rightB = new Vector3(fwdB.Z, 0f, -fwdB.X);
                            float sway = Mathf.Sin(_frame * 0.010f) * 0.6f;   // +/- ~34 deg swing around dead-astern
                            var dirB = (-fwdB * Mathf.Cos(sway) + rightB * Mathf.Sin(sway)).Normalized();
                            _vehCam.GlobalPosition = vtB.Origin + dirB * 7.0f + Vector3.Up * 1.5f;
                            _vehCam.LookAt(vtB.Origin - fwdB * 2.4f + Vector3.Up * 0.9f, Vector3.Up);   // aim at the exhaust cluster (aft of centre)
                        }
                        return;
                    }
                    if (System.Environment.GetEnvironmentVariable("UG_PLANEDEMO") == "1")
                    {   // FX DEMO: throttle RAMPS full->0->full so the afterburners visibly SCALE with thrust, while the
                        // jet accelerates (wingtip CONTRAILS fade IN with speed). A pulled-back 3/4-rear cam frames the
                        // whole plane so both the trails + the burners read.
                        float th;
                        if (_frame < 130) th = 1f;                                   // takeoff + get fast (contrails fade in)
                        else if (_frame < 250) th = 1f - (_frame - 130) / 120f;      // ramp DOWN -> burners shrink to nothing
                        else th = (_frame - 250) / 120f;                             // ramp back UP -> burners grow again
                        th = Mathf.Clamp(th, 0f, 1f);
                        var pbd = _veh.GlobalTransform.Basis;
                        float noseDegD = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(-pbd.Z.Y, -1f, 1f)));
                        float pitchD = Mathf.Clamp((6f - noseDegD) * 0.05f, -0.2f, 0.2f);
                        _veh.DrivePlane(th, 0f, _veh.Afloat ? (spd > 11f ? 0.55f : 0f) : pitchD, 0f, delta);
                        if (_vehCam != null)
                        {
                            var vtD = _veh.GetGlobalTransformInterpolated();
                            var fwdD = -vtD.Basis.Z; fwdD.Y = 0f; fwdD = fwdD.LengthSquared() > 0.001f ? fwdD.Normalized() : Vector3.Forward;
                            var rightD = new Vector3(fwdD.Z, 0f, -fwdD.X);
                            float swayD = Mathf.Sin(_frame * 0.008f) * 0.5f;
                            var dirD = (-fwdD * Mathf.Cos(swayD) + rightD * Mathf.Sin(swayD)).Normalized();
                            _vehCam.GlobalPosition = vtD.Origin + dirD * 16f + Vector3.Up * 4.5f;   // pulled back -> whole plane + both wingtip trails
                            _vehCam.LookAt(vtD.Origin + Vector3.Up * 0.5f, Vector3.Up);
                        }
                        return;
                    }
                    float throttle, pitch, roll;
                    if (_veh.Afloat)
                    {   // full-power takeoff run; once up to speed, ease back to ROTATE (raise AoA) and lift off the water
                        throttle = 1f; roll = 0f;
                        pitch = spd > 11f ? 0.55f : 0f;   // firm back-stick to rotate off the water once up to speed
                    }
                    else
                    {
                        // Showcase attitude-hold autopilot (test harness, NOT the flight model): ease to cruise
                        // power once up, and a proportional elevator holds a target nose angle so the clip shows a
                        // steady climb-out -> a near-level banked cruise; a gentle bank once climbed shows bank-to-turn.
                        var pb = _veh.GlobalTransform.Basis;
                        float noseDeg = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(-pb.Z.Y, -1f, 1f)));
                        float rollDeg = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(pb.X.Y, -1f, 1f)));
                        if (_frame > 620)
                        {   // GLIDE TEST (master "throttle down -> nosedive or glide?"): CUT the engine, level the
                            // wings, and let GO of the elevator -> does the airframe settle into a glide on its own?
                            throttle = -1f;                                              // S = bleed the sticky throttle to 0 (engine off), not hands-off
                            pitch = 0f;                                                  // hands off the elevator
                            roll = Mathf.Clamp(rollDeg * 0.05f, -0.3f, 0.3f);            // just level the wings
                        }
                        else
                        {
                            throttle = alt > 45f ? 0.6f : 1f;   // climb higher before cruise so the glide test has room
                            bool turning = _frame > 460;
                            float targetDeg = turning ? 4f : 9f;
                            pitch = Mathf.Clamp((targetDeg - noseDeg) * 0.06f, -0.25f, 0.25f);
                            // roll-ANGLE hold: bank to a steady target + HOLD it (instead of rolling forever)
                            float targetRoll = turning ? 20f : 0f;
                            roll = Mathf.Clamp((rollDeg - targetRoll) * 0.05f, -0.35f, 0.35f);
                        }
                    }
                    _veh.DrivePlane(throttle, 0f, pitch, roll, delta);
                    if (_vehCam != null)   // world-up chase cam behind the flattened heading -> the bank reads against a level horizon
                    {
                        var vt = _veh.GetGlobalTransformInterpolated();   // follow the INTERPOLATED visual transform, not the raw 50Hz physics one -> the clip is smooth instead of stepping at 50Hz (same as the in-game drive cam)
                        var fwd = -vt.Basis.Z; fwd.Y = 0f;
                        fwd = fwd.LengthSquared() > 0.001f ? fwd.Normalized() : Vector3.Forward;
                        float cd = 13f; var cde = System.Environment.GetEnvironmentVariable("UG_CAMDIST");
                        if (!string.IsNullOrEmpty(cde) && float.TryParse(cde, out var cdv)) cd = cdv;
                        _vehCam.GlobalPosition = vt.Origin - fwd * cd + Vector3.Up * 5f;
                        _vehCam.LookAt(vt.Origin + Vector3.Up * 0.5f, Vector3.Up);
                    }
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
            float _shotTimeTarget = 0f; { var _ste = System.Environment.GetEnvironmentVariable("UG_SHOTTIME"); if (!string.IsNullOrEmpty(_ste)) float.TryParse(_ste, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _shotTimeTarget); }
            if (_shotTimeTarget > 0f) { _shotElapsed += (float)delta; if (_shotElapsed < _shotTimeTarget) return; }   // UG_SHOTTIME: capture at an ELAPSED-TIME target (real-time frame counts drift off fixed-fps)
            else if (_peiPlay) { if (_peiFrame < (_peiHorde ? 130 : 160)) return; }   // peiplay: drop(~25f)+enter(50f)+drive(55f+); --horde captures mid-plow through the zombie field
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
            NodeCensus();
            ProfDump();
            GD.Print($"[lodperf] drawcalls {RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalDrawCallsInFrame)}" +
                     $" | primitives {RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalPrimitivesInFrame)}" +
                     $" | objects {RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalObjectsInFrame)}");
            var img = GetViewport().GetTexture().GetImage();
            if (img == null) { GD.PrintErr("[SHOT] null image -- run with a rendering driver (e.g. --rendering-driver vulkan), NOT --headless"); GetTree().Quit(1); return; }
            img.SavePng(_shotPath);
            GD.Print($"[SHOT] saved {_shotPath} ({img.GetWidth()}x{img.GetHeight()})");
            GetTree().Quit();
        }

        /// <summary>Dump the Prof breakdown at capture, so per-system cost is measurable from a HEADLESS-ish
        /// render instead of only off a screenshot of the F3 overlay.
        ///
        /// Prof accumulates whenever instrumented code runs, but only the overlay ever reads or resets it, and
        /// the overlay needs a keypress -- which the shot harness cannot give. That left every per-system
        /// number in this project unmeasurable except by asking a human to press F3 and photograph it, which
        /// is not a before/after. Same camera, same seed, two runs: now it is.
        ///
        /// Absolute microseconds here are a software rasteriser's and do not transfer to real hardware; the
        /// CALL COUNTS and the RATIO between runs do.</summary>
        /// Which timing key a count belongs to, so counts can print as a per-call ratio.
        static readonly System.Collections.Generic.Dictionary<string, string> CountOwner = new() { ["los_rays"] = "item_LOS" };

        void ProfDump()
        {
            if (System.Environment.GetEnvironmentVariable("UG_PROFDUMP") != "1") return;
            var list = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, long>>(Prof.Us);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            var sb = new System.Text.StringBuilder();
            foreach (var kv in list)
            {
                Prof.Calls.TryGetValue(kv.Key, out int n);
                sb.Append($"{kv.Key} {kv.Value / 1000.0:0.0}ms(x{n})   ");
            }
            var (tot, phys) = Prof.Totals();
            GD.Print($"[prof] since boot: total {tot / 1000.0:0.0}ms (physics {phys / 1000.0:0.0}ms) over {Engine.GetProcessFrames()} process / {Engine.GetPhysicsFrames()} physics frames");
            GD.Print($"[prof] {sb}");
            // Counts are printed SEPARATELY and as whole numbers -- Prof.Counts exists precisely because a
            // tally parked in the millisecond dictionary renders as "0.0" and reads as "this never ran".
            if (Prof.Counts.Count > 0)
            {
                var cs = new System.Text.StringBuilder();
                foreach (var kv in Prof.Counts)
                {
                    cs.Append($"{kv.Key} {kv.Value}");
                    // Per-call is the useful form for anything that also has a timing key: 9 rays a call and
                    // 1 ray a call are the same total from very different problems.
                    // Map a count key to its timing key so the ratio can be shown. "los_rays" belongs to
                    // "item_LOS", which no string substitution derives -- the first attempt built "los_LOS",
                    // matched nothing, and silently printed the raw total as if no pairing existed.
                    if (CountOwner.TryGetValue(kv.Key, out string owner) && Prof.Calls.TryGetValue(owner, out int calls) && calls > 0)
                        cs.Append($" ({(double)kv.Value / calls:0.00}/call)");
                    cs.Append("   ");
                }
                GD.Print($"[prof] counts: {cs}");
            }
        }

        /// <summary>Tally the scene tree by node CLASS, biggest first.
        ///
        /// The F3 profiler can only ever attribute what a script does; on the real map it accounts for ~6% of
        /// the frame while the scene holds ~40,000 nodes and ~70,000 objects. Per-node engine work -- transform
        /// propagation, visibility/culling bookkeeping, physics broadphase membership -- costs CPU with no
        /// script running, so no `Prof.Scope` can ever see it and the profiler will keep reporting it as
        /// unattributed no matter how many callbacks get instrumented. Knowing WHICH classes make up 40k nodes
        /// is the difference between guessing at that and going after it. Counts only; free to run.</summary>
        void NodeCensus()
        {
            if (System.Environment.GetEnvironmentVariable("UG_NODECENSUS") != "1") return;
            var byClass = new System.Collections.Generic.Dictionary<string, int>();
            int total = 0;
            var stack = new System.Collections.Generic.Stack<Node>();
            stack.Push(GetTree().Root);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                total++;
                string k = n.GetType().Name;   // the C# type, so UnturnedGodot classes show by their own name
                byClass.TryGetValue(k, out int c); byClass[k] = c + 1;
                foreach (var ch in n.GetChildren()) stack.Push(ch);
            }
            var list = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, int>>(byClass);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < list.Count && i < 25; i++) sb.Append($"{list[i].Key} {list[i].Value}   ");
            // Dropped items settle by freezing, so Freeze is an EXTERNAL read of "did physics do its job" --
            // no need to reach into WorldItem's privates. A map where most items are still unfrozen at capture
            // is a map where they never fell, which is the report being chased. Also the control for whether
            // ColliderBudget broke them: run it against UG_COLLDIST=0 and compare the fraction.
            int items = 0, settled = 0, airborne = 0, shown = 0;
            foreach (var n in GetTree().GetNodesInGroup("worlditems"))
                if (n is RigidBody3D rb && GodotObject.IsInstanceValid(rb))
                {
                    items++;
                    if (rb.Freeze) settled++; else if (rb.LinearVelocity.LengthSquared() > 0.02f) airborne++;
                    if (rb.Visible) shown++;
                }
            if (items > 0)
                // `shown` is the LOS verdict itself -- Visible is exactly what the occlusion cull writes. It is
                // the right instrument for an A/B on the sample-point count, and a whole-frame pixel diff is
                // the WRONG one: other systems (the shadow budget's timer, async load frame counts) differ
                // between two runs, so the frame moves for reasons that have nothing to do with items.
                GD.Print($"[census] worlditems {items}: {settled} settled (frozen), {airborne} still moving, {items - settled - airborne} idle-unfrozen, {shown} VISIBLE");
            GD.Print($"[census] {total} nodes across {byClass.Count} classes");
            GD.Print($"[census] top: {sb}");
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
            // Name the state we can actually SEE rather than asserting a cause we never checked. The old line
            // here said flatly "most common cause: UG_UNTURNED_DIR is unset" -- which sent me hunting a missing
            // map for five minutes while UG_UNTURNED_DIR was set correctly the whole time and the real answer was
            // that a showcase mode never arms a capture at all. A diagnostic that guesses in the voice of a
            // finding is worse than one that says nothing.
            string envHint = string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("UG_UNTURNED_DIR"))
                ? "UG_UNTURNED_DIR is NOT set -- if this scene needs the real map, that is very likely why."
                : "UG_UNTURNED_DIR IS set, so this is probably NOT a missing map.";
            GD.PrintErr($"[SHOT] {envHint}");
            GD.PrintErr("[SHOT] also note: the showcase modes (UG_HELITEST/UG_PLANETEST/UG_SHIPSHOW) return before "
                      + "the capture hook and never arm one -- for those the MOVIE is the artifact and this timeout "
                      + "is the normal end of the run. Set UG_SHOT_TIMEOUT=0 to opt out and bound the run yourself.");
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
