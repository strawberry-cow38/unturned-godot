// BINOCULARS lens disc (BinocularsView): a disc sat in each eyepiece of the held binoculars model. It shows the ONE
// magnified render (`view`, a second camera on the player's at FOV/zoom) where it sits on screen, so the image runs
// continuously across both lenses and each eyepiece cuts its own circle out of it (master 2026-09-05).
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_always;
uniform sampler2D view : filter_linear;
uniform float vignette = 0.30;   // darkening toward the rim so the glass reads as glass
uniform bool flip_y = false;

void fragment() {
    vec2 c = UV - 0.5;
    float r = length(c) * 2.0;   // 0 at the centre, 1 at the rim
    if (r > 1.0) discard;
    vec2 suv = SCREEN_UV;
    if (flip_y) suv.y = 1.0 - suv.y;
    vec3 col = texture(view, suv).rgb;
    col *= 1.0 - vignette * smoothstep(0.6, 1.0, r);
    ALBEDO = col;
}
