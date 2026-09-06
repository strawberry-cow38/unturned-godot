// RAIN RUNNERS ON GLASS -- the second half of rain_glass.gdshader, as its own shader and its own pass (master
// 2026-09-06: "runners and drops should be two separate shaders. runners follow the world, drops shouldnt").
//
// It runs as the glass material's next_pass: the same triangles, drawn again, painting only water. Nothing here
// touches the glass tint or the drops -- those are the first pass's business.
//
// TWO FRAMES, ON PURPOSE -- and the split between them is the whole reason this is its own shader:
//   ACROSS (which column a rivulet runs in) is OBJECT space, welded to the pane. A rivulet is a feature ON the
//          glass; it does not slide sideways because the car turned.
//   PHASE  (where the head is along its run) is WORLD HEIGHT. Gravity is world, so a rivulet falls at a true rate
//          whatever the pane's rake, and a raked windscreen and an upright window run alike.
//
// ⚠ THE ACROSS AXIS MUST NOT COME FROM THE PANE'S WORLD NORMAL. It did, for one commit: `h = cross(world_up,
// world_normal)` then `dot(world_pos, h)`. That axis is re-derived from the vehicle's ORIENTATION every frame and
// then dotted with a WORLD position, so the error scales with distance from the map origin -- tinyclaw's numbers:
// half a degree of pitch at 800 m moves the coordinate ~7 m, which at 14 columns/m reshuffles ~100 columns. Not a
// swim, a boil, and worst far from origin. The object-space frame below cannot do that: it has no orientation term.
//
// THE NORMAL COMES FROM THE GEOMETRY, not from the mesh. The ripped glass carries no vertex normals at all (there
// is not one `vn` in sedan_glass_*.txt and the loader generates none), so NORMAL is unusable here -- reading it is
// what dried every side window earlier today. Screen-space derivatives of the OBJECT position give a stable facing
// for the frame; the world normal is used only for the scalar "is this steep enough to run down" gate.
shader_type spatial;
// A pure overlay: it must not re-shade the glass, write depth over it, or cull differently from the pass beneath.
render_mode blend_mix, depth_draw_never, cull_disabled, unshaded;

global uniform float rain_intensity;    // 0 .. 1: rain falling NOW
// NOT an instance uniform. Instance parameters are addressed by SLOT per shader, and this is a next_pass shader with
// its own slot table -- so `SetInstanceShaderParameter("covered", ...)`, aimed at the first pass, lands somewhere
// else here. Read as "covered", it switches every runner off and looks exactly like the effect not working (master
// 2026-09-06: "i also dont see any runners"). A plain uniform, set on this material by Vehicle.RainCoverTick and
// GlassPane, has no slots to get wrong.
uniform float covered : hint_range(0.0, 1.0) = 0.0;

uniform float streak_amount : hint_range(0.0, 1.0) = 1.0;
uniform float refract_strength = 0.038;   // a rivulet is a fat lens: it bends the scene behind it a lot more than a bead does
uniform float max_distance = 22.0;      // past this: no runners, no screen-texture read (same gate as the drops)
uniform float rim_gain = 0.55;          // refraction alone is invisible against a flat sky; the lit EDGES are what read as water.
                                        // 0.20 was too shy to see against an overcast sky -- the mask proved the runners were
                                        // drawing all along and simply could not be made out (master 2026-09-06 "i also dont see
                                        // any runners"), so the contrast goes up rather than the geometry changing.
uniform sampler2D screen_tex : hint_screen_texture, filter_linear_mipmap;
// Harness only (UG_RUNNERS): 1 = paint the runner mask solid red, 2 = paint the WHOLE pass magenta. A still frame of a
// parked car cannot show a moving rivulet, and an image diff between two runs is confounded by the falling rain being
// randomised per run -- so this is how "is the pass drawing at all" gets answered as a fact instead of a squint.
uniform float debug_paint = 0.0;

varying vec3 wpos;
varying vec3 opos;   // object space: the across-axis is built here so no vehicle rotation can move it

float h21(vec2 p){ p = fract(p * vec2(127.32, 311.7)); p += dot(p, p + 34.53); return fract(p.x * p.y); }

void vertex(){ wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz; opos = VERTEX; }

void fragment(){
    // NO early `return` in fragment() -- Godot rejects it ("Using 'return' in the 'fragment' processor function is
    // incorrect"), so the work is gated by ifs and the outputs are written once at the end.
    float cov = 0.0, rim = 0.0, dxs = 0.0;
    vec2 off = vec2(0.0);

    float dist = length(VERTEX);
    float range = 1.0 - smoothstep(max_distance * 0.75, max_distance, dist);
    float run = clamp(rain_intensity, 0.0, 1.0) * (1.0 - clamp(covered, 0.0, 1.0)) * streak_amount * range;

    // IS THERE A SLOPE TO RUN DOWN? A scalar, so the world normal is safe here -- it decides how much, never where.
    vec3 wn = normalize(cross(dFdx(wpos), dFdy(wpos)));
    float tilt = clamp(length(cross(vec3(0.0, 1.0, 0.0), wn)), 0.0, 1.0);
    run *= smoothstep(0.25, 0.6, tilt);   // 0 on a flat pane (a sunroof has nothing to run down)

    // ...and WHERE the columns are: object space, so nothing the vehicle does can move them (see the header).
    vec3 on = normalize(cross(dFdx(opos), dFdy(opos)));
    vec3 oref = abs(on.y) < 0.9 ? vec3(0.0, 1.0, 0.0) : vec3(0.0, 0.0, 1.0);
    vec3 ov = oref - on * dot(oref, on);
    float ovl = length(ov);

    if (run > 0.001 && ovl > 1e-4) {
        vec3 ou = normalize(cross(on, ov / ovl));
        float gx = dot(opos, ou);     // ACROSS: welded to the pane
        float gy = wpos.y;            // PHASE: world height -- gravity, not the pane's slope
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
        vec2 sv = normalize(vec2(dFdx(gy), dFdy(gy)) + vec2(0.0, -1e-6));  // ...and the fall axis
        off = -(dxs * 0.6 * su + 0.5 * sv) * refract_strength * cov;
    }

    vec3 col = vec3(0.0);
    float a = 0.0;
    if (cov > 0.002 || rim > 0.002) {
        // the scene behind the runner, bent -- already lit, so it goes out unshaded rather than being re-lit
        vec3 bg = textureLod(screen_tex, clamp(SCREEN_UV + off, vec2(0.001), vec2(0.999)), 0.0).rgb;
        col = bg * (1.0 + rim * 0.35) + vec3(rim * rim_gain);   // the rim both brightens what it bends and glints on its own
        a = clamp(cov * 0.95 + rim * 0.9, 0.0, 1.0);
    }
    if (debug_paint > 1.5) { ALBEDO = vec3(1.0, 0.0, 1.0); ALPHA = 0.6; }        // the pass itself: magenta over every pixel of the glass
    else if (debug_paint > 0.5) { ALBEDO = vec3(1.0, 0.0, 0.0); ALPHA = step(0.002, max(cov, rim)); }   // just the runners
    else { ALBEDO = col; ALPHA = a; }
}
