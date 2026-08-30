// Worldspace rain streaks (RainSystem3D). Replicates the old StandardMaterial (unshaded, alpha, lens distance-fade)
// and ADDS a canopy rain shadow: drops inside `rain_canopy` (a tree's overhead cover) fade out, so the leaves occlude
// the rain BELOW them while it still falls everywhere else (master 2026-08-30 "leaves should occlude rain below;
// rain outside the occluded area still exists"). Rain intensity is baked into `alpha_base` by the CPU each frame.
shader_type spatial;
render_mode unshaded, blend_mix, cull_disabled, shadows_disabled;

uniform vec3 tint : source_color = vec3(0.80, 0.86, 0.96);
uniform float alpha_base = 0.14;              // CPU writes 0.14 * rain intensity (fades the whole field with the weather)
global uniform vec4 rain_canopy;              // xy = canopy centre XZ, z = radius (m), w = occlusion strength (0 = no canopy)

varying float amul;

void vertex() {
    vec3 wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;   // per-particle world position (particles are world-space, LocalCoords off)
    float shadow = 0.0;
    if (rain_canopy.w > 0.001)
        shadow = (1.0 - smoothstep(rain_canopy.z * 0.72, rain_canopy.z, distance(wpos.xz, rain_canopy.xy))) * rain_canopy.w;
    // canopy hole + the lens distance-fade (so near drops don't read as fat bars -- matches the old PixelAlpha fade)
    amul = (1.0 - shadow) * smoothstep(1.5, 3.2, distance(wpos, CAMERA_POSITION_WORLD));
}

void fragment() {
    ALBEDO = tint;
    ALPHA = alpha_base * amul;
}
