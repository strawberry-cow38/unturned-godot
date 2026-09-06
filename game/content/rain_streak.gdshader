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

// SHELTER, SMOOTHED (master 2026-09-06 "the rainmap seems really jagged on buildings placed on non-cardinals").
// The map stores a HEIGHT per 0.5 m cell and one nearest read gives a hard yes/no, so a roof that is not axis-aligned
// came out as a staircase along its edge. Blending the HEIGHTS is the trap the nearest read was chosen to avoid (it
// invents mid-air roofs at every edge), so the four surrounding cells are each tested SEPARATELY and it is their 0/1
// answers that get bilinearly blended. The edge then runs diagonally, over half a metre, instead of stepping.
float roof_shelter(vec2 xz, float y, float margin) {
    if (rain_roof_rect.z <= 0.0) return 0.0;
    vec2 uv = (xz - rain_roof_rect.xy) / (2.0 * rain_roof_rect.z) + 0.5;
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) return 0.0;
    ivec2 sz = textureSize(rain_roof, 0);
    vec2 t = uv * vec2(sz) - 0.5;
    ivec2 b = ivec2(floor(t));
    vec2 f = fract(t);
    float acc = 0.0;
    for (int j = 0; j < 2; j++) {
        for (int i = 0; i < 2; i++) {
            ivec2 c = clamp(b + ivec2(i, j), ivec2(0), sz - ivec2(1));
            float under = (y < texelFetch(rain_roof, c, 0).r - margin) ? 1.0 : 0.0;
            acc += under * mix(1.0 - f.x, f.x, float(i)) * mix(1.0 - f.y, f.y, float(j));
        }
    }
    return acc;   // 0 = open sky .. 1 = fully under something
}

varying float amul;

// AXIS-LOCKED BILLBOARD. ParticleFlagAlignY points each streak's Y at its own velocity, which gets the LEAN right and
// says nothing about which way the quad FACES: its normal stays wherever the particle basis happened to leave it, so
// from a perpendicular heading you see a 1.4 cm quad edge-on ("the raindrops dont face the camera to the east and
// west, so they are very thin" -- master 2026-09-05). A plain billboard would fix the facing and destroy the lean,
// since it forces Y to the view's up. So spin the quad about its OWN long axis instead: Y is kept exactly as the
// particle system set it, and X/Z are rebuilt so the face turns toward the camera. Lean preserved, width restored.
// `cam` is passed IN rather than read from CAMERA_POSITION_WORLD: Godot exposes shader built-ins only inside the
// entry points (vertex/fragment/light), never inside a user-defined function, and referencing one here fails the whole
// shader with "Unknown identifier in expression" -- which costs the rain entirely, not just the billboard.
mat4 face_camera(mat4 m, vec3 cam) {
    vec3 origin = m[3].xyz;
    vec3 ay = m[1].xyz;                       // long axis, velocity-aligned; length carries ScaleAmountMin/Max
    float len = length(ay);
    if (len < 1e-6) return m;
    vec3 a = ay / len;
    float wid = length(m[0].xyz);             // keep the authored 0.014 m width (and its per-particle scale)
    vec3 r = cross(a, cam - origin);
    float rl = length(r);
    // Looking straight down the streak's own axis there is no unique facing -- the cross degenerates. Keep the
    // original basis rather than normalising a zero vector, which would blow the quad up to NaN.
    if (rl < 1e-5) return m;
    r /= rl;
    return mat4(vec4(r * wid, 0.0), vec4(ay, 0.0), vec4(cross(r, a), 0.0), vec4(origin, 1.0));
}

void vertex() {
    mat4 bb = face_camera(MODEL_MATRIX, CAMERA_POSITION_WORLD);
    MODELVIEW_MATRIX = VIEW_MATRIX * bb;
    vec3 wpos = (bb * vec4(VERTEX, 1.0)).xyz;   // per-particle world position (particles are world-space, LocalCoords off)
    float shadow = 0.0;
    if (rain_canopy.w > 0.001)
        shadow = (1.0 - smoothstep(rain_canopy.z * 0.72, rain_canopy.z, distance(wpos.xz, rain_canopy.xy))) * rain_canopy.w;
    // canopy hole + the lens distance-fade (so near drops don't read as fat bars -- matches the old PixelAlpha fade)
    amul = (1.0 - shadow) * smoothstep(1.5, 3.2, distance(wpos, CAMERA_POSITION_WORLD));
    // ROOF: a drop below the topmost surface at its XZ has fallen through something -- it is gone (master 2026-09-04)
    amul *= 1.0 - roof_shelter(wpos.xz, wpos.y, 0.15);   // fade a drop out ACROSS a roof edge instead of switching it off at a cell boundary
}

void fragment() {
    ALBEDO = tint;
    // soft edges: a streak is a translucent thread, not a hard bar -- fade across its width and at both ends (master 2026-09-04
    // "there isnt transparency on the raindrop particles")
    float edge = smoothstep(0.0, 0.45, UV.x) * smoothstep(1.0, 0.55, UV.x) * smoothstep(0.0, 0.18, UV.y) * smoothstep(1.0, 0.82, UV.y);
    ALPHA = alpha_base * amul * edge;
}
