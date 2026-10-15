# Native foliage motion

The renderer reuses the shipped foliage, resource and hedge meshes. Motion is
ported from `grass_displace.gdshader`, `foliage_up.gdshader`,
`wind_sway.gdshader`, `disturbance.gdshaderinc` and `DisturbanceField.cs`.

`world/foliage.rs` owns the material adapter. Grass bends from its original
flipped UV height, flowers sway from authored local height, pebbles stay rigid,
and tree crowns/bushes/hedges use their original height-weighted wind. The
resource part selection follows `ResourceField.LoadResources`: part **0** is the
crown; trunks and cacti remain rigid. Only ground cover forces upward fragment
normals and disables shadows/specular. Canopies retain authored normals and
cast their deformed shadow. The prepass and forward vertex pipelines evaluate
the same offset, including in the water reflection view.

Merged ground cover retains original per-vertex local height in a second UV
channel. This is independent of world elevation, placement scale, rotation and
reflected winding. A hillside flower therefore bends by its own height rather
than its altitude. Source texture sampling is preserved: grass/flowers/resources
use linear mipmaps; pebbles retain nearest mipmaps. The source foliage shader
chooses texture **or** fallback tint, so textured flowers do not acquire a second
grey multiplication. Textureless pebbles use the original 0.456/0.506 greys.

The shared wind is `RainState.wind`, including the already ported WindField
integrated phase. There is no additional wind/noise sampler. The source phase
coordinates include the existing world-origin shader offset.

`foliage/disturbance.rs` gathers actual native bodies/replicas after transform
propagation. It retains the nearest 32 sources within 160 metres: the smoothed
local player point 0.5 metres above the feet, vehicle hulls, dropped items,
remote players, rotor wash and the original fading wake crumbs. The original
radius/strength/flatten/vertical-reach constants, 0.9-metre breadcrumb spacing,
1.6-second lifetime, spool-squared wash intensity and annular wash response are
unchanged. The rotor source uses the actual transformed hub, with the original
2.2-metre downward placement; its wake intentionally uses the unshifted live
hub, matching the source caller. Each shader sums offsets from the rest vertex,
then applies one displacement. Branch flattening uses 0.18; grass uses 1.0.

Session replacement clears cached wakes and smoothing. An origin rebase shifts
packed points, breadcrumbs, previous-source positions and the smooth point once.
Singleplayer pause freezes the displayed field; elapsed real time ages old
crumbs when play resumes, as in the source's wall-clock timestamps. Remote
clients consume replicated body positions without publishing shared gameplay
state.

## Validation

Six focused tests cover source material choices, original local-height retention
under reflected/elevated batches, press versus wash constants, wake spacing and
expiry, nearest-source selection, origin translation, and actual replica
source removal/session reset. An ignored render fixture loads the original
grass/flower/bush OBJ/PNG content and uses the production material pipeline:

```sh
UG_FOLIAGE_MODE=0 UG_FOLIAGE_SHOT=/tmp/foliage-wind.png \
  cargo test --manifest-path bevy/Cargo.toml render_original_foliage -- --ignored --nocapture
```

Run with a real X11 display/Vulkan renderer (the repository's xvfb/lavapipe
workflow); modes 1 and 2 supply controlled player-press and rotor-wash fields.
These are controlled cosmetic inputs, not a claim that a live helicopter was
flown. All three Vulkan/lavapipe renders completed and were opened:
`artifacts/bevy-foliage-wind.png`, `bevy-foliage-press.png`, and
`bevy-foliage-wash.png`. The press opens a localized matted patch; full wash bends
the bushes outward and flattens grass while the flowers retain wind-only motion.
Forward, shadow and depth shader pipelines reported no validation errors. An
independent source/code and three-image review found no blocker in this scope.
The full real PEI capture `artifacts/bevy-foliage-pei.png` and authority JSON also
completed and were opened: frame150, connected/alive player, eight vehicles,
original coast/grass/tree/props and ocean, with no shader errors.

## Remaining work

Per-instance accepted-hit shake, felled-tree gust/impact shudder and original
stump/debris lifetimes are implemented in the [resource adapter](RESOURCES_PORT.md).
Its remaining remote-event and collision-publication boundaries are recorded there.
The native settings UI does
not yet expose the source GrassDisplacement graphics toggle. The material field
has an enable switch for that eventual adapter. Source tree impostor/LOD rings
and non-foliage distance fades remain separate streaming work. Current cameras do
not enable TAA/motion-vector prepasses; that optional prepass still uses rest
previous positions and needs a previous-frame field before TAA can be enabled.
No grass/flower
collision or damage authority is added by these shaders.
