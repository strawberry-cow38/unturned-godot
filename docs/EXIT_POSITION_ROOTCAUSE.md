# Root cause: driver exits the vehicle at the ENTRY position (MP, post-wedge-fix)

Read-only diagnosis, code as of `main` @ `7ce2305`. Every claim is grounded in file:line from the
actual source; timeline claims come from `git log`/`git show` and the live unit's journal (read-only).

## TL;DR — verdict (B): a real bug in current main, but not where the suspects pointed

**The exit spot is computed CLIENT-SIDE from the vehicle replica** (`OnVehicleExited`,
`game/ClientWorldSession.cs:245-249`: `exit = v.Pos + right*2.4 + up`). That computation is only as
good as the replica's freshness — and two other systems conspire to hand it a *stale* replica while
*hiding the staleness from the driver*:

1. **The 7ce2305 recovery hold blacks out the whole snapshot stream.** While a reliable recovery
   full is in flight, the server sends this client **zero** unreliable snapshots — not just no
   vehicles, no *anything* (`NetWorldHost.cs:265-269`: `continue` before compose). Every replica on
   the driver's client freezes at the hold-start state. Reliable **events still flow** — including
   `VehicleExited`.
2. **Dead-reckoning extrapolates forever, so a frozen replica still "drives".**
   `VehicleReplicaView._Process` targets `e.Pos + LinVel * SinceSnap` with **unbounded**
   `SinceSnap` (`game/VehicleReplicaView.cs:60-66`). A replica frozen mid-drive with nonzero
   `LinVel` keeps the puppet gliding along its last velocity indefinitely — the car visibly "moves"
   on the driver's screen without a single snapshot arriving. **"The car moves on his screen" does
   NOT prove the replica was current.** The chase-cammed shell rides that extrapolating puppet
   (`PlayerController.cs:1241`), so the *drive feels normal*.
3. At exit, the reliable `VehicleExited` fact arrives (events are not held) and the handler
   computes the spot from the **frozen** `v.Pos` — the hold-start position. The freeze latches at
   the moment of the ack gap, and on this client that is **drive start** (engine-audio init, chase
   cam first frame, dust-particle/shader first render — the same first-render hitch profile
   documented in `docs/DRIVE_DRIVER_VIEW_ROOTCAUSE.md` §1.3). Hold-start car position ≈ where it
   was parked ≈ **the entry spot**. `ExitPuppet` places him there (`PlayerController.cs:1211-1214`).
4. **The reconciler cannot correct it**, because his own player entity replica froze with the same
   blackout: `e.LastProcessedInputSeq` is the stale pre-ride seq, and stale acks are ignored by
   design (`core/UnturnedNet/Prediction.cs:71-74` — `!NetSeq.IsNewer(seq, _lastAckSeq) → return
   false`). The reconciler is **inert**, not wrong — nothing drags the shell anywhere. He stands at
   the entry until fresh state round-trips (see §4 for why that can take many seconds or never
   happen).

Server-side, everything really is correct — `ServerExit` teleports his entity beside the *real*
(post-drive) car (`core/UnturnedNet/VehicleReplication.cs:509-518`) — the client just never applies
that state before, or in some cases long after, placing him.

This is the only mechanism that reconciles all three hard observations at once:
car-visibly-moves (extrapolation), exit-at-entry (frozen `v.Pos` = hold-start = drive-start ≈
entry), server-code-correct (it is; its output is simply not consumed).

---

## 1. Suspect 1 ("stale client build") — REFUTED, loudly

The prompt's preferred easy out does not survive `git log -L`:

- `OnVehicleExited` (`game/ClientWorldSession.cs:239-258`), `RidePuppet`
  (`game/PlayerController.cs:1228-1242`), `ExitPuppet` (`:1211-1224`) and the `_PhysicsProcess`
  ride branch (`:2651`) each have exactly **one** commit in their `-L` history: `2efe89e` (C6,
  2026-07-17 06:57). **The replica-based exit and the puppet ride-along shipped with ride mode
  itself and have never been touched.** There is no client build that can ride at all but carries
  different exit logic.
- A client that renders the car **moving** at all must have `4bcb297` (17:15:29 — pre-4bcb297 the
  puppet *mesh* renders frozen at its stale physics transform under the project-wide
  `physics_interpolation=true`; see the 4bcb297 commit message, `game/Vehicle.cs:884-889`). The
  only client-code commit after 4bcb297 is `6a84813` (a `--headless` window guard). **A driver who
  saw the car move was running, functionally, current main.**
- Server side: the watched DLL was rebuilt 19:45:48 and the unit restarted 19:45:51
  (`ExecMainStartTimestamp`), after `7ce2305` (19:31:52) — the live server IS the fix build. The
  live session ran 19:50–21:09 UTC.

So both ends were current. The bug is in `main`.

## 2. The mechanism, step by step (file:line)

### 2.1 The blackout

- Any ≥64-tick (1.28 s) gap in the server *receiving* this client's snapshot acks latches
  `WillSendFull` (`core/UnturnedNet/SnapshotComposer.cs:212-217`). The driver's client is a
  graphical WAN client whose single-threaded main loop stops acking for the entire duration of any
  long frame — the exact profile that latched the original wedge (`DRIVE_DRIVER_VIEW_ROOTCAUSE.md`
  §1.3). Post-fix, the latch now triggers **one reliable recovery full + a hold**:
  `NetWorldHost.TickReplication` sends the full (`NetWorldHost.cs:279-281`), records
  `_pendingRecoveryFulls[player] = tick`, and then **skips all unreliable snapshot sends** for this
  peer until its ack reaches the recovery tick (`:265-269`).
- The hold lasts until the client *receives* the (fragmented, ~3-4 KB) reliable full and acks its
  tick — i.e. exactly as long as the inbound outage that caused the latch, plus RTO retransmits
  (`NetProtocol.cs:79-80`). During a multi-second hitch/loss burst the client is in **total state
  blackout**: no vehicle updates, no player-entity updates, nothing. That multi-second outages
  really happened in this very post-fix session is proven by the journal: `player 1 left (Timeout)`
  19:50:20 and `player 2 left (Timeout)` 21:09:09 — a Timeout is ≥5 s of silence
  (`NetProtocol.cs:76`).
- Re-latching is cheap: after each recovery, the *next* ≥1.28 s hitch (world snapping current on
  screen, another first-render) starts another full + hold. A hitchy client spends much of its
  session inside holds.

### 2.2 What the driver sees during the blackout — a moving car

- `VehicleReplicaView._Process` (`game/VehicleReplicaView.cs:60-66`): `SinceSnap` accumulates
  every frame the replica pos doesn't change, and the puppet target is
  `e.Pos + LinVel * SinceSnap` — **no horizon**. Frozen at drive start with `LinVel` = a few m/s
  and climbing, the puppet keeps "driving" along the frozen velocity vector for the entire
  blackout. Steering input doesn't visibly respond (extrapolation is straight-line), but on a
  mostly-straight 40 m test drive that is indistinguishable from a working drive.
- The shell chase-cams and rides that puppet (`ShellStep` ride branch,
  `game/ClientWorldSession.cs:164-171`; `RidePuppet`, `game/PlayerController.cs:1241`), so the
  driver's whole view is the extrapolation. Meanwhile his `DriveInput` (client→server,
  UnreliableSequenced, `:167`) flows fine — the **real** server car genuinely drives
  (`game/VehicleNetSync.cs:106-108`), 40 m away from what his screen shows.

### 2.3 The exit

- He presses F → `SendExitVehicle` (reliable). Server: `ServerExit` frees the seat, teleports his
  player entity beside the REAL car (`core/UnturnedNet/VehicleReplication.cs:509-518`, terrain
  clamp wired at `game/DedicatedServer.cs:71-76`), broadcasts `EventVehicleExited` (reliable).
- Client `Tick` applies (zero) pending snapshots, then dispatches reliable events
  (`core/UnturnedNet/NetWorldHost.cs:441-444`). `OnVehicleExited`
  (`game/ClientWorldSession.cs:239-258`): the guards pass (`_ridingNetId` matches, `Shell.IsRiding`
  true), `Client.Vehicles.TryGet` **succeeds** — and returns the **frozen** entity. Exit spot =
  frozen `v.Pos` (≈ entry) + right·2.4 + up, terrain-clamped. `Shell.ExitPuppet(exit)`
  (`game/PlayerController.cs:1211-1224`) places him there. (If the ReliableOrdered channel still
  has the recovery full in flight, the exited event — queued behind it on the same ordered channel —
  delivers right after the full applies; the full's content is the *hold-start* state, so the
  replica is still the latch-point state either way.)
- On his screen the extrapolated car snaps back to him (puppet-to-target distance > `SnapDistance`
  8 m → hard snap, `VehicleReplicaView.cs:66`), which *looks like* "I got out and I'm back at the
  entry with the car". Self-consistent, completely wrong.

### 2.4 Why nothing corrects him (immediately, or sometimes ever)

The post-exit `ShellStep` reconcile path (`game/ClientWorldSession.cs:175-184`) is *designed* to
absorb the residual — but every input it needs is starved:

- His own-entity replica froze with the same blackout, so `e.LastProcessedInputSeq` is the
  pre-ride seq the reconciler already consumed → `OnAuthoritative` returns at the stale-ack guard
  (`core/UnturnedNet/Prediction.cs:72`) and `_pending` stays zero. **No snap toward anything.**
  (Note this refutes the prompt's specific sub-theory: a stale `e.Pos` can never *pull* him
  anywhere, because a stale *seq* makes the sample inert. The freeze doesn't misdirect the
  reconciler — it disarms it.)
- The heal needs a full round trip **through the still-held/starving stream**: fresh `MoveInput`
  (sent — `ShellStep` resumed) → server avatar consumes it (`game/PlayerNetSync.cs:125-131`,
  after the exit-adopt correctly snapped the avatar body to the beside-the-real-car teleport,
  `:99-107`) → write-back pairs the fresh seq (`:110-115`) → **a snapshot must reach the client**.
  During the hold no snapshot is sent at all (`NetWorldHost.cs:265-269`). When recovery does
  complete, the very next fresh-seq sample measures ~40 m of error and snaps him beside the real
  car (`Prediction.cs:80`, `ClientWorldSession.cs:178-183`) — seconds later, as a silent 40 m
  teleport (its own terrible UX). If instead the outage crosses 5 s, the session times out
  (`NetProtocol.cs:76`) — the journal shows exactly that happening — and he is *never* corrected:
  the shell stands at the entry with a dead session behind it.

So the reported end state — standing at the entry spot — is exactly what current main produces
whenever the exit lands inside (or at the tail of) a blackout window, and it persists for however
long recovery takes, or forever if the session dies first.

## 3. Every other suspect, proven sound (the elimination trail)

These are the paths the prompt asked to rank; each was read end-to-end and is **correct on
current main**:

| Suspect | Verdict | Evidence |
|---|---|---|
| Shell doesn't actually ride the puppet | Refuted | `_PhysicsProcess` ride branch runs every tick while `_riding != null` (`PlayerController.cs:2651`), `RidePuppet` copies the puppet pos (`:1241`); `IsRiding => _riding != null` (`:1143`) so the `:243` guard passes |
| Interp-restore stomps `ExitPuppet`'s placement | Refuted | the ride branch clears `_interpReady` every tick (`:2651`); the first post-exit physics tick skips the restore (`:2652`) and re-primes from the exit spot (`:2764`); `TruePhysicsPosition` falls back to `GlobalPosition` while unprimed (`:1125`), so the first post-exit `Record` is at the exit spot |
| Driver's own entity frozen at entry *server-side* during the ride | Refuted | `ServerVehicles.Step` teleports it onto the car every tick (`VehicleReplication.cs:539-549`) and `ServerTeleport` marks it dirty (`PlayerReplication.cs:479-486`); players are AllRelevant, no owner filtering anywhere in `WriteFull`/`WriteDelta`/`ReadSnapshot` (`PlayerReplication.cs:513-545`) |
| `PlayerNetSync` write-back re-asserts the stale avatar body over the exit teleport | Refuted | seated bodies follow the entity (`PlayerNetSync.cs:89-98`); the exit teleport is *adopted* body←entity via the `t.Seated` flag (`:99-107`) before write-back resumes; post-exit write-backs carry the stale pre-ride `LastInputSeq` (`:114`), which the client ignores (`Prediction.cs:72`) — harmless by design |
| `TryGet` fails at exit → `Shell.GlobalPosition` fallback = entry | Refuted | the registry never loses the entity (an empty/skipped vehicles block no-ops before the clear, `VehicleReplication.cs:220-221`), and even the fallback lands at the car because the shell rode the puppet (`:1241`, the comment says exactly this) |
| Reconciler snaps to a stale `e.Pos` | Refuted as stated | a stale seq short-circuits before `e.Pos` is even compared (`Prediction.cs:71-74`); the ring guard (`:74`) blocks unrecorded seqs. The *generalized* version — frozen replica consumed by the **exit-spot computation**, with the reconciler disarmed — is the confirmed bug (§2) |
| Server exit math / ordering | Sound | dispatch → `ServerExit` (entity beside current car) → `Players.ServerStep` (skips `ExternallyDriven`, `PlayerReplication.cs:429`) → `VehicleHost.Step` (driver already removed) → `PlayerNetSync` adopt → publishers → `TickReplication` last (`NetWorldHost.cs:157-171`, `DedicatedServer.cs:105-130`) |

Also verified: the recovery budget itself is ample (`MaxReliableMessageBytes/2` ≈ 150 kB vs a ~3-4 kB
full world, `NetProtocol.cs:66`), so the fix's reliable full always carries the vehicles block and
acking it clears the latch — 7ce2305 is not "still wedged"; its **hold window** is the problem.

Why no test caught this: `net.shell_drive` (enter/drive/exit with `RemoteAvatars`, exit-beside-door
asserted) and the new `net.vehicle_drive_sync_ackgap` (7ce2305 — replica *recovers after* an ack
gap) both run on MemTransport, where recovery is instantaneous once acks resume. **Nothing exits
*during* the blackout window**, which is the only time the bug exists.

## 4. Confirmation (cheap, in order of strength)

1. **Turn on `UG_NETLOG=1` on the server** (it is currently off — today's journal has no `[NET]`
   lines). Signature during a repro: `[NET] reliable recovery full -> player N` (`NetWorldHost.cs:281`)
   landing *during the drive*, and the exit following before the hold releases. The 1 Hz rollup's
   `snaps full/delta` counters date the holds precisely.
2. **L1 repro**: extend `net.vehicle_drive_sync_ackgap` — withhold the driver's acks mid-drive
   (already does), then send `ExitVehicle` **before** resuming acks/recovery, and assert the shell's
   exit position is beside the **server** vehicle node. Fails on current main (it will be beside the
   frozen replica), passes with the fix below.
3. Client-side print of `SinceSnap` for the ridden puppet at exit time — any value ≫ 0.08 s (two
   snapshot intervals) at the moment of `OnVehicleExited` proves the replica was stale when the spot
   was computed.

## 5. Minimal fix (not applied — diagnosis only)

**Primary: make the exit spot authoritative — carry it in the event.** The server already computes
the exact spot (`VehicleReplication.cs:513-518`); add it to `VehicleExitedEvent`'s payload and have
`OnVehicleExited` use `evt.Pos` instead of recomputing from the replica
(`ClientWorldSession.cs:245-251` becomes a read of the event field; keep the terrain clamp as a
belt-and-braces). The event already rides ReliableOrdered, so the spot arrives exactly when the
exit does, regardless of snapshot health. This removes the replica-freshness dependency entirely —
the entire §2 chain becomes harmless. (~7 bytes on a rare event; `EventVehicleExited` consumers:
`ClientWorldSession.OnVehicleExited` and the occupancy-only `ApplyExited`, which ignores the new
field.)

Hardening alongside (each independently worthwhile):
- **Bound the dead-reckoning horizon**: cap `SinceSnap`'s extrapolation in `VehicleReplicaView`
  (~0.5 s). A starved replica should visibly freeze — an honest freeze is what made the *original*
  wedge reportable; the current unbounded glide actively masks outages.
- **Don't blackout the driver's own entity during a recovery hold**: while
  `_pendingRecoveryFulls` holds a peer, still send a minimal players-only delta (or piggyback the
  own-entity state on the keepalive). That re-arms the reconciler's heal path so any residual
  misplacement corrects in one round trip instead of one recovery.
- The §4.2 regression test ships with the fix (the regression rule).
