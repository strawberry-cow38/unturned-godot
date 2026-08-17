shader_type spatial;

// CAUSTICS projected in WORLD SPACE onto surfaces below the water plane, seeded from the SAME value-noise as the
// water waves (content/water.gdshader) so the light web dances in sync with the swell. Sampled at each fragment's
// world XZ = a top-down projection; only where underwater; faded with depth. Emitted so it reads as focused light
// on the seabed even in shadow. (master 2026-08-16)

uniform vec4 base_color : source_color = vec4(0.66, 0.58, 0.42, 1.0);   // the surface's own albedo (sand / seabed)
uniform float sea_level = 0.0;
uniform float caustic_scale = 0.11;     // 1/world-units -> caustic cell size
uniform float caustic_speed = 0.25;
uniform vec3  caustic_tint : source_color = vec3(0.55, 0.9, 1.0);
uniform float caustic_strength = 0.5;
uniform float caustic_depth = 9.0;      // metres of depth the caustics fade out over

varying vec3 v_wpos;

// GRADIENT (Perlin) noise -- unlike value noise it has NO axis-aligned cell artifacts (value is 0 at every lattice
// point, random gradients interpolated between), so the caustic web doesn't read as blocks. Seed shared with the water.
float hashv(vec2 p) { return fract(sin(p.x * 127.1 + p.y * 311.7) * 43758.5453); }
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
float fbm(vec2 p) { float s = 0.0, a = 0.5; for (int i = 0; i < 4; i++) { s += a * gnoise(p); p *= 2.03; a *= 0.5; } return s; }

// caustics = the interference RIDGES of two scrolling noise fields (bright thin curves where they cross = focused light)
float caustics(vec2 p, float t) {
    float a = fbm(p + vec2(t, t * 0.4));
    float b = fbm(p * 1.31 + vec2(-t * 0.7, t * 0.55) + 17.3);
    float c = 1.0 - abs(a - b) * 4.0;
    return pow(clamp(c, 0.0, 1.0), 4.0);   // thin bright curves, dark between (a caustic WEB, not a bright sheet)
}

void vertex() {
    v_wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
    ALBEDO = base_color.rgb;
    ROUGHNESS = 1.0;
    float depth = sea_level - v_wpos.y;   // how far below the water surface this fragment sits
    if (depth > 0.0) {
        vec2 cp = mat2(vec2(0.87, 0.5), vec2(-0.5, 0.87)) * (v_wpos.xz * caustic_scale);   // rotate the noise lattice off-axis
        cp += 0.8 * (vec2(fbm(cp * 0.5), fbm(cp * 0.5 + 7.0)) - 0.5);   // domain warp -> the caustic web wanders instead of gridding
        float caust = caustics(cp, TIME * caustic_speed);
        caust = max(caust, caustics(cp * 1.6 + 9.0, -TIME * caustic_speed * 0.8));   // a counter-scrolling layer for a livelier shimmer
        // fade IN just under the waterline, fade OUT with depth (real caustics are strongest in the shallows)
        float fade = clamp(depth / 0.4, 0.0, 1.0) * (1.0 - clamp(depth / caustic_depth, 0.0, 1.0));
        EMISSION = caustic_tint * caust * caustic_strength * fade;
    }
}
