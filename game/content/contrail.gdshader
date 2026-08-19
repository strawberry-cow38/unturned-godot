// contrail.gdshader -- wingtip vapour contrail. Drawn on a thin, long cone that streams AFT from each wingtip
// (narrow at the tip -Y, diffusing wider aft +Y). Alpha-blended white vapour (a cloud, NOT a glow -> blend_mix,
// not additive). Born right at the wingtip, billows, then dissipates to nothing down the trail. Driven from
// Vehicle.cs: u_speed (0..1) fades the whole trail IN with airspeed so it only shows when you're going fast.
shader_type spatial;
render_mode blend_mix, cull_disabled, unshaded, depth_draw_never, shadows_disabled, fog_disabled;

uniform float u_speed : hint_range(0.0, 1.0) = 1.0;   // airspeed fade-in (from code)
uniform vec3  u_color : source_color = vec3(0.93, 0.96, 1.00);  // faint blue-white vapour
uniform float u_opacity = 0.55;      // max opacity at full speed
uniform float u_seed = 0.0;          // per-wingtip phase
uniform float u_length = 14.0;       // cone length, to normalise the axial coord

varying vec3  v_pos;     // model-local vertex
varying float v_axial;   // 0 at the wingtip -> 1 at the dissipating aft end

float hash13(vec3 p) {
	p = fract(p * 0.1031);
	p += dot(p, p.yzx + 33.33);
	return fract((p.x + p.y) * p.z);
}
float vnoise(vec3 x) {
	vec3 i = floor(x);
	vec3 f = fract(x);
	f = f * f * (3.0 - 2.0 * f);
	return mix(mix(mix(hash13(i+vec3(0,0,0)), hash13(i+vec3(1,0,0)), f.x),
	               mix(hash13(i+vec3(0,1,0)), hash13(i+vec3(1,1,0)), f.x), f.y),
	           mix(mix(hash13(i+vec3(0,0,1)), hash13(i+vec3(1,0,1)), f.x),
	               mix(hash13(i+vec3(0,1,1)), hash13(i+vec3(1,1,1)), f.x), f.y), f.z);
}
float fbm(vec3 p) {
	float a = 0.5;
	float s = 0.0;
	for (int k = 0; k < 4; k++) { s += a * vnoise(p); p *= 2.02; a *= 0.5; }
	return s;
}

void vertex() {
	v_pos = VERTEX;
	v_axial = clamp((VERTEX.y + u_length * 0.5) / u_length, 0.0, 1.0);
}

void fragment() {
	float t = v_axial;

	// slow-drifting turbulence -> the vapour billows and breaks into puffs down the trail
	vec3 np = v_pos * 0.55 + vec3(u_seed * 7.0);
	np.y -= TIME * 0.5;
	float n = fbm(np);

	// density: a quick fade-in right at the wingtip, then a long dissipation to nothing at the aft end
	float birth = smoothstep(0.0, 0.06, t);
	float diss  = smoothstep(1.0, 0.12, t);
	float puff  = mix(0.55, 1.0, n);
	float alpha = birth * diss * puff;

	// break the tail into gaps so it reads as puffy vapour, not a solid tube
	alpha *= mix(1.0, smoothstep(0.15, 0.7, n), smoothstep(0.25, 1.0, t));

	// soft radial edges: densest where a wall faces the camera, dissolving at the grazing silhouette
	float facing = abs(dot(normalize(NORMAL), normalize(VIEW)));
	alpha *= 0.32 + 0.68 * facing;

	ALBEDO = u_color;
	ALPHA  = clamp(alpha, 0.0, 1.0) * u_opacity * u_speed;
}
