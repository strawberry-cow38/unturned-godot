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

Native warhead FX and stuck-item pose rendering remain a follow-up after the
current renderer checkpoint. Authority still stops ordinary rounds at the first
contact: original thin-surface/water piercing remains a separate task, and
ordinary dropped-item physics has not been generalized by this change. In-flight
bullets remain transient across saves, as before.
