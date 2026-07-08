# Unturned → Godot Port — Progress Log

Plan: UNTURNED_GODOT_PLAN.md (fable-5). Target: Godot 4 (C#/.NET 8, Jolt). Private project.
Approach: translate the readable U3-SDK source; rip Steam assets (AssetRipper) as swappable placeholders.
Repo: C:\claude-workspace\unturned-godot\ (this). Source+plan: C:\claude-workspace\archive\.
Mode (master 2026-07-08): keep moving autonomously, don't stop until commanded; 15m self-pace cron; decide+log, don't ask.

## Phase 0 — Foundations (in progress)
- [x] Prereqs on 4080: .NET 8.0.419, Godot (choco), retail Unturned install present (rip source).
- [x] Workspace + git repo.
- [x] **0b — NetPak (netcode) engine-agnostic: compiles standalone + 46 tests GREEN** (bit/angle/string/
      defer-read/overrun). Thesis proven at logic level. (Steamworks-ex + UnityNetPakTests still deferred.)
- [x] **0b — UnturnedDat (data/mod .dat layer) engine-agnostic: compiles standalone + FULL test suite GREEN
      = 1039 passed / 0 failed** — incl the UnityDatEx (Vector) + UnityDatColorEx (Color32) extensions.
      The modding backbone carries untouched. (InternalsVisibleTo added.)
- [x] **0c — SDG.Compat harness: Mathf + Vector2/3/4 + Quaternion + Color + Color32 shims (namespace
      UnityEngine). PROVEN CORRECT by the game's own UnityDatEx/ColorEx tests passing against them.**
- [x] **TALLY: 1085 tests green standalone** (NetPak 46 + UnturnedDat 1039). The engine-agnostic core carries.
- [ ] Un-defer NetPak UnityNetPakTests (Vector/Quat now available) + Steamworks-ex (needs Steamworks.NET)
- [x] **Godot 4.6.2 mono skeleton BUILDS + RUNS the ported core in-engine.** Headless run proof: NetPak 0xABC r/w True, DatParser parsed (2 keys), Unity->Godot adapter Z-flip (1,2,3)->(1,2,-3). game/ = Godot .NET proj refs SDG.Compat+NetPak+UnturnedDat + GodotCompat adapter + Main smoke node. Jolt physics set.
- [x] **0a rip station OPERATIONAL** — AssetRipper 1.3.14 (win-x64) runs headless as a persistent SYSTEM
      scheduled task ("ARserver", port 5556), driven over its REST API (POST /LoadFile|/LoadFolder,
      /Export/UnityProject; form field `path`; /Reset). KEY FINDING: the retail install's base structure
      (globalgamemanagers + level0-10) holds only engine/UI/post-processing — the actual game content is
      ONE packed AssetBundle: **Bundles/core.masterbundle (112 MB)**. Ripping it → 28,012 files:
      **6,205 prefabs, 4,544 meshes, 5,482 textures (.png), 1,012 materials, 14,004 .meta (GUIDs preserved)**.
      The Bundles/ .dat files are UnturnedDat defs that reference these assets BY GUID (the swap seam).
- [x] **0d converter v0 + ContentProvider** — Free edition only bulk-exports meshes as Unity YAML
      (.asset, !u!43, serializedVersion 11, interleaved _typelessdata). So `tools/unity_mesh_to_obj.py`
      decodes that natively (m_Channels layout → pos/normal/uv Float32/Float16, m_IndexBuffer LE u16/u32),
      handedness Z-flip + winding-reverse, → Wavefront .obj. **VALIDATED byte-exact: decoded bbox == the
      Unity-declared localAABB to 3 decimals.** `game/ContentProvider.cs` = GUID→asset map (manifest.json),
      runtime .obj→ArrayMesh via SurfaceTool (runtime load, not editor-import — the shipping model).
- [x] **★ PHASE-0 GATE PASSED ★** — headless Godot proof: a REAL ripped prop **Aprix_Mask_0** (from
      core.masterbundle) instantiated as a MeshInstance3D in a live Godot scene, resolved through
      ContentProvider **BY ITS ORIGINAL UNITY GUID fb9428c7b8df82e4eb9642dacfaf9567**. verts=144 (48 tris),
      **aabb.size (0.521, 0.439, 0.253) == exactly 2× the Unity extent** — geometry byte-correct in-engine.
      The whole approach (readable-source core in Godot + Steam-asset rip keyed by original GUID) is PROVEN.

## Phase 0 — DONE. Pipeline proven end-to-end. Next: Phase 1 vertical slice.
- [x] **full-tree converter run** (tools/batch_convert.py, on 4080): over all 4,544 ripped meshes →
      **4,458 converted OK (98.1%)** to .obj + a master GUID→asset manifest (4,458 entries, GUID from each
      .meta). Edge cases parked for converter-v1: **28 mesh-compressed** (m_CompressedMesh packed stream),
      **57 multi-stream** (vertex data across >1 stream), 1 degenerate 0-vertex (Plane_2). 0 skinned
      (Unturned meshes are all static — empty m_BindPose). Converted tree + manifest live on the 4080 at
      `ripped-mb\converted\` (the asset store; NOT git — derived + large). Repo carries the TOOL + a slice.
- [x] **CATALOG GATE** — ContentProvider generalized to read any content root (Godot FileAccess for
      res://|user://, System.IO for the absolute external asset store). Headless `-- --catalog=<manifest>`:
      loaded a 200-GUID sample from the full 4,458-entry manifest → **200/200 OK, 78,363 verts / 26,121
      tris**, zero failures. The GUID→real-geometry pipeline scales to the whole catalog.
- [ ] un-defer NetPak UnityNetPakTests + Steamworks-ex

## Phase 1 — Vertical Slice (STARTED). Scope (from plan §5): ~150×150m ripped-prop level → ported
## movement/look/stance → 1 gun end-to-end (ItemGunAsset.dat→UseableGun raycast→damage→death) → 1
## direct-chase zombie w/ ported anims → minimal HUD on Glazier_Godot v0 → 2 players on a headless
## dedicated server over SystemSockets w/ NetPak RPCs. Foundations first, then fan out.
- [x] **Grounded the sim constants from retail ProjectSettings** — Fixed Timestep **0.02s (50 Hz)**,
      Maximum Allowed Timestep 0.33s (TimeManager); full 32-entry **layer table** (TagManager, all slots
      verified) → `core/UnturnedSim/Layers.cs` (LayerMasks indices + RayMasks bitmasks, faithful port of
      U3-SDK ELayerMask/RayMasks). Composite combat masks (DAMAGE_*/BLOCK_*) parked for the gun step.
- [x] **SimRoot tick spine (plan 0c) — `core/UnturnedSim/` (engine-agnostic) + 7 tests GREEN.**
      SimClock = deterministic fixed-timestep accumulator at 50 Hz (epsilon-guarded FP boundary, 0.33
      clamp vs spiral-of-death); SimRoot ticks registered ISimStepped systems in order w/ consecutive
      tick numbers; `game/SimDriver.cs` drives it from Godot _PhysicsProcess on its OWN 50 Hz clock
      (decoupled from Godot's tick = same as the dedicated server). Movement/AI/combat/replication hang here.
      Godot project rebuild GREEN with UnturnedSim referenced.
- [x] **Player movement/stance ported** — faithful from PlayerMovement.cs: `PlayerMovementDef` (heights
      2/1.2/0.8, speeds STAND 4.5 / SPRINT 7 / CROUCH 2.5 / PRONE 1.5, JUMP 7, GRAVITY 9.81×3=29.43,
      terminal −100, EPlayerStance order) + `PlayerMovementSim` (engine-agnostic velocity integrator).
      **11 movement tests GREEN** (stance speeds exact, diagonal clamp, jump apex in the Unturned band,
      terminal velocity, forward dist = speed×time, determinism) → 18 UnturnedSim tests total.
      `game/PlayerController.cs` = Godot CharacterBody3D (WASD/sprint/crouch/jump + mouse look) on Jolt;
      project.godot physics set to **50 Hz** to match retail. FIDELITY: constants exact, trajectory is
      "recognizably Unturned + tunable" (cross-engine PhysX→Jolt can't be byte-equal — accepted plan risk).
      Godot build GREEN. (Visual proof pending a render harness.)
- [x] **RENDER HARNESS working — first VISUAL** (`Main.cs --shot=<png>` mode). Builds a lit showcase
      (WorldEnvironment sky + DirectionalLight + shadows + ground + camera) of N real ripped props loaded
      via ContentProvider from the catalog, and saves a PNG. Runs on the 4080's real GPU over SSH via
      `godot --rendering-driver opengl3 --write-movie <avi> --fixed-fps 10 --quit-after 20` (forces the
      frame loop; captures the viewport in _Process). Confirmed: "OpenGL API ... RTX 4080 SUPER", 10 real
      props rendered + shadowed at 1280×720. Sent master the screenshot. (Grey = textures/materials not
      wired onto meshes yet.) Recipe banked for all future visual checks.
- [x] **TEXTURES wired** (master steer: "textures then playable"). Traced the asset chain prefab →
      MeshRenderer material → `_MainTex` → atlas `.png`; `tools/build_texture_map.py` composes
      **mesh_guid → albedo .png (2104 meshes mapped)** across the whole tree. ContentProvider loads the
      texture manifest + serves `GetTexturePath(guid)`; showcase loads each atlas PNG at runtime
      (Image.LoadFromFile → ImageTexture → StandardMaterial3D albedo). Meshes already carried UVs from the
      converter, so atlas mapping lands correctly. Rendered 10/10 textured — recognizable items (evil-eye
      amulet w/ iris+pupil, red canister, gold vest). Sent master. Unturned = shared atlas textures.
- [x] **Winding/"inside-out" investigated (master flagged) — NOT a bug.** Double-sided render = zero
      change (fronts weren't culled), and a Maplestrike rifle + Fireaxe + Crate all render solid +
      recognizable (a global winding flip would invert those too). The odd look was thin/hollow shell
      meshes (open-bottom helmets, frames) + a random-props demo w/ no scale. Added `--pick=<names>` to the
      shot harness (render named items big/clear via ContentProvider.FindGuidByName). Texture map v2 pairing
      re-confirmed correct-by-construction. (One outlier: a Crewman helmet reads messy — open shell + maybe
      a wrong-tinted atlas region; not chased yet.)
- [x] **★ PLAYABLE SLICE WORKING ★** (master: "just get the playable slice working"). `game/` now has:
      `PlayerController` (ported movement + mouse look + hitscan gun: raycast from camera vs the zombie
      collision bit → Damage), `ZombieController` (direct-chase AI on 50 Hz, collision capsule, flash-red-on-
      hit, topple-on-death, "zombies" group), `HUD` (crosshair + AMMO/KILLS), `DemoDirector` (scripts
      aim+fire+horde-respawn for a recorded clip). `--play` = interactive (WASD/mouse/LMB); `--demo` =
      recorded. Rendered an 8s clip on the 4080 GPU (--write-movie → mp4): a horde of green zombies chase,
      get shot (→red→topple), KILLS + AMMO tick live. THE CORE LOOP RUNS. Sent master the clip.
      Gaps (next): gun is a generic hitscan (not yet ItemGunAsset.dat-driven damage/ammo/spread); zombies
      are capsules (no ripped mesh/anim yet); no melee-damage-to-player; single-player (no netcode yet).
- [x] **Gun data-driven from the real ItemGunAsset .dat** (closes "1 gun end-to-end"). `game/GunDef.cs`
      reads a gun .dat through the ported UnturnedDat layer using the game's OWN accessors
      (ParseFloat/ParseInt32/GetString). Default = retail **Eaglefire**: loaded live in the demo →
      `[gun] 4: zombieDmg=99 playerDmg=40 range=200 firerate=4(0.080s) mag=30`. PlayerController.Fire now
      uses .dat damage/range + self-limits to the .dat firerate (`--gun=<path>` swaps weapons). Demo horde
      → 14 kills in 8s w/ the real Eaglefire ROF. The ported data layer now feeds live gameplay.
- [x] **Player damage / death / respawn — two-way survival loop VALIDATED.** ZombieController melees the
      player in range (AttackDamage on an interval); PlayerController.TakeDamage → death → respawn at spawn;
      HUD shows HP/AMMO/KILLS/DEATHS. Verified on screen: HP dropped 100→40 and DEATHS hit 1 as a horde
      swarmed (had to let the demo director spare point-blank zombies — its aimbot was killing melee ones
      before they landed a hit; the mechanic itself was correct). It's a real fight now, not a shooting gallery.
- [~] Zombie ripped mesh — INVESTIGATED, DEFERRED to Phase 2. There is no single humanoid "zombie body"
      mesh: Unturned characters are MODULAR SKINNED meshes (base body + separate clothing/head parts on a
      shared skeleton). `Player.002` = a torso/shirt piece, `Richard_Head_1` = a head part (rendered + confirmed).
      A real ripped zombie needs the modular character assembly + rig + skinning — that's the plan's Phase-2
      character/anim track, not a quick capsule swap. Capsule placeholders stay for the slice (honest).
- [x] **★ NETCODE SPINE WORKING ★** (master: "keep going"). Ported `SDG.NetTransport` (the real interface
      lib: IServerTransport/IClientTransport/ITransportConnection/ENetReliability + IPv4Address) — builds
      standalone. Wrote `UdpNetTransport` (UdpServer/Client/Connection) = the plan's transport REWORK: a
      standalone UDP impl of those exact interfaces, no Steam dep, poll-based to match. **Round-trip test
      GREEN**: a NetPak-packed player state (tick + pos x/y/z as float bits) crosses a REAL UDP socket
      client→server through the ported interfaces, unpacks byte-exact, and the server replies down the
      ITransportConnection → client receives it. So ported NetPak (real source) + the real transport
      interfaces + the UDP rework = the 2-player networking spine, proven over real sockets.
- [x] **★ 2-PLAYER SYNC — server-authoritative, tested + VISUALIZED ★**. `core/UnturnedNet` (NetServer +
      NetClient + PlayerState over NetPak): clients send state each tick → server assigns ids + broadcasts
      the world → clients apply it. **Headless test GREEN**: two clients see each other's positions through
      the server over real UDP. Then wired into Godot (`--netdemo`, NetDemoNode): a real NetServer + 2 real
      NetClients on loopback UDP, rendering a capsule per synced player — BLUE (local id 1) + ORANGE (remote
      id 2), the orange one's position having travelled bot→server→client over sockets+NetPak. Rendered an
      8s clip on the 4080 GPU, sent master. The 2-player networking loop is real + on screen.
- [x] **★ TRUE CROSS-PROCESS 2-PLAYER ★**. `--server` (headless dedicated server + bot) and `--client`
      (rendering) are now separate Godot processes. Ran both on the 4080: `[SERVER] ... udp 47872` +
      `[CLIENT] connected to 127.0.0.1:47872`, and the client rendered BOTH players (orange = the server's
      bot from the OTHER process, blue = local) — the orange's position crossed real UDP between two OS
      processes. 8s clip sent master. So the networking is real cross-process, not co-located.
- [x] **★ NETWORKED SURVIVAL CORE ★**. Extended the protocol: WorldState now carries players AND zombies;
      NetServer runs an **authoritative zombie sim** (horde spawns, each chases the nearest player) + broadcasts
      it. Two-process run on the 4080: headless `--server` runs the zombie sim, rendering `--client` shows
      BLUE (local player) + ORANGE (remote player, other process) + a **horde of GREEN zombies** chasing them
      — all server-authoritative, synced over UDP. So it's a real networked multiplayer survival core:
      netcode + server-side sim + players + zombies, cross-process, on screen. Net test still green (zombie
      count 0-safe). Sent master. THE WHOLE STACK COMES TOGETHER over the network.
- [x] **Server-authoritative hit-reg — networked COMBAT loop closed**. Added a Fire message (origin+dir);
      the client auto-fires at the nearest zombie → server does a math ray-vs-zombie Hitscan (no engine
      physics on the dedicated server) → removes the hit zombie + counts the kill. Two-process demo: the
      rendering client shoots the SERVER's zombies and the horde thins to zero (server-authoritative). So
      the full loop is networked: server spawns+chases zombies → client fires → server hit-regs → world
      updates → client sees the horde die. Sent master.
- [x] **Real PlayerController wired into the networked client**. Added a Welcome msg (server → client id;
      NetClient.SelfId) + PlayerController.ScriptedInput/CaptureMouse hooks. The client's local player is now
      a REAL PlayerController (the ported movement physics on Jolt, not a scripted orbit): it kites the
      nearest zombie + fires → server hit-reg. Renders blue=self (via SelfId) / orange=remote / green=server
      zombies. Demo drives it via ScriptedInput; a human `--client` run uses real WASD/mouse (flip off the
      scripting). So the networked player moves with the actual ported movement. Net test still green.
- [x] **Capsules → HUMANOID models + per-limb HITBOXES** (master: "swap caps for actual player models +
      the hitboxes involved"). `game/Humanoid.cs` = a blocky low-poly humanoid (head/torso/arms/legs) —
      Unturned's actual angular style — used for BOTH players (blue=self, orange=remote) and zombies (green).
      **Per-zone server hit-reg**: NetServer.Hitscan is now ray-vs-vertical-cylinder → hit HEIGHT picks the
      zone (head 3x / torso 1x / legs 0.6x) → damage to that zombie's HEALTH (100); the box layout mirrors
      the hitbox math so the visual model IS the hitbox. Fire msg carries damage; client aims headshots.
      Feet-based coords throughout. Plus ripped-prop scenery (crates/structures). 2-process networked render
      shows both-humanoids + horde + scene. HONEST: blocky stand-in for the full modular-skinned ripped
      character (Phase 2 to assemble); the zone/hitbox gameplay is real. Net test green.
- [x] **REAL ripped character model in-game** (master: "get the real models"). KEY UNLOCK: extended the
      mesh converter to handle **MULTI-STREAM (skinned) vertex data** (positions in stream 0, bone weights in
      stream 1) — the 57 skinned meshes now convert (4515 total, was 4458). Model_0_84 = the actual Unturned
      Character body; renders as the real low-poly humanoid (T-pose bind pose). `game/CharacterModel.cs`
      loads it once + `Build(tint)`; ClientNode uses it for BOTH players (blue/orange) and zombies (green),
      replacing the blocky stand-in. Fixed master's "inside-out" catch: skinned meshes wind opposite the
      static convention → material is double-sided → renders solid. size (2.25×1.96×0.40), scaled to ~1.8m,
      feet on ground. HONEST: bind-pose T-pose (no skeleton animation yet), and no skin texture mapped
      (Model_0_84 not in the texture map) so it's tinted. But it's the REAL ripped character.
- [x] **PORTABLE PLAYABLE BUILD** (master: "playable build w/ the launcher"). Made the game self-contained:
      bundled the ripped **character mesh (res://content/character.txt as raw .obj text so it packs)**, a
      **crate** prop, the **Eaglefire .dat**, into res://content/ — no more 4080-absolute paths. Loaders
      made portable: CharacterModel.LoadBundled, PlayerController.LoadGun reads res://, ContentProvider.ParseObj
      public. Default (no args) now boots **interactive single-player survival**: FP player (WASD/mouse/LMB/
      Space, real ported movement) + `HordeSpawner` (real character-model zombies chasing) + crate cover +
      HP/AMMO/KILLS/DEATHS HUD. Verified it boots + renders (assets load from res://). Created UnturnedGodot.sln
      (Godot .NET export needs it) + export_presets.cfg (Windows, embed_pck, embed .NET, content/ included).
      Exporting to build/UnturnedGodot.exe now.
- [x] **★ PLAYABLE BUILD SHIPPED ★**. Godot-exported UnturnedGodot.exe (185MB, embed_pck + embedded .NET,
      self-contained), verified it runs standalone (assets from the embedded pck). DISTRIBUTION saga: the
      4080↔cowtools link drops on any transfer >few MB (MAC errors), cowtools disk tight (336M), no gh/remote.
      Solved by SPLITTING the 67MB zip into 17×4MB chunks, transferring each in its own connection (survives
      the drops) + reassembling byte-EXACT on cowtools (verified: 70584217==70584217, unzip -t OK). Hosted on
      my caddy tunnel: **https://catboy.cowtools.uk/unturned/UnturnedGodot-win64.zip** (public HTTP 200).
      Sent master the link + controls (WASD/mouse/LMB, single-player survival vs the character-model horde).
      The whole slice is now a downloadable playable game.
- [x] **DIFFERENTIAL LAUNCHER (git-pull, BH pattern)** (master: "do it how the blockheads launcher did it").
      Scrapped the zip/hash approach. Pushed the repo to **github.com/strawberry-cow38/unturned-godot** (private).
      Bootstrapping saga: the 4080 CAN'T push (its git cred-manager=wincredman needs a desktop TTY, dies over
      ssh) — so `git bundle --all` (4.3MB) → chunk-transferred to cowtools (even 4MB drops on this link) →
      cloned + `gh repo create`+push from cowtools (gh authed as strawberry-cow38 there). `launcher/` rewritten
      to git-pull the repo → dotnet build game/UnturnedGodot.sln → godot --import → godot --path game. So a
      build update = only the changed git objects (KB), never the full game. Built the 160KB framework-dependent
      launcher exe on cowtools + sent master. Requires on the host: git + .NET 8 SDK + Godot 4.6.2 mono. Pushed ba670e5.
      NOTE: 4080 repo (build workspace) diverges from GitHub now — git ops go through the cowtools clone (has gh auth).
- [ ] NEXT: master tests the launcher (couldn't fully e2e it — clone needs their creds, run needs a display);
      then skeleton animation (out of T-pose); skin texture on the character; real Zombie AI + UseableGun.
- [x] **VIEWMODEL — source-accurate horizontal offset (2026-07-08)** (master: "is the right hand offset source accurate?").
      No. My earlier +0.22 right-shift was eyeballed. Verified against the FULL decompile: base
      `viewmodelCameraLocalPositionOffset` = `Vector3.zero` (PlayerAnimator.cs:1653); the only knobs
      `ViewmodelPreferenceData.Offset_Horizontal/Vertical/Depth` default 0 (constructor) and have NO menu UI
      wiring them (across the whole ripped tree `Offset_Horizontal` appears ONLY in PlayerAnimator +
      ViewmodelPreferenceData — prefs-JSON fields deserialized at Provider.cs:6514, effectively a hard 0 for
      every player, NOT the "accessibility slider" I first claimed). The ONLY baked-in position constant is
      `- 0.45f` on the VERTICAL axis (PlayerAnimator.cs:1431 — the gun sits LOW, no horizontal component).
      Right-handedness is the RIG itself (right arm holds the gun); lefties mirror the whole viewmodel via
      `localScale.x = -1` (PlayerAnimator:1613), never a lateral shift. FIX: `Viewmodel._armsPos.X` 0.22 → 0.
      Rendered the --vm strip to confirm: Eaglefire frames lower-right off the rig alone, barrel forward,
      front sight at the muzzle. Pushed ed6a683.
- [x] **VIEWMODEL — ADS / aim-down-sights (2026-07-08)** (master: "next is ADSing, build off source").
      Hold RMB to aim. Derived from UseableGun.startAim/stopAim + PlayerAnimator.GetAimingViewmodelAlignment:
      aim-in blends over Aim_In_Duration (Eaglefire.dat = 0.25s) with the source smootherstep-squared ease
      (GetInterpolatedAimAlpha: 1-(1-smootherStep01(t))^2); sway -> 0.1x (viewmodelSwayMultiplier); IRON SIGHTS
      DON'T ZOOM FOV (startAim -> enableZoom(1.0) for a scopeless first-person gun; FOV = zoomBaseFOV/factor,
      factor 1.0 = unchanged — only actual scopes magnify); the gun's aimHook raises onto the view axis (the
      +0.45 eye-raise cancels the -0.45 hip drop) scaled by alpha. ADS pose offset tuned to land THIS rig's
      sight on the axis (align-to-center is the source op), render-verified down the irons. Pushed c15f383.
      TODO polish: additive Aim_Start pose-blend (my aim clip is additive->T-pose, skipped for now) + gameplay
      couplings (Spread_Aim 0.05, aimingMovementSpeedMultiplier) once the fire model grows spread/move-speed hooks.
- [x] **VIEWMODEL — ADS equip gate + equip-loop fix (2026-07-08)** (master caught it: "can't ADS while pulling out, check src").
      CONFIRMED in source: UseableGun.ReceivePlayAimStart/Stop BOTH guard on player.equipment.IsEquipAnimationFinished
      = (Time >= equipStart + GetAnimationLength("Equip"), PlayerEquipment.cs:269/1633). No aim start/stop until the
      pull-out finishes. Viewmodel.SetAiming(true) is now ignored until the Gun_Equip clip length elapses (Eaglefire
      equip = 1.633s); tracks _equipElapsed, exposes IsEquipComplete. ALSO fixed the equip clip LOOPING (extractor
      marks non-Attack/Startle/Jump as loop -> Gun_Equip secretly re-raised); RiggedCharacter.SetClipLoop makes it
      play ONCE + hold the ready pose. Re-tuned _armsPosADS vs the corrected post-equip hold (the earlier crisp shot
      was aiming mid-raise, which the gate now correctly forbids; pose is sensitive near eye height — breech hits the
      lens if raised too far). --vm demo rebuilt data-driven: equip->settle->ADS->release->hip, no recoil. Pushed c78746c.
- [x] **VIEWMODEL — ADS aligns the REAL View-hook offset (2026-07-08)** (master: "ads too high + not centered, check src for offsets").
      Was eyeballing the ADS pose (the sin master keeps catching). Pulled the Eaglefire's ACTUAL model from the
      master bundle via the AssetRipper server (collection I:13): a bare gun has no aimHook -> aligns on the "View"
      hook (Attachments.cs viewHook fallback), gun-local (0, -0.7706, 0.1337) Unity = (0,-0.7706,-0.1337) Godot
      (unity_mesh_to_obj negates Z). Cross-checks against the gun mesh bounds (x[-.112,.073] y[-.616,.731] z[-.147,.193]):
      x=0 dead-centered, z=.1337 at the top (iron sight), y=-.77 at the rear (eye ref). A marker Node3D sits at the
      hook on the gun mesh; the arms move so it lands on the camera aim axis (x=0,y=0 = source-exact per
      GetAimingViewmodelAlignment), scaled by aim alpha. AdsSightDepth = the forward viewing distance (honest
      stand-in for the Aim_Start arm-extend anim, which is additive + not yet pose-blended). Render-verified:
      dead-centered, looking down the irons. Pushed b8dd932.
      HOOK-EXTRACTION RECIPE (reuse for other guns/offsets): AssetRipper HTTP `Search/View?q=<HookName>` -> filter
      the result links to the gun's collection index -> the hook GameObject's single component is its Transform ->
      `Assets/Yaml?Path={"C":{"B":{"P":[]},"I":<coll>},"D":<transformPathID>}` -> read m_LocalPosition. Gun models
      are all named "Model_0"; find the gun's collection by probing mesh pathID across collections.
- [x] **VIEWMODEL — real additive Aim_Start layer + ADS distance finding (2026-07-08)** (master: "real fix now!!!!!", no flat depth).
      Built the genuine additive ADS layer: RiggedCharacter bakes Gun_Aim's per-bone delta (end vs its own
      frame 0 = the additive reference), switches the arms' AnimationPlayer to Manual advance, and applies that
      delta over the base hold pose scaled by AimBlend (= aim alpha) in Tick (base-then-additive order). Verified
      Gun_Aim alone renders as the classic arm-stuck-out additive-delta pose. KEY FINDING: Aim_Start is a SUBTLE
      hand/shoulder refinement, NOT an arm-extend — its keyframe deltas are small, so it does NOT drive the ADS
      distance. The distance is the source camera->View-hook alignment, and the View hook is the REAR sight, so
      aligning it exactly to the eye parks the gun's breech in your face (a black block in our viewmodel;
      Unturned's own model geometry hides it in-game). Kept a small forward readability offset (AdsSightDepth
      -0.30), now HONESTLY labeled as a readability call, not source. x/y sight centering still source-exact from
      the real hook. Pushed 591b2c1. OPEN Q for master: accept the small offset, or go pure-source (the block)?
- [ ] **VIEWMODEL — ADS distance root cause (2026-07-08, investigation)** (master: "whats diff between ours and source?").
      Did the full camera-move restructure (arms un-parented from the camera -> siblings under the SubViewport;
      camera slides to the View hook, gun holds a FIXED hip position + aims along world -Z; the RiggedCharacter aim
      additive kept). SOURCE-PURE mechanically (no gun-push), BUT it exposed the real root cause: my hip arm pose
      holds the gun too far BACK — measured the View hook at z=+0.32 (0.32 BEHIND the eye at hip; the stock pokes
      past the face). So the camera has to pull back behind the breech to look "through" the rear sight -> you see
      the receiver block, not down the sights. Real Unturned's hip/aim arm pose holds the gun forward + raised so
      the sight comes to the eye. The pushed 591b2c1 (additive + a small forward "AdsSightDepth") looks clean
      because that offset is really EYE RELIEF (the eye sits behind the rear sight) — the restructure PROVED a
      viewing distance is genuinely needed, so it's not a hack. DECISION PENDING master: lock in the clean 591b2c1
      (relabel the offset as eye-relief) vs invest in replicating Unturned's real hip/aim arm poses (bigger task).
      Restructure code parked locally + on the 4080 (NOT pushed); GitHub stays at the clean 591b2c1.
- [ ] **VIEWMODEL — ADS proper-fix, deep source dig (2026-07-08)** (master: "just do it properly").
      Went source-pure on the viewmodel camera. FOUND the real eye: the source viewmodel camera is a child of the
      SKULL bone at localPosition (-0.45,0,0) rot 90deg-Z (PlayerAnimator line 1649: firstSkeleton/Spine/Skull/
      ViewmodelCamera — that's the actual -0.45, pulled from resources.assets I:8 pathID 2145/transform 2679).
      Confirmed the gun mount is source-correct (Euler(0,0,90) on the hook = identical placement to my view-space
      aim). The "View" hook (I:13 pathID 501) is the aiming EYE VIEWPOINT (behind the gun), and source ADS moves
      the camera exactly onto it (GetAimingViewmodelAlignment). Anchored our camera to the real skull eye (BoneAttachment
      on Skull, +(-0.45) along skull-X) + move it to the View hook on ADS. WALL: doing that exactly, the eye ends
      dead-behind the gun and our Eaglefire mesh's receiver/stock (a chunky octagon) fills the view -> block. The
      source MECHANICS are all correct now; Unturned's exact model geometry (sight-line height over the receiver,
      possibly mesh scale) is what keeps it clean in-game, and matching that is a deeper mesh job. Skull-eye code
      parked locally + 4080 (NOT pushed); GitHub stays at the clean 591b2c1 (additive + a small viewing offset that
      reads fine, down-the-sights). PENDING master: keep drilling the pure-source look vs lock in the clean version.
- [ ] **VIEWMODEL — iron-sight finding + convention wall (2026-07-08)** (master: "get the eaglefire irons on there").
      Investigated getting the Eaglefire irons on the gun to fix the ADS receiver-block. FINDINGS: the irons are
      NOT a separate extractable mesh — the gun's a modular model where the "Sight"/"Barrel" objects (I:13 D:395/394)
      are mount HOOK transforms (Sight at gun-local (0,-0.182,0.134)), not geometry. Also pathID 1156 is a
      mislabeled MeshRenderer sitting on a "Foliage_0" GameObject (306, mesh 236) — so my eaglefire_gun.txt
      provenance is murky (it renders as a gun + reads right at hip, but isn't cleanly 1156). My body mesh has a
      rear receiver bump (top verts ~z=0.19 at y=-0.18) but NO front sight post. So "irons on there" = MODELING a
      front post + rear aperture at the source hook positions, not extraction. Deeper blocker: the gun's up-axis /
      z-flip convention keeps biting (View hook z-flip lands the eye on the wrong side of the receiver -> the
      block). Doing it right = a focused ground-up pass: lock the gun's coordinate frame, model source-positioned
      irons, seat the eye on the sight line. PENDING master: dedicated pass vs lock in the clean 591b2c1 + move to
      reload. GitHub stays at clean 591b2c1; skull-eye/mount experiments parked locally + 4080.
- [x] **VIEWMODEL — ADS iron sights DONE (2026-07-08)** (master: "get the eaglefire irons on there" -> "just keep going").
      Resolved the ADS. The irons aren't extractable (modular hook model), so MODELED them: a front post at the
      muzzle + a rear aperture ring (TorusMesh) on the receiver, both on the sight line. KEY unlock: the gun mesh
      up-axis is -z (barrel +y) in the view-space-aimed frame — nailed empirically with bright diagnostic sights
      (mesh +z rendered DOWN, so all my earlier mesh-frame guesses were upside-down; that convention was the whole
      wall). The ADS eye viewpoint (ViewHookLocal, behind at the sight height) looks through the ring at the post
      -> proper down-the-sights, no receiver block. Built on the clean 591b2c1 base (view-space aim + additive Aim_Start
      + the AdsSightDepth viewing distance). Pushed c1cb7ad. The skull-eye/camera-slide/source-mount experiments were
      parked (not used) — the clean base + modeled irons is the shipped approach.
- [ ] **VIEWMODEL — Eaglefire irons = a default SIGHT ITEM (2026-07-08, mechanism FIGURED OUT)** (master: "thats a proc model, FIGURE OUT HOW ITS DONE").
      Cracked how the Eaglefire irons work (the real question): they're NOT part of the gun mesh — they're a separate
      default SIGHT ATTACHMENT. Eaglefire.dat has `Sight 5`, and item ID 5 = `Eaglefire_Iron_Sights` (GUID
      ec62d401a5a94d87a04564cc5705c026, Bundles/Items/Sights/Eaglefire_Iron_Sights/). The game mounts THAT item's
      model at the gun's Sight hook (0,-0.184,0.17) via Attachments.cs Instantiate(sightAsset.sight) — same path a
      scope takes. That's why my extracted gun mesh has no irons and why my modeled fakes (c1cb7ad) were wrong
      (master rejected them). BLOCKER: the actual iron-sight MESH is a generic Model_0 among 200+ item models in the
      master bundle; the HTTP asset-browser can't pin it (the sight bundle's Aim hook isn't in the loaded AssetRipper
      session, so it may not be loaded). Need a clean extraction (AssetRipper full named export, or the exact model
      location) then mount at the hook — the mount + hook are already wired. PENDING master: kick off the export vs
      point me to the model. NOTE: c1cb7ad on main still has the REJECTED fake sights — replace with the real model.
- [x] **RELOAD (basic mechanic) (2026-07-08)** — non-blocked progress while the iron sights await master's extraction call. R to reload: PlayerController blocks firing + timer = ReloadTime (1.633s = Gun_Reload clip length; no reload-time key in the .dat), refills Ammo to Gun.AmmoMax. Viewmodel.SetReloading dips the gun (can't ADS mid-reload). TODO: real Gun_Reload arm anim (needs additive-layer integration). Pushed 0fcdd95.
- [x] **RECOIL (real .dat pattern) (2026-07-08)** — non-blocked progress. GunDef parses Recoil_Min/Max_X/Y + Recover_X/Y (Eaglefire pitch +[3,4], yaw +-[0.5,1.5], recover 0.4). Fire kicks the camera (pitch up + random-sign yaw * Recover, UseableGun:1049/1188), recovers via Lerp rate 4 (PlayerLook), on top of mouse pitch in _Process. Pushed f1ddcd9.
- [x] **FIRE SPREAD (real .dat) (2026-07-08)** — non-blocked. GunDef parses Spread_Aim; Fire() deviates the hitscan in a cone of half-angle DegToRad(Spread_Angle_Degrees)*Lerp(1,Spread_Aim,aimAlpha) (Eaglefire 5.71deg hip -> 5% aimed) via DeviateInCone (port of RandomForwardVectorInCone, UseableGun:1004/5048). Viewmodel exposes AimAlpha. Pushed 6696b87.
- [x] **FIREMODES (Safety/Semi/Burst) (2026-07-08)** — non-blocked. GunDef parses .dat flags (Safety/Semi/Auto/Bursts N); V cycles the gun's available modes (Eaglefire Safety->Semi->Burst); LMB fires per mode (semi=1, burst=queue BurstCount at firerate, auto=held, safety=blocked), continuation in _PhysicsProcess. Pushed eb80831.
