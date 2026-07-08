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
- [ ] Phase 1 vertical slice: headless Godot server + ported NetPak transport, a small ripped level,
      1 gun vs a chasing/dying zombie, Godot-Glazier HUD (2 players)
