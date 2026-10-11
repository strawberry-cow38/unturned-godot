// GRASS DISPLACEMENT -- blades bend away from the player as they walk through.
//
// SOURCE-ACCURATE WHERE THE SOURCE IS READABLE. Retail's GrassDisplacement.cs is four lines: it pushes ONE global
// shader vector every frame,
//     Shader.SetGlobalVector("_Grass_Displacement_Point",
//         new Vector4(transform.position.x, transform.position.y + 0.5f, transform.position.z, 0.0f));
// gated by the GRASS_DISPLACEMENT_ON keyword (GraphicsSettings.cs:951). So: a SINGLE point, not a list -- only the
// local player displaces grass, never zombies or other players -- and it sits half a metre ABOVE the transform
// origin, which is what makes the bend read as a shin pushing through rather than the ground shoving blades aside.
// Both of those are reproduced exactly; the driver is PlayerController.
//
// WHAT IS NOT FROM THE SOURCE, stated plainly: the actual bend curve. Retail's falloff lives in the compiled foliage
// shader inside the asset bundles, not in the SDK C#, so the radius, strength and shape below are authored to look
// right rather than extracted. The INPUT is source-accurate; the response to it is a choice, and one worth a human
// eye before it is called done.
shader_type spatial;
render_mode world_vertex_coords, cull_disabled, depth_prepass_alpha, diffuse_lambert, specular_disabled;

// FOLIAGE WIND (master 2026-08-24): set per-frame by PlayerController from WindField. xy = wind direction, z = 0..1 strength.
global uniform vec4 wind_vec;
global uniform vec2 ug_origin;   // WorldOrigin: the gust phase is a WORLD pattern; the displacers above stay local
// EVERY SOURCE comes from the shared DISTURBANCE FIELD now -- including the local player, who used to be a
// special-cased global point of his own. One field, one curve, read by this shader and by wind_sway (so a
// helicopter's downwash reaches the trees as well as the lawn). See disturbance.gdshaderinc.
#include "res://content/disturbance.gdshaderinc"

uniform sampler2D albedo_tex : source_color, filter_linear_mipmap;
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
#include "res://content/foliage_fade.gdshaderinc"

uniform float alpha_scissor : hint_range(0.0, 1.0) = 0.4;

void vertex() {
	// world_vertex_coords: VERTEX arrives in WORLD space, so pushes compute directly. UV is a vertex attribute,
	// unaffected by that mode, so the height weight stays available. Accumulate every displacer's offset from the
	// REST position (each measured against the unmodified VERTEX, so they add rather than compound), apply once.
	// HEIGHT WEIGHT. ObjMesh.Load V-flips UVs on import (Unity V-up -> Godot V-down) and grass_00.obj has v=1 at
	// the blade TOP, so in Godot uvy = 0 is the top. Weighting the wrong end anchors the tips and swings the roots.
	float h = 1.0 - UV.y;
	h *= h;
	// Grass takes the FULL flatten: the hard-won lesson from three rounds of this is that dense short billboards
	// shuffling sideways is invisible, so the down-press has to dominate or the trampled patch cannot be seen.
	VERTEX += disturb_offset(VERTEX, h, 1.0);
	// WIND SWAY (master 2026-08-24): bend the tips in the wind direction with a gust wave crawling over the map.
	float wh = 1.0 - UV.y;   // 1 at the tip, 0 at the root (same weight as the player push)
	float gust = sin(wind_vec.w * 1.5 + (VERTEX.x + ug_origin.x) * 0.25 + (VERTEX.z + ug_origin.y) * 0.2);
	float wbend = wh * (0.10 + 0.35 * wind_vec.z) * (0.65 + 0.35 * gust);
	VERTEX.xz += wind_vec.xy * wbend;
	VERTEX.y -= abs(wbend) * 0.15;   // a bent blade sits a touch lower
}

void fragment() {
	vec4 c = texture(albedo_tex, UV);
	ALBEDO = c.rgb;
	ALPHA = c.a * foliage_fade_view(VERTEX, INV_VIEW_MATRIX);   // raw fragment VERTEX -- the include converts; see foliage_fade.gdshaderinc
	ALPHA_SCISSOR_THRESHOLD = alpha_scissor;
	ROUGHNESS = 1.0;
	// Foliage should light like GROUND -- flat billboards lit evenly, no per-face directional darkness. The mesh
	// normals ARE baked straight up (vn 0,1,0), but cull_disabled FLIPS the normal to (0,-1,0) on back faces, which
	// points at the floor and stays permanently dark at every time of day -- the "one side always dark" bug
	// (strawberry). Leaving NORMAL untouched only preserved the up-normal on the FRONT face. So force world-up in the
	// fragment regardless of facing (NORMAL lives in VIEW space here). Shadows are positional, so this keeps them intact.
	NORMAL = normalize((VIEW_MATRIX * vec4(0.0, 1.0, 0.0, 0.0)).xyz);
}
