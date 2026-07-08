# Unturned → Godot Port — Progress Log

Plan: UNTURNED_GODOT_PLAN.md (fable-5). Target: Godot 4 (C#/.NET 8, Jolt). Private project.
Approach: translate the readable U3-SDK source; rip Steam assets (AssetRipper) as swappable placeholders.
Repo: C:\claude-workspace\unturned-godot\ (this). Source+plan: C:\claude-workspace\archive\.
Mode (master 2026-07-08): keep moving autonomously, don't stop until commanded; 15m self-pace cron; decide+log, don't ask.

## Phase 0 — Foundations (in progress)
- [x] Prereqs on 4080: .NET 8.0.419, Godot (choco), retail Unturned install present (rip source).
- [x] Workspace + git repo.
- [x] **0b — NetPak (netcode) proven engine-agnostic:** core+System-ex compiles standalone in .NET 8, and
      the **NetPak test suite runs GREEN: 46 passed / 0 failed** (bit-pack, angle-pack, string, defer-read,
      overrun). fable's "pure-.NET core carries as-is" thesis proven at the LOGIC level, not just compile.
- [x] **0c start — SDG.Compat.Mathf shim** (namespace UnityEngine, pure System.Math delegation) → unblocks
      any engine-agnostic code that `using UnityEngine;` for Mathf. First harness piece.
      - deferred: UnityNetPakTests (need Vector3/Quaternion shims), NetGenTests (need NetGen-generated code),
        SteamworksNetPak ex (need Steamworks.NET bindings).
- [x] **UnturnedDat (data/mod .dat layer) proven engine-agnostic:** core compiles standalone + test suite GREEN: 800 passed / 0 failed (parser/tokenizer/dict/list/writer/metadata). The modding backbone carries untouched. (InternalsVisibleTo added; UnityDatEx/ColorEx deferred to Vector/Color shims.)
- [ ] SDG.Compat: Vector2/3/4, Quaternion, Color32 (→ unblocks UnityNetPakTests + tons of downstream)
- [ ] Godot 4 .NET solution skeleton (Jolt on) + Linux-headless CI
- [ ] 0a rip station: AssetRipper full rip of the Steam install → canonical ripped/ tree
- [ ] 0d converter v0 + ContentProvider (GLB/PNG passthrough, static-prop YAML→.tscn, GUID manifest)
- [ ] GATE: a ripped prop instantiates in a Godot scene via ContentProvider by its original GUID
