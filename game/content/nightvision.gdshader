// NIGHTVISION GOGGLES (master 2026-09-05: "when worn and toggled with N, green filter, as well as fog reduction around you.
// light sources bloom harder and are brighter. military is green, civilian is b&w and slightly less effective").
// A screen-space pass over the finished frame: the scene's luminance is amplified through a soft knee (the dark comes up,
// anything already bright saturates), painted in the tube's phosphor tint, with intensifier grain and a tube vignette.
// The fog pull-back and the harder bloom are NOT here -- they are Environment changes DayNightCycle makes while
// NightVision.Active (fog density scaled down, glow opened up), so they behave like real world lighting, not paint.
shader_type canvas_item;
uniform sampler2D screen_tex : hint_screen_texture, filter_linear;
uniform float gain = 2.6;                                     // light amplification -- how hard the dark is pulled up
uniform vec3 tint : source_color = vec3(0.22, 1.0, 0.32);     // military phosphor green; civilian is near-white (b&w)
uniform float grain = 0.05;                                   // intensifier noise
uniform float vignette = 0.35;                                // the tube's edge falloff
uniform float hot = 0.6;                                      // how far light sources push past the tint toward white
float hash12(vec2 p) { vec3 p3 = fract(vec3(p.xyx) * 0.1031); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.x + p3.y) * p3.z); }   // Hoskins hash, not fract(sin): stable at any coordinate
void fragment() {
    vec3 c = texture(screen_tex, SCREEN_UV).rgb;
    float lum = dot(c, vec3(0.299, 0.587, 0.114));
    float amp = 1.0 - exp(-lum * gain);                        // soft knee: 0 stays 0, mid-darks come up hard, brights saturate
    float n = (hash12(SCREEN_UV * 1024.0 + vec2(fract(TIME * 13.0) * 100.0)) - 0.5) * grain;
    vec3 col = tint * (amp + n);
    col += vec3(max(lum * gain - 1.2, 0.0)) * hot;             // a headlight or a fire blows out past the phosphor toward white
    float d = length((SCREEN_UV - 0.5) * vec2(1.0, 0.85));
    col *= 1.0 - vignette * smoothstep(0.42, 0.80, d);
    COLOR = vec4(col, 1.0);
}
