// Worldspace rain streaks (RainSystem3D). Replicates the old StandardMaterial (unshaded, alpha, lens distance-fade)
// and ADDS a canopy rain shadow: drops inside `rain_canopy` (a tree's overhead cover) fade out, so the leaves occlude
// the rain BELOW them while it still falls everywhere else (master 2026-08-30 "leaves should occlude rain below;
// rain outside the occluded area still exists"). Rain intensity is baked into `alpha_base` by the CPU each frame.
shader_type spatial;
render_mode unshaded, blend_mix, cull_disabled, shadows_disabled;

uniform vec3 tint : source_color = vec3(0.80, 0.86, 0.96);
uniform float alpha_base = 0.14;              // CPU writes 0.14 * rain intensity (fades the whole field with the weather)
global uniform vec4 rain_canopy;              // xy = canopy centre XZ, z = radius (m), w = occlusion strength (0 = no canopy)
global uniform sampler2D rain_roof;           // RainRoofMap: world Y of the topmost solid per cell (Rf, 64x64 over 64 m)
global uniform vec4 rain_roof_rect;           // centre X, centre Z, half-size (z = 0 -> no map)

// the height of whatever is topmost at this XZ (a roof, a canopy, a car, the ground); -1e9 outside the map
float roof_y(vec2 xz) {
    if (rain_roof_rect.z <= 0.0) return -1e9;
    vec2 uv = (xz - rain_roof_rect.xy) / (2.0 * rain_roof_rect.z) + 0.5;
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) return -1e9;
    return texture(rain_roof, uv).r;   // world Y of the topmost solid at this XZ (-1e6 = nothing)
}

varying float amul;

void vertex() {
    vec3 wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;   // per-particle world position (particles are world-space, LocalCoords off)
    float shadow = 0.0;
    if (rain_canopy.w > 0.001)
        shadow = (1.0 - smoothstep(rain_canopy.z * 0.72, rain_canopy.z, distance(wpos.xz, rain_canopy.xy))) * rain_canopy.w;
    // canopy hole + the lens distance-fade (so near drops don't read as fat bars -- matches the old PixelAlpha fade)
    amul = (1.0 - shadow) * smoothstep(1.5, 3.2, distance(wpos, CAMERA_POSITION_WORLD));
    // ROOF: a drop below the topmost surface at its XZ has fallen through something -- it is gone (master 2026-09-04)
    if (wpos.y < roof_y(wpos.xz) - 0.15) amul = 0.0;
}

void fragment() {
    ALBEDO = tint;
    // soft edges: a streak is a translucent thread, not a hard bar -- fade across its width and at both ends (master 2026-09-04
    // "there isnt transparency on the raindrop particles")
    float edge = smoothstep(0.0, 0.45, UV.x) * smoothstep(1.0, 0.55, UV.x) * smoothstep(0.0, 0.18, UV.y) * smoothstep(1.0, 0.82, UV.y);
    ALPHA = alpha_base * amul * edge;
}
