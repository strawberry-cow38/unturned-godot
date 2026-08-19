// afterburner.gdshader -- jet afterburner flame.
// Drawn on a hollow cone shell (wide at the nozzle -Y, tapering to a point +Y = aft). The shell is
// additive + unshaded so it reads as glowing gas, not a solid cone. Everything interesting is procedural:
//  * axial gradient  : blue-white HOT core at the nozzle -> saturated orange body -> deep smoky-red tip
//  * turbulent FBM    : scrolls nozzle->tip so the flame churns/licks (not a static cone) + flickers
//  * shock diamonds   : the standing bright bands a real afterburner shows in its near field
//  * wispy tip breakup : the tail dissolves into noisy tongues instead of a hard cone edge
//  * rim density      : grazing angles glow a touch more -> fake volumetric silhouette
// Driven from Vehicle.cs: u_throttle (0..1 spool) scales intensity/core size; per-burner u_seed
// de-syncs the two engines. Colours are source_color (sRGB->linear) so they can be re-tinted (e.g. retail purple).
shader_type spatial;
render_mode blend_add, unshaded, cull_disabled, depth_draw_never, shadows_disabled, fog_disabled;

uniform float u_throttle : hint_range(0.0, 1.0) = 1.0;   // engine spool (from code)
uniform vec3  u_core_color : source_color = vec3(0.55, 0.12, 0.95);  // PURPLE core (nozzle)
uniform vec3  u_mid_color  : source_color = vec3(0.15, 0.45, 1.00);  // BLUE mid
uniform vec3  u_tip_color  : source_color = vec3(1.00, 0.50, 0.10);  // ORANGE tip
uniform float u_seed = 0.0;          // per-burner phase so the two flames don't animate in lockstep
uniform float u_flow = 3.2;          // how fast the turbulence scrolls nozzle->tip
uniform float u_noise_scale = 2.4;
uniform float u_intensity = 1.6;     // additive emission multiplier (HDR -> blooms at the core)
uniform float u_height = 2.4;        // cone mesh height, to normalise the axial coord

varying vec3  v_pos;     // model-local vertex (stable under node scale -> pattern doesn't stretch)
varying float v_axial;   // 0 at nozzle (-Y) -> 1 at tip (+Y)

float hash13(vec3 p) {
	p = fract(p * 0.1031);
	p += dot(p, p.yzx + 33.33);
	return fract((p.x + p.y) * p.z);
}
float vnoise(vec3 x) {
	vec3 i = floor(x);
	vec3 f = fract(x);
	f = f * f * (3.0 - 2.0 * f);
	float n000 = hash13(i + vec3(0,0,0));
	float n100 = hash13(i + vec3(1,0,0));
	float n010 = hash13(i + vec3(0,1,0));
	float n110 = hash13(i + vec3(1,1,0));
	float n001 = hash13(i + vec3(0,0,1));
	float n101 = hash13(i + vec3(1,0,1));
	float n011 = hash13(i + vec3(0,1,1));
	float n111 = hash13(i + vec3(1,1,1));
	return mix(mix(mix(n000, n100, f.x), mix(n010, n110, f.x), f.y),
	           mix(mix(n001, n101, f.x), mix(n011, n111, f.x), f.y), f.z);
}
float fbm(vec3 p) {
	float a = 0.5;
	float s = 0.0;
	for (int k = 0; k < 4; k++) { s += a * vnoise(p); p *= 2.02; a *= 0.5; }
	return s;
}

void vertex() {
	v_pos = VERTEX;
	v_axial = clamp((VERTEX.y + u_height * 0.5) / u_height, 0.0, 1.0);
}

void fragment() {
	float t = v_axial;

	// turbulence flowing nozzle -> tip (flame is blown aft), plus a finer second layer for detail
	vec3 np = v_pos * u_noise_scale + vec3(u_seed * 11.3);
	np.y -= TIME * u_flow;
	float n  = fbm(np);
	float n2 = fbm(np * 2.1 + vec3(5.2, TIME * 0.7, 1.3));

	// wobble the axial coordinate so the colour bands + tip lick and waver instead of forming clean rings
	float tt = clamp(t + (n - 0.5) * 0.38 * (0.4 + t), 0.0, 1.0);

	// 3-stop gradient along the flame: PURPLE core (nozzle) -> BLUE -> ORANGE tip. NB the orange only reads at the
	// very tip -- blue+orange are complementary, so an additive flame washes their overlap toward white (a true
	// tri-colour needs the orange on a SEPARATE OUTER shell, not blended into the blue core).
	float mid = 0.40;
	vec3 col = (tt < mid)
		? mix(u_core_color, u_mid_color, tt / mid)
		: mix(u_mid_color, u_tip_color, (tt - mid) / (1.0 - mid));

	// shock (Mach) diamonds: near-stationary bright bands in the near field only
	float sd = sin(t * 28.0 - TIME * 2.0);
	// broken up by the turbulence so it reads as bright licks near the nozzle, NOT clean rings wrapping the cone shell
	sd = pow(max(sd, 0.0), 10.0) * smoothstep(0.42, 0.03, t) * u_throttle * (0.5 + 0.9 * n);
	col += mix(u_mid_color, u_core_color, 0.5) * sd * 0.22;

	// brightness: hottest at the nozzle but a GENTLE falloff so the orange tip still glows (tri-colour must read)
	float bright = (1.0 - t * 0.35) * (0.55 + 0.9 * n) * (0.45 + 0.75 * u_throttle) + sd * 0.28;

	// density / alpha: soft nozzle, wispy noisy tail so the tip dissolves into tongues
	float noseFade = smoothstep(0.0, 0.05, t);
	float tipFade  = smoothstep(1.2, 0.5, t);   // hold the tip longer so the ORANGE band is visible before it wisps out
	float wisp     = mix(1.0, n2, smoothstep(0.25, 1.0, t));
	float alpha    = noseFade * tipFade * wisp;

	// soft-body falloff: densest where a wall faces the camera, dissolving at the grazing silhouette -- so the
	// additive cone shell reads as soft gas, NOT hard bright rings at its circular cross-section edges
	float facing = abs(dot(normalize(NORMAL), normalize(VIEW)));
	bright *= 0.60 + 0.55 * facing;
	alpha  *= 0.28 + 0.72 * facing;

	ALBEDO = col * bright * u_intensity;
	ALPHA  = clamp(alpha, 0.0, 1.0) * clamp(u_throttle * 1.3, 0.0, 1.0);
}
