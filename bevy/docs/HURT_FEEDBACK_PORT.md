# Accepted owner hurt feedback

`ui/hurt.rs` ports `PlayerController.ShowHurtCosmetics`, its pain decay and
`HurtDirectionIndicator.cs`. It consumes accepted owner `hurt` events. A lower
health snapshot alone creates neither a flash nor an invented attacker.

Damage greater than5 replaces pain alpha with `clamp(damage/40,0,1)*.75`;
pain decays by1 per second. A weaker later hit can replace a stronger flash.
Any positive attributed hit adds its own marker; damage at least25 is critical.
The victim-to-source vector is flattened at the accepted hit and stored as a
direction, so floating-origin translation cannot move it. The final player
camera's flattened right/forward axes determine the on-screen bearing, including
free look. A purely vertical source or degenerate vertical view invents no
horizontal bearing.

Marks retain full opacity for1.5s, then fade linearly until3s. They use the
original ten-segment22-degree half-angle polygon between radii55.8 and90px,
with the source red/orange colors. Marks render above the pain/low-health pass
and below the ordinary HUD/crosshair; they do not intercept input. Materials
are retired with their marks. Accepted event sequence guards prevent replay;
session replacement/disconnect clears feedback. A killing blow still displays
under the death screen. Source respawn clears pain while existing markers finish
their independent lifetime. Singleplayer pause freezes feedback; network pause
continues it.

Pain is composited by the shared [low-health pass](LOW_HEALTH_PORT.md) before
desaturation and vignette, preserving the source HUD canvas order.

Five focused native tests pass, covering thresholds, replacement, fade, camera
bearing/pitch, duplicate events, snapshots, death/revival, session reset and pause.
The integration case launches two real UDP bridge processes: a normal rifle
head hit spends one round, lowers owner health by40, produces the critical mark
back toward the shooter and cannot replay. This journey exposed and guarded
the zero-damage adapter bug fixed in `ce500597`; use that bridge or newer.

The five-stage Vulkan fixture passed and every PNG was opened:
`bevy-hurt-bearing-{0..4}.png`. It shows a clean baseline, simultaneous regular
front/critical right marks, camera-turn reprojection, half-opacity fade and exact
restoration of the same-run baseline. Pixel assertions check the expected
directions and an untouched center; no stored golden is used. Independent
source/code review passed.

The combined production application also passed an accepted local-server journey
with the RainGlass material installed. The opened captures show healthy play,
an accepted85-damage flash, settled15HP, Inventory over the affected scene,
an equipped rifle/arms under the effect, death, and accepted100HP respawn with
healthy colors restored. Artifacts are `bevy-feedback-{healthy,accepted-hurt,
low-health,low-health-inventory,low-health-equipped,death,respawn}.{png,json}`.
The respawn capture is frame1655/tick1666; death is frame481/tick492. These
checks use actual replicated health and owner events, not injected UI state.

```sh
env -u WAYLAND_DISPLAY WINIT_UNIX_BACKEND=x11 WGPU_BACKEND=vulkan \
 VK_ICD_FILENAMES=/usr/share/vulkan/icd.d/lvp_icd.json \
 UG_HURT_SHOT=/tmp/bevy-hurt-bearing \
 xvfb-run -a cargo test --manifest-path bevy/Cargo.toml \
 ui::hurt::tests::visual::gpu_direction_marks_follow_the_camera_fade_and_restore_the_scene \
 -- --ignored --exact --nocapture
```
