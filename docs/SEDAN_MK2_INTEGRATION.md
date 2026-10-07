# Sedan Mk II — V17 cleanup and long-nose study (approval pending)

**Local preview only:** this revision is on `Preview-Sedan-V17`, not published.
`Staging-Astra` remains at the currently published V14 commit `b572d1e9`.
Wait for visual approval before moving this model to staging. Main/server are unchanged.

## Player access

Open the F1 dev console and run `vehicle sedan_mk2`. Display name: **Sedan Mk II**.
The key is appended at TypeId **39**. Original sedan stays at TypeId **3**; all prior
keys, source files, prefab seat data and original sedan handling remain untouched.
No existing natural sedan spawns were replaced. This is an additional vehicle.

## Model and runtime

28 new prefixed assets are byte-identical copies of the V17 normal preview exports. The
stock 4x2 palette is retained, including paintable index 0 and fixed charcoal index 5.
The fixed frame, four doors, hood/trunk, moving door panes, static fittings and
four interior pieces are installed separately. Lid undersides are body-painted.
The complete internal wheel housings are already in the approved frame; no diagnostic
or duplicate housing meshes are installed. Total: 2,984 triangles excluding tyres.

An opt-in `VehiclePanelRig` is shared by real cars and replica puppets. It uses body-space
pivots, follows existing glass/collider nodes, shares the body paint and adds no moving
rigid-body chassis walls. Board/exit pulses only the corresponding door, opening to
55 degrees and closing after 2.5 seconds. Internal seat switches do not pulse doors.
Hood/trunk open to 50 degrees with the existing mechanics/storage UI and close on
UI close/teardown. Runtime tick occurs before parked/NetHeld/distance early returns.

Body/stock fittings are 106% of original, tyre radius stays 0.6 m, track 2.18 m,
axles -1.9292/+1.949 m. Wheel mount Y=0.25 m matches the original sedan and
puts the nominal tyre centre at body Y=0 after the 0.25 m suspension drop.
Both native cars settle on the same flat floor: stock root Y=0.55353 m, Mk II
Y=0.55473 m in the focused regression run (1.2 mm difference). The earlier V13
shared-world camera capture settled at stock Y=0.5518 m / Mk II Y=0.5547 m
(2.9 mm difference); neither car is repositioned vertically for the image. All four wheels
are in contact and both ride heights remain stable over the final 20 ticks.
Loaded tyre meshes clear all faceted housing planes with zero measured overrun.

V13 uses a single inverted-U outline (eight upper semicircle facets and straight
lower legs) for the body openings, complete backed housings and liner reliefs.
The inner housing radius is 0.70 m, outer radius 0.75 m. Its nominal centre is
body Y=0, coherent with the native suspension. Lower-body exterior slopes,
1.06 footprint scale, wheelbase, stock fittings and hinge pivots remain unchanged.

The cabin subtraction removes the thick hidden roof/cowl/rear-deck masses; slim
painted jamb returns remain instead of broad interior blocks. Engine/trunk liners
retain thin floors and end bulkheads, without the former full-length side blocks.
The level floor, low tunnel, dashboard and steering support remain. Lower door
metal is about 0.18 m thick (previously about 0.086 m); hood/trunk panels are
about 0.12 m thick (previously about 0.064 m). All lid surfaces remain body paint.
Free-edge underside relief keeps the thicker lids clear during opening, without
changing the closed exterior top edge. Seat geometry is the source topology without
boolean notches: front seats move another 0.12 m back, rear another 0.08 m.
The rear bench is narrowed to 82% width to clear the solid inboard housing backs.

Four explicit seat origins follow the preview shifted seat model. The visible body
keeps the original sedan's tuned displacement coherently scaled and shifted. Real
first-person camera follows the seated skull as the existing game does; DriverEyeLocal
is a fallback, not an assertion that overrides the animated seated head.

## V15 proportion correction

The V14 0.40 m roof drop was visually rejected as too squashed. V15 derives fresh
from V13 and restores half that height: the crown is now **0.20 m below V13**, at
body Y=2.11068692. The exterior below body Y=1.24 remains unchanged; the window/pillar
band is shortened more gently, and the cap's crown/slope planes are translated intact.
All six glazing panes use the same deformation. Footprint, axles, tyre size, wheel
mounts, lower exterior, original fittings, hinge pivots and corrected ride height stay put.

To avoid returning to the old standing-room cabin, the floor top, tunnel and seats
rise **0.12 m**. The floor bottom stays at Y=-0.13, making a single supported slab,
not a floating carpet. Its top is Y=0.03276; source seat bottoms meet it within export
rounding. Fore/aft seat placement and rear width remain as V13. Rear backrest tops
are tapered only on their overly thick rear face (0.16 m forward), leaving their front
surface and 96-triangle source topology intact; this clears the rear glazing.

The steering wheel rises with the seated pose. The support is rebuilt between its
original embedded dashboard base and the raised wheel hub; the dashboard itself
is not moved into the windscreen. Positive support/dashboard overlap is about
0.00020974 m3, support/wheel overlap about 0.00017706 m3. Seat/glass intersections
are zero; minimum rear-seat/glass clearance is 0.0595 m. The floor overlaps the
fixed frame (about 0.1004611 m3), so its underside is connected rather than suspended.

The fallback RoofBox size stays 2.65 x 0.11 x 2.4592, center Y=2.0556869. Actual frame
hulls/roof hit geometry also follow V15. Seated body-anchor Y is 0.0986; driver-eye
fallback follows the final seated pose at Y~1.415765. The normal camera still follows
the real animated Skull bone. The driving body/seat vertical displacement and steering
pivot are updated coherently for real cars and replica puppets.

Actual roof/floor checks leave about 1.98–1.99 m of room over the flat floor, less than
the authoritative 2.0 m standing capsule. Native physics at the center aisle/tunnel
blocks the standing capsule and clears the 1.2 m crouching capsule. This query is
independent of the disabled seated player's collider.

After the driving/sitting blend, actual CPU-skinned bare heads clear BOTH roof and
glass in all four seats: minimum ~0.264 m front, ~0.246 m rear. Head locations are
measured from the actual rig, not backrest-center proxies. This is not a guarantee
for every headwear piece, initial boarding transition, or arbitrary animation overlay.
The showcase waits 25 physics ticks after boarding before capturing the seated driver.

## V16 detail corrections

- **Wheel wells:** outward arch facets, straight leg faces and outboard rim faces
  now use paintable texel 0. Tyre-facing inner surfaces and inboard backs remain
  dark texel 5. Geometry/extents stay unchanged; this is not a blanket recolour.
- **Door jambs:** the separate 26.5 mm lip slab and stepped cutter are replaced
  by a single continuous aperture bevel. Old Y=.9/Z=.2173 and sill Y=.15/Z=0
  probes now see ONE fixed interval per side, approximately X=1.1342..1.3048,
  rather than a disconnected inner slab and cabin shoulder. Original outer shutline
  and moving-door skin/pivots remain unchanged.
- **Dashboard/cowl:** the dashboard moves 40 mm rearward and its outer caps trim
  to +/-1.065 m. Actual dash/frame overlap is zero; steering-column embed remains
  positive (~0.00041439 m3). Moving the dash alone still left a cowl sight gap.
  A body-painted firewall behind the hood hinge closes it, joins the fixed cowl,
  and is relieved around the front wheelhousing envelope. Measured engine-to-dash
  rays now hit the real opaque frame, not merely a nominal bulkhead boundary.
- **Trunk:** one connected painted L-shaped skin extends to the original rear
  surface and down to Y=.49. At lamp heights its half-width is .78, widening to
  the original top width .99852 above Y=.97; taillights stay fixed with at least
  36.2 mm sampled clearance. The old aft top seam is healed, rear cut relocated,
  tall old cargo-wall removed and floor extended to a low threshold. A slim
  recessed painted stop backs the diagonal shutline without filling the central
  cargo passage. Hinge and -50-degree opening remain unchanged.

V15 height, raised seat/floor pack, dash-to-wheel support, original hardware,
headroom, tyre clearance and resting-height behaviour are retained. Native details
checks verify both real car and puppet paint selection, contiguous jamb sections,
actual cowl interception, rear-flange border and backed diagonal seam. Reinstalling
V15 as a negative control makes each changed feature's checks fail; restoring V16
passes all 46 checks. Matched before/after captures use the same compiled runtime,
cameras and lighting, with only the installed model assets exchanged. Sampled
trunk/door sweeps clear fixed geometry and hardware; they are not continuous proofs.

## V17 closure cleanup and separate alternate

The normal V17 retains all V16 proportions, moving skins, hinges and companion
parts. It replaces the old hood/trunk flat lip shelves and rear gap-stop slab
with welded, continuous 6 mm sheet-metal aperture returns. At X=.975/.985/.995,
Z=-2.3 and +2.3, each deck-edge section is a single 6 mm interval. Both lid
apertures remain hollow and the outer closed envelope is unchanged within
numerical export tolerance. Rear shutline backing is integrated rather than a
second stop shelf. No black weatherstrip or cosmetic overlaid cover is added.

A separate **long-nose study** is exported at `models/sedanV17Alt`; it is NOT a
registered game vehicle/type or a replacement for the normal preview. The forebody
is split before wheel/aperture construction, translated forward **0.36 m**, and
joined with an extrusion of its real cross-section from world Z=-1.930 to -1.570.
This is not a whole-car stretch. Old front wheel holes are rebuilt, not left behind.
Front axle Z=-2.2892, rear Z=1.949; wheelbase 4.2382 m. Windshield/side glazing,
greenhouse, rear body and seat pack remain in place. The lower front-door leading
edge is vertical at Z=-1.41722 from the sill to the window base; upper rake remains.
Front hinge pivots are (+/-1.3091,1.0598,-1.41722); hood pivot shifts to
(0,1.209352463,-1.9553). Front bumper, lamps and hood move rigidly forward.

The local `BuildSedanMk2LongNoseStudy` constructor copies wheel/hinge arrays,
updates body bounds and front emitters, and is called only by the offline showcase
with `UG_MK2_LONG_NOSE_STUDY=1`. It does not mutate the standard spec or append
`SpecNames`. Matching alternate assets are temporarily installed for that process;
the runner restores the normal V17 bytes afterwards. Both captures use actual
native suspension; the alternate settles on four contacts around root Y=.5524 m,
versus stock .5525 m, without a render-only height offset.

Export validation initially exposed numerical sliver triangles/parity errors.
Positions were precision-welded and Manifold remeshed coherently, never by dropping
individual triangles or disabling validators. Final exported OBJ normals, closed
edges, signed volume, independent solid-angle winding and ray parity pass, including
simulation of the native original-normal/clockwise conversion. Alternate engine
floor extension is relieved around full-width tyres; all static components clear
the conservative 12-gon .6 m tyre envelope at half-width .200003 m. Opening audits
include the re-cut doors and moved hinges. These remain discrete geometry checks;
native renders are not proof of handling or every possible steering/suspension pose.

## Networking boundary

This isolated preview's base wire format is **v55**, unchanged by these model changes.
This is NOT a claim of compatibility with current main; main has since advanced via
other work. Rebase/integrate and rerun relevant tests after visual approval.
Replicas use the new spec/paint/parts/glass.
Door pulses derive from existing occupant identity snapshots; driver twin-to-puppet
handoff preserves the local exit pulse. Unchanged occupancy does not allocate or
rebuild sets every frame, and closed panels do not rewrite node transforms.

**Hood/trunk UI state is local, not replicated.** This addition does not add remote
compartment interactions, synchronized lid targets, or new vehicle storage/mechanics
protocols. Existing multiplayer glass-break propagation is likewise not expanded.
Server and clients still need this new vehicle's build/assets to spawn/display it.
No main merge or server deployment is performed by this preview.

## Verification

- Build succeeded (existing warnings only).
- L0: all six engine-free suites, 2,364 passed / 0 failed. Rerun with
  `--no-build --no-restore` because this preview changes no core/test project
  sources and concurrent builds exceeded the runner timeout on the shared box.
- Focused vehicle regression L1: 13 tests, 636 checks, all passed. Includes 287 new
  model/rig checks and 42 real-runtime checks for native tyre contact/clearance,
  driver boarding/camera, safe exit, selective doors, UI closure, storage identity,
  teardown, driving and held braking.
- V15 independent authoring audit: all four doors and both lids sampled every
  0.25 degrees through their complete 55/50-degree swings (1,286 poses).
  V16 revised jambs/trunk were additionally checked at finer initial angles;
  the final firewall/closure stop passed the 204-sample trunk sweep and tyre envelopes. No
  fixed-frame/liner/seat penetration in those sampled poses. These are discrete geometry audits, not
  a proof of arbitrary runtime collision behaviour.
- Existing access, door exits, glass, lamps, hit meshes, paint, puppet glass/solidity,
  tyres and wagon tests stayed green. This is not a claim of a full L1 sweep.
- `--sedan-mk2-showcase=DIR` captures the actual game builders, native suspension,
  real PlayerController first-person camera and runtime rig. It does not reload
  the private art-preview scene. Output is explicitly a DIRECTORY, and PNG errors
  abort the capture. Dry rain shader globals are initialized before materials link.
  `UG_MK2_LONG_NOSE_STUDY=1` selects the isolated alternate spec only in the showcase.
  `UG_MK2_DETAILS_ONLY=1` captures matched closed/open rear, engine, trunk and
  close jamb views through the real builder, avoiding private-art-viewer substitutes.
  `UG_MK2_REMAINING=1` resumes just the driver/open-panel views;
  `UG_MK2_HEIGHT_ONLY=1` uses ONE fixed world camera/floor after native suspension
  settles, with no per-car camera recentering or height adjustment.
- Captures: original/closed/open side/front/rear, real driving 1P, open cabin, engine bay
  and trunk. Opening endpoints are inspection poses through the runtime rig;
  mechanics/storage input lifecycle is independently tested through the real UI methods.

## Portable asset audit

`python3 tools/install_sedan_mk2.py --check` verifies installed files and original
asset hashes through `docs/SEDAN_MK2_ASSETS.json`, even without the private art workspace.
Reinstallation requires the V17 normal preview workspace via `--art` and frozen palette via
`--frozen`; the installer is a byte copier, not a geometry-authoring recipe.

## SHA-256: normal preview source geometry and installed bytes

Every row below is byte-identical source -> installed asset. Manifest SHA-256:
`cc7169fcc012463958fdf3cf5c4c00c57a05bc8715a8e5cfc8f5b8ab0a21ff5a`.

| Source file | Installed file | SHA-256 |
|---|---|---|
| `sedan_frame.obj` | `sedan_mk2_frame.txt` | `cf97df89bc6b6f97d3aa56305f2c8635580b846385e3323676117f89fd249d15` |
| `sedan_front_door_left.obj` | `sedan_mk2_front_door_left.txt` | `d09dc8b87e2e5d6797cdb8037aeac9b50f5bd40b8e08b759d85f9aba723f28da` |
| `sedan_rear_door_left.obj` | `sedan_mk2_rear_door_left.txt` | `a0a57f84c309cd3ac04a945be9ce2512229d6efa0845cb5a2021d5a9325d7f31` |
| `sedan_front_door_right.obj` | `sedan_mk2_front_door_right.txt` | `4074a20132f548a66418813c649a7598e4ddd9491f6af6fa735fd11effd101a6` |
| `sedan_rear_door_right.obj` | `sedan_mk2_rear_door_right.txt` | `7ff648bac36444b60e65ea685e5b8b6ee2260a53bb4ffcc544b721dcc42079fe` |
| `sedan_hood.obj` | `sedan_mk2_hood.txt` | `2e513a74b847f0706ac0abbf8f9d67621166854eae613cbf621d7cc6ff813eea` |
| `sedan_trunk_lid.obj` | `sedan_mk2_trunk_lid.txt` | `ed5fc009e99a2e88c26aacb15adf98e01b20e556b06f0af547eb89b4b1e96ef8` |
| `sedan_glass_l_front.obj` | `sedan_mk2_glass_l_front.txt` | `223d1da2fe2dc98c8c792e4054e17ae74097665d748d12ba91814a092598f26a` |
| `sedan_glass_l_rear.obj` | `sedan_mk2_glass_l_rear.txt` | `a0fe986d67e2da6f3af40aa1aefa26d4976d635b3dc9643faf27287964767169` |
| `sedan_glass_r_front.obj` | `sedan_mk2_glass_r_front.txt` | `0a9c9a46469b5e543485f07c8bb202f68f3f7c09e8acfa314f414d293342192e` |
| `sedan_glass_r_rear.obj` | `sedan_mk2_glass_r_rear.txt` | `46b0f498b42bd1d6abf95f06c5137e6eb97e2d10d4c7d3a2993cd3742accdf21` |
| `sedan_glass_rear.obj` | `sedan_mk2_glass_rear.txt` | `bcc06fafe2ba97dcf1f7a68d7dab34b4b747505b74751816e6da6fdd4f8ce002` |
| `sedan_glass_windshield.obj` | `sedan_mk2_glass_windshield.txt` | `a9f8346c623802465963cb0ea3b96fa2f5241a6d86c02e38e7245e1be2907671` |
| `sedan_stock_front_bumper.obj` | `sedan_mk2_stock_front_bumper.txt` | `30ced7ffce1dcf3dd631bcda9dca60242b4d548bc6affe215f259e45295efac7` |
| `sedan_stock_rear_bumper.obj` | `sedan_mk2_stock_rear_bumper.txt` | `cf8c07161f34ee5e6874bde043a8228a884564a624f62bffdba284bd933ebea0` |
| `sedan_stock_exhaust.obj` | `sedan_mk2_stock_exhaust.txt` | `892409df2dfc5aad899599d9ad5404743684915e7b6b054bb1224c627f49ca65` |
| `sedan_stock_headlights.obj` | `sedan_mk2_stock_headlights.txt` | `51bb3b2b658ce3e90e58e73de531aa52fcb84b7ded87e7bc46c012ca368829a0` |
| `sedan_stock_taillights.obj` | `sedan_mk2_stock_taillights.txt` | `f96953110910401f91bd39b7d66f7e92d4d896ec7edb60277624c0259a077a5b` |
| `sedan_stock_hitch.obj` | `sedan_mk2_stock_hitch.txt` | `adff7e44427813c2349b45cb7710829046cb65f6372f60424f77edc2c2433588` |
| `sedan_stock_seats.obj` | `sedan_mk2_stock_seats.txt` | `8d26a0f149b3646a1544ac37938328cea6305be0423c783392f8e8f62a9ea160` |
| `sedan_stock_steer.obj` | `sedan_mk2_stock_steer.txt` | `880473cc6daf7a3fb520bc9d99ee1f0c4533a5cbc2e1c21212eda6f81f91e66f` |
| `sedan_dashboard.obj` | `sedan_mk2_dashboard.txt` | `ccdf14275efe4faf1312f9388aa64867654bce4a888acf04fb85638862345ac8` |
| `sedan_steering_column.obj` | `sedan_mk2_column_support.txt` | `767f79470bcb4ca059db379af1e7ddc017f1a7468276b1c43e6214524f63fb27` |
| `sedan_cabin_floor.obj` | `sedan_mk2_cabin_floor.txt` | `fff49c93f38102851b03a1fed9fdc57b9a8fa29e5e8be7bf1bf2ad4a44f9edbd` |
| `sedan_floor_tunnel.obj` | `sedan_mk2_floor_tunnel.txt` | `047e85767f954e2763808697a253c14928f85a70a82800b453885e501ec77161` |
| `sedan_engine_bay_liner.obj` | `sedan_mk2_engine_bay_liner.txt` | `104481bb736844a79fe8a5968edf72472335a4003ede31731f9161c6df3b445f` |
| `sedan_trunk_liner.obj` | `sedan_mk2_trunk_liner.txt` | `0237fdbdc2c6e9fd36ad2e0afa39211e51343b1ea69c0f450effe889cae5f8d8` |
| `sedan_palette.png` | `sedan_mk2_palette.png` | `df13338fc3cd5ac95c0de41b7273ae3d3f9a4c4a815f3113ee898bc3637b276b` |

### Frozen original sedan sources (unchanged)

| File | SHA-256 |
|---|---|
| `jeep_wheel_albedo.png` | `7acdadfdb30488d32b32458359470d6ee88957e7b0aec3b02b71372c55534c90` |
| `sedan_body.txt` | `6682845c86063fc8e48e4d59a4b2ad485ef2d95e46c0584e99ff430a4792b2b7` |
| `sedan_glass_l_front.txt` | `813cc5929dfa8bbcc9097738a5b6097c7ba781c716d5b4fb7b21907a78bb1e85` |
| `sedan_glass_l_rear.txt` | `c4de9c451e89131b9b29f96dfcdd83f5f8a6297b43fbc3455c528d210202b2c5` |
| `sedan_glass_r_front.txt` | `f16a0dbbf0ba7be9925984596f9cd4c27be635ab8e7b5add66b4d7102d0f6173` |
| `sedan_glass_r_rear.txt` | `ac26ac3182248212b36c0d528e0171035447b4fffb5f6dd63dbcb733e1314526` |
| `sedan_glass_rear.txt` | `bf2e5c04d326d4af1389cc7837b6d5c8a6c014f2a3fbf73d6fd92029122abff3` |
| `sedan_glass_windshield.txt` | `7466ef6015e552ed9e40e57c7c3cda6d268f8a8fef394ab7ff04e6125b834132` |
| `sedan_headlights.txt` | `513c6cfa3867d57f3f7dedef1bde3d786e6f1642164b8f79951980af6233fbdb` |
| `sedan_hitch.txt` | `7c61fd2d3db8add6f04043223e65b91468e449d183dffab8d4616f5b6d44b263` |
| `sedan_palette.png` | `df13338fc3cd5ac95c0de41b7273ae3d3f9a4c4a815f3113ee898bc3637b276b` |
| `sedan_seats.txt` | `223feb7f6345720608a81920e15ab30f1d61d8ed5d50cfafc3b8bcb1b2c459bf` |
| `sedan_steer.txt` | `52e096bcab44c183d9345aab9d804f82052fc38abad7248993706eefb677a064` |
| `sedan_taillights.txt` | `47488dc09847d2e8c0b7ead2627015dcbf7a8bb8c331b2afc86d93d8828fac75` |
| `sedan_wheel.txt` | `0f69e9fc1e9d4f12cae59bf83dca7e2a540dc1eb1018722d360f3e5c0a261e73` |
