shader_type spatial;
render_mode cull_disabled;

// Unturned paintable vehicle shading. The body samples a tiny colour palette through its UVs; texels flagged
// paintable (alpha 0) get tinted by the vehicle's _PaintColor, while fixed texels (exhaust grey, headlight cream,
// taillight red, metal) keep their palette colour. Low-poly + flat, matching the game.
uniform sampler2D palette : filter_nearest, source_color;
uniform vec3 paint_color : source_color = vec3(1.0);

void fragment() {
    // NO manual back-face flip (removed 2026-10-04). History, because it was right once and then wrong:
    // the bodies used to load reverse-wound, so every visible panel was a BACK face, and cull_disabled makes Godot
    // negate a back face's normal before fragment() runs (DO_SIDE_CHECK) -- which pointed every outward normal
    // into the body ("always in shadow", strawberry 2026-09-11). A manual `NORMAL = FRONT_FACING ? NORMAL : -NORMAL`
    // cancelled that, so it fixed the cars, but for the opposite reason to the one its comment gave (it claimed
    // Godot does NOT flip for custom shaders; it does). Since ContentProvider.ParseObj orients each triangle the
    // panels are FRONT faces, the line was a no-op on them, and on the genuinely two-sided case (the inside of a
    // hull through a window) it undid Godot's correct flip and lit the inside from the wrong side.
    vec4 t = texture(palette, UV);
    ALBEDO = (t.a < 0.5) ? paint_color : t.rgb; // paintable panels take the paint colour; fixed parts keep theirs
    METALLIC = 0.0;
    ROUGHNESS = 0.9;
}
