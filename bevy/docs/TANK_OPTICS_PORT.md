# Tank periscope and gunsight

The native `ui::tank_optics` module ports `TankOptics.cs` and
`PlayerController.UpdateTankOptics` (source lines 9829–9853). It is presentation
only; mounted aim, ammunition, firing and seat/hatch ownership remain authoritative.

An alive first-person tank driver sees the original binoculars mask at 1x.
The tank gunner sees the original item21 8x-scope reticle only while the accepted
hatch is closed. A press of the configured Aim binding cycles 8→12→4, preserving
the source index across seat exit, head-out and death; a new player session resets
it. Third person, other seats, non-tanks and the outside-camera diagnostic suppress
the overlay. Modal UI blocks zoom input while preserving the optic underneath.

The shared resource exposes `look_sensitivity()` as 1/zoom for the gunsight and
1 otherwise. Its logical `gunsight_active()` preserves the original distinction
from a diagnostic-hidden optic image: hiding the viewmodel does not enable a
second generic aiming crosshair. Its input stage follows the Perspective toggle before PlayerUpdate.
It reads newly accepted seating and hatch state directly from the snapshot, so
later native vehicle-component adoption cannot delay sensitivity or the Aim
cycle edge. The player input reader uses the accepted seat for driver/passenger
head limits, aircraft controls and exit gates as well. The visual stage runs
after PlayerCameraUpdate, reconciles late seat materialization and divides the
captured base FOV once. Exit restores that baseline; camera replacement captures
the new camera's base rather than inheriting a stale optic value.

The original two shaders are ported into a native UiMaterial. The periscope samples
the original 1024x576 mask with uniform cover/cropping, retaining round lenses
on narrow/wide windows. The gunsight has radius0.47 of screen height, the original
0.975–1 soft rim and alpha-weighted original reticle pixels to suppress the known
white fringe. Both original textures use linear filtering. The overlay follows
the first-person composite and stays below HUD/menu layers; it ignores picking.

## Verification

Seven focused optics tests and three actual mounted-input tests pass. They exercise real tank occupancy and source
Aim input, 8→12→4 and baseline restoration, repeated camera-base writes without
compounding zoom, UI/death/third-person/session transitions, missing bodies,
viewport resizing, camera replacement, and same-frame accepted head-out state.
The receiving-frame regression places accepted seat/exit facts before input and
native occupancy adoption afterward, then checks actual look sensitivity, seat
limits and Aim cycling. The late visual stage already reconciled final FOV; this
regression fixes input timing, not a newly observed delayed optic image.

Six explicit Vulkan/lavapipe fixtures passed and all PNGs were opened:

- `bevy-tank-periscope.png` and `bevy-tank-periscope-square.png`.
- `bevy-tank-gunsight-{8x,12x,4x,square}.png`.

They render the production overlay over a diagnostic 3D checker wall. The
screen-space reticle stays centered while the background changes scale;
the lenses remain circular at 16:9 and 1:1, the square periscope crops horizontally,
and the diagnostic HUD remains above the black housing. There were no shader
validation failures. Independent source review and three independently opened
aspect/reticle captures found no blocker.

These are optics presentation fixtures, with injected accepted seating. The
subsequent playable PEI tank journey and separate actual UDP observer capture
are recorded in `MOUNTED_NATIVE_PORT.md`. The generic HUD cannon-aim exception consumes the accepted mounted operator,
configured Aim binding and logical gunsight state. Its integration regression
covers driver first/third person, gunner priority, head-out, optic suppression,
modal input and death. Complete armed-fleet multiplayer journeys remain pending.
