// Flag CLOTH ripple (master 2026-08-24: "ripple the flag based off wind speed where the flag is"). The cloth mesh is
// in the prop's LOCAL base frame: +Y = the flying direction (0 at the pole, ~flag_len at the free edge), X = the thin
// perpendicular the cloth waves along, Z = the height along the pole. A travelling sine (a couple of modes) displaces
// X, amplitude growing toward the free edge and scaled by the live WindField strength (the `wind` uniform, per-frame).
shader_type spatial;
render_mode cull_disabled, diffuse_burley, specular_disabled;

uniform sampler2D flag_tex : source_color, filter_nearest_mipmap;   // master: NEAREST (the flag got forced to bilinear)
uniform float wind = 0.5;        // 0..1 WindField strength at the flag, set every frame by FlagCloth
uniform float flag_len = 4.0;    // cloth flying length (local +Y) -> the amplitude ramp

void vertex() {
    float y = VERTEX.y;                                       // flying distance from the pole (0 .. flag_len)
    float f = clamp(y / flag_len, 0.0, 1.0);                  // 0 at the pole (fixed), 1 at the free edge (loosest)
    // NO SAG (master 2026-09-05 "remove the sag effect on the windy flag shader"). The cloth used to fold down the
    // pole and curl in toward it below a light breeze; the flag now flies from its own geometry at every wind speed.
    //
    // The calm factor STAYS, because it was doing two different jobs under one name. Bending the mesh was the sag
    // and is gone; damping the RIPPLE so a still flag is not snapping in dead air is not sag, and removing it as
    // well would have tripled the amplitude at wind 0 -- a feel change nobody asked for, arriving under the
    // heading of a removal. Renamed to what it actually measures.
    float calm = 1.0 - smoothstep(0.2, 0.55, wind);
    // RIPPLE: a travelling sine on the thin axis, fading with wind (a limp flag barely moves)
    float amp = (0.06 + 0.55 * wind) * f * (1.0 - 0.7 * calm);
    float ph  = y * 2.4 - TIME * (2.0 + 4.0 * wind);          // wave crawls down the flag, faster in stronger wind
    float d = sin(ph) + 0.35 * sin(ph * 0.53 + VERTEX.z * 1.7);   // a 2nd mode + height variation -> non-uniform snap
    VERTEX.x += d * amp;
}

void fragment() {
    ALBEDO = texture(flag_tex, UV).rgb;
    ROUGHNESS = 0.85;
}
