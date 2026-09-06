// CHROMATIC ABERRATION (master 2026-09-06: "research implementing chromatic aberration to the edges of the
// screen"). Retail ships this -- it is in GraphicsSettingsData beside Bloom and SunShafts -- and the port left
// it out with the note "no Godot-side hook for those yet, so they are not shown rather than shown as dead
// rows" (GraphicsOptions.cs). This is that hook.
//
// A real lens fails to bring every wavelength to the same focus, and the error grows with distance from the
// optical axis: the middle of the frame is sharp and the corners fringe. So the offset here is RADIAL and
// scales with r^2 -- not a flat RGB split, which fringes the centre of the screen just as hard and reads as a
// broken monitor rather than as a lens.
//
// METHOD, matching Unity's PostProcessing v2 (which is what retail runs): sweep N taps along the radial
// offset and weight each by where it sits in a red->green->blue spectrum, instead of taking three hard R/G/B
// samples. Three taps is the cheap path and shows visible red/cyan edges; more taps fill the gap between them
// so the fringe reads as a smooth spectral smear. PPv2 scales its sample count with intensity for exactly
// this reason and calls the cheap version "Fast Mode".
shader_type canvas_item;
render_mode unshaded;

uniform sampler2D screen_tex : hint_screen_texture, filter_linear;
uniform float intensity = 0.35;    // radial offset at the very corner, in fractions of the screen half-width
uniform int samples = 6;           // taps along the sweep; 3 = PPv2's Fast Mode, more = smoother fringe
uniform float falloff = 2.0;       // r^falloff: 2.0 keeps the middle of the screen clean and loads the corners

void fragment() {
    vec2 uv = SCREEN_UV;
    vec2 fromCentre = uv - vec2(0.5);
    // Normalised so r == 1 at the CORNER rather than at the edge midpoint -- otherwise the offset saturates
    // along the short axis and the corners, which is where a lens actually fringes, stop getting worse.
    float r = length(fromCentre) / 0.7071068;
    float mag = intensity * pow(clamp(r, 0.0, 1.0), falloff) * 0.05;

    // Direction is radial. At dead centre fromCentre is the zero vector, whose normalize() is undefined, and
    // mag is 0 there anyway -- so the guard is about not feeding NaN to the sampler, not about the look.
    vec2 dir = r > 1e-5 ? normalize(fromCentre) : vec2(0.0);

    int n = clamp(samples, 2, 16);
    vec3 sum = vec3(0.0);
    vec3 wsum = vec3(0.0);
    for (int i = 0; i < n; i++) {
        float t = float(i) / float(n - 1);          // 0 at the inner tap, 1 at the outer
        // A cheap approximation of PPv2's 3x1 spectral LUT: a triangular red/green/blue response sliding
        // across the sweep, so the inner taps carry the red end and the outer taps the blue.
        vec3 w = vec3(
            max(0.0, 1.0 - abs(t - 0.0) * 2.0),
            max(0.0, 1.0 - abs(t - 0.5) * 2.0),
            max(0.0, 1.0 - abs(t - 1.0) * 2.0)
        );
        vec2 suv = clamp(uv + dir * mag * t, vec2(0.0), vec2(1.0));   // clamped: sampling off-screen smears the border
        sum += texture(screen_tex, suv).rgb * w;
        wsum += w;
    }
    // wsum can only be zero for a degenerate weight set; guarding it keeps a bad `samples` from blanking the
    // screen rather than merely looking wrong.
    COLOR = vec4(sum / max(wsum, vec3(1e-4)), 1.0);
}
