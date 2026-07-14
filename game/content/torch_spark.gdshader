// Blowtorch sparks: the game's blue spark sprite, billboarded + rolled a RANDOM amount PER PARTICLE (like the
// muzzle flash rolls per shot), output HDR so the viewmodel viewport's glow (GlowHdrThreshold 1.25) blooms them =
// an emissive glow. blend_mix (not additive) so overlapping sparks stay blue instead of stacking to white.
shader_type spatial;
render_mode unshaded, cull_disabled, blend_mix, depth_draw_never, shadows_disabled, specular_disabled;

uniform sampler2D tex : source_color, filter_linear;
uniform float intensity = 2.6;   // HDR boost -> crosses the viewport GlowHdrThreshold -> blooms (the "glow")

void vertex() {
    // per-particle random roll (stable per particle slot), the muzzle-flash-style random spin master asked for
    float r = fract(sin(float(INSTANCE_ID) * 12.9898) * 43758.5453) * 6.2831853;
    float c = cos(r), s = sin(r);
    VERTEX.xy = mat2(vec2(c, -s), vec2(s, c)) * VERTEX.xy;   // roll the spark quad in its own plane
    // billboard IN-SHADER (a StandardMaterial billboard would cancel the roll): keep world pos, orient from the camera
    MODELVIEW_MATRIX = VIEW_MATRIX * mat4(INV_VIEW_MATRIX[0], INV_VIEW_MATRIX[1], INV_VIEW_MATRIX[2], MODEL_MATRIX[3]);
}

void fragment() {
    vec4 t = texture(tex, UV);
    ALBEDO = t.rgb * intensity;   // HDR blue spark -> blooms via the viewport glow
    ALPHA = t.a;
}
