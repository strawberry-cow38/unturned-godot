# Local player appearance and cameras

The native player renderer consumes the accepted local player and inventory snapshot. It does not create another collision body or change inventory, health, clothing quality, or weapon ownership.

## Implemented from the current Godot source

- `PlayerController.UpdateBody`: the complete original `rig.json`, original idle/movement/swim/crouch/prone clips, source speed thresholds, spine/head look and stance counter-rotation. First person puts the complete body on source own-body layer 15; third person uses world layer 0. The original first-person body offset is 0.42 m.
- `CharacterModel`: fixed source skin, actual replicated profile face, shirt/pants atlas composition, source condition tatter, and modular hat/glasses/mask/vest/backpack meshes. Changes to condition update material uniforms without rebuilding gear. New viewmodel arm rigs receive the currently accepted shirt and pants.
- The actual viewmodel resolved gun/melee meshes and accepted attachment children are mirrored onto the full body's original hand hooks. Equip/reload/hammer/attack upper-body clips preserve locomotion legs; source crouch/prone shoulder counter-rotation and aim additive are retained. The original gun muzzle hook is exposed as `AvatarMuzzle` after transform propagation.
- Original first-person Alt head turn and third-person shoulder/orbit controls, source 0.39 m sphere stepped/bisected camera collision, 2 m chase length, and eye-plane convergence for third-person shots. Authority fire origin remains the physical player eye. The independent viewmodel camera turns with the head while its held frame stays on body heading.
- Original vehicle mesh-bounds chase distance and scroll zoom, world/prop camera collision, car orbit, roll-free pitched plane chase and airframe-locked helicopter chase. Entry resets chase and aircraft look offsets.
- Accepted owner death hides the live body, creates the source bare-skinned full-body ragdoll with the original 11 physical bones and cosmetic launch impulse, and runs the source death orbit. Accepted respawn removes it. The original source creates its death body without copying clothes; this behavior is preserved.
- Source `kill` and `sethp` console commands now enter the authoritative server damage queue, with the existing cheats gate. They do not write the client health field. Non-finite inputs are rejected.

## Verification

Focused native tests exercise an actual Bevy update schedule, original rig and clothing assets, accepted outfit changes, arm-rig replacement, reused material condition updates, first/third-person render layers, mirrored attachment replacement, locomotion legs under the gun overlay, the actual Rapier camera query at a thin wall, aircraft chase basis, and the original physical death/respawn lifecycle. A viewmodel transform regression checks a ninety-degree Alt look against actual propagated camera and frame basis vectors.

The managed host regression covers queued accepted health damage, inability to heal through `sethp`, non-finite rejection, single death notification, automatic authoritative respawn, and cheats-disabled rejection. Render evidence is recorded below after inspection.

## Remaining source behavior

This slice does not claim the entire player presentation is complete. Remaining source features include gear-material tatter, body gestures, remote-player bodies, independent corpse options/music, turret weapon presentation, and third-person weapon flash/eject presentation. Lean/peek, body lean and eye-height smoothing are tracked in `LEAN_PORT.md`; their physical adapter is tested, while the companion viewmodel compositor and render verification remain in progress. Coupled-trailer chase bounds await native coupling. The directional sun now includes source own-body layer15. Native clothing Wear/Unwear controls and original dropped-item manifest visuals were added during this checkpoint after the live journey exposed those missing routes; accepted state still owns the outfit. First-person passenger gun actions now use the tested source seat gate; driver and third-person vehicle gun use remain blocked.

## Render evidence, 2026-10-14

Actual xvfb/lavapipe input journeys produced `bevy-avatar-third-person.png`, `bevy-avatar-third-person-front.png`, and `bevy-avatar-third-person-crouch.png`, each with the accepted held rifle and owner stance in its JSON sidecar. The full body, original face, upper-body hold, and crouch change are visible. `bevy-avatar-authoritative-death.png` records the real `death` event and shows the physical corpse plus death UI. These images were opened and inspected. The concurrent first HDR adapter produced excessive daylight glow; these older images support pose/lifecycle checks, not final shading comparison.

A fresh native build with corrected source light units produced `bevy-clothing-wear-action.png` (actual Gold Tuxedo Top24 Wear button), `bevy-clothing-shirt-worn.png` (accepted shirt24 and changed first-person sleeve/cuff), and `bevy-worn-shirt-third-person.png` (the same shirt on the live body, accepted Military Nightvision334 with its visible head strap). Their JSON sidecars prove the real bag-to-worn transition: shirt0→24, then glasses0→334. All three images were independently opened and inspected; the refreshed body and shoreline no longer exhibit the earlier broad white glow. The first-person and third-person clothing routes are visually verified. Additional Unwear and worn-light switch captures are recorded by the interface/light checkpoint; the old clothing-attempt images are not evidence of successful wear.
