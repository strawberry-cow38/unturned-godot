shader_type spatial;
render_mode unshaded, cull_disabled, blend_mix, depth_draw_opaque;

// The Science_3 patient monitor's screen. Unshaded for the same reason the televisions are: a screen is a light
// SOURCE, so no lamp in the world may wash its colours out.
//
// Every proportion here is MEASURED off the vanilla prop's own modelled trace rather than chosen, because strawberry
// asked for "the same amplitude as the vanilla one" and the vanilla one is geometry, not a texture -- the prop's
// palette is 2x2 and the zigzag is twelve real vertices lying 2 mm proud of the screen face. Measured, in the mesh's
// local units, then divided by the screen quad's own height:
//
//     screen face   Z 1.2020 .. 1.7970   (0.5950 tall)
//     baseline      Z 1.4183             -> 0.364 up the screen
//     R peak        Z 1.6312 centreline  -> 0.721   (+0.358 above baseline)
//     Q trough      Z 1.3133 centreline  -> 0.187   (-0.176 below baseline)
//     line width      0.0452             -> 0.076 of screen height
//
// CENTRELINES, not the ribbon's outer edges. The trace is drawn with thickness -- each point in the mesh is a PAIR
// of vertices about 0.045 apart -- so reading the extremes off min/max Z gives the peak half a line-width too high
// and the dip half a line-width too low. Taken that way the amplitude comes out 12% larger than it is, which is
// small enough to look like a judgement call and is simply wrong. The shader draws a centreline of LINE_W thickness,
// so these have to be centreline numbers to match.
//
// So the spike reaches 36% of the screen height above the baseline and the dip 18% below it.

uniform float time_s = 0.0;
uniform float alive = 1.0;        // 1 = beating, 0 = flatline
uniform float lit = 1.0;          // powered and switched on; 0 draws nothing at all
uniform float period = 1.0;       // seconds per beat -- one full sweep of the trace
uniform float seed = 0.0;

const vec3 SCREEN_BG = vec3(0.055, 0.075, 0.062);   // near-black with the faintest green cast, like a phosphor panel
const vec3 TRACE     = vec3(0.247, 0.467, 0.204);   // the prop's OWN palette green, texel (63,119,52)/255
const float BASELINE = 0.364;
const float R_PEAK   = 0.358;    // above baseline, centreline to centreline
const float Q_DIP    = 0.176;    // below baseline, centreline to centreline
const float LINE_W   = 0.050;    // the vanilla RIBBON is 0.076 of the screen; drawn that thick the spike reads as a
                                 // blob at real on-screen size. Amplitude is what "same as vanilla" was about, and
                                 // a thinner line makes that amplitude MORE legible, not less faithful.

// One beat, as a PIECEWISE-LINEAR polyline (strawberry: "should be a hard bounce instead of a softer line").
//
// It was a sum of Gaussians, which is the anatomically tidy way to draw an ECG and the wrong way to draw THIS one:
// the vanilla trace is twelve vertices joined by straight segments, so it turns corners rather than easing through
// them. Smooth bumps read as a sine wave with ambitions. Straight segments with hard corners read as an instrument.
//
// The corner positions are still set by what survives on screen -- a monitor face is only ~150 px across at normal
// viewing distance, so the R upstroke gets 4% of the width rather than the 1.4% an anatomically-narrow spike would
// have. The first version of this shader used the narrow one and rendered a wavy line with no spike in it at all.
const int PTS = 13;
const vec2 BEAT[13] = {
    vec2(0.000,  0.0),
    vec2(0.120,  0.0),
    vec2(0.160,  0.06),      // P -- a small bounce
    vec2(0.200,  0.0),
    vec2(0.330,  0.0),
    vec2(0.375, -Q_DIP),     // Q -- straight down
    vec2(0.420,  R_PEAK),    // R -- straight up, the spike
    vec2(0.465, -0.09),      // S -- and straight back through
    vec2(0.510,  0.0),
    vec2(0.640,  0.0),
    vec2(0.700,  0.10),      // T
    vec2(0.760,  0.0),
    vec2(1.000,  0.0)
};

// Returns the height, and hands back the segment's SLOPE -- which the caller needs to keep the line an even width.
// Measuring how far a pixel is from the trace vertically is fine on a flat run and hopeless on the R upstroke, where
// a near-vertical segment is thousands of units "away" from a pixel sitting right next to it. That is what makes a
// hard-cornered trace render as a dotted or broken line if you draw it the naive way.
float ecg(float x, out float slope) {
    slope = 0.0;
    for (int i = 0; i < PTS - 1; i++) {
        if (x >= BEAT[i].x && x <= BEAT[i + 1].x) {
            float dx = max(BEAT[i + 1].x - BEAT[i].x, 1e-5);
            slope = (BEAT[i + 1].y - BEAT[i].y) / dx;
            return mix(BEAT[i].y, BEAT[i + 1].y, (x - BEAT[i].x) / dx);
        }
    }
    return 0.0;
}

void fragment() {
    if (lit < 0.5) { ALBEDO = vec3(0.0); ALPHA = 1.0; discard; }

    vec2 uv = UV;
    vec3 col = SCREEN_BG;

    // A faint graticule, so the panel reads as an instrument rather than as a black rectangle.
    vec2 gl = abs(fract(uv * vec2(8.0, 5.0)) - 0.5);
    col += vec3(0.035, 0.070, 0.048) * (1.0 - smoothstep(0.0, 0.06, min(gl.x, gl.y)));   // dim: it is a backdrop, not the reading

    // The sweep: a head travelling left to right, redrawing one beat per period, with the trace behind it fading --
    // which is what a monitor with a persistence sweep looks like, and what makes "it draws the same ECG every x
    // seconds" visible as an event rather than as a wave sliding past.
    float head = fract(time_s / max(period, 0.05));
    float age = head - uv.x;
    if (age < 0.0) age += 1.0;                                        // wrapped round the back
    float trail = 1.0 - smoothstep(0.0, 0.92, age);                   // bright at the head, dim behind

    float slope = 0.0;
    float y = alive > 0.5 ? ecg(uv.x, slope) : 0.0;
    float line = BASELINE + y;
    // Perpendicular distance, not vertical: divide by the segment's length-per-unit-x. Without this the steep parts
    // of a hard-cornered trace thin out to nothing and the spike looks like two dots.
    float d = abs(uv.y - line) / sqrt(1.0 + slope * slope);
    float on = 1.0 - smoothstep(LINE_W * 0.35, LINE_W * 0.75, d);

    // The head itself is a brighter dot, so the sweep has something to be at.
    float headGlow = (1.0 - smoothstep(0.0, 0.03, age)) * on;

    col += TRACE * (on * (0.45 + 0.55 * trail) + headGlow * 0.8);
    ALBEDO = col;
    ALPHA = 1.0;
}
