shader_type spatial;
render_mode cull_disabled, diffuse_burley, specular_schlick_ggx;

// Stylised sea for the port: LAYERED (non-grid) summed-sine WAVES vertex-displaced + fbm normal detail,
// white CREST FOAM on the peaks, depth-based SHORE FOAM at the waterline, and see-through depth-tinted
// water. Waves are world-anchored so they don't slide with the plane. (master 2026-08-16)

uniform vec4 water_deep : source_color = vec4(0.07, 0.19, 0.33, 0.72);     // alpha < 1 = see-through
uniform vec4 water_shallow : source_color = vec4(0.16, 0.42, 0.54, 0.26);  // shallows very transparent -> seabed shows
uniform vec4 foam_color : source_color = vec4(0.92, 0.97, 1.0, 1.0);
uniform float wave_amp = 0.45;      // metres of vertical wave
uniform float wave_len = 14.0;      // metres for the LONGEST swell (shorter octaves derive from it)
uniform float wave_speed = 0.7;
uniform float crest_foam = 0.63;    // wave-height fraction [-1..1] above which crest foam kicks in (crest-hugging caps, not chunky blobs)
uniform float shore_depth = 1.6;    // vertical water depth (m) under which shore foam shows (a visible band at the waterline)
uniform float detail_strength = 0.25;   // fbm ripple perturbation of the surface normal
uniform sampler2D depth_tex : hint_depth_texture, repeat_disable, filter_linear;

varying vec3 v_wn;       // world-space base wave normal (big swell)
varying float v_crest;   // this vertex's normalised wave height, for crest foam
varying vec3 v_wpos;     // world position of the (displaced) surface

// --- value-noise fbm (cheap, textureless) : breaks up the surface + foam so nothing reads as a lattice ---
float hash12(vec2 p) {
    vec3 p3 = fract(vec3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}
float vnoise(vec2 p) {
    vec2 i = floor(p); vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = hash12(i), b = hash12(i + vec2(1.0, 0.0));
    float c = hash12(i + vec2(0.0, 1.0)), d = hash12(i + vec2(1.0, 1.0));
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}
float fbm(vec2 p) {
    float s = 0.0, a = 0.5;
    for (int i = 0; i < 4; i++) { s += a * vnoise(p); p *= 2.03; a *= 0.5; }
    return s;
}

// LAYERED swell: several octaves with scattered (non-axis) directions + irrational freq ratios so the
// peaks never line up into a grid. Kept large-wavelength so it doesn't alias on the coarse ocean plane.
float wave_at(vec2 p, float t) {
    float k = 6.2831853 / wave_len;
    float h = 0.0;
    h += sin(dot(p, vec2( 0.86,  0.51)) * k * 0.70 + t * 0.90) * 0.50;
    h += sin(dot(p, vec2(-0.31,  0.95)) * k * 1.07 + t * 1.15) * 0.30;
    h += sin(dot(p, vec2( 0.67, -0.74)) * k * 1.53 + t * 0.80) * 0.20;
    h += sin(dot(p, vec2(-0.99,  0.16)) * k * 1.97 + t * 1.33) * 0.12;
    return h / 1.12;   // back to ~[-1, 1]
}

void vertex() {
    vec2 wp = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xz;   // world XZ of the FLAT grid point (waves are world-anchored)
    float t = TIME * wave_speed;
    float h = wave_at(wp, t);
    VERTEX.y += h * wave_amp;
    v_crest = h;
    // world-space normal from the wave gradient (finite difference)
    float e = 1.0;
    float hx = wave_at(wp + vec2(e, 0.0), t);
    float hz = wave_at(wp + vec2(0.0, e), t);
    v_wn = normalize(vec3((h - hx) * wave_amp, e, (h - hz) * wave_amp));
    v_wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;   // AFTER displacement -> true surface world pos
}

void fragment() {
    // FINE RIPPLE DETAIL: fbm-gradient perturbs the big-wave normal. Resolution-independent (fragment space),
    // so it adds a random broken chop that the coarse vertex grid can't -- this is what kills the "grid" look.
    vec2 np = v_wpos.xz * 0.15 + vec2(TIME * 0.03, TIME * -0.02);
    float n0 = fbm(np);
    float nx = fbm(np + vec2(0.06, 0.0));
    float nz = fbm(np + vec2(0.0, 0.06));
    vec3 detail = vec3((n0 - nx), 0.0, (n0 - nz)) * detail_strength * 6.0;
    vec3 wn = normalize(v_wn + detail);
    NORMAL = normalize((VIEW_MATRIX * vec4(wn, 0.0)).xyz);

    // SHORE FOAM: reconstruct the opaque scene behind the water, foam where the water is shallow over it.
    float raw = texture(depth_tex, SCREEN_UV).r;
    vec4 vp = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, raw, 1.0);
    vp.xyz /= vp.w;
    float bed_y = (INV_VIEW_MATRIX * vec4(vp.xyz, 1.0)).y;   // world Y of the seabed behind this fragment
    float depth_v = max(v_wpos.y - bed_y, 0.0);              // vertical water depth (angle-independent)
    float shore = 1.0 - smoothstep(0.0, shore_depth, depth_v);
    shore = pow(shore, 1.2);                                 // a visible band that gathers toward the waterline (not a wide pale shelf, not a drowned thin line)
    shore *= 0.75 + 0.25 * fbm(v_wpos.xz * 0.4 + TIME * 0.15); // broken foamy edge, not a clean band

    // CREST FOAM: caps are GATED by their own independent low-freq noise so they DON'T land on every peak.
    // Caps that hit every crest trace the residual sine periodicity + make the "grid" legible (tinyclaw's fft
    // catch); scattering them onto ~half the peaks in wandering blotches stops the pattern advertising itself.
    float capMask = fbm(v_wpos.xz * 0.09 + vec2(TIME * 0.04, TIME * -0.03));
    float gate = smoothstep(0.42, 0.72, capMask);
    float crest = smoothstep(crest_foam, crest_foam + 0.14, v_crest) * gate;

    float foam = clamp(max(crest, shore), 0.0, 1.0);

    // SEE-THROUGH depth-tinted water: shallow reads lighter + very transparent, deep darker + murkier.
    float dt = smoothstep(0.0, shore_depth * 4.0, depth_v);
    vec3 base = mix(water_shallow.rgb, water_deep.rgb, dt);
    float a = mix(water_shallow.a, water_deep.a, dt);

    ALBEDO = mix(base, foam_color.rgb, foam);
    ALPHA = mix(a, 1.0, foam);
    ROUGHNESS = mix(0.08, 0.9, foam);   // glassy water, matte foam
    SPECULAR = 0.5;
    METALLIC = 0.0;
}
