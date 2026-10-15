# Vehicle world-layer collision

The original vehicle has different geometry for driving, player/bullet contact,
and queries restricted to the world's collision layer. The native authority
uploader now preserves that distinction instead of treating every visible
vehicle triangle as a station obstruction.

## Source contract

`Vehicle.FinaliseHitboxLayers` gives up world bit 0 only when the vehicle actually
has a `HitMesh`. Ordinary body/pane/tire/turret/bifold contact meshes do not take
that bit. A vehicle without a `HitMesh` retains its chassis world layer. Native
`GROUP_4` plus the existing player/vehicle filters identifies this fallback,
using the same predicate as the source throw-clearance adapter.

`vehicle::geometry::upload` marks existing rendered contact rows
`sourceWorld:false, combat:true`. It separately tessellates the actual fallback
Rapier collider as `sourceWorld:true, combat:false`. Cuboids, convex polyhedra,
trimeshes and recursively transformed compounds retain their actual vertices;
there is no replacement model AABB. Rapier's raw shape already contains scale,
so the uploader applies only the root rotation and translation afterward.
Collider/group changes invalidate an otherwise stationary upload, including a
completed convex bake. Vehicle retirement clears both geometry classes.

The managed station query uses `sourceWorld`; ordinary combat and navigation
ignore rows with `combat:false`. This does not enlarge the handheld hit set or
replace authority with a native station decision.

## Original ship HullMesh

The source HullBands/HullBoxes branch optionally builds `HullTrimesh` from the
original body. `MeshRegion` retains a triangle only when **all three corners**
are within the inclusive region. `ObjData::split_zones` supplies that existing
operation. The original ship asset contains large triangles whose centroids are
inside while their corners are outside, so centroid selection is observably
different.

The resulting `HullMesh` is an independent fixed body with the world layer,
two-sided contact and an explicit exception for its carrier. It is not a
zero-mass collider attached to the moving vehicle: that alternative would
transmit visitor contact impulses into the carrier. The production Rapier
plugin uses `vehicle::PhysicsHooks` to filter only this carrier pair. Other
contacts keep Rapier's normal solver flags.

The ordinary physics and transform synchronization keeps the independent child
with its parent. Tests move the actual carrier through the existing
`move_physics_owner` helper, rather than a bare Transform edit which physics can
overwrite. Normal parent retirement removes the surface, and the existing
recursive origin-shift path already shifts its independent physics body.

The same actual HullMesh collider is uploaded as world-only authority geometry.
It adds no duplicate visible mesh. `UG_SHIPBOX=1` omits the source branch.

## Validation scope

The focused tests use the original ship OBJ, actual Rapier contacts, and the
production uploader with a real managed bridge. They cover exact region
selection, both sides of the surface, parent movement and retirement, a visitor
standing on the roof, and a negative control which removes the carrier exception
and reproduces self-contact. The station test places an accepted crafting bench,
then distinguishes a combat-only model from a world-layer fallback and replaces
that fallback without changing its artwork or pose.

The first checkpoint added the separately specified source HullMesh. The
follow-up below supplies the authored driving shapes; full moving-deck/cargo
behavior remains separate.

Five focused tests pass against the immutable
`crafting-station-live-v72-final` bridge. The shared native run on
`native-vehicle-source-world-final` finished with 667 passed, 35 ignored, and one
unrelated obsolete Swoop fixture which still expected the generic Inventory
root after the new native Inventory took ownership. All vehicle and authority
geometry cases passed. The UI owner is migrating that fixture; this result is
not recorded as a fully green suite. Source/code peer review passed. There is no
new visible mesh in this collision-only change.

## Authored driving hulls and parked supports

`Vehicle.Build` selects its authored branch before the ordinary car bake. Native
construction now follows that ordering: the ship's original lower `HullBands`
convex and nine yawed `HullBoxes`, or the six small trailers' three authored
boxes. The broad `BoxSize` fallback is added only if the authored branch creates
nothing. A successfully built independent HullMesh also counts as a part, as in
the source. The source `UG_SHIPBOX` switch skips this branch and then reaches the
ordinary car decomposition branch unless `UG_BOXHULL` also disables it.

Before these source methods run, Godot `Mesh.GetFaces` snaps all coordinates to
0.0001m through `TriangleMesh::create`. This is a float step with double snap
arithmetic, distinct from C# midpoint-even rounding. The native collision-only
face cache preserves the referenced zero-area faces that the rendering loader
intentionally drops. The default loader and visible mesh/AABB are unchanged.
The independent source oracle initially caught 40 reduced points versus 34 and
13 omitted region triangles; the correction follows the engine's actual
[face construction](https://github.com/godotengine/godot/blob/4.6/core/math/triangle_mesh.cpp#L127).

`ConvexBand` then uses inclusive source vertices, midpoint-even millimetre Y
planes, exact XZ deduplication and Andrew's monotone chain. The monotone-chain core is
shared with existing `MeshCut` caps; the cap's epsilon deduplication remains
separate and unchanged. No centroid selection or broad box replaces the ship's
34 reduced points. The reduction consumes the original body asset, before lamp
or door rendering splits.

All seven nonzero original `LandingGearSize` boxes are present while parked.
They remain in the compound when an ordinary trailer's asynchronous/cached car
hull replaces its temporary chassis. Ground placement includes these actual
source box supports and the authored hull boxes. This does not add coupling,
leg retraction, fifth-wheel exceptions or cargo carriage. Every current fleet
`HullDecompose` field is null; that distinct future region-decomposition branch
has not been substituted with a whole-body car bake.

The opt-in numeric oracle `bevy/tools/vehicle_hull_reference.py` extracts the
unchanged original C# `ConvexBand` and `Hull2D` methods and runs them in Godot 4.6.
It compares actual ship vertices, the complete selected region, negative
half-step/float-ULP snapping and boundary-crossing triangles. It generates
an artifact on demand, not a persisted visual-golden gate. Native contact tests
use the production original-asset builder: a moving body meets the wider ship
side, a visitor remains untouched outside the tapered bow, and restoring only
the old broad box reproduces the false contact. The parked-trailer test removes
only the landing support to reproduce the chassis drop.

Final authored-hull validation: 129 vehicle tests and 10 content tests passed on
`native-authored-hulls-final`, plus the explicit original-Godot oracle. The
vehicle group includes the earlier HullMesh parent movement, retirement,
two-sided contact, own-carrier exception and source-world authority tests
against `crafting-station-live-v72-final`. Thirteen opt-in vehicle render/oracle
tests remain ignored in the ordinary group; the source hull oracle was run
separately. Two independent source/code reviews passed. No new visible geometry or vehicle-art change is introduced.
