shader_type spatial;
render_mode cull_disabled, diffuse_burley, specular_schlick_ggx;

// NOISE-BASED ocean (master 2026-08-16, "drawing board"): the wave HEIGHT is ANISOTROPIC fbm noise -- organic
// directional swell with NO periodic term, so it physically cannot lattice the way summed sines always do.
// Big swell displaces the vertices (coarse-plane safe); fine chop + foam live in the fragment. Transparent,
// depth-tinted, crest foam on the peaks, depth-based shore foam. WaveField.cs is the CPU twin of swell_at().

uniform vec4 water_deep : source_color = vec4(0.07, 0.19, 0.33, 0.72);     // alpha < 1 = see-through
uniform vec4 water_shallow : source_color = vec4(0.16, 0.42, 0.54, 0.26);  // shallows very transparent -> seabed shows
uniform vec4 foam_color : source_color = vec4(0.92, 0.97, 1.0, 1.0);
uniform float swell_amp = 0.5;        // metres of vertical swell
uniform float swell_dir_deg = 30.0;   // swell travel direction
uniform float swell_fu = 0.081;       // noise freq ALONG travel (lowered 10% -> waves ~10% bigger, master "zoom in")
uniform float swell_fw = 0.027;       // noise freq ALONG the crest (lower = longer crests); fu/fw = 3:1 anisotropy (master's pick)
uniform float swell_speed = 3.0;
uniform float crest_foam = 0.25;      // swell-height fraction above which crest foam kicks in (lower = more whitecaps)
uniform float shore_depth = 2.2;      // vertical water depth (m) under which shore foam shows (wider = fuller shore band)
uniform float detail_strength = 0.30; // fbm fine-ripple perturbation of the normal
uniform vec4 sky_tint : source_color = vec4(0.52, 0.67, 0.86, 1.0);  // colour reflected at grazing angles (the sky)
uniform float reflectivity = 0.55;    // how strongly the sky shows at grazing angles (Fresnel)
uniform sampler2D depth_tex : hint_depth_texture, repeat_disable, filter_linear;

varying float v_crest;   // normalised swell height ~[-1,1], for crest foam
varying vec3 v_wpos;     // world position of the displaced surface

// GRADIENT (Perlin) noise -- unlike value noise it has NO axis-aligned cell artifacts (value is 0 at every lattice
// point, random gradients interpolated), so the surface doesn't read as blocks/squares or SNAP as it scrolls.
// Same seed/formula in WaveField.cs so the CPU twin still agrees.
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

// ANISOTROPIC swell height at world XZ + phase-time (coarse 3-octave swell; the vertex displaces this)
float swell_at(vec2 wp, float tphase) {
    float a = radians(swell_dir_deg); float c = cos(a), s = sin(a);
    float u = wp.x * c + wp.y * s;    // along travel
    float w = -wp.x * s + wp.y * c;   // along the crest line
    return fbm3(vec2(u * swell_fu + tphase, w * swell_fw));
}

void vertex() {
    vec2 wp = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xz;
    float tphase = TIME * swell_speed * swell_fu;   // scroll the field along the travel axis
    float h = swell_at(wp, tphase);
    VERTEX.y += h * swell_amp;
    v_crest = h;
    v_wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;   // normal is computed PER-PIXEL in the fragment (below)
}

void fragment() {
    // PER-PIXEL SWELL NORMAL: sample the big-wave gradient HERE (not interpolated from the coarse mesh vertices) so
    // the big waves SHADE smooth even on a low-poly grid -> no grid/snap. The vertex still displaces the geometry.
    float tp = TIME * swell_speed * swell_fu;
    float se = 0.6;
    float sh  = swell_at(v_wpos.xz, tp);
    float shx = swell_at(v_wpos.xz + vec2(se, 0.0), tp);
    float shz = swell_at(v_wpos.xz + vec2(0.0, se), tp);
    vec3 swell_n = vec3((sh - shx) * swell_amp / se, 1.0, (sh - shz) * swell_amp / se);
    // FINE RIPPLE DETAIL: fbm-gradient perturbs the normal (fragment space)
    vec2 np = v_wpos.xz * 0.135 + vec2(TIME * 0.03, TIME * -0.02);   // fine ripple ~10% bigger to match the zoomed swell
    float n0 = fbm5(np);
    float nx = fbm5(np + vec2(0.06, 0.0));
    float nz = fbm5(np + vec2(0.0, 0.06));
    vec3 detail = vec3((n0 - nx), 0.0, (n0 - nz)) * detail_strength * 6.0;
    vec3 wn = normalize(swell_n + detail);
    NORMAL = normalize((VIEW_MATRIX * vec4(wn, 0.0)).xyz);

    // SHORE FOAM: reconstruct the opaque scene behind the water, foam where the water is shallow over it.
    float raw = texture(depth_tex, SCREEN_UV).r;
    vec4 vp = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, raw, 1.0);
    vp.xyz /= vp.w;
    float bed_y = (INV_VIEW_MATRIX * vec4(vp.xyz, 1.0)).y;
    float depth_v = max(v_wpos.y - bed_y, 0.0);
    float shore = 1.0 - smoothstep(0.0, 0.85, depth_v);       // foam band at the waterline -- a bit more of it (master)
    shore *= 0.6 + 0.15 * fbm5(v_wpos.xz * 0.4 + TIME * 0.15);

    // CREST FOAM: gated by its own noise so caps scatter (belt-and-suspenders; noise swell already won't lattice)
    float capMask = fbm5(v_wpos.xz * 0.09 + vec2(TIME * 0.04, TIME * -0.03));
    float gate = smoothstep(-0.45, 0.05, capMask);   // looser -> whitecaps on more peaks (noise swell has no lattice to hide)
    float crest = smoothstep(crest_foam, crest_foam + 0.20, v_crest) * gate;

    float foam = clamp(max(crest, shore) * 1.2, 0.0, 1.0);   // all foam +20% opacity (master)

    // SEE-THROUGH depth-tinted water
    float dt = smoothstep(0.0, shore_depth * 4.0, depth_v);
    vec3 base = mix(water_shallow.rgb, water_deep.rgb, dt);
    float a = mix(water_shallow.a, water_deep.a, dt);

    // FRESNEL SKY REFLECTION: grazing angles reflect the sky, so the horizon brightens + the water reads as reflective
    // (real water isn't flat-dark edge-on). Uses the ripple-perturbed normal, so the reflection shimmers with the chop.
    float fres = pow(1.0 - clamp(dot(NORMAL, VIEW), 0.0, 1.0), 4.0);
    vec3 water_col = mix(base, sky_tint.rgb, fres * reflectivity);
    a = mix(a, 1.0, fres * reflectivity * 0.6);   // a sky-reflecting surface reads more opaque at grazing angles

    ALBEDO = mix(water_col, foam_color.rgb, foam);
    ALPHA = mix(a, 1.0, foam);
    ROUGHNESS = mix(0.08, 0.9, foam);
    SPECULAR = 0.5;
    METALLIC = 0.0;
}
