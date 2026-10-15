# Cosmetic bullets in Bevy

Accepted combat commands create the original local cosmetic flight in
`bridge/CosmeticBulletServices.cs`. The bridge extracts the source cone/pattern
helpers and reuses `BallisticsMath`; the native renderer reads `state.bullets`
without integrating a second flight or producing damage, impacts or hitmarkers.
Actual local aim/origin and projected muzzle context are retained before wire
packing, then joined to the accepted command sequence. Rejected shots create no
visual projectile. Pending and live coordinates cross the origin barrier.

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
Its source0.2m slab affects visual piercing only and never becomes a solid
physics collider or authoritative combat obstruction. Cosmetic water/thin
piercing remains separate from authoritative server hit truth.

## Verification

Nine native tests cover strip topology and shape, live muzzle pinning/10m cap,
suppression, source-color conversion, rocket orientation, camera projection,
optional fire context and accepted snapshot lifecycle. Eight actual-host tests
cover sequence acceptance, shared ballistics, pre-wire precision, suppression,
rocket identity, thin/water piercing, expiry/save and atomic origin shifts.
The current complete native suite passes242 tests, including the concurrent
scope, weather and avatar compile boundary.

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
world. Authoritative rocket splash and recoverable arrow behavior remain limited
by the existing server combat implementation. The original unified listen-host
also exits its `Bullet.Cosmetic` branch before the legacy direct path calls
`Explode`/`StickProjectile`, so these are direct-path-only source behaviors, not
an authority implementation this renderer omitted. This renderer does not create
a second damage path to conceal those gaps. Remote multiplayer cosmetic launches
need equivalent local visual context when a real external client is integrated.
