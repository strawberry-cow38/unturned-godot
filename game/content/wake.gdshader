shader_type spatial;
render_mode cull_disabled, unshaded, depth_draw_never, shadows_disabled;

// Foam wake for boats/ships. Same ocean-foam noise as content/water.gdshader (Hoskins hash +
// gradient-noise fbm) so the wake reads as the same water. Drawn on a world-space ribbon that
// WakeTrail.cs rebuilds behind the moving hull: UV.x runs across the ribbon (0..1), UV.y runs
// from the stern (0) to the far tail (1), COLOR.a carries the per-vertex age fade.
uniform vec4 foam_color : source_color = vec4(0.92, 0.97, 1.0, 1.0);
uniform float noise_scale = 0.6;
uniform float scroll_speed = 0.35;
uniform float body_lo = 0.42;
uniform float body_hi = 0.80;

float hashv(vec2 p){ vec3 p3 = fract(vec3(p.xyx) * 0.1031); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.x + p3.y) * p3.z); }
vec2 grad2(vec2 ip){ float h = hashv(ip) * 6.2831853; return vec2(cos(h), sin(h)); }
float gnoise(vec2 p){
    vec2 i = floor(p), f = fract(p);
    vec2 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
    float a = dot(grad2(i), f);
    float b = dot(grad2(i + vec2(1.0, 0.0)), f - vec2(1.0, 0.0));
    float c = dot(grad2(i + vec2(0.0, 1.0)), f - vec2(0.0, 1.0));
    float d = dot(grad2(i + vec2(1.0, 1.0)), f - vec2(1.0, 1.0));
    return mix(mix(a, b, u.x), mix(c, d, u.x), u.y) * 0.8 + 0.5;
}
float fbm5(vec2 p){ float s = 0.0, a = 0.5; for (int i = 0; i < 5; i++){ s += a * (gnoise(p) - 0.5); p *= 2.03; a *= 0.5; } return s + 0.5; }

varying vec2 v_uv;
varying vec3 v_wpos;
varying float v_fade;
void vertex(){
    v_uv = UV;
    v_wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    v_fade = COLOR.a;
}
void fragment(){
    float n = fbm5(v_wpos.xz * noise_scale + vec2(TIME * scroll_speed, TIME * scroll_speed * 0.6));
    float across = abs(v_uv.x * 2.0 - 1.0);   // 0 at the centreline, 1 at the ribbon edges
    float behind = v_uv.y;                     // 0 at the stern, 1 at the far tail
    float churn = smoothstep(body_lo, body_hi, n);                             // ocean-foam noise, same as the sea
    float side  = smoothstep(1.0, 0.70, across);                              // feather the ribbon sides -> soft blend, no hard edge
    float tail  = 1.0 - smoothstep(0.45, 1.0, behind);                        // dissolve toward the tail -> no hard base
    float core  = (1.0 - smoothstep(0.0, 0.55, behind)) * (1.0 - across * 0.5); // brighter prop-wash just behind the stern
    float foam  = clamp(max(churn * 0.85, core * 0.7) * side * tail, 0.0, 1.0);
    ALBEDO = foam_color.rgb;
    ALPHA = foam * v_fade * foam_color.a;
}
