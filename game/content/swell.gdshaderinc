// THE SWELL FIELD -- the one definition of where the sea surface actually is.
//
// It lives in its own include because THREE things have to agree on it to the metre: water.gdshader displaces its
// vertices by it, WaveField.cs is the CPU twin that floats boats on it, and rain_streak.gdshader now needs it to
// know where a raindrop stops (master 2026-09-07: "the water level should kill raindrops falling below it, so they
// dont fall underwater"). The mean sea level is NOT good enough for that: the swell is half a metre either way, so
// a flat cut leaves rain driving into every wave crest and a half-metre of empty air over every trough -- which is
// the artifact being complained about, just moved.
//
// ⚠ WaveField.cs is still a hand-kept copy of the constants and the noise. Change anything here and change it there
// (its own header says so). This include at least gets the two GPU users down from two copies to none.
//
// CONSTANTS, not uniforms: nothing has ever set them from C# and two shaders reading one set of numbers is the whole
// point. If they ever need to be tunable at runtime they become globals, not per-material uniforms.
const float SWELL_AMP = 0.5;        // metres of vertical swell (CALM baseline; scaled by swell_scale below)

// WEATHER SCALES THE WAVE HEIGHT (strawberry 2026-09-12: "does it actually affect the wave amplitude?" -- it did
// not; chop, foam, mirror and colour all made the sea LOOK rougher without it BEING rougher).
//
// A global, exactly as the note above prescribes, because this number is load-bearing in three places that must
// never disagree: the vertex displacement here, the RAIN shader (which needs the surface height to know where a
// drop stops), and WaveField.cs on the CPU -- which is what boats actually float on. Raise it on the GPU alone and
// the sea gets rougher while the runabout keeps bobbing to the calm-water height. One value, one setter
// (RainSystem3D, beside rain_intensity), so buoyancy and visuals cannot drift apart.
//
// Declared with a default of 1.0 so a shader that loads before the global is registered still renders calm water
// rather than a flat sea -- and EnsureGlobals runs before any water material is built, same rule as wind_vec.
global uniform float swell_scale;
float swell_amp() { return SWELL_AMP * max(swell_scale, 0.0001); }
const float SWELL_DIR_DEG = 30.0;   // swell travel direction
const float SWELL_FU = 0.081;       // noise freq ALONG travel (lowered 10% -> waves ~10% bigger, master "zoom in")
const float SWELL_FW = 0.027;       // noise freq ALONG the crest (lower = longer crests); fu/fw = 3:1 anisotropy (master's pick)
const float SWELL_SPEED = 3.0;

// GRADIENT (Perlin) noise -- unlike value noise it has NO axis-aligned cell artifacts (value is 0 at every lattice
// point, random gradients interpolated), so the surface doesn't read as blocks/squares or SNAP as it scrolls.
// precision-robust hash (Hoskins) -- fract() bounds the input FIRST, so it doesn't degrade at large world coords
// like fract(sin(...)) does. That sin-hash correlates into a GRID once the map's coords get big (the real bug).
float hashv(vec2 p) { vec3 p3 = fract(vec3(p.xyx) * 0.1031); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.x + p3.y) * p3.z); }
vec2 grad2(vec2 ip) { float h = hashv(ip) * 6.2831853; return vec2(cos(h), sin(h)); }
float gnoise(vec2 p) {
    vec2 i = floor(p), f = fract(p);
    vec2 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);   // quintic
    float a = dot(grad2(i), f);
    float b = dot(grad2(i + vec2(1.0, 0.0)), f - vec2(1.0, 0.0));
    float c = dot(grad2(i + vec2(0.0, 1.0)), f - vec2(0.0, 1.0));
    float d = dot(grad2(i + vec2(1.0, 1.0)), f - vec2(1.0, 1.0));
    return mix(mix(a, b, u.x), mix(c, d, u.x), u.y) * 0.8 + 0.5;   // remap ~[-0.6,0.6] -> ~[0,1]
}
float fbm5(vec2 p) { float s = 0.0, a = 0.5; for (int i = 0; i < 5; i++) { s += a * (gnoise(p) - 0.5); p *= 2.03; a *= 0.5; } return s; }
float fbm3(vec2 p) { float s = 0.0, a = 0.5; for (int i = 0; i < 3; i++) { s += a * (gnoise(p) - 0.5); p *= 2.03; a *= 0.5; } return s / 0.4375; }

/// ANISOTROPIC swell height at world XZ + phase-time, in units of SWELL_AMP (coarse 3-octave swell).
float swell_at(vec2 wp, float tphase) {
    float a = radians(SWELL_DIR_DEG); float c = cos(a), s = sin(a);
    float u = wp.x * c + wp.y * s;    // along travel
    float w = -wp.x * s + wp.y * c;   // along the crest line
    return fbm3(vec2(u * SWELL_FU + tphase, w * SWELL_FW));
}
float swell_phase(float t) { return t * SWELL_SPEED * SWELL_FU; }   // scroll the field along the travel axis
/// ...and in METRES above the mean water plane, which is what anything outside the water shader wants.
float swell_offset(vec2 wp, float t) { return swell_at(wp, swell_phase(t)) * swell_amp(); }
