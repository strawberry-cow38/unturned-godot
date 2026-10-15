# Accepted hit presentation

`impact.rs` consumes original server hit facts without applying damage. Wire
`impact` events render source flesh or surface effects; enriched animal/zombie
melee facts supply blood where the original path has no wire impact. Explicit
`impactEmitted` avoids rendering a bullet's blood twice. Owner hit indicators
come from accepted `hit` or the corresponding enriched host hit; remote hits do
not claim local feedback. Blast damage does not invent an aimed body marker.

Blood, surface chips and water droplets use the source textures, material
cutoffs, static atlas cells, launch cones, speeds, gravity, scale and spin from
`ImpactFx.cs`, including the globally reduced particle counts. Godot's actual
managed `Mathf.RoundToInt(2.5)` returns 2; the ten-droplet source water burst thus
contains two particles at the .25 global amount scale. Source growth curves use
the shared particle adapter's measured default-tangent Hermite interpolation.

World impact events intentionally retain the original client reconstruction:
probe one metre around the accepted point to recover surface normal/material,
and use soft dirt debris if the surface cannot be recovered. Classification
uses the real prop physic-material table, exact minority-material boxes, road
kind and source dominant terrain layer names. A Dock's concrete deck and metal
railings therefore remain distinct. Hard impacts leave the original bullet-hole
texture for 18 seconds using Bevy's depth-projected forward decal; moving vehicle
holes are parented using the current physics pose in their local frame. Bevy's
quad projector has different grazing/side clipping and edge fading from Godot's
box projector. A live tree impact was accepted, but its approximate collision
shape did not produce a clearly visible hole on the bark. Flat-wall visual
verification remains required; this is not a claim of decal visual parity.

`HitmarkerHUD` keeps the original .33-second four diagonal ticks, fixed body/head
alpha, red headshot plus source ding, and expanding/fading 28-segment prop/vehicle
ring. UI roots use the explicit overlay UI camera above the weapon pass. Effects expire, retained event
replays are ignored, and a new world epoch clears old transient feedback.

The original `GameAudio` bank scan/no-consecutive-repeat choice is now shared in
`sound.rs`; wildlife and surface impacts use it. Spatial impacts reuse the shared
source ranged-audio adapter with -3dB gain, 5m unit distance and 70m cutoff.

Eight native tests currently pass: real blood sheet and debris motion, original
prop material/minority-box resolution, event/vector contracts, exact once-only
bullet versus melee feedback, owner marker filtering and transient expiry/reset.
The actual PEI `bevy-impact-blood.png` capture shows the original red blood
sprites at an accepted 20-damage cow hit approximately eight metres away, with
exactly four particles and ammo 28 to 27 in the adjacent authority snapshot. The
run also verified original tree impact debris and clean UI Quit. Full app startup
caught and fixed a Transform query conflict between decal attachment and casing
physics; `Surfaces` now reads propagated poses while the separate `CurrentPoses`
parameter supplies fresh attachment transforms. The focused tests also cover
Rapier normal recovery, original hole scale/lifetime, overlay-camera order, and
attachment before transform propagation. The .33-second marker can expire before
a delayed screenshot at software-renderer frame intervals of .25 seconds. Its
actual timing and camera layering are covered by the native integration tests. Source water-splash specification is
present; a wire world impact alone cannot classify a noncolliding ocean plane,
matching the original reconstruction limitation. The local projectile water-hit
presentation path must use the retained splash specification when that path is ported.
