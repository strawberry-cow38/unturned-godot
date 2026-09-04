// WELL SHAFT (strawberry 2026-09-04): "a shader for the bottom of the well prop, that looks like the walls extend
// infinitely into the ground, but its actually a flat disc at the bottom of the well." The disc sits in the prop's
// object space (XY plane, shaft axis = Z, DOWN = -Z). Each fragment casts the eye ray from the disc into an
// infinite cylinder of the ring's inner radius and shades the wall where the ray would strike it: stone courses
// by depth, daylight from the mouth dying with depth, black past the fade. No geometry below the disc exists.
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_opaque;

uniform vec3 wall_color : source_color = vec3(0.42, 0.40, 0.37);   // the ring's own stone swatch (sampled off the prop atlas at build)
uniform float radius = 0.78;        // inner radius of the shaft (object units = metres)
uniform float fade_depth = 9.0;     // metres of shaft before the light is gone
uniform float course_height = 0.33; // one course of stones per this many metres
uniform float max_view_dist = 10.0; // "dont bother rendering the disc past like 10m since its impossible to see"

varying vec3 v_obj_pos;
varying vec3 v_obj_cam;

float hash21(vec2 p) { p = fract(p * vec2(123.34, 456.21)); p += dot(p, p + 45.32); return fract(p.x * p.y); }

void vertex() {
	v_obj_pos = VERTEX;
	v_obj_cam = (inverse(MODEL_MATRIX) * vec4(CAMERA_POSITION_WORLD, 1.0)).xyz;
}

void fragment() {
	vec3 world_pos = (MODEL_MATRIX * vec4(v_obj_pos, 1.0)).xyz;
	if (distance(CAMERA_POSITION_WORLD, world_pos) > max_view_dist) discard;   // a dark dot at 10 m: not worth the ray

	vec3 ro = v_obj_pos;
	vec3 rd = normalize(v_obj_pos - v_obj_cam);
	vec3 col = vec3(0.0);
	// intersect the ray with the infinite cylinder x^2 + y^2 = r^2 (axis Z); we start inside, take the exit hit
	float a = dot(rd.xy, rd.xy);
	if (a > 1e-6 && rd.z < 0.0) {
		float b = 2.0 * dot(ro.xy, rd.xy);
		float c = dot(ro.xy, ro.xy) - radius * radius;
		float disc = b * b - 4.0 * a * c;
		if (disc >= 0.0) {
			float t = (-b + sqrt(disc)) / (2.0 * a);
			if (t > 0.0) {
				vec3 hit = ro + rd * t;
				float depth = max(0.0, ro.z - hit.z);            // metres below the disc
				float ang = atan(hit.y, hit.x) / (2.0 * PI) + 0.5;   // 0..1 around the shaft
				// stone courses: rows of blocks, every other row offset by half a block, mortar lines between
				float row = floor(depth / course_height);
				float blocks = 10.0;
				float u = ang * blocks + (mod(row, 2.0) > 0.5 ? 0.5 : 0.0);
				float fu = fract(u), fv = fract(depth / course_height);
				float mortar = (fu < 0.06 || fu > 0.94 || fv < 0.10) ? 0.55 : 1.0;
				float shade = 0.85 + 0.30 * hash21(vec2(floor(u), row));     // per-block tone variation
				// the wall facing the light (the mouth is above) picks up more; the ray's grazing angle darkens the far side a little
				float light = exp(-depth / fade_depth * 2.2);
				col = wall_color * shade * mortar * light;
			}
		}
	}
	ALBEDO = col;
}
