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

This work does not implement the remaining ship HullBands/HullDecompose driving
shapes or certify full moving-deck/rider behavior. It preserves the actual native
fallback currently in use and adds the separately specified source HullMesh.

Five focused tests pass against the immutable
`crafting-station-live-v72-final` bridge. The shared native run on
`native-vehicle-source-world-final` finished with 667 passed, 35 ignored, and one
unrelated obsolete Swoop fixture which still expected the generic Inventory
root after the new native Inventory took ownership. All vehicle and authority
geometry cases passed. The UI owner is migrating that fixture; this result is
not recorded as a fully green suite. Source/code peer review passed. There is no
new visible mesh in this collision-only change.
