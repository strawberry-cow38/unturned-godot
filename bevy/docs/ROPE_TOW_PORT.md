# Rope tow port

Source: `game/Vehicle.cs` (`AttachTow`, `DetachTow`, `UpdateTow`,
`SetTowedFreeRoll`, tow constants and tow points), `game/TowRope.cs`,
`game/VehicleNetSync.cs` (`RegisterCommands`, `OnAttachTow`, `OnDetachTow`,
`IsRemoteDriven`, `WithinReach`, the enter guard) and the `game/PlayerController.cs`
rope tool (`UpdateRopeLook`, `RopeLmb`, `RopeManageArm`, `UpdateRopeManage`,
`RopeHudSet`). This is distinct from the fifth-wheel hitch
(`VEHICLE_HITCH_AUTHORITY.md`, `VEHICLE_COUPLING_NATIVE.md`).

## Authority

`bevy/bridge/VehicleTowServices.cs` is the game-side choke point that source
`VehicleNetSync` was. It registers commands 30/31 with the source validation
order:
1. Both vehicles exist.
2. Neither end is remote-driven: a driver other than the listen host's own
   player (`_client.PlayerId`) blocks it.
3. Neither end is already roped.
4. The requester is within 9 m of both vehicle origins.

`Vehicle.AttachTow` then rejects wrecks, a pair already joined by the semi
hitch, and a tower-rear to towed-front gap over 4.5 m. The rest length is
`clamp(gap, 2, 4.5)`, so a tie never yanks. Either end detaches.

`Step()` drops a rope when either end is wrecked or missing, or has been
boarded by a remote driver (the source enter guard). It also drops a rope whose
tow points are more than 7.5 m apart (`TowBreakLen`). The source judges that
snap on the physics host each tick. Here it is judged from the accepted poses
that host publishes, so only one writer mutates the relationship. The existing
`ServerPublishTow` carries `towedNetId`/`towRestLen`. On the wire the rest
length is floored to 1/16 m and capped at 3.9375 m, a cosmetic copy
(`VehicleReplication`). Source keeps the exact `_towRestLen` on the host node,
so the listen host's state row reads the exact value from this service, and only
remote peers draw the quantized one. A 4.2 m tie therefore pulls from 4.2 m and
does not yank 0.26 m on attach. Source `WorldSave` never persisted ropes and
neither does the port. No wire ids or bytes change.

`StepPlane`/`StepHeli` return before `UpdateTow`, so an aircraft tower never
snaps its rope; the authority skips the 7.5 m check for one.

One hybrid-only rule: a coupled trailer is simulated by its cab driver's host
(`coupling::state::simulates`), whereas source keeps every trailer on the host.
A trailer whose cab has a remote driver is therefore treated as remote-driven,
which refuses a tie and drops an existing rope. Otherwise each end of the rope
would be simulated on a different host.

## Native physics and presentation

`vehicle/rope.rs` reads the accepted relationship from the vehicle replica.
Each host applies source `UpdateTow` to the rope ends it simulates. The rope
only pulls: past rest it applies `clamp(20000 * stretch + 3200 * separation
speed, 0, 30000)` N. The towed car takes it at its front tow point and the tower
the opposite at its rear point. Tow points are the box face centres at
`BoxCenter.y - 0.3 * BoxSize.y`, 0.15 m outside the hull.

The lever arm copies a source quirk. Source calls
`ApplyForce(f, point - ToGlobal(CenterOfMass))`, and Godot 4.6 (Jolt) reads that
position as an offset from the body origin, then measures torque from the CoM
offset. The effective arm is therefore `point - origin - 2·R·CoM`.
`bevy/tools/apply_force_arm_reference.py` records this on the real engine
(`bevy/data/apply-force-arm-reference.json`, including a yawed, off-axis CoM
case), and `rope::apply` reproduces it.

The tower's engine force is multiplied by 0.9 while it tows (`if (Towing != null)
eng *= 0.9f`). The tank path returns before that line, so tracked towers are
exempt. Attach, detach and every taut tick `Wake()` both ends, which clears the
source `_parked` latch, so anti-roll runs on a dragged car. An aircraft tower
applies no force; an aircraft towed by a car is pulled.

The towed car free-rolls with wheel friction slip 0.5 (`SetTowedFreeRoll`). Its
per-wheel slip is saved on attach and restored on release, which matters for
tracked and fixed-wing hulls that never rewrite their own. The source skips its
surface-grip resample while towed; the port's drivetrain rewrites friction
every tick, so 0.5 is applied after that pass. The two ends get a mutual collision exception and
no layer swap. Rope ends are never parking-braked or settled, matching source
`towed`.

The committed rope is ten hemp cylinders (radius 0.032 m, colour
0.42/0.30/0.16, roughness 0.9) sagging by half the slack plus 6 cm, with the
same segment basis as `Wire.SetPoints`.

The rope tool (item 64, tool kind Rope) shows tow nubs on every vehicle: blue
front and green rear, 16 cm, unshadowed. The towing car's front and the towed
car's rear are hidden. The aimed nub is emissive. It picks analytically: an aim
ray within 6 m and a perpendicular distance under 0.7 m. LMB starts on an
unroped rear node and completes on another car's front with an `attachTow`
intent. RMB cancels a pending tie, otherwise it arms manage on a roped car:
releasing within 0.28 s sends `detachTow`, and holding for 1 s clears. The
preview rope uses rest 2 and is red until it reaches a valid front node.

HUD strings are the source's:
- `tow rope   x.x/4.5m`, with `   -- TOO FAR` or `   [LMB] tie`
- `[RMB]  hold: clear rope  ·  tap: disconnect`
- `[LMB] start tow`
- `clearing tow rope... N%`

Unlike the wire tool, look runs before manage, so the clearing percentage is
visible. As in source, the pending tie length is shown even while a panel
captures input. The length uses .NET's `0.0` midpoint rounding (away from
zero). Rope and wire readouts share the layer-40 label slot (`ToolReadouts`),
written before the `ToolHud` draw in the same frame; the held tools are mutually
exclusive.

## Known deviations

- **Rope mesh with an aircraft tower:** source never runs `UpdateTow` for an
  aircraft tower, so its rope mesh freezes at the attach-time endpoints. The
  port draws the accepted rope between the live tow points.
- **Nub visibility:** updated every frame. Source updates it only on the
  equip/unequip edge.
- **Click target:** a click uses the node aimed this frame. Source uses the
  previous frame's look result.

## Verification

- **Native, 13 tests:** tow points; spring, damper and clamp; sag; physics drag
  of a free-rolling parked car; the recorded Godot ApplyForce arm; tracked slip
  save/restore; the 0.9 nerf and taut-rope wake; aircraft ends; the mutual
  collision exception; midpoint rounding. Two integrated tool tests run against
  a real local bridge and adopt state through production `sync_vehicles`; they
  also cover the exact host rest, the attach wake and the input-captured HUD.
- **Managed, 8 tests over UDP:** exact host rest vs the quantized wire; reach,
  gap, duplicate and wreck refusals; either-end detach and the 7.5 m snap; wreck
  and despawn drops; remote boarding of either end vs the host's own driver; a
  hitched pair refused, and a remote cab dropping its trailer's rope; the
  aircraft tower's no-snap.
- **Negative controls:** each fix was reverted and its test confirmed to fail.
  That covers exact rest, coupled-cab remote, aircraft snap, the 0.9 nerf,
  taut wake, attach wake, slip restore, both aircraft filters and the arm
  quirk.
- **Vulkan fixture:** `render_source_rope_tool_preview_accepted_rope_tow_and_clear`
  (`UG_ROPE_SHOT`) drives injected ordered mouse edges against a real local
  authority. It shows nubs, a free preview, a tie preview, the tied rope, a tow
  under power and the clearing percentage. Renders are in
  `bevy/artifacts/bevy-rope-tool-*.png`. This is not a full-game OS journey.
