// FOLIAGE WIND SWAY (master 2026-08-24) for 3D foliage that isn't grass billboards: tree LEAVES (part 1, never the
// trunk), bushes, and other foliage props. Sways the mesh in the world wind direction, amplitude growing with LOCAL
// height (the base stays put, the top sways most), a gust wave crawling over the map, driven by the global `wind_vec`
// (xy = direction, z = 0..1 strength) that PlayerController pushes from WindField.
shader_type spatial;
// Shading matches ResourceField.MakeMat / the felled-debris StandardMaterial3D (default burley diffuse + specular,
// roughness 1) so a SWAYING canopy still reads the same colour as the tree's own fallen debris (master: the felled
// leaf colour must be consistent with the standing tree). The sway is a VERTEX effect; only the fragment must match.
render_mode cull_disabled, depth_prepass_alpha;

global uniform vec2 ug_origin;   // WorldOrigin: the floating origin, so the gust pattern stays put through a rebase
global uniform vec4 wind_vec;   // xy = direction, z = 0..1 strength, w = SWAY PHASE (integrated CPU-side: the gust rate rises with the wind, and a running clock scaled by a changing factor would jump)
// ROTOR WASH AND EVERYTHING ELSE THAT SHOVES PLANTS (strawberry 2026-10-11). Until now this shader read only the
// wind, which is why a helicopter could flatten a lawn and leave the hedge beside it perfectly still: the
// displacement field was private to the grass shader. It is shared now -- same texture, same curve.
#include "res://content/disturbance.gdshaderinc"

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
    vec3 wp = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz + vec3(ug_origin.x, 0.0, ug_origin.y);   // world pos so the gust travels coherently across the map
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
    // ⚠ A BRANCH BENDS, IT DOES NOT SQUASH. flat_mul is 0.18 rather than grass's 1.0: pressing a canopy down into
    // its own trunk reads as the tree SHRINKING, not as wind hitting it. Almost all of a tree's response should be
    // lateral, with just enough droop to stop the bend looking like a slide.
    //
    // ⚠⚠ THE WEIGHT IS LOCAL HEIGHT, NOT A UV. Grass weights by UV because a blade is a billboard hinging at the
    // ground; a canopy's vertices carry no such convention, and `h` here is already the local height above the
    // instance base that the wind sway itself uses. Reusing it keeps the trunk still and the crown moving, which
    // is the same hinge in a different coordinate system.
    //
    // wp is this vertex in world space (built above for the gust phase) -- the field is world-space, and the
    // result rotates back into local alongside the wind offset, so an instance's own Y rotation is respected.
    world_off += disturb_offset(wp - vec3(ug_origin.x, 0.0, ug_origin.y), min(h, 1.0), 0.18);
    VERTEX += inverse(mat3(MODEL_MATRIX)) * world_off;
}

void fragment() {
	// NO manual back-face flip here (removed 2026-10-04). cull_disabled ALREADY makes Godot negate the normal of a
	// back-facing fragment before this function runs (DO_SIDE_CHECK, scene_forward_clustered.glsl; true of custom
	// shaders and built-in materials alike). The `NORMAL = FRONT_FACING ? NORMAL : -NORMAL` that used to sit here
	// undid that, so every back face was lit from the wrong side -- the always-dark back of a flag, a guard rail
	// seen from behind. Measured on a bare quad: back face lit WITHOUT the line, black WITH it.
    vec4 c = texture(albedo_tex, UV);
    ALBEDO = c.rgb;
    if (use_alpha) { ALPHA = c.a; ALPHA_SCISSOR_THRESHOLD = alpha_scissor; }
    ROUGHNESS = 1.0;   // match MakeMat / the felled debris
}
