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

## In progress / next

- Batch B: wire-power/deploy scenarios (`UG_WIREMANAGE`/`UG_WIREFIRE` probes → L1, chain passthrough
  with a 2nd spotlight, `UG_WIREWRECK` shatter-cleanup facts, `UG_DEPLOYDMG` stage state, port arrows,
  placer aim probe, `--heartest`).
- Batch C: the inline `--*test` self-tests (dragtest/usetest/consumehold/magtest/crafttest/shelltest/
  farmtest/farmloop/skilltest/craftgate/farmyield/armortest) → L1 GameTests.
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
