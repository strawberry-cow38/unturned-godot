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
// ⭐⭐ DISTANCE EROSION -- why the grass no longer pops in 96 m squares.
//
// Each spatial cell is ONE MultiMeshInstance with VisibilityRangeEnd, and the fade mode was Disabled, so a 96 m
// block of grass switched on and off in a single frame as the camera crossed the threshold. That is exactly what
// master reported: "grass etc pops in/out in chunks around the player".
//
// ⭐ Godot's own VisibilityRangeFadeMode.Self was the obvious fix and this is better, for a reason specific to how
// foliage costs: it fades the whole INSTANCE, so the unit of change is still the 96 m cell -- a softer pop in the
// same square. Scaling ALPHA by distance instead erodes each BLADE independently, so the cell boundary stops
// existing as a visible thing at all.
//
// ⭐ AND IT MAKES THE FAR BAND CHEAPER RATHER THAN DEARER. This shader is alpha-scissored, which the foliage notes
// in FoliageField measured as the real cost ("alpha-scissor defeats early-Z, so every blade shades whatever is
// behind it ... nearly free in DRAW CALLS and potentially expensive in PIXELS"). Lowering ALPHA makes MORE texels
// fail the scissor the further out they are, so the most distant grass -- the densest on screen, several blades
// per pixel -- is the part that stops shading first. A fade-to-transparent would have added cost exactly there.
//
// ⚠ The range is a GLOBAL, pushed by FoliageField from the SAME number it gives VisibilityRangeEnd. It has to
// finish BEFORE the hard cull or the pop comes straight back, and UG_FOLIAGECULL moves that cull at runtime -- a
// constant here would silently stop matching it. Same one-owner rule as swell_scale.
global uniform float foliage_fade_start;   // metres: erosion begins
global uniform float foliage_fade_end;     // metres: nothing left (<= the cell's VisibilityRangeEnd)

/// 1.0 near -> 0.0 by foliage_fade_end, measured from the CAMERA.
///
/// ⚠⚠ TAKES A WORLD POSITION AND SUBTRACTS THE CAMERA, rather than using length(VERTEX). That looks like the
/// obvious spelling and it is wrong HERE: grass_displace declares `render_mode world_vertex_coords`, which puts
/// VERTEX in WORLD space in the fragment too -- so length(VERTEX) is the distance from the world ORIGIN. For grass
/// at world (75, 45, -775) that is ~780 m, past any fade end, so EVERY blade read as maximally distant and the
/// whole field was erased at any camera position. Not a subtle mis-fade: all 612k instances, gone.
///
/// ⭐ foliage_up.gdshader does NOT declare world_vertex_coords, so the identical line was CORRECT there. Two
/// shaders sharing an include disagreed about what VERTEX meant, and the one that was wrong is the one with the
/// instances in it. Passing an explicit world position makes the function independent of either render_mode.
///
/// ⚠ Guarded: an unregistered global reads 0, and a 0..0 range would erase every blade -- that failure looks like
/// "the foliage did not load", so it falls back to NO FADE rather than to no grass.
float foliage_fade(vec3 world_pos, mat4 inv_view) {
    if (foliage_fade_end <= 0.001) return 1.0;
    vec3 cam = inv_view[3].xyz;
    return 1.0 - smoothstep(foliage_fade_start, foliage_fade_end, length(world_pos - cam));
}

uniform float alpha_scissor : hint_range(0.0, 1.0) = 0.4;

// WIND SWAY (master 2026-08-24): FLOWERS sway (do_sway=true), pebbles do NOT (do_sway=false). Same wind_vec global +
// object-space, height-weighted, world-direction sway as wind_sway.gdshader; folded in here so flowers keep the
// up-normal fix. FoliageField sets do_sway from `nm.StartsWith("flowers")`.
global uniform vec4 wind_vec;   // xy = direction, z = 0..1 strength, w = SWAY PHASE (integrated CPU-side; see PlayerController)
uniform bool do_sway = false;
uniform float sway : hint_range(0.0, 0.4) = 0.08;

void vertex() {
	if (do_sway) {
		float h = max(VERTEX.y, 0.0);
		vec3 wp = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
		float gust = sin(wind_vec.w * 1.6 + wp.x * 0.2 + wp.z * 0.17);
		float amp = h * sway * (0.4 + wind_vec.z) * (0.7 + 0.3 * gust);
		VERTEX += inverse(mat3(MODEL_MATRIX)) * (vec3(wind_vec.x, 0.0, wind_vec.y) * amp);
	}
}

void fragment() {
	vec4 c = use_texture ? texture(albedo_tex, UV) : albedo_color;
	ALBEDO = c.rgb;
	ALPHA = c.a * foliage_fade((INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz, INV_VIEW_MATRIX);   // VERTEX is VIEW here
	ALPHA_SCISSOR_THRESHOLD = alpha_scissor;
	ROUGHNESS = 1.0;
	// Foliage should light like GROUND -- flat billboards lit evenly, no per-face directional darkness. Force
	// world-up in view space regardless of facing, same as grass_displace.gdshader. Shadows are positional, so
	// this keeps them intact.
	NORMAL = normalize((VIEW_MATRIX * vec4(0.0, 1.0, 0.0, 0.0)).xyz);
}
