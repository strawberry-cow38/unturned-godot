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

/// How much of this pixel is standing water. `wet` is rain_wetness (the waterline), `upness` is how up-facing the
/// surface is -- water runs off anything steep and never pools on a wall.
// NOTE: the third argument is NOT called `flat` -- that is a reserved interpolation qualifier in GLSL and the
// compiler rejects it with "Expected an identifier for argument name", pointing at the include rather than the caller.
float puddle_mask(vec2 wxz, float wet, float upness) {
    if (wet <= 0.01 || upness <= 0.01) return 0.0;
    // two octaves: broad hollows (~9 m) with a smaller wobble on the shoreline, so the outline is irregular rather
    // than a circle. Deliberately low frequency -- a road puddle is metres across, not centimetres.
    float depth = pud_noise(wxz * 0.11) * 0.72 + pud_noise(wxz * 0.37) * 0.28;
    float line = mix(0.66, 0.44, clamp(wet, 0.0, 1.0));   // the waterline rises with the rain: bigger, joined-up puddles
    return smoothstep(line + 0.05, line - 0.03, depth) * upness;
}
