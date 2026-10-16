# Restraints, binoculars, umbrella and fishing port

Ledger rows 11 and 12. Checkpoint 1 is the restraints; the binoculars, umbrella and
fishing follow as their own checkpoints.

## Restraints (row 11)

### Source

- `core/UnturnedSim/ArrestSim.cs`: `ArrestDef` (Handcuffs 1195 strength 128, Cable Tie 1197
  strength 64, Handcuffs Key 1196 recovering 1195), `ArrestSim.TryArrest/Struggle/TryUnlock`.
- `core/UnturnedNet/ServerTransactions.cs`: the handlers for command 52 `ArrestPlayer`,
  53 `UnlockArrest` and 54 `Struggle`, `InArrestReach` (7 m, retail `sqrMagnitude > 49`).
- `core/UnturnedSim/GestureRules.cs`: `ARREST_START` (`Gesture_Arrest`, looping, not
  requestable) and `RefusalFor` ("You're cuffed.").
- `game/PlayerController.cs`:
  - `KindOf` (`Restraint`, `RestraintKey`) and `EquipItemAsset`'s "Your hands are bound." gate;
  - `EquipHeldRestraint` (EmptyHands, `Cuff_*`/`Key_*` clips);
  - `TryRestraintUse`, `RestraintUseSeconds`, `TickArrest`, `ArrestAimRange` (3.5 m);
  - the lean-edge `NetStruggle` in `StepLean`.
- `game/RemotePlayers.cs`: `AimedPlayer` (best flattened dot above 0.5 within reach of the
  camera) and `GestureOf`.
- `game/MpLoopback.cs` / `game/ClientWorldSession.cs`: the `NetArrestPlayer`,
  `NetUnlockArrest`, `NetStruggle` seams; `ApplyNetGesture` adopts the server's gesture.

### Authority

Nothing here is decided on the client. The native client names a target; core decides:

- **52 ArrestPlayer:** the captor's replicated `HeldId` must be a restraint, the target must be
  in `SURRENDER_START` and within 7 m, and not already cuffed. The cuffs leave the captor's bag;
  the victim's combat entity is forced to `ARREST_START`, which replicates to everyone.
- **53 UnlockArrest:** a key in any hand frees (not only the captor's) and the `.dat`'s
  `Recover` hands the cuffs back to the freer (`GiveOrDrop`). A cable tie has no key.
- **54 Struggle:** the side must alternate and at most one counts per server tick. 128
  alternating leans break handcuffs, 64 a cable tie.

Review fixes in core (the source handlers had these holes):

- `HeldId` is the client's `MoveInput.HeldItemId`, so 52 and 53 now also require the restraint
  or key to be in the sender's authoritative bag. A forged 52 used to cuff with a pair the
  captor never owned, and the later `SpendAnyOf` spent nothing.
- A dead captor or freer is refused, and a dead target cannot be cuffed.

No wire ids or bytes change. The bridge reaches 52/53/54 through the reflected
`NetWorldClient.Send*` actions (`arrestPlayer`, `unlockArrest`, `struggle`) for host and
joined clients alike.

### Native

- `bevy/src/restraints.rs` ports `TryRestraintUse`/`TickArrest`:
  - the held item's catalog `handTool` (`kind` `restraint` / `restraintKey`, `useSeconds` from
    `Cuff_Use` 1.300 s / `Key_Use` 1.633 s) marks the hold;
  - LMB picks `AimedPlayer` from the player camera over the remote puppets, reads that
    player's replicated gesture and gives the source alerts ("Aim at someone surrendering.",
    "They have to surrender first.", "Aim at someone who's cuffed.", "They aren't cuffed.");
  - the cuffs: `Cuff_Use` plays and `arrestPlayer` is sent when it ends, if a restraint is
    still in the hand (the source re-checks nothing locally); one attempt at a time;
  - the key: `Key_Use` plays and `unlockArrest` is sent at the click.
- The viewmodel is the source's: EmptyHands wins the equip precedence (`Melee_Equip`), with
  the restraint's own Use clip (`Catalog.HandTool`).
- The cuffed player: the replicated `ARREST_START` already drove the local and remote
  `Gesture_Arrest` overlay, the struggle (`player::lean`) and the gesture refusal. The bridge
  now refuses `holdItem`/`equipAndHold` while the owner's accepted gesture is `ARREST_START`
  ("Your hands are bound."), as `EquipItemAsset` does for every equip path.

### Deviations

- **Bound hands are a client gate,** as in the source: what is already in the hand stays,
  and the server does not refuse a cuffed player's `HeldItemId`.
- **No local "Nobody to use that on.":** that alert is the source's direct-SP branch with no
  `NetAimedPlayer`; the port is always a client of a server, like `MpLoopback`.
- **Death and disconnect keep the cuffs,** as in the source (`ArrestSim.Forget` is never
  called, and death does not clear the gesture).
- **No clatter:** `ArrestBroke` (retail's Metal_1 effect) has no listener in the source.

### Verification

- **Core** (`ArrestAuthorityTests`, 2): a forged `HeldId` (no cuffs in the bag), a target not
  surrendering, a dead captor and a dead target are refused; the accepted arrest spends the cuffs;
  a forged or dead freer's key frees nobody; the key frees, returns the cuffs and is kept; 63
  alternating struggles plus a repeated side keep a cable tie on and the 64th breaks it.
- **Managed** (`RestraintTests`, 2, real UDP, listen host cuffs a joined peer): not surrendering
  and beyond 7 m are refused; the cuffs arrive on the peer's own entity and on the host's replica;
  the cuffed peer's equip is refused with "Your hands are bound." and a wave with "You're cuffed.";
  a key frees and returns the cuffs; 127 alternating leans and a repeat do not break handcuffs,
  the 128th does.
- **Native** (`restraints`, 6, and the hand-tool viewmodel test): `AimedPlayer` reach, cone and
  best-dot choice; every source alert; `Cuff_Use` timing with one attempt at a time; the key at
  the click; `TickArrest` only while a restraint is held; catalog parsing; the restraints hold
  `Melee_Equip` with `Cuff_Use`/`Key_Use` and no mesh. An integrated test runs a native listen host
  (two real bridges over UDP): the click on a non-surrendering peer gives the source reason, the
  cuffs land only after the clip, the peer is `ARREST_START` on both sides, its equip is refused,
  and the key frees it and returns the cuffs.
- **Negative controls**, each failing as intended: core bag check (52), core alive check (52),
  core bag check (53), core alive check (53), the bridge equip gate, sending at the click, the
  surrender gate, the cuffed gate for the key, the 0.5 aim cone, the still-held test in
  `TickArrest`, and the one-at-a-time gate.
- **Visual** (opened): the Godot 4.6.2 `--rig --anim=Gesture_Surrender,Gesture_Arrest` oracle
  beside the native remote-pose fixture with replicated gestures 11/11/6/0
  (`UG_REMOTE_POSE_GESTURES`): hands up beside the head and hands behind the back match. The
  first-person `--vm --gun=handcuffs` oracle (a new EmptyHands harness branch mirroring
  `EquipHeldRestraint`) beside the native fixture: 1.0 s into `Cuff_Use` the right forearm enters
  lower right in both. In the `Melee_Equip` hold the native arms sit lower than Godot's, the same
  pre-existing framing difference as the spare tire's hold. Renders:
  `bevy/artifacts/{bevy,godot}-restraint-handcuffs-{hold,use}.png`,
  `bevy/artifacts/bevy-restraint-remote-gestures.png`, `bevy/artifacts/godot-gesture-{surrender,arrest}.png`.

## Binoculars (row 12)

### Source

`game/PlayerController.cs`: `KindOf` (`EItemType.OPTIC`), `EquipHeldOptic`, `UpdateOptic`,
`CycleOpticZoom`, `ClearHeldOptic`, `OpticZoomLevels` (4/8/12), the eyepiece constants, the LMB
dispatch and the `sens /= zoom` look line; `game/BinocularsView.cs`; `game/Viewmodel.cs`
(the DeployableMesh follow branch with `HoldPos`/`HoldRoll`/`NaturalHold`, `AddHeldLens`);
`game/content/binoculars.gdshader`, `binoculars_overlay.gdshader`, `ui/binoculars_overlay.png`.

### Authority

None: the source's binoculars are local presentation (zoom, overlay, PiP). Holding them is the
ordinary `holdItem`.

### Native

`bevy/src/viewmodel/binoculars.rs`:
- **Carried:** `Catalog.HandTool` gives OPTIC items the `items/333.txt`/`.png` carry model and
  `Deploy_Equip` (the NaturalHold clip `Fuel_Equip` is not in the rig, so the source falls back too).
  Each frame the pair takes the hook's basis times `HoldRoll (0,0,-90)` at the hook plus
  `HoldPos (0,0.44,-0.14)` in view space.
- **PiP:** one camera 0.12 m up the barrels renders the world at FOV / zoom into a linear HDR
  target; a disc sunk 3 cm into each eyepiece samples it at its own screen position (corrected
  for the oversized arms target), with the 0.30 vignette. No inverse ACES is needed: the scene is
  untonemapped and the viewmodel pass tonemaps once, as the scope glass does.
- **Raised** (RMB held, captured mouse, no UI, alive, not in a vehicle): the arms camera is off,
  the tank optics' periscope overlay (the same `ui/binoculars_overlay.png` cover-fit) is shown, the
  main FOV is divided by the zoom once per frame, and look sensitivity is divided by it.
- **LMB** on foot steps 4x → 8x → 12x → 4x and plays `sounds/firemode.wav`; a fresh equip starts
  lowered at 4x. `UG_ADS=1` holds them up, as the source harness does.

## Umbrella (row 12)

### Source

`game/Umbrellas.cs` (the eight `Cloud` items, Gravity 0.25, `FallDamageGravityFloor` 0.67),
`PlayerController.EquipHeldUmbrella` (EmptyHands, `Melee_Equip`), the PhysicsTick line that sets
`_move.GravityMultiplier` from the held umbrella once `IsEquipComplete`, `PlayerMovementSim`
(gravity on the way down and the terminal clamp) and `CheckFallDamage`'s gate.

### Authority and native

- Movement is client-authoritative: `bevy/src/umbrella.rs` hands the bridge `simulate` the
  multiplier (the held item's catalog `gravityMultiplier` once the viewmodel is `ready`), and the
  existing `PlayerMovementSim` applies it.
- Fall damage is server-derived in this port, so the source's gate moved there: core
  `ServerPlayerAuthority.FallGravityMultiplier` skips the landing at or below 0.67, and
  `bevy/bridge/UmbrellaServices.cs` answers it from the replicated hand: an umbrella held at least
  `Melee_Equip`'s length is up. This covers the listen host and joined clients.
- The hold is the existing EmptyHands viewmodel (no carry model), as in the source.

### Deviations

- **No rain behaviour and no third-person umbrella:** the source has neither (nothing in
  `RainOverlay`, no carry model, no puppet visual), so neither does the port.
- **Equip timing on the server** is the time since the replicated hand changed, against the
  `Melee_Equip` length, rather than the client's animation state. They agree to within the input
  latency.
- **Wiring coverage:** the `gravityMultiplier` field in the native `simulate` request has no
  automated test of its own; the helper and the server gate do.

### Verification (binoculars and umbrella)

- **Core:** `FallDamage_SkippedWhileTheLandingGravityMultiplierIsAtOrBelowTheFloor` (0.25 and 0.67
  unhurt, 0.68 hurt).
- **Managed** (`UmbrellaTests`): a 30 m/s landing hurts bare-handed, not with the umbrella up all
  the way, not when opened after the peak speed, and fully when tapped 0.1 s before impact.
- **Native:** the umbrella helper; the binocular zoom cycle, equip reset, raise, sensitivity,
  `UG_ADS` and held-item tests; the carry and PiP pose maths; the production tank-optics `apply`
  showing the overlay and dividing the FOV exactly once; the hand-tool test (333 holds
  `Deploy_Equip` with the 333 mesh and texture; the umbrella and the rod hold `Melee_Equip` with no mesh).
- **Negative controls**, each failing as intended: the core 0.67 gate, the bridge hook, the
  bridge equip wait, the native equip wait, the binocular overlay, the zoom reset, the
  sensitivity, the hold roll, and the catalog carry model.
- **Visual** (opened): Godot 4.6.2 on PEI (`--peidrive`, `UG_HOLDITEM=333`, with and without
  `UG_ADS=1`) beside the native fixture `render_carried_and_raised_binoculars`. Carried, both show
  the pair low and centred with the barrels up and the two crossbars toward the eye, and the same
  framing; the native material reads lighter grey than Godot's near-black under its brighter
  fixture light. Raised, both show the two-hole retail overlay over a 4x view. The eyepiece discs
  face the eye from below the view in both, so the PiP is not visible in either frame and has
  no visual check beyond its pose test. Renders: `bevy/artifacts/{bevy,godot}-binoculars-{lowered,raised}.png`.

## Fishing (row 12)

### Source

- `core/UnturnedSim/FishingSim.cs` (the UseableFisher port: gauge tock, bite timer, catch window,
  catch challenge, weighted reward) and `game/inventory/FishingContent.cs` (`ConfigureForPei`:
  Rod_Fishing values, 6-14 s bites, the PEI ocean table, the challenge enabled).
- `game/PlayerController.cs`: `EquipHeldFisher` (EmptyHands), `FisherPrimary`/`FisherRelease`,
  `TickFishing`, `SpawnBobber`, `UpdateFishLine`, `BobberOverFishableWater`, `GrantFish`, `ClearFisher`.

### What the source did, and why the port adds a server

The whole loop ran in the client's `FishingSim`, and `GrantFish` called `tryAddItem` and
`AwardExperience` on the client's own copies. In the unified game those are server-adopted: the next
owner-inventory echo drops the fish, and the per-tick skills adoption drops the XP. A local grant is not
possible at all in the Bevy port (the native client has no inventory to mutate), so the catch is made
server-authoritative, as retail's UseableFisher is.

### Authority (protocol v80)

- **Command 103 `FishingCast`** (bobber position, gauge strength as a byte): the bridge
  `FishingServices` (host, like the v79 fuel handlers) checks a living, on-foot caster with a FISHER
  in the replicated hand and in the bag, a landing within 64 m horizontally and at most 2 m above the
  caster, on the sea plane (±1 m) with at least 4 m of water under it (a world ray). It then builds a
  `FishingSim` exactly as `ConfigureForPei` does (with the caster's server-side FISHING level) and
  `BeginLine`s it. A refusal answers `Idle`.
- **Command 104 `FishingInput`** (press, release, stop): press is `FishingSim.Press` with retail's
  `SERVER_LENIENCY_WINDOW` (1 s) past the client window; release sets the challenge cursor falling;
  stop drops the line.
- The host steps every line at 50 Hz (`Tock` + `Simulate`), drops it when the rod leaves the living
  hand, and grants as `GrantFish` does: `tryAddItem` (a full bag keeps nothing) and the XP through
  `SkillsReplication.ServerAward`.
- **Event 78 `FishingState`**, to the caster only: idle, line, bite or challenge, and the fish landed.
- Core additions: `FishingSim.BeginLine` and `Press(lateGrace)`/`BiteWindowOpen(lateGrace)`; the wire
  structs and `NetWorldClient.SendFishingCast/SendFishingInput/FishingState` (`core/UnturnedNet/Fishing.cs`).

⚠ As with commands 99/100, the handlers live in the bridge. A non-bridge host (the Godot
`DedicatedServer`, a bare `NetWorldServer`) registers no 103/104 and never answers a cast.

### Native

`bevy/src/fishing.rs`: LMB on a held rod starts the gauge (the source's 50 Hz tock, period
100 + 20 × FISHING level, no HUD in the source); release flings the bobber from 0.6 m along the aim
at Lerp(12, 26, strength) plus 3 m/s up under 20 m/s² gravity; a splash into water with 4 m of depth
under it (a Rapier ray against world solids; no physics world trusts the plane) sends 103, otherwise
the cast is dropped, as is one that falls 60 m. While the server's line is out, every press and release
is sent as 104; the bobber floats on the sea with a 0.06 m bob, is tugged 0.35 m under in the catch
window (1.0-2.4 s after the bite event) and held 0.5 m under during the challenge. The line runs from
the source's rod-tip point (camera + 0.5 forward + 0.25 right - 0.2 up) to the bobber, unshaded. Putting
the rod away, dying or getting into a vehicle with a line out sends stop.

### Deviations

- **Server-side catch** (above): new wire ids 103/104/78 and protocol 80; the integrator renumbers at merge.
- **No catch-challenge UI,** as in the source: the fish is played blind with LMB held/released.
- **Event timing:** the catch window opens when the bite event arrives, so the client's tug lags the
  server's window by the link delay; the server's 1 s leniency covers the press's return trip.
- **Other players' bobbers are not shown,** as in the source (the bobber is the caster's local node).
- **Flat sea:** the bobber rides the mean sea level (`WaveField.Flat`), as the native water does.

### Verification (fishing)

- **Core:** `FishingWireTests` (round trips, truncation, out-of-range kind/state, ids, version);
  `FishingSimTests.ServerLineStartsInTheWaterAndAcceptsALatePressWithinTheLeniency`.
- **Managed** (`FishingTests`, real UDP, joined peer casting at a listen host with water): no rod,
  beyond reach and off the sea plane are refused; a press with no line catches nothing; an early
  press reels in empty; after the server's bite a press in the window opens the challenge, which a
  bot plays with real 104 presses/releases until the server lands a PEI fish into the replicated bag
  with +3 XP; equipping anything else ends the line with an `Idle`.
- **Native** (`fishing`, 4): the gauge formula; hold/release/flight/deep-only cast; the server line
  driving the bobber (bob, tug window, challenge depth), presses and releases sent, stop on put-away,
  no line without a rod; an integrated native host casting into its own server: the bobber flies and
  splashes, 103 is sent, the server's `Line` arrives, an early press reels in empty and no fish appears.
- **Negative controls**, each failing as intended: the 104 kind range, the server leniency, the rod in
  hand and bag, the fishable check, the cast reach, clearing on a switch, the grant, the native press on
  a line, the native fishable check, stop on a switch and the catch-window tug. The rod and fishable
  controls first passed (the test's refusals had other causes); the test now stands the caster on the
  shore and casts 1.5 m off the plane, and the rod control mutates both the hand and bag checks.
- **Visual** (opened): `render_bobber_and_line_on_open_water` shows the red bobber floating on the line
  and pulled under the surface in the window, with the line from the rod-tip point. There is no Godot
  oracle: no source harness casts a line.
  Renders: `bevy/artifacts/bevy-fishing-{line,bite}.png`.
