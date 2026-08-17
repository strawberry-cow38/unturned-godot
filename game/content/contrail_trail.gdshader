// contrail_trail.gdshader -- world-space vapour trail ribbon (driven by Vehicle.cs Contrail: an ImmediateMesh
// rebuilt each frame from a ring buffer of world positions). Vertex COLOR.a carries the age/speed fade set per
// point; UV.x runs 0..1 ACROSS the ribbon so the side edges feather out. Soft white cloud, alpha-blended.
shader_type spatial;
render_mode blend_mix, unshaded, cull_disabled, depth_draw_never, shadows_disabled, fog_disabled;

uniform vec3 u_color : source_color = vec3(0.93, 0.96, 1.0);

void fragment() {
	float edge = 1.0 - abs(UV.x * 2.0 - 1.0);   // 0 at the two side edges, 1 down the centre
	edge = smoothstep(0.0, 0.5, edge);
	ALBEDO = u_color;
	ALPHA = COLOR.a * edge * 0.22;   // wispy / faint vapour (master: way more transparent)
}
