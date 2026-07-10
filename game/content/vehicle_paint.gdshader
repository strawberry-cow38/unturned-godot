shader_type spatial;
render_mode cull_disabled;

// Unturned paintable vehicle shading. The body samples a tiny colour palette through its UVs; texels flagged
// paintable (alpha 0) get tinted by the vehicle's _PaintColor, while fixed texels (exhaust grey, headlight cream,
// taillight red, metal) keep their palette colour. Low-poly + flat, matching the game.
uniform sampler2D palette : filter_nearest, source_color;
uniform vec3 paint_color : source_color = vec3(1.0);

void fragment() {
    vec4 t = texture(palette, UV);
    ALBEDO = (t.a < 0.5) ? paint_color : t.rgb; // paintable panels take the paint colour; fixed parts keep theirs
    METALLIC = 0.0;
    ROUGHNESS = 0.9;
}
