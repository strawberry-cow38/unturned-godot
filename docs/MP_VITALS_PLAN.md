# MP Vitals Plan — server-authoritative player vitals, damage, death & respawn

*Written 2026-07-18 on branch `mp-vitals` (off `main` @ 1d178ea). Design doc only — nothing here is
implemented yet. Companion to `docs/MP_PLAN.md` (§3.4 defines the target: "vitals
(health/food/water/stamina/infection/bleeding/broken) owner-only; other players expose only alive/dead +
coarse health").*

## 0. The gap, precisely

MP players are effectively immortal today. The three stubs that make it so, verbatim:

- `game/PlayerController.cs:1856` — `TakeDamage`: `if (NetAvatar) return;   // C2 v1: server avatars are
  invulnerable to LOCAL damage`
- `game/PlayerController.cs:1937` — `UpdateVitals`: `if (NetAvatar) return;   // v1 invulnerability (see
  TakeDamage): no local starvation/infection death on a server avatar either`
- `game/PlayerController.cs:1027` — `CheckFallDamage`: `if (NetAvatar) return;   // v1 invulnerability
  (see TakeDamage) -- and a broken-legs flag would silently eat the wire's jump bit`

Consequences on the live dedicated server:

- **Zombie bites are no-ops.** The server runs real `ZombieController` brains that chase and swing at the
  avatars, and the bite lands at `game/ZombieController.cs:371-372`
  (`player.TakeDamage(AttackDamage * mult, GlobalPosition); player.Infect(...)`) — straight into the
  :1856 early-return.
- **PvP is off.** `game/DedicatedServer.cs:80`: `Server.Combat.PvPEnabled = false; // D1 … shell vitals
  are still local, so server-side player damage would only rubber-band an unrendered death.`
- **The MP shell self-applies some damage client-side** (client-led, invisible to the server): its own
  fall damage (`CheckFallDamage` runs un-gated on the shell), the OOB kill
  (`PlayerController.cs:3041`), and the vehicle-explosion 150 (`PlayerController.cs:2980`). The server
  never hears about any of it; a shell that starves itself to death respawns locally while its avatar
  keeps standing.
- **No MP death/respawn UX exists.** `game/RemotePlayers.cs` has no `Alive`/`PlayerDied` handling at all —
  observers would render a dead player standing. `ClientWorldSession` subscribes to `HitConfirmed` /
  `ImpactFx` / `ZombieDied` / `GrenadeExploded` (`game/ClientWorldSession.cs:139-161`) but not
  `PlayerDied` / `PlayerRespawned`.

What ALREADY exists and this plan builds on (do not rebuild):

- **`core/UnturnedSim/PlayerVitalsSim.cs`** — the engine-free vitals sim (§1 below).
- **`core/UnturnedNet/ServerCombat.cs`** — server-side PvP damage, death and respawn are ALREADY
  implemented and L0-tested: `ApplyPlayerDamage` (:484-502) decrements `HealthExact`, flips `Alive`,
  bumps `Deaths`, schedules `RespawnAtTick = tick + RespawnDelayTicks` (175 ticks = the SP 3.5 s
  death-cam, :96), calls `ServerClearInput`, credits the kill, broadcasts `PlayerDiedEvent`; `Respawn`
  (:253-263) restores health, teleports to `CombatEntity.SpawnPos`, broadcasts `PlayerRespawnedEvent`.
  All of it dormant on the live server behind `PvPEnabled = false`.
- **`core/UnturnedNet/CombatReplication.cs`** — `PlayerCombatReplication` (SystemId 2, :343) already
  replicates alive/dead + a coarse health byte to EVERYONE: `public byte Health = 100; // coarse 0..100
  (the wire byte; server keeps the exact float)` (:349), `HealthExact` server-only (:355),
  `RespawnAtTick` (:359), `SpawnPos` (:362). Its own comment (:341) names this plan: "detailed owner-only
  vitals land with the Phase 6 interest blocks."
- **`core/UnturnedNet/SkillsReplication.cs`** — the owner-only block pattern to mirror verbatim (§5).
- **`core/UnturnedNet/ServerTransactions.cs:343-362`** — `OnConsume` already validates the cell, deletes
  the item, and applies `useHealth` into `HealthExact`; its comment names the rest: "food/water/stamina/
  infection have no server model yet … deferred with the vitals split."

So the plan is not "build a combat system" — it is: **run the vitals sim server-side, funnel every damage
source through the existing `ServerCombat` kill path, replicate the full vitals to the owner, and un-stub
the three gates.**

---

## 1. Ground truth: what `PlayerVitalsSim` actually simulates

`core/UnturnedSim/PlayerVitalsSim.cs` (52 lines, engine-agnostic — the server can instantiate it as-is):

State: `Health` / `MaxHealth` (floats, 0-100), `Stamina`, `Food`, `Water`, `Infection` (floats 0..1),
`StaminaRegenDelay` (seconds). `Multipliers` struct = four plain floats fed from `PlayerSkills`
(EXERCISE / CARDIO / SURVIVAL / VITALITY).

`Step(bool sprinting, bool survivalDrain, float dt, in Multipliers m)` (:33-49), one tick:

| rule | constant | line |
|---|---|---|
| stamina drains while sprinting | `-0.22/s * m.ExerciseStaminaDrain`, sets `StaminaRegenDelay = 1f` | :35 |
| stamina regens after a 1 s hold | `+0.33/s * m.CardioStaminaRegen` | :36 |
| hunger/thirst drain (only if `survivalDrain`) | food `-0.0050/s`, water `-0.0070/s`, `* m.SurvivalDrain` | :39-40 |
| infection clears slowly | `-0.01/s` | :42 |
| "sick" threshold | `Infection > 0.75f` | :43 |
| health regen while fed+hydrated, not sick | `+2/s * m.VitalityRegen` iff `Food > 0.30 && Water > 0.30` | :44-45 |
| starve/dehydrate/sickness damage | `-(sick ? 2 : 1.5)/s` iff `Food <= 0 \|\| Water <= 0 \|\| sick` | :46-47 |
| returns `true` when Health hits 0 this step | caller owns what death means | :48, doc :31-32 |

**What it does NOT simulate** (correcting a common assumption — the plan must not invent state): no
temperature, no oxygen/drowning, no bleeding damage-over-time, no broken bones. Verified by grep: the only
"oxygen"/"temperature" hits in the repo are HUD comments (`game/HUD.cs:16,64` — "virus/oxygen are
situational"). `Bleeding` and `Broken` are **shell-side flags on `PlayerController`**
(:51-53): `Bleeding` is a 5 s HUD-icon timer set by `TakeDamage` when `amount > 1f` (:1859) with **zero
DoT**; `Broken` is set by `CheckFallDamage` (:1029) and gates sprint (`_stance.Step(..., Broken, ...)`
:3107) and jump (`jump … && !Broken` :3118) until a `useHealBroken` consumable clears it (:75).

Damage entry points in the SP shell (all must reroute in MP):
- `TakeDamage(float amount, Vector3? fromPos)` (:1854) — zombies, bullets, melee, explosions, fall, OOB.
  Side effects: bleeding icon (:1859), pain overlay `Clamp(damage/40,0,1)*0.75` when `> 5f` (:1863),
  camera flinch (:1868-1879), death at 0 (:1881 → `Die()` :1884).
- `Infect(float)` (:62) — zombie bites (`Zombie.askDamage`'s `askInfect(b/3)`), `useVirus` consumables;
  applies `Skills.ImmunityInfectionMultiplier()`.
- `Consume(ItemAsset)` (:65-76) — `useHealth/useFood/useWater/useEnergy/useVirus/useDisinfectant/
  useStopsBleeding/useHealBroken` (fields: `core/UnturnedSim/ItemAsset.cs:45-48`; Food/Water are .dat
  0-100 divided by 100).
- `UpdateVitals(moving, dt)` (:1935) — steps the sim with the four skill multipliers, dies on `true`.
- `CheckFallDamage(verticalVel)` (:1025) — `FallMath.Hurts/BreaksLegs/Damage` with clothing
  (`PreventsFallingBoneBreak`, `FallingDamageMultiplier`) and STRENGTH skill.
- Death: `Die()` (:1884-1910) — ragdoll corpse, death-cam, 3.5 s `_deathTimer`; `Respawn()` (:1912-1929)
  — full vitals reset (:1915-1916), teleport to `Spawn`, corpse freed. **SP drops NO loot on death** —
  `Die`/`Respawn` never touch `Inventory`.

---

## 2. Design overview — one health, one kill path

```
 zombie bite (game, avatar) ──┐
 fall / OOB (game, avatar) ───┤   enqueue                     drain (deterministic order)
 vehicle explosion (game) ────┼──────────► ServerVitals ◄──────────── NetWorldServer.TickSimulation
 console / future sources ────┘            (core, NEW)                 (after Players.ServerStep)
                                             │  owns PlayerVitalsSim per player
 bullets / melee / grenades ────────► ServerCombat.ApplyPlayerDamage   (health decrements now go
 (already server-side, :484)                 │  through ServerVitals)
                                             ▼
                                   health <= 0 → ServerCombat.KillPlayer  (extracted :493-501 tail)
                                             │  Alive=false, Deaths++, RespawnAtTick, PlayerDiedEvent
                                             ▼
                              replication: SystemPlayerCombat coarse byte (everyone, unchanged wire)
                                         + SystemPlayerVitals owner block (NEW, SystemId 13 → v8)
```

Principles (the CLAUDE.md MP recipe, applied):
1. The server's `PlayerVitalsSim` instance is the ONLY authoritative health/food/water/stamina/infection.
   `CombatEntity.HealthExact`/`Health` become a **write-through mirror** updated at the same mutation
   sites (so observer coarse health can never fork from owner truth — §10 risk 6).
2. The client never self-applies vitals mutations in MP. The shell keeps a local `PlayerVitalsSim` as a
   **prediction/display copy**, overwritten wholesale by every owner-block echo (the
   `AdoptReplicatedSkills` pattern, `game/ClientWorldSession.cs:238-240`).
3. Every seam is null/false-gated so SP stays byte-identical (§6).
4. All damage converges on ONE death tail (`KillPlayer`), so death/respawn/kill-credit/events can't fork.

---

## 3. Component: `ServerVitals` — the per-player sim on the server tick

**New file `core/UnturnedNet/ServerVitals.cs`** (engine-free; `UnturnedNet` already references
`UnturnedSim` — `ServerCombat` uses `BallisticsMath`/`ExplosionMath`, `SkillsReplication` holds
`PlayerSkills`).

```csharp
public sealed class ServerVitals
{
    public sealed class Entry
    {
        public ushort OwnerPlayerId;
        public PlayerVitalsSim Sim = new PlayerVitalsSim();   // THE authoritative vitals
        public bool Bleeding;  public long BleedUntilTick;    // 5 s icon timer, server-side now
        public bool Broken;                                   // broken legs (fall), healed by consumable
        public long LastChangedTick;                          // owner-block delta dirtiness (Stamp(tick+1))
        public Vector3 PrevPos;                               // movement proxy for the sprint-drain input
    }
    public bool SurvivalDrainEnabled;      // the server's `survival` toggle (replaces the SP static, §10 risk 9)
    // damage queue: game-side sources (bite/fall/blast) enqueue; Step drains in deterministic order
    public void EnqueueDamage(ushort victim, float amount, byte cause);
    public void EnqueueInfection(ushort victim, float amount);     // applies IMMUNITY mult from server skills
    public void Step(long tick);           // drain queue → step every alive player's Sim → deaths via KillPlayer
    public Entry ServerAdd(ushort id, long tick);  public void ServerRemove(ushort id);
    public void ResetFor(ushort id, long tick);    // respawn: fresh sim (SP Respawn :1915-1916 parity)
}
```

- **Keying**: `Dictionary<ushort, Entry>` by `PlayerId` — the same key `CombatState` / `Skills` /
  `Inventories` use.
- **Init / teardown**: in the `PeerConnected` block of `core/UnturnedNet/NetWorldHost.cs` (:101-105,
  beside `CombatState.ServerAdd` / `Skills.ServerAdd` / `Inventories.ServerAdd`) add
  `Vitals.ServerAdd(peer.PlayerId, Session.CurrentTick)`; in `PeerDisconnected` (:121-124) add
  `Vitals.ServerRemove(peer.PlayerId)`.
- **Tick site**: `NetWorldServer.TickSimulation` (:163-177), inserted after
  `Players.ServerStep(Session.CurrentTick, (float)SimClock.FixedDelta)` and before
  `VehicleHost.Step` / `Combat.Step`:
  `Vitals.Step(Session.CurrentTick);` — mutation strictly before `TickReplication` (registered LAST),
  per the §2.5 order.
- **Step inputs**, all server-derived:
  - `sprinting`: `PlayerReplication.PlayerEntity` stance bits (`MoveInput.Stance`,
    `PlayerReplication.cs:133-142`; SPRINT = packed value 1) AND a real position delta
    (`(pe.Pos - entry.PrevPos).sqrMagnitude > ε`) — the SP predicate is `moving && Stance == SPRINT`
    (:1939).
  - `survivalDrain`: `SurvivalDrainEnabled` (server flag; the F1 `survival` verb routes to it via the
    server-gated console, `CommandConsole` 20).
  - `Multipliers`: from the server's authoritative `PlayerSkills`
    (`Skills.TryGet(id, out var sk)` → `sk.Skills.ExerciseStaminaDrainMultiplier()` etc.,
    `core/UnturnedSim/PlayerSkills.cs:115+`) — NOT the avatar body's default-level local skills.
  - dead players are not stepped (`Sim` doc line :32: "Callers must not step a dead player") — gate on
    `CombatState.Alive`.
- **Health unification**: `ServerCombat.ApplyPlayerDamage` (:488-489) changes from
  `cs.HealthExact -= damage` to `Vitals.ApplyDamageDirect(victim, damage)` which decrements `Sim.Health`
  and write-through-mirrors `cs.HealthExact = Sim.Health; cs.Health = (byte)Math.Clamp((int)Math.Ceiling(...), 0, 100)`
  (keep the exact :489 ceil convention). Every other reader of `HealthExact`
  (`OnConsume` :356-360 included) is rerouted through the same helper.
- **Death**: extract the `ApplyPlayerDamage` tail (:493-501) into
  `public void KillPlayer(ushort victim, ushort attacker, long tick)` on `ServerCombat` (idempotent —
  keeps the `!cs.Alive` guard). `ServerVitals.Step` calls it when `Sim.Step(...)` returns true or a
  drained queue entry brings health to 0 (attacker = the queued cause's attacker id, 0 for
  environment). `ServerCombat.Respawn` (:253) additionally calls `Vitals.ResetFor(id, tick)` and clears
  `Bleeding`/`Broken` — SP `Respawn` :1915-1916 parity.
- **Bleeding**: set server-side in the damage drain when `amount > 1f` (SP :1859), `BleedUntilTick =
  tick + 250` (5 s); cleared by timer, `useStopsBleeding`, and respawn. Icon state only — no DoT (SP
  parity).
- **Construction**: `NetWorldServer` ctor creates `Vitals` before `Combat` and passes it in
  (`ServerCombat` gains a `ServerVitals` ctor parameter, like the existing
  `PlayerCombatReplication state` parameter, :157-168).

---

## 4. Component: damage routing — every source → server → vitals

The client NEVER self-applies damage. Per source:

| source | today | plan |
|---|---|---|
| **zombie bite** | `ZombieController.cs:371-372` → avatar `TakeDamage`/`Infect` → `:1856` no-op | `ZombieController` UNCHANGED. The avatar's `TakeDamage`/`Infect` route through new null seams (§6): `if (NetAvatar) { ServerDamage?.Invoke(amount, cause); return; }`. `PlayerNetSync` wires them to `Vitals.EnqueueDamage/EnqueueInfection`. |
| **fall damage** | avatar: `:1027` no-op; shell: applies LOCALLY (client-led) | Un-stub `CheckFallDamage` for the avatar: it runs `FallMath` on the AUTHORITATIVE body's landing (`:3204` fires on both bodies) and enqueues via `ServerDamage`; `Broken` handling per §10 risk 8. Clothing/STRENGTH modifiers come from the avatar's adopted server inventory/skills. The shell keeps computing fx + predicted `Broken`, but its `TakeDamage` no longer writes health (RemoteVitals gate, §6). |
| **OOB kill** | shell-local only (`:3041` gated `!NetAvatar`) | Let the avatar's OOB check run too (drop the `!NetAvatar` from the gate; the damage routes through the seam like any other). Shell keeps its local check for fx only. |
| **blast — grenade** | ALREADY server-side: `ServerCombat.Explode` :464-472 → `ApplyPlayerDamage` (squared falloff, thrower included) | No change — flows through the unified health automatically. |
| **blast — vehicle explosion (the deferred 150)** | SP/shell-local only: `PlayerController.cs:2980` `if (_driving.Exploded) { ExitVehicle(); TakeDamage(150f); }` | Server-side: `VehicleNetSync` already publishes `v.Exploded` (`game/VehicleNetSync.cs:149`, `Exploded is never client-writable` `VehicleReplication.cs:157`). On the rising edge, apply 150 to every occupant (`DriverPlayerId`; passengers when they exist) via `Combat.ApplyPlayerDamage(occ, 150f, attacker: 0, ...)`; the existing "dead drivers exit" path in `VehicleHost.Step` (`NetWorldHost.cs:172`) ejects the corpse. Shell's :2980 becomes fx-only in MP. |
| **environmental tick** | starvation/dehydration/infection-sickness: in `PlayerVitalsSim.Step` :46-47 | Runs server-side in `ServerVitals.Step`. Drowning/temperature: **do not exist in the port** (§1) — nothing to route; out of scope. Bleeding: icon only, no DoT (SP parity). |
| **PvP (bullets/melee/grenade)** | server-side but disabled: `PvPEnabled` gates at `ServerCombat.cs:279/:377/:464`; `DedicatedServer.cs:80` forces false | Once death UX lands (P3), delete the `:80` override; default ON (`ServerCombat.cs:115`), env kill-switch `UG_DEDICATED_PVP=0` for the friendly test server. |
| **console `pdie` / cheats** | shell-local `_pdieTest` :3039 | MP console is already server-gated (`CommandConsole` 20); a server `pdie` verb calls `KillPlayer` directly. Low priority. |

**Ordering rule**: game-side sources (bite, fall, explosion) fire from Godot physics callbacks OUTSIDE
`TickSimulation` — they must **enqueue**, never mutate inline, and the queue drains at one deterministic
point (`Vitals.Step`). Otherwise damage-vs-command ordering is frame-dependent (§10 risk 2).

---

## 5. Component: replication — the owner-only vitals block (the v8 wire change)

**New file `core/UnturnedNet/VitalsReplication.cs`**, `SystemPlayerVitals = 13` appended to
`ReplicationIds` (`PlayerReplication.cs:16-28`; append-only, comment per the house rule). Mirrors
`SkillsReplication` (SystemId 5) structurally: `WriteFull`/`WriteDelta` both emit **at most ONE entry —
the receiving client's own** (`ctx.ClientPlayerId`, `SkillsReplication.cs:123-142`), `ReadSnapshot` is
the only writer on a client, mutations stamp `tick + 1` (`Stamp`, :76 — the compose-boundary
off-by-one), plus `StateHash()`/`StateHashFor(owner)` (:167-183).

**Wire layout** (after the standard block header), 9 bytes when present:

| field | encoding | notes |
|---|---|---|
| count | u8 (0 or 1) | owner-only; 0 = nothing for you this snapshot |
| owner | u16 | PlayerId (matches the skills block shape) |
| health | u8 | `Math.Ceiling(Sim.Health)` clamped 0-100 — same convention as the coarse byte (`CombatReplication.cs:489`); MaxHealth is fixed 100, not on the wire |
| food | u8 | `round(Sim.Food * 255)`; adopt divides by 255 (quantum 0.004 ≪ the 0.005/s drain rate) |
| water | u8 | same |
| stamina | u8 | same |
| infection | u8 | same |
| flags | u8 | bit0 = Bleeding, bit1 = Broken, bit2 = SurvivalDrainEnabled (lets the shell mirror the server's drain toggle for prediction parity, §10 risk 9); bits 3-7 reserved (append-only) |

Delta: dirty iff `LastChangedTick > baselineTick` — while regenerating/draining that's every tick, i.e.
~9 bytes @ 25 Hz owner-only ≈ 225 B/s. Fine; no cleverness needed.

**Registration**: server systems array (`NetWorldHost.cs:67`), client replicas (`NetWorldClient`, :307+),
composer/applier pick it up from those lists. **NOT added to `EnableSyncCheck`**
(`NetWorldHost.cs:141-147`) — owner-only systems differ per client by design (same reason Skills and
Inventory are excluded).

**Observers are already covered**: coarse health + alive/dead + kills/deaths keep riding
`SystemPlayerCombat` unchanged — the write-through mirror (§3) is what keeps the two views coherent.
Bleeding/broken/infection are owner-only (an observer has no HUD for them; retail exposes the same split
per MP_PLAN §3.4).

**Version**: `core/UnturnedNet/NetProtocol.cs:54` `Version = 7 → 8`. A new SystemId changes the snapshot
layout for every client, so old clients must version-reject at connect — that IS the bump's job. See §8
for the shared-bump coordination and re-golden list.

---

## 6. Component: un-stubbing `NetAvatar` + binding the shell/HUD to replicated vitals

Two sides, both using the codebase's existing null-seam discipline (`NetEnterVehicle`/`NetFire`/… —
null-default delegates set ONLY by the MP wiring, `ClientWorldSession.cs:437-466`):

**Server avatar side** (`NetAvatar == true`, bodies built by `game/PlayerNetSync.cs:77`):

```csharp
// PlayerController — new null seams, set ONLY by PlayerNetSync on dedicated avatars:
public Action<float, byte> ServerDamage;    // (amount, cause) -> Vitals.EnqueueDamage(playerId, ...)
public Action<float> ServerInfect;          //               -> Vitals.EnqueueInfection(playerId, ...)
public Action<bool> ServerBroken;           // fall broke/heal-cleared legs -> Vitals entry flag
```

- `TakeDamage` `:1856` becomes `if (NetAvatar) { ServerDamage?.Invoke(amount, cause); return; }` — with
  the seam unset (every existing L1 harness, SP) the behavior is EXACTLY today's no-op, which is what
  makes the P0 teeth test honest.
- `Infect` (:62) gets the same gate (`ServerInfect`); the server-side apply uses the server skills'
  `ImmunityInfectionMultiplier`, not the avatar's local default skills.
- `CheckFallDamage` `:1027`: the early-return is replaced by the avatar-path computation (Hurts/Damage on
  the avatar's own landing velocity) routing through `ServerDamage` + `ServerBroken`. The `Broken`
  flag's movement gating on the avatar is handled per §10 risk 8.
- `UpdateVitals` `:1937`: **the early-return STAYS.** The avatar body does not step vitals locally —
  `ServerVitals.Step` in core is the one authoritative stepping (running it in both places would
  double-drain). "Un-stubbing" here means the stepping exists server-side, not that the gate is removed.

**Client shell side** (`ClientWorldSession.SpawnShell`, :417):

```csharp
// PlayerController — one flag, set ONLY by ClientWorldSession.SpawnShell:
public bool RemoteVitals;   // MP: vitals truth is the owner block; local writes are prediction/fx only
```

- `TakeDamage` with `RemoteVitals`: apply the FX (pain overlay :1863, flinch :1868-1879, bleeding icon
  timer) but do NOT decrement `_vitals.Health` and do NOT `Die()` — covers fall (:1031), OOB (:3041),
  vehicle-explosion (:2980), `Explode` (:1061) on the shell.
- `UpdateVitals` with `RemoteVitals`: keeps stepping the local sim (stamina responsiveness for the stance
  FSM :3107) but skips the death branch (:1947).
- Local respawn timer `:3053` (`if (_deathTimer <= 0) Respawn()`): gated `!RemoteVitals` — the server
  owns respawn timing.
- **Adopt**: new `PlayerController.AdoptReplicatedVitals(VitalsReplication entry)` — the
  `AdoptReplicatedSkills` analogue (:1209), called every tick from `ClientWorldSession.ShellStep` beside
  the skills mirror (:238-240): overwrite `_vitals.{Health,Food,Water,Stamina,Infection}`, `Bleeding`,
  `Broken`, and `PlayerController.SurvivalDrain` from flags bit2. Track the previous adopted health; on a
  drop ≥ 1 HP fire the pain flash + bleed icon locally (sourceless — no camera kick, the :1852-1853
  precedent) so bites hurt visibly even though damage never executes client-side.
- **HUD needs zero changes**: `game/HUD.cs:65-75` reads `Player.Health/Food/Water/Stamina/Infection/
  Bleeding/Broken` through the shell properties (:48-62), which read `_vitals` — the adopt writes exactly
  there.
- **Death/respawn UX**: `ClientWorldSession` subscribes `Client.PlayerDied` / `Client.PlayerRespawned`
  (events already exist: `NetWorldClient` :333-334; EventIds 4/5).
  - Victim == me → `Shell.NetDie()`: the `Die()` visual block (:1884-1910 — corpse ragdoll, death-cam,
    viewmodel hide) without the local-timer respawn.
  - Me respawned → `Shell.NetRespawn()`: the `Respawn()` block (:1912-1929) minus the vitals write
    (echo carries it) and minus `GlobalPosition = Spawn` — position comes from the server's
    `ServerTeleport` (`ServerCombat.Respawn` :260) through the reconciler's adopt path, and the local
    restore must reset the interp snapshots (`TeleportTo`'s `_interpPrev = _interpCurr = pos` :100 —
    §7 risk 5: a bare `GlobalPosition` write is silently undone).
  - Victim == someone else → `RemotePlayers` hides/ragdolls that puppet; respawn restores it. (Today it
    renders dead players standing — nothing handles `Alive` in `game/RemotePlayers.cs`.)

---

## 7. Component: consume becomes server-authoritative

Phase A (just shipped on `main`, branch mp-parity-clientseams) is **client-led by disclosure**:
`game/PlayerController.cs:714-744` `TickConsume` applies the effects LOCALLY first —
`:720 Consume(_heldConsumable);   // apply Health/Food/Water/etc. (MP too: vitals stay client-led until
the vitals split; the server mirrors coarse health itself)` — then sends the delete intent
(`NetConsume(cp, cx, cy)` :731). Server-side, `ServerTransactions.OnConsume` (:343-362) validates the
cell + deletes the item + patches `useHealth` into `HealthExact` only.

**The flip** (P5):

1. `PlayerController.cs:720` becomes `if (NetConsume == null) Consume(_heldConsumable);` — SP and
   `MpLoopback` (seam null) byte-identical; the MP shell stops self-applying. The rest of `TickConsume`
   (the `FindBagCell` delete intent :731, the `left = count - 1` re-equip prediction :732, the
   revert-equip :742) is untouched.
2. `ServerTransactions.OnConsume` applies the FULL effect set into `ServerVitals`, mirroring
   `PlayerController.Consume` (:65-76) field-for-field: `useHealth` (through the unified health helper,
   replacing the :356-360 direct `HealthExact` patch), `useFood/useWater/useEnergy` (÷100 into the 0..1
   sim), `useVirus` (through `EnqueueInfection`, IMMUNITY-multiplied with the server skills),
   `useDisinfectant`, `useStopsBleeding` (clears the server bleed timer), `useHealBroken` (clears server
   `Broken` — and propagates to the avatar body, §10 risk 8). Keep the existing `ce.Alive` gate (:356) —
   a corpse can't eat.
3. The echo is the owner vitals block (§5) — no new event needed; the client's adopt (§6) lands the gain
   on the HUD in ≤ 1 snapshot (~40-80 ms). The existing L1 `net.shell_consume`
   (`game/testing/tests/NetTests.cs:2481`) keeps proving the delete/echo/re-equip loop.

Anti-cheat note: this closes the Phase-A gap where a client could claim any consume effect — the server
now derives ALL effects from the validated item in the server grid. A rejected consume (`asset == null ||
!IsConsumable` :350) now correctly results in nothing anywhere (Phase A left the client having self-fed).

---

## 8. Anti-cheat & wire/version coordination

**Anti-cheat posture after this plan**: the server owns 100 % of vitals state; the client sends only
intents (Fire/Melee/Grenade — already; Consume — after P5) and inputs. No client message can write
health/food/water/infection/bleeding/broken. Sender identity always from the connection
(`OnConsume(ushort sender, …)` pattern). Remaining disclosed deferral: **stamina does not yet gate the
server's movement** — the wire stance is client-resolved (`:1671 AlwaysHeadroom` /
`:3099` "a NetAvatar takes the wire stance"), so an abusive client can sprint at 0 stamina; server
stamina is bookkeeping for HUD/regen/consume. Add a `// TODO(mp-security)` beside the M2 markers in
`ServerTransactions.cs` and a line in MP_PLAN §7's revisit list. (Same class of test-server posture as
deployable ownership.)

**Version**: `NetProtocol.Version` 7 → **8** (`NetProtocol.cs:54`), carried by the SystemId 13 addition.
**Task #46 (vehicle envelope tightening + client-authoritative exit spot) also targets v8 — one
coordinated bump, one deploy.** Whichever branch merges second does NOT bump again to 9; both changes
ride the single 7→8 break, and the `Version = 8` doc-comment names both ("v8 (mp-vitals + #46): owner
vitals block SystemId 13; vehicle-exit …"). Do not deploy either alone if the other is mid-flight on the
server — the version gate makes a half-deployed pair reject clients, which is correct but should be a
planned window.

**Re-golden / version-byte checklist** (the §6 "bump + re-golden in the same commit" discipline):
- `tests/UnturnedNet.Tests/PacketHeaderGoldenTests.cs` — header goldens carry the version byte (:47
  "Re-goldened for Version=7…", :53); re-golden for 8.
- `tests/UnturnedNet.Tests/SnapshotFramingGoldenTests.cs` — framing constants change with the new block;
  re-golden (:13).
- `tests/UnturnedNet.Tests/MoveInputWireTests.cs` — payload unchanged; verify the goldens still hold
  (they encode no version byte — comment :29-30) and update the header-adjacent comments.
- New golden: the vitals owner block itself (byte-exact write of a known entry), so the NEXT change to it
  is caught — mirror the existing golden-test style.

---

## 9. Phased build order (each phase lands with its teeth test, same commit)

**P0 — teeth baselines (tests only, no product change).**
- New L1 `net.shell_bite_death_respawn` in `game/testing/tests/NetTests.cs` (the
  `net.shell_consume`/`net.shell_starvation_hold` harness shape: `BuildFullWorld(Dedicated)` +
  `MemNetwork` + `ClientWorldSession` + `DedicatedServer { RemoteAvatars = true }`), but built with
  zombies enabled and one zombie spawned adjacent to the avatar (or its bite driven directly by calling
  the avatar's `TakeDamage` the way `ZombieController.cs:371` does). **P0 form asserts TODAY'S broken
  truth**: after seconds of biting, server `CombatState.Health` is still 100, `Alive` still true, shell
  health still 100 — with a comment that each subsequent phase flips one assertion. This is the
  regression-rule anchor: when P2/P3 land, the same test is rewritten to its final form (bite → health
  drops → death → death-cam → respawn at `SpawnPos` → vitals reset → DESYNC-QUIET) and MUST fail if any
  phase's wiring is reverted.
- L1 fall baseline: shell steps off a ledge → assert server `CombatState.Health` stays 100 while the
  SHELL's local health dropped (documents the client-led divergence P2 erases).

**P1 — server sim + tick.**
- Files: `core/UnturnedNet/ServerVitals.cs` (new); `NetWorldHost.cs` (ctor wiring, `PeerConnected`/
  `PeerDisconnected`, `TickSimulation` insertion); `ServerCombat.cs` (`ServerVitals` ctor param,
  `ApplyPlayerDamage` reroute through the unified health helper, `KillPlayer` extraction, `Respawn` →
  `Vitals.ResetFor`); `ServerTransactions.cs` `OnConsume` :356-360 rerouted through the helper
  (behavior-identical).
- Tests (L0, new `tests/UnturnedNet.Tests/ServerVitalsTests.cs`): join creates a full-vitals entry;
  disconnect removes it; sprint stance + movement drains stamina, regen after the 1 s hold, CARDIO/
  EXERCISE multipliers from the server skills entry observed; `SurvivalDrainEnabled` starves food/water
  and kills at 0 → `PlayerDiedEvent` broadcast + `RespawnAtTick` honored + respawn resets the sim;
  queued damage drains deterministically (two same-tick sources apply in enqueue order); dead players not
  stepped. Teeth: each fails with the `TickSimulation` insertion removed.

**P2 — damage routing.**
- Files: `PlayerController.cs` (seams `ServerDamage`/`ServerInfect`/`ServerBroken`, `RemoteVitals`
  fx-only gates at :1856/:1027/:3041/:2980, avatar OOB un-gate); `PlayerNetSync.cs` (wire the seams on
  avatar build :77); `VehicleNetSync.cs` (explosion rising-edge → occupant 150); `ZombieController.cs`
  unchanged; `ClientWorldSession.cs` (`RemoteVitals = true` in `SpawnShell`).
- Tests: L0 — `EnqueueDamage`/`EnqueueInfection` entry points incl. IMMUNITY multiplier + bleed-flag
  set + kill-at-zero-through-queue. L1 — `net.shell_bite_death_respawn` flips its first assertions:
  bites now drop server `CombatState.Health` and the client's own `CombatState` replica (coarse byte —
  **already replicated to everyone**, so no wire change is needed to observe P2); L1 fall test flips:
  server health drops, shell no longer self-applies. Teeth: revert the `PlayerNetSync` seam wiring →
  both fail (seam-null = today's no-op).

**P3 — death + respawn UX, PvP on.**
- Files: `PlayerController.cs` (`NetDie`/`NetRespawn`, `:3053` gate); `ClientWorldSession.cs`
  (`PlayerDied`/`PlayerRespawned` subscriptions); `RemotePlayers.cs` (puppet death/respawn visuals);
  `DedicatedServer.cs` (delete :80, add `UG_DEDICATED_PVP=0` env gate).
- Tests: L1 `net.shell_bite_death_respawn` final form (death-cam engaged = shell `_dead`, corpse exists;
  respawn at `SpawnPos` with interp reset — assert no post-respawn snap-back; vitals full; DESYNC-QUIET).
  L1 PvP smoke: two sessions, one shoots the other → victim's coarse health drops, killer gets
  `HitConfirm`, kill credited. L0: `KillPlayer` idempotence (double-kill same tick), death-while-driving
  (dead driver exits, corpse doesn't ride).

**P4 — owner replication + HUD bind (THE v8 commit).**
- Files: `core/UnturnedNet/VitalsReplication.cs` (new); `PlayerReplication.cs` (`SystemPlayerVitals =
  13`); `NetWorldHost.cs` (register both sides); `NetProtocol.cs` (`Version = 8` + doc comment);
  `PlayerController.cs` (`AdoptReplicatedVitals`, pain-on-drop); `ClientWorldSession.cs` (adopt in
  `ShellStep` beside :238-240); golden re-baselines per §8.
- Tests: L0 — owner-only privacy (client B's block is empty of A — the skills-test mirror); quantization
  round-trip (0..1 ↔ u8 within 1/255); delta dirtiness (unchanged vitals stop writing once regen
  completes… note stamina full + fed = quiescent entry goes delta-silent); `StateHashFor` parity;
  vitals-block golden bytes. L1 `net.shell_vitals_hud`: bite → the SHELL's `_vitals.Health` (the HUD
  source) converges to the server value within a snapshot; infection rises then decays; bleeding flag
  shows and clears. Teeth: unregister the system → adopt never fires → fails.

**P5 — consume server-authoritative.**
- Files: `PlayerController.cs:720` (the one-line flip); `ServerTransactions.cs` `OnConsume` full-effect
  apply (§7).
- Tests: L0 — `OnConsume` applies every field (bandage stops bleeding; medkit heals broken; energy
  restores stamina; virus infects IMMUNITY-scaled; disinfectant lowers; dead/invalid rejected mutates
  nothing). L1 — extend `net.shell_consume` (:2481): pre-drain server food to 0.5 (test hook), eat a
  Food item → server food rises and the shell's HUD food converges via the echo; a consume of an empty
  cell changes nothing anywhere. Teeth: revert the :720 flip → the "shell did not self-apply ahead of
  the echo" assertion fails.

Gate for every phase: `./test.sh` (L0+L1). No L2 goldens move (death visuals reuse existing SP
rendering; no render-path change).

---

## 10. Adversarial self-review — how this plan fails, and the mitigations

1. **Vitals fight between shell prediction and the echo.** The shell steps its local sim between
   snapshots AND adopts wholesale every tick — if the server disagrees persistently (skill mismatch,
   drain-flag mismatch), the HUD oscillates. *Mitigation*: the shell's inputs are mirrored FROM the
   server (skills via `AdoptReplicatedSkills`, drain flag via flags bit2), so steady-state deltas are
   sub-quantum (max rate 0.33/s regen × 40 ms = 0.013 < 1/255 × 4). Vitals are excluded from the
   sync-check by design, so no false desync banners. Residual jitter is invisible at u8 HUD-bar
   resolution.
2. **Death/respawn races.** A heal command and a killing bite land the same tick; or death fires while
   the composer is mid-tick. *Mitigation*: game-side damage ENQUEUES and drains only in `Vitals.Step`,
   giving one deterministic order per tick (commands dispatch → movement → vitals drain+step → combat);
   `KillPlayer` keeps the `!cs.Alive` idempotence guard (:487); all mutation precedes `TickReplication`
   (registered LAST — the join-race lesson from MP_PLAN §7). Death-while-driving rides the existing
   dead-driver-exit in `VehicleHost.Step`.
3. **The consume flip vs the just-shipped Phase A.** A v7 client (self-applies food) against a v8 server
   (expects intent-only) would double-apply — but it can never connect: the version gate rejects at
   handshake. The re-equip prediction (`left = count - 1` :732) never depended on the local `Consume`
   call, so the flip doesn't disturb it. The one behavioral change a player feels: the food/water bar now
   moves one RTT+snapshot late (~100 ms) — acceptable; if it reads badly, a cosmetic "pending gain"
   shimmer can be layered later without wire changes.
4. **Fall damage on the predicted vs authoritative body.** The shell and avatar are separate bodies; under
   loss the avatar coasts (bounded by MaxCoastTicks=12 — proven pinned by `net.shell_starvation_hold`),
   so landing velocity can differ: phantom local pain with no real damage, or server damage with no local
   thud. *Mitigation*: only the SERVER's number is truth (shell writes nothing); `DeterministicGround`
   already forces identical vertical integration on a clean link (the mp-rubberband fix), so divergence
   only appears during packet loss and self-heals via the echo within 1-2 snapshots. Residual: a blackout
   ghost-fall could hurt a player "unfairly" — bounded by the coast pin; accepted for the test server,
   noted next to the stance deferral.
5. **Respawn-spawn selection + teleport races.** Respawning at `CombatEntity.SpawnPos` (join spawn — SP
   `Spawn` parity; beds/respawn points explicitly deferred) via `ServerTeleport` while the shell holds
   stale prediction → the classic silently-undone-teleport (§7 risk 5) or a reconciler yank.
   *Mitigation*: `NetRespawn` resets `_interpPrev/_interpCurr` (the `TeleportTo` :100 pattern) and the
   prediction history; server-side `ServerClearInput` on death (:498) already stops corpse drift; the L1
   asserts no post-respawn snap-back. Also assert `SpawnPos` is re-terrain-snapped if the map changed
   under it (it's captured at join from the same spawn the shell got).
6. **Observer coarse-health vs owner-full skew.** Two encodings of one value drift if mutated at
   different sites (it already half-happened: `OnConsume` :359 uses `RoundToInt` where combat :489 uses
   `Ceiling`). *Mitigation*: ONE write-through helper in `ServerVitals` owns both the float and the
   coarse byte with the `Ceiling` convention; all writers (combat, consume, environmental) route through
   it; an L0 asserts byte == ceil(float) after every mutation kind. Kill the :359 rounding divergence in
   P1.
7. **Broken legs vs the wire's jump bit** — the exact reason `:1027` was stubbed ("a broken-legs flag
   would silently eat the wire's jump bit"). If the avatar gates jump/sprint on a `Broken` the shell
   doesn't have (or vice versa), movement forks and the reconciler lurches. *Mitigation*: both bodies
   COMPUTE `Broken` locally from their own deterministic landing (same `FallMath`, same
   `DeterministicGround` integration → same tick on a clean link); the replicated flag is HUD/heal
   state. Clearing (`useHealBroken`) flows server → both: the echo clears the shell, `PlayerNetSync`
   pushes `Vitals.Broken == false` onto the avatar body each tick. Skew window ≈ 1 RTT, same class as
   the already-accepted client-resolved stance; the L1 death test runs DESYNC-QUIET to catch it
   regressing.
8. **Double-stepping or never-stepping the sim.** The avatar's `UpdateVitals` gate stays (correct), but a
   future refactor could remove it and double-drain server-side. *Mitigation*: an L0 pins the rate (food
   after N ticks == exactly N × 0.005 × dt) — any double-step fails it; a comment at :1937 points to
   `ServerVitals` as the one stepper.
9. **`SurvivalDrain` static vs the server flag.** SP's toggle is a static
   (`PlayerController.SurvivalDrain` :60, F1 `survival` verb `game/DevConsole.cs:186-189`); if the MP
   shell's static and the server flag disagree, the shell's predicted food drains while the server's
   doesn't (or vice versa) — permanent adopt-fighting. *Mitigation*: the server flag is authoritative,
   replicated as flags bit2, adopted into the static every tick; the dedicated console's `survival` verb
   mutates ONLY the server flag.
10. **Stamina anti-cheat gap reads as "done" when it isn't.** This plan replicates stamina but does NOT
    make the server enforce it against the wire stance (§8). *Mitigation*: disclosed loudly — TODO
    marker + MP_PLAN §7 revisit entry in the same commit as P1, so the posture list stays honest.

---

## 11. File touch-list (implementation index)

| file | change | phase |
|---|---|---|
| `core/UnturnedNet/ServerVitals.cs` | NEW — per-player sim + damage queue + write-through health | P1 |
| `core/UnturnedNet/NetWorldHost.cs` | ctor/join/leave/tick wiring; register vitals system both sides | P1, P4 |
| `core/UnturnedNet/ServerCombat.cs` | vitals param; `ApplyPlayerDamage` reroute; `KillPlayer` extraction; `Respawn` reset | P1 |
| `core/UnturnedNet/ServerTransactions.cs` | `OnConsume`: reroute health (P1) → full effect apply (P5); TODO(mp-security) stamina note | P1, P5 |
| `core/UnturnedNet/VitalsReplication.cs` | NEW — SystemId 13 owner-only block | P4 |
| `core/UnturnedNet/PlayerReplication.cs` | `SystemPlayerVitals = 13` (append-only) | P4 |
| `core/UnturnedNet/NetProtocol.cs` | `Version = 8` (coordinated with Task #46 — single bump) | P4 |
| `game/PlayerController.cs` | seams + `RemoteVitals` gates + `NetDie`/`NetRespawn` + `AdoptReplicatedVitals` + the :720 consume flip | P2-P5 |
| `game/PlayerNetSync.cs` | wire avatar seams; push server `Broken` onto the body | P2 |
| `game/VehicleNetSync.cs` | explosion rising-edge → occupant 150 | P2 |
| `game/ClientWorldSession.cs` | `RemoteVitals`, died/respawned subscriptions, vitals adopt | P2-P4 |
| `game/RemotePlayers.cs` | puppet death/respawn visuals | P3 |
| `game/DedicatedServer.cs` | remove the `:80` PvP-off override; `UG_DEDICATED_PVP` env gate | P3 |
| `tests/UnturnedNet.Tests/ServerVitalsTests.cs` (+ golden updates) | NEW L0 suite; re-goldens | P1-P5 |
| `game/testing/tests/NetTests.cs` | `net.shell_bite_death_respawn`, fall, `net.shell_vitals_hud`, consume extension | P0-P5 |

*Not in scope*: temperature/oxygen/drowning (don't exist in the port), bleeding DoT (SP has none),
loot-drop-on-death (SP keeps inventory through death — MP matches SP; retail's drop-everything is a
deliberate future gameplay decision), bed/claimed respawn points, stamina-gated server movement
(disclosed deferral), any deploy (the v8 rollout is coordinated with Task #46 outside this worktree).
