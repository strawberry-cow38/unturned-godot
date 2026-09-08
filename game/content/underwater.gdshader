// SUBMERGED CAMERA (strawberry 2026-09-08: "build an underwater submerged camera shader").
//
// A SPATIAL fullscreen pass, not a canvas_item one, and that is forced rather than chosen: `hint_depth_texture`
// is rejected outright in a canvas_item shader ("not supported in 'canvas_item' shaders") and the depth is the
// whole point -- without it this is a flat blue filter over the screen instead of water, because what makes water
// read as water is that things get bluer and murkier THE FURTHER AWAY THEY ARE.
//
// Drawn on a 2x2 QuadMesh parented to the camera; `POSITION = vec4(VERTEX, 1.0)` puts its corners straight into
// clip space, so it covers the screen exactly whatever the FOV or aspect. Depth test off and drawn last so no
// piece of world geometry can occlude it.
//
// Four things happen to light in water and all four are here:
//   * it goes BLUE-GREEN, because water absorbs red first -- distance-dependent, hence the depth buffer;
//   * it gets MURKY with distance, everything fading into the water colour;
//   * it WOBBLES, because neither the surface above nor the water in front of you is flat;
//   * the edges go dark, because you are looking down a tube of water.
shader_type spatial;
render_mode unshaded, fog_disabled, depth_draw_never, depth_test_disabled, cull_disabled;

uniform sampler2D screen_tex : hint_screen_texture, repeat_disable, filter_linear;
uniform sampler2D depth_tex : hint_depth_texture, repeat_disable, filter_nearest;

uniform vec3 water_tint : source_color = vec3(0.10, 0.34, 0.47);   // the colour distance fades TOWARD
uniform float murk = 0.11;           // per-metre absorption; the murkier the water the less you see
// YOU ARE INSIDE THE MEDIUM, and that is the difference between "underwater" and "blue fog". Distance
// absorption alone leaves anything a couple of metres away almost untouched -- the first render came out as a
// crisp road under a blue sky, because a 2 m path through water absorbs almost nothing. Real submerged vision
// also carries light SCATTERED into the eye from every direction, which tints the whole field of view at once
// regardless of how near the subject is. This is that term: a floor under the distance fade, not a replacement
// for it, so near things are clearly in water and far things still disappear into it.
uniform float base_tint = 0.30;
uniform float submersion = 1.0;      // 0 at the waterline .. 1 fully under -- fades the whole effect in
uniform float depth_below = 0.0;     // metres the eye is under the surface -> deeper is darker and bluer
uniform float wobble = 0.006;        // screen-space refraction amplitude
uniform float wobble_speed = 1.4;
uniform float vignette = 0.38;
uniform float red_loss = 0.55;       // how much of the red channel the water has eaten by full murk

void vertex() {
    POSITION = vec4(VERTEX, 1.0);   // 2x2 quad -> clip space directly: exact fullscreen at any FOV/aspect
}

void fragment() {
    vec2 uv = SCREEN_UV;

    // WOBBLE. Two crossed sine trains rather than one, so it does not read as a single scrolling bar. Scaled by
    // submersion so it eases in at the waterline instead of snapping on.
    float t = TIME * wobble_speed;
    vec2 warp = vec2(
        sin(uv.y * 22.0 + t * 1.7) + 0.6 * sin(uv.y * 41.0 - t * 2.3),
        cos(uv.x * 19.0 + t * 1.3) + 0.6 * cos(uv.x * 37.0 + t * 2.9)
    ) * wobble * submersion;
    vec2 wuv = clamp(uv + warp, vec2(0.001), vec2(0.999));   // a warp off the edge would sample garbage

    vec3 col = texture(screen_tex, wuv).rgb;

    // View-space distance to whatever is behind this pixel. Exact rather than a fudge curve: unproject the depth
    // sample through INV_PROJECTION_MATRIX, which a spatial shader actually has. The SKY sits at the far plane and
    // comes back enormous, which is what we want -- it should be fully absorbed into the water colour.
    float d = texture(depth_tex, wuv).x;
    vec4 view = INV_PROJECTION_MATRIX * vec4(vec3(wuv * 2.0 - 1.0, d), 1.0);
    float dist = -view.z / max(view.w, 1e-6);

    // ABSORPTION, Beer-Lambert: what survives falls off exponentially with the water it came through, and red
    // falls off fastest -- which is why deep water is blue rather than merely dim.
    float fade = 1.0 - (1.0 - base_tint) * exp(-murk * max(dist, 0.0));   // never falls below base_tint, even at zero distance
    vec3 absorbed = col * vec3(1.0 - red_loss * fade, 1.0 - 0.22 * fade, 1.0);
    col = mix(absorbed, water_tint, fade);

    // DEPTH of the eye itself: deeper means less light left and more of it already eaten before it reached you.
    // Saturates by ~18 m so a deep dive goes dim, not pure black.
    float deep = clamp(depth_below / 18.0, 0.0, 1.0);
    col = mix(col, water_tint * 0.45, deep * 0.55);

    // looking down a tube of water
    float v = distance(uv, vec2(0.5));
    col *= 1.0 - vignette * submersion * smoothstep(0.25, 0.85, v);

    ALBEDO = mix(texture(screen_tex, uv).rgb, col, clamp(submersion, 0.0, 1.0));
}
