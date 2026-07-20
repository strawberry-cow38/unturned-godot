# SP / MP Unification — Integrated-Server Migration Plan

**Status:** greenlit (VoX 2026-07-19), executing on branch `sp-mp-unify` (worktree `ug-unify`).
**Rev 2** — corrected after an adversarial plan review (see §"The correction").

Goal: make singleplayer run as an in-process **integrated server + loopback client whose local
views consume replicas**, so every gameplay feature is authored **once** (server-authoritative),
exactly like retail Unturned. Kill the "write every feature twice" tax (grid-power, clothing, and
rope-tow are SP-only today; combat/vitals live in both SP and MP).

## Retail model (verified vs the decompiled `Assembly-CSharp`)

- SP **is** a 1-player server: `Provider.singleplayer()` sets `isServer && isClient`, adds the local
  player over a dummy `TransportConnection_Loopback`, and skips the network listener. A dedicated
  server is the same code with `isServer` only.
- Write-once RPCs: one server-authoritative method + a generated `Client/ServerInstanceMethod`; when
  local, the send calls `InvokeLoopback` (memcpy the serialized buffer → run the identical read
  handler in-process). Same bytes, same handler, no socket.
- Movement is **input-authoritative** (client sends input, server re-simulates). **We deliberately
  diverge** — see §"Deliberate divergence".

## The correction (what the review caught)

Rev 1 assumed the loopback foundation was "mostly done — flip and delete." It is not. `MpLoopback`
(`--mploopback`) is **publish-only**: the local `PlayerController` keeps the **direct path** for
movement, vitals, zombies, vehicles, deployables, power, crops, clock. The `*NetSync` classes only
**publish** that direct-path world onto the wire for remote joiners — **nothing local consumes a
replica**. So "loopback-SP ≡ direct-SP" is trivially true (it *is* the direct path with a wire
bolted alongside). The actual migration — repointing every **local view from direct nodes to
replicas** — has not started, and is distributed across P1–P5 below. P6 is then a genuine
flip-and-delete, not the hidden home of the whole migration.

## Deliberate divergence — keep client-auth movement

Retail re-simulates the player from input server-side. We cannot: Godot `MoveAndSlide` is
non-deterministic across two bodies (the "inchworm" we already beat). The local body stays
**client-authoritative-position** — it owns its transform; the server envelope-validates and
**adopts** it (`PlayerReplication.ServerDrive`), never re-simulates. Vitals therefore become a
per-player **split authority**: position client-auth, HP server-auth. That model exists nowhere in
the codebase yet and is the core of the hard phase (P3).

## Genuinely built already

- Abstracted transport + in-process loopback (`core/SDG.NetTransport` `MemTransport`), no ENet.
- One-world assembly (`WorldBuilder.BuildFullWorld(mode)`), modes `Aerial/Playable/Dedicated/Client/Editor`.
- Engine-free shared sim spine (`core/UnturnedSim`: `SimClock` 50 Hz, `SimRoot`, `PowerSolver`,
  `BallisticsMath`, movement sim…) + the full 3-plane net stack (snapshots / commands / events) and
  server-auth logic (`ServerCombat`, `ServerTransactions`, `ServerPlayerAuthority`).
- Client-auth movement/driving authority (Part A) — the inchworm fix.
- The wire that **publishes** the local world to remote joiners (`MpLoopback` + the `*NetSync`).

## Phases

Each phase leaves the branch buildable, `./test.sh` (L0+L1) green, and the **live MP PEI**
unregressed (validated first on the non-live rig). Nothing merges to `main` until the full sequence
is proven + VoX signs off.

- **P0 — Baseline + harness + rig + inventory.** Green baseline captured. Build the real parity
  harness (boot direct-SP vs replica-consuming-SP, diff key state). Stand up a **non-live MP rig**
  (two instances on the box) so P3/P6 gates never require live PEI. Inventory every `NetId==0` /
  `NetAvatar` branch and every direct-path harness/golden (the migration + delete surface).
- **P1 — "Local consumes replica" mechanism + first subsystem** (world-items or deployables/power,
  the simplest). Build the general capability: the local player renders/consumes the replica for a
  subsystem and routes its commands through the loopback server. **Pattern-setter** — every later
  phase copies it. Gate: subsystem-1 replica-SP ≡ direct-SP.
- **P1b — Inventory server-authority (inserted; surfaced by P1).** The local player's inventory
  becomes server-owned + owner-replicated in consume mode (mirror the MP client: seed the server's
  inventory for the local player at loopback init, set the `NetMoveItem/NetEquipItem/NetDropItem/
  NetConsume/NetCraft` seams, `AdoptReplicatedInventory`). This CLOSES P1's gap — deployable placement
  now validates the item-spend server-side — and unblocks crafting, consuming, and combat ammo. Second
  pattern-setter. Gate: end-to-end place-a-deployable (spends a real server-side item) + inventory
  move/consume round-trip in consume-SP; full suite green.
- **P2 — Vehicles consume replica.** Driver already client-auth Part A; extend so occupancy + other
  vehicles consume replicas locally. Gate: vehicle parity in replica-SP + non-live MP.
- **P3 — Combat + vitals split-authority (HARD, checkpoint before).** Server-auth HP with client-auth
  position; migrate damage sources (zombie melee, `Explode`, fall, starvation/infection) from
  `PlayerController.TakeDamage` (client-authored) to server-authored; flip `PvPEnabled`
  (`DedicatedServer.cs:80`); replicate death→ragdoll→respawn to the local client; retire the
  duplicated client bullet-stepping. Overlaps P4 (zombie→player damage). Gate: death/respawn + PvP
  correct in replica-SP + non-live MP.
- **P4 — Zombies consume replica + zombie damage server-auth.** Local view consumes replica/puppet
  instead of direct brains; zombie→player melee server-authored (pairs with P3). Gate: zombie parity
  + server-auth zombie damage.
- **P5 — Animals + clothing.** Animal replication (SP-only → replicated + consumed); worn-clothing
  visuals on remote puppets. Gate: both live in replica-SP + MP.
- **P6 — Flip + delete (HARD, checkpoint before).** Default `Playable` boots the replica-consuming
  listen-server. Enumerate + invert every `NetId==0` / `NetAvatar` branch with a regression pass over
  grid-power / deployables / rope-tow. Migrate the dev/test harness fleet (`--peiplay`, `--vehicle`,
  `--deploytest`, `--drivetest`, editor) + L2 goldens onto the loopback (or a thin direct-construct
  shim) **before** deletion. Delete the dead direct path; re-baseline goldens. Gate: default SP =
  integrated server; suite + goldens + live PEI green; no direct-path code.
- **P7 — Edges + cleanup + merge.** SP save/load decision (in/out — state ownership moves
  server-side); pause/unpause verification (tree-pause freezes the loopback server → resume time-gap
  vs sync-check/envelope); DevConsole audit; docs; final adversarial review; merge to `main`.

P1 is the pattern-setter. P2/P4/P5 are independent conversions (can parallelize). P3 and P6 are the
hard, checkpointed phases.

## Execution

- Isolation: `sp-mp-unify` branch in `ug-unify`, rebased on `origin/main` frequently (cow tools churns
  main; `git checkout -- .` before every rebase/merge — the repo tracks bin/obj).
- Per-phase workflow: implement → tests per step → adversarial review fan-out → fix → full suite once
  at the end. On Opus; **no fable** (VoX out of usage).
- Non-live MP validation rig on the box so live PEI is never the first place a risky change runs.
- Autonomy: workflows for the mechanical fan-out inside a phase; a VoX-review checkpoint **between**
  phases, hard-stopping before **P3** and **P6**.

## Risks

1. **Determinism** — never re-sim the local player; keep client-auth-position (inchworm).
2. **Scope** — the migration is P1–P5 (per-subsystem replica consumption), not P6; gate per subsystem
   or it becomes an unreviewable big-bang.
3. **`NetId==0` inversion** — the flip turns on every `if (NetId != 0) route-over-wire` branch,
   including just-shipped grid-power / deployables / rope-tow. P0 inventories, P6 converts + regresses.
4. **Harness dependency** — the direct path is the L0/L1/L2 + render/editor substrate; migrate onto
   loopback (or a shim) before deletion — a P6 gate; expect golden re-baselining.
5. **Live PEI** — non-live rig validates first; merge to main only after full proof + signoff.
6. **Edges** — pause freezes the loopback server (verify resume); SP save/load absent + state ownership
   moves server-side (decide scope in P7).

## Progress + open architecture decision (2026-07-19)

Done + gated on `sp-mp-unify` (full suite 1449 green, not merged):
- **P0** — baseline + inventory + template.
- **P1** (2d61e306) — deployables/power consume replica behind `--spconsume`; parity test (16 checks, teeth).
- **P1b** (43f93a80) — server-authoritative inventory for the loopback local player; end-to-end test (21
  checks, teeth: empty→reject, real spend, second→reject, move/consume round-trip).

**Holding here for a VoX decision before the physics cluster (vehicles / combat-vitals / zombies).**
The fork: **entity-based** subsystems (deployables, loot, inventory) are clean — the in-process server
holds plain data, the client materializes the only Godot nodes, zero duplication. But **physics-body**
subsystems (vehicles = `VehicleBody3D`, zombies = nav) can't do pure host-consume without the server
owning a real body AND the client rendering a puppet = two bodies in one process. Retail's actual model
is the **listen-server**: the host KEEPS the real bodies and runs server logic on them directly
(write-once because the server logic is the only logic), and only REMOTE clients get puppets — which also
preserves the client-auth/inchworm decisions (the driven car stays the real client-auth body, never
re-simmed). **Lean: hybrid — listen-server (host keeps real bodies) for physics subsystems, pure-consume
for entity subsystems.** This decides the shape of vehicles/zombies/combat, so it's a checkpoint.

## P6 — the staged flip (AWAITING VoX REVIEW — do not execute without greenlight)

Everything up to here is done + gated on `sp-mp-unify` (full suite 1459 green, NOT merged): every
subsystem consumes server replicas under `--spconsume`, and combat/vitals is fully server-authoritative.
P6 is the irreversible step: make consuming the DEFAULT and delete the direct path. Precise plan, grounded
in the P0 inventory:

1. **Flip Playable's default.** `WorldMode.Playable` ("Drive PEI" / stable SP) boots the in-process
   listen-server + client with consume ON (currently behind `--spconsume`). The local player consumes
   replicas by default. (`Main.cs` AttachMpLoopback + the flag → default.)
2. **Invert / verify the `NetId==0` branches (P0 Catalog A).** Player-placed things now get real
   server NetIds → the `if (NetId != 0) wire; else direct` branches take the wire path. Verify each:
   deployable pickup/salvage/toggle/wire connect+remove, storage. **Edge cases that need a decision:**
   - **World FIXTURES stay host-direct.** Grid-power + gas-pump (`PowerNetId==0`) are world fixtures the
     host owns in the listen-server; they stay NetId==0 host-direct (consistent with "host keeps real
     bodies"). Wiring a fixture (NetId 0) to a player-placed deployable (NetId!=0) — `RequestConnectWire`
     currently refuses if either end is 0; handle host-fixture ↔ replicated-deployable wiring.
   - **Rope-tow stays host-direct** (no replication, SP-only by design; host owns the vehicles + the tow
     force). MP rope-tow is a separate future feature.
3. **Retain a direct-construct path for the harness fleet (P0 Catalog C).** `--peiplay/--vehicle/
   --deploytest/--drivetest/--proptest/editor` + the 10 L2 goldens boot the DIRECT Playable path for
   isolated component testing. Keep a thin `--direct` construct path for them (don't fully delete direct
   construction) so the test/render fleet keeps working; the GAME uses the consuming default. Re-baseline
   any golden whose visual shifts.
4. **Collapse the NetAvatar/Net* forks (P0 Catalog B) + delete the now-dead GAME direct-path branches**
   (retaining the harness direct-construct path from step 3).
5. **End-to-end verify:** a full consuming-SP playthrough (place → wire → drive → fight → die → respawn →
   loot) matches the old direct-SP, on the non-live rig; then a live-PEI smoke; then merge to `main`
   (rebased onto catboy's Vehicle Battery f685227) on VoX signoff.

### P6a — flip the SP-GAME default to the consuming listen-server (DONE, reversible; direct path RETAINED)

Step 1 of the flip, done SAFELY (flip + verify, no deletion). The real SP GAME Playable entries now boot the
consuming listen-server BY DEFAULT; the direct path is kept intact as a reversible fallback + the harness substrate.
- **The flip (`Main.cs`).** `AttachMpLoopback(res, gameDefault)` + the pure `Main.ResolveLoopbackMode(gameDefault,
  mpLoopback, spConsume, direct)`. GAME entries pass `gameDefault=true` → attach + `ConsumeDeployables=true` with NO
  `--mploopback --spconsume`: menu "Drive PEI"/`--peidrive` (BuildObjectsTest, gated `_peiPlayable && !_bakeNav`) and
  `--peiplay` (BuildPeiPlay). TEST HARNESSES pass `gameDefault=false` → unchanged legacy: direct unless explicit
  `--mploopback` (nav-bake/navpath/zombietest set `_bakeNav`; `--objects` is Aerial → null player → early return).
- **Opt out = `--direct` (or `UG_DIRECT=1`)** → `ResolveLoopbackMode` returns `(attach=false)` on the game path =
  the pure direct SP path, byte-for-byte the pre-flip boot. The A/B knob + the reversible fallback. `menu "Play"`/
  `--play` (bespoke flat arena, no SimDriver/WorldBuildResult) is NOT wired for the loopback and is left direct —
  it never called AttachMpLoopback; wiring it needs new sim-spine plumbing, out of scope for a flip.
- **Fixture edge (Catalog A), unchanged by the flip:** grid-power (`GridPowerSource.PowerNetId => 0`) + gas-pump
  (`GasPump.PowerNetId => 0`) stay host-direct. Wiring a fixture (NetId 0) to a player-placed replicated deployable
  (NetId != 0): `RequestConnectWire` refuses (an end is 0, `PlayerController.cs:1860`) → `CompleteWire` falls to the
  direct local SP wire (`:524-531`) → the local PowerNet solves it, so the fixture powers the deployable host-locally.
  Not replicated to remote joiners (grid-power MP is a later task). Rope-tow (`Vehicle.AttachTow`, no Net/Send) is
  host-only; vehicles stay host-direct under the flip, so it is untouched. Both identical to the `--spconsume` path.
- **Gate:** builds clean; `unify.default_flip` L1 test locks the truth table (teeth: game-default-no-flags now
  `(attach,consume)==(true,true)`, `(false,false)` on the pre-flip gate); full `./test.sh` green. Direct path,
  the `NetId==0`/`Net*` forks, and the harness direct-construct all UNTOUCHED — deletion is a LATER P6 step.

**Deferred (post-flip fast-follows, non-blocking):** fall-damage wire-cap fidelity (hard falls cap at
32 dmg — safe direction); remote-puppet death/respawn rendering (cosmetic, MP-only); animal replication
(SP animals stay host-owned; invisible to remote MP joiners until an AnimalNetSync); clothing on remote
puppets.

Subsumes the "port SP features missing in MP" task.
