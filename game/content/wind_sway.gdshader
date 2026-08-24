// FOLIAGE WIND SWAY (master 2026-08-24) for 3D foliage that isn't grass billboards: tree LEAVES (part 1, never the
// trunk), bushes, and other foliage props. Sways the mesh in the world wind direction, amplitude growing with LOCAL
// height (the base stays put, the top sways most), a gust wave crawling over the map, driven by the global `wind_vec`
// (xy = direction, z = 0..1 strength) that PlayerController pushes from WindField.
shader_type spatial;
// Shading matches ResourceField.MakeMat / the felled-debris StandardMaterial3D (default burley diffuse + specular,
// roughness 1) so a SWAYING canopy still reads the same colour as the tree's own fallen debris (master: the felled
// leaf colour must be consistent with the standing tree). The sway is a VERTEX effect; only the fragment must match.
render_mode cull_disabled, depth_prepass_alpha;

global uniform vec4 wind_vec;   // xy = wind direction, z = 0..1 strength

uniform sampler2D albedo_tex : source_color, filter_linear_mipmap;
uniform float alpha_scissor : hint_range(0.0, 1.0) = 0.4;
uniform bool use_alpha = true;
uniform float sway : hint_range(0.0, 0.4) = 0.05;   // metres of lateral sway per metre of height at full wind

void vertex() {
    float h = max(VERTEX.y, 0.0);                            // LOCAL height above the base -> the canopy top sways most
    vec3 wp = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;        // world pos so the gust travels coherently across the map
    float gust = sin(TIME * 1.3 + wp.x * 0.16 + wp.z * 0.14);
    float amp = h * sway * (0.4 + wind_vec.z) * (0.7 + 0.3 * gust);
    // Bend in the WORLD wind direction: build the world offset, rotate it into local (the instance may be Y-rotated).
    vec3 world_off = vec3(wind_vec.x, 0.0, wind_vec.y) * amp;
    VERTEX += inverse(mat3(MODEL_MATRIX)) * world_off;
}

void fragment() {
    vec4 c = texture(albedo_tex, UV);
    ALBEDO = c.rgb;
    if (use_alpha) { ALPHA = c.a; ALPHA_SCISSOR_THRESHOLD = alpha_scissor; }
    ROUGHNESS = 1.0;   // match MakeMat / the felled debris
}
