# Sedan Mk II — approved long nose, V18 geometry cleanup

Approved by strawberry on 2026-10-07. The long-nose alternate is now the normal
`sedan_mk2` builder, not a harness-only study. Original sedan assets and TypeId 3
are unchanged. Mk II retains its existing TypeId 39; no natural sedan spawns were
replaced and no second new vehicle type was added for this revision.

## Player access

Open the F1 dev console and run `vehicle sedan_mk2`. Display name: **Sedan Mk II**.
This branch incorporates current main `288f4ee5` (pipes/durability/ladder changes).
It inherits main's **wire v57**; this vehicle revision adds no protocol change.
This is not a claim of compatibility with a live server or a different staging
branch. Main/server are not updated by publishing this staging branch.

## Approved geometry and canonical runtime

- Cleaned exports from `models/sedanV18`, derived from the approved V17 long nose.
  Current manifest SHA256 `7eed246ea1e5d50c400b1f626ada43f556571b5a349d47dbcc7b7162ff79c183`.
  28 prefixed assets; 2,914 triangles excluding tyres. The stock palette retains
  paintable index 0 and fixed charcoal index 5. Fifteen original source asset
  hashes are preserved and checked separately.
- Forebody split before wheel/aperture construction, shifted rigidly **0.36 m**
  forward, then joined with its real cross-section extruded through
  world Z=-1.930..-1.570. This is not a whole-car stretch. Old front wheel holes
  are rebuilt rather than left in place. Windshield/side glazing, greenhouse,
  rear body, seats, floor, steering wheel and dash retain the approved V15/V16 fit.
- Front axle Z=-2.2892; rear axle Z=1.949; wheelbase **4.2382 m**. Stock 0.6 m tyres,
  2.18 m track and 0.25 m mount/rest-drop remain. Body scale remains 106% of the
  original, except for the locally reconstructed nose extension.
- Front lower door leading edges are vertical at Z=-1.41722 from sill to beltline.
  Front pivots (+/-1.3091,1.0598,-1.41722) follow those actual edges; upper window
  rake is retained. Hood pivot (0,1.209352463,-1.9553) follows the relocated hood.
  Hood, front bumper, headlights and their real light emitters shift together.
  Rear emitters, hitch, exhaust, tail lamps and rear hinges do not take that shift.
- Fallback body box size (2.65,.97096,6.35536), center (0,.60188,-.24678); the real
  mesh hit geometry and puppet/focus/tow bounds derive from the same canonical spec.
  The offline alternate constructor/environment flag was removed: F1, real cars,
  native showcase and replica puppets all use the normal spec.
- Hood/trunk closure filler shelves and rear stop are replaced by integrated 6 mm
  folded sheet-metal aperture returns. Openings stay hollow, trunk lid remains a
  single body-painted L-shaped skin extending down the rear face to Y=.49, leaving
  fixed tail-light borders. Lid undersides remain body paint.
- Single-bevel painted door jambs; no black weatherstrip or three-step inner lips.
  Wheel housings remain complete inverted-U shapes with solid inboard backs;
  outward/rim facets use body paint, tyre-facing surfaces remain dark. Full-width
  tyre-envelope checks include the extended engine-bay floor.
- Dashboard is trimmed to +/-1.065 m and shifted 40 mm rearward, with a real
  wheel-relieved painted firewall closing the engine-to-dash sight gap. Level
  floor, low tunnel, clean bay and cargo liners retained.

## Cabin and panels

V14's 0.40 m roof drop was rejected. This model retains V15's middle proportion:
roof 0.20 m below V13, maximum body Y=2.11068692, not a rescaled chassis. Floor,
seats/tunnel/wheel rise 0.12 m while the floor bottom stays at Y=-.13. Floor top
Y=.03276; tunnel top Y=.18. Front seat total Z shift +.30, rear +.14 with 82% rear
bench width. Rear backrest top-rear surface is relieved, front/topology retained.
Seat origins, tuned body bias and steering support agree for real cars and puppets.

Actual native tests block the independent 2.0 m standing capsule and clear the
1.2 m crouching capsule. Fully blended CPU-skinned bare heads clear both real
roof and glass in all four seats. This does not guarantee all headwear, initial
boarding transitions or arbitrary animation overlays. Driver capture waits 25
physics ticks rather than capturing mid-blend at 5 ticks.

`VehiclePanelRig` is shared by real/replica builders. Board/exit pulses only the
corresponding door (55 degrees, 2.5 second hold), not an internal seat switch.
Hood/trunk open 50 degrees with existing mechanics/storage UI and close on UI
teardown. Glass/query nodes follow the correct pivots; painted parts share the
body material. Runtime tick precedes parked/distance/NetHeld early returns.

Replica head/tail lens materials for the separate Mk II lamp assets now register
with existing lighting flags. They are per-puppet, so head/tail/brake/alarm emission
does not leak between replicas. This is opt-in to Mk II, not a generic original-car
rewrite or a new network field.

## Networking boundary

Occupancy-derived door pulses use existing snapshots. **Hood/trunk local UI-open
state is not synchronized**, and this model revision does not extend existing
remote glass/lamp breakage protocols. These remain separate work. Replica collision
retains the existing generic spec-sized box instead of a duplicate physics vehicle.
The model revision itself makes no core/server/wire changes beyond the inherited
main merge. Staging-Tinyclaw's later v58 car-storage work is not folded into this branch.

## Validation on the integrated main tree

- Build green (0 errors; 23 existing warnings).
- L0: **2,444 passed, 0 failed**, all six engine-free suites rebuilt/run.
- Targeted L1: **13 tests / 682 checks**, all passed. Covers installed art/hinges,
  real and puppet builders, front/rear lights and emission, native tyre contact and
  clearance, actual bare head/camera/standing/crouching fit, entry/exit/UI teardown,
  hit meshes, paint, glass, tyres and wagon controls. Not a full L1 sweep.
- Native rest: original root Y=.55353, Mk II Y=.55229 (1.24 mm difference); all four
  tyres in contact, stable final 20 ticks, no measured housing-plane overrun.
- Matched native details capture uses **normal `BuildByName`**, not an alternate
  helper. Seven actual closed/open detail frames saved; process exited 0. Shared
  ground render settles original .5525 / Mk II .5524, without height offsets.
- Independent exported geometry audit checks consistent winding/closed meshes,
  backing/hollow openings, full-width tyre envelope and sampled panel sweeps.
  Discrete geometry/render checks are not proof of every handling/suspension pose.
- Read-only independent integration audit verified spec/F1/puppet routing, bounds,
  original assets and main merge seams. It found the separate-puppet lamp-material
  registration gap; that gap is fixed and covered by installed-material tests.

## Portable asset audit

`python3 tools/install_sedan_mk2.py --check` verifies approved installed hashes and
unchanged stock assets even without the private art workspace. Reinstallation
requires the V18 cleaned long-nose workspace via `--art` and frozen palette via
`--frozen`. Installer is a byte copier, not a hidden mesh-processing operation.

## V18 conservative geometry cleanup

Only four installed OBJ assets change: fixed frame, both rear door skins and
cargo liner. Redundant coplanar subdivisions and microscopic join facets are
collapsed coherently, rather than dropping faces. Frame 1736→1730, rear doors
88/100→80/80, cargo liner 118→92. Total 2974→2914; not a target-budget remodel.

All original-body design angles, palette selection boundaries, structural returns,
panel thickness, wheelhouse backs, firewall, openings, seat/glass/hardware fit and
runtime pivots/spec remain. Twenty-three companion OBJ assets are byte-identical.
No detached component was found in the eleven inspected closed solids. The
cleanup does not claim that every subjectively unwanted interior feature is gone.

A bidirectional same-colour triangle-coverage audit bounds fixed/rear-door surface
changes below 0.02 mm; cargo-floor retessellation has a tighter nanometre-scale
bound. These are geometric bounds, not nearest-vertex samples. Surface winding,
closed topology and normals are independently validated on the exported OBJ. A
trunk-lid simplification trial could not establish the same full-surface proof,
so it was rejected: the approved lid geometry stays byte-identical.

Changed rear doors are re-swept against ALL fixed geometry; unchanged moving
parts are re-swept against the changed frame/cargo liner. Each door has 224
poses and each lid 204, with initial .01/.05/.10-degree probes and .25-degree
steps. Unchanged pairs reuse the preceding byte-identical source evidence. No
measured overlap >1e-10 m3, no static/conservative nominal-tyre interference.
The same 13 runtime tests/682 checks pass with the cleaned installed meshes.
Native before/after details capture retains the normal builder/camera/lighting;
this cleanup has no C# or core/wire changes, and uses the already validated
08327d82 assembly. Proof/count/hash summary: `SEDAN_MK2_CLEANUP.json`.
