// FOLIAGE (flowers + pebbles) -- the non-displacing sibling of grass_displace.gdshader.
//
// Flat double-sided billboards use cull_disabled, and Godot FLIPS the vertex normal to (0,-1,0) on back faces --
// pointing at the floor, which reads as permanently dark at every time of day (the "one side always dark" bug,
// strawberry). grass_displace.gdshader already fixes this for grass by forcing the normal world-up in the
// fragment; this shader is the same fix for flowers and pebbles, which sit on a plain StandardMaterial3D and
// have no displacement of their own to justify a custom shader otherwise.
shader_type spatial;
render_mode cull_disabled, depth_prepass_alpha, diffuse_lambert, specular_disabled;

uniform sampler2D albedo_tex : source_color, filter_linear_mipmap;
uniform vec4 albedo_color : source_color = vec4(1.0);
uniform bool use_texture = true;
uniform float alpha_scissor : hint_range(0.0, 1.0) = 0.4;

// WIND SWAY (master 2026-08-24): FLOWERS sway (do_sway=true), pebbles do NOT (do_sway=false). Same wind_vec global +
// object-space, height-weighted, world-direction sway as wind_sway.gdshader; folded in here so flowers keep the
// up-normal fix. FoliageField sets do_sway from `nm.StartsWith("flowers")`.
global uniform vec4 wind_vec;   // xy = direction, z = 0..1 strength
uniform bool do_sway = false;
uniform float sway : hint_range(0.0, 0.4) = 0.08;

void vertex() {
	if (do_sway) {
		float h = max(VERTEX.y, 0.0);
		vec3 wp = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
		float gust = sin(TIME * 1.6 + wp.x * 0.2 + wp.z * 0.17);
		float amp = h * sway * (0.4 + wind_vec.z) * (0.7 + 0.3 * gust);
		VERTEX += inverse(mat3(MODEL_MATRIX)) * (vec3(wind_vec.x, 0.0, wind_vec.y) * amp);
	}
}

void fragment() {
	vec4 c = use_texture ? texture(albedo_tex, UV) : albedo_color;
	ALBEDO = c.rgb;
	ALPHA = c.a;
	ALPHA_SCISSOR_THRESHOLD = alpha_scissor;
	ROUGHNESS = 1.0;
	// Foliage should light like GROUND -- flat billboards lit evenly, no per-face directional darkness. Force
	// world-up in view space regardless of facing, same as grass_displace.gdshader. Shadows are positional, so
	// this keeps them intact.
	NORMAL = normalize((VIEW_MATRIX * vec4(0.0, 1.0, 0.0, 0.0)).xyz);
}
