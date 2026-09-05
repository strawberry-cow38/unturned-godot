// BINOCULARS overlay (BinocularsView): ONE magnified render (a second camera at FOV/zoom, `view`) shown through two
// circular cut-outs -- one per lens, overlapping like real binoculars -- with the housing outside them black.
// The scope's two-render PiP method, full-screen (master 2026-09-05).
shader_type canvas_item;
uniform sampler2D view : filter_linear;
uniform float aspect = 1.7778;   // screen width / height (set by the C# side)
uniform float radius = 0.46;     // lens radius, in screen-HEIGHT units
uniform float sep = 0.20;        // half the distance between the two lens centres
uniform float edge = 0.010;      // soft rim
uniform float vignette = 0.35;   // darkening toward each rim so the glass reads as glass

void fragment() {
    vec2 p = vec2((UV.x - 0.5) * aspect, UV.y - 0.5);
    float dl = length(p - vec2(-sep, 0.0));
    float dr = length(p - vec2( sep, 0.0));
    float d = min(dl, dr);
    float m = 1.0 - smoothstep(radius - edge, radius, d);
    vec3 c = texture(view, UV).rgb;
    c *= 1.0 - vignette * smoothstep(radius * 0.55, radius, d);
    COLOR = vec4(c * m, 1.0);
}
