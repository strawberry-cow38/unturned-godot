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
        Vector3 _vAim; bool _vHave;   // first real (Police/Fire/Ambulance) vehicle, for the demo cam
        bool _noZombies;   // --nozombies: a quiet test environment (skip the horde spawner)
        string _mapRoot = @"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Maps\PEI";   // --map=NAME switches the whole map (terrain + objects + spawns)
        string _mapPlace = "placements.txt";   // per-map baked object placements in content/objects/ (non-PEI = placements_<key>.txt)
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
            string catalog = null, shot = null, picks = null, gun = null, rig = null, anim = "Walk", vm = null, bakeIcon = null, veh = null, drivetest = null, proptest = null, animrig = null, rottest = null, itemtest = null, navShot = null, croptest = null;
            bool skillsui = false;
            bool play = false, demo = false, netdemo = false, server = false, client = false, smoke = false, hurtdemo = false, invdemo = false, invsel = false, invequip = false, invdrop = false, invloot = false, invcrate = false, daynight = false, buildmode = false, meleedemo = false, falldemo = false, pronetest = false, brokentest = false, grenadetest = false, firetest = false, supp = false, terrain = false, peiplay = false, objects = false, peidrive = false, craftui = false, bakenav = false, navPathTest = false, zombieTest = false, hearTest = false, armorTest = false, farmTest = false;
            foreach (var arg in OS.GetCmdlineUserArgs())
            {
                if (arg.StartsWith("--catalog=")) catalog = arg["--catalog=".Length..];
                else if (arg.StartsWith("--shot=")) shot = arg["--shot=".Length..];
                else if (arg.StartsWith("--navshot=")) navShot = arg["--navshot=".Length..];   // verify screenshot: navmesh floor overlay + zombie vision cones, synchronous world, aerial over a pocket
                else if (arg == "--bakenav") bakenav = true;   // offline TOOL: sync-load the FULL world + bake all 19 nav pockets -> save the .res files (commit them; the game only LOADS, never gens)
                else if (arg == "--navpathtest") navPathTest = true;   // OFFLINE verify: sync world -> query the navmesh -> log whether zombie paths ROUTE AROUND buildings (not through)
                else if (arg == "--zombietest") zombieTest = true;   // OFFLINE verify: sync world -> bucket Animals.dat into pockets -> check planned spawns land ON the baked navmesh
                else if (arg == "--heartest") hearTest = true;   // OFFLINE verify: Phase 3 hearing -> a zombie picks the LOUDEST+CLOSEST sound, ignores out-of-range/too-quiet
                else if (arg == "--armortest") armorTest = true;   // OFFLINE verify: worn clothing's whole-body fall + explosion armor aggregates as a PRODUCT
                else if (arg == "--farmtest") farmTest = true;   // OFFLINE verify: a planted crop grows over Growth secs then harvest yields Grow
                else if (arg.StartsWith("--proptest=")) proptest = arg["--proptest=".Length..];   // spawn ONE named prop at identity + RGB axes -> diagnose mirror/orientation/material
                else if (arg.StartsWith("--croptest=")) croptest = arg["--croptest=".Length..];   // spawn a farm crop (young + grown) on a ground plane -> validate mesh/tex/orientation (UG_CROPROT tunes rot)
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
                else if (arg == "--client") client = true;
                else if (arg.StartsWith("--connect=")) { client = true; _connectHost = arg["--connect=".Length..]; }   // join a dedicated server by IP
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
                    _mapRoot = @"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Maps\" + mn;
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
                else if (arg == "--meleedemo") meleedemo = true;
                else if (arg == "--falldemo") falldemo = true;
                else if (arg == "--pronetest") pronetest = true;
                else if (arg == "--brokentest") brokentest = true;
                else if (arg == "--grenadetest") grenadetest = true;
                else if (arg == "--daynight") daynight = true;
                else if (arg == "--build") buildmode = true;
                else if (arg == "--invdragtest") { RunDragTest(); GetTree().Quit(); return; }
                else if (arg == "--invusetest") { RunUseTest(); GetTree().Quit(); return; }
                else if (arg == "--crafttest") { RunCraftTest(); GetTree().Quit(); return; }   // parse an item .dat's Blueprints -> print (crafting parser self-test)
                else if (arg == "--extractblueprints") { RunExtractBlueprints(); GetTree().Quit(); return; }   // walk retail item .dats -> content/blueprints.tsv catalog
                else if (arg == "--shelltest") { RunShellTest(); GetTree().Quit(); return; }   // shotgun shell-by-shell reload detection + sequence self-test
                else if (arg == "--farmloop") { RunFarmLoopTest(); GetTree().Quit(); return; }   // plant->grow->harvest loop: crops.tsv<->farms.tsv seed linkage + growth/yield self-test
                else if (arg == "--skilltest") { RunSkillTest(); GetTree().Quit(); return; }   // PlayerSkills grid + XP cost formula + upgrade/mastery self-test
                else if (arg == "--craftgate") { RunCraftGateTest(); GetTree().Quit(); return; }   // blueprint CRAFTING-skill gating self-test
                else if (arg == "--farmyield") { RunFarmYieldTest(); GetTree().Quit(); return; }   // agriculture-skill 2nd-yield roll self-test
                else if (arg == "--consumeholdtest") { RunConsumeHoldTest(); GetTree().Quit(); return; }   // inventory hold->eat->decrement->auto-unequip self-test
                else if (arg == "--magtest") { RunMagTest(); GetTree().Quit(); return; }   // working-magazine reload-swap self-test
            }

            // UG_MAP env var = map name; robust for names with SPACES that get mangled through `--map=` user-args
            // (e.g. master's "cow tools"). Mirrors the --map= logic. Set $env:UG_MAP before launching godot.
            var ugMap = System.Environment.GetEnvironmentVariable("UG_MAP");
            if (!string.IsNullOrEmpty(ugMap))
            {
                _mapRoot = @"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Maps\" + ugMap;
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
            if (hearTest) { RunHearTest(); return; }   // pure logic check, no world needed
            if (armorTest) { RunArmorTest(); return; }   // pure logic check, no world needed
            if (farmTest) { RunFarmTest(); return; }   // pure logic check, no world needed

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
                _rigCaptureFrames = System.Environment.GetEnvironmentVariable("UG_HAMMER") == "1"
                    ? new[] { 52, 56, 60, 64, 68, 72 }          // UG_HAMMER: the rack window (PlayHammer at f50) -> verify the gun ROTATES through the charge
                    : new[] { 10, 66, 89, 92, 95, 120 };        // equip -> ADS -> fire+1 (muzzle flash + tracer) -> reload
                _vmTest = true;
                GetWindow().Size = new Vector2I(2560, 1440);
                BuildViewmodelTest(gun ?? "eaglefire");   // --gun=<name> picks the gun (eaglefire | maplestrike)
                if (_vmAttach) _rigCaptureFrames = new[] { 40, 50, 60, 70, 80, 90 };   // menu open (post-equip) for each frame
                return;
            }

            if (veh != null)
            {
                _rigDir = veh;
                _rigCaptureFrames = new[] { 45, 90, 150, 210, 280, 340 };   // spread across the driving course (also keeps the movie running the full length)
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
                menu.OnDrivePEI = noZombies => { menu.QueueFree(); _noZombies = noZombies; _peiPlayable = true; BuildObjectsTest(); };
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
            _vm = isMelee
                ? new Viewmodel { MeleeMesh = $"{gunName}.txt", MeleeAlbedo = $"{gunName}_albedo.png" }
                : new Viewmodel { GunName = gunName };   // self-contained: own SubViewport camera at FOV 60, composited on top
            AddChild(_vm);
            _vmMelee = isMelee;
            if (isMelee) AddChild(new MeleeSwingDriver { VM = _vm });   // periodic swings so the --vm render shows the melee swing anim
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

            AddChild(Terrain.LoadMapMerged(_mapRoot + @"\Landscape\Heightmaps", withCollider: false));   // --map= aware (defaults to PEI); any modern-Landscape map renders here

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

        // --farmloop: validate the plant->grow->harvest loop's DATA (crops.tsv seed id <-> farms.tsv growth/grow) + the
        // growth timer + harvest yield, without a scene. (The console `plant` + E-harvest INPUT is playtested live.)
        void RunFarmLoopTest()
        {
            CropRegistry.Load();
            SDG.Unturned.FarmRegistry.Load();
            int pass = 0, fail = 0;
            foreach (var cropName in new[] { "carrot", "wheat", "tomato", "potato" })
            {
                if (!CropRegistry.TryByName(cropName, out var cd)) { GD.Print($"[farmloop] {cropName}: FAIL no crops.tsv entry"); fail++; continue; }
                SDG.Unturned.FarmRegistry.TryGet(cd.SeedId, out var def);
                var crop = new SDG.Unturned.PlantedCrop { Def = def, PlantedAt = 0 };
                bool young = !crop.IsFullyGrown(1);                         // just planted -> not grown
                bool grown = def.Growth > 0 && crop.IsFullyGrown(def.Growth + 1);   // after Growth secs -> grown
                ushort yield = crop.Harvest(def.Growth + 1);               // harvest a grown crop -> Grow item id
                bool yok = yield == def.Grow && yield != 0;
                bool ok = young && grown && yok;
                GD.Print($"[farmloop] {cropName}: seed={cd.SeedId} growth={def.Growth}s grow={def.Grow} | young={young} grown={grown} yield={yield}/{yok} => {(ok ? "PASS" : "FAIL")}");
                if (ok) pass++; else fail++;
            }
            GD.Print($"[farmloop] {pass} PASS / {fail} FAIL");
        }

        // --skilltest: validate the PlayerSkills grid sizes + the source XP cost formula + upgrade/mastery (data-model self-test).
        void RunSkillTest()
        {
            var sk = new SDG.Unturned.PlayerSkills();
            int pass = 0, fail = 0;
            void Check(string name, bool ok) { GD.Print($"[skilltest] {name}: {(ok ? "PASS" : "FAIL")}"); if (ok) pass++; else fail++; }

            Check("OFFENSE has 7", sk.skills[(int)SDG.Unturned.EPlayerSpeciality.OFFENSE].Length == 7);
            Check("DEFENSE has 7", sk.skills[(int)SDG.Unturned.EPlayerSpeciality.DEFENSE].Length == 7);
            Check("SUPPORT has 8", sk.skills[(int)SDG.Unturned.EPlayerSpeciality.SUPPORT].Length == 8);

            // cost: AGRICULTURE (max7,base10,diff1.0) L0=10,L1=20 ; CRAFTING (max3,base20,diff1.5) L0=20,L1=50
            var ag = sk.GetSkill((int)SDG.Unturned.EPlayerSpeciality.SUPPORT, (int)SDG.Unturned.EPlayerSupport.AGRICULTURE);
            Check("AGRICULTURE max 7", ag.max == 7);
            Check("AGRICULTURE L0 cost 10", ag.Cost == 10);
            var cr = sk.GetSkill((int)SDG.Unturned.EPlayerSpeciality.SUPPORT, (int)SDG.Unturned.EPlayerSupport.CRAFTING);
            Check("CRAFTING max 3", cr.max == 3);
            Check("CRAFTING L0 cost 20", cr.Cost == 20);

            // award 30 XP -> upgrade AGRICULTURE twice (10+20) -> level 2, 0 XP left, 3rd blocked
            sk.AwardExperience(30);
            bool u1 = sk.TryUpgrade(SDG.Unturned.EPlayerSupport.AGRICULTURE);   // 10 -> lvl1, 20 left
            bool u2 = sk.TryUpgrade(SDG.Unturned.EPlayerSupport.AGRICULTURE);   // 20 -> lvl2, 0 left
            bool u3 = sk.TryUpgrade(SDG.Unturned.EPlayerSupport.AGRICULTURE);   // 30 -> blocked (0 XP)
            Check("upgrade x2 ok + 3rd blocked", u1 && u2 && !u3);
            Check("AGRICULTURE level 2", sk.Level(SDG.Unturned.EPlayerSupport.AGRICULTURE) == 2);
            Check("XP spent to 0", sk.experience == 0);
            Check("mastery 2/7", Mathf.Abs(ag.Mastery - 2f / 7f) < 0.001f);

            // SHARPSHOOTER recoil/spread multiplier = 1 - mastery*0.4 (lvl0 = 1.0, max7 = 0.6)
            var ss = sk.GetSkill((int)SDG.Unturned.EPlayerSpeciality.OFFENSE, (int)SDG.Unturned.EPlayerOffense.SHARPSHOOTER);
            ss.level = 0; Check("sharpshooter mult 1.0 at lvl0", Mathf.Abs(sk.SharpshooterRecoilMultiplier() - 1.0f) < 0.001f);
            ss.level = 7; Check("sharpshooter mult 0.6 at max", Mathf.Abs(sk.SharpshooterRecoilMultiplier() - 0.6f) < 0.001f);

            // STRENGTH fall-damage multiplier = 1 - mastery*0.75 (max STRENGTH lvl 5 -> 0.25)
            var st = sk.GetSkill((int)SDG.Unturned.EPlayerSpeciality.DEFENSE, (int)SDG.Unturned.EPlayerDefense.STRENGTH);
            st.level = 0; Check("strength fall mult 1.0 at lvl0", Mathf.Abs(sk.StrengthFallMultiplier() - 1.0f) < 0.001f);
            st.level = 5; Check("strength fall mult 0.25 at max", Mathf.Abs(sk.StrengthFallMultiplier() - 0.25f) < 0.001f);

            // survival-sim multipliers at max level (all max 5)
            sk.GetSkill((int)SDG.Unturned.EPlayerSpeciality.DEFENSE, (int)SDG.Unturned.EPlayerDefense.VITALITY).level = 5;
            Check("vitality regen 2.0x at max", Mathf.Abs(sk.VitalityRegenMultiplier() - 2.0f) < 0.001f);
            sk.GetSkill((int)SDG.Unturned.EPlayerSpeciality.DEFENSE, (int)SDG.Unturned.EPlayerDefense.SURVIVAL).level = 5;
            Check("survival drain 0.8x at max", Mathf.Abs(sk.SurvivalDrainMultiplier() - 0.8f) < 0.001f);
            sk.GetSkill((int)SDG.Unturned.EPlayerSpeciality.OFFENSE, (int)SDG.Unturned.EPlayerOffense.CARDIO).level = 5;
            Check("cardio regen 2.0x at max", Mathf.Abs(sk.CardioStaminaRegenMultiplier() - 2.0f) < 0.001f);
            sk.GetSkill((int)SDG.Unturned.EPlayerSpeciality.OFFENSE, (int)SDG.Unturned.EPlayerOffense.EXERCISE).level = 5;
            Check("exercise drain 0.5x at max", Mathf.Abs(sk.ExerciseStaminaDrainMultiplier() - 0.5f) < 0.001f);
            sk.GetSkill((int)SDG.Unturned.EPlayerSpeciality.OFFENSE, (int)SDG.Unturned.EPlayerOffense.OVERKILL).level = 7;
            Check("overkill melee 1.5x at max", Mathf.Abs(sk.OverkillMeleeMultiplier() - 1.5f) < 0.001f);
            sk.GetSkill((int)SDG.Unturned.EPlayerSpeciality.OFFENSE, (int)SDG.Unturned.EPlayerOffense.DEXTERITY).level = 5;
            Check("dexterity reload 1.5x at max", Mathf.Abs(sk.DexterityReloadSpeed() - 1.5f) < 0.001f);
            sk.GetSkill((int)SDG.Unturned.EPlayerSpeciality.DEFENSE, (int)SDG.Unturned.EPlayerDefense.IMMUNITY).level = 5;
            Check("immunity infection 0.5x at max", Mathf.Abs(sk.ImmunityInfectionMultiplier() - 0.5f) < 0.001f);
            sk.GetSkill((int)SDG.Unturned.EPlayerSpeciality.DEFENSE, (int)SDG.Unturned.EPlayerDefense.SNEAKYBEAKY).level = 7;
            Check("sneakybeaky noise 0.25x at max", Mathf.Abs(sk.SneakyBeakyNoiseMultiplier() - 0.25f) < 0.001f);

            GD.Print($"[skilltest] {pass} PASS / {fail} FAIL");
        }

        // --craftgate: a blueprint requiring CRAFTING level 2 is blocked below it + allowed at/above it (skill-effect self-test).
        void RunCraftGateTest()
        {
            var skills = new SDG.Unturned.PlayerSkills();
            var bp = new BlueprintDef { Skill = "Craft", SkillLevel = 2 };   // requires CRAFTING >= 2
            var craft = skills.GetSkill((int)SDG.Unturned.EPlayerSpeciality.SUPPORT, (int)SDG.Unturned.EPlayerSupport.CRAFTING);
            int pass = 0, fail = 0;
            void Check(string n, bool ok) { GD.Print($"[craftgate] {n}: {(ok ? "PASS" : "FAIL")}"); if (ok) pass++; else fail++; }

            Check("blocked at CRAFTING 0", !Crafting.MeetsSkill(bp, skills));
            craft.level = 1;
            Check("blocked at CRAFTING 1", !Crafting.MeetsSkill(bp, skills));
            craft.level = 2;
            Check("allowed at CRAFTING 2", Crafting.MeetsSkill(bp, skills));
            craft.level = 5;
            Check("allowed at CRAFTING 5", Crafting.MeetsSkill(bp, skills));
            Check("no-skill blueprint always ok", Crafting.MeetsSkill(new BlueprintDef { Skill = "", SkillLevel = 0 }, new SDG.Unturned.PlayerSkills()));
            Check("null skills = ungated", Crafting.MeetsSkill(bp, null));

            GD.Print($"[craftgate] {pass} PASS / {fail} FAIL");
        }

        // --farmyield: the agriculture-skill 2nd-yield roll (source InteractableFarm: Random.value < mastery(AGRICULTURE)).
        void RunFarmYieldTest()
        {
            var skills = new SDG.Unturned.PlayerSkills();
            var ag = skills.GetSkill((int)SDG.Unturned.EPlayerSpeciality.SUPPORT, (int)SDG.Unturned.EPlayerSupport.AGRICULTURE);
            int pass = 0, fail = 0;
            void Check(string n, bool ok) { GD.Print($"[farmyield] {n}: {(ok ? "PASS" : "FAIL")}"); if (ok) pass++; else fail++; }

            ag.level = 0; Check("mastery 0 at agri 0", ag.Mastery == 0f);
            ag.level = 7; Check("mastery 1.0 at agri max", Mathf.Abs(ag.Mastery - 1f) < 0.001f);
            ag.level = 0; int f0 = 0; for (int i = 0; i < 2000; i++) if (GD.Randf() < ag.Mastery) f0++;
            Check("no 2nd-yield at agri 0", f0 == 0);
            ag.level = 7; int f1 = 0; for (int i = 0; i < 2000; i++) if (GD.Randf() < ag.Mastery) f1++;
            Check("always 2nd-yield at agri max", f1 == 2000);
            ag.level = 4; int f4 = 0; for (int i = 0; i < 4000; i++) if (GD.Randf() < ag.Mastery) f4++;
            float rate = f4 / 4000f;   // mastery 4/7 ~= 0.571
            Check($"~57% at agri 4 (got {rate:0.00})", Mathf.Abs(rate - 4f / 7f) < 0.05f);

            GD.Print($"[farmyield] {pass} PASS / {fail} FAIL");
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

        async void BuildObjectsTest()
        {
            _worldBuild = true;   // --shot waits for _worldReady (below) so the async world (incl. Trees) is fully loaded before the screenshot
            float F(string s) => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
            // Phased async load with a progress screen + per-category timing (master). Phase(name) records the PREVIOUS
            // phase's elapsed ms, advances the bar, sets the label, and yields a frame so the overlay actually paints
            // before the next (blocking) chunk of work runs.
            var loading = new LoadingScreen(); AddChild(loading); loading.SetTotal(11);
            var timings = new System.Collections.Generic.Dictionary<string, double>();
            string curPhase = null; var phaseSw = System.Diagnostics.Stopwatch.StartNew();
            async System.Threading.Tasks.Task Phase(string name)
            {
                if (curPhase != null) { timings[curPhase] = phaseSw.Elapsed.TotalMilliseconds; loading.Advance(); }
                curPhase = name; loading.SetStatus(name + "…"); phaseSw.Restart();
                if (!_bakeNav) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);   // --bakenav: skip the per-phase frame-yield so the WHOLE world loads synchronously -> we can bake offline
            }
            // REAL PEI lighting via DayNightCycle (src Lighting.dat: ported sky shader + warm ambient + sun per time-of-day)
            // -- replaces the ProceduralSky + sky-tinted ambient that didn't match the source palette. "Drive PEI"
            // (--peidrive) is the mode master actually plays, so THIS is the one that has to carry the src-accurate lighting.
            var env = new Godot.Environment { AmbientLightSource = Godot.Environment.AmbientSource.Color };
            AddChild(new WorldEnvironment { Environment = env });
            var sun = new DirectionalLight3D { LightEnergy = 1.2f, ShadowEnabled = true };
            AddChild(sun);
            AddChild(new DayNightCycle { Sun = sun, Env = env, DayLength = 300f });
            await Phase("Terrain");
            var terr = Terrain.LoadMapMerged(_mapRoot + @"\Landscape\Heightmaps", withCollider: true);
            AddChild(terr);
            await Phase("Objects");

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
                if (nm.StartsWith("Glass"))   // Glass_0/Glass_1 have NO albedo texture (src uses a shader-based transparent material) -> the brown fallback made them opaque. Give glass a proper see-through look.
                {
                    mm = new StandardMaterial3D
                    {
                        AlbedoColor = new Color(0.62f, 0.73f, 0.78f, 0.26f),   // light blue-grey, mostly see-through
                        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                        Metallic = 0f, Roughness = 0.06f,                       // smooth + glossy like glass
                        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                    };
                    matCache[nm] = mm;
                    return mm;
                }
                // VertexColorUseAsAlbedo: objects are white by default (texture unchanged), but billboards bake their ad-geometry
                // palette into per-vertex colour so multi-material signs keep their real colours over the merged mesh (master).
                mm = new StandardMaterial3D { Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled, VertexColorUseAsAlbedo = true };
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
                        // Tiny PALETTE textures (billboards etc. use a 2x2/4x2 colour-key sampled by the mesh UVs): Linear
                        // filtering + mipmaps average the palette cells together -> the thin logo/text geometry minifies to
                        // black at distance (same class as the gun black-texture bug). Nearest + no mipmaps keeps each cell crisp.
                        bool palette = img.GetWidth() <= 16 && img.GetHeight() <= 16;
                        if (!palette) img.GenerateMipmaps();
                        mm.AlbedoTexture = ImageTexture.CreateFromImage(img);
                        // master: the whole world is Nearest (crisp Unturned look); only grass+flowers are bilinear (FoliageField).
                        // palette textures skip mipmaps too (else the 2x2 cells average to black at distance).
                        mm.TextureFilter = palette ? BaseMaterial3D.TextureFilterEnum.Nearest : BaseMaterial3D.TextureFilterEnum.NearestWithMipmaps;
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
            // holiday gate: PEI's Objects.dat has ~285 Christmas/Halloween props placed that only show in-season
            // (src: ObjectAsset.holidayRestriction + HolidayUtil schedule). Skip any whose holiday != the active one.
            var holidayOf = new System.Collections.Generic.Dictionary<string, string>();
            string hpath = dir + "holiday_props.txt";
            if (System.IO.File.Exists(hpath))
                foreach (var hl in System.IO.File.ReadLines(hpath)) { var q = hl.Split(' ', System.StringSplitOptions.RemoveEmptyEntries); if (q.Length >= 2) holidayOf[q[0]] = q[1]; }
            string activeHoliday = ActiveHoliday();
            int holidaySkipped = 0;
            foreach (var line in System.IO.File.ReadLines(dir + _mapPlace))
            {
                var p = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 10 || !g2m.TryGetValue(p[0], out var name)) continue;
                if (holidayOf.TryGetValue(p[0], out var ph) && ph != activeHoliday) { holidaySkipped++; continue; }   // out-of-season holiday prop
                if (!cache.TryGetValue(name, out var mesh)) { mesh = ObjMesh.Load(dir + name + ".obj"); cache[name] = mesh; }
                if (mesh == null) continue;
                float px = F(p[1]), py = F(p[2]), pz = F(p[3]), ex = F(p[4]), ey = F(p[5]), ez = F(p[6]), sx = F(p[7]), sy = F(p[8]), sz = F(p[9]);
                var gpos = new Vector3(px, py, -pz);
                // negate-Z LAYOUT (keeps the map orientation) with a RAW mesh (un-mirrored geometry): rotation = old C_z-conjugated euler
                // raw (un-mirrored) mesh convention: pitch is +ex, NOT -ex. The -ex was left over from the old negate-Z-verts
                // convention -> it inverted the pitch, flipping tilted props (e.g. the lighthouse, ex=270) upside-down into the
                // ground. +ex matches Unity's rotation for the raw mesh; yaw = 180-ey: the raw mesh in the negate-Z layout faces 180 off (only visible on asymmetric props like town buildings, hidden on the symmetric lighthouse), so +180 corrects every prop's facing.
                // ROLL = -ez: the raw-mesh frame flips the roll sign (same as it flips pitch), so ez!=0 props (e.g. the
                // Alberton bank clocks) came out ~180 off. Negating ez faces them right. ez=0 props are UNCHANGED (roll
                // term is identity), so the whole map except the handful of rolled props is byte-identical -- no regression.
                var rot = new Basis(new Vector3(0, 1, 0), Mathf.DegToRad(180f - ey)) * new Basis(new Vector3(1, 0, 0), Mathf.DegToRad(ex)) * new Basis(new Vector3(0, 0, 1), Mathf.DegToRad(-ez));
                var basis = rot.Scaled(new Vector3(sx, sy, sz));
                AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = MatFor(name), Transform = new Transform3D(basis, gpos),
                    VisibilityRangeEnd = 320f, VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Disabled });   // individual props already frustum-cull behind the player; add a distance cutoff (master)
                // tree foliage: a SEPARATE leaf mesh with its own leaf material (so the trunk keeps its bark texture)
                if (!folCache.TryGetValue(name, out var fmesh))
                {
                    string fp = dir + name + "_foliage.obj";
                    fmesh = System.IO.File.Exists(fp) ? ObjMesh.Load(fp) : null;
                    folCache[name] = fmesh;
                }
                if (fmesh != null) AddChild(new MeshInstance3D { Mesh = fmesh, MaterialOverride = MatFor(name + "_foliage"), Transform = new Transform3D(basis, gpos),
                    VisibilityRangeEnd = 240f, VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Disabled });   // leaves cull closer
                if (_peiPlayable)   // walkable collision: trimesh of the VISUAL mesh (trees collide on the trunk only; the separate leaf mesh has no collider, so you walk through foliage)
                {
                    if (!shapeCache.TryGetValue(name, out var shp)) { shp = mesh.CreateTrimeshShape(); shapeCache[name] = shp; }
                    if (shp != null)
                    {
                        // Only LARGE opaque structures (buildings, gated by scaled mesh size) block the item LOS raycast; every small prop
                        // + all glass/alpha-cutout goes on the see-through layer 6 so the raycast passes through (master). Player collides with both.
                        var ab = mesh.GetAabb();
                        float maxDim = Mathf.Max(ab.Size.X * sx, Mathf.Max(ab.Size.Y * sy, ab.Size.Z * sz));
                        bool losBlocker = maxDim >= 5f && MatFor(name).Transparency == BaseMaterial3D.TransparencyEnum.Disabled;
                        var body = new StaticBody3D { Transform = new Transform3D(basis, gpos), CollisionLayer = losBlocker ? 1u << 0 : 1u << 6 };
                        body.SetMeta(PlayerController.SurfMeta, (int)(fmesh != null ? PlayerController.Surf.Wood : PlayerController.Surf.Concrete));   // trees (have foliage) = wood impacts; buildings/props = concrete
                        body.AddChild(new CollisionShape3D { Shape = shp });
                        AddChild(body);
                    }
                }
                placed++;
                var cell = new Vector2I(Mathf.FloorToInt(px / 96f), Mathf.FloorToInt(pz / 96f));
                cellCount.TryGetValue(cell, out int cc); cellCount[cell] = cc + 1;
                cellSum.TryGetValue(cell, out Vector3 cs); cellSum[cell] = cs + gpos;
                if (cc + 1 > bestN) { bestN = cc + 1; bestCell = cell; }
            }
            var focus = placed > 0 ? cellSum[bestCell] / bestN : Vector3.Zero;
            GD.Print($"[OBJECTS] placed {placed} objects ({cache.Count} meshes); densest cluster {bestN} near {focus}; holiday-gated {holidaySkipped} (active={activeHoliday})");

            // aerial over the busiest cluster so the full populated PEI (all ~360 types) reads at once, no gaps
            if (_peiPlayable)
            {
                await Phase("Player");
                // menu "Drive PEI": drop the player + jeep on open grass with REAL controls (WASD + mouse look, E to enter/drive the jeep)
                float sx = 0f, sz = -350f, spawnYaw = 0f;
                // player spawn: PEI's REAL regular spawn points (Spawns/Players.dat = u8 ver, u8 count, per point Vector3 + u8 angle*2 + bool isAlt if v>3;
                // source LevelPlayers.getSpawn picks a random NON-alt spawn). Falls back to the inland-grass scan if the file's missing.
                bool gotSpawn = false;
                {
                    string ppath = _mapRoot + @"\Spawns\Players.dat";
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
                if (System.Environment.GetEnvironmentVariable("UG_LHSPAWN") == "1") { sx = 247.452f; sz = -792.643f; }   // demo: spawn at the PEI lighthouse (prop-orientation check)
                { var _ox = System.Environment.GetEnvironmentVariable("UG_SPAWNX"); var _oz = System.Environment.GetEnvironmentVariable("UG_SPAWNZ");   // spawn at arbitrary godot XZ (e.g. a named location node) for town orbits
                  if (_ox != null && float.TryParse(_ox, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _px)) sx = _px;
                  if (_oz != null && float.TryParse(_oz, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _pz)) sz = _pz; }
                CharacterModel.LoadBundled();
                var player = new PlayerController { CaptureMouse = true };
                player.LoadGun("res://content/eaglefire.dat");
                AddChild(player);
                _pdPlayer = player;   // UG_AUTOFIRE terrain-impact verification
                player.LinkWorldLighting(sun, env);   // FP gun takes the world day/night sun + ambient -- was NEVER called in Drive PEI, so the gun ignored time-of-day (master saw "not applying at all")
                AddChild(new DevConsole { Player = player });   // F1 dev console: give <item> / vehicle <name> / plant <crop> spawns at the look-orb (master)
                AddChild(new CropManager());   // farm crop growth ticking + plant/harvest (console `plant`, E to harvest)
                AddChild(new MapUI { Player = player });         // M: full-screen PEI map (town nodes + player pos/facing)
                player.GlobalPosition = new Vector3(sx, terr.SampleHeight(sx, sz) + 3f, sz);
                player.RotationDegrees = new Vector3(0f, spawnYaw, 0f);   // face the spawn point's angle
                player.Spawn = player.GlobalPosition;   // respawn on this above-ground point, NOT the default (0,1,0) which is underground on PEI
                if (System.Environment.GetEnvironmentVariable("UG_OOBTEST") == "1") player.GlobalPosition = new Vector3(sx, -2000f, sz);   // test hook: drop below the map -> should trip the OOB kill
                { var hud = new HUD { Player = player }; AddChild(hud); player.Hud = hud; }
                AddChild(new FpsCounter());   // top-right yellow FPS counter (master 2026-07-11)
                { var hmL = new CanvasLayer { Layer = 98 }; hmL.AddChild(new HitmarkerHUD()); AddChild(hmL); }   // hit / headshot markers (master)
                { var pause = new PauseMenu(); AddChild(pause); player.PauseMenu = pause; }               // ESC menu (parity with BuildPlayable)
                AddChild(new Profiler());   // F3 perf overlay (parity)
                { var attach = new AttachmentMenu(); AddChild(attach); player.AttachMenu = attach; }       // T weapon-attachment menu -- was never wired in PEI drive, so T did nothing (broken since PEI map)
                var jeep = Vehicle.BuildByName("jeep");
                AddChild(jeep);
                jeep.GlobalPosition = new Vector3(sx + 2.2f, terr.SampleHeight(sx + 2.2f, sz) + 1.5f, sz);

                // ZOMBIE SPAWNS: PEI's REAL zombie spawn points (Spawns/Animals.dat = 1456 points; legacy filename that
                // LevelZombies reads), region-streamed around the player like Unturned's region loader -- see ZombieField.
                // Replaces the old Environment/Bounds.dat navmesh approximation (52 zombies) with the map's actual horde design.
                if (!_noZombies)   // "Drive PEI — No Zombies" menu button / --nozombies flag
                {
                    await Phase("Zombies");
                    var zf = new ZombieField { Player = player, Terr = terr };
                    zf.LoadFromPei(_mapRoot);
                    AddChild(zf);
                    _ztField = zf;   // --zombietest reads this at frame 25 to verify spawns land on the navmesh
                }

                // VEHICLE SPAWNS: Spawns/Vehicles.dat (source LevelVehicles River: u8 ver, [SteamID if 1<v<3], u8 tableCount,
                // per table [color 3B, name str, tableID u16 if v>3, u8 tierCount, per tier: name str, chance f32, u8 spawnCount, per spawn u16],
                // u16 pointCount, per point: u8 type, Vector3, u8 angle*2). type = table index: 0 Civilian, 1 Police, 2 Fire, 3 Military,
                // 4 Medic, 5 Farm, 6-11 air/water/tank. LAND (0-5): Civilian=car pool, Police/Fire/Medic=static mesh, Military=humvee, Farm=jeep stand-in.
                {
                    await Phase("Vehicles");
                    string vpath = _mapRoot + @"\Spawns\Vehicles.dat";
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
                            string vn = null;   // all PEI service vehicles (Police / Fire / Medic) are drivable now, no static meshes
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
                            else   // drivable: Civilian -> real civilian-car pool, Military -> humvee, Farm -> jeep stand-in (no tractor mesh yet)
                            {
                                vn = type switch   // reuse the outer vn (null here); the static-mesh branch above handled Police/Fire/Medic
                                {
                                    0 => (i % 3) switch { 0 => "sedan", 1 => "hatchback", _ => "roadster" },   // Civilian rolls the civilian car pool
                                    1 => "police",                                                              // Police
                                    2 => "firetruck",                                                           // Fire
                                    3 => (i % 3) switch { 0 => "humvee", 1 => "jeep", _ => "ural" },            // Military_Canada: humvee + jeep + ural truck, all forest
                                    4 => "ambulance",                                                           // Medic -> drivable ambulance
                                    5 => "tractor",                                                             // Farm -> drivable tractor
                                    _ => "quad",                                                                // fallback
                                };
                                var veh = Vehicle.BuildByName(vn, i);   // variant=i -> deterministic paint variety per spawn point
                                AddChild(veh);
                                veh.GlobalPosition = vpos;
                                veh.RotationDegrees = new Vector3(0f, -ang, 0f);
                                nv++;
                            }
                        }
                    }
                    GD.Print($"[vehicles] spawned {nv} PEI vehicles (Civilian=sedan/hatchback/roadster, Military=humvee, Farm=jeep; Police/Fire/Ambulance=static mesh; air/water/tank skipped)");
                }
                // LOOT: PEI's 2470 item spawn points (Spawns/Jars.dat), region/distance-streamed around the player (LootField).
                {
                    await Phase("Loot");
                    var loot = new LootField { Player = player, Terr = terr };
                    loot.LoadFromPei(_mapRoot);
                    AddChild(loot);
                }
                // WILDLIFE: Spawns/Fauna.dat animal points (deer/pig/cow), streamed as rigged RiggedCharacters (AnimalField).
                {
                    await Phase("Animals");
                    var animals = new AnimalField { Player = player, Terr = terr };
                    animals.LoadFromPei(_mapRoot);
                    AddChild(animals);
                }
                // ROAD SPLINES: Environment/Paths.dat bezier road network (separate from the road props) -> extruded strips.
                {
                    await Phase("Roads");
                    var rf = new RoadField { Terr = terr };
                    rf.LoadFromEnvironment(_mapRoot + @"\Environment");
                    AddChild(rf);
                }
                // FOLIAGE: PEI's baked Foliage.blob grass (asset 1, 612K instances) as one MultiMesh
                {
                    await Phase("Foliage");
                    var ff = new FoliageField();
                    AddChild(ff);
                    ff.LoadGrass();
                }
                // RESOURCES: Terrain/Trees.dat -> trees/bushes/ore-rocks/mushrooms (1694 spawns, 26 types) as MultiMeshes
                {
                    await Phase("Trees");
                    var rsf = new ResourceField();
                    AddChild(rsf);
                    rsf.LoadResources(ActiveHoliday());   // gate CHRISTMAS resources (candy canes/snow piles) like the objects
                }
                if (System.Environment.GetEnvironmentVariable("UG_ZAERIAL") == "1")   // demo cam: look down on the spawn town so the zombies are visible
                {
                    var acam = new Camera3D { Current = true, Fov = 62f, Far = 20000f };
                    AddChild(acam);
                    var ctr = _vHave ? _vAim : player.GlobalPosition;   // prefer a real vehicle; else the spawn town
                    acam.Position = ctr + (_vHave ? new Vector3(0f, 9f, 11f) : new Vector3(0f, 50f, 44f));
                    acam.LookAt(ctr, Vector3.Up);
                }
                if (System.Environment.GetEnvironmentVariable("UG_LHSPAWN") == "1")   // demo cam: frame the lighthouse (prop-orientation check)
                {
                    var lcam = new Camera3D { Current = true, Fov = 55f, Far = 20000f };
                    AddChild(lcam);
                    var lb = player.GlobalPosition;   // spawned at the lighthouse base
                    if (System.Environment.GetEnvironmentVariable("UG_ORBIT") == "1")   // orbit the prop (showcase video) instead of a static frame
                        AddChild(new OrbitCam { Cam = lcam, Center = lb });   // radius/height/center-lift via UG_ORBITR/UG_ORBITH/UG_ORBITCY
                    else
                    {
                        lcam.Position = lb + new Vector3(46f, 30f, 46f);
                        lcam.LookAt(lb + new Vector3(0f, 22f, 0f), Vector3.Up);
                    }
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
            NearestFilter.Apply(this);   // Unturned point-filters level/object textures (FilterMode.Point) -- match it scene-wide (crisp pixel look)
            if (curPhase != null) { timings[curPhase] = phaseSw.Elapsed.TotalMilliseconds; loading.Advance(); }   // record the final phase
            loading.Finish(timings);   // hide the overlay + show the per-category timing breakdown top-left for a few seconds (master)
            // Zombie navmesh POCKETS -- bake NOW, in the FULL world, so the BUILDINGS (layer 1<<0) carve the mesh and
            // zombies route around them. This full-world bake is the CANONICAL one (save:true -> pei_pocket_N.res);
            // the terrain-only peiplay/navshot verify modes pass save:false so they never overwrite it.
            try { var _navPk = ZombieNav.LoadPockets(_mapRoot); ZombieNav.BuildOrLoad(this, _navPk, overlay: false, save: _bakeNav, bakeIfMissing: _bakeNav); } catch (System.Exception _ne) { GD.PrintErr($"[zombienav] full-world nav failed: {_ne.Message}"); }   // --bakenav BAKES+SAVES here; the game just LOADS the committed .res
            _worldReady = true;   // async world fully built (terrain..trees) -> the --shot harness can now capture a loaded frame
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

            var terr = Terrain.LoadMapMerged(@"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Maps\PEI\Landscape\Heightmaps", withCollider: true);
            AddChild(terr);

            var pockets = ZombieNav.LoadPockets(@"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Maps\PEI");
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

        // --peiplay: drop the player onto REAL PEI terrain (colliders on), spawned on land via SampleHeight, scripted to walk.
        void BuildPeiPlay()
        {
            // REAL PEI lighting via DayNightCycle (src Lighting.dat: warm ambient + sky/sun per time-of-day). The old
            // hardcoded flat GREY env (0.6 grey @ 0.75) is what made everything dark + washed -- it never used the
            // lighting rework at all. The DayNightCycle drives Env (sky + warm ambient) + the sun each frame.
            var env = new Godot.Environment { AmbientLightSource = Godot.Environment.AmbientSource.Color };
            AddChild(new WorldEnvironment { Environment = env });
            var sun = new DirectionalLight3D { LightEnergy = 1.2f, ShadowEnabled = true };
            AddChild(sun);
            AddChild(new DayNightCycle { Sun = sun, Env = env, DayLength = 300f });

            var terr = Terrain.LoadMapMerged(@"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Maps\PEI\Landscape\Heightmaps", withCollider: true);
            AddChild(terr);

            // Zombie navmesh POCKETS (source LevelNavigation Flags): bake a Godot navmesh in each of PEI's 19 POI
            // pockets from the world collision (agent-radius wall buffer), saved + reused. (Phase 1 -- pathing wired next.)
            { var _pk = ZombieNav.LoadPockets(@"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Maps\PEI"); ZombieNav.BuildOrLoad(this, _pk, overlay: false, save: false); }   // peiplay is terrain-only -> don't save (loads the canonical full-world mesh if --peidrive baked it)

            CharacterModel.LoadBundled();
            var player = new PlayerController();
            player.LoadGun("res://content/eaglefire.dat");
            AddChild(player);
            player.LinkWorldLighting(sun, env);   // FP gun takes the world day/night sun + ambient (same missing hookup as Drive PEI)
            if (System.Environment.GetEnvironmentVariable("UG_HOLD") is string _hc && _hc.Length > 0)
            {
                SDG.Unturned.ItemCatalog.RegisterAll();
                var _ha = SDG.Unturned.Assets.find(ConsumableRegistry.IdForMesh(_hc));   // resolve a REAL asset so StartConsume works (UG_EAT)
                player.EquipHeldConsumable(_ha, _hc);   // render harness: hold a consumable (UG_HOLD=canned_beans [UG_EAT=1 to play the eat])
            }
            if (System.Environment.GetEnvironmentVariable("UG_TESTLIGHT") == "1")   // render harness: a bright red dynlight just in front-left of the player -> should spill onto the FP gun
            {
                var tl = new OmniLight3D { OmniRange = 6f, LightColor = new Color(1f, 0.1f, 0.1f), LightEnergy = 8f, ShadowEnabled = false };
                tl.AddToGroup("dynlight");
                player.AddChild(tl);
                tl.Position = new Vector3(1.1f, 0.1f, 0f);   // relative to the player: hard RIGHT -> gun should light on its right (outer) side if the transform is correct
            }
            AddChild(new CropManager());   // farm crop growth ticking + plant/harvest (console `plant`, E to harvest)
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
            AddChild(new DevConsole { Player = player });   // F1 dev console: give <item> / vehicle <name> spawns at the look-orb (master)
            AddChild(new MapUI { Player = player });         // M: full-screen PEI map (town nodes + player pos/facing)

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

            // NEW effects loaded from consumable_stats.tsv (the whole catalog, not just 8 hardcoded)
            p.Stamina = 0.1f; p.Infection = 0.5f;
            p.Consume(SDG.Unturned.Assets.find(93));   // Bottled Energy: +55 water, +75 energy
            Check("energy -> stamina 0.1+0.75=0.85", Mathf.Abs(p.Stamina - 0.85f) < 0.01f);
            var abx = SDG.Unturned.Assets.find(11);    // Antibiotics: disinfectant (lowers infection)
            if (abx != null && abx.useDisinfectant > 0) { p.Consume(abx); Check("antibiotics -> infection dropped", p.Infection < 0.5f); }
            var cola = SDG.Unturned.Assets.find(80);   // Canned Cola -- was inert (no hardcoded stats); now works from the .dat
            Check("previously-inert cola is now IsConsumable", cola != null && cola.IsConsumable);
            Check("cola has real .dat effects (water/energy)", cola != null && (cola.useWater > 0 || cola.useEnergy > 0));

            GD.Print($"[USETEST] RESULT {pass} passed, {fail} failed");
        }

        // --consumeholdtest: the full inventory HOLD flow -- equip a consumable to hand, click to eat, decrement the
        // stack, and auto-unequip back to the gun when the last one is gone (source UseableConsumeable equip->use->remove).
        static void RunConsumeHoldTest()
        {
            SDG.Unturned.ItemCatalog.RegisterAll();
            var p = new PlayerController { Health = 50f, Food = 0.1f, Water = 0.1f };
            p.Inventory = new PlayerInventory();
            p.Inventory.items[2].tryAddItem(new SDG.Unturned.Item(13));   // 2x Canned Beans (id 13) in pockets, each its own grid item
            p.Inventory.items[2].tryAddItem(new SDG.Unturned.Item(13));
            int pass = 0, fail = 0;
            void Check(string n, bool ok) { if (ok) { pass++; GD.Print($"[HOLDTEST] PASS  {n}"); } else { fail++; GD.Print($"[HOLDTEST] FAIL  {n}"); } }

            var beans = SDG.Unturned.Assets.find(13);
            Check("beans resolves + IsConsumable", beans != null && beans.IsConsumable);
            Check("beans has a held mesh in the registry", ConsumableRegistry.Mesh(13) != null);
            Check("start count = 2", p.Inventory.getItemCount(13) == 2);

            p.EquipHeldConsumable(beans, ConsumableRegistry.Mesh(13));
            Check("holding after equip", p.HoldingConsumable);
            Check("no stack spent just by holding", p.Inventory.getItemCount(13) == 2);

            p.StartConsume();                                                 // 1st click -> eat
            for (int i = 0; i < 200; i++) p.DebugConsumeTick(0.05f);   // 10s > the longest per-item Use clip (bag_chips 7.6s)
            Check("food rose after 1st eat", p.Food > 0.5f);
            Check("count -> 1 after 1st eat", p.Inventory.getItemCount(13) == 1);
            Check("still holding (1 left)", p.HoldingConsumable);

            p.StartConsume();                                                 // 2nd click -> eat the last one
            for (int i = 0; i < 200; i++) p.DebugConsumeTick(0.05f);   // 10s > the longest per-item Use clip (bag_chips 7.6s)
            Check("count -> 0 after 2nd eat", p.Inventory.getItemCount(13) == 0);
            Check("auto-unequipped when depleted", !p.HoldingConsumable);

            // per-item eat/drink archetypes (source: each item plays its own Use clip; useTime = that clip's length)
            var beansAn = ConsumableRegistry.Anims("canned_beans");
            var waterAn = ConsumableRegistry.Anims("bottled_water");
            var medkitAn = ConsumableRegistry.Anims("medkit");
            Check("beans has a Use archetype clip", !string.IsNullOrEmpty(beansAn.Use));
            Check("drink clip != eat clip (per-item)", waterAn.Use != beansAn.Use && !string.IsNullOrEmpty(waterAn.Use));
            Check("syringe/medkit clip != eat clip", medkitAn.Use != beansAn.Use && !string.IsNullOrEmpty(medkitAn.Use));
            Check("per-item useTime from Use-clip length", waterAn.UseLen > 0f && Mathf.Abs(waterAn.UseLen - beansAn.UseLen) > 0.01f);

            // per-item use/eat/drink SOUND (source ItemConsumeableAsset.use)
            Check("beans use-sound = eatcanl", ConsumableRegistry.Sound(13) == "eatcanl");
            Check("water use-sound = drinkswallow", ConsumableRegistry.Sound(14) == "drinkswallow");
            Check("medkit use-sound = use_medkit", ConsumableRegistry.Sound(15) == "use_medkit");
            Check("beans WAV loads as 16-bit PCM", PlayerController.DebugCanLoadWav("eatcanl"));
            Check("water WAV loads as 16-bit PCM", PlayerController.DebugCanLoadWav("drinkswallow"));

            // no-texture consumables use their flat _Color (cheese=yellow, potato=brown), not the gray default
            Check("cheese has a flat _Color (no texture)", ConsumableRegistry.FlatColor("cheese") is Color cc && cc.G > 0.5f && cc.B < 0.5f);
            Check("potato has a flat _Color", ConsumableRegistry.FlatColor("potato") != null);
            Check("textured item (canned_beans) has NO flat color", ConsumableRegistry.FlatColor("canned_beans") == null);

            GD.Print($"[HOLDTEST] beans={beansAn.Use}/{beansAn.UseLen:0.00}s water={waterAn.Use}/{waterAn.UseLen:0.00}s medkit={medkitAn.Use}/{medkitAn.UseLen:0.00}s");
            GD.Print($"[HOLDTEST] RESULT {pass} passed, {fail} failed");
        }

        // --magtest: working magazines (Military STANAG) -- reload SWAPS the fullest spare mag in, the old one goes back
        // keeping its leftover rounds, ammo is conserved, and no spare mag = no reload.
        static void RunMagTest()
        {
            SDG.Unturned.ItemCatalog.RegisterAll();
            var p = new PlayerController { Inventory = new PlayerInventory() };
            p.Inventory.wearBackpack(new SDG.Unturned.Item(253));   // Alicepack -> the BACKPACK page has room
            var bag = p.Inventory.items[PlayerInventory.BACKPACK];
            bag.tryAddItem(new SDG.Unturned.Item(6, 30));   // 2 full + 1 partial Military mags in the bag
            bag.tryAddItem(new SDG.Unturned.Item(6, 30));
            bag.tryAddItem(new SDG.Unturned.Item(6, 12));
            int pass = 0, fail = 0;
            void Check(string n, bool ok) { if (ok) { pass++; GD.Print($"[MAGTEST] PASS  {n}"); } else { fail++; GD.Print($"[MAGTEST] FAIL  {n}"); } }

            p.LoadGun("res://content/eaglefire.dat");
            Check("eaglefire uses magazine items (caliber match)", p.DebugUsesMag());
            Check("gun starts loaded (Ammo=30)", p.Ammo == 30);
            Check("3 spare mags = 72 rounds in the bag", p.Inventory.getItemCount(6) == 72);
            Check("has a spare mag to reload from", p.DebugHasSpareMag());

            p.Ammo = 5;              // fired down to 5 (a round still chambered)
            p.DebugMagSwap();        // TACTICAL reload -> keep the chambered round
            Check("tactical reload keeps the chambered round (Ammo=31 = mag+1)", p.Ammo == 31);
            Check("ammo conserved: spares now 46 (72 - 30 taken + 4 old back, chambered round stayed)", p.Inventory.getItemCount(6) == 46);

            p.Ammo = 0;              // fired dry -> chamber empty
            p.DebugMagSwap();        // EMPTY reload -> no +1 (the rack chambers one out of the fresh mag)
            Check("empty reload has no chambered bonus (Ammo=30, not 31)", p.Ammo == 30);

            // empty the bag of mags -> reload must be blocked
            for (byte b = 0; b < (byte)(PlayerInventory.PAGES - 2); b++) { var pg = p.Inventory.items[b]; for (int i = pg.getItemCount() - 1; i >= 0; i--) if (pg.getItem((byte)i)?.item?.id == 6) pg.removeItem((byte)i); }
            Check("no spare mag -> cannot reload", !p.DebugHasSpareMag());

            // masterkey: a BREAK-action double-barrel that feeds from loose 20 Gauge Shells (item 381, stack 32), no +1 chamber
            int Jars(ushort id) { int n = 0; var pg = p.Inventory.items[PlayerInventory.BACKPACK]; for (byte i = 0; i < pg.getItemCount(); i++) if (pg.getItem(i)?.item?.id == id) n++; return n; }
            p.LoadGun("res://content/masterkey.dat");
            Check("masterkey is a shotgun", p.DebugIsShotgun());
            Check("masterkey break-action is NOT shell-by-shell (loads together)", !p.DebugShellReload());
            Check("masterkey has no +1 chamber", !p.DebugHasChamber());
            Check("masterkey feeds from loose shells", p.DebugUsesShells());
            Check("masterkey fires 8 pellets (from the 20ga shell)", p.DebugPellets() == 8);
            bag.tryAddItem(new SDG.Unturned.Item(381, 20));
            bag.tryAddItem(new SDG.Unturned.Item(381, 20));   // 20 + 20 -> merges to 32, overflows 8
            Check("20 gauge shells stack (40 carried)", p.DebugCountShells() == 40);
            Check("shells cap at 32/slot (40 -> 32 + 8 = 2 stacks)", Jars(381) == 2);
            p.Ammo = 0;                 // both barrels empty
            p.DebugCompleteReload();     // one reload
            Check("masterkey loads BOTH barrels from the stack (Ammo=2)", p.Ammo == 2);
            Check("reload consumed 2 shells (40 -> 38)", p.DebugCountShells() == 38);
            // 12 Gauge Shells (item 113, caliber 8) are a DIFFERENT ammo type -> the caliber-16 masterkey ignores them
            bag.tryAddItem(new SDG.Unturned.Item(113, 32));
            Check("12 gauge shells don't count for the 20ga masterkey (still 38)", p.DebugCountShells() == 38);
            p.LoadGun("res://content/bluntforce.dat");
            Check("bluntforce (pump) feeds from loose shells", p.DebugUsesShells());
            Check("bluntforce is shell-by-shell (pump)", p.DebugShellReload());
            Check("bluntforce sees the 12 gauge shells (32)", p.DebugCountShells() == 32);
            Check("bluntforce fires 6 pellets (from the 12ga shell)", p.DebugPellets() == 6);

            // bolt/pump per-shot rechamber (source RechamberAfterShotCount): a bolt-action must cycle the bolt before firing again
            p.LoadGun("res://content/timberwolf.dat");
            Check("timberwolf (bolt) rechambers after each shot", p.DebugRechamberCount() == 1);
            p.DebugFireRechamber();
            Check("after a shot the bolt gun must cycle (blocked)", p.DebugNeedsRechamber());
            p.DebugRechamberTick(0.30);   // past RechamberAfterShotDelay -> the bolt-cycle animation starts
            p.DebugRechamberTick(0.60);   // past the cycle -> ready again
            Check("after cycling the bolt gun can fire again", !p.DebugNeedsRechamber());
            p.LoadGun("res://content/eaglefire.dat");
            Check("eaglefire (semi) does NOT rechamber per shot", p.DebugRechamberCount() == 0);
            p.DebugFireRechamber();
            Check("semi-auto never needs a per-shot cycle", !p.DebugNeedsRechamber());
            // world loot: magazines spawn FULL (master)
            Check("military mag spawns full as loot (30 rounds)", SDG.Unturned.Assets.makeLoot(6).amount == 30);
            Check("non-mag loot spawns as a single (1)", SDG.Unturned.Assets.makeLoot(13).amount == 1);

            // melee: the strong-swing multiplier + stamina come from the real .dat (source UseableMelee)
            string knifePath = ProjectSettings.GlobalizePath("res://content/knife_military.dat");
            if (System.IO.File.Exists(knifePath))
            {
                var mk = MeleeDef.FromDatText("knife_military", System.IO.File.ReadAllText(knifePath));
                Check("melee: knife strong-swing x1.5 (Strength)", System.Math.Abs(mk.Strength - 1.5f) < 0.01f);
                Check("melee: knife swing costs 15 stamina", System.Math.Abs(mk.Stamina - 15f) < 0.01f);
                Check("melee: knife is NOT Repeated (keeps normal weak/strong swings)", !mk.Repeated);
                // blowtorch: source "Repeated" + "Repair" flags (trailing bare flags past the Blueprints block, on a CRLF .dat) -> continuous HOLD tool, no weak/strong swing, no strong (RMB) attack
                string btPath = ProjectSettings.GlobalizePath("res://content/blowtorch.dat");
                if (System.IO.File.Exists(btPath))
                {
                    var bt = MeleeDef.FromDatText("blowtorch", System.IO.File.ReadAllText(btPath));
                    Check("melee: blowtorch parses Repeated (no weak/strong swing, you don't punch)", bt.Repeated);
                    Check("melee: blowtorch parses Repair (continuous heal, not damage)", bt.Repair);
                }
            }

            // gun-state persistence: a gun remembers ammo/firemode/mag on its backing item through hands<->inventory<->drop (master)
            var gunItem = new SDG.Unturned.Item(4);   // an Eaglefire item
            p.LoadGun("res://content/eaglefire.dat");
            p.DebugSetHeldItem(gunItem);
            p.Ammo = 17; p.DebugSetFiremode(2);       // fire some down + flick to Auto
            p.DebugSaveGunState();
            Check("item remembers the gun's ammo (17)", gunItem.gunAmmo == 17);
            Check("item remembers the fire mode (Auto=2)", gunItem.gunFiremode == 2);
            p.Ammo = 30; p.DebugSetFiremode(1);        // wipe live state (as if a fresh equip)
            p.DebugRestoreGunState(gunItem);
            Check("re-equip restores ammo (17)", p.Ammo == 17);
            Check("re-equip restores fire mode (Auto)", p.DebugFiremodeIdx() == 2);
            p.Ammo = 25; p.DebugRestoreGunState(new SDG.Unturned.Item(4));   // a fresh item has no saved state
            Check("fresh gun item keeps live defaults (no clobber)", p.Ammo == 25);
            GD.Print($"[MAGTEST] RESULT {pass} passed, {fail} failed");
        }

        // Crafting parser self-test: parse a real item .dat's Blueprints list -> print each (operation, inputs,
        // outputs, skill, station). Proves BlueprintDef reads the modern nested-GUID blueprint format end to end.
        static void RunCraftTest()
        {
            SDG.Unturned.ItemCatalog.RegisterAll();   // populates the item GUID->id map used to resolve blueprint ingredients
            string path = ProjectSettings.GlobalizePath("res://content/eaglefire.dat");
            if (!System.IO.File.Exists(path)) { GD.Print($"[CRAFTTEST] no .dat at {path}"); return; }
            string text = System.IO.File.ReadAllText(path);
            SDG.Unturned.IDatDictionary d = new SDG.Unturned.DatParser().Parse(text);
            var bps = BlueprintDef.ParseAll(d, "4");
            GD.Print($"[CRAFTTEST] eaglefire.dat -> {bps.Count} blueprint(s):");
            int resolved = 0, total = 0;
            foreach (var bp in bps)
            {
                GD.Print("[CRAFTTEST]   " + bp.ToString());
                foreach (var ing in bp.Inputs)
                {
                    total++;
                    var a = SDG.Unturned.Assets.findByGuid(ing.Guid);
                    if (a != null) { resolved++; GD.Print($"[CRAFTTEST]     in {ing.Guid.Substring(0, 8)} -> id {a.id} \"{a.itemName}\" x{ing.Amount}{(ing.Consume ? "" : " (tool)")}"); }
                    else GD.Print($"[CRAFTTEST]     in {ing.Guid} -> UNRESOLVED");
                }
            }
            GD.Print($"[CRAFTTEST] resolved {resolved}/{total} ingredient GUIDs -> item ids");

            // craft-LOGIC proof against a mock inventory, using eaglefire's real Repair blueprint (4 Metal Scrap + Blowtorch tool)
            var repair = bps.Find(b => b.Operation == "RepairTargetItem");
            bool logicOk = false;
            if (repair != null)
            {
                var inv = new Crafting.DictInv(); inv.Add(67, 4); inv.Add(76, 1);   // Metal Scrap x4 + Blowtorch x1
                bool can = Crafting.CanCraft(repair, inv, out string why);
                Crafting.DoCraft(repair, inv);
                GD.Print($"[CRAFTTEST] logic: CanCraft={can} ({why}); after craft scrap={inv.Count(67)}(exp 0) blowtorch={inv.Count(76)}(exp 1 tool-kept)");
                var inv2 = new Crafting.DictInv(); inv2.Add(67, 2); inv2.Add(76, 1);
                bool can2 = Crafting.CanCraft(repair, inv2, out string why2);
                GD.Print($"[CRAFTTEST] logic: CanCraft(only 2 scrap)={can2}(exp false) ({why2})");
                // outputs path: a synthetic Craft that turns 2 scrap -> 1 blowtorch
                var scrapA = SDG.Unturned.Assets.find(67); var torchA = SDG.Unturned.Assets.find(76);
                var inv3 = new Crafting.DictInv(); inv3.Add(67, 2);
                if (scrapA != null && torchA != null)
                {
                    var synth = new BlueprintDef { Operation = "Craft", Name = "synthetic" };
                    synth.Inputs.Add(new BlueprintDef.Ingredient { Guid = scrapA.guid, Amount = 2, Consume = true });
                    synth.Outputs.Add(new BlueprintDef.Ingredient { Guid = torchA.guid, Amount = 1, Consume = true });
                    Crafting.DoCraft(synth, inv3);
                    GD.Print($"[CRAFTTEST] logic outputs: 2 scrap -> craft -> scrap={inv3.Count(67)}(exp 0) blowtorch={inv3.Count(76)}(exp 1 produced)");
                }
                logicOk = can && !can2 && inv.Count(67) == 0 && inv.Count(76) == 1 && inv3.Count(76) == 1;

                // craft against the REAL grid PlayerInventory via the adapter (in-game integration)
                var pinv = new SDG.Unturned.PlayerInventory();
                pinv.tryAddItem(new SDG.Unturned.Item(67, 4));   // Metal Scrap x4
                pinv.tryAddItem(new SDG.Unturned.Item(76, 1));   // Blowtorch x1
                var padapt = new Crafting.PlayerInvAdapter(pinv);
                bool pcan = Crafting.CanCraft(repair, padapt, out _);
                Crafting.DoCraft(repair, padapt);
                GD.Print($"[CRAFTTEST] PlayerInventory: CanCraft={pcan}; after craft scrap={pinv.getItemCount(67)}(exp 0) blowtorch={pinv.getItemCount(76)}(exp 1 tool-kept)");
                logicOk = logicOk && pcan && pinv.getItemCount(67) == 0 && pinv.getItemCount(76) == 1;
            }

            // blueprint REGISTRY: load the pre-extracted catalog + list what's craftable from a stocked inventory
            int loaded = BlueprintRegistry.Load();
            var stock = new Crafting.DictInv(); stock.Add(67, 50); stock.Add(76, 1);   // 50 Metal Scrap + Blowtorch
            var applic = loaded > 0 ? BlueprintRegistry.Applicable(stock) : new System.Collections.Generic.List<BlueprintDef>();
            GD.Print($"[CRAFTTEST] registry: {loaded} loaded; {applic.Count} craftable from 50 scrap + blowtorch");
            for (int i = 0; i < System.Math.Min(6, applic.Count); i++) GD.Print("[CRAFTTEST]   craftable: " + applic[i]);
            GD.Print($"[CRAFTTEST] RESULT parse {bps.Count}bp, resolve {resolved}/{total}, craft-logic {(logicOk ? "PASS" : "FAIL")}, registry {loaded}bp");
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

        // Shotgun shell-by-shell reload self-test: verify Pump/Break guns are detected as ShellReload + show the
        // incremental load sequence (each shell cancelable by firing). Non-shell guns mag-swap (whole mag at once).
        static void RunShellTest()
        {
            foreach (var g in new[] { "masterkey", "eaglefire", "cobra", "grizzly" })
            {
                string path = ProjectSettings.GlobalizePath($"res://content/{g}.dat");
                if (!System.IO.File.Exists(path)) { GD.Print($"[SHELLTEST] {g}: (no .dat bundled)"); continue; }
                var gun = GunDef.FromDatText(System.IO.File.ReadAllText(path));
                GD.Print($"[SHELLTEST] {g}: Action={gun.Action ?? "?"} ShellReload={gun.ShellReload} AmmoMax={gun.AmmoMax}");
                if (gun.ShellReload)
                {
                    var seq = new System.Collections.Generic.List<int>();
                    for (int a = 0; a < gun.AmmoMax;) { a = System.Math.Min(a + 1, gun.AmmoMax); seq.Add(a); }
                    GD.Print($"[SHELLTEST]   loads shell-by-shell: {string.Join(" -> ", seq)} (firing mid-reload cancels + shoots what's loaded)");
                }
            }
            // catalog gun-wiring: gun items with gunName set are pick-up-equippable in-game (EquipHeldGun -> viewmodel)
            SDG.Unturned.ItemCatalog.RegisterAll();
            int wired = 0; var sample = new System.Collections.Generic.List<string>();
            foreach (var a in SDG.Unturned.Assets.all()) if (!string.IsNullOrEmpty(a.gunName)) { wired++; if (sample.Count < 8) sample.Add($"{a.id}={a.gunName}"); }
            GD.Print($"[SHELLTEST] equippable guns (gunName set): {wired} -- {string.Join(", ", sample)}");
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
            player.EquipHeldMelee(gunPath ?? "knife_military");   // --gun=<melee name> (knife_military | sledgehammer | machete...) -> real .dat range/damage
            AddChild(player);                       // _Ready builds the FP camera used to aim the swing
            player.GlobalPosition = new Vector3(0, 1.0f, 0);

            var z = new ZombieController { Target = player, Speciality = ZombieController.ESpeciality.NORMAL };
            AddChild(z);
            z.GlobalPosition = player.GlobalPosition + new Vector3(0f, 0.2f, -1.4f);   // dead ahead, in reach

            AddChild(new MeleeTestDriver { P = player, Z = z });
            GD.Print("[MELEE] demo: NORMAL zombie ~1.4m ahead; swinging the equipped melee weapon (weapon-specific dmg/range)");
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
        string _connectHost = "127.0.0.1";   // --connect=<ip>: the dedicated server to join (default = same-machine loopback)

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

            var cli = new Net.NetClient(_connectHost, NetPort);
            AddChild(new ClientNode { Client = cli });
            GD.Print($"[CLIENT] connected to {_connectHost}:{NetPort}; local player = real PlayerController (synced)");
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

        // --heartest: a zombie should react to the LOUDEST+CLOSEST sound it can hear (salience = loudness - dist),
        // ignoring sounds outside its HearingRange sphere or too quiet to carry that far (master's hearing rework).
        void RunHearTest()
        {
            var z = new ZombieController();
            AddChild(z);                       // _Ready: joins the "zombies" group, HearingRange 48
            z.GlobalPosition = Vector3.Zero;
            z.Hear(new Vector3(10, 0, 0), 12f);   // dist 10 <= 12 loud  -> heard, salience 2
            z.Hear(new Vector3(5, 0, 0), 6f);     // dist 5  <= 6  loud  -> heard, salience 1
            z.Hear(new Vector3(40, 0, 0), 48f);   // dist 40 <= 48 loud  -> heard, salience 8  (LOUD gunshot beats near footsteps)
            z.Hear(new Vector3(3, 0, 0), 2f);     // dist 3  >  2  loud  -> IGNORED (too quiet to carry)
            z.Hear(new Vector3(60, 0, 0), 64f);   // dist 60 >  48 range -> IGNORED (outside the ears)
            var (pos, sal) = z.DebugHeard();
            bool ok = pos.DistanceTo(new Vector3(40, 0, 0)) < 0.01f && Mathf.Abs(sal - 8f) < 0.01f;
            GD.Print($"[heartest] winner pos={pos} salience={sal:0.##}  (expected (40,0,0) sal 8)  -> {(ok ? "PASS" : "FAIL")}");
            GD.Print($"[heartest] loud gunshot(48@40m,sal8) beat near footstep(6@5m,sal1); too-quiet(2@3m) + out-of-range(64@60m) correctly ignored");
            // stay-on-task gate: while committed to a loud sound (salience 8), a quieter footstep must NOT override, but a louder shot must.
            bool ignoresFootstep = !z.DebugWouldOverride(8f, new Vector3(5, 0, 0), 6f);    // footstep salience 1 < 8 -> stays on task
            bool takesLouder     =  z.DebugWouldOverride(8f, new Vector3(10, 0, 0), 48f);   // gunshot salience 38 > 8 -> switches
            GD.Print($"[heartest] stay-on-task: ignores quieter footstep={ignoresFootstep}, switches to louder shot={takesLouder} -> {(ignoresFootstep && takesLouder ? "PASS" : "FAIL")}");
            GetTree().Quit();
        }

        // --armortest: worn clothing's whole-body protection aggregates as a PRODUCT of every worn piece (source
        // PlayerClothing), for fall (fallingDamageMultiplier) + explosion (explosionArmor). A bare player = 1.0 (no cut).
        void RunArmorTest()
        {
            SDG.Unturned.Assets.clear();
            SDG.Unturned.Assets.add(new SDG.Unturned.ItemAsset { id = 9001, itemName = "Test Vest", type = SDG.Unturned.EItemType.VEST, fallingDamageMultiplier = 0.5f, explosionArmor = 0.7f });
            SDG.Unturned.Assets.add(new SDG.Unturned.ItemAsset { id = 9002, itemName = "Test Hat",  type = SDG.Unturned.EItemType.HAT,  fallingDamageMultiplier = 0.8f });
            var inv = new SDG.Unturned.PlayerInventory();
            float bare = inv.FallingDamageMultiplier;                 // nothing worn -> 1.0
            inv.wearVest(new SDG.Unturned.Item(9001));
            inv.wearHat(new SDG.Unturned.Item(9002));
            float fall = inv.FallingDamageMultiplier;                 // 0.5 * 0.8 = 0.40
            float expl = inv.ExplosionArmor;                          // 0.7 * 1.0 = 0.70
            bool ok = Mathf.Abs(bare - 1f) < 1e-4f && Mathf.Abs(fall - 0.40f) < 1e-4f && Mathf.Abs(expl - 0.70f) < 1e-4f;
            GD.Print($"[armortest] bare={bare:0.##}  fall(vest.5 x hat.8)={fall:0.###}  explosion(vest.7)={expl:0.###}  (expect 1 / 0.4 / 0.7) -> {(ok ? "PASS" : "FAIL")}");
            GD.Print($"[armortest] a 50 m/s fall: {Mathf.RoundToInt(Mathf.Min(101f, 50f))} dmg bare -> {Mathf.RoundToInt(Mathf.Min(101f, 50f * fall))} dmg armored (x{fall:0.##})");
            // real data: RegisterAll loads the catalog + wires clothing_armor.tsv onto the actual items
            SDG.Unturned.ItemCatalog.RegisterAll();
            var boots = SDG.Unturned.Assets.find(1839);   // fall gear (Falling_Damage_Multiplier 0.05)
            var mil   = SDG.Unturned.Assets.find(2);       // armored top (Armor/Armor_Explosion 0.95)
            bool data = boots != null && Mathf.Abs(boots.fallingDamageMultiplier - 0.05f) < 1e-3f
                     && mil != null && Mathf.Abs(mil.explosionArmor - 0.95f) < 1e-3f;
            GD.Print($"[armortest] real data wired: id1839 fall={boots?.fallingDamageMultiplier:0.###}(exp .05)  id2 expl={mil?.explosionArmor:0.###}(exp .95) -> {(data ? "PASS" : "FAIL")}");
            // bone-proof: any worn piece with Prevents_Falling_Broken_Bones stops leg-break (source PlayerLife:2436)
            SDG.Unturned.Assets.add(new SDG.Unturned.ItemAsset { id = 9003, itemName = "Test Boots", type = SDG.Unturned.EItemType.PANTS, preventsFallingBoneBreak = true });
            var inv2 = new SDG.Unturned.PlayerInventory();
            bool bareBone = inv2.PreventsFallingBoneBreak;                 // nothing worn -> legs CAN break
            inv2.wearPants(new SDG.Unturned.Item(9003));
            bool bootBone = inv2.PreventsFallingBoneBreak;                 // boots on -> bones protected
            GD.Print($"[armortest] bone-proof: bare={bareBone}(want F)  boots={bootBone}(want T) -> {((!bareBone && bootBone) ? "PASS" : "FAIL")}");
            GetTree().Quit();
        }

        // --farmtest: a planted crop grows over FarmDef.Growth seconds, then harvest yields FarmDef.Grow (source InteractableFarm).
        void RunFarmTest()
        {
            SDG.Unturned.FarmRegistry.Load();
            bool isSeed = SDG.Unturned.FarmRegistry.IsSeed(330);            // Carrot seed
            SDG.Unturned.FarmRegistry.TryGet(330, out var carrot);
            var crop = new SDG.Unturned.PlantedCrop { Def = carrot, PlantedAt = 0.0 };
            bool freshOk = !crop.IsFullyGrown(5.0) && crop.Harvest(5.0) == 0;   // just planted -> not grown, no yield
            double t = carrot.Growth + 1.0;
            ushort yield = crop.Harvest(t);
            bool grownOk = crop.IsFullyGrown(t) && yield == carrot.Grow && yield != 0;   // grown -> yields Grow (329)
            float half = crop.GrowthFraction(carrot.Growth / 2.0);
            bool ok = isSeed && freshOk && grownOk && Mathf.Abs(half - 0.5f) < 0.01f;
            GD.Print($"[farmtest] {SDG.Unturned.FarmRegistry.Count} crops; seed330 growth={carrot.Growth}s grow={carrot.Grow}: fresh(nogrow,yield0)={freshOk} grown(yield{yield})={grownOk} half={half:0.##} -> {(ok ? "PASS" : "FAIL")}");
            GetTree().Quit();
        }

        public override void _Process(double delta)
        {
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
                        if (System.Environment.GetEnvironmentVariable("UG_SIDE") == "1")   // diagnostic PURE side profile (collider vs mesh height — pair with UG_COLLVIS=1)
                        {
                            var right = new Vector3(fwd.Z, 0f, -fwd.X);   // fwd rotated -90 about Y
                            _vehCam.GlobalPosition = vt.Origin + right * 12f + Vector3.Up * 1.4f;
                            _vehCam.LookAt(vt.Origin + Vector3.Up * 1.1f, Vector3.Up);
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
                            _vehCam.GlobalPosition = vt.Origin - fwd * 7.5f + Vector3.Up * 3.2f;
                            _vehCam.LookAt(vt.Origin + Vector3.Up * 0.7f, Vector3.Up);
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
            else if (++_frame < 6) return; // let the renderer settle
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

    public partial class MeleeTestDriver : Node3D
    {
        public PlayerController P;
        public ZombieController Z;   // re-anchored dead-ahead each frame so the swing always has an in-reach target (the demo player physics-drifts)
        int _frames; bool _done;

        public override void _PhysicsProcess(double delta)
        {
            _frames++;
            if (Z != null && P != null && !Z.Dead) Z.GlobalPosition = P.GlobalPosition + new Vector3(0f, 0f, -1.2f);   // keep it 1.2 m dead ahead of the (facing -Z) player
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
