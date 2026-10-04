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

// Per-metre extinction, PER CHANNEL, 1/m. Red dies first -- which is why deep water is blue rather than merely
// dim, and why a per-channel vector is not a flourish: a scalar murk can only make water darker, never bluer.
// Written by RainSystem3D (weather stirs sediment and raises it), so one C# owner feeds both shaders.
global uniform vec3 water_extinct;
// The colour light scatters INTO on the way -- what fills in as the original light is absorbed.
global uniform vec3 water_scatter_col;
// Master gate, 0..1: 0 is EXACTLY the look before any of this, as the A/B control. ⚠ Defaults to 0 -- a wrong
// sea that is ON costs master the sea that already worked.
global uniform float water_optics;

/// Fraction of light surviving `path` metres of water, per channel.
vec3 water_transmit(float path) { return exp(-max(water_extinct, vec3(0.0)) * max(path, 0.0)); }

/// What `incoming` light looks like after crossing `path` metres of water.
vec3 water_through(vec3 incoming, float path) {
    vec3 t = water_transmit(path);
    return incoming * t + water_scatter_col * (1.0 - t);
}

/// How much of the detail has been scattered away over `path` metres, 0..1 -- the blur amount.
/// ⭐ Derived from the SAME transmittance as the colour (green channel, the eye's luminance channel) rather than
/// from a parameter of its own. Scattering is why the light is missing AND why what is left is smeared; giving
/// those two separate knobs is how a shader ends up sharp but blue, or blurry but clear, neither of which is water.
float water_scatter(float path) { return 1.0 - water_transmit(path).g; }
