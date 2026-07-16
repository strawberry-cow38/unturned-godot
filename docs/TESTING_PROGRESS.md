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

### Phase 3 — pure-logic extractions to L0
- `core/UnturnedSim/PowerSolver.cs` — the wire-power algorithm on plain `PowerDevice`/`PowerPort`/
  `PowerWire` records; `PowerNet.Recompute` is now a thin group-walk → Solve → write-back adapter.
  `tests/UnturnedSim.Tests/PowerSolverTests.cs` (11 tests): chains, 10-deep chain, over-draw,
  mid-chain fire, two-generators-one-consumer (pins the last-wire-wins overwrite semantic), and wire
  cycles (terminate; can't self-power — the loop-back wire overwrites the feed).
- `core/UnturnedSim/CombatMath.cs` — `ExplosionMath` (linear zombie/vehicle + squared player
  falloffs), `FallMath` (22 m/s threshold, min(101,|v|)×armor, bone-break gate), `StealthDetection`
  (the DETECT_* stance table + driving radius). Call sites rewired: PlayerController (Explode,
  CheckFallDamage, GetStealthDetectionRadius), Deployable.ExplodeDamage, Vehicle explosion.
  `tests/UnturnedSim.Tests/CombatMathTests.cs` (16 tests).
- Behavior verified identical: the L1 power/grenade/fall/stance tests pass unchanged.
- Adapter note: wires whose endpoint ports aren't on a grouped, valid deployable are now skipped
  (the old code half-processed them); unreachable in practice since KillPowerHardware/DisconnectWires
  free such wires the same frame.

### Phase 4 — visual golden-image tests (L2)
- `tools/visual_tests.py` + `tests/visual/manifest.json` + `tests/visual/golden/*.png` (10 scenes,
  760K committed): deploy ghosts / port arrows / focus outline, lamps on / off-dark / loadbar,
  damage fire / wreck, jeep 3/4-side day + night lights.
- `./test.sh --visual` (or `--all`) runs it; `--only` globs scene names; `--update <name|all>`
  re-baselines. Same `[TEST]`/`[SUITE]` grammar; diff PNGs land in `.testresults/visual/`.
- Determinism measured on this box: 8/10 scenes byte-identical run-to-run (lavapipe is a software
  raster); the two CPUParticles scenes (dmg_fire/dmg_wreck) vary ~0.009 MAE vs their 0.04 tolerance.
- ~30s/scene, ~5 min for the full set — nightly + on-demand, not the inner loop.

### Phase 5 — robotic manager
- `tools/nightly_tests.sh`: dedicated clone under `~/.cache/unturned-godot-nightly` (never touches a
  dev tree), fetch+reset to `origin/main`, `./test.sh --all`, last-good sha tracked; on failure it
  prints a ready-to-post report (the `[SUMMARY]` line, first failing test + repro, and the
  `git log --oneline lastgood..HEAD` blame range). Verified end-to-end against real origin/main
  (GREEN @ 98a361c). **NOT wired to cron on purpose** — the enable snippet is in the script header
  (`17 9 * * *` UTC suggested).
- Extra: `smoke.content_loads` tier-0 gate (item catalog registers + runtime OBJ parse yields
  geometry) — the old `--smoke` GUID GATE needs `res://content/manifest.json`, a rip-pipeline
  artifact not in the repo, so it stays a dev-box check.

## Deferred / notes

- `--navpathtest` / `--zombietest` need the real PEI map + baked navmesh (async world build, big
  content); they stay as manual harnesses for now. Candidate for an L1 Tier-2 composite later
  (the proposal's `world.pei_smoke`).
- Coverlet coverage on `core/` deferred: the five test csprojs carry no coverage collector package,
  and adding one is restore/package churn for a nice-to-have — `dotnet add package coverlet.collector`
  per test project + `--collect:"XPlat Code Coverage"` in run_suite when someone wants it.
- gdUnit4Net evaluation (proposal §2) still deliberately deferred.
- L1 sharding not needed: the whole 29-test suite is one ~10s boot.
- `MeleeSwingDriver` / `DeployUseDriver` in Main.cs are render-demo drivers (used by `--vm` visuals),
  not tests — intentionally kept.
- `UG_WIRETEST`/`UG_DEPLOYDMG`/`UG_WIREWRECK`/`UG_WIREARROWS`/`UG_WIREOFF` env scenes stay in
  `BuildDeployTest` for the phase-4 visual goldens; only the print-probe branches that L1 now covers
  (`UG_WIREMANAGE`, `UG_WIREFIRE`) get deleted.
