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
    // ONLY `level` gates the early out, and that is deliberate: it is a global uniform, so every pixel of a quad
    // takes the same branch and the fwidth() below is well defined. upness and range VARY within a quad (a road
    // meeting a kerb, the far edge of the distance fade), and branching on those would leave neighbouring lanes
    // without a `depth` to differentiate against -- undefined derivatives, a hairline of wrong edge width along
    // exactly those seams. They multiply out at the end instead; in clear weather nothing here runs either way.
    if (level <= 0.01) return 0.0;
    // two octaves: hollows a few metres across with a smaller wobble on the shoreline, so the outline is irregular
    // rather than a circle. Scaled down from the first pass at master's word -- these are puddles, not ponds.
    float depth = pud_noise(wxz * 0.19) * 0.72 + pud_noise(wxz * 0.62) * 0.28;
    // THE WATERLINE RISES WITH THE WATER -- and it used to fall. `depth` is ground HEIGHT and the test below wets
    // everything BELOW `line`, so mix(0.62 -> 0.46) meant the driest moment had the highest waterline: the instant
    // rain_puddle crossed the early-out at 0.01 the roads went straight to their widest coverage, and then shrank
    // as it kept raining. That is what master saw as "the puddles just appear suddenly" -- not a fade that was too
    // fast, a fade running backwards from full. Low end sits under the noise floor so no rain really is no water.
    float line = mix(0.10, 0.46, clamp(level, 0.0, 1.0));
    // A SHORELINE, not a smudge. A fixed-width band (0.05/0.03) is many metres wide on noise this slow, so every
    // puddle faded out like smoke instead of ending at an edge. fwidth gives the band the width of about one PIXEL
    // of the depth field, so the edge is crisp at your feet and still anti-aliased at range rather than crawling.
    float aa = max(fwidth(depth) * 1.5, 0.004);
    if (upness <= 0.001 || range <= 0.001) return 0.0;
    // ...and the first puddles come UP rather than blinking on: coverage alone would start them as fully-formed
    // specks. Over the first ~12% of the fill (about 18 s of rain) they are faint as well as small.
    return smoothstep(line + aa, line - aa, depth) * upness * range * smoothstep(0.0, 0.12, level);
}
