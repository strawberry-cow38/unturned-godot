# Original fallback door

`--demo-furniture` now includes the original `Door.cs` fixture beside the
original diagnostic bed. Ordinary retail maps keep both fixtures disabled.
The source `WorldBuilder.InteractableAnchor` seeded selection supplies the
anchor; the door uses its original offset `(-3, 0, +2)`, yaw zero, independent
door ID 1 and owner 1. Matching remote peers opt into the same flag and consume
the accepted door replica; only the host registers authority and collision
geometry. No client mints an ID or applies damage.

The native leaf uses the original 1 × 2 × 0.12 metre box, brown material,
focus emission and world collision layer. Its body, collider and exported
authority triangles follow the same fixed left hinge through the original
90-degree, 0.45-second linear swing. Local presentation follows accepted open
and locked state. Geometry is republished after origin or world revisions;
disconnect and accepted removal retire the visible body and collider.

Actual interaction follows `PlayerController`'s source distinction: release F
before one second to toggle, or hold it for one second to request locking or
unlocking. Progress appears after 0.2 seconds. Looking away, opening a modal or
changing sessions cancels a pending hold. Generic press dispatch cannot also
toggle a manually handled door. The original local keyring cue plays for a
queued lock request with source volume, distance and pitch parameters. The
host emits the original 8-metre zombie noise once per accepted open-state
transition at the fixed hinge.

`ServerInteractables` retains original ownership, reach and cooldown checks.
The unified source path explicitly passes `arcBlocked: false`; its closing-arc
limitation is preserved. The direct Godot test-only path has a separate arc
probe, which is not substituted into the shared server path.

The accepted combat adapter uses the same nearest-world-obstruction callback
as the bed. Original bullet/melee `VehicleDamage` consumes the door's 250 HP;
retirement removes the source door registry entry and its dynamic triangles.
Repeated registration cannot heal or revive it, and late geometry uploads for
the retired key remain filtered. Source explosions do not call this seam.
The existing save format preserves open/locked state but has no door-health
record, so health persistence is an explicit inherited limitation. Hinge
metadata is stored as an offset from the source position, preserving noise
coordinates across rebase, save and load.

Managed regressions exercise accepted cooldown/lock/noise, translated and
saved hinge coordinates, nearest obstruction, exact health and stale uploads,
plus a real UDP peer's rejected foreign lock request and eight accepted
Eaglefire shots removing the peer's door replica. Native regressions cover
fixed-hinge triangles, accepted animation and collider removal, manual
tap/hold routing, default-off and remote registration gates, cancellation,
origin shifts and disconnect cleanup.

All seven focused native regressions pass. The immutable v2 adapter passes
the three door tests and all three existing bed-damage tests, including their
real UDP journeys.

The production `--play --demo-furniture` PEI run used actual F, inventory,
save/load and mouse input. Opened `bevy-demo-door-closed.png`,
`bevy-demo-door-locked.png`, and `bevy-demo-door-open.png` show the source leaf,
accepted lock prompt and 90-degree swing. A saved closed state was restored
before firing. `bevy-demo-door-seven-hits.png` retains the door and wood mark
with ammo23; `bevy-demo-door-eighth-shot.png` shows clear ground, no door prompt
and ammo22. Their JSON sidecars confirm replica retirement. These shots use
the fixed diagnostic camera and third-person aim convergence; the close
diagnostic view also includes part of the local body at the lower edge.
The renderer exited cleanly through Quit without shader errors. Independent
source and visual reviews passed this door scope; the diagnostic body framing
does not claim whole-scene first-person visual parity.
