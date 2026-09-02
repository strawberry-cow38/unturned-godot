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

        int _bakeHullsFrames = -1;   // --bakehulls frame countdown (-1 = inactive)
        string _shotPath; float _shotElapsed;   // UG_SHOTTIME: capture at an elapsed-time target (real-time frame counts drift off fixed-fps -- tinyclaw)
        float _bootCmdElapsed; bool _bootCmdRun;   // UG_BOOTCMD: see TickBootCommand
        Camera3D _orbitCam; Vector3 _orbitCenter; float _orbitR, _orbitAngle;   // UG_PROPSPIN: 360 turntable camera orbit for the prop-showcase movie
        Deployable _spotDbg;    // UG_WIRETEST: spotlight, probed for lamp-lit state at the shot frame
        Vector3 _vAim; bool _vHave;   // first real (Police/Fire/Ambulance) vehicle, for the demo cam
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
        string _glassShotDir;                        // --glassshot=DIR : eyeline orbit of ONE parked vehicle, per-pane glass colours
        Camera3D _glassCam; float _glassRadius = 6.5f; float _glassEye = 1.70f;
        System.Collections.Generic.List<MeshInstance3D> _glassPanes;
        bool _hullOverlayDone; bool _glassRadiusSet;
        System.Collections.Generic.List<MeshInstance3D> _bodyMeshes;
        static readonly Color[] PaneColors = {   // deliberately NO magenta: that is the body colour
            new Color(0.1f, 1f, 1f),     new Color(1f, 0.15f, 0.15f), new Color(1f, 0.95f, 0.1f),
            new Color(0.15f, 1f, 0.25f), new Color(1f, 0.55f, 0f),    new Color(0.25f, 0.45f, 1f),
            new Color(1f, 1f, 1f),       new Color(0.6f, 0.2f, 1f),   new Color(0.1f, 0.35f, 0.2f),
            new Color(0.5f, 0.9f, 0.1f), new Color(0.2f, 0.2f, 0.6f), new Color(0.7f, 0.7f, 0.2f),
            new Color(0f, 0.5f, 0.5f),   new Color(0.55f, 0.3f, 0.1f),
        };
        static readonly float[] GlassShotYaws = { 0f, 45f, 90f, 135f, 180f, 225f, 270f, 315f };
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
        bool _vehTest; Vehicle _veh; Camera3D _vehCam; int _vehVariant; bool _night, _demo, _crash, _chain, _hitch, _backunder, _pivots; Vehicle _buTrailer; int _buCoupledFrame = 999999;   // --vehicle=DIR [--variant=N] [--night] [--demo] [--crash] [--chain] [--hitch] [--backunder] [--pivots]
        bool _lampFxDone;   // UG_LAMPBREAK/UG_LAMPS apply once per run
        bool _planeTest;   // UG_PLANETEST (with --boattest --gun=otter): scripted fixed-wing flight (throttle/pitch/roll injected) to verify the flight model in a render
        int _heliPhase, _heliPhaseTick;   // UG_HELITEST maneuver sequence: 0 climb, 1 cruise, 2 turn, 3 slide, 4 recover
        bool _heliTest;    // UG_HELITEST (with --vehicle --gun=minicopter|huey): scripted ROTARY flight -- see the loop in _PhysicsProcess for why this exists
        System.Collections.Generic.List<Vector3> _trP; System.Collections.Generic.List<float> _trD;
        System.Collections.Generic.List<(MeshInstance3D body, MeshInstance3D bf, MeshInstance3D bb, float off)> _trUnits;
        float _trS, _trRailY = 1.4f; bool _trAnim;
        readonly System.Collections.Generic.List<(Node3D mark, Vehicle veh, Vector3 local)> _pivotMarks = new();   // --pivots: arrow markers pinned to each coupling point
        bool _driveTest, _swarm, _drivethru, _nade, _grassTest; PlayerController _dtPlayer;      // --drivetest=DIR [--swarm|--drivethru|--nade] : enter/drive a jeep; swarm = mob it; drivethru = loud drive wakes zombies; nade = grenade the parked car. _grassTest (UG_GRASSTEST=1): a lawn + overhead cam, jeep stays parked -> verify grass displacement
        bool _fireTest; PlayerController _ftPlayer; int _ftFrame;   // --firetest [--supp] : player fires downrange -- viewmodel / tracer / ADS / impact test rig
        bool _paActive; RiggedCharacter _paRig; float _paT; bool _paHit; bool _paGun;   // --puppetanim: drive a player rig idle->walk->run (SetLocomotion+Tick, like RemotePlayers). UG_PAHITBOX: PvP damage zones + idle. UG_PAGUN: gun-hold, and its own hold->ADS->lean sequence
        byte _paStance; float _paLean; bool _paMeasured;   // UG_PASTANCE=stand/crouch/prone/lean holds that pose under the hitbox overlay; dumps the rig's bone Y/Z once posed
        bool _peiPlay; PlayerController _peiPlayer; int _peiFrame;   // --peiplay : drive a jeep on real PEI
        int _tpFrame; double _tpPrims, _tpDraws, _tpMs; int _tpN;   // --- UG_TERRPERF terrain cost probe
        PlayerController _pdPlayer; int _pdFireT;   // --peidrive on-foot player -> UG_AUTOFIRE terrain-impact verification
        bool _peiPlayable;   // menu "Drive PEI": BuildObjectsTest spawns a player+jeep with REAL controls instead of the aerial cam
        bool _worldBuild, _worldReady;   // BuildObjectsTest (objects/peidrive) async load -> the --shot harness waits for _worldReady before capturing
        // --landmarkshot=DIR: after the PEI world loads, fly a camera to a few points at rising distance from the big
        // landmarks (Lighthouse_0, the Alberton Dock/Harbor) and capture each -> verify the landmark cull extension
        // (LodTable) actually draws them across the map, past the old 447m region cap.
        string _lmShotDir; Camera3D _lmCam; int _lmIdx, _lmFrame;
        static readonly (Vector3 Eye, Vector3 Look, string Tag)[] _lmTour =
        {
            (new Vector3(247f, 68f, -650f),  new Vector3(247f, 90f, -793f), "lighthouse_close"),  // Lighthouse_0 WORLD (247,58,-793) -- port NEGATES Z: ~145m, the 51m tower vs the sea
            (new Vector3(150f, 72f, -700f),  new Vector3(247f, 88f, -793f), "lighthouse_sw"),     // ~135m from the SW
            (new Vector3(247f, 160f, -380f), new Vector3(247f, 82f, -793f), "lighthouse_420"),    // ~420m -- carries with the fix
            (new Vector3(247f, 220f, -30f),  new Vector3(247f, 78f, -793f), "lighthouse_780"),    // ~780m across the map
            (new Vector3(-212f, 55f, -280f), new Vector3(-248f, 46f, -320f), "foliage"),            // Fernwood treed hills for the wind-sway A/B
        };
        int _treeCheckFrame; bool _treeChecked;   // UG_TREECHECK: raycast self-test that tree trunk colliders are actually hittable
        float _perfT;   // UG_PERF: throttle the perf log
        bool _itemTest;   // --itemtest=ID,ID,... : drop those items as physics WorldItems onto a ground plane -> validate mesh/tex/scale/settle
        bool _doorAnim; ObjectDoor _doorAnimDoor; double _doorAnimElapsed; float _doorAnimToggle1At, _doorAnimToggle2At, _doorAnimDoneAt; bool _doorAnimToggle1Done, _doorAnimToggle2Done;   // --doortest UG_DOOR_ANIM=1: real-time DEFAULT->away->DEFAULT cycle for a --write-movie capture
        WeatherManager _stormWm; double _stormT; float[] _stormStrikes; int _stormStrikeIdx;   // --daynight UG_WEATHER + UG_STRIKE_AT=<s,s,s>: fire lightning strikes at those times for the --write-movie storm demo

        public override void _Ready()
        {
            TickHub.AddProcess(this, HubProcess); SetProcess(false);   // PERF: hub-ticked (see TickHub.AddProcess)
            GameAudio.AuditBanks();   // UG_AUDIODBG=1: every emitted bank name vs the files on disk (prints EMPTY BANK lines)
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
            string glassShot = null, catalog = null, shot = null, picks = null, gun = null, rig = null, anim = "Walk", vm = null, bakeIcon = null, veh = null, drivetest = null, proptest = null, magnettest = null, animrig = null, rottest = null, itemtest = null, navShot = null, croptest = null, menuShot = null, clothtest = null, boattest = null, slingtest = null, trainshow = null, traintrack = null, ammoRadial = null, animaltest = null, treetest = null, profileShot = null;
            bool bakeHulls = false;   // --bakehulls: build every vehicle spec once so the convex-hull bakes get written (user://vehicle_hulls -> commit into content/vehicle_hulls)
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
            bool elevatorTest = false;
            bool rainTest = false;
            bool rainMatTest = false;
            bool windowBarrTest = false;
            string arenaSpawns = null;   // --arenaspawns[=POIname] : debug-render the 8 arena spawn points in a POI (master 2026-09-02)
            bool play = false, demo = false, netdemo = false, server = false, dedicated = false, client = false, smoke = false, invdemo = false, invsel = false, invequip = false, invdrop = false, invloot = false, invcrate = false, daynight = false, lightTest = false, trafficTest = false, buildmode = false, firetest = false, supp = false, terrain = false, peiplay = false, playground = false, objects = false, peidrive = false, craftmenu = false, stationtest = false, editorMode = false, impactTest = false, doorGallery = false, lampTest = false, beamTest = false, impTest = false, treeSweep = false, bakeLods = false, bakeLodsDry = false, netobserve = false, zombieTier = false, zflow = false, zhunt = false, zkill = false, zsound = false, zface = false, zpath = false;
            bool puppetAnim = false;   // --puppetanim: prove RemotePlayers locomotion animates
            foreach (var arg in OS.GetCmdlineUserArgs())
            {
                if (arg.StartsWith("--catalog=")) catalog = arg["--catalog=".Length..];
                else if (arg.StartsWith("--shot=")) { shot = arg["--shot=".Length..]; _shotRequested = shot; }
                else if (arg.StartsWith("--menushot=")) { menuShot = arg["--menushot=".Length..]; _shotRequested = menuShot; }   // render the 3D barn main menu + capture each of the 5 camera anchors (menu_00..04.png)
                else if (arg == "--editor") editorMode = true;   // boot straight into the map editor (the Workshop entry); --editor --shot=OUT captures a loaded frame
                else if (arg == "--fluidtest") fluidTest = true;   // F2 verify: source -> hose -> storage flows + fills (headless log check)
                else if (arg == "--doortest") { doorTest = true; doorTestName = "Fridge_0"; }   // openable prop door MVP: place one a few metres from the camera; UG_DOOR_OPEN=1 spawns it already open
                else if (arg.StartsWith("--doortest=")) { doorTest = true; doorTestName = arg["--doortest=".Length..]; }   // e.g. --doortest=Wardrobe_0 -- any prop with a doors.txt entry
                else if (arg == "--containertest") { containerTest = true; containerTestName = "Fridge_0"; }   // lootable+openable merge: spawn the doored prop as a REAL StoreShelf container + render its door; UG_CONTAINER_OPEN=1 opens it
                else if (arg.StartsWith("--containertest=")) { containerTest = true; containerTestName = arg["--containertest=".Length..]; }
                else if (arg.StartsWith("--proptest=")) { proptest = arg["--proptest=".Length..]; _shotRequested = proptest; }
                else if (arg.StartsWith("--animaltest=")) { animaltest = arg["--animaltest=".Length..]; _shotRequested = animaltest; }   // one animal rig posed as if walking -Z, to measure the RigYawFix (UG_ANIMALYAW spins it)
                else if (arg.StartsWith("--treetest=")) { treetest = arg["--treetest=".Length..]; _shotRequested = treetest; }   // standing tree beside a felled one (its dropped logs) -> render the harvest
                else if (arg == "--elevatortest") { elevatorTest = true; _shotRequested = "elevator"; }   // Elevator_0 wired to ride up/down on F; auto-Calls so an offline UG_SHOTTIME catches it mid-ride
                else if (arg == "--raintest") { rainTest = true; _shotRequested = "rain"; }   // rain-visuals showcase: overcast ground + boxes + the RainOverlay (UG_RAININT / UG_RAINCAM)
                else if (arg == "--rainmattest") rainMatTest = true;   // positional rain-on-material audio: a tree w/ collider under heavy rain, cam nearby -> the foliage emitter homes to it (UG_RAINMATDBG logs)
                else if (arg == "--windowbarrtest") windowBarrTest = true;   // window-barricade visual: a wall+window with a barricade on each face (inside + outside)
                else if (arg == "--trainshow") trainshow = "1";   // assemble train_cargo_0 from its extracted pieces for a 3/4 shot
                else if (arg == "--traintrack") traintrack = "1";   // ride the train along a curved test track
                else if (arg.StartsWith("--slingtest=")) { slingtest = arg["--slingtest=".Length..]; _shotRequested = slingtest; }
                else if (arg.StartsWith("--magnettest=")) { magnettest = arg["--magnettest=".Length..]; _shotRequested = magnettest; }
                else if (arg == "--tailcheck") tailCheck = true;
                else if (arg.StartsWith("--bellyshot=")) { bellyShot = arg["--bellyshot=".Length..]; _shotRequested = bellyShot; }
                else if (arg.StartsWith("--tailshot=")) { tailShot = arg["--tailshot=".Length..]; _shotRequested = tailShot; }   // NAME:OUT -- close-up of one heli's tail from behind   // audit every heli: which side is the tail-rotor POST on, vs where the spec puts the hub   // sky-crane winch + electromagnet: dangle, energise, bite a load, lift it   // skycrane + shipping container: in-the-bay vs slung-beneath, side by side   // spawn ONE named prop at identity + RGB axes -> diagnose mirror/orientation/material
                else if (arg.StartsWith("--croptest=")) croptest = arg["--croptest=".Length..];   // spawn a farm crop (young + grown) on a ground plane -> validate mesh/tex/orientation (UG_CROPROT tunes rot)
                else if (arg == "--deploytest") deployTest = true;   // both deployables placed on a ground plane + a valid(blue)+invalid(red) ghost -> verify models/palette/stand-up/ghost materials
                else if (arg == "--impacttest") impactTest = true;   // one bullet-impact FX per surface (concrete/metal/wood/dirt/grass/sand/water/blood) across a wall -> verify the reimplemented ImpactFx
                else if (arg == "--doorgallery") doorGallery = true;   // --shot=OUT : lineup of the 12 ripped WOODEN door barricade models (Door/Doubledoor/Gate/Hatch x Birch/Maple/Pine) for master to eyeball
                else if (arg == "--barricadetest") barricadeTest = true;   // barricades mounted on a STRUCTURE wall (upright, facing out) + a valid ghost + a floor barricade -> verify surface placement
                else if (arg == "--barricadeplay") barricadePlay = true;   // INTERACTIVE: fly (hold RMB) + LMB-place barricades on a structure room -- test placement feel ([1-3]=def, Tab=mount, R=rotate)
                else if (arg == "--skillsui") skillsui = true;   // render the skills menu (showcase/validate the SkillsUI)
                else if (arg.StartsWith("--itemtest=")) itemtest = arg["--itemtest=".Length..];   // drop a row of loot items (ids) as physics WorldItems -> validate real mesh/tex/scale/settle
                else if (arg.StartsWith("--ammoradial=")) { ammoRadial = arg["--ammoradial=".Length..]; _shotRequested = ammoRadial; }   // open the R-hold shotgun ammo radial (mock 12ga choices) -> screenshot the picker UI
                else if (arg.StartsWith("--profileshot=")) { profileShot = arg["--profileshot=".Length..]; _shotRequested = profileShot; }   // two nameplates -- a valid 128px pfp and a refused one -> verify the render + the missing-texture fallback
                else if (arg.StartsWith("--animrig=")) { animrig = arg["--animrig=".Length..]; _shotRequested = animrig; }   // build a rigged animal (content/NAME_rig.json) at rest + 3/4 cam -> validate the static pose stands
                else if (arg == "--puppetanim") puppetAnim = true;
                else if (arg == "--bakehulls") bakeHulls = true;   // build EVERY spec (SpecNames), let _Ready decompose + bake, quit   // drive a player rig idle->walk->run -> prove RemotePlayers locomotion animates (movie)
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
                else if (arg.StartsWith("--glassshot=")) { glassShot = arg["--glassshot=".Length..]; _shotRequested = glassShot; }   // parked vehicle, PLAYER-EYE orbit, one frame per yaw
                else if (arg.StartsWith("--boattest=")) boattest = arg["--boattest=".Length..];   // spawn a BOAT on a flat test sea + auto-drive (verify buoyancy + water propulsion)
                else if (arg.StartsWith("--drivetest=")) drivetest = arg["--drivetest=".Length..];
                else if (arg.StartsWith("--variant=")) _vehVariant = int.Parse(arg["--variant=".Length..]);
                else if (arg == "--night") _night = true;   // dark env + headlights on (headlight demo)
                else if (arg == "--demo") _demo = true;      // scripted honk + damage->explosion (destruction demo); off = clean drive
                else if (arg == "--crash") _crash = true;    // a wall ahead to ram (collision-damage demo)
                else if (arg == "--chain") _chain = true;         // a 2nd car beside _veh -> blow _veh -> chain reaction (source vehicle-explosion damage)
                else if (arg == "--hitch") _hitch = true;         // with --gun=semi: back a trailer under the cab + couple it (verify the fifth-wheel hitch + articulation)
                else if (arg == "--backunder") { _backunder = true; _hitch = false; }   // with --gun=semi: spawn a PARKED trailer behind + reverse the cab UNDER it, couple on proximity (verify the drive-under + phase-through)
                else if (arg == "--pivots") { _pivots = true; _hitch = false; }   // with --gun=semi: show cab + trailer SEPARATE with a labeled arrow at each coupling pivot (fifth wheel / kingpin)
                else if (arg == "--nade") _nade = true;           // with --drivetest: lob a grenade onto the parked jeep (source Grenade Vehicle_Damage)
                else if (arg.StartsWith("--pick=")) picks = arg["--pick=".Length..];
                else if (arg.StartsWith("--gun=")) gun = arg["--gun=".Length..];
                // NOTE: `--demo` is claimed higher up this same else-if chain (it sets _demo, the scripted
                // honk/explode script), so the second branch that used to live here could never run. `demo` stayed
                // false forever, which made the DemoDirector + overview camera + fixed 1920x1080 capture size
                // unreachable: `--play --demo --write-movie` recorded interactive play from the player camera at
                // the window size instead of the scripted demo. Both meanings now ride the ONE flag, so the two
                // cannot drift apart again. Review 2026-08-16.
                else if (arg == "--play") play = true;
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
                else if (arg == "--firetest") firetest = true;   // player fires downrange: viewmodel / tracer / ADS / impact rig (+ --supp = suppressor)
                else if (arg == "--supp") supp = true;           // with --firetest: attach the suppressor
                else if (arg == "--terrain") terrain = true;     // load a real map's Landscape heightmap terrain (PEI Tile_0_0)
                else if (arg == "--craftmenu") craftmenu = true; // open the CraftingMenu (browsable recipe index) over a stocked bag
                else if (arg == "--stationtest") { stationtest = true; _shotRequested = shot; }   // line up all 9 crafting-station deployables to eyeball the extracted models
                else if (arg == "--objects") objects = true;     // place PEI's real Level/Objects.dat objects (fences/props/rocks) on the terrain
                else if (arg == "--zombietier") zombieTier = true;   // zombie AI rewrite phase-1 verify: chunk grid + tier classification (logs tiers as an anchor sweeps out of a town)
                else if (arg == "--zflow") zflow = true;             // zombie AI rewrite phase-2 verify: flow field routes a horde AROUND a wall (log split; --write-movie for the visual)
                else if (arg == "--zhunt") zhunt = true;             // zombie AI rewrite phase-3 verify: near zombies promote to visible HOT bodies + shamble in (log; --write-movie for the visual)
                else if (arg == "--zkill") zkill = true;             // zombie AI rewrite phase-3b verify: player auto-fires at a chasing cluster -> bullet damage + kills climb
                else if (arg == "--zsound") zsound = true;           // zombie AI rewrite phase-4 verify: a gunshot lures out-of-sight zombies to the NOISE, not the player (sound-lure + stealth)
                else if (arg == "--zface") zface = true;             // facing DIAGNOSTIC: one zombie, DesiredVel forced world +X; top-down w/ RED=+X BLUE=+Z markers -> read the exact yaw offset unambiguously
                else if (arg == "--zpath") zpath = true;             // pathfinding demo: horde behind a WALL, target beyond it -> the flow field routes them around the wall's open end (master: "show how they path around objects")
                else if (arg.StartsWith("--landmarkshot=")) _lmShotDir = arg["--landmarkshot=".Length..];   // fly a camera past the big landmarks at range -> verify they render across the map
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
                else if (arg.StartsWith("--arenaspawns")) arenaSpawns = arg.Contains('=') ? arg.Split('=', 2)[1] : "";   // arena: debug-render the 8 spawns in a POI (=name, else default)
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


            if (firetest)   // player fires downrange -> viewmodel / tracer / ADS / bullet-impact test rig
            {
                GetWindow().Size = new Vector2I(1280, 720);
                _fireTest = true;
                _shotPath = shot;   // --shot: capture at a late frame (below) with live impacts down-range
                BuildFireTest(supp, gun);
                return;
            }

            if (craftmenu)   // open the CraftingMenu (the current in-game one) over a stocked bag -> render it
            {
                GetWindow().Size = new Vector2I(2560, 1440);   // full-res so the now-FULLSCREEN crafting menu renders crisp (master 2026-08-26)
                _shotPath = shot;
                BuildCraftMenu();
                return;
            }

            if (stationtest)   // line up all 9 crafting stations -> eyeball the extracted models
            {
                GetWindow().Size = new Vector2I(1600, 720);
                _shotPath = shot;
                BuildStationTest();
                return;
            }

            if (zombieTier) { BuildZombieTierTest(); return; }   // zombie AI rewrite phase 1 verify (docs/ZOMBIE_REDESIGN.md)
            if (zflow) { BuildZombieFlow(); return; }             // zombie AI rewrite phase 2 verify
            if (zhunt) { BuildZombieHunt(); return; }             // zombie AI rewrite phase 3 verify
            if (zkill) { BuildZombieKill(); return; }             // zombie AI rewrite phase 3b verify
            if (zsound) { BuildZombieSound(); return; }           // zombie AI rewrite phase 4 verify
            if (zface) { BuildZombieFace(); return; }             // facing diagnostic
            if (zpath) { BuildZombiePath(); return; }            // pathfinding-around-obstacles demo

            if (terrain)   // load a real Unturned map's terrain (PEI Landscape heightmap tile) -> a Godot mesh, replacing the flat test-plane
            {
                GetWindow().Size = new Vector2I(1280, 720);
                _shotPath = shot;   // wire the general frame-6 capture (else --shot renders the movie forever + hangs)
                // preview the terrain rain-wetness/splashes: UG_RAINWET / UG_RAININT (0..1) drive the shader's rain globals
                RainSystem3D.EnsureGlobals();
                var _trw = System.Environment.GetEnvironmentVariable("UG_RAINWET");
                var _tri = System.Environment.GetEnvironmentVariable("UG_RAININT");
                RenderingServer.GlobalShaderParameterSet("rain_wetness", string.IsNullOrEmpty(_trw) ? 0f : float.Parse(_trw));
                RenderingServer.GlobalShaderParameterSet("rain_intensity", string.IsNullOrEmpty(_tri) ? 0f : float.Parse(_tri));
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

            if (_lmShotDir != null)   // --landmarkshot: build the real PEI world (no player/zombies), then run the camera tour in _Process
            {
                GetWindow().Size = new Vector2I(1280, 720);
                BuildObjectsTest();
                return;
            }

            if (editorMode)   // --editor: boot the map editor (the Workshop path); --shot=OUT captures once the world's loaded
            {
                GetWindow().Size = new Vector2I(1280, 720);
                _shotPath = shot;
                // UG_GENSEED=<n>: the menu's Generate Map, reachable from a flag so the generated world can be
                // LOOKED at (tools/shot.py island). The suite can only ever check the generator's numbers; a
                // road prop rotated 180 or a building sunk into a hillside is invisible to every one of them.
                string genSeedEnv = System.Environment.GetEnvironmentVariable("UG_GENSEED");
                // UG_GENPLAY=1 takes the whole menu path: generate, then drop into play exactly as pressing
                // PLAY on the Generate Island entry does. Without it the shot only proves the EDITOR builds the
                // world -- and "you can walk around in it" is the actual request.
                if (!string.IsNullOrEmpty(genSeedEnv) && int.TryParse(genSeedEnv, out int genSeedArg))
                    // A UNIQUE name, exactly as the menu does. Passing null meant every generated render opened
                    // the map "NewMap" -- and EditorObjects' ctor calls LoadSaved(), which reads
                    // editor_<map>.txt. So once a playtest had SAVED NewMap, every later render loaded those
                    // props off disk AND generated a fresh set on top: two of everything, the old copy frozen at
                    // whatever rotation it was saved with. It reads exactly like a generator bug -- strawberry
                    // spotted a road turn "cloned inside the other", one at the correct yaw and one at the old.
                    BuildEditorNew(EditorMaps.Unique($"Island {genSeedArg}"), genSeed: genSeedArg,
                                   autoPlay: System.Environment.GetEnvironmentVariable("UG_GENPLAY") == "1");
                else if (System.Environment.GetEnvironmentVariable("UG_NEWMAP") == "1") BuildEditorNew();
                else BuildEditor();
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





            if (playground) { WorldBuilder.BuildPlaygroundWorld(this); return; }
            if (arenaSpawns != null)   // --arenaspawns[=POI]: debug-render the 8 arena spawns in a POI (master: eyeball placement)
            {
                GetWindow().Size = new Vector2I(1280, 720);
                _shotPath = shot;
                BuildArenaSpawns(arenaSpawns);
                return;
            }
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
            if (elevatorTest) { GetWindow().Size = new Vector2I(1280, 800); _shotPath = shot; BuildElevatorTest(); return; }
            if (rainTest) { GetWindow().Size = new Vector2I(1280, 800); _shotPath = shot; BuildRainTest(); return; }
            if (rainMatTest) { GetWindow().Size = new Vector2I(1280, 720); BuildRainMatTest(); return; }
            if (windowBarrTest) { GetWindow().Size = new Vector2I(1280, 720); _shotPath = shot; BuildWindowBarrTest(); return; }
            if (trainshow != null) { GetWindow().Size = new Vector2I(1600, 720); BuildTrainShow(); return; }
            if (traintrack != null) { GetWindow().Size = new Vector2I(1600, 900); BuildTrainTrack(); return; }

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

            if (profileShot != null)   // nameplates: name + profile picture over two rigged bodies -> eyeball the render
            {
                GetWindow().Size = new Vector2I(1280, 720);
                _shotPath = profileShot;
                SDG.Unturned.ItemCatalog.RegisterAll();
                BuildProfilePlateDemo();
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
            if (bakeHulls)
            {
                foreach (var n in Vehicle.SpecNames)
                {
                    if (n == "jet") continue;   // alias of fighterjet
                    try { var bv = Vehicle.BuildByName(n); if (bv != null) { AddChild(bv); bv.Position = new Vector3(0f, 200f, 0f); } }
                    catch (System.Exception e) { GD.PrintErr($"[bakehulls] {n}: {e.Message}"); }
                }
                _bakeHullsFrames = 0;   // _Process counts a few frames (VHACD runs in _Ready on entry) then quits
                GD.Print($"[bakehulls] built {Vehicle.SpecNames.Length - 1} specs; waiting for _Ready bakes");
                return;
            }
            if (puppetAnim) { GetWindow().Size = new Vector2I(720, 960); BuildPuppetAnim(); if (shot != null) { _shotPath = shot; _shotRequested = shot; } return; }   // --shot=P arms a still too (UG_SHOTTIME picks the moment; no --shot -> movie as before)   // idle->walk->run movie (no _shotPath -> --write-movie captures the whole run)

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

            if (glassShot != null)
            {
                _glassShotDir = glassShot;
                System.IO.Directory.CreateDirectory(glassShot);
                _rigDir = glassShot;
                _rigCaptureFrames = new int[GlassShotYaws.Length];
                for (int i = 0; i < GlassShotYaws.Length; i++) _rigCaptureFrames[i] = 24 + i * 8;   // settle, then one frame per yaw
                GetWindow().Size = new Vector2I(1280, 720);
                BuildGlassShot(gun ?? "van");
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
                var m = new MainMenu { OnDrivePEI = () => { }, OnPlay = () => { } };
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
                    MusicPlayer.Get(this)?.PlayLoop("pei_loop");   // retail menu music = the PEI loop; the world swaps to the picked map's loop once built
                    var menu = new MainMenu();
                    menu.OnPlay = () => { menu.QueueFree(); BuildPlayable(null, false, null); };
                    menu.OnDrivePEI = () => { menu.QueueFree(); ApplyMenuMap(menu.SelectedMapFolder); _peiPlayable = true; BuildObjectsTest(); };
                    menu.OnMultiplayer = () => { menu.QueueFree(); _connectHost = "claw.bitvox.me"; _playableClient = true; BuildClient(); };   // legacy MP-test entry (fallback)
                    menu.OnJoinServer = (host, port) => { menu.QueueFree(); _connectHost = host; _connectPort = port; _playableClient = true; BuildClient(); };   // server browser JOIN / direct-connect -> real client join
                    menu.OnEditor = () => { menu.QueueFree(); BuildEditor(); };   // Workshop -> the singleplayer map editor (PEI)
                    menu.OnPlayground = () => { menu.QueueFree(); WorldBuilder.BuildPlaygroundWorld(this); };   // Playground -> the gun range (same entry as --playground)
                    menu.OnOpenMap = name => { menu.QueueFree(); BuildEditorNew(name); };   // Workshop -> a custom map by name (creates or opens)
                    // Play a custom map: open it exactly as the editor does, then enter play immediately. NOT a
                    // second world-building path -- a "play build" that assembles the map its own way is how the
                    // thing you test stops being the thing you edited.
                    // autoPlay is a PARAMETER, not a field set around the call. It used to be `BuildEditorNew(name);
                    // _autoPlayMap = true;` -- assigned after the only line that reads it, so Workshop's Play
                    // opened the editor and stayed there, and left the flag set for whichever map you opened
                    // next. Passing it in makes that ordering impossible to get wrong again.
                    menu.OnPlayMap = name => { menu.QueueFree(); BuildEditorNew(name, autoPlay: true); };
                    // Generate Map -> a brand new map whose terrain is a generated island, played immediately.
                    // The name carries the seed so two generated maps do not overwrite each other's save.
                    menu.OnGenerateMap = seed => { menu.QueueFree(); BuildEditorNew(EditorMaps.Unique($"Island {seed}"), genSeed: seed, autoPlay: true); };
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

        // --elevatortest: the Elevator_0 prop wired as an interactive lift (look at it + F rides it up/down). It
        /// <summary>UG_BOOTCMD: run one DevConsole line, once, UG_BOOTCMD_AT seconds after boot (default 3).
        /// It exists so a STATE that only a player can reach is still renderable offline -- `UG_BOOTCMD=kill`
        /// plus a later UG_SHOTTIME is how the death screen gets captured, since dying is not something a
        /// headless harness can otherwise do. Same shape as the other UG_* debug hooks around it: no effect
        /// unless the variable is set.</summary>
        void TickBootCommand(double delta)
        {
            if (_bootCmdRun) return;
            string cmd = System.Environment.GetEnvironmentVariable("UG_BOOTCMD");
            if (string.IsNullOrEmpty(cmd)) { _bootCmdRun = true; return; }
            float at = 3f;
            var atEnv = System.Environment.GetEnvironmentVariable("UG_BOOTCMD_AT");
            if (!string.IsNullOrEmpty(atEnv)) float.TryParse(atEnv, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out at);
            _bootCmdElapsed += (float)delta;
            if (_bootCmdElapsed < at) return;
            _bootCmdRun = true;
            var console = FindDevConsole(this);
            if (console == null) { GD.PrintErr("[BOOTCMD] no DevConsole in the tree -- nothing run"); return; }
            GD.Print($"[BOOTCMD] {cmd}");
            console.DebugRun(cmd);
        }

        static DevConsole FindDevConsole(Node n)
        {
            if (n is DevConsole dc) return dc;
            foreach (var c in n.GetChildren()) { var f = FindDevConsole(c); if (f != null) return f; }
            return null;
        }

        // auto-Calls a beat after spawn so an offline UG_SHOTTIME capture catches it in transit. Master 2026-08-29:
        // "theres an elevator prop -- wire it to move up/down on an interaction."
        void BuildElevatorTest()
        {
            AddChild(new WorldEnvironment { Environment = new Godot.Environment {
                BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.5f, 0.62f, 0.78f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.62f, 0.63f, 0.66f), AmbientLightEnergy = 0.7f } });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-50f, -35f, 0f), LightEnergy = 1.1f });
            AddChild(new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(80f, 80f) }, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.34f, 0.40f, 0.30f), Roughness = 1f } });
            var ev = Elevator.Build();
            ev.Position = new Vector3(0f, ev.BaseLift, 0f);   // ground the stood-up car's base; set BEFORE AddChild so _Ready latches the base Y correctly
            AddChild(ev);
            // a simple humanoid "rider" parented to the car so it rides with the lift (visual: a player aboard the demo)
            if (System.Environment.GetEnvironmentVariable("UG_ELEVNORIDER") != "1")
            {
                var rider = new Node3D { Position = new Vector3(-0.05f, 0f, 0f) };   // INSIDE the car, feet on the interior floor (~0.25 above base)
                ev.AddChild(rider);
                var skin = new StandardMaterial3D { AlbedoColor = new Color(0.85f, 0.68f, 0.55f), Roughness = 1f };
                var shirt = new StandardMaterial3D { AlbedoColor = new Color(0.25f, 0.45f, 0.75f), Roughness = 1f };
                rider.AddChild(new MeshInstance3D { Mesh = new CapsuleMesh { Radius = 0.32f, Height = 1.3f }, MaterialOverride = shirt, Position = new Vector3(0f, 0.9f, 0f) });   // torso + legs
                rider.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.22f, Height = 0.44f }, MaterialOverride = skin, Position = new Vector3(0f, 1.75f, 0f) });   // head
            }
            // FLOOR LANDINGS: a thin deck at each floor height, off to the +Z side of the -X doorway (so it doesn't
            // block the door view), so the car visibly serves 3 floors as the buttons call it. Visual context only.
            for (int f = 0; f < ev.Floors.Length; f++)
            {
                float fy = ev.Floors[f];   // world Y (the car is grounded at 0)
                float shade = 0.30f + f * 0.06f;
                float landdy = 0f; { var _l = System.Environment.GetEnvironmentVariable("UG_ELEVLANDDY"); if (!string.IsNullOrEmpty(_l)) landdy = float.Parse(_l); }   // diag: nudge landing to find flush-with-car-floor
                AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(5f, 0.25f, 4f) },
                    Position = new Vector3(-4.6f, fy + 0.125f + landdy, 4.4f),   // surface at fy+0.25 = the car's MEASURED interior floor, so the car stops flush with each landing
                    MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(shade, shade, shade + 0.03f), Roughness = 1f } });
                if (System.Environment.GetEnvironmentVariable("UG_ELEVMESHCOL") == "1") GD.Print($"[landing] floor {f}: landing top world Y = {fy + 0.125f + landdy + 0.125f:0.000}");   // diag alongside the mesh-floor raycast
                // EXTERNAL call button at this landing: summon the car to THIS floor (master: "external call buttons
                // too"). Same ElevatorButton -> GoToFloor(f); WORLD-parented so it stays at the floor, doesn't ride.
                var ecol = f == 0 ? new Color(0.35f, 0.85f, 0.45f) : f == ev.Floors.Length - 1 ? new Color(0.92f, 0.34f, 0.32f) : new Color(0.95f, 0.85f, 0.35f);
                AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.14f, 1.35f, 0.14f) },
                    Position = new Vector3(-3.35f, fy + 0.92f, 3.0f),
                    MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.18f, 0.18f, 0.2f), Roughness = 0.8f } });
                var ext = ElevatorButton.Make(ev, f, ecol);
                AddChild(ext);
                ext.Position = new Vector3(-3.5f, fy + 1.4f, 3.0f);   // on the post face, turned to the -X approach (door/camera side)
                ext.RotationDegrees = new Vector3(0f, 90f, 0f);
            }
            ev.AutoFloors = System.Environment.GetEnvironmentVariable("UG_ELEVFLOORS") == "1";   // multi-floor VIDEO: step 0->1->2->1->0 (buttons in action)
            ev.AutoCycle = System.Environment.GetEnvironmentVariable("UG_ELEVCYCLE") == "1";      // ride VIDEO: 2-stop cycle up/down
            if (System.Environment.GetEnvironmentVariable("UG_ELEVFAST") == "1") { ev.SpeedMul = 6f; ev.DwellTime = 0.2f; }   // fast (for a GIF)
            var _sp = System.Environment.GetEnvironmentVariable("UG_ELEVSPEED"); if (!string.IsNullOrEmpty(_sp)) ev.SpeedMul = float.Parse(_sp);   // tune ride speed
            var _dw = System.Environment.GetEnvironmentVariable("UG_ELEVDWELL"); if (!string.IsNullOrEmpty(_dw)) ev.DwellTime = float.Parse(_dw);   // tune per-floor dwell
            bool hold = System.Environment.GetEnvironmentVariable("UG_ELEVHOLD") == "1";   // park at floor 0 (panel close-up still)
            var _goto = System.Environment.GetEnvironmentVariable("UG_ELEVGOTO");   // diag: ride to floor N + park (measure car-vs-landing alignment)
            if (!string.IsNullOrEmpty(_goto)) ev.GoToFloor(int.Parse(_goto));
            else if (!ev.AutoFloors && !ev.AutoCycle && !hold) ev.Call();   // single ride up (still); AutoFloors/AutoCycle self-start from floor 0, UG_ELEVHOLD stays parked
            var cam = new Camera3D { Current = true, Fov = 55f, Far = 2000f };
            AddChild(cam);
            cam.Position = new Vector3(-14f, 7.5f, 2.5f);
            cam.LookAt(new Vector3(0.5f, 4.2f, 0f), Vector3.Up);   // from the -X/door side: sees into the car (rider + button panel) across the floors
            var _ec = System.Environment.GetEnvironmentVariable("UG_ELEVCAM");   // diag: "ex,ey,ez,tx,ty,tz"
            if (!string.IsNullOrEmpty(_ec)) { var a = _ec.Split(','); cam.Position = new Vector3(float.Parse(a[0]), float.Parse(a[1]), float.Parse(a[2])); cam.LookAt(new Vector3(float.Parse(a[3]), float.Parse(a[4]), float.Parse(a[5])), Vector3.Up); }
            GD.Print("[elevatortest] Elevator + floor-button panel; UG_ELEVFLOORS=1 steps every floor. Set UG_SHOTTIME/--write-movie to capture.");
        }

        // --raintest: a showcase scene for the rain visuals -- overcast ground + hard-surface boxes + the RainOverlay.
        // UG_RAININT sets intensity (0..1), UG_RAINCAM="ex,ey,ez,tx,ty,tz" moves the camera. Master 2026-08-29: a nice,
        // pretty, performant rain shader (varying intensity; splashes + wetness land in later passes).
        void BuildRainTest()
        {
            var env = new Godot.Environment {
                BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.42f, 0.46f, 0.52f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.5f, 0.53f, 0.57f), AmbientLightEnergy = 0.75f,
                FogEnabled = true, FogDensity = 0.015f, FogLightColor = new Color(0.5f, 0.54f, 0.6f) };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-55f, -40f, 0f), LightEnergy = 0.7f, LightColor = new Color(0.72f, 0.76f, 0.84f), ShadowEnabled = true });
            // WETNESS + SPLASHES: register the rain_wetness global (WeatherManager owns it in-game) + apply the wet
            // surface shader to the hard surfaces so up-facing faces darken/gloss + ripple as the rain soaks them.
            RainSystem3D.EnsureGlobals();
            var wetShader = GD.Load<Shader>("res://content/wet_surface.gdshader");
            ShaderMaterial WetMat(Color dry, float rough, float impact = 1f) { var m = new ShaderMaterial { Shader = wetShader }; m.SetShaderParameter("dry_albedo", dry); m.SetShaderParameter("dry_roughness", rough); m.SetShaderParameter("impact_amount", impact); return m; }
            AddChild(new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(140f, 140f) }, MaterialOverride = WetMat(new Color(0.20f, 0.22f, 0.25f), 0.7f, impact: 0f) });   // GROUND = wetness only, no impacts (master: impacts on props, not terrain)
            for (int i = 0; i < 6; i++)
            {
                float hgt = 2.5f + (i % 3);
                AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(3f, hgt, 3f) },
                    Position = new Vector3((i - 2.5f) * 5.2f, hgt * 0.5f, -6f - (i % 2) * 4f),
                    MaterialOverride = WetMat(new Color(0.38f, 0.40f, 0.44f), 0.75f) });
            }
            var cam = new Camera3D { Current = true, Fov = 60f, Far = 500f };
            AddChild(cam);
            cam.Position = new Vector3(0f, 3.2f, 13f);
            cam.LookAt(new Vector3(0f, 2f, -6f), Vector3.Up);
            var _rc = System.Environment.GetEnvironmentVariable("UG_RAINCAM");
            if (!string.IsNullOrEmpty(_rc)) { var a = _rc.Split(','); cam.Position = new Vector3(float.Parse(a[0]), float.Parse(a[1]), float.Parse(a[2])); cam.LookAt(new Vector3(float.Parse(a[3]), float.Parse(a[4]), float.Parse(a[5])), Vector3.Up); }
            float inten = 1f; var _ri = System.Environment.GetEnvironmentVariable("UG_RAININT"); if (!string.IsNullOrEmpty(_ri)) inten = float.Parse(_ri);
            float wetv = inten; var _rw = System.Environment.GetEnvironmentVariable("UG_RAINWET"); if (!string.IsNullOrEmpty(_rw)) wetv = float.Parse(_rw);
            RenderingServer.GlobalShaderParameterSet("rain_wetness", wetv);
            RenderingServer.GlobalShaderParameterSet("rain_intensity", inten);
            AddChild(new RainSystem3D { Cam = cam, Intensity = inten });   // worldspace GPU-particle rain (geometry occludes it)
            GD.Print($"[raintest] worldspace 3D rain, intensity {inten:0.00}. UG_RAININT / UG_RAINWET / UG_RAINCAM.");
        }

        // --rainmattest: positional rain-on-material audio proof. A pine (visual + a TreeTrunk collider on the world
        // layer) under HEAVY rain, camera parked ~5 m away -> RainMaterialAudio's foliage emitter homes to the trunk and
        // plays rain-on-foliage FROM it; the offline AVI captures the 3D-panned result. UG_RAINMATDBG logs what it finds.
        void BuildRainMatTest()
        {
            var env = new Godot.Environment {
                BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.42f, 0.46f, 0.52f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.5f, 0.53f, 0.57f), AmbientLightEnergy = 0.75f,
                FogEnabled = true, FogDensity = 0.012f, FogLightColor = new Color(0.5f, 0.54f, 0.6f) };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-55f, -40f, 0f), LightEnergy = 0.7f, ShadowEnabled = true });
            RainSystem3D.EnsureGlobals();
            var ground = new StaticBody3D { CollisionLayer = 1u << 0 };
            ground.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
            AddChild(ground);
            AddChild(new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(80f, 80f) }, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.22f, 0.24f, 0.26f), Roughness = 0.7f } });

            // TREE at (-4,0,0): the visual + a TreeTrunk (StaticBody3D) with a cylinder collider on the world layer, so
            // the material-audio sphere query finds it (a bare-constructed TreeTrunk has no collider -- ResourceField
            // adds one when it places the real ones).
            string dir = ProjectSettings.GlobalizePath("res://content/resources/");
            AddChild(LoadTreeVisual(dir, "Pine_0", new Vector3(-4f, 0f, 0f)));
            var trunk = new TreeTrunk { TreeName = "Pine_0", ResDir = dir, LogItem = 41 };
            trunk.CollisionLayer = 1u << 0;
            trunk.AddChild(new CollisionShape3D { Shape = new CylinderShape3D { Radius = 0.6f, Height = 9f }, Position = new Vector3(0f, 4.5f, 0f) });
            AddChild(trunk); trunk.Position = new Vector3(-4f, 0f, 0f);

            var dn = new DayNightCycle { DayLength = 120f, Time = 0.5f, Speed = 0f, VisualsEnabled = false };
            AddChild(dn);
            var wm = WeatherManager.Attach(this, null, dn, seed: 1);
            wm.Sim.SetPerpetual(1);   // heavy rain -> full rint -> the material emitters run

            var cam = new Camera3D { Current = true, Fov = 62f };
            AddChild(cam);
            cam.Position = new Vector3(-4f, 3f, 22f);   // START 22 m out -- past the 16 m radius, so the foliage is silent
            cam.LookAt(new Vector3(-4f, 3.5f, 0f), Vector3.Up);   // AFTER AddChild -- LookAt needs the node in the tree
            // walk IN to 4 m over the clip: the foliage fades up as the camera crosses into the tree's radius, so the
            // positional read is AUDIBLE, not just asserted.
            var tw = CreateTween();
            tw.TweenProperty(cam, "position", new Vector3(-4f, 2.4f, 2.5f), 5.0);   // end WELL under the canopy so the rain hole reads
            GD.Print("[rainmattest] pine at (-4,0,0), cam walks 22m -> 4m over 5s, heavy rain -> foliage fades in");
        }

        // --windowbarrtest: window-barricade visual proof. A drawn wall with one window opening + a WindowBarricade
        // placed on EACH face (inside + outside) via Barricade.PlaceInWindow, seen from a 3/4 angle so both the fit
        // (panel scaled to the opening) and the two-sided placement read. Still: `--shot=P` + UG_SHOTTIME.
        void BuildWindowBarrTest()
        {
            AddChild(new WorldEnvironment { Environment = new Godot.Environment {
                BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.52f, 0.60f, 0.70f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.45f, 0.45f, 0.5f), AmbientLightEnergy = 1.0f,
            } });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-50f, -35f, 0f), LightEnergy = 1.1f, ShadowEnabled = true });
            var ground = new StaticBody3D();
            ground.AddChild(new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(24f, 24f) } });
            ground.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
            AddChild(ground);

            // UG_WBSTYLE=planks|bars|plate -> a single style, camera close, for tuning the look.
            string wbOne = System.Environment.GetEnvironmentVariable("UG_WBSTYLE");
            if (wbOne != null)
            {
                var def = wbOne.Contains("bar") ? DeployableDef.WindowBars : wbOne.Contains("plate") ? DeployableDef.WindowPlate : DeployableDef.WindowBarricade;
                var w1 = new WallSurface { Length = 3f, Height = 3f, Thickness = 0.5f, Position = new Vector3(-1.5f, 0f, 0f) };
                w1.Openings.Add(new UnturnedSim.WallOpening(0.8f, 1.0f, 1.4f, 1.5f));
                AddChild(w1);
                Callable.From(() => Barricade.PlaceInWindow(w1, 0, 1, def)).CallDeferred();
                var camc = new Camera3D { Current = true, Fov = 42f };
                AddChild(camc);
                camc.GlobalPosition = new Vector3(0.15f, 1.68f, 3.4f);   // near straight-on -> judge the true tilt, not perspective
                camc.LookAt(new Vector3(0f, 1.66f, 0.15f), Vector3.Up);
                return;
            }

            // UG_WBSIZES=1 -> three DIFFERENT opening sizes (small square / wide / tall), each a different style,
            // to prove the panels fit the opening + re-tile (more bars on the wide one, taller on the tall one).
            if (System.Environment.GetEnvironmentVariable("UG_WBSIZES") != null)
            {
                (float cx, float ow, float oh, float ov, DeployableDef def)[] sizes = {
                    (-4.2f, 1.0f, 1.0f, 1.2f, DeployableDef.WindowBarricade),   // small square, planks
                    ( 0f,   2.4f, 1.1f, 1.3f, DeployableDef.WindowBars),        // WIDE, bars (more bars)
                    ( 4.4f, 1.1f, 2.2f, 0.5f, DeployableDef.WindowPlate),       // TALL, plate
                };
                foreach (var (cx, ow, oh, ov, def) in sizes)
                {
                    var wall = new WallSurface { Length = ow + 1.4f, Height = 3.2f, Thickness = 0.5f, Position = new Vector3(cx - (0.7f + ow * 0.5f), 0f, 0f) };
                    wall.Openings.Add(new UnturnedSim.WallOpening(0.7f, ov, ow, oh));
                    AddChild(wall);
                    var d = def;
                    Callable.From(() => { var b = Barricade.PlaceInWindow(wall, 0, 1, d); GD.Print($"[windowbarrtest] {d.Name} on {ow}x{oh} window -> {b?.GlobalPosition}"); }).CallDeferred();
                }
                var camz = new Camera3D { Current = true, Fov = 60f };
                AddChild(camz);
                camz.GlobalPosition = new Vector3(1.2f, 2.9f, 10f);
                camz.LookAt(new Vector3(0f, 1.55f, 0f), Vector3.Up);
                return;
            }

            // UG_WBDOOR=1 -> two DOORED openings: left a plain door, right the same door boarded over (planks on the
            // near face) -> shows the barricade fits a full-height doorway + sits proud of the door (master 2026-09-01).
            if (System.Environment.GetEnvironmentVariable("UG_WBDOOR") != null)
            {
                float dh = UnturnedSim.WallOpenings.DoorHeight;
                foreach (var (px, barr) in new[] { (-2.3f, false), (2.3f, true) })
                {
                    var wall = new WallSurface { Length = 3.6f, Height = dh, Thickness = 0.5f, Position = new Vector3(px - 1.8f, 0f, 0f) };
                    wall.Openings.Add(new UnturnedSim.WallOpening(0.8f, 0f, 2.0f, dh - 0.6f) { DoorProp = "Door_Pine" });
                    AddChild(wall);
                    if (barr) { var w2 = wall; Callable.From(() => Barricade.PlaceInWindow(w2, 0, 1, DeployableDef.WindowBarricade)).CallDeferred(); }
                }
                var camd = new Camera3D { Current = true, Fov = 60f };
                AddChild(camd);
                camd.GlobalPosition = new Vector3(1.6f, 2.3f, 8f);
                camd.LookAt(new Vector3(0f, 1.9f, 0f), Vector3.Up);
                return;
            }

            // UG_WBPRE=1 -> one wall whose openings were PRE-BARRICADED in the editor (opening.Barricade set): a boarded
            // window (planks), a boarded door (bars, over a real door), a boarded window (plate) -> the abandoned-building
            // result RebuildBarricades produces from the editor's per-opening choice (master 2026-09-01).
            if (System.Environment.GetEnvironmentVariable("UG_WBPRE") != null)
            {
                float dh = UnturnedSim.WallOpenings.DoorHeight;
                var wall = new WallSurface { Length = 9f, Height = dh, Thickness = 0.5f, Position = new Vector3(-4.5f, 0f, 0f) };
                wall.Openings.Add(new UnturnedSim.WallOpening(1.0f, 1.2f, 1.6f, 1.5f) { Barricade = UnturnedSim.WallBarricade.Planks });                          // boarded window
                wall.Openings.Add(new UnturnedSim.WallOpening(3.5f, 0f, 2.0f, dh - 0.6f) { DoorProp = "Door_Pine", Barricade = UnturnedSim.WallBarricade.Bars });  // boarded door
                wall.Openings.Add(new UnturnedSim.WallOpening(6.4f, 1.2f, 1.6f, 1.5f) { Barricade = UnturnedSim.WallBarricade.Plate });                           // boarded window
                AddChild(wall);
                var camp = new Camera3D { Current = true, Fov = 60f };
                AddChild(camp);
                camp.GlobalPosition = new Vector3(1.5f, 2.6f, 9.5f);
                camp.LookAt(new Vector3(0f, 1.8f, 0f), Vector3.Up);
                return;
            }

            // three walls side by side, each with a centred window boarded on the +Z (camera) face by a different
            // style -> one render proves all three looks + their fit. left = wooden planks, mid = metal bars, right = metal plate.
            (float cx, DeployableDef def, string label)[] cells = {
                (-4f, DeployableDef.WindowBarricade, "planks"),
                ( 0f, DeployableDef.WindowBars,      "bars"),
                ( 4f, DeployableDef.WindowPlate,     "plate"),
            };
            foreach (var (cx, def, label) in cells)
            {
                var wall = new WallSurface { Length = 3f, Height = 3f, Thickness = 0.5f, Position = new Vector3(cx - 1.5f, 0f, 0f) };
                wall.Openings.Add(new UnturnedSim.WallOpening(0.8f, 1.0f, 1.4f, 1.5f));   // centred window (U centre = 1.5 -> world X = cx)
                AddChild(wall);
                var d = def; var l = label;
                Callable.From(() => {   // after the wall's _Ready (transform + Rebuild + "walls" group)
                    var b = Barricade.PlaceInWindow(wall, 0, 1, d);    // +Z (outside/camera) face
                    GD.Print($"[windowbarrtest] {l}: HP={d.Health} at {b?.GlobalPosition}");
                }).CallDeferred();
            }

            var cam = new Camera3D { Current = true, Fov = 58f };
            AddChild(cam);
            cam.GlobalPosition = new Vector3(2.0f, 2.6f, 8.6f);
            cam.LookAt(new Vector3(0f, 1.5f, 0f), Vector3.Up);
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
            if (System.Environment.GetEnvironmentVariable("UG_REFLTEST") == "1")
            {
                // SHORELINE REFLECTION test: a water strip in FRONT of the trees (+Z toward the cam) + the planar
                // reflection + a low grazing cam looking across the water at the standing tree, so it reflects.
                Terrain.HasWater = true; Terrain.SeaLevelY = 0.05f;
                // a wide seabed under the whole strip so the see-through water has ground beneath it, not sky
                AddChild(new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(300f, 300f) }, Position = new Vector3(0f, -0.15f, 40f), MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.24f, 0.30f, 0.20f), Roughness = 1f } });
                var wmat = new ShaderMaterial { Shader = GD.Load<Shader>("res://content/water.gdshader") };
                var water = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(140f, 100f), SubdivideWidth = 70, SubdivideDepth = 50 }, Position = new Vector3(0f, 0.05f, 50f), MaterialOverride = wmat };
                water.Layers = WaterReflection.WaterLayer;   // keep the water OUT of its own mirror pass (self-occlusion)
                AddChild(water);
                var refl = new WaterReflection(); AddChild(refl); refl.Setup(wmat, 0.05f, new Vector2I(1024, 1024));
                cam.Fov = 55f;
                // look DOWN across the water at the trees so the surface fills the frame; UG_REFLCAM="ex,ey,ez,tx,ty,tz" iterates without a rebuild
                Vector3 _ce = new Vector3(0f, 5f, 68f), _ct = new Vector3(0f, 0.8f, 10f);
                var _rc = System.Environment.GetEnvironmentVariable("UG_REFLCAM");
                if (!string.IsNullOrEmpty(_rc)) { var _a = _rc.Split(','); _ce = new Vector3(float.Parse(_a[0]), float.Parse(_a[1]), float.Parse(_a[2])); _ct = new Vector3(float.Parse(_a[3]), float.Parse(_a[4]), float.Parse(_a[5])); }
                cam.LookAtFromPosition(_ce, _ct, Vector3.Up);
                GetWindow().Size = new Vector2I(1280, 800);
            }
            else
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
                if (ContentProvider.LoadOk(img, dir + name + "_" + i + "_tex.png")) { img.GenerateMipmaps(); mat.AlbedoTexture = ImageTexture.CreateFromImage(img); mat.TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps; }
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
                    if (ContentProvider.LoadOk(refImg, ProjectSettings.GlobalizePath("res://content/ship_body_tex.png"))) refMat.AlbedoTexture = ImageTexture.CreateFromImage(refImg);
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

        // --glassshot=DIR [--gun=<vehicle>]: ONE parked vehicle, shot from PLAYER EYE HEIGHT (1.70 m) at eight
        // yaws around it -- master 2026-09-02: "take ur multi-angle photos from a player height view instead of
        // looking down on the car". The existing --vehicle harness is a chase cam on a car mid-course, which is
        // the wrong instrument twice over: it looks DOWN, and the thing being inspected is moving.
        // The body is forced to a flat bright magenta so glass reads against it instead of against dark paint;
        // set UG_GLASSDEBUG=1 to give every pane its own flat unshaded colour (the palette-as-classifier method).
        // UG_GLASSRADIUS / UG_GLASSEYE override the orbit distance and eye height.
        void BuildGlassShot(string type)
        {
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.18f, 0.20f, 0.24f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.85f, 0.85f, 0.85f), AmbientLightEnergy = 1.0f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-48f, -38f, 0f), LightEnergy = 1.25f });
            AddChild(new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(60f, 60f) },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.26f, 0.28f, 0.31f) } });
            var floor = new StaticBody3D();   // the PlaneMesh is only paint; without this the car falls forever
            floor.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
            AddChild(floor);

            { var r = System.Environment.GetEnvironmentVariable("UG_GLASSRADIUS"); if (r != null && float.TryParse(r, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rr)) { _glassRadius = rr; _glassRadiusSet = true; } }
            { var e = System.Environment.GetEnvironmentVariable("UG_GLASSEYE");    if (e != null && float.TryParse(e, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ee)) _glassEye = ee; }

            _veh = Vehicle.BuildByName(type, 0);
            if (_veh == null) { GD.PrintErr($"[glassshot] no vehicle '{type}'"); GetTree().Quit(1); return; }
            AddChild(_veh);
            _veh.Position = new Vector3(0f, 1.2f, 0f);   // drop onto the floor so the suspension settles, as --vehicle does

            // Bright flat body so the glass is the only thing that isn't magenta (master: "color the body a
            // bright color too to help you diff"). Applied to every mesh EXCEPT the glass panes, which the
            // glass builder already named Glass_<label>.
            var bodyMat = new StandardMaterial3D { AlbedoColor = new Color(1f, 0.10f, 0.85f),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            int painted = 0, panes = 0;
            _glassPanes = new System.Collections.Generic.List<MeshInstance3D>();
            _bodyMeshes = new System.Collections.Generic.List<MeshInstance3D>();
            void Paint(Node n)
            {
                if (n is MeshInstance3D mi)
                {
                    if (mi.Name.ToString().StartsWith("Glass_") || mi.Name.ToString() == "Glass") { panes++; _glassPanes.Add(mi); }
                    else { mi.MaterialOverride = bodyMat; painted++; _bodyMeshes.Add(mi); }
                }
                foreach (var c in n.GetChildren()) Paint(c);
            }
            Paint(_veh);
            if (System.Environment.GetEnvironmentVariable("UG_GLASSDIAG") == "1")
                foreach (var mi in _bodyMeshes)
                { var a = mi.GetAabb(); GD.Print($"[mesh] {mi.Name,-26} size=({a.Size.X,7:0.00},{a.Size.Y,6:0.00},{a.Size.Z,7:0.00}) pos=({mi.Position.X,6:0.00},{mi.Position.Y,6:0.00},{mi.Position.Z,6:0.00})"); }
            // Colour the panes MYSELF rather than leaning on UG_GLASSDEBUG: its palette starts at
            // (1,0.2,1), the same magenta as the body above, so every vehicle's windscreen was
            // invisible against the bodywork and I read a van's REAR window (seen through the empty
            // cabin) as its windscreen. A pane palette that cannot contain the body colour is the fix.
            for (int i = 0; i < _glassPanes.Count; i++)
                _glassPanes[i].MaterialOverride = new StandardMaterial3D {
                    AlbedoColor = PaneColors[i % PaneColors.Length],
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            GD.Print($"[glassshot] '{type}': {painted} body meshes painted, {panes} glass panes left alone, {GlassShotYaws.Length} yaws at eye {_glassEye:0.00} m, radius {_glassRadius:0.0} m");
            // Per-pane numbers next to the pictures: a pane that is the wrong SIZE or in the wrong PLACE is
            // easier to see in metres than in pixels, and the debug colour tells me which pane I am looking at.
            foreach (var mi in _glassPanes)
            {
                var a = mi.GetAabb(); var c = a.GetCenter(); var sz = a.Size;
                string col = mi.MaterialOverride is StandardMaterial3D sm
                    ? $"#{(int)(sm.AlbedoColor.R * 255):X2}{(int)(sm.AlbedoColor.G * 255):X2}{(int)(sm.AlbedoColor.B * 255):X2}" : "?";
                // thinnest axis = the pane's normal; its extent is the pane's thickness
                float t = Mathf.Min(sz.X, Mathf.Min(sz.Y, sz.Z));
                string axis = t == sz.X ? "X" : (t == sz.Y ? "Y" : "Z");
                GD.Print($"[pane] {type,-10} {mi.Name,-18} {col}  c=({c.X,6:0.00},{c.Y,6:0.00},{c.Z,7:0.00})  size=({sz.X,5:0.00},{sz.Y,5:0.00},{sz.Z,5:0.00})  thin={axis} {t:0.0000}m");
            }

            _glassCam = new Camera3D { Current = true, Fov = 55f, Far = 400f };
            AddChild(_glassCam);
            PlaceGlassCam(0);
        }

        // A headlight's beam cone is a MeshInstance like any other and it is 15.4 x 14.0 m on a sedan --
        // six times the car. Merged into the bounds it put the orbit 40 m out and shrank every vehicle to
        // a speck, which is how a fixed radius that was merely wrong became an auto radius that was wrong
        // by more. Light volumes are not the vehicle's shape, so they are not in its bounds.
        static bool IsLightVolume(MeshInstance3D mi)
        {
            string n = mi.Name.ToString();
            return n.Contains("Beam") || n.Contains("Halo") || n.Contains("Glow") || n.StartsWith("GlassShotFloor");
        }

        // The vehicle's own visible bounds, in world space.
        Aabb VehicleBounds()
        {
            var box = new Aabb(); bool first = true;
            void Grow(Node n)
            {
                if (n is MeshInstance3D mi && mi.Mesh != null && !IsLightVolume(mi))
                { var a = mi.GlobalTransform * mi.GetAabb(); if (first) { box = a; first = false; } else box = box.Merge(a); }
                foreach (var ch in n.GetChildren()) Grow(ch);
            }
            if (_veh != null) Grow(_veh);
            return first ? new Aabb(Vector3.Zero, new Vector3(4f, 2f, 4f)) : box;
        }
        Vector3 VehicleCentre() { var b = VehicleBounds(); return new Vector3(b.GetCenter().X, 0f, b.GetCenter().Z); }
        float VehicleSpan() { var b = VehicleBounds(); return Mathf.Max(b.Size.X, b.Size.Z); }

        // Aim at the centre of the panes themselves: the frame is only useful if the glass is IN it.
        Vector3 GlassAim()
        {
            if (_glassPanes == null || _glassPanes.Count == 0) { var b = VehicleBounds(); return b.GetCenter(); }
            var sum = Vector3.Zero; int n = 0;
            foreach (var mi in _glassPanes) { if (!GodotObject.IsInstanceValid(mi)) continue; sum += mi.GlobalTransform * mi.GetAabb().GetCenter(); n++; }
            return n == 0 ? new Vector3(0f, 1.05f, 0f) : sum / n;
        }

        // Eye-height orbit: stand a player's-eye camera on the ring and look at the car's mid-height, NOT down at it.
        void PlaceGlassCam(int i)
        {
            if (_glassCam == null) return;
            float yaw = GlassShotYaws[Mathf.Clamp(i, 0, GlassShotYaws.Length - 1)] * Mathf.Pi / 180f;
            // Orbit the VEHICLE, not the world origin, at a radius set by how big it is. Fixed 6.5 m
            // about (0,0,0) is fine for a sedan and wrong for anything long: a semi spans z -2.6..4.5,
            // so its cab sat 1 m off the pivot and every "front" shot was an oblique of the chassis.
            // I read that as a broken windscreen pane and nearly reported it as one.
            var c = VehicleCentre();
            float r = _glassRadiusSet ? _glassRadius : Mathf.Max(6.5f, VehicleSpan() * 1.15f);
            _glassCam.Position = new Vector3(c.X + Mathf.Sin(yaw) * r, _glassEye, c.Z + Mathf.Cos(yaw) * r);
            _glassCam.LookAt(GlassAim(), Vector3.Up);
            if (System.Environment.GetEnvironmentVariable("UG_GLASSDIAG") == "1")
            {
                var aabb = new Aabb(); bool first = true;
                void Grow(Node n) { if (n is MeshInstance3D mi && mi.Mesh != null) { var a = mi.GlobalTransform * mi.GetAabb(); if (first) { aabb = a; first = false; } else aabb = aabb.Merge(a); } foreach (var c in n.GetChildren()) Grow(c); }
                if (_veh != null) Grow(_veh);
                GD.Print($"[glassdiag] yaw {i}: cam {_glassCam.GlobalPosition} fwd {-_glassCam.GlobalTransform.Basis.Z} | veh {(_veh == null ? "NULL" : _veh.GlobalPosition.ToString())} visible {(_veh?.Visible)} meshAabb {(first ? "NONE" : aabb.ToString())}");
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

            // UG_VEHOCCUPANT=1 (+ UG_SEATIDX=N, default 0): drop a rigged body into a seat so a --vehicle= showcase
            // actually shows where a body sits, not just the empty shell -- SeatBodyLocal is the exact placement
            // PlayerController uses to seat a real driver/passenger (strawberry 2026-09-03: "theres still a lot of
            // vehicle seating positions that arent accurate. fix them all").
            if (System.Environment.GetEnvironmentVariable("UG_VEHOCCUPANT") == "1")
            {
                int seatIdx = int.TryParse(System.Environment.GetEnvironmentVariable("UG_SEATIDX"), out var si) ? si : 0;
                var occ = RiggedCharacter.Build("res://content/rig.json", new Color(0.82f, 0.66f, 0.52f));
                if (occ != null)
                {
                    AddChild(occ);
                    occ.GlobalTransform = _veh.GlobalTransform * new Transform3D(Basis.Identity, _veh.SeatBodyLocal(seatIdx));
                    occ.PlayLoop(occ.ClipLength("Idle_Sit") > 0f ? "Idle_Sit" : "Idle_Stand");
                }
            }

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

            if (_chain)   // a 2nd jeep beside _veh: when _veh blows, the blast chains to the car (source vehicle-explosion damage)
            {
                var jeep2 = Vehicle.BuildByName("jeep");
                jeep2.Position = _veh.Position + new Vector3(4f, 0f, 0f);   // ~4 m away, well inside the 8 m blast
                AddChild(jeep2);
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

            // GRASS-DISPLACEMENT VERIFICATION (master, opt-in UG_GRASSTEST=1): carpet the drive lane with the REAL grass
            // billboard on the grass_displace material, so the auto-driving jeep + the on-foot player visibly flatten it
            // -- the chase cam frames the swath. Proves the whole pipeline at once: shader parse, the C# displacer
            // texture, retail's player point, AND the vehicle texture path. No collider (visual only), so it can't
            // affect the physics this harness exists to check. Off by default -> a normal --drivetest is unchanged.
            if (System.Environment.GetEnvironmentVariable("UG_GRASSTEST") == "1")
            {
                _grassTest = true;
                string fdir = ProjectSettings.GlobalizePath("res://content/foliage/");
                var gblade = ObjMesh.Load(fdir + "grass_00.obj");
                if (gblade != null)
                {
                    GrassDisplacers.EnsureGlobals();   // globals before the grass material (same rule as FoliageField -- else it links them invalid + no displacement)
                    var gsMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://content/grass_displace.gdshader") };
                    var gimg = new Image();
                    if (ContentProvider.LoadOk(gimg, fdir + "grass_00_tex.png")) { gimg.GenerateMipmaps(); gsMat.SetShaderParameter("albedo_tex", ImageTexture.CreateFromImage(gimg)); }
                    const int side = 110; const float spacing = 0.6f;   // 110x110 blades ~0.6m apart -> a dense ~66m lawn over the whole drive path
                    var gmm = new MultiMesh { Mesh = gblade, TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, InstanceCount = side * side };
                    int gi = 0;
                    for (int gx = 0; gx < side; gx++)
                        for (int gz = 0; gz < side; gz++)
                        {
                            // deterministic jitter + yaw from the indices (no Math.random in a harness -> repeatable frames)
                            float jx = ((gx * 7 + gz * 13) % 11) * 0.03f, jz = ((gx * 11 + gz * 5) % 11) * 0.03f;
                            float yaw = ((gx * 37 + gz * 101) % 360) * Mathf.Pi / 180f;
                            var t = new Transform3D(new Basis(Vector3.Up, yaw).Scaled(Vector3.One * 1.4f), new Vector3((gx - side / 2) * spacing + jx, 0f, (gz - side / 2) * spacing + jz));   // slightly taller tufts so a flattened patch reads at a low angle without occluding
                            gmm.SetInstanceTransform(gi++, t);
                        }
                    AddChild(new MultiMeshInstance3D { Multimesh = gmm, MaterialOverride = gsMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
                }
            }

            var jeep = Vehicle.BuildByName("jeep");
            jeep.GlobalPosition = new Vector3(3f, 1.2f, 0f);
            jeep.AddToGroup("vehicles");
            AddChild(jeep);

            _dtPlayer = new PlayerController { CaptureMouse = false };
            _dtPlayer.LoadGun("res://content/eaglefire.dat");
            AddChild(_dtPlayer);
            _dtPlayer.GlobalPosition = new Vector3(0.8f, 1.0f, 0f);   // right beside the jeep (within enter range)

            if (_grassTest)   // park the player well clear of the jeep so BOTH flattened patches show separately: the player's
            {                 // retail point (small) AND the jeep's texture-path ring (wide). Frame both from a 3/4 overhead cam,
                _dtPlayer.GlobalPosition = new Vector3(-5.5f, 1f, 0f);   // created AFTER the player so its Current wins (player cam is made Current once at build, never re-asserted).
                var gcam = new Camera3D { Fov = 60f, Current = true };   // LOW 3/4 sweep across the lawn (not overhead -- tall grass occludes from above; a low angle shows the parting against the blade silhouette)
                AddChild(gcam);
                gcam.GlobalPosition = new Vector3(-9f, 4f, 5.5f);
                gcam.LookAt(new Vector3(-1f, 0.4f, 0f), Vector3.Up);
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
                        var img = ContentProvider.LoadImage(texPath);
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

            CharacterModel.LoadBundled();  // real ripped character model
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

            var freezeMode = new FreezeMode();   // ESC -> Freeze Mode: paused sim + freecam + single-tick stepping
            AddChild(freezeMode);
            var pause = new PauseMenu();   // ESC -> pause menu (freezes the sim)
            pause.Freeze = freezeMode;
            pause.WorldRoot = this;
            AddChild(pause);
            player.PauseMenu = pause;
            var attach = new AttachmentMenu();   // T -> weapon-attachment menu (iron sights removable, etc.)
            AddChild(attach);
            player.AttachMenu = attach;
            var ammoRadial = new AmmoRadial();   // R-hold -> shotgun ammo-type picker (buckshot / slug)
            AddChild(ammoRadial);
            player.AmmoRadial = ammoRadial;
            GD.Print("[PLAY] interactive: WASD move / mouse look / LMB fire / Space jump");
        }

        // --firetest [--supp]: a reusable firing-mechanics harness -- the player fires downrange (UG_HITWALL / UG_HITGLASS
        // put a concrete wall / destructible pane in front) to render the viewmodel, tracers, ADS (UG_ADS) and the
        // bullet-impact FX. --supp attaches the suppressor (verify the suppressed muzzle flash / viewmodel).
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
            player.RotationDegrees = new Vector3(0, 180f, 0);   // face +Z toward the downrange wall/glass (UG_HITWALL / UG_HITGLASS)
            { var hud = new HUD { Player = player }; AddChild(hud); player.Hud = hud; }
            _ftPlayer = player;
            if (suppressed) player.SetSuppressor(true);


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
            GD.Print($"[FIRETEST] suppressed={suppressed} -- firing downrange (viewmodel / tracer / ADS / impact rig)");
        }

        // --craftmenu: open the NEWER CraftingMenu (the browsable recipe index wired to the player as _craftMenu / Y)
        // over a bag stocked with metal scrap + a blowtorch + our tree logs, so the recipe list + craftability render.
        void BuildCraftMenu()
        {
            // a lit 3D scene behind the menu so the frosted-glass backdrop has real content to blur (like --invdemo)
            AddChild(new WorldEnvironment { Environment = new Godot.Environment {
                BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.42f, 0.55f, 0.72f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.55f, 0.57f, 0.6f), AmbientLightEnergy = 0.6f } });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-52f, -46f, 0f), LightEnergy = 1.2f });
            var gmesh = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(120, 120) } };
            gmesh.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.30f, 0.34f, 0.28f) };
            AddChild(gmesh);
            var craftBoxCols = new[] { new Color(0.85f,0.30f,0.30f), new Color(0.30f,0.70f,0.90f), new Color(0.92f,0.80f,0.32f), new Color(0.40f,0.80f,0.45f), new Color(0.92f,0.52f,0.25f), new Color(0.68f,0.42f,0.85f) };
            for (int bi = 0; bi < 6; bi++)
            {
                float ang = bi * Mathf.Pi * 2f / 6f;
                var box = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(2.4f, 3.2f + (bi % 3), 2.4f) } };
                box.MaterialOverride = new StandardMaterial3D { AlbedoColor = craftBoxCols[bi] };
                box.Position = new Vector3(Mathf.Sin(ang) * 10f, 1.6f, Mathf.Cos(ang) * 10f);
                AddChild(box);
            }
            var craftCam = new Camera3D { Position = new Vector3(0f, 2f, 6f), Fov = 60f, Current = true };
            AddChild(craftCam);
            craftCam.LookAt(new Vector3(0f, 1.5f, 0f), Vector3.Up);   // LookAt needs the node in-tree

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

        // --stationtest: place all 9 crafting-station deployables in a row on a lit ground -> verify the ripped models.
        void BuildStationTest()
        {
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.5f, 0.62f, 0.78f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.62f, 0.63f, 0.6f), AmbientLightEnergy = 0.9f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-48f, -40f, 0f), LightEnergy = 1.2f, ShadowEnabled = true });
            AddChild(new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(60f, 60f) }, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.32f, 0.4f, 0.26f), Roughness = 1f } });
            SDG.Unturned.ItemCatalog.RegisterAll();
            var stations = new[] { DeployableDef.Workbench, DeployableDef.Campfire, DeployableDef.ChemistryLab, DeployableDef.Kiln, DeployableDef.Loom, DeployableDef.OvenBrick, DeployableDef.OvenElectric, DeployableDef.SewingTable, DeployableDef.SpinningWheel };
            for (int i = 0; i < stations.Length; i++)
                Deployable.Spawn(this, stations[i], new Vector3((i - 4) * 3f, 0f, 0f), 0f);
            var cam = new Camera3D { Fov = 46f, Far = 400f };
            AddChild(cam);
            cam.LookAtFromPosition(new Vector3(0f, 7f, 17f), new Vector3(0f, 0.8f, 0f), Vector3.Up);
            GD.Print("[stationtest] 9 crafting stations placed");
        }

        // --terrain: load PEI's Landscape Tile_0_0 heightmap into a Godot terrain mesh (the first real WORLD step; replaces
        // the flat test-plane). Aerial camera over the 1024 m tile so the real terrain shape is visible.
        // --zombietier: zombie AI rewrite PHASE 1 verify (docs/ZOMBIE_REDESIGN.md). Build PEI terrain + the ZombieChunkField,
        // then sweep a debug anchor OUT of the densest town and log the tier counts at each step -- so you can watch chunks
        // (+ their zombies) flip HOT -> WARM -> COLD -> FROZEN as a "player" walks away, and confirm the 64/anchor budget holds.
        void BuildZombieTierTest()
        {
            var terr = Terrain.LoadMapMerged(MapDir("PEI") + "/Landscape/Heightmaps", withCollider: false);
            if (terr == null) { GD.Print("[zombietier] no PEI terrain -- need the retail install"); GetTree().Quit(); return; }
            AddChild(terr);
            var zf = new ZombieChunkField { Terr = terr };
            AddChild(zf);
            zf.LoadFromPei(MapDir("PEI"));

            var town = zf.DensestChunkCenter();
            GD.Print($"[zombietier] densest town chunk @ ({town.X:0},{town.Z:0}); sweeping an anchor out of it:");
            foreach (float off in new float[] { 0f, 60f, 120f, 200f, 300f, 500f })
            {
                zf.DebugAnchor = town + new Vector3(off, 0f, 0f);
                zf.ForceReclassify();
                int sim = zf.TierZombies[3] + zf.TierZombies[2];   // HOT + WARM = the simulated ones the budget caps
                GD.Print($"[zombietier] +{off,4:0}m | chunks HOT {zf.TierChunks[3]} WARM {zf.TierChunks[2]} COLD {zf.TierChunks[1]} FROZEN {zf.TierChunks[0]}"
                       + $" | zombies HOT {zf.TierZombies[3]} WARM {zf.TierZombies[2]} COLD {zf.TierZombies[1]} (sim={sim}/{ZombieChunkField.Budget}) FROZEN-potential {zf.TierZombies[0]}");
            }
            GD.Print("[zombietier] done -- tiers should shed HOT->WARM->COLD->FROZEN as the anchor leaves, and sim never exceeds the budget.");
            GetTree().Quit();
        }

        // --zflow: zombie AI rewrite PHASE 2 verify (docs/ZOMBIE_REDESIGN.md). Synthetic scene -- flat ground + a 60m WALL,
        // the "player" anchor EAST of it, 40 zombies WEST. The flow field must route them AROUND the wall's ends, so if they
        // reach EAST (past the wall) they demonstrably pathed around it (the wall blocks the direct line). Headless run logs
        // the east/at-wall/west split; a `--write-movie` run records the flow (top-down cam + red dots).
        bool _zflowMode; ZombieChunkField _zff; Vector3 _zflowAnchor; double _zflowT;
        Node3D _zflowDotRoot; readonly System.Collections.Generic.List<MeshInstance3D> _zflowDots = new();

        void BuildZombieFlow()
        {
            GetWindow().Size = new Vector2I(1000, 1000);
            var env = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.12f, 0.13f, 0.15f), AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.8f, 0.8f, 0.85f), AmbientLightEnergy = 1f };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-70f, -30f, 0f), LightEnergy = 1f });

            var ground = new StaticBody3D { CollisionLayer = WorldLayers.World };
            ground.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(500f, 1f, 500f) }, Position = new Vector3(0f, -0.5f, 0f) });   // thin box, top at y=0 -> below the walkability probe (a WorldBoundaryShape half-space tripped the probe everywhere)
            var gm = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(400, 400) } };
            gm.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.26f, 0.30f, 0.24f) };
            ground.AddChild(gm); AddChild(ground);

            var wsize = new Vector3(6f, 5f, 60f);   // a 60m wall along Z at x=0 -- open only at each end
            var wall = new StaticBody3D { CollisionLayer = WorldLayers.World };
            wall.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = wsize } });
            var wm = new MeshInstance3D { Mesh = new BoxMesh { Size = wsize } };
            wm.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.42f, 0.35f) };
            wall.AddChild(wm); AddChild(wall); wall.Position = new Vector3(0f, 2.5f, 0f);

            var zf = new ZombieChunkField();   // no Terr -> flat ground at y=0
            AddChild(zf);
            _zflowAnchor = new Vector3(45f, 0f, 0f);
            zf.DebugAnchor = _zflowAnchor;
            zf.DebugSeed(new Vector3(-45f, 0f, 0f), 40, spread: 44f);
            _zff = zf;

            var am = new MeshInstance3D { Mesh = new SphereMesh { Radius = 1.6f, Height = 3.2f } };
            am.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.2f, 1f, 0.3f), EmissionEnabled = true, Emission = new Color(0.15f, 0.8f, 0.25f) };
            AddChild(am); am.Position = _zflowAnchor + Vector3.Up * 1.5f;

            _zflowDotRoot = new Node3D(); AddChild(_zflowDotRoot);
            var cam = new Camera3D { Current = true, Fov = 55f, Far = 4000f };
            AddChild(cam); cam.Position = new Vector3(0f, 175f, 0.01f); cam.LookAt(Vector3.Zero, Vector3.Forward);
            _zflowMode = true;
            GD.Print("[zflow] wall scene: anchor +45x, 40 zombies -45x, 60m wall at x=0 -> they must round the ends.");
        }

        void UpdateZflowDots()
        {
            if (_zff == null) return;
            int i = 0;
            foreach (var z in _zff.DebugZombies())
            {
                MeshInstance3D dot;
                if (i < _zflowDots.Count) dot = _zflowDots[i];
                else
                {
                    dot = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(1.5f, 1.9f, 1.5f) } };
                    dot.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.9f, 0.2f, 0.2f), EmissionEnabled = true, Emission = new Color(0.5f, 0.08f, 0.08f) };
                    _zflowDotRoot.AddChild(dot); _zflowDots.Add(dot);
                }
                dot.Position = z.Pos + Vector3.Up * 0.95f;
                i++;
            }
        }

        void ZflowReport()
        {
            int west = 0, atwall = 0, east = 0; float minx = 1e9f, maxx = -1e9f, sumx = 0; int n = 0;
            foreach (var z in _zff.DebugZombies())
            {
                if (z.Pos.X > 8f) east++;
                else if (z.Pos.X < -8f) west++;
                else atwall++;
                minx = Mathf.Min(minx, z.Pos.X); maxx = Mathf.Max(maxx, z.Pos.X); sumx += z.Pos.X; n++;
            }
            // The proof the field ROUTES rather than pushing into the wall: sample its direction just WEST of the wall
            // centre -- it must aim toward an END (|z| grows), NOT straight +x into the wall.
            var f = _zff.Field;
            var behind = f.Sample(new Vector3(-10f, 0f, 0f));      // OPEN cell directly behind the wall centre -> must point to an END (strong |Z|)
            var mid = f.Sample(new Vector3(-25f, 0f, 0f));         // further back, still behind centre -> still angled to an end
            var end = f.Sample(new Vector3(-10f, 0f, 33f));        // OPEN cell past the north end -> curls east (+X) around it
            GD.Print($"[zflow] after {_zflowT:0}s: EAST(past wall) {east} / at-wall {atwall} / WEST {west} | x min {minx:0} avg {(n>0?sumx/n:0):0} max {maxx:0}");
            GD.Print($"[zflow] field: {f.BlockedCells}/{f.CellCount} cells blocked; cost behind-centre(-10,0)={f.CostAt(new Vector3(-10,0,0))} vs at-end(-10,33)={f.CostAt(new Vector3(-10,0,33))}  [behind should cost MORE if routing around]");
            GD.Print($"[zflow] field dir behind-centre (-10,0)= ({behind.X:0.00},{behind.Y:0.00})  further (-25,0)= ({mid.X:0.00},{mid.Y:0.00})  past-end (-10,33)= ({end.X:0.00},{end.Y:0.00})   [strong Y behind-centre => routing toward an end, NOT straight through]");
            _zflowMode = false;
            GetTree().Quit();
        }

        // --zhunt: zombie AI rewrite PHASE 3 verify. A cluster of zombies ~24 m from a "player" anchor -> each promotes to
        // a HOT ZombieBody (ripped rig + collision) and shambles in, separating from its neighbours. Headless logs the HOT
        // count + closing distance; a `--write-movie` run shows the visible horde.
        bool _zhMode; ZombieChunkField _zhf; Vector3 _zhAnchor; double _zhT;

        void BuildZombieHunt()
        {
            GetWindow().Size = new Vector2I(1280, 720);
            var env = new Godot.Environment { AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.62f, 0.62f, 0.68f), AmbientLightEnergy = 1f, TonemapMode = Godot.Environment.ToneMapper.Aces };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-55f, -50f, 0f), LightEnergy = 1.3f, ShadowEnabled = true });

            var ground = new StaticBody3D { CollisionLayer = WorldLayers.World };
            ground.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(300f, 1f, 300f) }, Position = new Vector3(0f, -0.5f, 0f) });
            var gm = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(300f, 300f) } };
            gm.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.30f, 0.34f, 0.28f) };
            ground.AddChild(gm); AddChild(ground);

            var zf = new ZombieChunkField();   // no Terr -> flat ground at y=0
            AddChild(zf);
            _zhAnchor = Vector3.Zero;
            zf.DebugAnchor = _zhAnchor;
            zf.DebugSeed(new Vector3(0f, 0f, -24f), 24, spread: 30f);   // cluster ~10-40 m out, all within the 45 m HOT radius
            _zhf = zf;

            var am = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.5f, Height = 1.8f } };
            am.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.2f, 1f, 0.3f), EmissionEnabled = true, Emission = new Color(0.15f, 0.8f, 0.25f) };
            AddChild(am); am.Position = new Vector3(0f, 0.9f, 0f);

            var cam = new Camera3D { Current = true, Fov = 55f, Far = 2000f };
            AddChild(cam); cam.Position = new Vector3(6f, 6f, 16f); cam.LookAt(new Vector3(0f, 1f, -14f), Vector3.Up);
            _zhMode = true;
            GD.Print("[zhunt] 24 zombies ~24m from the anchor -> HOT bodies shamble in.");
        }

        void ZhuntReport()
        {
            int hot = 0; float sum = 0; int n = 0;
            foreach (var z in _zhf.DebugZombies())
            {
                if (z.Body != null && GodotObject.IsInstanceValid(z.Body)) hot++;
                sum += Mathf.Sqrt(z.Pos.X * z.Pos.X + z.Pos.Z * z.Pos.Z); n++;
            }
            GD.Print($"[zhunt] after {_zhT:0}s: {hot} HOT bodies of {n} zombies; avg dist to anchor {(n > 0 ? sum / n : 0):0.0}m (started ~24m -> CLOSES as they shamble in)");
            _zhMode = false;
            GetTree().Quit();
        }

        // --zkill: zombie AI rewrite PHASE 3b verify. A player faces a cluster of HOT zombies 10 m downrange and auto-fires;
        // the bullets must damage them (ragdoll on death) and the player's Kills counter must climb. Headless log.
        bool _zkMode; ZombieChunkField _zkf; PlayerController _zkPlayer; double _zkT; int _zkFrame;

        void BuildZombieKill()
        {
            var env = new Godot.Environment { AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.7f, 0.7f, 0.75f), AmbientLightEnergy = 1f };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-55f, -40f, 0f), LightEnergy = 1.2f });
            var ground = new StaticBody3D { CollisionLayer = WorldLayers.World };
            ground.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(200f, 1f, 200f) }, Position = new Vector3(0f, -0.5f, 0f) });
            var gm = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(200f, 200f) } };
            gm.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.30f, 0.34f, 0.28f) };
            ground.AddChild(gm); AddChild(ground);

            var player = new PlayerController();
            player.LoadGun("res://content/eaglefire.dat");
            AddChild(player);
            player.GlobalPosition = new Vector3(0f, 1f, 0f);
            player.RotationDegrees = new Vector3(0f, 180f, 0f);   // face +Z (mirror --firetest, which fires +Z and works)
            { var hud = new HUD { Player = player }; AddChild(hud); player.Hud = hud; }

            var zf = new ZombieChunkField();
            AddChild(zf);
            zf.DebugAnchor = player.GlobalPosition;             // they hunt the player -> HOT + chase into the line of fire
            zf.DebugSeed(new Vector3(0f, 0f, 7f), 3, spread: 1f);   // a tight column dead ahead in the fire line
            _zkf = zf; _zkPlayer = player;
            _zkMode = true;
            GD.Print("[zkill] player vs 6 zombies 10m downrange, auto-firing...");
        }

        void ZkillReport()
        {
            int alive = 0; float minHp = 999f; ZombieBody sample = null;
            foreach (var z in _zkf.DebugZombies()) if (z.Body != null && GodotObject.IsInstanceValid(z.Body)) { if (!z.Body.Dead) { alive++; sample ??= z.Body; } minHp = Mathf.Min(minHp, z.Body.Health); }
            GD.Print($"[zkill] after {_zkT:0}s: gun fired (Ammo {_zkPlayer.Ammo}); a bullet CONNECTED -> lowest zombie HP {minHp:0} (< 100 = the ZombieBody hit-wiring works); player Kills={_zkPlayer.Kills}");
            // The death path (Die -> ragdoll -> despawn) shares the same Damage entry the bullets already proved. Trigger it lethally to confirm it fires.
            if (sample != null) { bool wasDead = sample.Dead; sample.Damage(999f, _zkPlayer.GlobalPosition); GD.Print($"[zkill] lethal test: 999 dmg -> zombie Dead {wasDead}->{sample.Dead} (true = Die() ran: ragdoll + leaves the group + despawns)"); }
            _zkMode = false;
            GetTree().Quit();
        }

        // --zsound: zombie AI rewrite PHASE 4 verify. A "player" (anchor) sits at origin but OUT OF SIGHT of a cluster of
        // zombies 35 m away. For 3 s: silence -> they hold (stealth). Then a gunshot lands 72 m out (behind them, away from
        // the player) -> they LURE to the noise, walking toward the gunshot, NOT the player. Log: dist-to-gunshot shrinks,
        // dist-to-player grows. A --write-movie run shows it.
        bool _zsMode; ZombieChunkField _zsf; Vector3 _zsSound; double _zsT; bool _zsFired;

        void BuildZombieSound()
        {
            GetWindow().Size = new Vector2I(1280, 720);
            var env = new Godot.Environment { AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.6f, 0.6f, 0.66f), AmbientLightEnergy = 1f, TonemapMode = Godot.Environment.ToneMapper.Aces };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-55f, -40f, 0f), LightEnergy = 1.3f, ShadowEnabled = true });
            var ground = new StaticBody3D { CollisionLayer = WorldLayers.World };
            ground.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(400f, 1f, 400f) }, Position = new Vector3(0f, -0.5f, 0f) });
            var gm = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(400f, 400f) } };
            gm.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.28f, 0.32f, 0.26f) };
            ground.AddChild(gm); AddChild(ground);

            var zf = new ZombieChunkField();
            AddChild(zf);
            zf.DebugAnchor = Vector3.Zero;                      // the player -- but the zombies are 35m off, past the 24m sight range
            zf.DebugSeed(new Vector3(0f, 0f, -35f), 10, spread: 14f);
            _zsf = zf;
            _zsSound = new Vector3(0f, 0f, -72f);               // the gunshot lands here (behind the zombies, away from the player)

            var pm = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.6f, Height = 2f } };   // green = player
            pm.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.2f, 1f, 0.3f), EmissionEnabled = true, Emission = new Color(0.15f, 0.8f, 0.25f) };
            AddChild(pm); pm.Position = new Vector3(0f, 1f, 0f);
            var sm = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.8f, Height = 1.6f } };  // orange = the gunshot point
            sm.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(1f, 0.55f, 0.1f), EmissionEnabled = true, Emission = new Color(1f, 0.45f, 0.05f) };
            AddChild(sm); sm.Position = _zsSound + Vector3.Up * 0.8f;

            var cam = new Camera3D { Current = true, Fov = 58f, Far = 2000f };
            AddChild(cam); cam.Position = new Vector3(34f, 20f, -35f); cam.LookAt(new Vector3(0f, 1f, -52f), Vector3.Up);
            _zsMode = true;
            GD.Print("[zsound] player at origin (out of sight); 10 zombies at -35 -> silent hold; gunshot at -72 @ 3s -> lure to the NOISE.");
        }

        void ZsoundReport()
        {
            float toSound = 0f, toPlayer = 0f; int n = 0;
            foreach (var z in _zsf.DebugZombies()) { toSound += z.Pos.DistanceTo(_zsSound); toPlayer += Mathf.Sqrt(z.Pos.X * z.Pos.X + z.Pos.Z * z.Pos.Z); n++; }
            GD.Print($"[zsound] after {_zsT:0}s: avg dist to GUNSHOT {(n > 0 ? toSound / n : 0):0}m (started ~37 -> SHRINKS = lured by the noise); avg dist to PLAYER {(n > 0 ? toPlayer / n : 0):0}m (started ~35 -> GROWS = they chased the sound, NOT the player)");
            _zsMode = false;
            GetTree().Quit();
        }

        // --zface: FACING DIAGNOSTIC. ONE zombie, DesiredVel forced to world +X, viewed TOP-DOWN so the world axes are
        // unambiguous (RED ball = +X = the movement target, BLUE ball = +Z). Whichever ball the model's arms/face point
        // at tells us the exact rig yaw offset -- ends the "which sign" guessing on ZombieBody's facing. Arms at RED = OK.
        bool _zfMode; ZombieBody _zfz; double _zfT;
        void BuildZombieFace()
        {
            GetWindow().Size = new Vector2I(1280, 720);
            var env = new Godot.Environment { AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.8f, 0.8f, 0.85f), AmbientLightEnergy = 1f, TonemapMode = Godot.Environment.ToneMapper.Aces };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-80f, 10f, 0f), LightEnergy = 1.1f });
            var ground = new StaticBody3D { CollisionLayer = WorldLayers.World };
            ground.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(60f, 1f, 60f) }, Position = new Vector3(0f, -0.5f, 0f) });
            var gm = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(60f, 60f) } };
            gm.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.30f, 0.34f, 0.28f) };
            ground.AddChild(gm); AddChild(ground);

            void Ball(Vector3 p, Color c) { var m = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.7f, Height = 1.4f } }; m.MaterialOverride = new StandardMaterial3D { AlbedoColor = c, EmissionEnabled = true, Emission = c }; AddChild(m); m.Position = p; }
            Ball(new Vector3(7f, 0.7f, 0f), new Color(1f, 0.1f, 0.1f));    // +X = RED (the movement target)
            Ball(new Vector3(0f, 0.7f, 7f), new Color(0.15f, 0.4f, 1f));   // +Z = BLUE
            Ball(new Vector3(-7f, 0.7f, 0f), new Color(0.35f, 0f, 0f));    // -X = dark red
            Ball(new Vector3(0f, 0.7f, -7f), new Color(0f, 0f, 0.35f));    // -Z = dark blue

            _zfz = new ZombieBody(); AddChild(_zfz); _zfz.Position = Vector3.Zero;
            var cam = new Camera3D { Current = true, Fov = 46f, Far = 500f };
            AddChild(cam); cam.Position = new Vector3(6f, 3f, 15f); cam.LookAt(new Vector3(6f, 1f, 0f), Vector3.Up);   // wide SIDE view: zombie travels +X (screen-right) across frame; a planted foot should hold its WORLD spot, not skate back
            _zfMode = true;
            GD.Print("[zface] one zombie, DesiredVel = world +X (toward RED). top-down: RED=+X(right) BLUE=+Z(down). arms should point at RED if facing is correct.");
        }

        // --zpath: PATHFINDING-AROUND-OBSTACLES demo (master: "show how they path around objects"). A horde spawns BEHIND a
        // long wall; the target (green) sits beyond it. A repeating noise at the target keeps the flow field flooded from
        // there; the wall blocks both LOS (no beeline) and the walkability probe, so the BFS routes the horde AROUND the
        // wall's open right end -> they stream around it, then beeline once they round the corner and can see the target.
        bool _zpMode, _zpReported; ZombieChunkField _zpf; Vector3 _zpTarget; MeshInstance3D _zpMarker; double _zpT, _zpNextEmit = 0.4;
        // Diagnostic: at t=25s, log every zombie that hasn't reached the target -> WHERE it's stuck (near the wall X~0? in
        // the open? which end?) so the stuck cause is data, not a guess.
        void ZpathReport()
        {
            int far = 0, atWall = 0, total = 0;
            foreach (var z in _zpf.DebugZombies())
            {
                total++;
                float dT = new Vector2(z.Pos.X - _zpTarget.X, z.Pos.Z - _zpTarget.Z).Length();
                bool wall = Mathf.Abs(z.Pos.X) < 4f;
                if (dT > 4f) { far++; if (wall) atWall++; GD.Print($"[zpath] STUCK at ({z.Pos.X:0.0},{z.Pos.Z:0.0}) dT {dT:0.0}m  {(wall ? "<-AT WALL (genuine)" : "(en route near target)")}"); }
            }
            GD.Print($"[zpath] after 25s: {atWall}/{total} genuinely stuck AT WALL; {far - atWall} more en route near the target");
        }
        void BuildZombiePath()
        {
            GetWindow().Size = new Vector2I(1280, 720);
            var env = new Godot.Environment { AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.62f, 0.62f, 0.68f), AmbientLightEnergy = 1f, TonemapMode = Godot.Environment.ToneMapper.Aces };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-55f, -50f, 0f), LightEnergy = 1.3f, ShadowEnabled = true });

            var ground = new StaticBody3D { CollisionLayer = WorldLayers.World };
            ground.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(300f, 1f, 300f) }, Position = new Vector3(0f, -0.5f, 0f) });
            var gm = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(300f, 300f) } };
            gm.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.30f, 0.34f, 0.28f) };
            ground.AddChild(gm); AddChild(ground);

            // THE WALL -- a tall barrier running along Z (16 m, X=-1..+1), dead between the horde (left, -X) and the target
            // (right, +X). The straight line is blocked, so the flow field forks the horde around BOTH open ends (Z=+-8).
            var wsz = new Vector3(2f, 4f, 16f);
            var wall = new StaticBody3D { CollisionLayer = WorldLayers.World };
            wall.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = wsz }, Position = Vector3.Zero });
            var wm = new MeshInstance3D { Mesh = new BoxMesh { Size = wsz } };
            wm.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.52f, 0.5f) };
            wall.AddChild(wm); AddChild(wall); wall.Position = new Vector3(0f, 2f, 0f);

            var zf = new ZombieChunkField();   // no Terr -> flat ground at y=0
            AddChild(zf);
            _zpTarget = new Vector3(11f, 0f, 0f);
            zf.DebugAnchor = _zpTarget;
            zf.DebugSeed(new Vector3(-12f, 0f, 0f), 18, spread: 12f);   // spread across the wall's height so the field FORKS them -- upper half round the top end, lower half the bottom
            _zpf = zf;

            var am = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.6f, Height = 1.8f } };   // green = target (other side of the wall)
            am.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.2f, 1f, 0.3f), EmissionEnabled = true, Emission = new Color(0.15f, 0.8f, 0.25f) };
            AddChild(am); am.Position = _zpTarget + Vector3.Up * 0.9f; _zpMarker = am;

            var cam = new Camera3D { Current = true, Fov = 52f, Far = 2000f };
            AddChild(cam); cam.Position = new Vector3(0f, 40f, 0.01f); cam.LookAt(Vector3.Zero, new Vector3(0f, 0f, -1f));   // TOP-DOWN (-Z up, +X right) -> the fork around the wall reads cleanly
            _zpMode = true;
            GD.Print("[zpath] 18 zombies vs a 16m wall between them and the target (green). Noise at the target floods the flow field -> they FORK around both ends.");
        }

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
            if (ContentProvider.LoadOk(cimg, dir + "Container_0_tex.png")) { cimg.GenerateMipmaps(); cmat.AlbedoTexture = ImageTexture.CreateFromImage(cimg); }
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
            if (System.IO.File.Exists(tp)) { var img = new Image(); if (ContentProvider.LoadOk(img, tp)) { img.GenerateMipmaps(); mat.AlbedoTexture = ImageTexture.CreateFromImage(img); } }
            var propMi = new MeshInstance3D { Mesh = mesh, MaterialOverride = mat };
            { var _pr = System.Environment.GetEnvironmentVariable("UG_PROPROT"); if (!string.IsNullOrEmpty(_pr)) { var a = _pr.Split(','); propMi.RotationDegrees = new Vector3(float.Parse(a[0]), float.Parse(a[1]), float.Parse(a[2])); } }   // UG_PROPROT: reorient the prop (e.g. the elevator's stand-up) for the showcase
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
            if (System.Environment.GetEnvironmentVariable("UG_PROPSPIN") == "1") { _orbitCam = cam; _orbitCenter = propMi.Transform.Basis * c; _orbitR = r * 1.7f; }   // 360 turntable movie
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
            Material Tex(string t) { var m = new StandardMaterial3D { TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest, Metallic = 0f, Roughness = 0.75f, CullMode = BaseMaterial3D.CullModeEnum.Disabled }; var img = new Image(); if (ContentProvider.LoadOk(img, ProjectSettings.GlobalizePath($"res://content/{t}.png"))) m.AlbedoTexture = ImageTexture.CreateFromImage(img); return m; }
            Material carMat = Tex("train_car_tex"), bogieMat = Tex("train_bogie_tex");
            // PAINTABLE LIVERY: recolour the body palette slot (blue) to a random livery, and the stripe slot
            // (orange) STAYS fixed orange (master). Demo body colour here; per-spawn xorshift in the real vehicle.
            Color livery = new Color(0.16f, 0.42f, 0.22f);
            var bodyMat = new StandardMaterial3D { TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest, Metallic = 0f, Roughness = 0.75f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            var _bimg = new Image();
            if (ContentProvider.LoadOk(_bimg, ProjectSettings.GlobalizePath("res://content/train_body_tex.png"))) { _bimg.Convert(Image.Format.Rgba8); _bimg.SetPixel(0, 1, livery); bodyMat.AlbedoTexture = ImageTexture.CreateFromImage(_bimg); }
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
            Material Tex(string tn) { var m = new StandardMaterial3D { TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest, Roughness = 0.75f, CullMode = BaseMaterial3D.CullModeEnum.Disabled }; var img = new Image(); if (ContentProvider.LoadOk(img, ProjectSettings.GlobalizePath($"res://content/{tn}.png"))) m.AlbedoTexture = ImageTexture.CreateFromImage(img); return m; }
            var carMat = Tex("train_car_tex"); var bogieMat = Tex("train_bogie_tex");
            var bodyMat = new StandardMaterial3D { TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest, Roughness = 0.75f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            { var bimg = new Image(); if (ContentProvider.LoadOk(bimg, ProjectSettings.GlobalizePath("res://content/train_body_tex.png"))) { bimg.Convert(Image.Format.Rgba8); bimg.SetPixel(0, 1, new Color(0.16f, 0.42f, 0.22f)); bodyMat.AlbedoTexture = ImageTexture.CreateFromImage(bimg); } }
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
                if (System.IO.File.Exists(tp)) { var img = new Image(); if (ContentProvider.LoadOk(img, tp)) mat.AlbedoTexture = ImageTexture.CreateFromImage(img); else mat.AlbedoColor = new Color(0.5f, 0.36f, 0.22f); }
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
            if (System.IO.File.Exists(tp)) { var img = new Image(); if (ContentProvider.LoadOk(img, tp)) { img.GenerateMipmaps(); mat.AlbedoTexture = ImageTexture.CreateFromImage(img); } }

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
            var img = System.IO.File.Exists(mp) ? ContentProvider.LoadImage(mp) : null;
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
                    if (System.IO.File.Exists(tp)) { var img = new Image(); if (ContentProvider.LoadOk(img, tp)) mat.AlbedoTexture = ImageTexture.CreateFromImage(img); else mat.AlbedoColor = new Color(0.62f, 0.66f, 0.70f); }
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

        // --profileshot=OUT: two rigged bodies wearing real Nameplates -- one with a VALID 128x128 picture that
        // travels the real client-side acceptance path (ClientAcceptAvatar -> DecodeAvatar), and one whose
        // picture is refused, so the missing-texture checkerboard is in the same frame as the thing it stands
        // in for. This verifies the RENDER; the wire path is covered by ProfileReplicationTests, and a
        // screenshot is not evidence about networking.
        void BuildProfilePlateDemo()
        {
            // Ambient, not just a key light: a DirectionalLight alone leaves the rig's unlit faces pure black,
            // which makes the bodies read as silhouettes and the shot useless for judging anything but the plate.
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.10f, 0.12f, 0.16f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.55f, 0.58f, 0.66f),
                AmbientLightEnergy = 1.0f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { Rotation = new Vector3(Mathf.DegToRad(-40f), Mathf.DegToRad(35f), 0f), LightEnergy = 1.4f });

            var repl = new UnturnedGodot.Net.PlayerProfileReplication();
            byte[] good = MakeDemoAvatarPng();
            ulong hash = SDG.Unturned.ProfileRules.AvatarHash(good);
            bool accepted = repl.ClientAcceptAvatar(hash, good);   // the REAL acceptance path: header re-check + hash recompute
            GD.Print($"[PROFILESHOT] avatar accepted by the client path: {accepted} ({good.Length} bytes)");

            void Place(float x, string name, byte[] png)
            {
                var body = UnturnedGodot.RiggedCharacter.Build("res://content/rig.json", new Color(0.82f, 0.66f, 0.52f));
                if (body == null) { GD.Print("[PROFILESHOT] rig.json failed to load"); return; }
                body.PlayLoop("Idle");
                body.Position = new Vector3(x, 0f, 0f);
                AddChild(body);
                var plate = UnturnedGodot.Nameplate.Attach(body);
                plate?.Set(name, png);
                GD.Print($"[PROFILESHOT] '{name}' plate: text='{plate?.DebugText}' missingTexture={plate?.DebugShowingMissingTexture}");
            }

            // Far enough apart that the two plates cannot overlap -- a screenshot where the names run into
            // each other cannot answer "does the name render correctly", which is the only thing it is for.
            Place(-1.6f, "strawberry_cow", good);
            // The SAME bytes with the wrong dimensions: refused everywhere, so the plate falls back.
            Place(1.6f, "no_picture_set", MakeWrongSizePng());

            var cam = new Camera3D { Current = true, Fov = 45f };
            AddChild(cam);
            cam.Position = new Vector3(0f, 1.6f, 5.2f);
            cam.LookAt(new Vector3(0f, 1.35f, 0f), Vector3.Up);
        }

        /// <summary>A recognisable 128x128 PNG built in-process: a claw-red field with a lighter diagonal, so
        /// the screenshot shows something that is obviously A PICTURE rather than a flat swatch that could be
        /// a fallback colour.</summary>
        static byte[] MakeDemoAvatarPng()
        {
            const int n = SDG.Unturned.ProfileRules.AvatarPixels;
            var img = Image.CreateEmpty(n, n, false, Image.Format.Rgb8);
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    bool stripe = ((x + y) / 12) % 2 == 0;
                    bool ring = System.Math.Abs((x - n / 2) * (x - n / 2) + (y - n / 2) * (y - n / 2) - 2200) < 700;
                    img.SetPixel(x, y, ring ? new Color(1f, 0.95f, 0.6f)
                                            : stripe ? new Color(0.85f, 0.18f, 0.30f) : new Color(0.55f, 0.10f, 0.20f));
                }
            return img.SavePngToBuffer();
        }

        static byte[] MakeWrongSizePng()
        {
            var img = Image.CreateEmpty(64, 64, false, Image.Format.Rgb8);
            img.Fill(new Color(0.2f, 0.7f, 0.4f));
            return img.SavePngToBuffer();
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

        // --puppetanim: prove the RemotePlayers locomotion drive animates. A player rig.json body driven idle->walk->run
        // via SetLocomotion(speed) + Tick(delta) -- the exact calls RemotePlayers makes each frame -- over a --write-movie run.
        void BuildPuppetAnim()
        {
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.30f, 0.34f, 0.40f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.80f, 0.80f, 0.80f), AmbientLightEnergy = 1.0f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-50f, -40f, 0f), LightEnergy = 1.3f });
            AddChild(new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(30f, 30f) }, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.20f, 0.22f, 0.26f) } });   // ground
            _paRig = RiggedCharacter.Build("res://content/rig.json", new Color(0.82f, 0.66f, 0.52f));
            if (_paRig == null) { GD.PrintErr("[puppetanim] rig build failed"); GetTree().Quit(1); return; }
            AddChild(_paRig);
            _paRig.PlayLoop("Idle_Stand");
            if (System.Environment.GetEnvironmentVariable("UG_PAGUN") == "1")
            {
                _paRig.AttachGun("eaglefire");    // gun mesh on Right_Hook + upper-body gun layer -> held while the legs run locomotion/stance
                _paRig.EnableGunLayer("Eaglefire_Aim");
                _paRig.SetGunOverlay("Eaglefire_Equip", 1f, loop: false);   // play the equip -> holds its end = the READY HOLD (shouldered), exactly like the local 3p body (PlayerController:6734)
                _paGun = true;   // -> the hold -> ADS -> lean-right -> lean-left sequence in _Process
            }
            string paMelee = System.Environment.GetEnvironmentVariable("UG_PAMELEE");   // UG_PAMELEE=katana -> the melee model in the 3P hand (RiggedCharacter.AttachMelee, what puppets/own body call)
            if (!string.IsNullOrEmpty(paMelee)) { _paRig.AttachMelee(paMelee); _paRig.ShowMeleeHold(paMelee); }   // + the ready hold; UG_PASWING=1 fires a strong swing at 0.8s
            if (System.Environment.GetEnvironmentVariable("UG_PAHITBOX") == "1")
            {
                _paHit = true;   // hold the chosen stance under the zone overlay
                string sv = System.Environment.GetEnvironmentVariable("UG_PASTANCE") ?? "stand";
                _paStance = sv == "crouch" ? (byte)2 : (sv == "prone" ? (byte)3 : (byte)0);
                _paLean = sv == "lean" ? 24f : 0f;
                // the EXACT server damage zones for this stance (ServerCombat.PlayerHitZones -- the same the hit test uses),
                // rolled with the lean so the boxes track the tilted body.
                UnturnedGodot.Net.ServerCombat.PlayerHitZones(_paStance, out float zr, out float ztop, out float zhm, out float ztm);
                var zroot = new Node3D();
                AddChild(zroot);
                // A lean pivots at the SPINE bone (rig.json bone 1, y 0.735) and rolls only the spine chain --
                // Spine and Left_Hip are SIBLINGS off the Skeleton root, so TorsoPoseModifier tilts head/torso/arms
                // while the legs stay planted. Rotating the whole stack about the origin (the old -_paLean on zroot)
                // was wrong twice over: mirrored in sign, and it tilted the leg box off the legs it was covering.
                var leanN = new Node3D { Position = new Vector3(0f, 0.735f, 0f), RotationDegrees = new Vector3(0f, 0f, _paLean) };
                zroot.AddChild(leanN);
                Color red = new(1f, 0.22f, 0.22f), yel = new(1f, 0.82f, 0.2f), blu = new(0.32f, 0.62f, 1f), grn = new(0.36f, 0.95f, 0.45f);
                StandardMaterial3D ZMat(Color c) => new() { AlbedoColor = new Color(c.R, c.G, c.B, 0.42f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
                // EXACT boxes measured off the SKINNED mesh per stance (center + size, x/y/z)
                // upper:true -> under the lean pivot (head/torso/arms); false -> planted with the legs.
                void Box(float cx, float cy, float cz, float sx, float sy, float sz, Color c, bool upper = true) =>
                    (upper ? leanN : zroot).AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(sx, sy, sz) }, Position = new Vector3(cx, upper ? cy - 0.735f : cy, cz), MaterialOverride = ZMat(c) });
                // FOUR regions (strawberry 2026-09-02): torso is the Spine CORE only, arms are their own x0.6 region
                // per side. Every number below is the per-part AABB printed by the [mesh] measurement pass in
                // _Process -- skinned verts grouped by the bone their SKIN SLOT resolves to, per stance. Nothing eyeballed.
                switch (_paStance)
                {
                    case 2:  // CROUCH -- arms up to the torso top (1.16); legs already overlap the torso bottom, no gap
                        Box( 0f,     1.265f, -0.305f, 0.40f, 0.61f, 0.47f, red);
                        Box( 0f,     0.765f, -0.085f, 1.12f, 0.79f, 0.79f, yel);
                        Box(-0.620f, 0.695f, -0.085f, 0.44f, 0.93f, 0.57f, grn);
                        Box( 0.675f, 0.685f, -0.085f, 0.49f, 0.95f, 0.65f, grn);
                        Box( 0.005f, 0.260f,  0.215f, 0.81f, 0.66f, 1.03f, blu, upper: false); break;
                    case 3:  // PRONE -- body is along Z, so the gaps close in Z: arms back to the torso front face
                             // (-0.46), legs forward to the torso back face (0.34)
                        Box( 0f,     0.660f, -0.355f, 0.40f, 0.60f, 0.47f, red);
                        Box( 0f,     0.260f, -0.060f, 1.12f, 0.48f, 0.80f, yel);
                        Box(-0.425f, 0.170f, -0.755f, 0.43f, 0.46f, 0.59f, grn);
                        Box( 0.425f, 0.170f, -0.755f, 0.43f, 0.46f, 0.59f, grn);
                        Box( 0.005f, 0.200f,  0.705f, 0.81f, 0.38f, 0.73f, blu, upper: false); break;
                    default: // STAND -- arms up to the torso top (1.50) to cover the shoulders, legs up to the
                             // torso bottom (0.75). A per-part AABB leaves gaps between regions; hit zones must TILE.
                        Box( 0f,     1.680f, -0.010f, 0.40f, 0.56f, 0.40f, red);
                        Box( 0f,     1.125f, -0.010f, 1.12f, 0.75f, 0.38f, yel);
                        Box(-0.605f, 1.050f, -0.010f, 0.41f, 0.90f, 0.40f, grn);
                        Box( 0.605f, 1.050f, -0.010f, 0.43f, 0.90f, 0.40f, grn);
                        Box( 0.005f, 0.365f, -0.005f, 0.81f, 0.77f, 0.39f, blu, upper: false); break;
                }
                // fixed side legend so the labels never cover the body
                // The old legend sat at x=1.7,z=0 and DID cover the body from the default camera (verified in a
                // render), which is exactly what makes a fit look wrong. Offset it along the camera-right vector
                // (~0.49,0,-0.85 for UG_PACAM 0) and shrink it so it sits clear beside the model.
                // Legend heights ride the stance's camera target, or prone (target y 0.32) pushes them off the top.
                float lgB = _paStance == 3 ? 0.32f : (_paStance == 2 ? 0.68f : 0.98f);
                void Lg(float dy, Color c, string t) => AddChild(new Label3D { Text = t, Position = new Vector3(0.72f, lgB + dy, -1.25f), Modulate = c, FontSize = 68, PixelSize = 0.0022f, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = true, OutlineSize = 10 });
                Lg(0.74f, red, "HEAD  x2.0"); Lg(0.50f, yel, "TORSO  x1.0"); Lg(0.26f, grn, "ARMS  x0.6"); Lg(0.02f, blu, "LEGS  x0.6");
            }
            var cam = new Camera3D { Current = true, Fov = 42f, Far = 200f };
            AddChild(cam);
            string camv = System.Environment.GetEnvironmentVariable("UG_PACAM") ?? "0";   // 0 3/4-front, 1 side, 2 front, 3 low-3/4
            cam.Position = camv switch
            {
                "1" => new Vector3(9.2f, 1.5f, 0.4f),
                "2" => new Vector3(0.5f, 1.6f, 9.2f),
                "3" => new Vector3(5.6f, 0.95f, 5.6f),
                _ => new Vector3(7.2f, 2.60f, 4.15f),
            };
            float look = _paStance == 3 ? 0.32f : (_paStance == 2 ? 0.68f : 0.98f);   // lower target for crouch/prone
            cam.LookAt(new Vector3(0f, look, 0f), Vector3.Up);
            _paActive = true;
            GD.Print("[puppetanim] driving idle->walk->run via SetLocomotion+Tick");
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
            if (System.IO.File.Exists(tp)) { var img = new Image(); if (ContentProvider.LoadOk(img, tp)) { img.GenerateMipmaps(); mat.AlbedoTexture = ImageTexture.CreateFromImage(img); } }
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
            bool syncLoad = System.Environment.GetEnvironmentVariable("UG_SYNCLOAD") == "1";
            var res = await WorldBuilder.BuildFullWorld(this, _peiPlayable ? WorldMode.Playable : WorldMode.Aerial,
                _mapRoot, _mapPlace, syncLoad: syncLoad, ActiveHoliday());
            // A1 FIX (master 2026-07-20: PEI shelves spawned empty in SP): load the loot tables BEFORE AttachMpLoopback.
            // Under a consuming loopback ContainerNetSync rolls the map containers' loot INSIDE AttachMpLoopback (below),
            // so the tables must be loaded by then -- but the only load site was SpawnMapContainers (@1848), which is
            // gated OFF under consume, so it never ran and every shelf's display digest came back empty.
            if (_peiPlayable) LootTables.Load(_mapRoot + "/Spawns/Items.dat");
            _pdPlayer = res.Player;   // UG_AUTOFIRE terrain-impact verification
            if (_pdPlayer != null && System.Environment.GetEnvironmentVariable("UG_START3P") == "1") _pdPlayer.DriveFP = false;   // start in 3rd person (verify the 3P centre crosshair + the 3P body)
            if (res.HasVehicleAim && !_vHave) { _vAim = res.VehicleAim; _vHave = true; }
            // P6a: the GAME "Drive PEI"/--peidrive path (Playable + a real player, NOT the nav-bake/navpath/zombie
            // offline harnesses, which set _bakeNav) boots the consuming listen-server by default. --objects is Aerial
            // (res.Player == null) so it early-returns regardless. gameDefault=false keeps the harnesses direct.
            AttachMpLoopback(res, gameDefault: _peiPlayable);
            if (res.Ready) _worldReady = true;   // async world fully built (terrain..trees) -> the --shot harness can now capture a loaded frame
            if (_peiPlayable) { string mk = System.IO.Path.GetFileName(_mapRoot).ToLowerInvariant().Replace(" ", ""); MusicPlayer.Get(this)?.PlayLoop(GameAudio.Clip("music", mk + "_loop") != null ? mk + "_loop" : "pei_loop"); }   // retail per-map loop (pei/washington shipped; others fall back to PEI)
            // WEATHER on PEI: BuildFullWorld never attached a WeatherManager, so the `weather` console command did
            // NOTHING in the real game (master 2026-08-29 "no weather manager on pei"). Attach it here on the REAL
            // PEI clock so `weather rain|heavy|clear|lightning` drives the worldspace 3D rain + terrain wetness
            // in-game. Null overlay -- the 3D rain replaced the 2D streaks. UG_WEATHER forces a perpetual state for
            // render-verifying (same knob as the daynight demo).
            if (res.DayNight != null && WeatherManager.Current == null)
            {
                var wm = WeatherManager.Attach(this, null, res.DayNight);
                switch (System.Environment.GetEnvironmentVariable("UG_WEATHER"))
                {
                    case "rain": wm.Sim.SetPerpetual(0); break;
                    case "heavy": wm.Sim.SetPerpetual(1); break;
                    case "lightning": wm.Sim.SetPerpetual(1); wm.Strike(); break;
                }
            }
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

        // genSeed != null -> the map starts as a GENERATED island instead of a flat plain (the menu's Generate
        // Map). Same 3x3 CreateFlat either way, deliberately: the saved heightmap only reloads when its dims
        // match the terrain the open path builds, so a generated map that chose its own size would come back
        // flat the next time you opened it, with nothing about the save looking wrong.
        void BuildEditorNew(string mapName = null, int? genSeed = null, bool autoPlay = false)
        {
            mapName = EditorMaps.Sanitise(mapName) ?? "NewMap";
            _worldBuild = true;
            var terr = Terrain.CreateFlat(3, 3);
            // A NEW MAP GETS A SEA. Nothing used to set this outside the retail-map load path, so a fresh editor
            // map had HasWater at whatever the boot left it -- false. A generated island would then have rendered
            // as a plateau in a void, with nothing about the heightmap itself looking wrong.
            Terrain.HasWater = System.Environment.GetEnvironmentVariable("UG_NOWATER") != "1";
            Terrain.SeaLevelY = 25.6f;   // the default a legacy-water retail map uses; ProcIsland builds its coast to match
            AddChild(terr);
            // BEFORE the camera is placed and before any prop is spawned: generation rewrites every height, and
            // both of those read the ground it produces.
            var genPois = genSeed.HasValue ? terr.GenerateIsland(genSeed.Value) : null;
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
            // A generated island fills a 3072 m map whose ORIGIN IS A CORNER, and that corner is open sea. Open
            // over the first town instead: the playtest spawn is a ray straight down from this camera, so where
            // the editor looks is also where the player lands.
            var camPos = new Vector3(0f, 130f, 190f);
            bool camTop = false;
            float camPitch = -30f;
            if (genPois != null && genPois.Count > 0)
            {
                var focus = genPois[0];
                foreach (var q in genPois) if (q.Kind == ProcIsland.PoiKind.Town) { focus = q; break; }
                // Directly OVER the town centre, because the playtest spawn is a ray straight down from this
                // camera -- the framing offset IS the spawn offset. The default 190 m put the player in an
                // empty field with the town a smudge on the horizon; 55 m landed them face-first against a
                // shopfront. The town's centre cell is a junction, so straight down is a street.
                camPos = ProcIslandSpawn.PosFor(terr, focus.X, focus.Z) + new Vector3(0f, 90f, 0f);
                camPitch = -35f;   // shallow enough that the town spreads out ahead rather than under the lens
                // UG_GENTOP=1: straight down over the same town. A 3/4 view cannot show whether streets JOIN --
                // one tile hides the gap behind the next -- and "the roads connect" is the claim the numbers in
                // the suite are least able to settle, since they recompute the layout with the placing formula.
                if (System.Environment.GetEnvironmentVariable("UG_GENTOP") == "1")
                {
                    camPos = ProcIslandSpawn.PosFor(terr, focus.X, focus.Z) + new Vector3(0f, 230f, 0f);
                    camTop = true;
                }
            }
            var cam = new EditorCamera { Position = camPos, RotationDegrees = new Vector3(camTop ? -90f : camPitch, 0f, 0f) };
            editor.AddChild(cam);
            editor.Setup(mapName, null, cam);
            LootTables.Load(_mapRoot + "/Spawns/Items.dat");   // new maps use PEI's loot tables as the pool (for loot crates)
            var objs = new EditorObjects(editor, this, cam, objectsPreloaded: false); editor.AddChild(objs); editor.Objects = objs;
            // The monuments the generator laid out are only lists until something instantiates them.
            if (genPois != null) ProcIslandSpawn.Spawn(terr, objs);
            var spawns = new EditorSpawns(editor, cam, MapDir(mapName)); editor.AddChild(spawns); editor.Spawns = spawns;   // dir doesn't exist -> starts empty
            var envEd = new EditorEnvironment(editor, dayNight); editor.AddChild(envEd); editor.Environment = envEd;
            var terrainEd = new EditorTerrain(editor, cam, terr); editor.AddChild(terrainEd); editor.TerrainEd = terrainEd;
            var rf = new RoadField { Terr = terr };
            rf.LoadMaterialsOnly(_mapRoot + "/Environment");   // shared road materials so roads can be added on the blank map
            AddChild(rf);
            var roadsEd = new EditorRoads(editor, cam, rf); editor.AddChild(roadsEd); editor.RoadsEd = roadsEd;
            var roadDrawEd = new EditorRoadDraw(editor, cam, rf); editor.AddChild(roadDrawEd); editor.RoadDrawEd = roadDrawEd;   // R = draw, Shift+R = legacy nodes
            var riverEd = new EditorRiver(editor, cam, terr); editor.AddChild(riverEd); editor.RiverEd = riverEd;   // V = carve river (spline tool, sits with the road tools)
            editor.AddChild(new EditorDashboard { Editor = editor, OnExit = ReturnToMenu });
            var play = new EditorPlayMode();   // playtest button -- custom maps get it too, not just PEI
            editor.AddChild(play);
            play.Setup(editor, null, cam);
            // Workshop's per-map Play opens the editor and goes straight in, so the map you play is the
            // map the editor built -- one world-building path, not two that can disagree.
            if (autoPlay) play.CallDeferred(nameof(EditorPlayMode.EnterPlay));
            _worldReady = true;
            GD.Print(genSeed.HasValue
                ? $"[editor] custom map '{mapName}' (GENERATED island, seed {genSeed.Value}) up"
                : $"[editor] custom map '{mapName}' (flat 3x3 base) up");
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
            var res = await WorldBuilder.BuildFullWorld(this, WorldMode.Editor, _mapRoot, objPlace,
                                                        syncLoad: false, ActiveHoliday());
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
            var riverEd = new EditorRiver(editor, cam, res.Terr); editor.AddChild(riverEd); editor.RiverEd = riverEd;   // V = carve river (spline tool, sits with the road tools)
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
            // Static caches are NOT in the node tree, so ReloadCurrentScene does not touch them and the next
            // map gets served resources built during the editor session -- the "every texture is purple/black/
            // white after leaving the editor" report. Clear BEFORE the reload, while this scene still owns them.
            // The warmup that this drops re-runs by itself: the reload re-enters the default boot above, which
            // calls Warmup.Begin. Clear on the way out, warm on the way in.
            ResourceCaches.ClearAll();
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
                                      MapId = System.IO.Path.GetFileName(_mapRoot?.TrimEnd('/')),   // save identity: the map folder name, so a PEI save never loads onto Washington
                                      DayNight = res.DayNight, Resources = res.Resources, Destructibles = res.Destructibles,   // Phase 8 world-state syncs (§3.7) + rubble
                                      Fixtures = res.Fixtures,                              // A3: grid-power fixtures -- ServerPlaced under consume, direct-Attached otherwise
                                      Containers = res.Containers,                          // A1: container manifest -> ContainerNetSync publishes server-owned fixtures
                                      ConsumeDeployables = consume });                      // P6a: true by default on the GAME path
        }

        // --peiplay: the world assembly lives in WorldBuilder.BuildPeiPlayWorld (MP_PLAN §4 Phase 3);
        // this wrapper keeps the capture plumbing (_peiPlayer drives the scripted drop/enter/drive).
        void BuildPeiPlay()
        {
            var res = WorldBuilder.BuildPeiPlayWorld(this, MapDir("PEI"));
            _peiPlayer = res.Player;
            AttachMpLoopback(res, gameDefault: true);   // P6a: --peiplay is a real SP GAME entry -> consuming listen-server by default (--direct opts out)
        }

        // --arenaspawns[=POI]: build the PEI world, pick a POI, generate the 8 arena spawns, drop bright markers + an
        // angled top-down camera -> a --shot debug view so spawn placement (spread / water / buildings) can be eyeballed.
        // Set UG_SHOTTIME ~7 so the world loads first; UG_ARENARADIUS overrides the ring radius.
        async void BuildArenaSpawns(string poiArg)
        {
            // The REAL collidered world (Editor mode = full Objects.dat props + colliders, but NO player/HUD/loot/zombies)
            // so spawns are based on the town's ACTUAL buildings AND overlap-tested against the walls. (master 2026-09-02)
            _worldBuild = true;   // the --shot capture waits for _worldReady (set at the end) -> it fires on a loaded frame
            var res = await WorldBuilder.BuildFullWorld(this, WorldMode.Editor, _mapRoot, _mapPlace, syncLoad: true, ActiveHoliday());
            var terr = res.Terr;
            if (terr == null) { GD.PrintErr("[arena] no PEI terrain (no local map?) -- can't place spawns"); _worldReady = true; return; }
            var spawns = ComputeArenaRing(terr, poiArg, out var centre, out var halfX, out var halfZ, out var poiName, out var inWall);
            if (spawns == null) { _worldReady = true; return; }
            int n = 0;
            foreach (var (pos, yaw) in spawns) { AddArenaMarker(pos, new Color(0.15f, 0.95f, 1f), $"{++n}"); GD.Print($"[arena]   spawn {n}: ({pos.X:0},{pos.Y:0},{pos.Z:0})"); }
            AddArenaMarker(centre, new Color(1f, 0.25f, 0.85f), "C");
            AddArenaBorder(centre, halfX, halfZ, terr);   // the arena play-area boundary (the POI extent), on the ground

            // GUN RAIN: no normal loot -- guns continuously SPAWN + randomly DELETE over time, churning ~Target on the
            // ground (a runtime ArenaGuns node drives it; each gun = a real pickup + a bright orange marker).
            SDG.Unturned.ItemCatalog.RegisterAll();
            AddChild(new ArenaGuns { Terr = terr, Centre = centre, HalfX = halfX, HalfZ = halfZ, InWall = inWall, Target = 40 });

            float span = Mathf.Max(halfX, halfZ);
            var cam = new Camera3D { Current = true, Fov = 60f, Far = 8000f };
            AddChild(cam);
            cam.GlobalPosition = centre + new Vector3(0f, span * 2.7f + 45f, span * 0.22f);   // steep top-down framing the whole border
            cam.LookAt(centre, Vector3.Up);
            _worldReady = true;   // capture can now fire (loaded world + markers in frame)
        }

        // A bright emissive spawn marker: a flat ground disc (reads from top-down) + a tall pillar (3D presence) + a
        // floating billboarded number, so the 8 spawns + the POI centre 'C' are all legible in one debug shot.
        void AddArenaMarker(Vector3 groundPos, Color col, string label)
        {
            var mat = new StandardMaterial3D { AlbedoColor = col, EmissionEnabled = true, Emission = col, EmissionEnergyMultiplier = 1.8f, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
            AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 4f, BottomRadius = 4f, Height = 0.3f }, MaterialOverride = mat, Position = groundPos + Vector3.Up * 0.2f });      // ground disc
            AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.6f, BottomRadius = 0.6f, Height = 12f }, MaterialOverride = mat, Position = groundPos + Vector3.Up * 6f });   // pillar
            AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = 2.2f }, MaterialOverride = mat, Position = groundPos + Vector3.Up * 13.5f });                                        // cap ball
            AddChild(new Label3D { Text = label, FontSize = 128, PixelSize = 0.08f, Modulate = col, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = true, Position = groundPos + Vector3.Up * 17f });
        }

        // The arena play-area boundary = the POI extent box, drawn as a bright emissive fence that FOLLOWS the terrain
        // (posts sampled to ground height along each edge) so the whole rectangle reads even over undulating ground.
        void AddArenaBorder(Vector3 centre, float halfX, float halfZ, Terrain terr)
        {
            var col = new Color(1f, 0.82f, 0.1f);
            var mat = new StandardMaterial3D { AlbedoColor = col, EmissionEnabled = true, Emission = col, EmissionEnergyMultiplier = 1.8f, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            const int segs = 44;
            for (int e = 0; e < 4; e++)
                for (int i = 0; i <= segs; i++)
                {
                    float t = i / (float)segs;
                    float x = e < 2 ? centre.X - halfX + t * halfX * 2f : centre.X + (e == 2 ? -halfX : halfX);
                    float z = e < 2 ? centre.Z + (e == 0 ? -halfZ : halfZ) : centre.Z - halfZ + t * halfZ * 2f;
                    float y = terr != null ? terr.SampleHeight(x, z) : centre.Y;
                    AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(1.1f, 5f, 1.1f) }, MaterialOverride = mat, Position = new Vector3(x, y + 2f, z) });
                }
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
                    var img = ContentProvider.LoadImage(p);
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

            // A ring of bright props around the player so the new frosted-glass backdrop actually READS in the
            // screenshot -- the blur is invisible over a flat void. Demo dressing only, not part of the UI.
            var boxCols = new[] {
                new Color(0.85f, 0.30f, 0.30f), new Color(0.30f, 0.70f, 0.90f), new Color(0.92f, 0.80f, 0.32f),
                new Color(0.40f, 0.80f, 0.45f), new Color(0.92f, 0.52f, 0.25f), new Color(0.68f, 0.42f, 0.85f),
                new Color(0.95f, 0.60f, 0.72f), new Color(0.50f, 0.85f, 0.80f),
            };
            for (int bi = 0; bi < 8; bi++)
            {
                float ang = bi * Mathf.Pi * 2f / 8f;
                float r = 9f + (bi % 3) * 2.5f;
                var box = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(2.2f, 3.0f + (bi % 4), 2.2f) } };
                box.MaterialOverride = new StandardMaterial3D { AlbedoColor = boxCols[bi] };
                box.Position = new Vector3(Mathf.Sin(ang) * r, 1.5f, Mathf.Cos(ang) * r);
                AddChild(box);
            }

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
            if (System.Environment.GetEnvironmentVariable("UG_MAGLOAD") == "1" || System.Environment.GetEnvironmentVariable("UG_MAGVERT") == "1")   // stock an EMPTY mag + a 5.56 stack; InventoryUI auto-starts the fill wheel (headless can't drag-drop)
            {
                SDG.Unturned.ItemCatalog.RegisterAll();
                player.Inventory.tryAddItem(new SDG.Unturned.Item(6, 0));      // empty STANAG magazine (cap 30)
                player.Inventory.tryAddItem(new SDG.Unturned.Item(5004, 20)); // 5.56 FMJ loose rounds (a 20-batch < the 30 cap -> wheel total shows /20, the dragged amount)
            }
            if (System.Environment.GetEnvironmentVariable("UG_MAGUNLOAD") == "1")   // stock a FULL mag; InventoryUI auto-starts the UNLOAD wheel (rounds return to the bag)
            {
                SDG.Unturned.ItemCatalog.RegisterAll();
                player.Inventory.tryAddItem(new SDG.Unturned.Item(6, 30));   // full STANAG magazine (30 rounds)
            }
            SDG.Unturned.ItemCatalog.RegisterAll();
            player.Inventory.wearBackpack(new SDG.Unturned.Item(253));   // Alicepack (8x7) -> the widest storage grid, so master can eyeball the wide layout next to the paperdoll
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
                var img = ContentProvider.LoadImage(texPath);
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
            if (System.IO.File.Exists(tex)) { var img = ContentProvider.LoadImage(tex); if (img != null) mat.AlbedoTexture = ImageTexture.CreateFromImage(img); }
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
            if (System.IO.File.Exists(tex)) { var img = ContentProvider.LoadImage(tex); if (img != null) mat.AlbedoTexture = ImageTexture.CreateFromImage(img); }

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
                    var img = ContentProvider.LoadImage(texPath);
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
                // UG_STRIKE_AT=<s,s,s>: schedule strikes at those times (for the --write-movie storm demo -> flashes + thunder mid-clip)
                var _saEnv = System.Environment.GetEnvironmentVariable("UG_STRIKE_AT");
                if (!string.IsNullOrEmpty(_saEnv))
                {
                    var _times = new System.Collections.Generic.List<float>();
                    foreach (var _p in _saEnv.Split(',')) if (float.TryParse(_p.Trim(), out var _v)) _times.Add(_v);
                    if (_times.Count > 0) { _stormWm = wm; _stormStrikes = _times.ToArray(); }
                }
                GD.Print($"[WEATHERSHOT] mode={wmode} stage={wm.Sim.Stage} blend={wm.Sim.BlendAlpha:0.00} active={wm.Sim.Active?.Name ?? "none"}");
            }

            // when storming, the ground + boxes use the wet_surface shader (darken + raindrop splashes) for the full storm demo
            ShaderMaterial StormWet(Color dry, float rough)
            {
                if (wmode == null) return null;
                var m = new ShaderMaterial { Shader = GD.Load<Shader>("res://content/wet_surface.gdshader") };
                m.SetShaderParameter("dry_albedo", dry); m.SetShaderParameter("dry_roughness", rough);
                return m;
            }
            var ground = new StaticBody3D { CollisionLayer = 1 << 0 };
            ground.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
            var gmesh = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(80, 80) } };
            gmesh.MaterialOverride = (Material)StormWet(new Color(0.20f, 0.22f, 0.25f), 0.7f) ?? new StandardMaterial3D { AlbedoColor = new Color(0.32f, 0.36f, 0.30f) };
            ground.AddChild(gmesh);
            AddChild(ground);

            for (int i = 0; i < 5; i++)   // boxes to catch the light + cast shadows
            {
                var b = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(1f, 1.5f, 1f) } };
                b.MaterialOverride = (Material)StormWet(new Color(0.42f, 0.40f, 0.42f), 0.6f) ?? new StandardMaterial3D { AlbedoColor = new Color(0.62f, 0.56f, 0.5f) };
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
        // The arena play area, computed once and shared. The --arenaspawns debug render exists so the ring can be
        // eyeballed BEFORE a server uses it, which is only worth anything if the server uses this same code -- two
        // copies of the geometry would drift and only one of them is ever looked at.
        // Returns null (with the reason logged) when the world cannot support an arena.
        System.Collections.Generic.List<(Vector3 Pos, float Yaw)> ComputeArenaRing(
            Terrain terr, string poiArg, out Vector3 centre, out float halfX, out float halfZ, out string poiName,
            out System.Func<Vector3, bool> inWallOut)
        {
            centre = Vector3.Zero; halfX = 0f; halfZ = 0f; poiName = null; inWallOut = _ => false;
            if (terr == null) { GD.PrintErr("[arena] no PEI terrain (no local map?) -- can't place spawns"); return null; }
            var pois = MapNodes.Locations;
            if (pois.Count == 0) { GD.PrintErr("[arena] no POIs in nodes.tsv"); return null; }
            int idx = 0;
            if (!string.IsNullOrEmpty(poiArg))
            {
                string want = poiArg.Replace(" ", "").ToLowerInvariant();
                int f = pois.FindIndex(p => p.Name.Replace(" ", "").ToLowerInvariant().Contains(want));
                if (f >= 0) idx = f;
            }
            else { int f = pois.FindIndex(p => p.Name.Contains("Charlottetown")); if (f >= 0) idx = f; }   // default: the big central town
            var poi = pois[idx]; poiName = poi.Name;
            Vector3 node = new Vector3(poi.Pos.X, terr.SampleHeight(poi.Pos.X, poi.Pos.Z), poi.Pos.Z);

            // the POI's ACTUAL extent = the bounding box of its real buildings clustered near the node point.
            var buildings = new System.Collections.Generic.List<Node3D>();
            foreach (var b in GetTree().GetNodesInGroup(ColliderBudget.Group))
                if (b is StaticBody3D sb && (sb.CollisionLayer & WorldLayers.World) != 0) buildings.Add(sb);
            float link = ArenaMode.LinkDist;
            { var re = System.Environment.GetEnvironmentVariable("UG_ARENALINK"); if (re != null && float.TryParse(re, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rr)) link = rr; }
            ArenaMode.PoiBounds(node, buildings, link, out centre, out halfX, out halfZ, out int near);

            // in-wall test: a standing box at the candidate overlapping a solid structure -> rejected.
            var space = GetViewport()?.World3D?.DirectSpaceState;
            if (space == null) GD.PrintErr("[arena] no physics space -- spawns are NOT wall-rejected this run");
            var probe = new BoxShape3D { Size = new Vector3(1.0f, 1.8f, 1.0f) };
            System.Func<Vector3, bool> inWall = pos =>
            {
                if (space == null) return false;
                var q = new PhysicsShapeQueryParameters3D
                {
                    Shape = probe, Transform = new Transform3D(Basis.Identity, pos + Vector3.Up * 0.9f),
                    CollisionMask = WorldLayers.World, CollideWithBodies = true, CollideWithAreas = false,
                };
                return space.IntersectShape(q, 1).Count > 0;
            };

            inWallOut = inWall;   // the caller reuses the SAME probe for gun drops -- a second one would disagree
            var ring = ArenaMode.GenerateSpawns(centre, halfX, halfZ, terr, inWall, ArenaMode.SpawnCount);
            GD.Print($"[arena] POI '{poi.Name}': connected town = {near} buildings -> extent ~{halfX * 2:0}x{halfZ * 2:0}m, {ring.Count}/{ArenaMode.SpawnCount} spawns (land + clear of walls)");
            return ring;
        }

        async void BuildDedicated()
        {
            // async void swallows exceptions silently -- a bad map path used to leave the server dead with no
            // log and no bound socket. Surface anything that goes wrong + hard-exit so systemd restarts cleanly.
            try
            {
                string holiday = ActiveHoliday();   // P3: ONE decision -- the world builds with it AND it rides the Accept (joiners build the same collision set)
                var res = await WorldBuilder.BuildFullWorld(this, WorldMode.Dedicated, _mapRoot, _mapPlace,
                    syncLoad: true, activeHoliday: holiday);

                // UG_ARENA=1 turns this into an arena server: players spawn on the POI ring instead of the map's
                // Players.dat points, and the match holds until UG_ARENAMIN (default 2) are connected. Off by
                // default, so every existing dedicated boot and every test harness is byte-identical.
                bool arena = System.Environment.GetEnvironmentVariable("UG_ARENA") == "1";
                System.Collections.Generic.List<(Vector3 Pos, float Yaw)> arenaRing = null;
                int arenaMin = 2;
                Vector3 arenaCentre = Vector3.Zero; float arenaHalfX = 0f, arenaHalfZ = 0f;
                System.Func<Vector3, bool> arenaInWall = _ => false;
                { var e = System.Environment.GetEnvironmentVariable("UG_ARENAMIN"); if (e != null && int.TryParse(e, out var m) && m > 0) arenaMin = m; }
                if (arena)
                {
                    arenaRing = ComputeArenaRing(res.Terr, System.Environment.GetEnvironmentVariable("UG_ARENAPOI"),
                                                 out arenaCentre, out arenaHalfX, out arenaHalfZ, out var arenaPoi, out arenaInWall);
                    if (arenaRing == null || arenaRing.Count == 0)
                        GD.PrintErr("[ARENA] no spawn ring generated -- falling back to the map's Players.dat spawns");
                    else
                        GD.Print($"[ARENA] arena server on '{arenaPoi}': {arenaRing.Count} spawns, holding until {arenaMin} players");
                }

                AddChild(new DedicatedServer { Port = PortEnv(), Driver = res.Sim, Terr = res.Terr,
                    Arena = arena, ArenaSpawns = arenaRing, ArenaMinPlayers = arenaMin,   // Terr: server grenades bounce on real terrain height (Phase 5)
                    DayNight = res.DayNight, Resources = res.Resources, Destructibles = res.Destructibles, MapRoot = _mapRoot,   // Phase 8: tick-derived clock + resource bitmap + rubble + nav-pocket relevancy cells (§3.7/§2.6)
                    Deadzones = res.Deadzones,                                                       // SP/MP unify: the contaminated volumes get copied into the server's own hazard step
                    Fixtures = res.Fixtures,                                                         // A3: server-place the Circuit_0 grid-power sources into the deployable graph (mains OFF)
                    Containers = res.Containers,                                                     // A1: container manifest -> ContainerNetSync publishes server-owned fixtures
                    RemoteAvatars = true,                                                            // C2: remote peers get real avatar bodies (real spawns/collision/jump) on this world
                    ActiveHoliday = holiday,                                                         // P3 (wire v6): joiners build THIS holiday's props/colliders
                    AllowCheats = System.Environment.GetEnvironmentVariable("UG_DEDICATED_NOCHEATS") != "1" });   // test server: give/xp/skill console cheats ON (useful for testing); set UG_DEDICATED_NOCHEATS=1 to lock them off, no code change (review C1 toggle)
                // GUN RAIN is the arena's loot model -- no normal loot, guns churn on the ground. NOTE: no
                // ItemCatalog.RegisterAll() here, unlike the --arenaspawns debug path. WorldBuilder already
                // registered the catalog during this build (WorldBuilder.cs:1617/1929) and RegisterAll CLEARS the
                // asset table first, so re-running it on a live server would blank the assets out from under it.
                if (arena && arenaRing != null && arenaRing.Count > 0)
                    AddChild(new ArenaGuns { Terr = res.Terr, Centre = arenaCentre, HalfX = arenaHalfX, HalfZ = arenaHalfZ, InWall = arenaInWall, Target = 40 });

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
                    syncLoad: true, activeHoliday: ActiveHoliday());
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
                    syncLoad: false, activeHoliday: ActiveHoliday());
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
                                                      PlayerName = PlayerProfile.Name,   // the HANDSHAKE name (what others see until SetProfile lands, and if it never does) -- was the field default "player" for every real joiner
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

        // UG_VERTEXLIGHT=1: apply vertex shading at boot, so the look can be captured in a render rather than
        // only toggled at runtime through the console. The perf question needs real hardware (this box is
        // software-rasterised) but the LOOK does not -- lavapipe draws the right image, just slowly.
        int _vertexLightQuiet = -1;   // -1 = not started; counts consecutive passes that changed nothing

        public override void _Process(double delta) => HubProcess(delta);   // forwarder for direct callers; the engine's callback is off (SetProcess(false) in _Ready) -- TickHub ticks HubProcess
        public void HubProcess(double delta)
        {
            if (_bakeHullsFrames >= 0 && ++_bakeHullsFrames > 8) { GD.Print("[bakehulls] done"); GetTree().Quit(); return; }
            if (_orbitCam != null && IsInstanceValid(_orbitCam)) { _orbitAngle += (float)delta * 0.7f; _orbitCam.Position = _orbitCenter + new Vector3(Mathf.Cos(_orbitAngle) * _orbitR, _orbitR * 0.42f, Mathf.Sin(_orbitAngle) * _orbitR); _orbitCam.LookAt(_orbitCenter, Vector3.Up); }   // UG_PROPSPIN: 360 turntable orbit for the prop-showcase movie
            if (_zflowMode) { _zflowT += delta; UpdateZflowDots(); if (_zflowT >= 40.0) ZflowReport(); return; }   // zombie phase-2 verify owns the frame
            if (_zhMode) { _zhT += delta; if (_zhT >= 6.0) ZhuntReport(); return; }                               // zombie phase-3 verify owns the frame
            if (_zkMode) { _zkT += delta; _zkFrame++; if (_zkFrame > 60 && _zkFrame % 15 == 0) _zkPlayer?.Fire(); if (_zkT >= 14.0) ZkillReport(); return; }   // phase-3b: pace shots so recoil recovers between them
            if (_zsMode) { _zsT += delta; if (!_zsFired && _zsT >= 3.0) { SoundBus.Emit(GetTree(), _zsSound, SoundBus.Gunshot); _zsFired = true; GD.Print("[zsound] GUNSHOT emitted at the far point"); } if (_zsT >= 13.0) ZsoundReport(); return; }   // phase-4: fire the lure at t=3s
            if (_zfMode) { _zfT += delta; if (_zfz != null) _zfz.DesiredVel = new Vector2(1.3f, 0f); if (_zfT >= 5.0) { GD.Print("[zface] done"); GetTree().Quit(); } return; }   // facing/gait diagnostic: DesiredVel = world +X at the shamble speed
            if (_zpMode) { _zpT += delta; _zpTarget = new Vector3(11f, 0f, Mathf.Sin((float)_zpT * 0.4f) * 7f); if (_zpMarker != null) _zpMarker.Position = _zpTarget + Vector3.Up * 0.9f; if (_zpf != null) _zpf.DebugAnchor = _zpTarget; if (_zpT >= _zpNextEmit) { _zpNextEmit += 2.0; SoundBus.Emit(GetTree(), _zpTarget, SoundBus.Gunshot); } if (_zpT >= 25.0 && !_zpReported) { _zpReported = true; ZpathReport(); } if (_zpT >= 26.0) { GD.Print("[zpath] done"); GetTree().Quit(); } return; }   // MOVING target (a real player moves) -> the field keeps rebuilding so no stable corner-trap can hold
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
            if (_stormWm != null && _stormStrikes != null && _stormStrikeIdx < _stormStrikes.Length)   // --daynight storm demo: fire each UG_STRIKE_AT strike at its time
            {
                _stormT += delta;
                if (_stormT >= _stormStrikes[_stormStrikeIdx]) { _stormWm.Strike(); GD.Print($"[stormdemo] strike {_stormStrikeIdx} at t={_stormT:0.00}s"); _stormStrikeIdx++; }
            }
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
            if (_menuShotDir != null && _menuShotMenu != null)   // step the menu camera through its 5 anchors, capture each
            {
                // switch to anchor i, then capture 15 frames later.
                //
                // The increment used to run BEFORE these comparisons, so _frame was never 0 here and
                // switchAt[0] == 0 NEVER MATCHED -- ShowTab(0) has never once fired. That was invisible while
                // MainMenu happened to start the camera already sitting on the Title anchor: the capture
                // agreed with the anchor it was never told to select. It stops being invisible the moment the
                // menu opens anywhere else (it now opens on initialCamera, as retail does), and menu_00 then
                // records the opening pose instead of Title. A harness whose first step silently does nothing
                // is worse than one that fails, because its output still looks like an answer.
                // UG_MENUCLIP=1: record a GLIDE WALKTHROUGH instead of stills -- drive _forceTab WITHOUT the snap so
                // _Process lerps between anchors (opening initialCamera->Title drift, then a glide+hold on each submenu).
                // Pair with --write-movie; no stills taken. (ShowTab SNAPS -- deliberately, for exact-anchor goldens --
                // so a big UG_MENUSHOT_STEP only spaces snapped stills further apart; it does NOT glide.)
                if (System.Environment.GetEnvironmentVariable("UG_MENUCLIP") == "1")
                {
                    int[] glideAt = { 0, 140, 235, 330, 425 };   // Title (opening pan gets extra) then Play/Survivors/Config/Workshop
                    if (_menuShotIdx < glideAt.Length && _frame == glideAt[_menuShotIdx]) { _menuShotMenu.GlideTab(_menuShotIdx); _menuShotIdx++; }
                    if (_frame >= 525) GetTree().Quit();
                    _frame++;
                    return;
                }
                // UG_MENUSHOT_STEP frames per anchor (default 20). ShowTab SNAPS to each anchor, then a still is captured.
                int step = int.TryParse(System.Environment.GetEnvironmentVariable("UG_MENUSHOT_STEP"), out var _mst) && _mst > 5 ? _mst : 20;
                int[] switchAt = { 0, step, 2 * step, 3 * step, 4 * step };
                int[] shotAt = { step - 5, 2 * step - 5, 3 * step - 5, 4 * step - 5, 5 * step - 5 };
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
                _frame++;
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
                double physMs = Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000.0;
                double procMs = Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0;
                GD.Print($"[perf] fps={Engine.GetFramesPerSecond()} physicsMs={physMs:0.0} processMs={procMs:0.0} draws={Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame)}");
            }
            if (_fireTest && _ftPlayer != null) { _ftFrame++; if (System.Environment.GetEnvironmentVariable("UG_LEAN") is string _ln && _ln.Length > 0 && _ftFrame >= 8) _ftPlayer.ScriptedLean = int.Parse(_ln);   /* UG_LEAN=1 lean left / -1 right: verify the 1P viewmodel rolls with the lean */ if (System.Environment.GetEnvironmentVariable("UG_MOVE") == "1" && _ftFrame >= 8) _ftPlayer.ScriptedInput = new UnityEngine.Vector2(0f, 1f);   /* UG_MOVE=1: walk forward -> verify the viewmodel movement-sway tilt */ if (System.Environment.GetEnvironmentVariable("UG_ADS") == "1") { if (_ftFrame >= 40) _ftPlayer.ForceAim(true); } else if (System.Environment.GetEnvironmentVariable("UG_TRACERANGLE") == "1") { if (_ftFrame >= 45 && _ftFrame % 10 == 0) _ftPlayer.DebugFireAngled(-28f); } else if (_ftFrame >= 60 && _ftFrame % 15 == 0) _ftPlayer.Fire(); }   // own counter; UG_ADS: hold ADS; UG_TRACERANGLE: fire tracers 38deg across the view so the stretched streak is seen side-on
            if (_paActive && _paRig != null && IsInstanceValid(_paRig))
            {
                _paT += (float)delta;
                if (_paHit)   // hitbox viz: hold the chosen stance + lean under the zone overlay
                {
                    var hst = _paStance == 2 ? SDG.Unturned.EPlayerStance.CROUCH : (_paStance == 3 ? SDG.Unturned.EPlayerStance.PRONE : SDG.Unturned.EPlayerStance.STAND);
                    _paRig.LeanDeg = _paLean;
                    _paRig.SetLocomotion(0f, hst);
                    _paRig.Tick(delta);
                    if (!_paMeasured && _paT > 1.2f && _paRig.Skeleton != null && _paRig.Body?.Mesh is ArrayMesh am)
                    {
                        _paMeasured = true;
                        var sk = _paRig.Skeleton;
                        var arr = am.SurfaceGetArrays(0);
                        var verts = (Vector3[])arr[(int)Mesh.ArrayType.Vertex];
                        var bones = (int[])arr[(int)Mesh.ArrayType.Bones];   // 4 SKIN-SLOT indices per vert (NOT skeleton bones -- tinyclaw)
                        var skin = _paRig.Body.Skin;
                        int bc = skin.GetBindCount();
                        var parts = new System.Collections.Generic.Dictionary<string, Aabb>();
                        for (int i = 0; i < verts.Length; i++)
                        {
                            int slot = bones[i * 4];
                            if (slot < 0 || slot >= bc) continue;
                            int b = skin.GetBindBone(slot); if (b < 0) b = sk.FindBone(skin.GetBindName(slot));   // slot -> skeleton bone
                            if (b < 0) continue;
                            var p = sk.GetBoneGlobalPose(b) * skin.GetBindPose(slot) * verts[i];   // correct GPU skinning: pose * bindpose * v
                            string bn = sk.GetBoneName(b);
                            // ARMS are their own region now (strawberry 2026-09-02: "arms into their own region, same mult as legs"),
                            // so TORSO is the Spine core alone -- the wide box that used to swallow the arms is what made the fit wrong.
                            string grp = bn.Contains("Skull") ? "HEAD"
                                : ((bn.Contains("Leg") || bn.Contains("Foot") || bn.Contains("Hip") || bn.Contains("Knee")) ? "LEGS"
                                : ((bn.Contains("Shoulder") || bn.Contains("Arm") || bn.Contains("Hand") || bn.Contains("Hook")) ? (bn.StartsWith("Left") ? "ARM_L" : "ARM_R") : "TORSO"));
                            parts[grp] = parts.TryGetValue(grp, out var ab) ? ab.Expand(p) : new Aabb(p, Vector3.Zero);
                        }
                        foreach (var g in new[] { "HEAD", "TORSO", "ARM_L", "ARM_R", "LEGS" }) if (parts.TryGetValue(g, out var a)) GD.Print($"[mesh] st={_paStance} lean={_paLean:0} {g} Y=[{a.Position.Y:0.00}..{a.End.Y:0.00}] X=[{a.Position.X:0.00}..{a.End.X:0.00}] Z=[{a.Position.Z:0.00}..{a.End.Z:0.00}]");
                    }
                    return;
                }
                if (_paGun)   // shouldered hold (0-1.6) -> ADS (1.6-3.2) -> lean right (3.2-4.8) -> lean left (4.8+); same AimBlend/LeanDeg the local 3p body uses
                {
                    _paRig.AimBlend = (_paT >= 1.6f && _paT < 3.2f) ? 1f : 0f;
                    _paRig.LeanDeg = _paT < 3.2f ? 0f : (_paT < 4.8f ? 22f : -22f);
                    _paRig.SetLocomotion(0f, SDG.Unturned.EPlayerStance.STAND);
                    _paRig.Tick(delta);
                    return;
                }
                // the full locomotion range the puppet can drive: idle -> walk -> run -> crouch-walk -> prone-crawl,
                // all via the SAME SetLocomotion(speed, stance) + Tick the local 3p body + RemotePlayers use.
                float spd; SDG.Unturned.EPlayerStance st;
                if (_paT < 1.6f) { spd = 0f; st = SDG.Unturned.EPlayerStance.STAND; }       // idle
                else if (_paT < 3.2f) { spd = 1.6f; st = SDG.Unturned.EPlayerStance.STAND; } // walk
                else if (_paT < 4.8f) { spd = 4.8f; st = SDG.Unturned.EPlayerStance.STAND; } // run
                else if (_paT < 6.4f) { spd = 1.2f; st = SDG.Unturned.EPlayerStance.CROUCH; }// crouch-walk
                else { spd = 0.8f; st = SDG.Unturned.EPlayerStance.PRONE; }                  // prone crawl
                _paRig.SetLocomotion(spd, st);
                _paRig.Tick(delta);
            }
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
                        // UG_CAMYAW/UG_CAMPITCH orbit this shot too. ApplyCamOrbit was only wired to the rig
                        // path, so --vehicle silently ignored it and a 12-angle sweep rendered 12 identical
                        // frames -- which looks exactly like "the geometry is symmetric".
                        // UG_LAMPS=on : switch the head/taillights on for the shot.
                        // UG_LAMPBREAK=headlight_l,taillight_r : shoot those lamps out first, so a render can show
                        // a dead lamp NEXT TO a lit one -- the only way to see that a break is per side and that a
                        // shot-out lens stays dark when the lights come on.
                        if (!_lampFxDone && _veh != null)   // once, not every frame: this sits in the per-frame camera block
                        {
                            _lampFxDone = true;
                            var lb = System.Environment.GetEnvironmentVariable("UG_LAMPBREAK");
                            if (!string.IsNullOrEmpty(lb))
                                foreach (var want in lb.Split(','))
                                    for (int li = 0; li < _veh.LampCount; li++)
                                        if (_veh.LampLabel(li) == want.Trim()) _veh.BreakLamp(li);
                            if (System.Environment.GetEnvironmentVariable("UG_LAMPS") == "on") _veh.SetLightsForTest(true);
                            var tp = System.Environment.GetEnvironmentVariable("UG_TIREPOP");   // e.g. "0,3"
                            if (!string.IsNullOrEmpty(tp))
                                foreach (var w in tp.Split(','))
                                    if (int.TryParse(w.Trim(), out var wi)) _veh.PopTire(wi);
                        }
                        ApplyCamOrbit(_vehCam, vt.Origin + Vector3.Up * 1.0f);
                    }
                }
                if (_driveTest && _dtPlayer != null)
                {
                    if (_frame == 25 && !_nade && !_grassTest) _dtPlayer.EnterNearestVehicle();             // hop in (skip for --nade: keep the jeep parked to grenade it; grasstest: keep it parked on the lawn)
                    if (_frame >= 30 && !_grassTest) _dtPlayer.ScriptedDrive = _swarm ? Vector2.Zero : _drivethru ? new Vector2(0f, 1f) : new Vector2(_frame > 130 ? 0.5f : 0f, 1f);  // swarm: sit still; drivethru: straight full-throttle; else forward then curve
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
                    // UG_SHOWHULL=1: draw the vehicle's ACTUAL collision shapes over it, from the same eight
                    // angles. Built here rather than at spawn because the convex decomposition runs on the
                    // vehicle's _Ready, so at build time there is nothing yet to draw. Wireframe, so the body
                    // stays readable underneath and any hull standing PROUD of the model is the visible part.
                    if (_glassShotDir != null && !_hullOverlayDone
                        && System.Environment.GetEnvironmentVariable("UG_SHOWHULL") == "1" && _veh != null)
                    {
                        _hullOverlayDone = true;
                        var hullMat = new StandardMaterial3D { AlbedoColor = new Color(0.1f, 1f, 0.2f),
                            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
                        int drawn = 0;
                        void DrawShapes(Node n)
                        {
                            foreach (var c in n.GetChildren())
                            {
                                if (c is CollisionShape3D cs && cs.Shape != null)
                                {
                                    // UG_HULLKIND=convex draws ONLY the decomposed hulls, =box only the
                                    // fitted boxes. Rendered against the body silhouette that separates
                                    // "the model's own shape, captured" from "the brick bolted around it".
                                    string kind = System.Environment.GetEnvironmentVariable("UG_HULLKIND");
                                    bool isConvex = cs.Shape is ConvexPolygonShape3D;
                                    if (kind == "convex" && !isConvex) continue;
                                    if (kind == "box" && isConvex) continue;
                                    var dm = cs.Shape.GetDebugMesh();
                                    if (dm != null)
                                    {
                                        var wire = new MeshInstance3D { Mesh = dm, MaterialOverride = hullMat };
                                        _veh.AddChild(wire);
                                        wire.GlobalTransform = cs.GlobalTransform;
                                        drawn++;
                                        var a = cs.Shape.GetDebugMesh().GetAabb();
                                        GD.Print($"[hull] {cs.GetParent().Name}/{cs.Name} {cs.Shape.GetType().Name} " +
                                                 $"pos=({cs.Position.X,6:0.00},{cs.Position.Y,6:0.00},{cs.Position.Z,7:0.00}) size=({a.Size.X,5:0.00},{a.Size.Y,5:0.00},{a.Size.Z,5:0.00})");
                                    }
                                }
                                if (c is not MeshInstance3D) DrawShapes(c);
                            }
                        }
                        DrawShapes(_veh);
                        // UG_HULLONLY=1 hides the model so the frame is the HULL's silhouette alone.
                        // Rendered against the body-only pass from the identical camera, the pixels that
                        // are hull-and-not-body are exactly where the hitbox stands proud of the car --
                        // a number, rather than me judging an overlay by eye.
                        if (System.Environment.GetEnvironmentVariable("UG_HULLONLY") == "1")
                            foreach (var mi in _bodyMeshes) if (GodotObject.IsInstanceValid(mi)) mi.Visible = false;
                        GD.Print($"[hull] {drawn} collision shapes drawn");
                    }
                    string p = $"{_rigDir}/rig_{_rigShot:D2}.png";
                    im.SavePng(p);
                    GD.Print($"[RIG] saved {p} (frame {_frame})");
                    _rigShot++;
                    if (_glassShotDir != null) PlaceGlassCam(_rigShot);   // move to the NEXT yaw for the next capture
                    if (_rigShot >= _rigCaptureFrames.Length) GetTree().Quit();
                }
                return;
            }
            if (_worldReady && !_treeChecked && System.Environment.GetEnvironmentVariable("UG_TREECHECK") == "1" && ++_treeCheckFrame > 15) { _treeChecked = true; DoTreeCheck(); }
            // --landmarkshot camera tour: independent of the single-shot _shotPath harness below. One point per settle
            // window: position the cam, wait ShotSettleFrames for VisibilityRange to recompute at the new pos, capture.
            if (_lmShotDir != null)
            {
                if (!_worldReady) return;
                if (_lmCam == null) { _lmCam = new Camera3D { Fov = 55f, Far = 3000f, Current = true }; AddChild(_lmCam); }
                if (_lmIdx >= _lmTour.Length) { GD.Print("[LMSHOT] done"); GetTree().Quit(); return; }
                var lt = _lmTour[_lmIdx];
                if (_lmFrame == 0) { _lmCam.GlobalPosition = lt.Eye; _lmCam.LookAt(lt.Look, Vector3.Up); }
                if (++_lmFrame < ShotSettleFrames) return;
                var lmimg = GetViewport().GetTexture()?.GetImage();
                if (lmimg == null) { GD.PrintErr("[LMSHOT] null image -- need --rendering-driver vulkan"); GetTree().Quit(1); return; }
                lmimg.SavePng($"{_lmShotDir}/{_lmIdx:D2}_{lt.Tag}.png");
                GD.Print($"[LMSHOT] {lt.Tag} dist~{lt.Eye.DistanceTo(lt.Look):0}m draws={RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalDrawCallsInFrame)} objs={RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalObjectsInFrame)}");
                _lmIdx++; _lmFrame = 0;
                return;
            }
            TickBootCommand(delta);
            if (_shotPath == null) return;
            float _shotTimeTarget = 0f; { var _ste = System.Environment.GetEnvironmentVariable("UG_SHOTTIME"); if (!string.IsNullOrEmpty(_ste)) float.TryParse(_ste, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _shotTimeTarget); }
            if (_shotTimeTarget > 0f) { _shotElapsed += (float)delta; if (_shotElapsed < _shotTimeTarget) return; }   // UG_SHOTTIME: capture at an ELAPSED-TIME target (real-time frame counts drift off fixed-fps)
            else if (_peiPlay) { if (_peiFrame < 160) return; }   // peiplay: drop(~25f)+enter(50f)+drive(55f+)
            else if (_itemTest) { if (++_frame < 90) return; }   // itemtest: let the dropped items FALL + settle onto the plane before the shot
            else if (_driveTest) { if (++_frame < 120) return; }   // drivetest: let the car spawn+enter+drive (+ --demo damage->explosion) play out before the shot
            else if (_fireTest) { if (System.Environment.GetEnvironmentVariable("UG_ADS") == "1") { if (_ftFrame < 70) return; } else if (_ftPlayer == null || _ftPlayer.Ammo > 20 || _ftFrame < 75) return; }   // firetest: capture once ~10 shots fired (high-cap: Ammo<=20); the _ftFrame>=75 floor lets a low-cap gun (launcher = 1 rocket at frame 60) actually fire + impact before the quit. UG_ADS: capture the settled aim frame (70) instead
            else if (_worldBuild) { if (!_worldReady || ++_frame < ShotSettleFrames) return; }   // objects/peidrive: WAIT for the async world (terrain..trees) to finish + settle before the shot
            else if (System.Environment.GetEnvironmentVariable("UG_DEPLOYDMG") != null) { if (++_frame < 45) return; }   // deploytest damage: let smoke/fire particles accumulate before the shot
            else if (System.Environment.GetEnvironmentVariable("UG_WIREWRECK") == "1") { if (++_frame < 20) return; }   // shatter: catch the debris collapsing toward the ground
            else if (System.Environment.GetEnvironmentVariable("UG_WIRETEST") == "1") { if (++_frame < 50) return; }   // wire test: let the lamp warmup envelope settle (past the flicker ramp) before capturing steady state
            else if (++_frame < 6) return; // let the renderer settle
            if (_spotDbg != null && IsInstanceValid(_spotDbg)) GD.Print($"[LAMPDBG] consumerPowered={_spotDbg.DebugConsumerPowered} lampsLit={_spotDbg.DebugLampsLit}");   // plain UG_WIRETEST render: a wired+powered spotlight's lamps must be on
            // Draw calls + primitives at the capture frame. Frame MILLISECONDS on a software rasteriser say
            // nothing about a real GPU, but what the culler admitted into the frame is hardware-independent --
            // so this is the number to compare when changing draw distances, not fps.
            NodeCensus();
            GD.Print($"[lodperf] drawcalls {RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalDrawCallsInFrame)}" +
                     $" | primitives {RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalPrimitivesInFrame)}" +
                     $" | objects {RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalObjectsInFrame)}");
            var img = GetViewport().GetTexture().GetImage();
            if (img == null) { GD.PrintErr("[SHOT] null image -- run with a rendering driver (e.g. --rendering-driver vulkan), NOT --headless"); GetTree().Quit(1); return; }
            img.SavePng(_shotPath);
            GD.Print($"[SHOT] saved {_shotPath} ({img.GetWidth()}x{img.GetHeight()})");
            GetTree().Quit();
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
