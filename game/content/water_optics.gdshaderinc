// WHAT WATER DOES TO LIGHT -- the ONE definition, shared by the surface and the submerged view.
//
// ⚠⚠ THE DEFECT THIS EXISTS TO FIX IS THAT THERE WERE TWO MODELS. water.gdshader tinted the bed by VERTICAL
// depth with its own blur curve; underwater.gdshader ran its own Beer-Lambert with its own tint, murk and
// red_loss. They were tuned separately, so swimming down through the surface crossed a discontinuity between two
// different seas. Master, on the whole of it (2026-10-04): "the water should look visibly cloudy at depth,
// instead of perfectly translucent on the surface and a cheap fog effect underwater" -- one complaint about two
// implementations, which is why tuning either one alone never answered it.
//
// ⭐⭐ ONE PATH LENGTH, ONE EXTINCTION, TWO VIEWPOINTS. Looking down from a boat, the light travelled from the
// seabed to the surface. Looking at that same seabed while swimming, it travelled from the seabed to your mask.
// Both are "how many metres of water did this light cross", so both are this function. Whatever is true of the
// sea from above is then automatically true of it from below, for free and permanently.
//
// ⚠ AND THE PATH IS THE SLANT DISTANCE, NOT THE DEPTH. The surface shader used v_wpos.y - bed_y, the VERTICAL
// water depth, which is only the path length when you are looking straight down. At the grazing angle a player
// standing on a beach actually looks at the sea, light from the bottom crosses many times that -- so the old
// model showed the seabed through 40 m of water as though it were under 2 m. That is a large part of "perfectly
// translucent", and it is a wrong QUANTITY rather than a wrong coefficient.

// ⭐⭐ THE SEA'S COLOUR LIVES HERE, not in either shader, because master looked at the first version and said:
// "the color of the underwater fog doesnt match the surface at all". They were right, and the reason is that the
// first pass at this unified the MATHS and left the COLOURS in two places -- the surface was blue (0.10 : 0.40 :
// 1.00) and the submerged veil was teal (0.28 : 0.81 : 1.00). Two numbers that were never compared side by side
// cannot agree by luck, and "one model" was only half true while they existed separately.
//
// ⭐ THE SURFACE'S VALUES ARE THE AUTHORITY, not a new third set. They carry three rounds of master's own tuning
// (2026-09-08, 09-12, 09-15); the submerged view is DERIVED from them. Declaring them in the include rather than
// in water.gdshader is what makes that structural instead of a promise -- the same trick swell.gdshaderinc uses
// for the wave constants, and it keeps the `source_color` hint, so the sRGB->linear conversion still happens.
uniform vec4 water_deep : source_color = vec4(0.025, 0.105, 0.26, 0.94);   // 2026-09-15 "turn down how blue": blue is 67% of the tint, back off the 09-12 step (78%) -- the near-opaque ALPHA that step also asked for stays
uniform vec4 water_shallow : source_color = vec4(0.060, 0.245, 0.44, 0.58); // ...and the shallows with it, 66% blue -> 59%. Alpha untouched: "less see-through" was a separate ask

// ⚠ The submerged pass draws UNSHADED, while the surface colours above are ALBEDO and get lit. Handing the raw
// albedo to an unshaded pass gives a veil far darker than the sea it is meant to match, so this is the lighting
// the surface gets and the submerged view does not. It is a brightness, never a hue: the hue is master's.
uniform float scatter_gain = 2.2;

/// The colour the water veils everything with, at an eye depth in metres. ⭐ Shallow water really is a different
/// colour from deep water, and it is the SAME pair the surface draws -- so diving from a turquoise shallow into
/// blue depths now reads continuously instead of crossing between two unrelated palettes.
vec3 water_veil(float eye_depth_m) {
    return mix(water_shallow.rgb, water_deep.rgb, clamp(eye_depth_m / 12.0, 0.0, 1.0)) * max(scatter_gain, 0.0);
}

// Per-metre extinction, PER CHANNEL, 1/m. Red dies first -- which is why deep water is blue rather than merely
// dim, and why a per-channel vector is not a flourish: a scalar murk can only make water darker, never bluer.
// Written by RainSystem3D (weather stirs sediment and raises it), so one C# owner feeds both shaders.
global uniform vec3 water_extinct;
// Master gate, 0..1: 0 is EXACTLY the look before any of this, as the A/B control. ⚠ Defaults to 0 -- a wrong
// sea that is ON costs master the sea that already worked.
global uniform float water_optics;

/// Fraction of light surviving `path` metres of water, per channel.
vec3 water_transmit(float path) { return exp(-max(water_extinct, vec3(0.0)) * max(path, 0.0)); }

/// What `incoming` light looks like after crossing `path` metres of water, veiled by the sea's own colour.
vec3 water_through(vec3 incoming, float path, vec3 veil) {
    vec3 t = water_transmit(path);
    return incoming * t + veil * (1.0 - t);
}

/// How much of the detail has been scattered away over `path` metres, 0..1 -- the blur amount.
/// ⭐ Derived from the SAME transmittance as the colour (green channel, the eye's luminance channel) rather than
/// from a parameter of its own. Scattering is why the light is missing AND why what is left is smeared; giving
/// those two separate knobs is how a shader ends up sharp but blue, or blurry but clear, neither of which is water.
float water_scatter(float path) { return 1.0 - water_transmit(path).g; }
