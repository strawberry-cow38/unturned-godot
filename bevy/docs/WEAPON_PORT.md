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
optical axis and per-sight eye relief. Scope bodies and aim hooks are mounted,
but PiP glass remains the next task. Tactical meshes/textures are present;
active lens/laser/flashlight effects remain separate.

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

## Remaining source behavior

The following are distinct tasks, not completed by the checkpoints above:

- Scope PiP cameras/glass, scoped sway, tactical laser and
  flashlight presentation, detachable-part animations and special gun furniture.
- Original cosmetic flight tracers, visible rocket rounds and remote player
  gunfire presentation. Authority projectile collision/damage is already separate.
- Remaining source trigger gates and special actions (sprint/air delay, minigun
  spin, inspect/unload and mounted weapons); empty-trigger UI feedback.
- Original first-person ambient/key/fill/world-light mapping, HDR glow and
  complete camera modes, lean/free-look and third-person clothing/body/weapon pose.
- Complete soundscape and audible hardware validation.

The original Godot chainsaw harness confirms the very close authored forearm
pose. It does not establish lighting parity: the current native viewmodel is
darker in the inspected captures, and its viewport lacks the original HDR glow.
