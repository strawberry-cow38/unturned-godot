# Authoritative impact warheads and recoverable ammunition

The original `PlayerController.StepBullets` applies direct hit damage, then the
launch-time gun's `BlastRadius` and three damage channels, then its piercing
decision, then `StickProjectile`. The Godot unified host did not carry the latter
features. The Bevy host now implements impact blasts and recovered ammunition
from actual server bullet contacts. Cosmetic flight cannot damage or spawn items.

`ServerCombat.CaptureBulletContext` captures an immutable host payload after fire
validation and before equipment wear/ammo callbacks. `BulletImpact` reports that
payload after direct damage, once per impacting pellet. Both callbacks are null
by default and add no wire fields. A delayed round retains the gun and warhead
that launched it after a weapon switch. `ServerDetonateWeaponAt` reuses existing
blast damage, armor, occlusion and host wildlife/vehicle adapters without emitting
a fabricated grenade event. Linear wildlife/vehicle falloff and the source
player falloff `damage * (1 - (distance/radius)^2)` remain shared `ExplosionMath`.

The bridge emits the retained fact `bulletExplosion` with `playerId`, `sequence`,
`pellet`, `itemId`, `gun`, `position` and `radius`. This is the native renderer's
warhead picture/audio/shake trigger. It must not infer a second blast from the
cosmetic `bulletRetired` event.

`StickAmmoFor` is extracted unchanged from source. Only an unpropelled caliber
whose gun's magazine asset has capacity at most one produces a recoverable item.
A nailgun does not mint a whole magazine per nail. The ordinary authoritative
world-item entity has amount1, quality100 and is settled at the actual impact
point plus the surface normal times0.05m. Pickup uses the existing validated
inventory transaction. It remains frozen in world space when the struck animal
or zombie moves, matching the source's explicit limitation.

Existing world-item fields and packets remain compatible. Optional bridge fields
describe only these frozen projectiles:

- `stuckNormal`: actual triangle or analytic capsule normal.
- `stuckDirection`: normalized direction from the offset item position to the
  source `LookAt` target, which is the impact point plus normalized flight velocity.
- `stuckEuler`: only for the source nearly-vertical guard (`abs(dir.y)>=.98`),
  which retains its randomized initial drop rotation. Values are radians in
  Godot's YXZ Euler convention; the original spawn-angle expression is extracted
  at build time. This is an explicit frozen pose, not loose-drop physics.

Legacy closest-approach core actor tests have no actual contact normal; their
new optional normal is null. No attachment pose is invented for that old path.
The Bevy world's actual triangles and current animal/zombie capsules provide
normals. Stuck poses persist in the optional `BevyBridge.StuckProjectiles` save
extension by world-item save-list ordinal plus item ID. Loading binds fresh
NetIds and reasserts settled state. Origin shifts translate the existing item
position, while direction/normal/Euler values remain unchanged.

Focused checks cover accepted-only impact and equipment-switch capture, direct
damage ordering, unchanged grenade events, actual rocket vehicle/player falloff
and wall shelter, crossbow save/load/rebase/pickup, animal capsule contact and
world-frozen behavior, vertical spawn pose, and the nailgun exclusion. The new
crossbow journey fails against the previous executable because no arrow exists.

Original thin-surface/water piercing is handled by the optional continuation
adapter described below. Ordinary dropped-item physics has not been generalized
by this change. In-flight bullets remain transient across saves, as before.


## Surface continuation

`ServerCombat.ContinueBullet` is null by default. Its optional response resumes
an accepted round after direct damage and the warhead callback. The bridge copies
the original `PierceCost` ordering: at most two surfaces; explosive rounds stop;
a `thin` tag wins over water; everything else stops. Thin surfaces keep75% velocity
and70% damage; water keeps45% velocity and50% damage. All damage channels retain
the cumulative multiplier. The round resumes0.06m beyond the contact, consumes a
ballistic step and skips that tick's ordinary gravity update. It can expire on a
pierce, without gaining extra range or spawning a premature recovered projectile.

The rendered ocean's trusted0.2m box metadata participates only in bullet rays.
The shared box query supplies its entry normal and ignores queries starting
inside the slab, so a pierced round does not immediately hit the back face.
Movement and explosion shelter still use ordinary world geometry. The historical
`worldCosmeticGeometry` operation and `CosmeticWater` save key remain compatible;
their trusted host metadata now serves both flight paths. No production source
prop sets `ThinMeta` (only the original wallbang harness does), so production
materials have not been guessed to be thin.

The retained `bulletPierced` fact identifies the accepted player/sequence/pellet,
position/normal, water/thin flags, cumulative surface count and current velocity/
damage retention. It is diagnostic presentation data, not a second damage source.
Core tests verify default stopping, exact damage/velocity costs, contact ordering,
step exhaustion and skipped gravity. Actual-host journeys prove a vehicle behind
a sheet now receives reduced damage where only its cosmetic tracer previously
continued; they also cover water costs/no back-face hit, two-surface cap, ordinary
solid stopping, arrow recovery only at the final contact, and rocket detonation
at the water surface. The pre-continuation executable fails the behind-sheet
vehicle test with health1000 instead of975.5.

The existing retained `impact` event now includes the corresponding accepted
contact's normal, water/thin classification, player/sequence/pellet and tick.
Original wire position/surface fields remain unchanged. The managed adapter
joins the sole core publisher's reliable ordered facts to its contact queue;
it does not deduplicate coincident shotgun hits by position or emit another FX.
The origin barrier drains that queue before translating retained positions,
while normals remain directions. Four real-host regressions cover water-to-solid
ordering, coincident pellets, actual animal capsule normals and the origin
barrier.

## Native accepted presentation

The existing impact consumer now keeps the accepted point and normalized normal.
Its short local probe only supplies the original material classification and
decal parent; older facts without a normal retain the original probe behavior.
Accepted water bypasses solid geometry entirely and uses the source lit cutout
droplets. `bulletPierced` does not emit another splash. Coincident pellets remain
separate impacts because retained event sequence, rather than position, identifies
each fact.

`bulletExplosion` shares the existing source fireball, two dust columns, fading
light and bomb audio used by frag grenades. Smoke and flare events remain distinct.
The source large water column appears at sea level when the accepted explosion
is no higher than sea+2m, with scale `clamp(radius/5,2,4)`. Effect/audio consumers
deduplicate retained sequences and reset on world session changes. Source blast
camera flinch is still a separate pending camera integration.

World-item replicas consume `stuckDirection` as a frozen negative-Z look direction
or `stuckEuler` in the source YXZ order. The same pose survives a live update,
origin shift and load with a new network ID. The existing source mesh, oriented
focus box and ordinary pickup/removal path remain in use. Unmarked dropped items
retain their prior presentation; no loose-item physics or moving-target parenting
is inferred from this frozen-ammunition metadata.

Native Bevy-world regressions cover the water-versus-solid probe conflict,
authoritative normal versus later contact, retained splash deduplication, source
ocean blast cutoff/count, actual blast light/emitters/lifetime/pause/session
cleanup, original bomb voice selection, and arrow orientation/focus/removal.
Actual PEI input/render verification used the immutable 120-test bridge candidate.
A real crossbow shot consumed its round, produced Arrow347 at the accepted wall
contact, and the native focus prompt picked it back up through the server
(world item removed; inventory gained one arrow). A real RPG-7 shot at the ocean
consumed its round and emitted one `bulletExplosion` at y25.7 with radius9. The
captured frame shows the blast light/fireball; a later frame shows the rising
column. Capture metadata records the original24 water droplets at sea y25.6 and
the original three blast emitters. No native damage is applied.

This check also exposed two independent presentation/geometry defects. The
container door exporter omitted the authored leaf AABB center (fixed separately
with an actual bridge ray regression). The original arrow pickup mesh has its
nose along +Y, while `StickProjectile.LookAt` aligns root -Z, so faithfully
applying the saved root pose leaves this mesh sideways to the flight. A separate
native adapter correction will rotate only the known arrow mesh and focus box;
the accepted root pose and saved state remain unchanged. Local artifacts:
`bevy-crossbow-arrow-pickup-focus`, `bevy-crossbow-arrow-recovered`,
`bevy-rocket-ocean-blast` and `bevy-rocket-ocean-column` (PNG plus JSON under
`bevy/artifacts/`).
