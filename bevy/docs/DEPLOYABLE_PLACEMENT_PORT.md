# Held deployable placement and special deployable bodies

Parity ledger rows 1 (placement) and 2 (special bodies). The source of truth is
the unified Godot game: `game/PlayerController.cs` (`EquipHeldDeployable`,
`TryPlaceDeployable`, `TickDeploy`, the LMB/RMB/R input arms),
`game/BarricadePlacer.cs` (the placer the game actually uses),
`game/DeployablePlacer.cs` (shared ghost materials) and `game/HUD.cs`
(`PlacementHint`). `game/PlacementGhost.cs` is the build/editor tools' box ghost;
no held deployable uses it, so it is not part of this slice.

## Authority

Nothing in the native client places, spends or draws a body. Completion sends
the existing Command 7 (`placeDeployable`) with the click-frozen point and yaw
and the held jar's page/x/y, read from the accepted held address at completion
(`HeldDeployableAddress`). The host validates it in
`ServerTransactions.OnPlaceDeployable`, spends that exact jar (carrying its
condition onto the body) and replicates the entity; the existing native
deployable presentation draws it from the accepted replica.

Server gaps closed in `core/UnturnedNet/ServerTransactions.cs`: the validator now
refuses a dead sender (`TryPlaceDeployable` returns on `_dead`) and a seated one
(a seated LMB is the horn/turret for a driver and nothing for a passenger, so no
hand reaches the ghost). Range (`Range + 4`), item ownership and the storage
adapter's container were already checked. As in the source, surface, clearance
and attachability are client rules; the host does not re-run them.

No wire ids, events or protocol version changed.

## Native placement (`bevy/src/deployables/placement*`)

- **Held def.** The accepted `weapon.held` jar whose catalog item is a
  deployable. The ghost exists while it is held and is drawn while the owner can
  aim (alive, on foot, cursor captured, no capturing UI, window focused) or the
  place gesture plays.
- **Aim (`BarricadePlacer.Aim`).** The source `_cam`: the rendered camera in first
  person, the eye camera (`player::aim_eye`, lean included) in third person,
  where the source also aims from the eye rather than the shoulder camera. The
  ray is the def's `Range` against source bit0 (`player::world_solid`,
  excluding the owner). On a hit:
  - surface gates per mount: Floor `n.y >= 0.01`, Wall `|n.y| < 0.1`, Ceiling
    `n.y <= -0.01`;
  - clearance: a `Radius` sphere at `hit + n * Offset`, ignoring the hit body and
    terrain (the source drops the terrain/ground/structures groups). Players are
    not obstacles: BarricadePlacer's mask is bit0 only;
  - `DefaultAttachable`: a vehicle or another deployable refuses;
  - the submersible band for `WaterDepthMin >= 0`, against the world's sea level
    (25.6 when the world has none, as `Terrain.SeaLevelY` defaults).
- **Yaw.** Floor: camera yaw plus `YawOffset` (R adds 90). Wall: `YawFacing(n)`
  only, since R never reaches the wall family. Sticky: the offset only.
- **Reasons**, most specific first, in amber 46 px under the crosshair (22 px,
  black outline): `Too far away`, `Needs flat ground` / `Needs a wall` /
  `Needs a surface`, `Not enough room`, `Can't build on that`,
  `Needs deeper water` / `Water too deep`. A ceiling def's surface reason is
  `Needs flat ground`, because the source `SurfaceReason` has no ceiling case.
- **Ghost.** `Deployable.BuildMesh` with `DeployablePlacer.ValidMat/InvalidMat`
  (blue/red, alpha 0.25, unshaded, double-sided), the transform
  `MountBasis * MeshBasis` at `MountOrigin` (proved equal to the accepted seat),
  and blue/red port arrows (0.92 alpha) at the raw def port positions in the mesh
  frame, billboarded about their flow axis.
- **Place.** LMB on a blue ghost freezes point, normal and yaw and plays
  `Deploy_Use` (0.967 s; 0.45 s without the arms rig). The ghost stays blue at the
  frozen pose while the camera moves. At the end it sends Command 7, plays the
  def's `PlacementAudioClip` at the point (6 m unit size), then predicts the echo
  as the source does: the last one reverts the hands to what was held before the
  first deployable (`holdItem`), or to empty hands; otherwise the arms settle to
  the carry hold and the next jar of the def is held. RMB empties the hands.

### Deviations and source oddities kept

- Kept: a turbine ghost's tower stands half below the aim point and only the
  tower is tinted (the source ghost overwrites the tower node's transform, and
  `MaterialOverride` does not reach the nacelle or blades). Lamp ghosts show
  the whole mesh (no lens split), as the source ghost does.
- Kept: the source ordinary path sends page 255 (the id search). The native
  intent names the held jar, the address the storage, fluid and door branches
  already send, so a worn or fuelled jar keeps its condition.
- Deviation: the source never clears the hint when the deployable is put away
  (only `TickDeploy` writes it). Natively the hint follows the ghost.
- Not ported: the Window mount (`AimWindow` needs live wall openings, which the
  native world does not expose) is always red `Needs a window opening`. The
  Container mount (storage adapter) is always red `Needs a storage container`.
  No catalog def uses Sticky.
- Kept: a door ghost stands upside down (BuildMesh uses `StandRotX` 90, while
  the placed door uses `DoorDeploy`'s 270).

## Special bodies (`bevy/src/deployables/storage*`)

Sources: `DeployableReplicaView.Sync` (IsStorage, fluid and DeployableCrate
branches), `FridgeDeploy`, `game/inventory/StorageCrate.cs` (`Refrigerator`),
`game/inventory/DeployableCrate.cs`, `PlayerController.OpenNearestCrate` and the
F chain in `_UnhandledInput`.

- **Refrigerator (9130).** Not a Deployable body: `Refrigerator.Spawn` stands a
  yawed node (no stand-up) on the accepted point with the source 0.7 x 1.7 x 0.7
  steel box, the dark handle at (0.28, 1.0, 0.36), a static box collider (also
  exported as host geometry) and the always-on `Refrigerator` Label3D at 1.95 m
  (billboard, no depth test, 56 px at 0.006 m/px, outline 10, tint
  0.85/0.9/0.95). Meshes and label cut off past 80 m (`RenderDist`). Its 200 W
  consumer socket sits at (0, 0.25, -0.36) in that yawed frame, so the wire tool
  and accepted wires use it. Being a StorageCrate, it takes no look focus, no
  readout and no hold-F pickup (`_focusDeployable` needs a Deployable).
- **Campfire (362) and other DeployableCrate bodies.** The ordinary body keeps
  its deployable focus: looking at it shows the source panel and F holds to pick
  it up, ranked above crop harvest as in the source F chain.
- **OpenNearestCrate.** With nothing else focused, F opens the nearest placed
  crate (fridge or deployable crate) whose node origin is within 2.5 m of the
  player's origin, through the existing `openStorage` command and storage
  screen. The source shows no prompt for it, so the native candidate is hidden,
  and it is offered with the storage flag so crop harvest outranks it. The host
  crate is the one `OnPlaceDeployable` registered under the accepted NetId.
- **Deferred.** Fluid devices (`FluidDeploy`/`FluidContainer` tanks, pumps,
  valves and hose ports) belong to the fluid network row and still draw no body.

## Placeable doors (`bevy/src/deployables/doors*`)

The 12 wooden/metal doors, gates and hatches are LocalOnly in the source:
`DeployableNetSchema` keeps no host entity for a `DoorProp` def, so Command 7
only spends the jar, and `TickDeploy` calls `DoorDeploy.SpawnFor` on the
placer's own machine on every path. No other player sees the door, it collides
only for its placer, and it is never saved. The native port keeps that player
view exactly; it is the one placement result drawn without an accepted
replica, because the source has none to draw.

- `WoodenLeaves`: the form's hinge row from `wooden_door_anims.txt`, angle
  clamped to 90 degrees with its sign kept, pivot rebased by the leaf mesh's AABB
  centre, 0.4667 s, `DoorHandle`; Doubledoor (two hinges, one mesh) refused.
- `SpawnProp`: host at the frozen point and yaw, leaf stood up 270 degrees about
  X, the palette material (roughness 1, both faces), and the host raised so the
  closed leaf's lowest corner sits on the point. No wooden door ships sampled
  curves, so the swing uses ObjectDoor's smoothstep.
- ObjectDoor: the leaf's own box (min 0.15 m per axis) on the source bit6 layer
  (solid to the player and look rays), tracking the swing; never solid
  mid-swing, and solid at rest only when no player stands in it. F within the
  4 m door look ray shows `[F] Open door` / `Close door`, toggles locally with
  the 0.35 s cooldown and plays `DoorHandle` (0.125 linear, 6 m unit).
- A new world session removes the doors, as unloading the scene does.

Fluid devices remain undrawn (fluid network row 21).

## Verification

- Storage: native proximity rule test; actual-UDP fridge test (Refrigerator body
  at the yawed point, socket position, no focus or panel, hidden open-storage
  candidate with the storage flag, none beyond 2.5 m); actual-UDP campfire test
  (deployable focus when looked at, open-storage fallback when looking away);
  entities test that a hidden candidate shows no prompt yet dispatches
  `openStorage` and opens the storage screen. Managed actual-UDP: the fridge
  (5 x 4) and the campfire (3 x 2) open by NetId, take an item, close, and the
  host crate keeps it on reopening.
- Doors: `WoodenLeaves` rebase/clamp/refusal, `SpawnProp` seat and height
  (doors stand up, hatches lie flat, the authored top is up), and an actual-UDP
  test placing a door through the native ghost: the host spends the named jar
  and replicates nothing, the placer's door appears closed and solid, F offers
  and toggles it, it is non-solid mid-swing and solid open, and a new session
  removes it. Controls failing their guards: no local spawn, solid mid-swing, an
  unclamped angle, an unrebased pivot, no lowest-corner seat, and a 90 degree
  stand-up.
- Door Vulkan fixture `deployables::doors::visual`. Opened:
  `bevy-door-ghost.png` (the upside-down blue source ghost),
  `-closed.png` (the placed Pine door standing closed on the point) and
  `-open.png` (swung 90 degrees open after F).
- Storage surgical controls, each failing its guard: the fridge taking the
  ordinary focus, no fallback, the fallback offered above crop harvest (no
  storage flag), a 25 m reach, a stood-up fridge, the fridge routed to the
  ordinary body, and a hidden candidate that advertises F.
- Storage Vulkan fixture `deployables::storage::visual` (real local bridge,
  injected LMB). Opened: `bevy-storage-fridge-ghost.png` (the source ProcBox
  ghost, blue), `-fridge-placed.png` (steel body, handle and the Refrigerator
  label; no focus offered, the open-storage fallback recorded),
  `-campfire.png` (the campfire's own `Hold [F]: pick up` panel) and
  `-wired.png` (an accepted generator wire into the fridge's rear socket, wire
  tool held).
- Native: 3 rule tests (surface gates; reason precedence, yaw and water band;
  ghost == accepted seat for floor, upright, wall and ceiling), and 2
  actual-UDP owner tests running the production systems: a blue ghost on
  terrain, R spin, a click-frozen gesture that survives looking away, Command 7
  naming the held jar, no body or spend before acceptance, the host spend, the
  accepted body at the ghost transform and the revert to the prior hold; and the
  red cases (sky, wall, obstruction, another deployable), the hint gate and RMB.
- Core: `dead_or_seated_senders_cannot_place_or_spend`. Managed (actual UDP):
  the named worn jar is spent and its 50% condition becomes 225 HP; out of range,
  seated and dead placers are refused without spending.
- Surgical controls, each failing its guard: attach ignoring deployables, no
  clearance sphere, an unaddressed intent, no revert capture, no R spin, a ghost
  without GroundLift (native); and removing the alive or seated gate (core and
  managed).
- Vulkan fixture `deployables::placement::visual` (real meshes, real local
  bridge, injected ordered mouse edges; not an OS journey). Opened:
  `bevy-placement-valid.png` (blue generator ghost with port arrows),
  `-invalid.png` (red ghost on a wall with `Needs flat ground`), `-placed.png`
  (the accepted generator body and the next jar's blue ghost) and `-wall.png`
  (a blue cagelight ghost flush on the wall).

## Remaining

- Window mount (`AimWindow` onto live wall openings and baked markers) and the
  Container mount (`AimContainer`: storage adapter snapping to container faces,
  door-face refusal, top mount). Both ghosts are always red natively.
- Fluid device bodies (`FluidDeploy`, `FluidContainer`) with the fluid network.
- The held carry model: the viewmodel still shows its existing deployable hold
  (Deploy_Equip with the catalog mesh), not the source `HoldMesh` /
  `EmptyHands` split; only `Deploy_Use` was wired for the place gesture.
- Fridge food preservation is the client-local `Refrigerator.Tick` in the
  source; the native body does not mark `preserved` items.
- The remaining frame of OpenNearestCrate: plain map `StorageCrate`s outside the
  look-based shelves are not in the native world, so only placed crates qualify.
