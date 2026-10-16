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
