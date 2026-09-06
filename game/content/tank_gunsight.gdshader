// The tank gunner's sight (master 2026-09-05: "the turret gunner in 1st person, inside the turret gets an 8x scope
// reticle"): a round eyepiece cut aspect-true out of a black field, the ripped 8x-scope reticle filling the glass. The
// magnification itself is the main camera's FOV (PlayerController.UpdateTankOptics), as the binoculars do it.
shader_type canvas_item;
uniform sampler2D reticle : filter_linear;
uniform float screen_aspect = 1.7778;   // width / height, set by TankOptics
uniform float radius = 0.47;            // eyepiece radius as a fraction of the screen HEIGHT
void fragment() {
    vec2 p = (UV - 0.5) * vec2(screen_aspect, 1.0) / radius;   // unit circle regardless of the window's shape
    float r = length(p);
    if (r >= 1.0) { COLOR = vec4(0.0, 0.0, 0.0, 1.0); }
    else {
        vec4 t = texture(reticle, p * 0.5 + 0.5);
        // The png's transparent texels are pure WHITE (255,255,255,0), so the bilinear filter bleeds white into every
        // mark's edge -- a white crust round the reticle (master 2026-09-06). Weight the colour by its own alpha: the
        // marks are black, so their edges go dark instead of chalky.
        vec3 mark = t.rgb * t.a;
        float rim = smoothstep(0.975, 1.0, r);   // a soft housing edge
        COLOR = vec4(mark * (1.0 - rim), max(t.a, rim));
    }
}
