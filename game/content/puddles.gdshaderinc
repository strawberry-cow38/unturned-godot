// PUDDLES (strawberry 2026-09-06: "give road props, road splines (not trails) and road terrain materials a separate
// puddles shader, puddles of more reflective blobs on those surfaces").
//
// Its own shader file, included by the three surfaces that need it -- the road props and the road splines (both go
// through wet_surface.gdshader) and the road layer of the terrain shader. An include rather than an extra PASS on
// purpose: a second pass over the terrain redraws the whole ground to paint puddles on a fraction of it, and the
// look is identical either way. The runners on glass earned a pass because they anchor to something different; these
// do not.
//
// WORLD-ANCHORED AND STILL. The field is a function of world XZ only, so a puddle stays exactly where it is -- it
// does not swim when the camera moves, and a car driving over one does not drag it along (the mistake the drops on
// glass made this morning). Nothing here reads TIME either: standing water does not crawl.
//
// It is a DEPTH field, not a texture of blobs: low ground holds water, and as the rain keeps falling the waterline
// rises, so puddles grow and join up rather than fading in as fixed shapes. Dry weather has no waterline at all.

float pud_hash(vec2 p) { vec3 p3 = fract(vec3(p.xyx) * 0.1031); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.x + p3.y) * p3.z); }
vec2  pud_grad(vec2 ip) { float h = pud_hash(ip) * 6.2831853; return vec2(cos(h), sin(h)); }
float pud_noise(vec2 p) {
    vec2 i = floor(p), f = fract(p);
    vec2 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
    float a = dot(pud_grad(i), f), b = dot(pud_grad(i + vec2(1.0, 0.0)), f - vec2(1.0, 0.0));
    float c = dot(pud_grad(i + vec2(0.0, 1.0)), f - vec2(0.0, 1.0)), d = dot(pud_grad(i + vec2(1.0, 1.0)), f - vec2(1.0, 1.0));
    return mix(mix(a, b, u.x), mix(c, d, u.x), u.y) * 0.8 + 0.5;   // ~0..1
}

/// How much of this pixel is standing water. `level` is rain_puddle -- the SLOW one, minutes behind the rain, not
/// rain_wetness -- `upness` is how up-facing the surface is (water runs off anything steep and never pools on a wall),
/// and `range` fades the whole thing out with distance so far-off ground is not paying for water nobody can see.
// NOTE: the third argument is NOT called `flat` -- that is a reserved interpolation qualifier in GLSL and the
// compiler rejects it with "Expected an identifier for argument name", pointing at the include rather than the caller.
float puddle_mask(vec2 wxz, float level, float upness, float range) {
    if (level <= 0.01 || upness <= 0.01 || range <= 0.01) return 0.0;
    // two octaves: hollows a few metres across with a smaller wobble on the shoreline, so the outline is irregular
    // rather than a circle. Scaled down from the first pass at master's word -- these are puddles, not ponds.
    float depth = pud_noise(wxz * 0.19) * 0.72 + pud_noise(wxz * 0.62) * 0.28;
    float line = mix(0.62, 0.46, clamp(level, 0.0, 1.0));   // the waterline rises as water gathers: bigger, joined-up puddles
    return smoothstep(line + 0.05, line - 0.03, depth) * upness * range;
}
