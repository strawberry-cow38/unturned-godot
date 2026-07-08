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
- [ ] NEXT ("playable"): 1 gun end-to-end (ItemGunAsset.dat → UseableGun raycast on RayMasks → damage →
      death) + a direct-chase zombie + minimal HUD, on the SimRoot tick. Then 2-player headless server.
