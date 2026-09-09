// FOLIAGE WIND SWAY (master 2026-08-24) for 3D foliage that isn't grass billboards: tree LEAVES (part 1, never the
// trunk), bushes, and other foliage props. Sways the mesh in the world wind direction, amplitude growing with LOCAL
// height (the base stays put, the top sways most), a gust wave crawling over the map, driven by the global `wind_vec`
// (xy = direction, z = 0..1 strength) that PlayerController pushes from WindField.
shader_type spatial;
// Shading matches ResourceField.MakeMat / the felled-debris StandardMaterial3D (default burley diffuse + specular,
// roughness 1) so a SWAYING canopy still reads the same colour as the tree's own fallen debris (master: the felled
// leaf colour must be consistent with the standing tree). The sway is a VERTEX effect; only the fragment must match.
render_mode cull_disabled, depth_prepass_alpha;

global uniform vec4 wind_vec;   // xy = direction, z = 0..1 strength, w = SWAY PHASE (integrated CPU-side: the gust rate rises with the wind, and a running clock scaled by a changing factor would jump)

uniform sampler2D albedo_tex : source_color, filter_linear_mipmap;
uniform float alpha_scissor : hint_range(0.0, 1.0) = 0.4;
uniform bool use_alpha = true;
uniform float sway : hint_range(0.0, 0.4) = 0.05;   // metres of lateral sway per metre of height at full wind

// A FELLED TREE'S OWN CANOPY (master 2026-09-09: "have the leaves react to falling via the wind shader. and a react
// on impact"). Per-material, not global: only the tree that is actually coming down moves. `gust_dir` is a world
// -space push whose LENGTH is metres of offset per metre of local height, which ResourceField aims against the
// canopy's own travel while the trunk swings; `shake` is the landing shudder, spiked on impact and decayed CPU-side
// so the whole animation stays deterministic. A standing tree leaves both at zero and renders exactly as before.
uniform vec3 gust_dir = vec3(0.0);
// ...and a UNIFORM push, not height-weighted. gust_dir alone is a cantilever: it pins the end nearest the mesh
// origin and grows outward, which is right for a canopy hanging off a swinging trunk and wrong for a landed log,
// where the visible section is the butt -- the end with almost no lever. A felled trunk shudders along its whole
// length. Left at zero for the leaves, which want the cantilever.
uniform vec3 gust_base = vec3(0.0);
uniform float shake : hint_range(0.0, 1.0) = 0.0;
const float SHAKE_AMP = 0.018;   // metres of shudder per metre of local height at shake = 1

void vertex() {
    float h = max(VERTEX.y, 0.0);                            // LOCAL height above the base -> the canopy top sways most
    vec3 wp = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;        // world pos so the gust travels coherently across the map
    float gust = sin(wind_vec.w * 1.3 + wp.x * 0.16 + wp.z * 0.14);
    float amp = h * sway * (0.4 + wind_vec.z) * (0.7 + 0.3 * gust);
    // Bend in the WORLD wind direction: build the world offset, rotate it into local (the instance may be Y-rotated).
    vec3 world_off = vec3(wind_vec.x, 0.0, wind_vec.y) * amp + gust_dir * h + gust_base;
    // PER-INSTANCE axe-hit shudder rides the MultiMesh custom-data channel: the canopy material is shared by
    // every tree of a species, so a uniform would shake the whole map at once. INSTANCE_CUSTOM is zero for
    // anything that is not a MultiMesh instance (the felled debris canopy), which keeps `shake` working there.
    float hit = shake + INSTANCE_CUSTOM.x;
    if (hit > 0.0) {
        // Leaf-BY-LEAF, so the canopy shudders instead of sliding about as one block. Layered sines off the vertex
        // position rather than fract(sin(dot(...))): this is twenty metres of geometry and that hash bands visibly
        // at this scale (the same reason the water shader dropped it).
        vec3 j = vec3(sin(VERTEX.x * 3.7 + VERTEX.y * 2.3),
                      sin(VERTEX.z * 4.1 - VERTEX.y * 1.9),
                      cos(VERTEX.x * 2.9 + VERTEX.z * 3.3));
        world_off += j * (hit * h * SHAKE_AMP);
    }
    VERTEX += inverse(mat3(MODEL_MATRIX)) * world_off;
}

void fragment() {
    vec4 c = texture(albedo_tex, UV);
    ALBEDO = c.rgb;
    if (use_alpha) { ALPHA = c.a; ALPHA_SCISSOR_THRESHOLD = alpha_scissor; }
    ROUGHNESS = 1.0;   // match MakeMat / the felled debris
}
