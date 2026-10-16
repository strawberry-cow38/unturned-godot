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
- LocalOnly doors (`DoorProp`): the source spawns its door on the placer's
  machine only, while the host spends the item and keeps no entity. Natively the
  spend happens and no local door is drawn yet (see Remaining).

## Verification

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
