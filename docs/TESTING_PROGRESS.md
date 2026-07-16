# TESTING_PROGRESS — implementation log for docs/TESTING_PROPOSAL.md

Running status of the remaining phases (2b–5) on branch `testing-infra`. Phase 1 (test.sh runner)
and phase 2 core (GameTest/TestHost + first 5 ports) landed on `main` earlier.

## Done

### Phase 2b — driver ports (batch A: the five self-quitting drivers)
- `player.stance_stealth_radius` ← `--pronetest` / `PronetestDriver`
- `player.fall_damage` ← `--falldemo` / `FallTestDriver`
- `player.broken_legs_mend` ← `--brokentest` / `BrokenTestDriver`
- `combat.melee_kill` ← `--meleedemo` / `MeleeTestDriver`
- `combat.grenade_falloff` ← `--grenadetest` / `GrenadeTestDriver` (falloff table + the fused-throw kill)

All five drivers, their `Build*` scene builders, and their `--flag` dispatch deleted from `Main.cs`.
Shared helpers live in `game/testing/tests/Rigs.cs` (ground plane + demo player).

### Phase 2b — wire-power/deploy scenarios (batch B)
- `power.chain_passthrough` — gen → spotA → passthrough → spotB (live/powered/draw down the chain)
- `power.wreck_drops_wires` ← `UG_WIREWRECK` (shattered spotlight takes its wire + gen load with it)
- `deploy.damage_stages` ← `UG_DEPLOYDMG` (smoke/heavy/fire/wreck state; the LOOKS stay a golden scene)
- `deploy.port_arrows` ← `UG_WIREARROWS` state (geometry/colour stays visual); added
  `ConnectionPort.DebugArrowVisible` probe
- `deploy.placer_aim` ← the `[DEPLOYPROBE]` scripted aim check (+ sky/overlap/wall rejects; note: the
  TOP of an obstacle is legitimately placeable, so "occupied ground" is asserted via an ADJACENT box
  intersecting the clearance sphere, not a box under the ray)
- `zombie.hear_salience` ← `--heartest`

Deleted from Main.cs: the `UG_WIREMANAGE` + `UG_WIREFIRE` print probes, the `_deployProbePlacer`
frame-3 check. Kept (phase-4 golden scenes): `UG_WIRETEST` (+`[POWERTEST]`/`[LAMPDBG]` prints),
`UG_WIREOFF`, `UG_WIREWRECK`, `UG_WIREARROWS`, `UG_DEPLOYDMG`, `UG_DEPLOYFOCUS`, `UG_LOADBAR`.

### Phase 2b — the inline `--*test` self-tests (batch C)
- `inv.drag_swap_crosspage` ← `--invdragtest` · `vitals.consume_effects` ← `--invusetest` ·
  `inv.consume_hold_flow` ← `--consumeholdtest` (InventoryTests.cs)
- `gun.mag_reload` ← `--magtest` · `gun.action_types` ← `--shelltest` (GunTests.cs)
- `craft.blueprints_resolve` ← `--crafttest` · `craft.skill_gate` ← `--craftgate` (CraftTests.cs)
- `farm.grow_harvest` ← `--farmtest` · `farm.crops_loop` ← `--farmloop` ·
  `farm.second_yield_roll` ← `--farmyield` (FarmTests.cs; the yield roll now uses seeded `T.Rng`)
- `skills.grid_xp_mastery` ← `--skilltest` (SkillTests.cs)
- `armor.product_aggregation` ← `--armortest` (ArmorTests.cs; safe in the shared boot because
  `RegisterAll` starts with `Assets.clear()` — the catalog is rebuilt, plus inert test ids 9001-9003)

All 13 `Run*Test` functions + their dispatch deleted from Main.cs (~600 lines).
`--extractblueprints` kept (it's a content tool, not a test).

Fixes found while porting:
- The old usetest probed Antibiotics as id **11** behind a `useDisinfectant > 0` guard that silently
  skipped — real Antibiotics is id **389**; the port asserts it unconditionally.
- `item.trimesh_no_tunnel` was **flaky**: WorldItem's drop pose uses unseeded `GD.RandRange` tilt and
  an edge landing can wobble past the settle window. The test now sets `WorldItem.NoDropRotation=true`
  (deterministic pose — it guards CCD-vs-trimesh, not landing dynamics); ResetGlobals restores it.

## In progress / next

- Phase 3: PowerSolver → core/UnturnedSim + NUnit suite; explosion falloff + stance table extractions.
- Phase 4: visual goldens (manifest + tools/visual_tests.py + `--visual` in test.sh).
- Phase 5: tools/nightly_tests.sh (ready-to-enable, NOT wired to cron).

## Deferred / notes

- `--navpathtest` / `--zombietest` need the real PEI map + baked navmesh (async world build, big
  content); they stay as manual harnesses for now. Candidate for an L1 Tier-2 composite later.
- `MeleeSwingDriver` / `DeployUseDriver` in Main.cs are render-demo drivers (used by `--vm` visuals),
  not tests — intentionally kept.
- `UG_WIRETEST`/`UG_DEPLOYDMG`/`UG_WIREWRECK`/`UG_WIREARROWS`/`UG_WIREOFF` env scenes stay in
  `BuildDeployTest` for the phase-4 visual goldens; only the print-probe branches that L1 now covers
  (`UG_WIREMANAGE`, `UG_WIREFIRE`) get deleted.
