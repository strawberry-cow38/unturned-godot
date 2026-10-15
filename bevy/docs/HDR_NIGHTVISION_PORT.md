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
Bevy's conversions. `hdr/light_units.rs` adapts Bevy's canonical shared PBR
lighting shader in place to the original default inverse-distance attenuation
and outer-cone rim profile. The common quartic range cutoff, material BRDF,
shadow sampling and texture-light behavior remain in the upstream module.
The adapter runs before shader asset publication, retains its canonical import
identity, and rejects changed upstream equations rather than partially patching
them. All currently materialized native local lights use the source default
exponents. Nondefault siren/headlight/streetlight exponents need per-light GPU
metadata when those renderers are integrated.

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
no shader or renderer error. Fixed-midnight captures add `bevy-night-source-off`, `bevy-night-headlamp-on`,
`bevy-night-worn-military`, `bevy-night-worn-civilian`, and
`bevy-night-scope-crosshair` PNG/JSON pairs. The goggles were worn through actual
inventory transactions and toggled with N; sidecars confirm IDs334/1044 and
active switch state. Opened images retain coastal silhouettes while the military
tube reveals more cloud/foliage detail and the civilian tube remains weaker and
grainier. The 8x image confirms aimAlpha1 and the corrected absence of the white
first-person HUD crosshair. The baseline lamp uses stock inverse-square falloff. The opened
`bevy-night-headlamp-source-falloff` image uses the same saved pose (under0.2mm
position difference, identical yaw/pitch and only the headlamp enabled) with the
source distance/cone adapter: its readable grass/beach light pool extends beyond
the baseline's weak central patch. The source-light Vulkan run has no shader
validation failures; all six focused HDR tests pass. Root peer review confirmed
both night-vision modes, the scope/crosshair image and the broader lamp pool.
The earlier immediate lamp-off screenshot retained illumination despite its
switch sidecar being false. A regression using Bevy's actual VisibilityPlugin
and LightPlugin reproduced an ordering defect: newly enabled lights had stale
InheritedVisibility in their first render frame. Player lamp presentation now
runs before visibility propagation and light bounding-sphere updates. The same
regression verifies creation, repeated on/off transitions and camera movement
through both InheritedVisibility and the ViewVisibility consumed by PBR.
Thirteen focused lighting tests pass. Opened fresh `bevy-headlamp-visible-now`,
`bevy-headlamp-hidden-now` and delayed `bevy-headlamp-hidden-settled` images show
the main pool dimming when switched off, but retain a faint ground pool relative
to the earlier clean night baseline. This residual is a separate unresolved
render issue; the switch sidecar and propagation regression do not prove that
all GPU light contribution has disappeared.
Other scope variants remain unverified.

The Godot and upstream BakingLab MIT notices are in
`licenses/GODOT_POSTPROCESS.txt`. Source references:

- [Godot Forward+ Gaussian glow](https://github.com/godotengine/godot/blob/4.6-stable/servers/rendering/renderer_rd/shaders/effects/copy.glsl)
- [Godot tone mapping and bicubic reconstruction](https://github.com/godotengine/godot/blob/4.6-stable/servers/rendering/renderer_rd/shaders/effects/tonemap.glsl)
- [Godot environment defaults](https://github.com/godotengine/godot/blob/4.6-stable/scene/resources/environment.cpp)
- [Godot nonphysical light units](https://github.com/godotengine/godot/blob/4.6-stable/servers/rendering/renderer_rd/storage_rd/light_storage.cpp)
- [Godot local-light attenuation](https://github.com/godotengine/godot/blob/4.6-stable/servers/rendering/renderer_rd/shaders/scene_forward_lights_inc.glsl)

The viewmodel adapter now supplies the source camera-space sunlight, two
readability fills, night floors and per-view ambient override through
`viewmodel/lighting.rs`, using the same derived light units. Its `lighting/world.rs`
adapter also ports `PlayerController.ScanWorldLights` and
`Viewmodel.SetWorldLights`: every fifth simulation tick it selects the nearest
four tagged sources within twice their range and pools shadowless layer1 omni
mirrors under the viewport camera. It walks hidden ancestors explicitly, because
Bevy's explicitly-visible child otherwise overrides a hidden parent. The shared
`NearbyLight` resource averages every eligible source in the original sRGB color
space and caps energy at4, even while unarmed or without a viewport camera.
Camera replacement rebuilds the pool; origin changes preserve camera-local
placement. Vehicle fire carries the exact original `dynlight` tag, and
`UG_TESTLIGHT=1` creates the original WorldBuilder diagnostic red lamp. Actual
source membership is narrower than its comments: current ThrowableFx flare,
blast, player eye lamp and muzzle constructors do not join `dynlight`, so the
adapter does not invent membership for them. Future native grid fixtures,
deployable lamps and vehicle headlights/sirens/afterburners must carry the tag
when those light renderers are ported. Inventory-stage consumption, source
label/focus-outline renderers and the broader volumetric-fog pipeline remain
separate presentation work. Five focused lighting tests cover the source scan,
aggregation, hidden ancestry, camera/origin mapping and replacement lifecycle;
the opened midnight `bevy-world-light-mirror` PNG/JSON shows the original
UG_TESTLIGHT red source reaching the separate rifle viewport while all player
lamps and goggles are off. The fixture also initializes visibility on its native
physics-only parent, avoiding an incomplete visibility hierarchy. Further scope variants remain pending;
the fixed-night cases above are verified.
