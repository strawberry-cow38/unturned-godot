// Muzzle flash: a camera-facing billboard that also ROLLS about the view axis (master: each shot rolls the flash L/R
// by an amount, remembering the last). StandardMaterial billboard cancels node rotation, so we billboard in-shader and
// rotate the quad's local XY by the `roll` uniform (set per shot). Additive, unshaded, no depth write -- like the star.
shader_type spatial;
render_mode unshaded, cull_disabled, blend_add, depth_draw_never, shadows_disabled, specular_disabled;

uniform sampler2D tex : source_color, filter_linear_mipmap;
uniform float roll = 0.0;
uniform float intensity = 2.6;   // HDR boost so the additive flash crosses the viewport glow threshold and blooms

void vertex() {
    float c = cos(roll);
    float s = sin(roll);
    VERTEX.xy = mat2(vec2(c, -s), vec2(s, c)) * VERTEX.xy;   // roll the flash quad in its own plane
    // billboard: keep the world position, take orientation from the camera
    MODELVIEW_MATRIX = VIEW_MATRIX * mat4(INV_VIEW_MATRIX[0], INV_VIEW_MATRIX[1], INV_VIEW_MATRIX[2], MODEL_MATRIX[3]);
}

void fragment() {
    vec4 t = texture(tex, UV);
    ALBEDO = t.rgb * intensity;   // HDR so the bright core exceeds the glow HDR threshold -> blooms
    ALPHA = t.a;
}
