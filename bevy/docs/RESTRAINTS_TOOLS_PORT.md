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
