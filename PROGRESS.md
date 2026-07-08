# Unturned → Godot Port — Progress Log

Plan: docs UNTURNED_GODOT_PLAN.md (fable-5). Target: Godot 4 (C#/.NET 8, Jolt). Private project.
Approach: translate the readable U3-SDK source; rip Steam assets (AssetRipper) as swappable placeholders.

## Phase 0 — Foundations (in progress)
- [x] Prereqs on 4080 verified: .NET 8.0.419, Godot (choco), retail Unturned install present (rip source).
- [x] Workspace: C:\claude-workspace\unturned-godot\
- [x] **0b kernel — NetPak engine-agnostic core extracted → compiles standalone in .NET 8, 0 errors, ZERO
      Unity deps** (core = Reader/Writer/Const/MaxValue/EnumAttribute; only `System.Runtime.InteropServices`).
      → fable's "pure-.NET core carries as-is" thesis PROVEN firsthand.
- [x] Mapped the only Unity coupling in NetPak's shims: `UnityEngine.Mathf` (angle-clamp) → first SDG.Compat need.
- [ ] SDG.Compat.Mathf shim → build the System/Steam NetPak ex-shims
- [ ] NetPak test suite green (full 0b gate)
- [ ] UnturnedDat (data/mod layer) extracted + tests green
- [ ] Godot 4 .NET solution skeleton (Jolt on) + CI
- [ ] 0a rip station: AssetRipper full rip of the Steam install → canonical ripped/ tree
- [ ] 0d converter v0 + ContentProvider (GLB/PNG passthrough, static-prop YAML→.tscn, GUID manifest)
- [ ] GATE: a ripped prop instantiates in a Godot scene via ContentProvider by its original GUID
