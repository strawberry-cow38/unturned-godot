# Weapon and first-person presentation parity

The original managed authority owns accepted shots, damage, condition, ammunition,
reloads and installed parts. Native presentation consumes accepted facts. A native
rendered flash, casing or sound never creates a hit or spends ammunition.

## Verified checkpoints

- Original arm rig, exported held meshes, per-gun hooks and available clips;
  food/throwable part tracks and driving-wheel pose integration. See
  `VALIDATION.md` for visual evidence and boundaries.
- Canonical trigger/cadence, reload and chamber state through the original core;
  accepted shot-time condition/skill recoil; continuous melee/repair leases.
- Source blowtorch and chainsaw Start/Stop edges, held final swing pose, nozzle
  particles in the viewmodel frame, shake and original audio scheduling.
- Source attachment defaults and saved IDs, suppressor cartridge restrictions,
  per-barrel sound/volume, integral suppression and velocity, bottle break after
  the accepted first shot. See bridge tests and README for schema.

## Current gunfire presentation checkpoint

`viewmodel/weapon_fx.rs` ports the source `Viewmodel.Kick`, `EjectCasing`, casing
integration, and shoot/reload/hammer/melee audio. It reads the `guns_visual.tsv`
ejection flag, original muzzle/eject hooks and original assets. Accepted facts
retain barrel state before break/wear, including the distinction between
`barrelSilenced` (muzzle light/billboard) and `silenced` (projectile/noise rules).

Six native integration regressions exercise overlapping shot voices, retained
fact deduplication, first-shot bottle suppression, camera-facing flashes,
torso-frame casing motion, one bounce, expiry/unequip/session cleanup, pause,
reload pitch, delayed hammer timing/cancellation and continuous-melee whoosh
gating. Original Ogg shoot/reload/hammer files are decoded through Bevy's enabled
Vorbis decoder, including authored silence before their sounds begin. These are
code checks. The native `bevy-eaglefire-flash-ads.png` capture shows an accepted
30-to-29 shot with the original warm flash behind the front sight. A fresh
Godot `godot-eaglefire-flash/rig_00.png` provides the original ADS reference.
Both were opened and inspected. Casing lifetime/motion has code coverage but
was not visible in the delayed screenshot; lighting/HDR differences remain below.

The source `UG_FLASHHOLD=1` diagnostic retains an accepted flash for inspection.
Normal flash lifetime is 0.05 seconds; casings expire after 1.3 seconds and use
the original current-foot-surface bounce bank after 0.5 seconds. No audio device
is present in this environment, so playback has not been verified by listening.

## Native attachment menu and installed parts

The original hold-T menu now uses authority options, exact owned item/gun tokens,
source icon/rarity colors, projected hooks, simultaneous chord-spaced option
rings, per-magazine rounds and shell-type/unload choices. It closes on release,
selection replacement or session change. The original AttachStart clip presents
the gun; release snaps back to its ready hold. Reload animations can finish in
the normal hold while the menu stays open, as in the source; reopening presents
it again and all buttons follow the current hook positions.

Only accepted attachment state replaces native parts. The original source mesh
and texture mappings drive each slot; factory iron offsets remain distinct from
rail-mounted optic offsets. Water-bottle seating/scaling uses actual barrel
geometry. Red-dot, halo and kobra use the source reticle, depth-tested billboard,
optical axis and per-sight eye relief. Scope bodies and aim hooks are mounted.
The scope glass and active tactical effects are covered by the next checkpoint.

All206 native tests passed at integration. After review,21 focused viewmodel
checks and3 attachment UI checks passed, including same-ID physical-gun rig
replacement, stale shot suppression, exact duplicate-magazine actions, source
icon alpha bounds, ring spacing and source-roster bottle dimensions. Accepted
fit/removal events play the original single restarting selector voice; this has
code validation, not an audible test on this machine.

The actual Vulkan journey fitted barrel7 and sight146, consumed those bag items,
and showed the centered red dot. A magazine swap changed loaded30 to31 while
returning the outgoing29-round magazine, preserving the chamber. Detaching both
returned the same part IDs and restored factory sight5. Inspected paired image/
authority captures: `bevy-attachment-rings`, `bevy-attachment-magazine-options`,
`bevy-attachment-silencer-confirmed`, `bevy-attachment-dot-fitted`,
`bevy-attachment-dot-ads`, `bevy-attachment-magazine-swapped`,
`bevy-attachment-removal-confirmed`, and
`bevy-attachment-factory-irons-confirmed` under `artifacts/`. The original Godot
`godot-eaglefire-attach/rig_00.png` reference confirms the same close diagonal
AttachStart framing and hook positions. Native arm/gun lighting remains darker
and is tracked with the first-person environment work.

## Scope and light integration in progress

`viewmodel/scopes.rs` ports the original objective-mounted camera and rigid ocular
glass for the eight source optic meshes, including AUG's integral cross/donut.
The original lens, objective, eye relief, FOV, polygon mask and reticle assets
drive the result. Each scope uses a linear HDR 720-pixel target, the source High
quality default; a quality selector remains pending. Detachable scopes have no
glow; the integral AUG environment is part of the common HDR adapter. Scoped
sensitivity and breath-hold sway are described in `SCOPE_STEADY_PORT.md`.

The live first-fit test exposed a camera activation ordering crash in Bevy's
directional shadow preparation. Scope positioning now precedes cascade creation.
A regression runs Bevy's actual cascade builder and checks the new active camera
has shadow entries in its first frame. This is additional coverage beyond camera
geometry, attachment cleanup, missing-reticle fallback and input sensitivity.
The corrected live journey fitted the real owned 8x scope (accepted sight21),
then captured both hip and fully aimed views. `bevy-scope-fitted-hip` and
`bevy-scope-fitted-ads` show the actual magnified world inside the mounted lens;
their snapshots record zoom8 and aimed alpha1. Both images were opened, including
peer review of the HDR path, with no renderer panic or shader error. The original
Godot `godot-scope-8x/rig_00.png` reference was also opened. Its harness resizes an
oversized viewport, so it is a visual reference rather than a pixel-match test.
Other scope variants still need their individual live checks.

`viewmodel/lights.rs` ports N-key tactical/headlamp/NVG priority, RMB handheld
light switching, original lens emission masks and source spot-light settings.
The rail switch resets on equip or changed attachment fit. Worn switches retain
their intent across temporary removal, while active effects follow actual worn
equipment. Tactical laser rays hit world/vehicle/prop surfaces and exclude enemies;
the first-person emitter follows the live tactical attachment, and third person
uses the body weapon's muzzle. A real Rapier regression checks the surface hit,
enemy exclusion, both emitters and removal cleanup. An actual input-system test
covers reload gates, key priority, changed fit, inventory, death and session reset.
During the original inspect gesture the beam follows the attachment's model +Y
axis for six metres, including misses; its surface dot still requires a contact.
The live journey fitted151 then152 and toggled each accepted tactical state;
`bevy-tactical-laser-on` and `bevy-tactical-lamp-on` retain their authority and
switch sidecars. Wear/Unwear then exercised military334, civilian1044 and
headlamp1199 through the real inventory controls, with the expected N-key mode
and HUD-preserving overlay. These images were opened. Fixed-night and source
distance/cone attenuation checks are recorded in `HDR_NIGHTVISION_PORT.md`.
Handheld light and third-person laser visuals remain pending; these journeys
do not establish audible parity.

`viewmodel/lighting.rs` now uses the source final `MatchWorldLight` mapping: one
camera-space sun, two complementary fills, world ambient color, and the original
night readability floors. It uses the common renderer-derived energy conversion
and an independent viewmodel ambient override. A real transform schedule tests
changed sun direction, overhead sun, source floors and render-layer isolation.
The fresh `bevy-gun-source-lighting` capture was opened with the world reviewer;
source dynamic world-light mirrors remain separate work.

## Weapon inspection integration

F reaches inspection only when no world interaction claims it and the player is
outside a vehicle. Guns and melee use their own positive-length original Inspect
clip. SKS has the source's explicit Zubeknakov donor; other missing clips and
non-weapon holdables do nothing. Safety lowering allows inspection, while actual
sprint movement blocks or cancels it. Aim and the attachment menu replace the
gesture; cancelling snaps to the ready hold instead of replaying equip. The click
that cancels a gun inspection cannot also fire. Automatic fire may resume on the
next held-input frame. Accepted reload/melee actions replace the gesture, and
world-load/death cleanup prevents a stale inspection from surviving the session.

The body uses the same authored Inspect clip and the gun follows its animated
hand. The tactical beam follows the rotating model rather than converging toward
the eye ray. Six native checks exercise the real rig clips, source donor limits,
focus priority, safety/actual-sprint distinction, cancellation/ADS/menu transitions,
reload/session gates, timer expiry and model-space laser direction. Live visual
checks used the actual owned AR-15, fitted 8x optic/laser and Military Knife in
PEI. Opened captures `bevy-gun-inspect-active`, `bevy-gun-inspect-cancel`,
`bevy-gun-inspect-laser`, `bevy-gun-inspect-to-ads` and
`bevy-melee-inspect-active` show the original authored poses and transitions;
sidecars verify accepted equipment, inspection state and unchanged 30-round
ammunition on the cancelling click. The first four received peer visual review.

Third-person inspection exposed a floating beam from the invisible first-person
weapon pose. The original inspect branch unconditionally selected that pose too.
The native adapter now uses the visible body's muzzle and model +Y barrel axis
in third person. The existing real-body and Rapier laser tests guard the actual
hook direction, six-metre miss beam, hit-only dot and normal eye-ray behavior.
The initial `bevy-gun-inspect-third-person` capture records the defect. Opened
`bevy-gun-inspect-third-person-fixed` and `bevy-gun-inspect-third-person-side`
captures no longer show that floating beam. The thin beam is not clearly resolved
against the daylight scene, so its full third-person visual alignment remains
unconfirmed; the actual muzzle/beam geometry is covered by the regressions above.

## Remaining source behavior

The following are distinct tasks, not completed by the checkpoints above:

- Remaining optic variants, handheld and third-person light presentation;
  detachable-part animations and special gun furniture.
- Remote player gunfire presentation. Local accepted cosmetic tracers and rockets
  are documented in `BULLET_PORT.md`; authority collision/damage remains separate.
- Remaining source trigger gates and special actions (sprint/air delay, minigun
  spin, unload and mounted weapons); empty-trigger UI feedback.
- Dynamic world-light mirrors in the first-person viewport; remaining camera
  modes, lean and complete third-person weapon effects. Local body/clothing and
  head-look work is tracked in `AVATAR_PORT.md`, HDR in `HDR_NIGHTVISION_PORT.md`.
- Complete soundscape and audible hardware validation.

The original Godot chainsaw harness confirms the very close authored forearm
pose. Its earlier native capture predates the common HDR and source lighting
adapter, so it does not establish current chainsaw lighting parity.
