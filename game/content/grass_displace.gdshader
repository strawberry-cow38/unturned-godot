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
// FOLIAGE WIND (master 2026-08-24): set per-frame by PlayerController from WindField. xy = wind direction, z = 0..1 strength.
global uniform vec4 wind_vec;
// EXTENDED DISPLACERS (master 2026-08-24: vehicles, dropped items, remote players -- BEYOND retail's local-player-only
// point above). PlayerController packs the nearest few into this data texture: one texel each, xyz = world point,
// w = footprint radius in metres (w <= 0 = an empty slot). grass_displacer_count = how many are live this frame, so
// the per-blade loop only touches active slots. texelFetch (not texture()) so the packed floats read back exact.
global uniform sampler2D grass_displacers : filter_nearest;
global uniform int grass_displacer_count;

uniform sampler2D albedo_tex : source_color, filter_linear_mipmap;
uniform float alpha_scissor : hint_range(0.0, 1.0) = 0.4;
uniform float radius : hint_range(0.1, 8.0) = 2.2;        // how far out blades feel you (bumped from retail-ish 1.5 so it actually reads)
uniform float strength : hint_range(0.0, 3.0) = 0.7;     // how far the tip is pushed aside, in metres
uniform float vertical_reach : hint_range(0.1, 8.0) = 2.5; // ...and how far up/down, so a roof overhead flattens nothing

// One displacer's offset on this blade: bend the tip away in XZ + press it down, scaled by INFLUENCE (radial +
// vertical falloff, squared for a soft edge) and the HEIGHT WEIGHT h (base fixed, tip bends most -- a blade hinging
// at the ground). Factored out so retail's player point and every extended displacer share ONE curve, each sizing
// its own footprint (pradius) / push (pstrength) / vertical gate (pvert) / flatten (downf).
vec3 grass_push(vec3 v, float uvy, vec3 point, float pradius, float pstrength, float pvert, float downf) {
	vec2 away = v.xz - point.xz;
	float dist = length(away);
	float infl = clamp(1.0 - dist / pradius, 0.0, 1.0);
	// Gate vertically too: the point is a position, not a capsule, so this is what stops a car on a bridge (or a
	// player on a roof) from mowing the lawn underneath.
	infl *= clamp(1.0 - abs(v.y - point.y) / pvert, 0.0, 1.0);
	infl *= infl;   // ease in, so the radius edge is a soft boundary, not a visible circle in the grass
	// HEIGHT WEIGHT. ObjMesh.Load V-flips UVs on import (Unity V-up -> Godot V-down) and grass_00.obj has v=1 at the
	// blade TOP, so in Godot uvy = 0 is the top. Weighting the wrong end anchors the tips + swings the roots.
	float h = 1.0 - uvy;
	h *= h;
	float push = infl * h * pstrength;
	if (dist < 0.0001) return vec3(0.0);
	vec3 o = vec3(0.0);
	o.xz = (away / dist) * push;   // bent aside...
	o.y = -push * downf;           // ...and pressed down -- a bent blade is shorter
	return o;
}

void vertex() {
	// world_vertex_coords: VERTEX arrives in WORLD space, so pushes compute directly. UV is a vertex attribute,
	// unaffected by that mode, so the height weight stays available. Accumulate every displacer's offset from the
	// REST position (each measured against the unmodified VERTEX, so they add rather than compound), apply once.
	vec3 off = vec3(0.0);
	// LOCAL PLAYER -- retail's single point at (x, y+0.5, z); radius/strength/vertical_reach + the 0.35 flatten
	// exactly as before, so this path is behaviourally unchanged.
	off += grass_push(VERTEX, UV.y, grass_displacement_point.xyz, radius, strength, vertical_reach, 0.8);   // strong flatten so the trampled patch reads, not just a sideways shuffle of dense blades
	// EXTENDED DISPLACERS (master): vehicles, dropped items, remote players. Each texel's w is its footprint radius;
	// strength / vertical gate / flatten scale UP with radius, so a truck presses a wide hard swath while a dropped
	// can only dimples. The loop is bounded by the live count, so quiet frames cost nothing.
	for (int i = 0; i < grass_displacer_count; i++) {
		vec4 d = texelFetch(grass_displacers, ivec2(i, 0), 0);
		float pr = d.w;
		if (pr <= 0.0) continue;
		float ps = clamp(pr * 0.4, 0.35, 1.6);        // bigger footprint -> stronger bend
		float pv = max(pr, 2.0);                        // ...reaches higher up/down
		float pd = clamp(0.6 + pr * 0.12, 0.6, 1.0);   // ...and flattens HARD (a car mats the grass flat, not just parts it)
		off += grass_push(VERTEX, UV.y, d.xyz, pr, ps, pv, pd);
	}
	VERTEX += off;
	// WIND SWAY (master 2026-08-24): bend the tips in the wind direction with a gust wave crawling over the map.
	float wh = 1.0 - UV.y;   // 1 at the tip, 0 at the root (same weight as the player push)
	float gust = sin(TIME * 1.5 + VERTEX.x * 0.25 + VERTEX.z * 0.2);
	float wbend = wh * (0.10 + 0.35 * wind_vec.z) * (0.65 + 0.35 * gust);
	VERTEX.xz += wind_vec.xy * wbend;
	VERTEX.y -= abs(wbend) * 0.15;   // a bent blade sits a touch lower
}

void fragment() {
	vec4 c = texture(albedo_tex, UV);
	ALBEDO = c.rgb;
	ALPHA = c.a;
	ALPHA_SCISSOR_THRESHOLD = alpha_scissor;
	ROUGHNESS = 1.0;
	// Foliage should light like GROUND -- flat billboards lit evenly, no per-face directional darkness. The mesh
	// normals ARE baked straight up (vn 0,1,0), but cull_disabled FLIPS the normal to (0,-1,0) on back faces, which
	// points at the floor and stays permanently dark at every time of day -- the "one side always dark" bug
	// (strawberry). Leaving NORMAL untouched only preserved the up-normal on the FRONT face. So force world-up in the
	// fragment regardless of facing (NORMAL lives in VIEW space here). Shadows are positional, so this keeps them intact.
	NORMAL = normalize((VIEW_MATRIX * vec4(0.0, 1.0, 0.0, 0.0)).xyz);
}
