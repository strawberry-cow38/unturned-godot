# Source HDR, viewmodel composite and night vision

`hdr.rs` gives the original world and first-person viewmodel independent HDR
render targets. The viewmodel clears transparently into a window-sized
`Rgba16Float` image and composites over the completed world before the HUD.
Resizing preserves its image handle and viewport scale factor; an inactive
viewmodel never leaves the previous frame's arms visible. The world, UI and
console share a matching HDR intermediate, with Bevy tone mapping disabled so
neither completed 3D image is tone mapped again. The source canvas composite is
performed in sRGB, after each 3D viewport's own tone map.

Light units come from both engines' renderer implementations. In Godot's default
nonphysical mode directional, omni and spot energies are multiplied by pi before
the BRDF. Bevy multiplies physical light units by its default camera exposure;
point and spot lights additionally divide authored lumens by4pi. The shared
conversion helpers therefore use `pi/exposure` lux per directional source unit,
`4*pi*pi/exposure` lumens per point/spot source unit, and `1/exposure` for flat
ambient energy. This replaces the earlier18,000-lux visual calibration, which
overexposed daylight when fed into the source tone curve. `UG_AMB` preserves
the source ambient override. Tests recover the original diffuse energy after
Bevy's conversions. Stock Bevy still has inverse-square local-light attenuation,
whereas the source defaults to inverse-distance; their range cutoff matches,
but that distance profile and spot cone interpolation need a shader adaptation.

The source Forward+ glow chain is adapted from Godot4.6 `copy.glsl`: four
half-resolution mip levels use the original nine-tap separable Gaussian,
workgroup edge sampling, first-pass firefly compression, threshold/bloom floor,
and luminance cap12. Default mip weights are `[0,.8,.4,.1]`, reconstructed with
the original four-tap bicubic sampler. Screen blending precedes the original
ACES fit, including its1.8 exposure bias, matrix orientation and white1
normalization. Source world controls are intensity.8, bloom.1, threshold.9;
viewmodel controls are.9/.15/1.25. `UG_NOGLOW`, `UG_GLOW`, `UG_GLOWTHRESH`,
`UG_LINEAR` and `UG_EXP` retain the original diagnostic meanings.

The planar reflection camera uses the shared source world environment profile.
Its final reflection image remains LDR, as in the original SubViewport. Integral
AUG scope cameras receive the world glow profile but remain linear HDR;
detachable scopes receive neither glow nor tone mapping. Both pass their linear
lens image through the viewmodel's single ACES pass. Underwater optics precede
world glow and tone mapping; they never process the isolated hands.

`nightvision.rs` consumes the original local switch result from
`viewmodel::lights::Switches`. It does not select goggles or mutate inventory.
Military/civilian gain2.6/1.9, grain.11/.19, tint, soft knee, hot-source whitening,
vignette and Hoskins noise follow `NightVision.cs` and
`content/nightvision.gdshader`. Shader time uses the source3600s wrap and keeps
running while gameplay is paused. The source sRGB tube pass follows the
viewmodel composite and precedes HUD/console rendering. The existing lightning
lift stays above the HUD and below the console, matching source layers90/100.

Active goggles scale fog density by.2/.4 and apply the source world glow
multipliers/thresholds. The independent viewmodel glow profile is unchanged.
`OverlayDim` and `OverlayTint` equivalents are exposed for original in-world
labels/focus outlines when those renderers are ported; unrelated HUD panels are
not dimmed. `UG_NIGHTVISION=military|civilian` remains a presentation-only capture
seam. The source sun illuminates the hidden first-person body layer15 so its
shadow remains visible without putting that body into the main camera.

## Validation

CPU tests exercise separate-target ownership, transparent clear, resize/handle
stability, inactive-camera cleanup, absence of a second UI tone map, source
profile selection, reflection environment inheritance, light-unit conversion,
and military/civilian/off state transitions. Root peer review
checked Gaussian indexing, matrix orientation, exposure/white normalization and
postprocess order against the actual Godot4.6 source. The fresh corrected-unit
Vulkan build renders without shader failures; the opened
`artifacts/bevy-daylight-source-units.png` retains green grass, shaded foliage
and the coast reflection instead of the earlier broad white daylight bloom.
The combined native suite passes274 tests at this boundary. Opened and reviewed
`bevy-gun-source-lighting` and `bevy-scope-fitted-ads` show the actual magnified
scene without a second tone-map wash, with clean lens/composite edges.

Real inventory Wear/Unwear and N-key journeys produced
`bevy-worn-military-nvg`, `bevy-worn-civilian-nvg`, and
`bevy-worn-headlamp-on`, with accepted equipment/switch state in their sidecars.
The green military and neutral civilian passes preserve colored HUD elements;
removal restores the ordinary world. These images were opened and reviewed with
no shader or renderer error. Fixed-night comparisons, other scope variants and
the source local-light distance/cone adapter remain pending; daylight captures
do not establish their parity.

The Godot and upstream BakingLab MIT notices are in
`licenses/GODOT_POSTPROCESS.txt`. Source references:

- [Godot Forward+ Gaussian glow](https://github.com/godotengine/godot/blob/4.6-stable/servers/rendering/renderer_rd/shaders/effects/copy.glsl)
- [Godot tone mapping and bicubic reconstruction](https://github.com/godotengine/godot/blob/4.6-stable/servers/rendering/renderer_rd/shaders/effects/tonemap.glsl)
- [Godot environment defaults](https://github.com/godotengine/godot/blob/4.6-stable/scene/resources/environment.cpp)
- [Godot nonphysical light units](https://github.com/godotengine/godot/blob/4.6-stable/servers/rendering/renderer_rd/storage_rd/light_storage.cpp)
- [Godot local-light attenuation](https://github.com/godotengine/godot/blob/4.6-stable/servers/rendering/renderer_rd/shaders/scene_forward_lights_inc.glsl)

The viewmodel adapter now supplies the source camera-space sunlight, two
readability fills, night floors and per-view ambient override through
`viewmodel/lighting.rs`, using the same derived light units. Dynamic world-light
mirrors from `Viewmodel.SetWorldLights`, source label/focus-outline renderers,
and the broader volumetric-fog pipeline remain separate presentation work.
Fresh corrected-unit night and further scope-variant visual checks remain pending.
