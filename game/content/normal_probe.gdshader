shader_type spatial;
render_mode cull_disabled, unshaded;

// A DIAGNOSTIC, not a look. Brightness = how much this surface faces world UP, so an up-facing face must be
// WHITE and a vertical one BLACK -- an absolute answer rather than a comparison against a previous render.
//
// Written after two ambiguous renders in one day: a car-lift platform that slid sideways looked exactly like
// one that rose, and a vehicle body that was lit from inside looked like dark paint. Both were settled in one
// frame by painting the normal instead of the material. FRONT_FACING is honoured because cull_disabled draws
// back faces and Godot does not flip NORMAL for them in a custom shader.
// ⚠ DELIBERATELY DOES NOT CORRECT FOR FRONT_FACING. Correcting makes the probe self-healing: it would report
// a back-facing mesh as fine, which is precisely the question being asked. This measures what an ordinary
// StandardMaterial3D actually receives, so a white top means the mesh is genuinely right way out.
void fragment() {
    vec3 n = normalize(NORMAL);
    vec3 up = normalize((VIEW_MATRIX * vec4(0.0, 1.0, 0.0, 0.0)).xyz);
    ALBEDO = vec3(max(dot(n, up), 0.0));
}
