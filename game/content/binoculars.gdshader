// BINOCULARS lens disc: a disc sat in each eyepiece of the CARRIED binoculars (Viewmodel.AddHeldLens). It shows the ONE
// magnified render (BinocularsView's second camera at FOV/zoom) where it sits on screen -- the scope's two-render PiP --
// so the image runs continuously across both lenses and each eyepiece cuts its own circle (master 2026-09-05).
// The disc lives in the ARMS viewport, whose Environment tonemaps with ACES like the world -- but `view` is ALREADY the
// world's tonemapped output, so drawing it straight got tonemapped twice (washed white). The exact inverse of Godot's
// ACES fit (tonemap.glsl: exposure bias 1.8 folded into rgb_to_rrt, the rational curve, odt_to_rgb, white 1.0) is
// applied here so the viewport's own pass lands back on the original colours.
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_always;
uniform sampler2D view : source_color, filter_linear;
uniform float vignette = 0.30;   // darkening toward the rim so the glass reads as glass
uniform bool flip_y = false;
uniform bool debug = false;   // UG_LENSDBG=1: flat magenta, to see the disc itself
uniform vec2 over = vec2(1.0, 1.0);   // the viewport this disc is drawn in is OVERSIZED vs the screen by this much (the arms viewport: 1.15 x 1.45), composited centred 1:1

vec3 aces_inverse(vec3 y) {
    const float kA = 0.0245786, kB = 0.000090537, kC = 0.983729, kD = 0.432951, kE = 0.238081, bias = 1.8;   // (E is a built-in constant in Godot's shader language)
    const mat3 rgb_to_rrt = mat3(vec3(0.59719 * bias, 0.35458 * bias, 0.04823 * bias),
                                 vec3(0.07600 * bias, 0.90834 * bias, 0.01566 * bias),
                                 vec3(0.02840 * bias, 0.13383 * bias, 0.83777 * bias));
    const mat3 odt_to_rgb = mat3(vec3(1.60475, -0.53108, -0.07367),
                                 vec3(-0.10208, 1.10813, -0.00605),
                                 vec3(-0.00327, -0.07276, 1.07602));
    float w = 1.0 * bias;
    float white_tm = (w * (w + kA) - kB) / (w * (kC * w + kD) + kE);
    vec3 v = (y * white_tm) * inverse(odt_to_rgb);            // Godot multiplies ROW-vector style (`color *= odt_to_rgb`), so the inverse goes on the same side
    // (c*(c+A)-B)/(c*(C c+D)+E) = v  ->  (1 - vC) c^2 + (A - vD) c - (B + vE) = 0
    vec3 qa = 1.0 - v * kC, qb = kA - v * kD, qc = -(kB + v * kE);
    vec3 c = (-qb + sqrt(max(qb * qb - 4.0 * qa * qc, vec3(0.0)))) / (2.0 * qa);
    return max(c * inverse(rgb_to_rrt), vec3(0.0));       // `color *= rgb_to_rrt` in tonemap.glsl
}

void fragment() {
    vec2 c = UV - 0.5;
    float r = length(c) * 2.0;   // 0 at the centre, 1 at the rim
    if (r > 1.0) discard;
    vec2 suv = (SCREEN_UV - 0.5) * over + 0.5;   // -> true screen UV
    if (flip_y) suv.y = 1.0 - suv.y;
    vec3 col = debug ? vec3(1.0, 0.0, 1.0) : aces_inverse(clamp(texture(view, suv).rgb, 0.0, 0.999));
    col *= 1.0 - vignette * smoothstep(0.6, 1.0, r);
    ALBEDO = col;
}
