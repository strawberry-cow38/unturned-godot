# MP wire gaps, part A: host enrichment (ledger row 15, protocol 80)

Before this slice a joined client reported fourteen `network.wireGaps`. Three of
them were host state the client could not see:

- **repair:** the remote's blowtorch was refused.
- **weather and day:** each process rolled its own.
- **vehicle part health:** a remote drew every engine and rotor as healthy and
  never saw fire.

This slice closes those three over the wire. It also sorts the remaining tokens
into two lists:

- gaps still to port;
- data the Godot source never sent to a joined client either.

Slices B (held-weapon tracers and warhead pictures) and C (proximity vehicle
enter) are separate.

## Wire ids (v80, one place)

| Kind | Id | Name | Payload |
| --- | --- | --- | --- |
| Protocol | 80 | `NetProtocol.Version` | v79 peers are refused (VersionMismatch) |
| Command | 101 | `CommandToolUse` | `ToolUseCommand { bit Active }` |
| System | 21 | `SystemEnvironment` | `EnvironmentReplication` singleton |
| System 9 tail | — | `VehicleReplication` entity | presence bit + part tail (below) |

No event id was taken. The fishing branch also claims v80, commands 103/104 and
event 78. The integrator renumbers at merge, so the ids above are the only ones
to move. They are pinned in these goldens:

- `CommandTableGoldenTests`;
- `SystemTableGoldenTests`;
- `PacketHeaderGoldenTests`. The keepalive version byte moves from `0x4F` to
  `0x50` and no other byte changes.

## Repair tool for remote players

- **Source.** `PlayerController.HasBlowtorch` is a held melee with `Repair`. The
  shell repairs the car or deployable it looks at while LMB is held, and salvages
  a cooled wreck.
- **Host.** The host already ran this per owner in `BodyCombatServices.ToolUse`
  and `StepTool`.
- **Command 101.** A remote now sends Command 101, registered in the
  `BodyCombatServices` constructor (the `VehicleFuelServices` pattern).
  - The validator requires a live sender.
  - Tool, target, rate and reach all come from the sender's accepted held item
    and pose, never from the payload.
- **Remote send rate.** `RemoteSession.SendRemoteToolUse` sends a change of state
  at once, and repeats a held tool at most once per bridge tick (the host window
  is 5 ticks).
- **Bug fixed.** The tool state was one `_toolOwner`, target and clock for the
  whole server. A second repairer's ToolUse overwrote the first, so only the last
  caller repaired. Tool state is now a per-owner dictionary.
- **Salvage by a non-listen owner.** A completed deployable-wreck salvage by an
  owner who is not the listen player is dispatched through the original
  `CommandSalvageDeployable` validator under that owner's identity. Before, it
  went out on the listen owner's client, which failed `CanSalvageWreck`, and on a
  dedicated host it hit a null client.

## Weather and day number for remote clients

The source never replicated weather. `WeatherManager.Attach` seeds from
`GD.Randi()` in every process, so a remote's `TemperatureFeedback` read day 0 and
no weather.

**System 21** carries what a remote needs to rebuild the host's
`WorldEnvironment.State()` without a second simulation:

| Field | Encoding |
| --- | --- |
| seed | i32 |
| base day | i32 |
| weather type index | presence bit + u8 |
| stage | 2 bits |
| blend, puddle, snow | u8 fractions |

- Values are quantized before they are stored, so hashes match exactly.
- The block dirties only on a quantized change: about 13 small blocks a second
  during a 20 s fade, and one bit per delta snapshot otherwise.
- It is part of the sync-check set.

**Host side.**

- `WorldEnvironment.Publish` runs at construction and at the end of every
  `Step`.
- The type and schedule table is built by `WorldEnvironment.Tables.Build()`, so
  the host and the remote assemble the same table and an index names the same
  weather on both.

**Remote side.** `RemoteEnvironment` derives:

- the day, from `DayAt(baseDay, clock, tick)`;
- the outlook, from `Tables.Outlook(seed, day)`, cached per day;
- the state row, from the shared `WorldEnvironment.StateOf` projection.

**Consumers.**

- `worldClock.day` and `weather` (including `today`, `tomorrow` and `dayMeanC`)
  are no longer null on a remote. They have the host's values, which the native
  traffic phase and the radio forecast board need.
- `TemperatureFeedback` uses the replicated day, active type and blend.

## Vehicle part health

The System 9 entity gains a presence-bit tail after `CoupledTrailerNetId`,
following the v78 System 6 precedent:

| Field | Encoding |
| --- | --- |
| engine, main rotor, tail rotor | u8 fraction of the spec maximum |
| drowned | bit |
| fuse lit | bit |
| burn tick | presence bit + i32 |

- **Fraction rules.**
  - 255 means intact, or the vehicle has no such part.
  - 0 means exactly dead.
  - Any live remainder is at least 1, so rounding never kills an engine.
  - Only an intact part reads 255.
- **Burn tick.** It is signed, because a restore early in the session can place
  it before tick 0.
- **Fuse lit.** The task did not list this field. It is needed because `onFire`
  is `Fuse or Burn`.
- **Cost.** An untouched vehicle costs one bit.

**Host side.**

- `BodyCombatServices.PublishParts` runs at the end of `StepWorldServices`, so
  it serves both listen and dedicated hosts.
- It copies the exact `VehicleServices` part pools and the fuse/burn lifecycle,
  quantized through `ServerSetParts`. That call dirties only on a change.

**Remote side.**

- `ReplicaRows` fills `engineHealth`, `mainRotorHealth` and `tailRotorHealth`.
  Each is the fraction times the same maximum the native `registration` gives
  the host:
  - engine: `Health*10*0.4`;
  - rotors: `MainRotorHp` / `TailRotorHp`, or `Health*0.45` / `Health*0.28` on
    helicopters only.
  - The maxima come from the shared `vehicles.json` spec.
- It also fills `engineDrowned`, `onFire = fuse || burn` and
  `burnSeconds = (appliedTick - burnTick) * 0.02` (-1 when not burning, as on
  the host), and `wreckSalvageable` from that.
- Host rows keep the exact service values.

## `RemoteWireGaps` and `sourceAbsent`

`network.wireGaps` now lists only `heldCosmeticBullets` and
`heldWarheadEffects`; both are slice B.

`network.sourceAbsent` lists data that the Godot source never sent to a joined
client. These are not to be ported. Each was checked in `game/`:

| Token | Source evidence |
| --- | --- |
| explicitRespawn | `PlayerController.Die`: a remote server leaves the death-screen buttons dead (`_serverOwnedRespawn && NetRequestRespawn == null`). Only the automatic respawn runs. |
| acceptedImpactDetails | `ClientWorldSession` `ImpactFx` renders the position and flesh/not-flesh only. |
| stuckProjectilePose, projectileLifetime | `ProjectileReplicaView` mirrors in-flight System 4 entities and frees the node when the server retires them. |
| wildlifeHostState | `AnimalPuppets` interpolates the transform and anim byte. It has no health and no corpse clock. |
| password | The original handshake has no password field. |
| acceptedShotTick | `PlayerFiredEvent` carries origin, direction and gun only. |
| zombieHostState | See the finding below. |

### Zombie finding (decision for the user)

**Source.** In the Godot source, remote MP players do **not** see zombies:

- `ClientWorldSession.cs:50,170,252` removed the `ZombiePuppets` renderer "with
  the zombie system".
- `DedicatedServer.cs:63` removed `ZombieNetSync`. Nothing publishes
  `Server.Zombies`.
- The current horde (`ZombieChunkField`) is built only in the SP/listen
  `WorldBuilder` branches, with `Player = player`. It streams and sights on that
  one local player. The dedicated build creates none.
- The rewrite has no attack on players at all (`ZombieBody.Damage` is only
  incoming).

**Bevy port.**

- The extractor maps the field's `Player == null` branch to every server
  player, so on a listen or dedicated host the horde streams around remote
  players and chases them.
- No zombie damages anyone, so the planner's worry ("remote players are attacked
  by zombies they cannot see") does not hold today.
- A remote does receive `hasBody:false` System 3 rows, which native drops.

**Decision needed.** Two options:

1. Keep source parity: remotes see no zombies, and the horde should anchor only
   on the local player, as in the source.
2. Add a remote zombie presentation path, which the source removed.

Nothing was changed here.

## Verification

- **Core (`HostEnrichmentWireTests`, 5).**
  - System 21: full, delta, unchanged (one-bit delta, a sub-quantum republish
    does not dirty), truncation and hash.
  - System 21 over the real snapshot plane.
  - Vehicle tail: one bit when intact; all fields round-trip, including a
    negative burn tick; truncation keeps the accepted view; delta and
    no-dirty-on-same; the 0/1/254/255 rules.
  - Command 101 round-trip.
  - A v79 handshake is refused.
  - The three goldens are updated.
- **Managed, real UDP (`RemoteSessionTests.HostEnrichment.cs`, 4).**
  - A remote blowtorch raises host HP; bare hands do not; release stops it.
  - Two simultaneous repairers both repair.
  - Host `weather rain` reaches the remote's `weather.name`, stage, blend and
    `today`, and `worldClock.day`. After a forced day wrap, the remote's day,
    `today`, `tomorrow` and `dayMeanC` equal the host's.
  - Host `hurtmain` reaches the remote's `mainRotorHealth`; `killtail` reads
    exactly 0.
  - The two existing assertions that expected a null remote weather now assert
    the replicated view and that the remote owns no `WorldEnvironment`.
- **Negative controls.** Backups are in `bevy/.local/nc-backup/`.
  - Command 101 unregistered: both repair tests fail.
  - A single shared tool restored: the two-repairer test fails ("the listen
    owner's repair is not stolen").
  - Environment publish dropped: the weather test and both updated older tests
    fail.
  - `PublishParts` dropped: the hurtmain test fails.
- **Two-process visual check.** The setup was:
  - a managed v80 listen host process (frozen bridge, JSONL driver);
  - a separate native client joined over UDP on Xvfb with lavapipe, using a
    native binary built at port/bevy (no native change in this slice) and
    `UG_BRIDGE_DLL` pointing at the frozen v80 bridge.

  Run with the host set to `weather rain`:
  - The client's opened capture `bevy/artifacts/row15a/client-rain-joined.png`
    shows rain streaks, wet fog and an overcast sky.
  - Its sidecar has the host's `Default Rain`, Active, blend 1, puddle 0.65 and
    `today`/`tomorrow`.

  The same setup with `weather clear`:
  - `client-clear-joined.png` shows a dry, clear beach from the same spawn.
  - Its sidecar has `Clear`, blend 0.

  Both clients reported `worldClock.day` 0, equal to the host's. The first rain
  join hit the known software-client first-join timeout
  (DEDICATED_SERVER_PORT "Remaining"). Rejoining through Play → Direct Connect
  worked, and the menu capture before the rejoin already carried the replicated
  weather at blend 0.23.
