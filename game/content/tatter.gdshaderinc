// tatter.gdshaderinc -- WORN-CLOTHING look (strawberry 2026-10-06: "a tattered holey shader for clothing thats
// worn that shows in 1p and 3p for other players too"). Shared by clothes.gdshader (the shirt + pants painted on
// the body) and gear_wear.gdshader (hats, vests, packs, masks).
//
// One number drives it: wear = 1 - condition (0 = new, 1 = broken). Procedural, so no texture assets: a value-noise
// field decides WHERE the cloth gives way, and wear decides HOW MUCH.
//   0.00-0.30  grime only: the cloth dulls and browns, scuffs appear.
//   0.30+      holes open in the lowest noise, each ringed by a dark frayed edge; ~15% of the cloth at broken.
// The field is fixed per garment, so a hole that opened at 60% is still there at 40% -- wear GROWS holes rather
// than reshuffling them. Holes are SNAPPED TO THE TEXEL GRID (the caller passes a snapped coordinate): Unturned's
// clothes are nearest-filtered pixel art, and a smooth round hole in a blocky shirt reads as a paint splash.

float tatter_hash(vec2 p) {
	p = fract(p * vec2(123.34, 456.21));
	p += dot(p, p + 45.32);
	return fract(p.x * p.y);
}

float tatter_vnoise(vec2 p) {
	vec2 i = floor(p), f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	float a = tatter_hash(i), b = tatter_hash(i + vec2(1.0, 0.0));
	float c = tatter_hash(i + vec2(0.0, 1.0)), d = tatter_hash(i + vec2(1.0, 1.0));
	return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

float tatter_fbm(vec2 p) {
	return tatter_vnoise(p) * 0.55 + tatter_vnoise(p * 2.17 + 7.3) * 0.3 + tatter_vnoise(p * 4.61 + 3.1) * 0.15;
}

// Given the noise value n at this point: x = how much cloth is left (0 = a hole), y = frayed-edge darkening,
// z = grime amount. speck = a second, finer noise for scuffs.
vec3 tatter_from(float n, float speck, float wear) {
	if (wear <= 0.001) return vec3(1.0, 0.0, 0.0);
	float open = clamp((wear - 0.30) / 0.70, 0.0, 1.0);
	float cut = mix(0.0, 0.335, open);
	float keep = open > 0.0 ? step(cut, n) : 1.0;
	float rim = open > 0.0 ? (1.0 - step(cut + 0.05, n)) * keep : 0.0;   // a hard, one-texel-ish frayed band
	float scuff = step(1.0 - wear * 0.25, speck);
	// broad dirt STAINS from the high end of the same field, growing with wear -- what tells 65% from 100% before
	// anything has torn
	float stain = step(1.0 - wear * 0.42, n) * 0.55;
	float grime = clamp(wear * 0.5 + scuff * 0.4 + stain, 0.0, 1.0);
	return vec3(keep, rim, grime);
}

vec3 tatter(vec2 p, float wear) {
	return tatter_from(tatter_fbm(p), tatter_vnoise(p * 5.3 + 11.0), wear);
}

// 3-D variant for gear, whose tiny palette textures make UV useless as a noise domain: two orthogonal planes of
// the object-space position, blended, so every face of a hat gets its own pattern.
vec3 tatter3(vec3 p, float wear) {
	float n = tatter_fbm(p.xy) * 0.5 + tatter_fbm(p.zy + 4.7) * 0.3 + tatter_fbm(p.xz + 9.1) * 0.2;
	float s = tatter_vnoise(p.xy * 5.3 + p.z * 3.1 + 11.0);
	return tatter_from(n, s, wear);
}

// The colour a worn garment shows: dulled toward a brown-grey, and near-black along a frayed edge.
vec3 tatter_tint(vec3 rgb, vec3 t) {
	vec3 dull = mix(rgb, rgb * vec3(0.70, 0.64, 0.56) + vec3(0.035, 0.028, 0.016), t.z);
	return dull * (1.0 - t.y * 0.65);
}
