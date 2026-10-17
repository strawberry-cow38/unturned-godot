# Proximity vehicle enter port (ledger row 15, part C)

Sources:
- `game/PlayerController.cs:6535-6562`: `NearestPuppet` and `RequestEnterNearestPuppet` (C6).
- `game/PlayerController.cs:8977-9050`: the F (Interact) chain and its rank.
- `game/VehicleReplicaView.cs`, `game/VehiclePuppet.cs`: the puppets it scans, and their look focus.
- `core/UnturnedNet/VehicleReplication.cs`: `EnterVehicleCommand.AnySeat` (255) and `ServerVehicles.CanEnter` (`EnterReach` 6 m).

## How the source works

`NetEnterVehicle` is set only by `ClientWorldSession`, so only a joined MP client runs this; singleplayer and the listen host take the early return. `NearestPuppet` scans the `vehicle_puppets` group and keeps the first strictly nearest puppet with `GlobalPosition.DistanceSquaredTo(p) < 4²`: player origin to vehicle origin, in 3-D, with no filter for wrecks, trailers or occupancy (the server judges those). The request is Command 21 `EnterVehicle` with seat 255. The door-zone seat applies only while `_focusAccessValid`, which a puppet never sets. `CanEnter` validates reach, occupancy and life; the seat arrives through the `VehicleEntered` fact.

It touches no focus state, so it has no prompt and no outline. A puppet's own rim and name tag come only from looking at it (`_focusPuppet`), which is independent of F.

In the F chain it is tried after:
- closing a note or container;
- vehicle exit, train/crane exit, standing up from a prop seat, and exiting a puppet;
- `TryToggleHitch`;
- shelf or dropped item pickup, and replicated item pickup;
- a looked-at `_focusVehicle` (door, hood, trunk, bus door).

It is tried before every other branch: couplers, trains and cranes, deployables, fluid devices, doors, prop doors, forage, the car lift, TVs, radios, vending, lamps, light switches, elevator buttons, monitors, trading/dialogue, NPCs, notes, beds, seats, crop harvest, shelves, crates and inspect.

## Native

`bevy/src/vehicle/proximity_enter.rs` (`ProximityEnterPlugin`):
- **Scan.** It runs in `CollectInteractions` only when the snapshot says `network.ownsWorld == false`. It is skipped while UI captures input, while seated (vehicle `Occupancy` or `player.vehicleNetId`) and while dead. It takes the first strictly-nearest `VehicleBody` origin to the `PlayerBody` origin (the feet, as in the source and in crop harvest), with `d² < 16` and NetId ≠ 0.
- **Offer.** `InteractionFocus::offer_proximity("enterVehicle", {netId, seat: 255})`. The existing bridge action maps it to `NetWorldClient.SendEnterVehicle(netId, 255)`, so there is no wire change. The client never seats itself.
- **Not on `--dedicated`.** The plugin is added in `add_game_plugins` only under `!dedicated`, beside the outline and bug reporter. A dedicated host also owns its world, which is a second gate.

`InteractionFocus` (`bevy/src/entities.rs`) gains a proximity dispatch tier that sits beside the distance-ranked look candidate rather than competing with it:
- `preempted()` is true when a proximity offer exists and the look winner is not one of the branches the source tries first. Those branches are `outranks_proximity`: `sitSeat` with NetId 0 (stand up), `vehicleHitch`, `pickupItem`, shelf `takeFromStorage`, and the looked-at vehicle actions `enterVehicle`, `openVehicleHood`, `openVehicleTrunk`, `vehicleDoor` and `vehicleDoorMoving`. Vehicle exit never meets it, because nothing on foot is offered while seated.
- `dispatch()` is what `apply_interaction` sends on F: the proximity action when preempted; otherwise the non-manual look candidate, as before. A manual look candidate (hood/trunk/placed door) still keeps F for its own system.
- `selected()`/`selected_args()` mean "F belongs to this target" and are false while preempted, so the manual F systems (deployable hold/tap, placed doors, world devices, the demo door) do not also fire. `looked()`/`looked_args()` keep the plain look winner for what the source shows regardless of F: the scenery-lamp and vending billboards, and the deployable billboard plus its mouse fuel actions. `deployables::interaction::apply` now tracks the look winner and drops only its F handling while preempted.
- The look candidate's label and outline stay on screen while preempted, as the source's look focus does; with no look target the HUD shows nothing.
- `idle()` (no candidate and no proximity) replaces `!has_candidate()` in the viewmodel inspect fallback, so F at a nearby car does not also inspect the weapon. `has_candidate()` keeps its look-only meaning, so the vehicle hull fallback in `access::focus` is unaffected.
- Crop harvest (`fall_through`) is a look candidate at infinite distance, so the tier outranks it automatically.
- **Every F consumer goes through `selected*`.** NPC `activate` (`entities/npcs.rs`) used to read the candidate directly, so F on a trader opened the dialogue while proximity enter also sent the seat request. It now uses `selected_args("npcTalk", …)`. The only other direct reads of the candidate are the shelf-item tag and its silent offer, which are look-only presentation, and `TAKE` outranks proximity anyway.
- **Wrecks and trailers offer no vehicle access** (`vehicle/access.rs`). The source's `_focusVehicle` branch requires `!IsWreck && !IsTrailer` (`IsTrailer` = a kingpin), so a looked-at husk or trailer offers no door, hood, trunk or bus door, and F falls through to the next branch: proximity enter on a joined client. The hitch offer above it is the trailer's only F, as `TryToggleHitch` is. Before this, a husk's or trailer's door zone offered `enterVehicle`, and a trailer offered its trunk.

## Deviations

- **Restraints, fishing, binoculars, umbrella** (merged at v81) add no F action: cuffs/keys and the fishing cast are LMB, binoculars RMB/LMB, and the umbrella is passive. None is in the source F chain, and `IsArrested` gates only equipping, so `outranks_proximity` is unchanged.

- **A looked-at vehicle on a joined client.** In the source a joined client's cars are puppets, never `_focusVehicle`, so every remote enter goes through this proximity path with seat 255. Natively, look access (`vehicle/access.rs`) already serves joined clients with door seats, hood, trunk and hitch, and it keeps ranking above proximity, as `_focusVehicle` does in the source. Proximity enter covers the rest: not looking at the car, or looking at a lower-ranked target.
- **Rank among look targets.** The native look winner is chosen by ray distance across every collector, while the source walks a fixed list of per-kind focus fields. Proximity compares against that single winner. An outranking target hidden behind a nearer lower-ranked one (for example an item behind a door) therefore loses F to the car, where the source would pick it up. The same approximation already governs every other native F choice.
- **Wreck/trailer `_focusAccess` seat.** When the source reaches this branch with a looked-at wreck or trailer that resolved a door zone, it forwards that zone's seat to the nearest puppet. Natively it is always 255.

## Verification

- **Cargo tests** (`vehicle::proximity_enter::tests`, through the production registrations of the proximity system, crop harvest and `access::focus`):
  - 3.9 m remote: enters with seat 255 over a grown crop and over a deployable. The deployable keeps its look (billboard, rim) but loses F, and no rim is claimed for the car.
  - 4.1 m: harvests.
  - Owning worlds (SP with no network block, `local`, `listen`): never offered; F inspects or harvests.
  - Stand-up, hitch, item pickup, shelf take, a look enter and the bus door keep F. Hood, trunk and a moving door keep F for their manual systems.
  - 17 lower actions, by their real offered names and args, lose F: `npcTalk`, `claimBed`, a non-zero `sitSeat`, `toggleObjectDoor`, `openStorage`, `forageResource`, `toggleDoor`, `placedDoor`, `ordinaryDeployable`, the seven world-device actions and `harvestCrop`. Each is checked against its collector's source text, so a misspelled name cannot pass by default. Each also owns F with no car near, and keeps its look (`looked_args`) while preempted.
  - A real Rapier look at a husk's or a trailer's door zone falls through to a live car 3 m behind, while the same pose on a live police car keeps its door (the control).
  - `npcs::tests::proximity_enter_takes_f_from_a_looked_at_npc_on_a_joined_client`: with the production NPC focus/activate and a car 2.5 m behind, F leaves `CurrentNpcSpeaker` empty and the bridge opens no dialogue, while the trader still wins the look.
  - A real Rapier look at a police door zone enters that door's seat, although a nearer puppet stands behind the player. Looking up, the same press asks for the nearest puppet with seat 255.
  - Seated, dead or in a menu: never offered.
- **UDP check** (`vehicle::proximity_enter::udp`): separate host and client bridges over OS UDP. A host Roadster stands 3 m from the joined player. One F press through the production `apply_interaction` sends `enterVehicle {seat 255}`, with no prompt. The snapshot shows no seat until the host's echo, then `vehicleNetId` and `driverPlayerId` equal the joiner (any seat, driver first).
- **Negative controls**, each restored byte-identical (sha256) from `bevy/.local/nc-backup/`:
  - system removed: 4 failed;
  - every look target loses to proximity: 2 failed;
  - proximity never preempts: 4 failed;
  - owning-world gate removed: 1 failed;
  - NPC `activate` back on the raw look winner: the NPC test failed;
  - wreck/trailer gate removed: the husk test failed (the husk's door seat won);
  - `npcTalk`/`claimBed` marked as outranking: the rank test and the NPC test failed.
- **Visual** (two processes under Xvfb/lavapipe): a native `--listen 47993 --vehicle roadster` host and a native `--connect` client.
  - The client walked to 3.0 m from the Roadster, turned away (no prompt, no rim) and pressed F. It was seated as the driver: `vehicleNetId` 10, `driverPlayerId` 2.
  - Frames: `bevy/artifacts/bevy-proximity-enter-{1-beside,2-seated,3-host}.png` (gitignored). The scripts are in `bevy/.local/journey-m/`.
