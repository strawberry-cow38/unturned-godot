// POWER LINE WIRES. One static mesh for a whole map's grid; the only thing that moves is this vertex stage.
//
// Master 2026-10-06: "should draw a line and the 4 wires should connect. sways very slightly in the wind."
//
// ⭐ THE SWAY IS HERE AND THE SAG IS NOT. The hanging curve is baked into the geometry by PowerLineField, because
// it never changes -- rebuilding it per frame would be arithmetic done 60 times a second to produce the same
// answer. The wind does change, so it lives in the shader, which also means a map with four hundred spans costs
// one draw call and zero per-frame CPU. Same division the foliage uses.
shader_type spatial;
// ⚠⚠ cull_disabled, NOT cull_back. A 45 mm tube seen from 30 m is pixel-identical whether it is wound inside
// out or not, so the winding CANNOT be judged from the renders I take -- master saw it from arm's length and I
// could not reproduce it at all. Double-siding costs nothing on geometry this thin and removes the entire failure
// mode: there is no camera position from which a wire can be invisible or hollow. Godot also negates back-face
// normals before fragment() under cull_disabled (DO_SIDE_CHECK), so the lighting is right on both sides too.
// See [[feedback_godot_mirror_front_facing_trap]].
render_mode diffuse_lambert, specular_disabled, cull_disabled;

// xy = wind direction, z = 0..1 strength, w = integrated sway phase. The SAME global the grass, flowers and flags
// read (foliage_up.gdshader), so a gust crosses the whole world at once instead of each system having its own
// weather. ⚠⚠ A global uniform reads ZERO until something registers it -- see the godot traps index; WindField's
// registration runs before any material that samples it.
global uniform vec4 wind_vec;
global uniform vec2 ug_origin;   // WorldOrigin: neighbouring spans keep their phase through a rebase

uniform vec3 wire_color : source_color = vec3(0.055, 0.055, 0.062);
uniform float sway_metres = 0.16;   // how far the MIDDLE of a span moves at full wind. "very slightly" (master): a
                                    // span is tens of metres long, so at this amplitude the motion reads as life in
                                    // the line rather than as a skipping rope. It is the whole look, so it is named.

void vertex() {
    // UV.x is the fraction along the span, baked in by PowerLineField.AddWire. It is the only per-span information
    // a single combined mesh can carry -- there is no uniform per span, because there is no draw call per span.
    float t = UV.x;

    // ⭐ PINNED AT BOTH ENDS. A wire is bolted to an insulator at each end and free in the middle, so the envelope
    // has to be zero at t=0 and t=1 and peak at the centre: sin(t*PI) is exactly that and costs one instruction.
    // Without it the ends would shear away from their anchors and the wires would visibly detach from the poles,
    // which is the one artifact that would make this read as broken rather than as windy.
    float freedom = sin(t * 3.14159265);

    // Two waves that do not share a period, so a long line of spans never pulses in unison. The world position is
    // folded in so neighbouring spans are at different points in the cycle -- without that the whole grid breathes
    // together, which reads as the map scaling rather than as wind.
    vec3 wp = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    // ⚠ 1.1 and 1.7, NOT arbitrary. wind_vec.w wraps at 20*PI, so a term reading sin(k * w) is only continuous
    // across that wrap when k*10 is a whole number -- otherwise every wire on the map jumps together once a cycle.
    // The grass and flags use 1.3/1.5/1.6 for the same reason. Keep any new multiplier a multiple of 0.1.
    float drift = (wp.x + ug_origin.x) * 0.09 + (wp.z + ug_origin.y) * 0.07;
    float gust = sin(wind_vec.w * 1.1 + drift) * 0.75
               + sin(wind_vec.w * 1.7 + drift * 1.7 + 1.3) * 0.25;

    // Across the wind, and a smaller amount of lift. A real line swings mostly sideways and rises a little as it
    // does, because the wire length is fixed -- the vertical term is a quarter of the lateral one rather than a
    // separate wobble, so the two cannot drift out of step and make the wire look like it is stretching.
    vec2 w = wind_vec.xy;
    float amp = freedom * sway_metres * (0.25 + 0.75 * wind_vec.z) * gust;
    vec3 push = vec3(w.x, 0.0, w.y) * amp + vec3(0.0, abs(amp) * 0.25, 0.0);

    // MODEL_MATRIX is identity for this mesh (PowerLineField builds it in world space and pins the holder at the
    // origin), but the inverse is kept so the wires still move correctly if the holder is ever parented under
    // something that moves. Costs nothing and removes a hidden assumption.
    VERTEX += inverse(mat3(MODEL_MATRIX)) * push;
}

void fragment() {
    ALBEDO = wire_color;
    ROUGHNESS = 0.55;   // a weathered conductor: not a mirror, not chalk
    METALLIC = 0.0;
    SPECULAR = 0.35;
}
