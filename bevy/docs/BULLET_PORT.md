# Cosmetic bullets in Bevy

Accepted combat commands create the original local cosmetic flight in
`bridge/CosmeticBulletServices.cs`. The bridge extracts the source cone/pattern
helpers and reuses `BallisticsMath`; the native renderer reads `state.bullets`
without integrating a second flight or producing damage, impacts or hitmarkers.
Actual local aim/origin and projected muzzle context are retained before wire
packing, then joined to the accepted command sequence. Rejected shots create no
visual projectile. Pending and live coordinates cross the origin barrier.

The original source scatter is now sampled once after acceptance and supplied
to both visual flight and `ServerCombat.ResolveBulletDirections`. That core
adapter is null by default: existing hosts retain their center-aim behavior.
The Bevy host restores the original direct-path pellet behavior through this
explicit adapter; the source unified `NetFire` path previously sent center aim
and its server did not scatter. Server profile data still owns pellet count,
muzzle speed, range and damage. Accepted packed commands own authority origins,
while local visual origins retain pre-wire precision. The optional callback
cannot add pellets or scale muzzle speed; invalid/missing samples use the
accepted command axis.

Burst count is computed from accepted host shots with the original0.35s reset.
The early cosmetic `visual.patternShot` hint no longer changes it. Source ADS
alpha remains clamped owner input, and shot-time skills/condition scale the
sample. No JSON input carries sampled directions or hit claims. The extracted
source currently replaces its cone sample when the per-axis pattern branch is
active, despite the stale additive comment; that executed behavior is retained.

`src/bullets.rs` ports `PlayerController.UpdateTracer`: two crossed strips,
16 sections, 10m maximum length, head fraction0.55, caliber width/color, original
round nose/pointed tail, suppression and the 0.1m visibility threshold. The tail
starts at the original viewmodel muzzle projected through the world camera's
0.6m depth plane; after 10m it trails the head. Geometry is relative to the head,
so origin shifts do not leave stale world vertices. Source rocket geometry uses
the original olive material, minus90-degree mesh correction, velocity-aligned
holder and spatial flight loop. Authoritative retirement removes every visual
and voice. Pause/master-volume changes affect the existing voices.

Tracer colors are source colors, including values above1. Godot declares the
StandardMaterial albedo as `source_color` and converts it to linear shader data;
Bevy therefore uses `Color::srgb`, not `Color::linear_rgb`. See the original
[material generator](https://github.com/godotengine/godot/blob/4.6/scene/resources/material.cpp)
and [variant conversion](https://github.com/godotengine/godot/blob/4.6/servers/rendering/storage/variant_converters.h).
This preserves the input color convention; it does not establish equal global
HDR/glow/exposure between engines.

The actual rendered `OceanBox` is registered through `worldCosmeticGeometry`.
Its source0.2m slab feeds both cosmetic flight and the trusted host bullet ray.
It never becomes a movement collider or explosion-shelter surface. Authority and
cosmetics apply the same source continuation costs; cosmetics never apply damage.
The operation retains its historical name and save key for compatibility.

## Verification

Nine native tests cover strip topology and shape, live muzzle pinning/10m cap,
suppression, source-color conversion, rocket orientation, camera projection,
optional fire context and accepted snapshot lifecycle. Eight actual-host tests
cover sequence acceptance, shared ballistics, pre-wire precision, suppression,
rocket identity, thin/water piercing, expiry/save and atomic origin shifts.
The current complete native suite passes242 tests, including the concurrent
scope, weather and avatar compile boundary.

The later accepted-scatter checkpoint passes all117 bridge integration tests
and645 network tests. Three core cases prove the adapter is accepted-only,
default-compatible, normalized to profile speed/count and robust to missing or
nonfinite samples. Three actual-host journeys prove every shotgun impact follows
its published sample, an off-axis pellet destroys a target the former center ray
missed, and rejected requests/legacy visual hints cannot advance/reset the burst
counter. The pre-adapter executable fails the off-axis damage regression. The
water-tangent regression uses the actual ADS crossbow's below-threshold spread
so its tangent remains exact after shared scatter was enabled.

A real PEI input journey gave/equipped the rifle and RPG through console and
inventory UI, then fired using the mouse. Inspected captures:

- `bevy/artifacts/bevy-tracer-first.png` and JSON: accepted rifle shot, ammo30→29,
  940m/s, first flight step18.8m. The original narrow tracer projects nearly
  end-on in this view.
- `bevy/artifacts/bevy-rocket-flight.png` and JSON: original rocket mesh and
  muzzle-anchored strip are visible against the sky, ammo1→0, 500m/s and10m first
  step. Parent review independently confirmed the rendered flight/tail.

Those live captures predate the source-color conversion correction; the
correction is covered by the material conversion regression, so exact final
brightness has not yet been visually compared. Spatial sound was constructed
without audio hardware; listening remains unverified.

`--fixed-fps N` is an opt-in render diagnostic equivalent in purpose to source
movie mode. It advances render time by1/N per frame and records `fixedFps` in
capture JSON. It leaves default gameplay timing, ballistic speeds and flight
lifetimes unchanged. This is useful on lavapipe, where a real frame may take a
second and a rifle flight can otherwise finish between rendered frames.

## Remaining source boundaries

Per-bone corpse geometry is not yet exported to the managed cosmetic collision
world. Original direct-path rocket splash, recoverable ammunition and piercing
now have authoritative host adapters; see [WARHEAD_PORT](WARHEAD_PORT.md). Native
blast presentation and frozen projectile poses still need their accepted-fact
consumers. Remote multiplayer cosmetic launches need equivalent local visual
context when a real external client is integrated. The optional authoritative
scatter adapter now closes the earlier center-ray/pellet mismatch for this host;
external clients still need their accepted ADS input integrated into that seam.
