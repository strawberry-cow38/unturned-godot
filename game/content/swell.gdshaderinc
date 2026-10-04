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
const float SWELL_SPEED = 3.0;

// HOW STRETCHED THE SWELL IS: fu/fw, so 3 = crests three times longer than they are wide. master 2026-10-04,
// "making the whole thing look a lot less stretched on one axis" -- this ratio IS that stretch, and it was a
// deliberate pick in an earlier pass rather than an accident.
//
// ⚠⚠ A GLOBAL, NOT A CONST, EXACTLY AS THE NOTE ABOVE PRESCRIBES -- and for the reason that note gives: the CPU
// twin floats boats on this same field. A const changed here and forgotten in WaveField.cs gives a sea whose
// crests run one way on screen and another under the hull. One C# owner (WaveField.SwellAniso) pushes this, the
// way RainSystem3D already owns swell_scale.
//
// ⚠ Guarded, because an UNREGISTERED global reads 0 and fw = fu/0 is infinity -- a flat, dead sea that looks like
// the water failed to load rather than like a bad number.
global uniform float swell_aniso;
float swell_fw() { return SWELL_FU / max(swell_aniso, 0.25); }

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

// SHORE REFRACTION (master 2026-10-04: "bringing the waves to move towards the shore"). ShoreField.cs bakes the
// seabed gradient once at world build; rg = unit direction toward shallower water, b = how much to trust it.
//
// ⚠⚠ NOT FROM THE SHORE FOAM. That foam reads `depth_tex`, the SCREEN depth buffer, so a direction taken from it
// would swing as the camera turned -- the sea reacting to the viewer, which is worse than a fixed heading.
// ⭐ The seabed is view-independent and identical for every player, which is the property actually needed.
global uniform sampler2D shore_dir_tex : filter_linear, repeat_disable;
global uniform vec4 shore_rect;   // xy = world origin, zw = 1/world size -> world XZ to UV

// ⚠⚠⚠ HOW MUCH THE SHORE MAY BEND THE SWELL. DEFAULT 0 -- OFF -- because the first version of this VISIBLY
// WRECKED THE SEA (master, looking at it: "whys it all scrunchy").
//
// THE CAUSE, which is worth stating exactly because the idea is sound and the implementation was not:
// the swell samples noise along `u = dot(wp, d)`. With one global `d` that is a clean linear ramp, and the
// wave's spatial frequency is exactly SWELL_FU. Let `d` vary with position and u stops being linear -- its
// gradient gains a `(grad d) . wp` term.
//
// ⚠ And `wp` is WORLD coordinates: thousands of metres. So an utterly negligible change in direction gets
// multiplied by a ~2000 m lever arm and swamps SWELL_FU entirely. The effective wavelength collapses wherever
// the field turns, which is the swirling and pinching in master's screenshot. Smoothing the direction field
// bounds `grad d` and does nothing whatever about the number it is multiplied by.
//
// ⭐ I wrote that risk down BEFORE building this -- "if the direction varies per position the field becomes
// incoherent" -- and then argued myself out of it because the field is smooth. Smooth was never the question.
global uniform float shore_bend;

vec3 shore_sample(vec2 wp) {
    vec2 uv = (wp - shore_rect.xy) * shore_rect.zw;
    // ⚠ Outside the baked rect there IS no seabed data, so claim none rather than clamping to the edge cell --
    // an edge cell smeared across the open ocean would steer the whole horizon toward one arbitrary beach.
    if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0)))) return vec3(1.0, 0.0, 0.0);
    return texture(shore_dir_tex, uv).rgb;
}

/// ANISOTROPIC swell height at world XZ + phase-time, in units of SWELL_AMP (coarse 3-octave swell).
float swell_at(vec2 wp, float tphase) {
    float a = radians(SWELL_DIR_DEG);
    vec2 open = vec2(cos(a), sin(a));          // the open-ocean heading, unchanged offshore
    vec3 sh = shore_sample(wp);
    // ⭐ BLEND, never switch. Refraction is a shallow-water effect: deep water keeps the swell it arrived with and
    // only bends as the bottom comes up, which is both what the sea does and what keeps the field continuous --
    // a hard switch would put a seam along the contour where it flipped.
    vec2 d = mix(open, sh.rg, clamp(sh.b, 0.0, 1.0) * clamp(shore_bend, 0.0, 1.0));
    float dl = length(d);
    // ⚠ A mix of two near-opposite headings can cancel to nothing. Falling back to the open heading keeps a
    // defined direction instead of normalising a zero vector into NaN and punching a hole in the sea.
    d = dl > 1e-3 ? d / dl : open;
    float c = d.x, s2 = d.y;
    float u = wp.x * c + wp.y * s2;    // along travel
    float w = -wp.x * s2 + wp.y * c;   // along the crest line
    return fbm3(vec2(u * SWELL_FU + tphase, w * swell_fw()));
}
float swell_phase(float t) { return t * SWELL_SPEED * SWELL_FU; }   // scroll the field along the travel axis
/// ...and in METRES above the mean water plane, which is what anything outside the water shader wants.
float swell_offset(vec2 wp, float t) { return swell_at(wp, swell_phase(t)) * swell_amp(); }
