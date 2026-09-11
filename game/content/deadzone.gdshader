// CONTAMINATED GROUND, AS SEEN (strawberry 2026-09-11: "add a heavy film grain effect that intensifies the
// longer you stay in the deadzone, desaturate color, starts tame").
//
// Two effects on one ramp, because they are one idea: the longer you stand in it, the less the world looks
// like film and the more it looks like a damaged sensor. Grain climbs and colour drains together, both driven
// by a single `exposure` the game feeds from DeadzoneField's own clock -- so what you see is the dose you are
// actually taking, not a separate animation that happens to look worrying.
//
// STARTS TAME is a real constraint, not a taste note. A deadzone you can walk into by accident has to warn
// you before it punishes you, so at exposure 0 this is nearly invisible and the first seconds only hint. The
// curve below is deliberately slow off the mark and steep later, which is also the shape of the danger:
// infection self-clears below 0.5, so an early exit costs nothing and a long stay costs everything.
shader_type canvas_item;
render_mode unshaded;

uniform sampler2D screen_tex : hint_screen_texture, filter_linear;
uniform float exposure = 0.0;        // 0 = just arrived, 1 = fully dosed. Driven by DeadzoneOverlay.
uniform float time_seconds = 0.0;    // fed from the engine: the grain has to MOVE or it reads as dirt on the lens
uniform float grain_max = 0.35;      // grain amplitude at full exposure
uniform float desat_max = 0.85;      // how much colour is gone at full exposure (1.0 = greyscale)

// A cheap per-pixel hash. Deliberately not a texture lookup: film grain wants a different value every pixel
// every frame, and a sampled noise tile at that rate shows its own repeat pattern through the effect.
float hash(vec2 p) {
    return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453);
}

void fragment() {
    vec4 src = texture(screen_tex, SCREEN_UV);

    // The ramp. pow(,1.6) keeps the opening seconds subtle -- "starts tame" -- while still reaching full
    // strength rather than asymptotically creeping at it, which would never read as "this is now bad".
    float t = clamp(exposure, 0.0, 1.0);
    float ramp = pow(t, 1.6);

    // DESATURATE toward luma. Rec. 709 weights rather than a flat average: an even mix turns skin and foliage
    // into the same mid-grey and the world stops reading as lit, which looks like a broken shader instead of
    // like colour draining out of it.
    float luma = dot(src.rgb, vec3(0.2126, 0.7152, 0.0722));
    vec3 drained = mix(src.rgb, vec3(luma), desat_max * ramp);

    // GRAIN, signed so it darkens as often as it brightens -- one-sided noise just washes the image out.
    // Seeded on the pixel AND on time so it crawls; a static seed is dirt on the lens, not grain.
    float n = hash(SCREEN_UV * vec2(1024.0, 1024.0) + vec2(time_seconds * 60.0, time_seconds * 37.0));
    float g = (n - 0.5) * 2.0 * grain_max * ramp;

    // Applied multiplicatively so the grain rides the image's own brightness: noise added flat would be
    // loudest in the shadows, where a real sensor is noisy too but not THIS noisy, and it would lift the
    // blacks into grey fog.
    vec3 grained = drained * (1.0 + g);

    COLOR = vec4(clamp(grained, vec3(0.0), vec3(1.0)), 1.0);
}
