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

// Set by PlayerController via RenderingServer.GlobalShaderParameterSet. xyz = the point, w unused (matches retail's
// Vector4, whose w is likewise 0).
global uniform vec4 grass_displacement_point;

uniform sampler2D albedo_tex : source_color, filter_linear_mipmap;
uniform float alpha_scissor : hint_range(0.0, 1.0) = 0.4;
uniform float radius : hint_range(0.1, 6.0) = 1.5;        // how far out blades feel you
uniform float strength : hint_range(0.0, 2.0) = 0.55;     // how far the tip is pushed, in metres
uniform float vertical_reach : hint_range(0.1, 6.0) = 1.6; // ...and how far up/down, so a roof overhead flattens nothing

void vertex() {
	// world_vertex_coords: VERTEX arrives in WORLD space, so the push can be computed directly. UV is a vertex
	// attribute and is unaffected by that mode, which is what makes the height weight below still available.
	vec2 away = VERTEX.xz - grass_displacement_point.xz;
	float dist = length(away);

	float infl = clamp(1.0 - dist / radius, 0.0, 1.0);
	// Gate vertically too. Without this, standing on a roof or a second floor mows the lawn underneath -- the point
	// is a position, not a capsule, so nothing else stops it reaching through a floor.
	infl *= clamp(1.0 - abs(VERTEX.y - grass_displacement_point.y) / vertical_reach, 0.0, 1.0);
	infl *= infl;   // ease in, so the edge of the radius is a soft boundary rather than a visible circle in the grass

	// HEIGHT WEIGHT. ObjMesh.Load V-flips UVs on import (Unity V-up -> Godot V-down) and grass_00.obj has v=1 at the
	// blade TOP, so in Godot UV.y = 0 is the top. Verified against the mesh rather than assumed -- weighting the wrong
	// end anchors the tips and swings the roots, which reads as the grass being blown from below.
	float h = 1.0 - UV.y;
	h *= h;   // the base barely moves; the bend accumulates toward the tip, like a real blade hinging at the ground

	float push = infl * h * strength;
	if (dist > 0.0001) {
		VERTEX.xz += (away / dist) * push;
		VERTEX.y -= push * 0.35;   // pressed down as well as aside -- a blade bent over is shorter
	}
}

void fragment() {
	vec4 c = texture(albedo_tex, UV);
	ALBEDO = c.rgb;
	ALPHA = c.a;
	ALPHA_SCISSOR_THRESHOLD = alpha_scissor;
	ROUGHNESS = 1.0;
	// Foliage normals are baked straight UP by the extraction tools, so the flat billboards light like ground rather
	// than showing per-face directional darkness. Keeping NORMAL untouched preserves that.
}
