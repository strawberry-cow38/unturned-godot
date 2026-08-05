shader_type spatial;
render_mode unshaded, cull_disabled, blend_mix, depth_draw_opaque;

// Every screen in the world draws through THIS, one program per set (strawberry: terminals, bar graphs, static fuzz,
// a bouncing DVD blob, the SMPTE card, a flat colour). It is a shader rather than a per-frame ImageTexture because
// the map has dozens of screens and the alternative is rewriting a texture per set per frame on the CPU.
//
// Everything that was previously a StandardMaterial3D property lives here as a uniform, and it has to STAY equivalent:
//   unshaded          -- a screen is a light SOURCE, not a lit surface. No light in the world may wash out its colours.
//   cull_disabled     -- ripped meshes, winding may face either way.
//   blend_mix + ALPHA -- the CRT warmup is a CROSSFADE onto the cabinet's own screen face, not a brightness ramp.
//                        Brightness cannot do it: it MULTIPLIES, so a low value is a dim picture, never a flat colour.
//
// `tint` carries brightness in rgb and the warmup fade in a. `mono` desaturates for a monochrome CRT and for the
// power-off collapse. `time_s` is offset per set by `seed`, so a room full of televisions is not in lockstep.

uniform int program;            // 0 TestCard 1 Static 2 Dvd 3 Colour 4 TerminalCursor 5 TerminalScroll 6 BarGraph
uniform sampler2D pattern_tex : source_color, filter_linear, repeat_enable;
uniform float pattern_frac = 1.0;   // windows the composite onto the picture; the rest is the blanking bar
uniform float roll_offset = 0.0;    // vertical-hold slip
uniform vec4 tint = vec4(1.0);      // rgb = colour * brightness, a = warmup fade
uniform float mono = 0.0;
uniform float time_s = 0.0;
uniform float seed = 0.0;
uniform float scroll_offset = 0.0;   // driven from C#, so the text can scroll in BURSTS rather than continuously
uniform float cursor_on = 0.0;       // the terminal's cursor, shown while a burst is idle

float hash11(float p) { return fract(sin(p * 127.1) * 43758.5453123); }
float hash21(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453123); }

// Rec. 709 luma, the same weights the C# side uses. Two desaturation rules that merely look similar is exactly the
// kind of difference nobody reports and nobody can explain.
vec3 desat(vec3 c) { float l = dot(c, vec3(0.2126, 0.7152, 0.0722)); return vec3(l); }

// ---- the SMPTE test card, sampled the way the old Uv1Scale/Uv1Offset did --------------------------------------
vec3 prog_testcard(vec2 uv) {
    return texture(pattern_tex, vec2(uv.x, uv.y * pattern_frac + roll_offset)).rgb;
}

// ---- analogue snow. Quantised in BOTH space and time: a full-resolution per-pixel hash reads as a flat grey haze
// at any distance, and an unquantised time makes it shimmer at the render rate rather than at a broadcast one.
vec3 prog_static(vec2 uv) {
    vec2 cell = floor(uv * vec2(96.0, 72.0));
    float t = floor(time_s * 18.0);
    float v = hash21(cell + t * 7.13 + seed);
    return vec3(0.06 + v * 0.88);
}

// ---- the bouncing blob. Triangle waves on each axis ARE a perfect reflection off the walls, so the corners fall out
// of the maths instead of needing a collision test.
vec3 prog_dvd(vec2 uv) {
    vec2 half_size = vec2(0.17, 0.11);
    vec2 span = vec2(1.0) - half_size * 2.0;
    float tx = abs(fract((time_s * 0.13 + seed) ) * 2.0 - 1.0);
    float ty = abs(fract((time_s * 0.097 + seed * 1.7)) * 2.0 - 1.0);
    vec2 c = half_size + vec2(tx, ty) * span;
    vec2 d = abs(uv - c) / half_size;
    // rounded rect, softened just enough that it does not crawl with aliasing as it slides
    float m = 1.0 - smoothstep(0.86, 1.0, max(d.x, d.y * 0.92));
    return vec3(m);
}

// ---- a terminal waiting for input: black, with one block cursor blinking at roughly 1.5 Hz.
vec3 prog_cursor(vec2 uv) {
    vec2 cell = vec2(1.0 / 40.0, 1.0 / 20.0);
    vec2 pos = vec2(2.0, 2.0) * cell;                    // two cells in, two lines down
    float blink = step(0.5, fract(time_s * 1.5));
    vec2 d = (uv - pos) / cell;
    float on = step(0.0, d.x) * step(d.x, 0.9) * step(0.0, d.y) * step(d.y, 0.9);
    return vec3(on * blink) * vec3(0.92, 0.96, 0.92);
}

// ---- block "text" scrolling forever. Not noise: lines have RAGGED LENGTHS with blank gaps, and glyphs are drawn from
// a 3x5 sub-grid, which is what makes it read as text rather than as a dither pattern. Scrolls upward continuously.
vec3 prog_scroll(vec2 uv) {
    float cols = 34.0, rows = 17.0;
    // `scroll_offset` rather than time: master wants it to run in bursts of random length with random gaps, and a
    // burst is a state machine with memory. Deriving one from a pure function of time is possible but unreadable, and
    // the CPU is already ticking these sets -- so C# owns the position and this just draws it.
    vec2 g = vec2(uv.x * cols, uv.y * rows + scroll_offset);
    vec2 cell = floor(g);
    vec2 f = fract(g);

    float line = hash11(cell.y * 1.37 + seed);           // how far along this line the text runs
    float lineLen = 6.0 + line * (cols - 8.0);

    // The cursor sits after the end of the newest line, and only shows while the terminal is IDLE -- text does not
    // flow and blink at the same time on a real one, and having it blink through a burst reads as two effects
    // fighting rather than one machine working.
    float newest = floor(scroll_offset) + rows - 1.0;
    if (cursor_on > 0.5 && cell.y == newest) {
        float curX = floor(6.0 + hash11(newest * 1.37 + seed) * (cols - 8.0)) + 1.0;
        if (cell.x == curX && f.y > 0.08 && f.y < 0.92 && f.x > 0.05 && f.x < 0.75)
            return vec3(0.55, 0.95, 0.60);
    }

    if (cell.x > lineLen) return vec3(0.0);              // ragged right edge
    if (hash11(cell.y * 3.71) < 0.13) return vec3(0.0);  // occasional empty line, like paragraph breaks

    // a 3x5 glyph cell with a one-pixel gutter
    vec2 sub = floor(vec2(f.x * 4.0, f.y * 6.0));
    if (sub.x > 2.5 || sub.y > 4.5) return vec3(0.0);
    float bit = hash21(cell * 11.7 + sub + seed);
    float on = step(0.42, bit);
    return vec3(on) * vec3(0.55, 0.95, 0.60);            // phosphor green
}

// ---- an instrument readout: bars on a gridded panel, each breathing at its own rate, with a sweeping trace over the
// top. The per-bar frequencies are deliberately incommensurate so the row never falls into a visible marching pattern.
vec3 prog_bars(vec2 uv) {
    vec3 col = vec3(0.02, 0.03, 0.05);
    // grid
    vec2 gl = abs(fract(uv * vec2(12.0, 8.0)) - 0.5);
    float grid = (1.0 - smoothstep(0.0, 0.06, min(gl.x, gl.y))) * 0.10;
    col += vec3(0.15, 0.45, 0.35) * grid;

    float bars = 11.0;
    float bi = floor(uv.x * bars);
    float bf = fract(uv.x * bars);
    if (bf > 0.16 && bf < 0.84) {
        float ph = hash11(bi + seed * 3.0) * 6.28318;
        float rate = 0.6 + hash11(bi * 2.7 + seed) * 1.9;
        float h = 0.14 + 0.72 * (0.5 + 0.5 * sin(time_s * rate + ph));
        if (uv.y > 1.0 - h) {
            float up = (uv.y - (1.0 - h)) / max(h, 1e-4);
            col = mix(vec3(0.15, 0.85, 0.55), vec3(0.95, 0.85, 0.20), 1.0 - up);
        }
    }
    // sweeping trace
    float trace = 0.5 + 0.32 * sin(uv.x * 9.0 + time_s * 1.7 + seed);
    col += vec3(0.9, 0.35, 0.35) * (1.0 - smoothstep(0.0, 0.022, abs(uv.y - trace)));
    return col;
}

void fragment() {
    vec2 uv = UV;
    vec3 c;
    if (program == 0) c = prog_testcard(uv);
    else if (program == 1) c = prog_static(uv);
    else if (program == 2) c = prog_dvd(uv);
    else if (program == 4) c = prog_cursor(uv);
    else if (program == 5) c = prog_scroll(uv);
    else if (program == 6) c = prog_bars(uv);
    else c = vec3(1.0);                      // 3 Colour: the flat colour rides `tint`

    c = mix(c, desat(c), clamp(mono, 0.0, 1.0));
    ALBEDO = c * tint.rgb;
    ALPHA = tint.a;
}
