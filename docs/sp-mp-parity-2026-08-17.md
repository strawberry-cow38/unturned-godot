# SP/MP parity review — 2026-08-17 (main @ 53ee16d7)

Hunting features that work in singleplayer and are broken, missing or divergent in multiplayer.
Five agents cover inventory, combat, world/deployables, player/vehicles and the wiring seams; their
findings land below once hand-verified. **Findings I verified myself come first.**

## The architecture, restated because every finding depends on it

Singleplayer runs through an in-process loopback listen-server —
`Main.ResolveLoopbackMode(gameDefault:true, direct:false)` returns `(attach:true, consume:true)`. So
there are three configurations, and a feature can work in one and not the others:

1. **Pure SP** (`--direct`) — no server.
2. **SP-as-loopback** — the shipped game; `MpLoopback.cs` wires the client seams.
3. **Real MP** — `ClientWorldSession.cs` wires them; `DedicatedServer.cs` is the other end.

A seam wired in one of `MpLoopback.cs` / `ClientWorldSession.cs` and not the other is a feature that
works in SP and dies in MP. That asymmetry is the single most productive check in this codebase.

---

## Verified by hand

### 1. ~~Weather is per-client~~ — **I GOT THIS WRONG. Corrected by par-world.**

I reported that every MP client rolls its own weather seed, so players stand together in different
weather. The seed fact is true: `WeatherManager.Attach(..., int seed = 0)` falls back to
`(int)GD.Randi()`, and the only call site that passes no seed is production.

**The conclusion was wrong, because weather is never instantiated in the shipped game at all.**
`WorldBuilder` contains **zero** references to Weather (verified: `grep -c` returns 0), and both
`Attach` call sites are prototype scenes — `Main.cs:1377` is inside `BuildPlayable`, the `--play`
harness, and `Main.cs:5217` is inside `BuildDayNightDemo`, the `--weather` reference scene. The real
world builds through `BuildFullWorld`, which never attaches it.

So it is **not a divergence — it is a wiring gap**: `WeatherSim`, the rain overlay and
`FishBiteIntervalMultiplier` are dead code in the shipped game on *both* seams. Worth fixing, but it
is a different bug than the one I claimed, with a different fix.

**Why I got it wrong, since it is the third time this exact shape has bitten me:** I verified a real
fact (the seed is random per process) and then answered a *different question* with it (what players
experience). I never checked whether the system is constructed on the path the game runs.
par-world checked instantiation; I checked behaviour of something that is never instantiated. A real
number from a real file can still answer the wrong question.

### 2. Structures have no MP story at all — CONFIRMED (as a gap, not a regression)
`game/StructureManager.cs`

Zero references to `Net*`/`Replication`/`Server`/`Client`, and absent from both seam files. Built
structures are client-local: in MP nobody else would see what you build, and nothing validates
placement server-side.

**This is not a regression** — structures did not exist on main until I merged `feat/structures`
tonight, so nothing that worked has broken. It is an SP-only feature newly present on main, which is
exactly the class this review exists to surface. Same applies to **fishing** (no seam wiring) and
should be checked for **vehicle-vs-prop crash damage**, which does route through the existing
`Vehicle.NetDamageObject` seam and so is probably fine.

*Worth stating plainly:* I merged these tonight on VoX's instruction to merge approved branches. They
were approved as features; their MP support was never claimed. Flagging rather than quietly shipping.

---

## THE HEADLINE, which reorders everything below

**"SP IS MP" does not hold for combat.** `MpLoopback` never wires `NetFire` / `NetMelee` /
`NetGrenade` / `NetReload` — those four seams are set only in `ClientWorldSession.cs:487-490`, and
`MpLoopback.cs:83-89` says so outright: *"the local player's bullets stay CLIENT-side in the loopback
(combat isn't wire-routed like deployables are)"*.

So the loopback host plays SP combat verbatim, and **`ServerCombat` is exercised only by a joined
client**. No amount of singleplayer or loopback play touches the MP combat path, and it has drifted
freely. That single fact explains why the combat findings below are the worst in the review, and it
means the inventory subsystem's parity (which IS symmetric across both seams) was achieved precisely
*because* the loopback exercises it.

---

## Findings, worst first

Severity is "what a player loses in MP". ✅ = I re-verified against source myself; ⬜ = agent-reported,
traced with file:line, not yet re-verified by me.

### Combat — the whole subsystem has diverged

1. ✅ **Every gun in MP is a stale Eaglefire.** `ServerCombat.SetGunProfile` has zero production
   callers, so `GunFor(sender)` always returns `DefaultGun`: 40/player, 99/zombie, 1 pellet, 30-round
   mag, and a 5-tick minimum gap. Consequences measured against the 55 shipped `.dat`s — masterkey and
   sawed-off fire 1 pellet instead of 8; dragonfang/fury/nykorev go silent after 30 of their 200–250
   rounds; **10 of 55 guns fire faster than the server permits** and have 20–40% of their shots
   silently rejected; and a timberwolf's 1.02 s cycle is validated at 0.1 s, a 10× anti-cheat hole.
   *(Correction to my brief: `HeldId` rides the wire but is never **written** —
   `PlayerAppearanceNetSync.cs:13` defers it. A fix must populate it first.)*
2. ⬜ **Zombie limb multipliers are absent on the MP path.** `ServerCombat.cs:400` uses flat
   `ZombieDamage`, and `ZombieNetSync.cs:180` discards the `headshot` bool on both branches. Yesterday's
   fix landed in `ZombieDirector.ShootSegment`, whose comment claims the wire passes a resolved
   amount — it does not. SP: 20 body / 100 head. MP: 99 either way, so the design ("headshots one-shot")
   is inverted.
3. ⬜ **Gunfire is inaudible to server-side zombies.** SP emits `SoundBus.Gunshot` at 48 m;
   `ServerCombat` emits nothing. The seam demonstrably works — `PlayerNetSync.cs:96` re-derives
   *footstep* noise for exactly this reason — gunshots were never given the same treatment. Suppressors
   are a no-op in MP because there is nothing to suppress.
4. ⬜ **Vehicles and deployables are immune to every server weapon.** `StepBullets` has three hit
   kinds (player/zombie/world) and no vehicle or deployable branch; `Explode` loops zombies and players
   only. Shoot a helicopter's tail rotor in MP and nothing happens.
5. ⬜ **Explosion armour, distance falloff, melee weapon identity and the OVERKILL skill are all
   SP-only.** MP melee is always the Military Knife (fists hit for 50, a katana also 50, a golf club
   loses its reach). MP has no falloff and no armour term.
6. ⬜ **Warheads never detonate in MP** — `FireCommand` carries origin+dir only, and `ServerGunProfile`
   has no blast fields, so the rocket launcher's entire point is missing.
7. ⬜ **Stance is replicated, applied to the follower body, and ignored by the hitbox.** The server's
   player zone is fixed (radius 0.42, top 1.8). A prone player is still hittable through empty air
   1.5 m above their back — and that registers as a 2× headshot.

### Vehicles and player state

8. ✅ **Every vehicle is single-occupant in MP.** `VehicleEntity` has one `ushort DriverPlayerId`
   ("single driver, §3.6 v1"), `EnterVehicleCommand` carries a NetId and **no seat index**, and
   `grep -ri turret core/` returns **nothing**. The second player to press F gets silent nothing.
   *(Correction to my brief: I said two players could believe they hold the same seat. It is stricter —
   they cannot share the vehicle at all.)*
9. ⬜ **A vehicle being driven in MP is indestructible.** Two independent gates: the driver's node is
   `NetClientPredicted` so `TakeDamage` returns early, and the server's node is `NetHeld` so its
   `_PhysicsProcess` returns before the crash detector. Core has no vehicle-damage path at all.
10. ⬜ **Remote players are always rendered standing.** `PlayerAppearanceNetSync` reads stance from the
    MoveInput channel, which a joined client stopped using at wire v9 (`ClientWorldSession` only ever
    calls `SendPlayerState`). It works on the loopback — the one path with no remote puppet to dress.
11. ⬜ **Respawn resets HP but not the fine vitals**, so infection/hunger/thirst survive death (bounded
    by `SurvivalDrain`).

### World, deployables, power

12. ✅ **The town power grid is ON in SP and OFF on the dedicated server.** `MpLoopback.cs:126` seeds
    every `GridSource` fixture `ToggledOn = true`; `DedicatedServer.cs:121` has only the `GasPump` arm.
    Every joiner arrives in a dead town, and the only remedy is a cheat-gated console verb.
13. ⬜ **The Power Switch is inert on both server-backed seams** (so the shipped SP game too, not just
    MP) — four independent links drop it, the first fatal: `CanToggle` requires `FuelCapacity > 0` and
    the switch is `Fuel = 0`.
14. ⬜ **Player-placed doors and the entire fluid stack place locally only** — `DeployableNetSchema`
    skips `Fluid`/`IsStorage`/`DoorProp` defs, **yet the server still spends the item**. A base door in
    MP blocks the person who built it and nobody else.
15. ✅ **Structures have no replication whatsoever**, so a base exists only on its builder's client —
    and the server's combat raycast world never knows it is there, so bullets pass through a wall the
    builder is hiding behind.

### Inventory — the one subsystem whose two seams ARE symmetric

Because the loopback exercises it, the wiring is even. What remains is the third seam: local mutations
with no intent at all, which are reverted on **both** server-backed paths.

16. ⬜ **The whole fluid-container subsystem has no wire intent** — filling, drinking and the autodrink
    toggle are all local, and `WriteJar` carries the server's values back over them.
17. ⬜ **Re-placing a picked-up deployable discards its HP and fuel** — ✅ I verified `ServerPlace` takes
    `(id, defId, owner, pos, yaw, tick)` and no condition. Free repair and free refuel, repeatable.
18. ⬜ **Detaching an attachment is client-local**, which also makes attachment *swap* unreachable on
    any server-backed seam.
19. ⬜ **Four of the five magazine/ammo grid paths never reach the server** — only `DoMagSwap` was fixed
    yesterday; `ConsumeShells`, `LoadMagInstance`, `RemoveMagazine` and `RackGun` are the same shape.
20. ⬜ **The Nearby/ground-pickup page is dead on both server-backed seams**, for two different reasons —
    `TryDrag` rejects `page0 >= PAGES-1` (which is the AREA page), and on a joined client the page is
    empty anyway because MP materializes `WorldItemPuppet`, which is not in the `"worlditems"` group.
21. ⬜ **Station and repair/salvage recipes are silently rejected server-side** while the Craft button
    stays enabled and says nothing.
22. ⬜ **`MixEntry` (the desync StateHash) omits the per-slot attachment ids** that `WriteJar` sends —
    so the sync-check is structurally blind to exactly the field group that shipped broken last review.

---

## Corrections the agents made to MY briefing — recorded because they matter

- **Animals are not a parity bug.** I briefed "wildlife unkillable in MP". `AnimalAgent.cs` is 72 lines
  with no collider, no health and no damage entry point — **wildlife is unkillable in SP too**. It is a
  missing feature, not a divergence; adding the server loop would have nothing to call.
- **Weather was my error, not a divergence** (see finding 1 above, struck through).
- **Vehicle seats are worse than I described** (single-occupant, not contested-seat).
- **`HeldId` is on the wire but never written**, so "the data is there and unused" was half wrong.
