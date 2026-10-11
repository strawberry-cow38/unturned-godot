// THE DISTURBANCE FIELD -- everything in the world that shoves plants around, in one place that grass, bushes,
// hedges and tree canopies all read (strawberry 2026-10-11: "rip out our grass displacement and rebuild it from
// the ground up. add rotor wash for helis on foliage and trees").
//
// ⭐ WHY THIS REPLACED grass_displace's PRIVATE LIST. The old field was named, shaped and scoped for GRASS:
// `grass_displacers`, one texel of (xyz, radius), read by exactly one shader. Three things fell out of that and
// the rotor-wash request needs all three fixed:
//   1. TREES COULD NOT BE DISTURBED AT ALL. wind_sway.gdshader -- canopies, bushes, hedges -- read only wind_vec.
//      A helicopter cannot flatten what cannot be reached.
//   2. A DISPLACER HAD NO STRENGTH AND NO KIND. One texel of (pos, radius) can only say "something this big is
//      here"; the shader then GUESSED strength from radius. That is fine while every source is a thing standing on
//      the ground, and useless the moment one of them is a column of air with its own power curve.
//   3. A BLADE AND A BRANCH WANT OPPOSITE THINGS. Grass is FLATTENED (pressed down -- the hard-won lesson that a
//      sideways shuffle in short billboards is invisible). A tree is BENT, and pressing a canopy into its trunk
//      looks like the tree shrank. One curve with a per-consumer flatten scale, rather than two curves to drift.
//
// ⚠⚠ THE TUNING IS CARRIED OVER VERBATIM, NOT REDERIVED. The old numbers took three rounds against master's own
// eyes to land ("this goes easier if you dont doubt the person with eyes") and the failure mode was always the
// same: too subtle to see in real, tall grass. The architecture is new; the feel must not be. Every ground-press
// constant below is the number grass_displace shipped with, and DisturbanceField.PressFromRadius reproduces the
// old radius->strength curve exactly.
//
// LAYOUT: an RGBAF texture, one COLUMN per source, two ROWS. texelFetch, never texture(), so the packed floats
// read back exact.
//   row 0: xyz = world position, w = radius in metres   (w <= 0 marks an empty slot)
//   row 1: x = strength, y = flatten, z = kind, w = vertical reach
// Row 1 is the whole point: it is what the old single-texel layout had no room for.

global uniform sampler2D disturb_tex : filter_nearest;
global uniform int disturb_count;

const float DISTURB_PRESS = 0.0;   // something resting on the ground: a player, a car, a dropped can, a wake crumb
const float DISTURB_WASH  = 1.0;   // rotor downwash: a column of air, strongest under the disc, blowing OUTWARD

// One source's world-space offset on one vertex.
//   world_v   the vertex in WORLD space
//   w         bend weight, 0 where the plant is anchored and 1 at the part that moves most
//   flat_mul  how much of the push becomes DOWN rather than ASIDE. Grass 1.0 (it mats flat); a canopy wants
//             almost none of it, because a branch pushed into its own trunk reads as the tree shrinking.
vec3 disturb_one(vec3 world_v, float w, float flat_mul, vec4 a, vec4 b) {
    float pr = a.w;
    if (pr <= 0.0) return vec3(0.0);
    vec2 away = world_v.xz - a.xz;
    float dist = length(away);
    if (dist < 0.0001) return vec3(0.0);
    float strength = b.x, flatten = b.y, kind = b.z, vert = b.w;

    float infl = clamp(1.0 - dist / pr, 0.0, 1.0);
    // Vertical gate: a source is a POINT, not a column, so this is what stops a car on a bridge -- or a
    // helicopter two hundred metres up -- from mowing the lawn underneath.
    infl *= clamp(1.0 - abs(world_v.y - a.y) / vert, 0.0, 1.0);

    if (kind > 0.5) {
        // ROTOR WASH. Not a bigger footprint: a different shape. Under the disc the air goes straight DOWN and
        // there is little lateral flow, so the flattening peaks in an annulus out near the blade tips and the
        // dead centre is comparatively calm -- which is what makes a real downwash read as a ring blasting
        // outward rather than a circle being stamped into the ground.
        float rn = dist / pr;
        float ring = smoothstep(0.0, 0.35, rn) * (1.0 - smoothstep(0.55, 1.0, rn));
        infl = max(infl * 0.35, ring) * clamp(1.0 - abs(world_v.y - a.y) / vert, 0.0, 1.0);
    } else {
        infl *= infl;   // ease in, so the radius edge is a soft boundary and not a visible circle in the grass
    }

    float push = infl * w * strength;
    vec3 o = vec3(0.0);
    o.xz = (away / dist) * push;          // bent aside, away from the source...
    o.y = -push * flatten * flat_mul;     // ...and pressed down: a bent blade is a shorter blade
    return o;
}

// Every source, accumulated from the REST position. Each offset is measured against the unmodified vertex so
// they ADD rather than compound -- two cars side by side must not multiply into a crater.
vec3 disturb_offset(vec3 world_v, float w, float flat_mul) {
    vec3 off = vec3(0.0);
    for (int i = 0; i < disturb_count; i++)
        off += disturb_one(world_v, w, flat_mul,
                           texelFetch(disturb_tex, ivec2(i, 0), 0),
                           texelFetch(disturb_tex, ivec2(i, 1), 0));
    return off;
}
