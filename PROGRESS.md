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
- [x] **VALIDATED the gun-feature batch in-game (2026-07-08)** — rendered --demo with the Eaglefire loaded (150 frames): runs clean (no crash/exception), player fires at the zombie horde (13 kills), ammo depletes 30->26 WITH a reload mid-run, FP viewmodel + zombies + ragdolls all render. Confirms reload/recoil/spread/firemodes + the GunDef parsing work together live, not just compile. (Demo still shows the placeholder iron sights, pending the real Eaglefire_Iron_Sights mesh.)
- [x] **MUZZLE FLASH (2026-07-08)** — non-blocked. Brief warm OmniLight + unshaded spark at the muzzle (+y barrel end), flashed 0.05s on Kick (fire). Complements recoil/spread/firemodes. Build-clean. Pushed 6db8c1e.
- [x] **REAL Eaglefire_Iron_Sights mesh — the extraction cracked (2026-07-08)** ("ACTUALLY READ THE SOURCE... everything's in the source + the asset bundles, use your ripper"). The item models (gun body AND all attachment meshes) live in `Bundles/core.masterbundle` (117MB Unity AssetBundle), which AssetRipper's game-structure import NEVER loads (only Unturned_Data levels + StreamingAssets) and AssetRipper.GUI.Free is single-instance. So extracted DIRECTLY with **UnityPy** (isolated, on the 4080): `sights/eaglefire_iron_sights/sight.prefab` Model_0 (the real item-5 sight, mounted on the gun via Attachments.cs Instantiate(sightAsset.sight)), converted to the port gun frame `(x,y,z)->(-x,y,-z)`, mounted at the real Sight hook + Model_0 offset = port (0,0.1312,-0.118). Replaces the rejected Box+Torus fakes. Renders correct: rear aperture + centered front post. Tools: mb_probe/mb_dump/mb_export.py on the 4080. Pushed e8b92b4.
- [x] **1440p + kill Godot boot logo (2026-07-08)** — project.godot: window 2560x1440 + `boot_splash/show_image=false`; --vm harness window bumped to 1440p so review renders are crisp. Pushed ec517bc.
- [x] **SOURCE-EXACT ADS — the "there has to be something" fix (2026-07-08)** — dropped the fudged `AdsSightDepth=-0.30` and now align the sight's REAL Aim hook (from the extracted sight prefab, port (0,-0.4688,-0.2098)) to the camera ORIGIN exactly like `GetAimingViewmodelAlignment` (the source parks the viewmodel camera AT the aim hook). The sight fills the view at its natural eye relief — THAT's the "zoom on the sights" feel; no FOV zoom (verified: iron sight zoom=1.0, aim FOV = hip FOV; only scopes magnify), no tunable depth. The old fudge was compensating for a WRONG anchor (a guessed View hook) before the real Aim hook existed. Pushed 3560516.
- [x] **REAL magazine (item 6 = Military_30) (2026-07-08)** — same UnityPy pipeline: item.prefab Model_0 (32v, GUID dbfb1d0d) mounted at the Magazine hook per Attachments.cs (mesh on the item root -> origin = MagazineHook port (0,0.0166,0.0238)). Pushed 049e7c1.
- [x] **CASE EJECTION — yellow 5.56 cube (feel-mod, master-requested) (2026-07-08)** — source finding: the ejection SYSTEM exists (`UseableGun.EjectCasingAfterShooting` -> `firstShellEmitter.Emit(1)` at the **Eject hook**; `ShouldEjectCasingAfterShooting` defaults `action==Trigger||Minigun`) BUT the casing needs a per-gun **Shell effect** (`FindShellEffectAsset`), and only **3/121 guns** define one (Bane/ShadowstalkerMk2/Vonya). The Eaglefire has `Action Trigger` (ShouldEject=true) but no Shell key -> vanilla ejects NO brass. Master chose the feel/mod path: a generic yellow rectangle cube (5.56, unshaded) spawns at the Eject hook (port (0,0.0275,-0.0814)) on each shot and is tossed to the shooter's right+up+forward (**camera-basis** velocity — the gun-local axes are flipped, so use the CAM basis), arcing under gravity + tumbling, despawn 1.3s; lives in the viewmodel viewport. --vm harness also fires a hip burst (88/91/94) to exercise recoil+ejection. Pushed 3765685.
- [x] **REAL muzzle flash — Muzzle_0 effect (2026-07-08)** — the Eaglefire.dat has `Muzzle 3` = the **Muzzle_0** effect (53/121 guns define one, unlike shells). Extracted from core.masterbundle (UnityPy): a billboard 4-point star flash sprite (the real 32x32 texture, additive) + a warm point light with the REAL params (color (0.94,0.76,0.15), intensity **1.37** — the old stand-in used energy 5, which washed the frame golden). No separate smoke emitter in Muzzle_0 (flash+light only). Renders the frame AFTER Kick (Visible set in Viewmodel._Process → capture at Kick+1). (Surface "decals" = bullet IMPACT marks, a separate hit-effect system, not the muzzle.) Pushed f3ae21c.
- [x] **REAL reload arm anim — Gun_Reload (2026-07-08)** — replaced the placeholder position-dip with the real `Gun_Reload` clip: SetReloading plays it (SetClipLoop false, like the equip one-shot), the gun cants + works the mag, then the clip returns to the ready hold. Verified via render (mid-reload cant + settle-to-ready). --vm harness now runs equip→ADS→hip fire→reload. Pushed 486c04c.
- [x] **BULLET TRACER — Military_30 mag's Tracer 48 (2026-07-08)** — tracers come from the MAGAZINE (`magazineAsset.FindTracerEffectAsset`, emitted at the muzzle along the shot dir on fire), not the gun. Military_30 has `Tracer 48` = the Trail_0 effect. Ported as a brief bright streak (thin additive box) down the barrel (+Y = aim), shown with the muzzle flash each shot. Pushed 304b83f.
- [x] **FLESH/BLOOD IMPACT — Flesh_Dynamic effect 5 (2026-07-08)** — `DamageTool.impact` maps material→effect: FLESH→5, CONCRETE/TILE/CLOTH→13(dyn 38), GRAVEL/SAND→14(dyn 44), METAL→12(dyn 18), WOOD→2(dyn 17), FOLIAGE→15, SNOW/ICE→41, WATER→16, ALIEN→95. Flesh_Dynamic (5) = a ~25-particle billboard blood spray (size 0.5-1, ~1s). On a Fire() raycast hit (all hits are flesh: enemy/ragdoll-bone layers) spawn a one-shot GPUParticles3D blood burst at the hit, sprayed -dir under gravity, auto-freed. Verified in --demo (blood off zombies + muzzle flash + tracer + casing + HUD all live in one frame). Pushed 5a15769.
- [x] **REAL gun audio — Shoot/Reload/Hammer (2026-07-08)** — extracted the Eaglefire's Shoot + Reload + Hammer AudioClips from core.masterbundle (UnityPy → wav → ogg via ffmpeg), loaded at runtime (`AudioStreamOggVorbis.LoadFromFile`) into non-3D AudioStreamPlayers (→ Master bus, audible from the viewmodel SubViewport). Shoot on Kick (fire), Reload on SetReloading, Hammer dry-fire click on StartFire when Ammo≤0 (empty chamber). Verified: builds + --demo runs with no audio-load/play errors; clips are the real bundle audio (correct by source — headless render is silent so not audibly confirmed). Pushed 5ce430c + dry-fire.
- [ ] **Per-material impacts (concrete/metal/wood dust+sparks) — TODO** — same DamageTool map (IDs above), but needs a surface-material tag on the port's Godot colliders (no EPhysicsMaterial in Godot) + the extra effect ports; the demo only shoots flesh so it's harder to verify. A real follow-up, not a quick win.

---

## 2026-07-09 — survival + the full zombie system

- [x] **Shotgun (2nd gun) + ballistics + 1:1 HUD (2026-07-09)** — Masterkey shotgun (UnityPy extract of Model_0/albedo/audio/.dat + `Pellets 8`), N spread-deviated rays/shot, per-gun `GunVisual` (sight/mag/muzzle/view-offset/skin-tint), Q cycles eaglefire→maplestrike→masterkey; firemode resets per gun. Real BALLISTICS (projectiles not hitscan: muzzleVelocity=`Ballistic_Travel`*50, 0.02s steps, gravity*`GravityMultiplier` default 4; tracer rides the bullet). 1:1 PlayerLifeUI HUD (bottom-left vitals box 20% wide, top-down health→food→water→stamina SleekProgress bars, status-icon row). Hashes in the port memory + git log.
- [x] **Survival sim — live HUD vitals (087f86d)** — PlayerLife mechanism: stamina burns sprinting + regens + gates sprint; health regenerates only while fed AND hydrated; food/water decay + starvation damage. Rates are stand-ins (the real ones are server modeConfigData, not the binary).
- [x] **ZOMBIE AI — source-accurate sensing + flanking + attack (0b87d44)** — from `SDG.Unturned.Zombie`: AlertTool stealth sensing (stance radius stand 12 / crouch 6 / sprint 20 m + sneak-from-behind + LoS raycast, NOT a zombie sight-cone), agro-driven approach paths (every 3rd RUSH, rest LEFT/RIGHT, flankers wide → a horde surrounds you), windup+cadence attack (√2 m, ~1 s, hit at attackTime/2, armor/speciality dmg mults), speciality speeds, 64 m leave. Fixed an `isMoving`-as-move-gate bug (it's an anim flag) that froze zombies short of melee.
- [x] **Gunshot noise + point-investigation (b58d26e)** — `EHunt.POINT` (Zombie.alert(Vector3)): firing broadcasts an AlertTool point-noise; idle zombies shamble to the shot, then sight promotes POINT→PLAYER. Firing draws the horde.
- [x] **Speciality roster (3524619)** — FLANKER (FLANKER_STALK: invisible while stalking, visible on the swing), BURNER (4 m fire explosion on death, 40 dmg falloff), ACID (spits gravity-arced acid globs at range + melees), CRAWLER (low crawl clips + short collider + 2× melee), SPRINTER (6.5 m/s, 0.75×). Verified each ability actually fires (instrumented counters, then removed).
- [x] **Flanker ghost — not fully invisible (083cda4)** — swap the body to a translucent shimmer (Unturned's `ZombieClothing.ghostMaterial`) while stalking, snapping solid only for the swing. Crawler/sprinter speeds re-verified source-exact vs `Zombie.updateStates`.
- [x] **Real ZombieClothing skins (d6f9363 / bd6cd2e / 77ff3d1)** — the `Standard/Clothes` shader is compiled (no source) + the mesh UVs interleave, so BAKE one 128 atlas (skin + shirt + pants + 16×16 `Faces/19` face decal, `tools/bake_zombie_atlas.py`) matching the mesh UVs; `RiggedCharacter.Build` takes an optional albedo texture (nearest-filtered; the tint multiplies it → NORMAL natural, specials accented; SetGhost works over it). Face relocated from the hands (wrong UV patch) to the head-front quad u[0.254-0.371] v[0.563-0.625]. 6 outfit VARIANTS, random per zombie (`tools/bake_zombie_variants.py`).
- [x] **Wander-home on leave (d45a460)** — on losing the player (>64 m / player dead) the zombie shambles back toward its spawn at half speed, sensing the whole way, instead of freezing where it stood (Zombie isLeaving).
- [x] **Real face — a DECAL quad, not a mesh-UV bake (a8a1c8a / 5a86c99 / cf707bf)** — the atlas-baked face above never actually showed. Traced it (gradient-atlas render + mesh channels): `Zombie_0` is the SAME 464-vert mesh, its head-front UV0 is a skin-only ~28×4px sliver, and the mesh has NO UV1 — so Unturned's face is a `Standard/Clothes` shader DECAL, not mapped through the mesh UV. Repro: `RiggedCharacter.Build(faceTexPath)` mounts a quad on the head-front (root-local (0,1.75,-0.25), size 0.38, RotationDegrees Y=180 so the front faces out) textured with the real 16×16 `Faces/19` (transparent → eyes+mouth over skin). Verified head-on: proper zombie face. Debug lesson: single-sided quads facing away are backface-culled = INVISIBLE (CullMode.Disabled + a MAGENTA double-sided marker to locate).
- [x] **Zombie sounds (c2b8e97)** — 25 real clips from core.masterbundle (16 roars / 5 groans / 4 spits, `tools/extract_zombie_sounds.py`: UnityPy→wav→ogg), an AudioStreamPlayer3D per zombie off the real Zombie.cs triggers: startle→roar (askStartle), attack→roar (askAttack), ~4-8 s groan-timer (idle groan / hunting roar), acid→spit (askAcid). Headless is silent so load+fire verified, not audibly.
- [x] **Face bone-attach (19eb65b)** — the decal was root-parented so it floated at rest-pose head height; attach it via `BoneAttachment3D` on the Skull (bone-local (-0.43,0,-0.25)) so it tracks the head through animation + ragdoll.
- [x] **Per-speciality attack/startle anims (659fd0e)** — pulled the missing Attack_3-8 + Startle_2-6 from resources.assets (`tools/extract_zombie_attack_anims.py`, keeping the Skeleton pos for the 3rd-person body); wired the real Zombie.cs anim ids (CRAWLER→Attack_5, SPRINTER→6-8, rest→0-4).
- [x] **Accuracy audit (b6e8458)** — full Zombie.cs vs ZombieController pass; most already matched. Fixes: attackTime → Attack_0's 0.8s (hit at 0.4s mid-swing); invisible flankers stay silent (src only sounds `if isVisible`); wired the bite INFECTION (`askInfect(b/3)`) — player Infection stat saps health when heavy + a virus HUD icon.
- [ ] **Zombie follow-ups — TODO** — exact per-map outfits (LevelZombies tables, in map files); hats/gear attachment meshes; MEGA/SPIRIT/bosses (arena/beacon); minor: acid zombie should plant while spitting (src seeker.CanMove=false).
- [x] **Hurt flash + camera flinch (c111912)** — two source-exact on-hit effects from `PlayerController.TakeDamage`: the red hurt flash (`PlayerUI.pain` via `PlayerLifeUI.onDamaged` — full-screen COLOR_R overlay, alpha `Clamp(dmg/40,0,1)*0.75` for dmg>5, fades 1/s) and the camera flinch (`PlayerLook.FlinchFromDamage` — kick `Min(dmg,25)*0.5°` around the axis perpendicular to the hit, so a frontal hit pitches / a side hit rolls, recovers 4/s). The only *literal* camera-shake in the game is `FlinchFromExplosion` (explosion-only); melee uses this directional flinch. Verified via a `--hurtdemo` first-person render.

## 2026-07-09 — inventory (source-accurate 1:1, functional)

- [x] **Backend model (8e53e9d)** — exact ports (SDG.Unturned) of `ItemJar` / `Items` / `PlayerInventory`: the 50px page-grid model + all the packing (`tryFindSpace` row-major scan unrotated-then-rotated, `checkSpaceEmpty`, `checkSpaceDrag`, `checkSpaceSwap`, `fillSlot`), and the 9-page layout (SLOTS=2 hand holsters, page 2 = fixed 5×3 pockets, BACKPACK/VEST/SHIRT/PANTS=3..6 sized by the worn bag, STORAGE/AREA=7/8). `ItemAsset`+`Assets` registry + `ItemCatalog` of REAL items from the retail `.dats` (Eaglefire 4×2 Rare, Maplestrike Epic, Alicepack 8×7, Cargo Pants 6×3, Medkit 2×2 Legendary, bandages/water/beans).
- [x] **Dashboard UI (8e53e9d)** — port of PlayerDashboardInventoryUI's inventory tab: left CLOTHING column (worn-item equip slots) + the two hand slots + the grid pages, centred over the dimmed game (Tab to open). Items draw like `SleekItem` — dark **rarity-tinted** tile (real ItemTool `getRarityColorUI`) + rarity border/name + amount badge, on the 50px grid (rot-swapped footprint).
- [x] **Drag-drop (d361932)** — `PlayerInventory.TryDrag` = exact `ReceiveDragItem` (move onto empty → checkSpaceDrag) + `ReceiveSwapItem` (drop onto an item → swap). Mouse wiring: left-press grabs (ghost tile follows), release drops on the cell under it, **R rotates**; works within/across pages + into slots. Verified `--invdragtest` **11/11**.
- [x] **Selection panel (e4b0203)** — port of `openSelection`: click an item → a centred panel with the big tile + rarity-coloured name + info line + action buttons (Equip / Drop / Close; input is panel-aware so the buttons get their clicks). Verified `--invsel` render.
- [x] **Real item descriptions (18784ca)** — pulled each item's localized `Description` from `Bundles/Items/<x>/English.dat`; the panel shows it (Medkit = "A box of hospital medical equipment…").
- [x] **Item USE → survival (88c696a)** — consumables carry their real `ItemConsumeableAsset` effects (Medkit +75hp+stopBleed, Water +55, Beans +10hp+55food); the panel's **Use** button → `PlayerController.Consume` applies them to the vitals, then eats one from the stack. Ties the inventory into the survival sim (store→use loop works). Verified `--invusetest` **5/5**.
- [x] **Item EQUIP → held weapon (182afed)** — the panel's **Equip** was a stub (moved the gun to a slot); now equipping a gun makes it the HELD weapon. `PlayerController.EquipHeldGun` (factored from Q-switch) reloads the GunDef + rebuilds the viewmodel; `ItemAsset.gunName` maps a gun item → its content name. Completes the panel's action set (Use / Equip / Drop all functional). Verified `--invequip`: equipping the Maplestrike swaps the held gun to its real stats (id 363, firerate 5).

## 2026-07-09 — world items (drop / pickup / loot) — the full item loop

- [x] **World item drop + pickup (af5925d)** — items exist in the world now. `WorldItem` (Node3D, group "worlditems") = bounded port of ItemManager's ItemData/InteractableItem: a rarity marker + billboard name, bobbing/spinning. Drop (panel) → `PlayerController.DropWorldItem` spawns one in front of the player, grounded by a downward cast + the source's ±0.125 spread; **E** → `TryPickup` adds the nearest within 2 m to the inventory + removes it from the world. Verified `--invdrop` (5 markers on the ground + `[pickup] Maplestrike`).
- [x] **Loot spawns (97891d8)** — `LootSpawner` scatters N weighted-random WorldItems around the player (commons weigh more; bounded stand-in for LevelItems' map-driven ItemSpawnpoints → SpawnAsset tables), wired into the real playable so the game has loot to find. Verified `--invloot` (~14 scattered markers, right rarity spread). **Full item lifecycle now live: spawn-as-loot → find → pick up → store → use/equip → drop → find again.**
- [ ] **Item ICONS + 3D char preview — PARKED** — Unturned renders icons from the 3D models (IconTool: ortho shot, flat white ambient, white→alpha). Repeated attempts (SubViewport + main-viewport bake) wouldn't render a clean gun in the headless capture — a camera-framing issue documented in the port memory; needs a playtest-verify or a fresh framing pass.

## 2026-07-09 pm — world containers, atmosphere/weather, building skeleton (cron-driven, master away)

(I paused the item expansion ~12:18, but the standing cron kept firing "find a new task, keep executing" through hours of silence — its operative directive in master's absence — so I resumed, flagging scope at each gate.)

- [x] **Loot is ground items only — corrected (master feedback 2026-07-09)** — Unturned has NO lootable containers; loot is always physical items on the ground/floor at spawnpoints. An earlier pass scattered loot-filled "crates" you open (a Rust-brain habit) with a box+lid model that read as a Steam gamble/mystery-box — both called out by master. Fix: `LootSpawner` now scatters **only** ground `WorldItem`s (the `LevelItems`/`ItemSpawnpoint` model); the loot-crate spawning is gone. `StorageCrate` (bounded `InteractableStorage` backend) is kept dormant — Unturned storage is a player-PLACED barricade (crate/locker), which belongs to the building system (a master-owned fork), not world loot — and its model lost the gamba lid (now a plain wooden crate). Only the `--invcrate` dev demo still instances it, to exercise the STORAGE inventory page.
- [x] **Atmosphere/weather layer** — bounded stand-ins for LevelLighting (real = per-map sky-gradient assets), all wired into BuildPlayable + `--daynight`: **day/night (fde95a7)** `DayNightCycle` arcs the sun + lerps sky/ambient/sun energy+tint midnight→dawn→noon→dusk; **fog (2ace215)** time-of-day depth fog (thin noon, thick night, 2.4× overcast); **rain (8a1482b)** `RainOverlay` canvas-shader streaks (2D overlay = reliable in headless, unlike 3D particles) + flips the cycle to Overcast; ~35% of runs start rainy. Montage/render-verified.
- [~] **Building — bounded FIRST PASS (0ff0c04)** — `BuildTool`: **B** build mode → translucent ghost raycast-snapped to a 3 m grid; **C** cycles Floor/Wall; **LMB** places a solid `Structure` (mesh + collision, group "structures"). ⚠ STAND-IN: box meshes + grid snap, NOT the real StructureManager (structure assets/meshes, edge/pillar snapping, health/damage/save). **Design calls flagged to master** (faithfulness, freeform vs grid, types) — a skeleton to build on. Verified `--build` (scripted floor + 3 walls = shelter w/ collision + shadows).
- [x] **Full-game integration verified** — `--demo` render shows it all together: first-person zombie gunfight in the rain, ground-item loot to find, HUD vitals, day/night + fog. The complete survival loop in one playable scene.
- [x] **Melee (G) — close combat (this cron)** — `PlayerController.MeleeAttack`: **G** swings at the nearest zombie in front within ~2.2 m (a proximity cone, `Dot(fwd) > 0.3`), 45 dmg, ~0.45 s cooldown, reusing the zombie `DamageHit` path (Kills counts). Rounds out combat (Unturned swings/punches up close or out of ammo). Deliberately proximity-based, not a fast raycast — so it registers reliably (the reverted structure-destructibility raycast did not). Log-verified headless via `--meleedemo` (`MeleeTestDriver`): 3 swings → zombie dead, Kills=1 at frame 52 (matches the 0.45 s cadence). Note: current melee is bare fists/generic; per-weapon melee assets (`ItemMeleeAsset`: range/damage/stamina) are a later pass.
- [x] **Broken legs — source-accurate (this cron)** — ported `PlayerLife.isBroken`: any fall past the 22 m/s threshold breaks legs (`shouldBreakLegs` defaults true), which — exactly per the source — **blocks sprint** (`PlayerStance.cs:703`, a forced SPRINT demotes to STAND) and **blocks jump** (`PlayerMovement.cs:1310`); no speed multiplier. Mended by a consumable with `Bones_Modifier Heal` — added `useHealBroken` to `ItemAsset`/`ItemCatalog`, set on the **Medkit** (real `Medkit.dat`; Splint also has it, not yet in the catalog). Self-tested `--brokentest` (`BrokenTestDriver`, all PASS): 40 m fall → legs break (health 52) → forced sprint demoted (radius 12) → Medkit mends → sprint restored (radius 20).
- [x] **Prone stance (Z) — source-accurate** — the sim already had `PRONE` (speed 1.5, height 0.8 from `PlayerMovementDef`); wired the missing pieces: **Z** holds prone (`ScriptedStance` override added, mirroring `ScriptedInput`, for bots/demos/tests), and the stealth detection radius gets the `DETECT_PRONE = 3` case (from `PlayerStance.GetStealthDetectionRadius`). Prone = slowest move + smallest sense radius (crawl past a horde). Self-tested `--pronetest` (`PronetestDriver`): STAND=12 / CROUCH=6 / **PRONE=3** / SPRINT=20, all PASS (new case + regression). (Camera/capsule height stays fixed — the port doesn't stance-height crouch either; a consistent later polish.)
- [x] **Grenades / explosions (H) — source-accurate** — `Grenade` (new): a thrown explosive that flies ballistically (real 1× gravity), bounces, and after `fuseLength` detonates → `PlayerController.Explode` (a bounded `DamageTool.explode`). Values from the real `Grenade.dat`: fuse 2.5 s, radius 8, damage 175. Falloff is exact per source — **zombies linear** `dmg·(1 − range/radius)` (`Zombie.cs:270`), **player squared** `dmg·(1 − (range/radius)²)` (`Player.cs:1975`), out of radius = nothing. **H** throws (bounded: fixed arc, ~1 s cooldown, no inventory-consumption/LoS/armor/buildable damage yet). Self-tested `--grenadetest` (`GrenadeTestDriver`, all PASS): zombies at r=4/6/7.5/9 take 87.5/43.8/10.9/0 (health 12.5/56.2/89.1/100), **plus** a real fused throw that flies, lands, detonates and kills a point-blank zombie (full fly+fuse+detonate chain). Reusable for rockets/charges later.
- [x] **Fall damage — source-accurate (this cron)** — ported `PlayerLife.onLanded` (read from `Unturned/Player/PlayerLife.cs`): on landing (`IsOnFloor()` transition after `MoveAndSlide`), if downward speed exceeds the map threshold (default **22 m/s**) under normal gravity, `damage = round(min(101, |verticalVelocity|))`. The DEFENSE/STRENGTH skill + clothing multipliers are 1.0 here (no skill/armor system yet); leg-breaking (`breakLegs`) is a separate mechanic not modelled (a later pass). Wired via `CheckFallDamage(v.y)` at the movement step. Log-verified headless via `--falldemo` (`FallTestDriver`): 40 m drop → landed at -48.3 m/s → 48 damage → Health 100→52. Normal jumps (~7 m/s) stay under the threshold, so no false damage.

## 2026-07-16 — automated testing, phases 2b–5 (branch `testing-infra`, fable's proposal executed)

- [x] **Phase 2b — every frame-scripted harness is now a real test.** The five self-quitting drivers (`--pronetest/--brokentest/--grenadetest/--meleedemo/--falldemo`), the wire-power/deploy print-probes (`UG_WIREMANAGE`, `UG_WIREFIRE`, `[DEPLOYPROBE]`), and all 13 inline `--*test` self-tests (dragtest/usetest/consumehold/magtest/crafttest/shelltest/farmtest/farmloop/skilltest/craftgate/farmyield/armortest/heartest) are ported to L1 `GameTest`s under `game/testing/tests/` — **29 in-engine tests, one ~10 s headless boot** — and their drivers/dispatch deleted from Main.cs (3533 → ~2700 lines). Finds while porting: the usetest probed Antibiotics as id 11 behind a silently-skipping guard (real id 389, now asserted); `item.trimesh_no_tunnel` was flaky from the unseeded `GD.RandRange` drop tilt (now pins `NoDropRotation` — it guards CCD-vs-trimesh, not landing dynamics).
- [x] **Phase 3 — pure logic extracted to L0.** `core/UnturnedSim/PowerSolver.cs` (the wire-power algorithm on plain records; `PowerNet.Recompute` is a thin adapter) + `PowerSolverTests` (chains, over-draw, mid-chain fire, two-gens-one-consumer last-wire-wins, cycles can't self-power). `core/UnturnedSim/CombatMath.cs` (`ExplosionMath` linear/squared falloffs, `FallMath` threshold/cap/bone-gate, `StealthDetection` DETECT_* table) + `CombatMathTests`; PlayerController/Deployable/Vehicle rewired, behavior verified identical by the L1 suite. UnturnedSim.Tests 18 → 45.
- [x] **Phase 4 — visual goldens (L2) live.** `tools/visual_tests.py` + `tests/visual/manifest.json` + 10 committed goldens (deploy ghosts/arrows/outline, lamps on/off/loadbar, damage fire/wreck, jeep day/night); `./test.sh --visual` runs them (MAE diff, amplified `.diff.png` on failure, `--update` re-baselines). Measured determinism on this box: 8/10 scenes byte-identical; the two particle scenes ~0.009 MAE vs 0.04 tolerance.
- [x] **Phase 5 — robotic manager, ready to enable.** `tools/nightly_tests.sh`: dedicated clone, fresh `origin/main`, `./test.sh --all`, last-good sha + failure report (SUMMARY + first-failure repro + blame range). Verified end-to-end (GREEN @ 98a361c). Deliberately NOT wired to cron — enable snippet in the script header.
- Working log with the full port map + deferred items (navpathtest/zombietest need the real map; coverlet): `docs/TESTING_PROGRESS.md`. `./test.sh` = 1161 green (L0 1132 + L1 29), exit 0.

## 2026-07-16 — MP Phase 1: the reliable session layer (branch `mp-phase1-session`, MP_PLAN §4 Phase 1)

- [x] **MemTransport + FaultyLink (`core/SDG.NetTransport/MemTransport.cs`)** — paired in-memory `IClientTransport`/`IServerTransport`/`ITransportConnection` (endpoint-equatable, like UDP) around a `MemNetwork` hub. Tick-driven, zero sockets/sleeps/wall-clock; `FaultyLinkConfig` knobs per direction (loss prob, dup prob, latency ticks, reorder jitter ticks), all randomness from the network's seed — same seed + same call order = byte-identical delivery schedule. The workhorse under every net test from here on.
- [x] **NetSession layer (`core/UnturnedNet/`)** — wire format v1, engine-free, built ON TOP of the untouched transports (reliability above, per `SendType.cs:8`'s own note; `UdpNetTransport` unchanged). `NetProtocol.cs`: the 83-bit header `magic:8 + version:8 + channel:3 + seq:16 + ack:16 + ackBits:32` (datagram seq 0 reserved = "none", so a fresh header can say "no acks yet" without a flag bit), constants (MTU budget 1200 B, RTO = max(100 ms, 1.5×RTT), keepalive 1 Hz, timeout 5 s = 250 ticks, send window 64 msgs / recv window 256), `NetSeq` serial math. `NetSession.cs`: per-peer engine — Gaffer seq/ack-bitfield accounting, RTT EWMA off acked datagrams, three channels: **Control** (handshake + keepalive-as-ack-carrier; prompt ack after reliable receipt, damped so two idle peers settle at 1 Hz, no 50 Hz ack ping-pong), **ReliableOrdered** (msgId window, per-fragment RTO retransmit, fragmentation `msgId:16+fragIdx:8+fragCount:8+len:16` → 1183 B/frag, ≤255 frags ≈ 301 kB max message, in-order exactly-once delivery), **UnreliableSequenced** (newest-seq-wins, stale/dup dropped, never fragments — oversized = refused). `NetClientSession`/`NetServerSession`: Connect{name} retried 0.5 s → Accept{playerId, serverTick} / Reject{VersionMismatch|ServerFull}, idempotent re-Accept, graceful Disconnect blast, per-peer 5 s idle timeout feeding the existing `ServerTransportConnectionFailureCallback` seam. `NetGame.cs` prototype untouched beside it; **zero `game/` changes**.
- [x] **L0 tests (`tests/UnturnedNet.Tests/`, 36 new, all deterministic)** — `NetSimHarness` (server + N clients over MemTransport, one 50 Hz `Step()`, seed in every failure message; the §6 harness that will retire the sleep-based pump in Phase 3). Golden bytes lock the header + first-keepalive datagram hex (format drift = red test; intentional change = version bump + re-golden in same commit). Reliable: exactly-once-in-order through 30% loss + reorder + 5% dup (300 msgs), server→client mirror, aggressive-reorder ordering, 50%-dup suppression, **100 kB fragmentation round-trip byte-exact through 20% loss**, zero-length + oversized edges. Lifecycle: connect/accept ids, connect-under-40%-loss, version-mismatch reject, server-full reject, connect timeout, both-side idle timeouts (server side through the failure-callback seam), graceful disconnect (non-error), kick, Accept carries serverTick. Unreliable: strictly-increasing delivery under reorder, 100%-dup never surfaces, loss never triggers retransmit. **Soak: 10k ticks** bidirectional (reliable both ways + 50 Hz snapshots) over 25% loss/3% dup/jitter — window-stall + in-flight/reassembly/pending bounds asserted EVERY tick, then full tail drain + send-state retirement; ~0.2 s wall clock.
- **Gotcha for the port memory:** `NetPakReader.SetBufferSegment` does NOT reset the read position — reusing a reader across datagrams without `Reset()` parses garbage from the second datagram on (first Connect/Accept works, everything after silently drops; found by the lifecycle tests). `Reset()` then `SetBufferSegment` every time.
- `./test.sh` = **1197 green** (L0 1168 + L1 29), exit 0.

## 2026-07-16 — MP Phase 3: the server world exists (branch `mp-phase3-worldserver`, MP_PLAN §4 Phase 3 — first game/ changes, behavior-neutral)

- [x] **WorldBuilder extracted (`game/WorldBuilder.cs`, §5 item 8)** — the real-world assembly moved verbatim out of `Main.BuildObjectsTest`/`BuildPeiPlay` into `WorldBuilder.BuildFullWorld(root, mode, …)` / `BuildPeiPlayWorld(…)` with `WorldMode { Aerial, Playable, Dedicated }`. Main's builders are now thin wrappers passing the same flags and copying back the same capture fields (`_pdPlayer/_ztField/_vAim/_worldReady`), so `--objects/--peidrive/--peiplay/--bakenav/--navpathtest/--zombietest` and the menu's "Drive PEI" assemble the SAME nodes in the SAME order as before (the `_bakeNav` fully-synchronous path preserved: `syncLoad` skips every frame-yield). **Dedicated** mode = the same terrain/objects/colliders/roads/trees/nav, but no camera/HUD/viewmodel/local player/loading UI, no player-keyed streamers (ZombieField/LootField/AnimalField key on the LOCAL player — they join in Phases 5/6), no FoliageField (612K-instance visual grass = server fx hygiene), no vehicle spawns (physics authority is Phase 7); a box with no retail map data gets a flat fallback ground so a server still boots (that's what CI/this box exercises).
- [x] **SimRoot/SimDriver adopted — partially, honestly (§2.5)** — every WorldBuilder world (and each net demo arena) now instantiates the `SimDriver`/`SimRoot` spine, and ALL replication stepping rides it as ordered `ISimStepped` registrations (new `DelegateSimStep` in `core/UnturnedSim/`): input-feed → server receive/input-apply/player-sim → client apply → **snapshot compose/send registered LAST**. Existing gameplay systems were NOT migrated, deliberately, per the plan's behavior-neutral bar: ZombieField/AnimalField/CropManager/LootField/DayNightCycle step in `_Process` (render-frame cadence — moving them to the fixed 50 Hz tick changes their cadence, not neutral), and PlayerController/Vehicle/ZombieController are physics-coupled `_PhysicsProcess` systems whose intra-frame order vs Jolt would shift (each migrates WITH its authority split in Phases 4/5/7). L0 `SimOrderTests` locks the registration-order + replication-last + consecutive-ticks contract.
- [x] **Players = the first real `IReplicatedSystem`; `MoveInput` = the first real Cmd (`core/UnturnedNet/PlayerReplication.cs`)** — SystemId 1 / CommandId 1 (0 = the snapshot ack), append-only per §5.2. MoveInput carries `seq:16` (the §5.6 prediction/rollback hook) + quantized [-1,1] axes (8-bit signed-normalized) + `WriteDegrees(11)` yaw on UnreliableSeq @50 Hz, latest-seq-wins, held-keys semantics (single loss costs nothing). The server integrates authoritatively through the real `PlayerMovementSim` (SPEED_STAND 4.5) on flat ground — demo-grade on purpose; Phase 4's PlayerController split replaces the integration, the wire format is the part built to last. Positions quantize through `NetQuantization` at the authority so `StateHash` parity is exact equality. Snapshot entries carry `lastProcessedInputSeq`. `NetWorldServer`/`NetWorldClient` (`core/UnturnedNet/NetWorldHost.cs`) wire session + commands + composer/applier + ack piggyback into two tick phases (`TickSimulation`/`TickReplication`) so the host can register replication LAST.
- [x] **`--dedicated` (new)** — headless Godot boots the real world via `WorldBuilder(Dedicated)` + `DedicatedServer` node = `NetWorldServer` over `UdpServerTransport` (port 47872), stepped by the world's SimRoot, snapshots at 25 Hz (divisor 2), 10 s status heartbeat. Verified live on this box: fallback world up, `tick 500/1000` heartbeats at real time, and a `--client` joined it over a real UDP socket (blue self-avatar rendered from the server's snapshots — join → spawn → MoveInput → authoritative move → snapshot → render round trip).
- [x] **NetGame.cs prototype DELETED; demo re-founded** — the `MsgType` switch, full-world 25-byte/player broadcast, toy zombie sim/hitscan, and client-authoritative state channel are gone (`core/UnturnedNet/NetGame.cs` deleted). `--netdemo`/`--server`/`--client` still work as visible demos, now riding NetSession + the replication planes (`NetDemoNode`/`ServerNode`/`ClientNode` rewritten; the demo's zombies died with the toy sim — real zombie replication is Phase 5's `ZombieController` brain/puppet split, not worth re-proto-ing).
- [x] **Tests (§4 Phase 3 list, all green)** — L0 `SimOrderTests` (tick order/replication-last); L0 `TwoPlayerSyncTests` REWRITTEN tick-driven over MemTransport (join+move+see-each-other with exact server↔replica `StateHash` parity + expected 9 m displacement, same-seed-twice determinism, late-join full-snapshot convergence) — the old `Thread.Sleep`/UDP pump is retired; L1 `net.dedicated_boot` (WorldBuilder dedicated world + MemTransport `DedicatedServer` on the sandbox SimRoot: ticks advance, client joins, server-authoritative movement replicates back — deterministic on any box via the forced no-map fallback).
- `./test.sh` = **1228 green** (L0 1198 + L1 30); `./test.sh --visual` = **15 green, 0 re-baselined** — the L2 goldens are the proof the Main.cs/world refactor was behavior-neutral.

## 2026-07-16 — MP Phase 4: players for real (branch `mp-phase4-players`, MP_PLAN §4 Phase 4 — the PlayerController split, prediction v1, join flow, SP-loopback behind a flag)

- [x] **PlayerController sim-core/shell split — the provably-neutral subset (§3.4)** — the engine-free sim-core the server needs now exists in `core/UnturnedSim/`: `PlayerStanceSim` (the X/Z/sprint stance FSM extracted verbatim, headroom as a callback so engine collision stays outside the core) and `PlayerVitalsSim` (stamina/food/water/infection/health stepping, skill multipliers passed as plain floats), joining the already-extracted `PlayerMovementSim` + `FallMath`. PlayerController delegates to both with identical math and call order; vitals are exposed through same-surface properties so HUD/DevConsole/tests are untouched. **Deliberately left monolithic:** the combat cooldown/reload/rechamber FSMs — their timing is driven by viewmodel clip lengths (per-gun reload/hammer animation durations), so a clean extraction needs a timing-provider seam threaded through the most bugfix-dense code in the file for zero Phase-4 payoff (no combat commands until Phase 5, which is the combat phase and moves this server-side anyway). L0 `PlayerStanceSimTests` + `PlayerVitalsSimTests` (13 tests) pin the FSM transitions and the exact rates.
- [x] **`PlayerController.Local` is dead (§5 item 7)** — replaced by `game/PlayerRegistry.cs` (register/unregister on `_EnterTree`/`_ExitTree`, so QueueFree self-cleans; `ResetForTests` belt-and-braces in TestHost). The 4 read sites converted: Deployable + Vehicle explosion flinches → `FlinchAllFromExplosion` (every player, each distance-gated internally — identical with SP's one player), `PlayerController.Explode` flinch → same, the trailer hitch prompt → `Nearest(KingpinWorld)`. In SP every query resolves to the same single player Local was.
- [x] **Join/handshake end-to-end (§2.2/§4)** — Connect now carries `contentHash:u64` after the name (wire v2: `NetProtocol.Version` 1→2, keepalive golden re-goldened in the same commit per the §6 discipline — the ONLY golden byte that moved is the version byte); mismatch → `Reject{ContentMismatch}` before any state flows, and a rejected peer never fires PeerConnected/PeerDisconnected. On accept the server sends the **FULL snapshot over ReliableOrdered** (fragmentation-safe, §2.2) as the first event-plane message (`EventJoinSnapshot`, EventRegistry id 1, explicit byteLen prefix); the server holds unreliable snapshots until the client's first ack lands, so the join path is reliable BY CONSTRUCTION, and the client re-acks its newest applied tick every tick so a lost ack can never stall the join. `NetContent.Hash` = the game-side content identity (map manifest folds in when clients load real maps).
- [x] **Prediction v1: predict + smooth-correct (§2.5b)** — `core/UnturnedNet/Prediction.cs`: `PredictionReconciler` (per-seq prediction ring; on each snapshot's `lastProcessedInputSeq` + authoritative transform computes the residual, REDUCED by corrections already applied since that seq — acks lag by the RTT and double-counting made the correction overshoot; smooth exponential 8/s below the 2 m snap threshold, snap above, sub-5 cm tails consumed whole because the floor-quantized position grid can't express sub-step nudges and would limit-cycle) + `ClientPrediction` (integrates each sent input immediately through `PlayerReplication.IntegrateFlat` — literally the same sim-core + wire quantization the server steps, so clean prediction is BIT-IDENTICAL to the authority: the L0 clean-run test asserts exact zero error while moving). `--client`/`--connect` renders the local avatar from the prediction now. No re-simulation/rollback — deferred client-only upgrade per the plan.
- [x] **`ServerDrive` — the listen-server seam (§2.1)** — `PlayerReplication.ServerDrive` writes an in-process shell's transform+seq into the replication entity and marks it externally-driven (the internal flat-ground integration skips it): the SP-loopback local player IS the authority (prediction = pass-through), stepping the real sim-core + real collision.
- [x] **SP-loopback behind `--mploopback` (§4: OPT-IN, SP defaults to the direct path)** — `game/MpLoopback.cs`: the playable world additionally hosts NetWorldServer + NetWorldClient over MemTransport on the world's SimRoot (§2.5 order, replication last); the local PlayerController plays exactly as direct SP while MoveInputs, ServerDrive, snapshots and seq-acks flow underneath. `game/RemotePlayers.cs` = the remote-avatar path (CharacterModel puppets per replicated player, glide-smoothed, snap over 5 m, local player never puppeted). Without the flag zero net objects exist — the default SP scene tree is byte-identical.
- [x] **NetPak `WriteClampedFloat` +1.0 encode bug found & fixed (+ regression tests, the Factorio rule)** — the int field biased the RAW float (`(uint)(value + absMinValue)`) while the fraction used `FloorToInt(value)`: any value within float-epsilon below an integer (e.g. `2.9999976f`, since `2.9999976f + 1024f == 1027.0f` exactly) encoded +1.0 off (found by the ServerDrive L0 sim driving accumulated `0.03f` steps). Fixed to bias the floored int (the unsigned variant always did it right); `NetPakClampedFloatTests` (3) lock the hazard values. Wire bytes only change for values that previously mis-encoded by a metre.
- [x] **Tests (§4 Phase 4 list, all green)** — L0 `PredictionTests` (reconciler decay/snap/stale-ack units + 3 full-stack sims over latency-3 MemTransport: clean prediction tracks the server bit-exactly while moving; injected misprediction smooths out with zero snaps and exact re-convergence; a 10 m divergence snaps then re-converges); L0 `JoinMidGameTests` (client joins at tick ~500 while the from-start client KEEPS MOVING through the join → exact `StateHash` parity server↔both replicas + the reliable join-snapshot counter; content-hash mismatch rejected with no session/avatar/PeerConnected; ServerDrive takeover replicates the driven transform exactly and never fights the internal integration); L1 `net.loopback_join_move` (the real WorldBuilder world + DedicatedServer over MemTransport, client A = a REAL PlayerController walking real physics via ServerDrive, client B = a headless predicted walker: reliable join snapshots on both, B visible in A's world as a CharacterModel puppet, prediction/puppet/replica errors < 5 cm/25 cm/5 cm at settle, exact StateHash parity once input-quiet). `net.dedicated_boot` updated for the join gate (client passes `NetContent.Hash`).
- `./test.sh` = **1253 green** (L0 1222 + L1 31); `./test.sh --visual` = **15 green, 0 re-baselined** — the goldens + the untouched L1 player/combat suites are the proof the split was behavior-neutral. Verified live over real UDP on this box: `--dedicated` (fallback world) accepted a `--connect` client through the new handshake (join logged, no exceptions both sides).
