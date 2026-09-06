// RAIN RUNNERS ON GLASS -- the second half of rain_glass.gdshader, as its own shader and its own pass (master
// 2026-09-06: "runners and drops should be two separate shaders. runners follow the world, drops shouldnt").
//
// It runs as the glass material's next_pass: the same triangles, drawn again, painting only water. Nothing here
// touches the glass tint or the drops -- those are the first pass's business.
//
// ANCHORED TO THE WORLD, which is the difference that earns the split. Every coordinate below is world space, so a
// run holds its place in the world while the pane slides past it: drive forward and the streaks trail back across
// the windscreen, which is what water on a moving screen does. The drops in the first pass do the opposite, and
// deliberately -- they are stuck to the glass and travel with it. One frame could not give both.
//
// THE NORMAL COMES FROM THE GEOMETRY, not from the mesh. The ripped glass carries no vertex normals at all (there
// is not one `vn` in sedan_glass_*.txt and the loader generates none), so NORMAL is unusable here -- reading it is
// what dried every side window earlier today. The screen-space derivatives of the world position give the true
// facing of whatever triangle is being drawn, whatever the file left out.
shader_type spatial;
// A pure overlay: it must not re-shade the glass, write depth over it, or cull differently from the pass beneath.
render_mode blend_mix, depth_draw_never, cull_disabled, unshaded;

global uniform float rain_intensity;    // 0 .. 1: rain falling NOW
// Declared in the SAME ORDER as rain_glass.gdshader. Instance uniforms are addressed per instance, and a next_pass
// shader that lists them differently reads the wrong slot -- so the pair stays in step even though `pane_axis` is
// unused here (this shader gets its facing from the geometry instead).
instance uniform float covered : hint_range(0.0, 1.0) = 0.0;
instance uniform vec3 pane_axis;

uniform float streak_amount : hint_range(0.0, 1.0) = 1.0;
uniform float refract_strength = 0.020;
uniform float max_distance = 22.0;      // past this: no runners, no screen-texture read (same gate as the drops)
uniform float rim_gain = 0.20;          // refraction alone is invisible against a flat sky; the lit EDGES are what read as water
uniform sampler2D screen_tex : hint_screen_texture, filter_linear_mipmap;

varying vec3 wpos;

float h21(vec2 p){ p = fract(p * vec2(127.32, 311.7)); p += dot(p, p + 34.53); return fract(p.x * p.y); }

void vertex(){ wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz; }

void fragment(){
    // NO early `return` in fragment() -- Godot rejects it ("Using 'return' in the 'fragment' processor function is
    // incorrect"), so the work is gated by ifs and the outputs are written once at the end.
    float cov = 0.0, rim = 0.0, dxs = 0.0;
    vec2 off = vec2(0.0);

    float dist = length(VERTEX);
    float range = 1.0 - smoothstep(max_distance * 0.75, max_distance, dist);
    float run = clamp(rain_intensity, 0.0, 1.0) * (1.0 - clamp(covered, 0.0, 1.0)) * streak_amount * range;

    // the surface's own facing, from the geometry (see the header)
    vec3 n = normalize(cross(dFdx(wpos), dFdy(wpos)));
    vec3 h = cross(vec3(0.0, 1.0, 0.0), n);          // in-plane and horizontal: the axis a vertical run is measured ACROSS
    float hl = length(h);
    run *= smoothstep(0.25, 0.6, clamp(hl, 0.0, 1.0));   // 0 on a flat pane (a sunroof has nothing to run down)

    if (run > 0.001 && hl > 1e-4) {
        h /= hl;
        float gx = dot(wpos, h);      // WORLD across
        float gy = wpos.y;            // WORLD height -- gravity, not the pane's slope
        float cols = 14.0;            // columns per metre
        float cx = gx * cols;
        float cid = floor(cx);
        float cs = h21(vec2(cid, 5.7));
        float active = step(1.0 - 0.55 * run, cs);                // more rain -> more columns running
        float x0 = 0.5 + (cs - 0.5) * 0.5;                        // where in its column this runner lives
        float w = 0.045 + 0.035 * h21(vec2(cid, 2.3));            // half-width in column units (~3-6 mm)
        dxs = (fract(cx) - x0) / w;
        float dx = abs(dxs);
        float period = 1.1 + 1.4 * h21(vec2(cid, 9.1));           // metres of fall per pass
        float speed = 0.30 + 0.40 * h21(vec2(cid, 4.4));          // m/s, straight down
        float s = fract((TIME * speed + cs * period + gy) / period);   // 0 at the head, growing UP behind it
        float body = active * (1.0 - smoothstep(0.0, 0.22 + 0.18 * cs, s));
        float tail = body * (1.0 - smoothstep(0.6, 1.0, dx));
        float headAmt = active * smoothstep(0.030, 0.0, s);
        float head = headAmt * (1.0 - smoothstep(0.9, 1.6, dx));
        cov = max(tail * 0.7, head);
        // refraction alone is invisible against a flat sky; the lit EDGES are what read as water
        rim = body * smoothstep(0.35, 0.8, dx) * (1.0 - smoothstep(0.9, 1.15, dx)) * 0.7
            + headAmt * smoothstep(0.8, 1.3, dx) * (1.0 - smoothstep(1.5, 1.9, dx));
        vec2 su = normalize(vec2(dFdx(gx), dFdy(gx)) + vec2(1e-6, 0.0));   // the across axis, on screen
        vec2 sv = normalize(vec2(dFdx(gy), dFdy(gy)) + vec2(0.0, -1e-6));  // ...and the down axis
        off = -(dxs * 0.6 * su + 0.5 * sv) * refract_strength * cov;
    }

    vec3 col = vec3(0.0);
    float a = 0.0;
    if (cov > 0.002 || rim > 0.002) {
        // the scene behind the runner, bent -- already lit, so it goes out unshaded rather than being re-lit
        vec3 bg = textureLod(screen_tex, clamp(SCREEN_UV + off, vec2(0.001), vec2(0.999)), 0.0).rgb;
        col = bg + vec3(rim * rim_gain);
        a = clamp(cov + rim * 0.6, 0.0, 1.0);
    }
    ALBEDO = col;
    ALPHA = a;
}
