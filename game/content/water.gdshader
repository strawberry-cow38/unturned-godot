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
uniform float swell_fu = 0.09;        // noise freq ALONG travel (higher = tighter crest spacing)
uniform float swell_fw = 0.030;       // noise freq ALONG the crest (lower = longer crests); fu/fw = 3:1 anisotropy (master's pick)
uniform float swell_speed = 3.0;
uniform float crest_foam = 0.25;      // swell-height fraction above which crest foam kicks in (lower = more whitecaps)
uniform float shore_depth = 2.2;      // vertical water depth (m) under which shore foam shows (wider = fuller shore band)
uniform float detail_strength = 0.30; // fbm fine-ripple perturbation of the normal
uniform vec4 sky_tint : source_color = vec4(0.52, 0.67, 0.86, 1.0);  // colour reflected at grazing angles (the sky)
uniform float reflectivity = 0.55;    // how strongly the sky shows at grazing angles (Fresnel)
uniform sampler2D depth_tex : hint_depth_texture, repeat_disable, filter_linear;

varying vec3 v_wn;       // world-space base swell normal
varying float v_crest;   // normalised swell height ~[-1,1], for crest foam
varying vec3 v_wpos;     // world position of the displaced surface

// sin-hash value-noise fbm -- IDENTICAL formula in wavemap.py + WaveField.cs so all three agree
float hashv(vec2 p) { return fract(sin(p.x * 127.1 + p.y * 311.7) * 43758.5453); }
float vnoise(vec2 p) {
    vec2 i = floor(p), f = fract(p); f = f * f * (3.0 - 2.0 * f);
    float a = hashv(i), b = hashv(i + vec2(1.0, 0.0)), c = hashv(i + vec2(0.0, 1.0)), d = hashv(i + vec2(1.0, 1.0));
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}
float fbm5(vec2 p) { float s = 0.0, a = 0.5; for (int i = 0; i < 5; i++) { s += a * (vnoise(p) - 0.5); p *= 2.03; a *= 0.5; } return s; }
float fbm3(vec2 p) { float s = 0.0, a = 0.5; for (int i = 0; i < 3; i++) { s += a * (vnoise(p) - 0.5); p *= 2.03; a *= 0.5; } return s / 0.4375; }

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
    float e = 1.0;
    float hx = swell_at(wp + vec2(e, 0.0), tphase);
    float hz = swell_at(wp + vec2(0.0, e), tphase);
    v_wn = normalize(vec3((h - hx) * swell_amp, e, (h - hz) * swell_amp));
    v_wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
    // FINE RIPPLE DETAIL: fbm-gradient perturbs the swell normal (fragment space, so it doesn't need dense geometry)
    vec2 np = v_wpos.xz * 0.15 + vec2(TIME * 0.03, TIME * -0.02);
    float n0 = fbm5(np);
    float nx = fbm5(np + vec2(0.06, 0.0));
    float nz = fbm5(np + vec2(0.0, 0.06));
    vec3 detail = vec3((n0 - nx), 0.0, (n0 - nz)) * detail_strength * 6.0;
    vec3 wn = normalize(v_wn + detail);
    NORMAL = normalize((VIEW_MATRIX * vec4(wn, 0.0)).xyz);

    // SHORE FOAM: reconstruct the opaque scene behind the water, foam where the water is shallow over it.
    float raw = texture(depth_tex, SCREEN_UV).r;
    vec4 vp = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, raw, 1.0);
    vp.xyz /= vp.w;
    float bed_y = (INV_VIEW_MATRIX * vec4(vp.xyz, 1.0)).y;
    float depth_v = max(v_wpos.y - bed_y, 0.0);
    float shore = 1.0 - smoothstep(0.0, 0.7, depth_v);        // foam band at the waterline (a touch longer up the beach; still not the whole shelf)
    shore *= 0.45 + 0.15 * fbm5(v_wpos.xz * 0.4 + TIME * 0.15);

    // CREST FOAM: gated by its own noise so caps scatter (belt-and-suspenders; noise swell already won't lattice)
    float capMask = fbm5(v_wpos.xz * 0.09 + vec2(TIME * 0.04, TIME * -0.03));
    float gate = smoothstep(-0.45, 0.05, capMask);   // looser -> whitecaps on more peaks (noise swell has no lattice to hide)
    float crest = smoothstep(crest_foam, crest_foam + 0.20, v_crest) * gate;

    float foam = clamp(max(crest, shore), 0.0, 1.0);

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
