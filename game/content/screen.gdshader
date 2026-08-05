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
uniform float head_line = 0.0;       // absolute index of the line being TYPED right now
uniform float head_col = 0.0;        // how many cells have been typed on it
uniform float cursor_on = 0.0;       // the terminal's cursor, shown while a burst is idle
uniform float banner = 0.0;          // the flatscreen's "input" OSD, over the running channel for BannerDur
uniform vec3 screen_black = vec3(0.0);   // the MESH's own screen colour -- what an unlit pixel shows, instead of 0
uniform sampler2D blob_tex : source_color, filter_linear, repeat_disable;   // the studio logo, bounced like a DVD
uniform sampler2D bg_tex : source_color, filter_linear, repeat_disable;     // a map preview, behind the instrument panel
uniform vec2 blob_half = vec2(0.12, 0.18);   // half-size in UV, aspect-corrected in C# so the logo stays SQUARE on a
                                             // 2:1 flatscreen instead of being stretched into a smear
uniform vec2 blob_pos = vec2(0.5);   // the DVD blob's centre, in UV. Driven from C# so the SPILL LIGHT can be placed
                                     // exactly where the blob is drawn -- if both sides derived it from time_s they
                                     // would agree only until one of them was edited, and then the light would track
                                     // a blob that is not there.

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
    // CHUNKY on purpose. This was 96x72 cells, which on a 0.85 m CRT face is ~9 mm of noise -- and high-frequency
    // noise viewed from anywhere but point-blank averages straight out to a FLAT GREY FIELD. strawberry reported
    // exactly that: "a blank grayish screen" on a set that was correctly on the static channel and correctly hissing.
    // The sound was right, the picture was averaging itself into nothing.
    //
    // 34x26 keeps a visible grain at across-the-room distance, which is also closer to what analogue snow looks like
    // on a real set: chunks, not a fine dither.
    vec2 cell = floor(uv * vec2(34.0, 26.0));
    float t = floor(time_s * 14.0);
    float v = hash21(cell + t * 7.13 + seed);
    // A second, much coarser band drifting at a different rate, so the field has structure instead of being uniform
    // sparkle -- real snow rolls and clumps. Without it, chunkier cells alone still read as a static texture.
    float band = hash21(floor(uv * vec2(5.0, 4.0)) - t * 1.7 + seed * 3.0);
    float lum = clamp(0.05 + v * 0.82 + (band - 0.5) * 0.22, 0.0, 1.0);
    return vec3(lum);
}

// ---- the bouncing blob. Triangle waves on each axis ARE a perfect reflection off the walls, so the corners fall out
// of the maths instead of needing a collision test.
vec3 prog_dvd(vec2 uv) {
    // The actual studio logo, bouncing -- master: "repurpose existing assets in the game". Which is the joke: a DVD
    // screensaver is a logo, and the game already ships one.
    vec2 luv = (uv - blob_pos) / (blob_half * 2.0) + 0.5;
    if (luv.x < 0.0 || luv.x > 1.0 || luv.y < 0.0 || luv.y > 1.0) return vec3(0.0);
    vec4 t = texture(blob_tex, luv);
    return t.rgb * t.a;   // premultiplied against black: the screen around it is off
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
    const float cols = 34.0, rows = 17.0;

    // A terminal TYPING, not a wall of text sliding past. The write head is owned by C# -- typing has state and a burst
    // has memory. The screen fills from the top and only starts scrolling once the head reaches the bottom.
    //
    // The cursor lives on ITS OWN BLANK LINE, one below whatever is being printed (master: "the cursor of the terminal
    // scroll one should blink on its own blank line under whatever is being printed line by line"). So the visible
    // window has to reach one line PAST the head, or the cursor would sit just off the bottom edge and never be seen.
    float cursorLine = head_line + 1.0;
    float top = max(0.0, cursorLine - (rows - 1.0));
    float cy = floor(uv.y * rows) + top;          // absolute line under this pixel
    float cx = floor(uv.x * cols);
    vec2 f = vec2(fract(uv.x * cols), fract(uv.y * rows));

    // The cursor's own line, and nothing below it.
    if (cy > cursorLine) return vec3(0.0);
    if (cy == cursorLine) {
        // Column 0, because the line is blank -- this is where the NEXT character would go.
        if (cursor_on > 0.5 && cx == 0.0 && f.y > 0.08 && f.y < 0.92 && f.x > 0.05 && f.x < 0.75)
            return vec3(0.55, 0.95, 0.60);
        return vec3(0.0);
    }

    // Ragged right edge + the occasional blank line, so it reads as prose rather than as a block.
    float lineLen = 6.0 + hash11(cy * 1.37 + seed) * (cols - 8.0);
    float endCol = hash11(cy * 3.71) < 0.13 ? 0.0 : lineLen;
    // ...and on the line being typed, only as far as the head has got.
    if (cy == head_line) endCol = min(endCol, head_col);
    if (cx >= endCol) return vec3(0.0);

    // a 3x5 glyph cell with a one-pixel gutter
    vec2 sub = floor(vec2(f.x * 4.0, f.y * 6.0));
    if (sub.x > 2.5 || sub.y > 4.5) return vec3(0.0);
    float bit = hash21(vec2(cx, cy) * 11.7 + sub + seed);

    // The occasional RED line (strawberry: "a random chance for a red string of words"). Hashed off the LINE index, so
    // a whole line is red or none of it is -- per-character would read as broken glyphs rather than as an error being
    // printed, and the line keeps its colour as it scrolls because the hash is on its absolute index, not its position
    // on screen.
    vec3 ink = hash11(cy * 5.19 + seed) < 0.09 ? vec3(0.82, 0.07, 0.06) : vec3(0.55, 0.95, 0.60);   // deeper red (strawberry) -- the old 0.95,0.32,0.28 read as salmon next to the green
    return vec3(step(0.42, bit)) * ink;
}

// ---- THE INPUT BANNER (strawberry): "blue, thin 1px white border with 1px blue padding outside the white border,
// between it and the rectangle edge with white text". So, outside in: 1 px blue, 1 px white rule, then the blue panel.
// It OVERLAYS the channel -- the picture is already running behind it, which is what a real set does when you switch
// source: the input OSD sits on top of live video, it does not replace it.
//
// The pixel sizes are in SCREEN pixels via fwidth, not UV fractions, so the border stays one pixel on a 0.3 m monitor
// and on a 1.2 m television instead of scaling with the panel and turning into a fat stripe on the big one.
const vec3 banner_blue = vec3(0.06, 0.16, 0.62);
const vec3 banner_white = vec3(0.95, 0.96, 1.0);

vec4 prog_banner(vec2 uv) {
    const vec2 halfBox = vec2(0.24, 0.115);
    vec2 d = abs(uv - 0.5);
    if (d.x > halfBox.x || d.y > halfBox.y) return vec4(0.0);   // outside: the channel shows through untouched

    // One screen pixel, in UV, per axis. fwidth(uv) is exactly that by definition.
    vec2 px = fwidth(uv);
    vec2 inset = halfBox - d;                     // distance from the box edge, per axis
    float edge = min(inset.x / max(px.x, 1e-6), inset.y / max(px.y, 1e-6));   // in pixels from the edge
    if (edge < 1.0) return vec4(banner_blue, 1.0);    // 1 px blue padding, outermost
    if (edge < 2.0) return vec4(banner_white, 1.0);   // 1 px white rule
    // ...and the blue panel, with white block text -- the same 3x5 glyph cell the terminal scroll uses, so it reads as
    // the same alphabet rather than as a second, unrelated kind of fake writing.
    vec2 p = (uv - (vec2(0.5) - halfBox)) / (halfBox * 2.0);
    const float cols = 10.0, textTop = 0.34, textBot = 0.66;
    if (p.y < textTop || p.y > textBot) return vec4(banner_blue, 1.0);
    float cx = floor(p.x * cols);
    if (cx < 1.0 || cx > 7.0) return vec4(banner_blue, 1.0);   // a margin, so the text is not flush to the rule
    vec2 f = vec2(fract(p.x * cols), (p.y - textTop) / (textBot - textTop));
    vec2 sub = floor(vec2(f.x * 4.0, f.y * 6.0));
    if (sub.x > 2.5 || sub.y > 4.5) return vec4(banner_blue, 1.0);
    return vec4(step(0.42, hash21(vec2(cx, 11.0) * 11.7 + sub + seed)) > 0.5 ? banner_white : banner_blue, 1.0);
}

// ---- an instrument readout: bars on a gridded panel, each breathing at its own rate, with a sweeping trace over the
// top. The per-bar frequencies are deliberately incommensurate so the row never falls into a visible marching pattern.
vec3 prog_bars(vec2 uv) {
    // A dim map preview behind the panel, so it reads as a monitoring station rather than as an abstract chart --
    // again an asset the game already has, rather than something invented.
    vec3 col = vec3(0.02, 0.03, 0.05) + texture(bg_tex, uv).rgb * 0.16;
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

    // THE BLACK IS THE GLASS (strawberry: "change the black color for all screen types on all channels to not be
    // perfect black, but the mesh's screen color"). Scaled by how DARK the pixel already is, so an unlit pixel lands
    // exactly on the mesh's own colour and a lit one is left alone -- adding it flat would wash the whole picture a
    // fifth of the way to grey, and max() would floor the colour channels and desaturate anything saturated.
    c += screen_black * (1.0 - clamp(dot(c, vec3(0.2126, 0.7152, 0.0722)), 0.0, 1.0));

    c = mix(c, desat(c), clamp(mono, 0.0, 1.0));
    // The banner rides ON TOP, after mono: an input OSD is generated by the panel's own scaler, not received down the
    // channel, so a monochrome set would not render it in grey.
    if (banner > 0.5) { vec4 b = prog_banner(uv); c = mix(c, b.rgb, b.a); }
    ALBEDO = c * tint.rgb;
    ALPHA = tint.a;
}
