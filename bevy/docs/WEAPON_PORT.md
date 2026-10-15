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

## Remaining source behavior

The following are distinct tasks, not completed by the checkpoints above:

- Native attachment fitting/removal UI and native installed-part replacement,
  all hook rings and attach-view pose/orbit, shell-type and magazine choices.
- All scope/optic sight pictures and PiP cameras, scoped sway, tactical laser and
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
