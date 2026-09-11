shader_type spatial;
render_mode cull_disabled;

// Unturned paintable vehicle shading. The body samples a tiny colour palette through its UVs; texels flagged
// paintable (alpha 0) get tinted by the vehicle's _PaintColor, while fixed texels (exhaust grey, headlight cream,
// taillight red, metal) keep their palette colour. Low-poly + flat, matching the game.
uniform sampler2D palette : filter_nearest, source_color;
uniform vec3 paint_color : source_color = vec3(1.0);

void fragment() {
    // ⚠ THE NORMAL ARRIVES INVERTED, AND THIS IS WHY EVERY VEHICLE LOOKED PERMANENTLY IN SHADOW
    // (strawberry 2026-09-11: "the lighting on vehicles is really dark ... like they are always in shadow").
    //
    // The bodies are drawn BACK-FACE: the meshes load raw (ContentProvider.ParseObj does no axis negation,
    // matching ObjMesh's CONV=1), so Unity's left-handed winding lands in Godot's right-handed space reversed.
    // cull_disabled hides that -- the surface still draws -- but Godot does NOT flip NORMAL for a back face in
    // a custom spatial shader the way it does for a built-in material. So every fragment on every car was lit
    // with a normal pointing INTO the body: the sun was always on the wrong side of the panel and only ambient
    // got through. Uniformly dark, with no gradient between a bonnet and a door, which is exactly what
    // "always in shadow" looks like.
    //
    // Diagnosed by rendering dot(NORMAL, worldUp) as brightness: the whole body came back BLACK including the
    // flat bonnet and the roll-bar top. With the flip below those go white and the vertical panels stay dark,
    // which is the correct answer for that probe.
    NORMAL = FRONT_FACING ? NORMAL : -NORMAL;
    vec4 t = texture(palette, UV);
    ALBEDO = (t.a < 0.5) ? paint_color : t.rgb; // paintable panels take the paint colour; fixed parts keep theirs
    METALLIC = 0.0;
    ROUGHNESS = 0.9;
}
