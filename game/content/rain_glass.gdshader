// RAIN ON GLASS (strawberry 2026-09-05 "make a rain on vehicle glass + building editor building glass window shader",
// reworked 2026-09-06 to their notes). Vehicle windscreens / side glass and the building editor's GlassPane windows share
// this ONE material: the plain tinted glass they used to be, plus -- while it rains -- water DROPS clinging to the pane
// and RUNNERS sliding down it.
//
// TWO EFFECTS, TWO FRAMES (master 2026-09-06 "one for drops (follows the glass' direction) and one for the runners
// (always vertical)"). They are separated by their coordinate frame, not by a second material: a second pass would be a
// full extra transparent overdraw on every window in the game for a result the frames already give.
//   DROPS   live in the PANE's own frame (across x down-the-slope). They follow the glass wherever it faces, and stick
//           to it -- a moving windscreen carries its water rather than sliding out from under it.
//   RUNNERS travel along WORLD HEIGHT (world up projected into the pane), so a run is always gravity-vertical no matter
//           how the pane is raked or facing, which is what water actually does. Still object-space, so they ride the car.
//
// THE BIGGEST FACE IS THE ONLY WET ONE (master: "have both shaders consider the BIGGEST face of the glass, because with
// side windows it was targetting the thin sides"). A pane is a solid with thickness: its edge faces carry their own
// normals, and a frame built per-fragment made those thin sides their own little wet surface with its own "down". C#
// measures the mesh's largest-area face once at build time and hands it here as `pane_axis` (object space); every frame
// is built from THAT, and a fragment whose own normal is off it -- an edge -- stays dry.
//
// Each drop is a little lens: it shows the scene behind it BENT (screen texture, shifted radially, inverted like a real
// drop) with a lit rim. Refraction alone is invisible against a flat grey sky; the rims are what read as water.
//
// SHELTER comes from the CPU, per instance (`covered`), NOT from the roof map: rain_roof stores the topmost solid at an
// XZ, and a car's own roof is above its own side windows, a house's roof above its own windows -- gate glass on "anything
// above" and no window anywhere is ever wet (tinyclaw's catch). Vehicle.RainCoverTick casts up from ABOVE its own roof
// and sets `covered` on its panes; a building window is exterior by definition and never sets it.
shader_type spatial;
render_mode diffuse_burley, specular_schlick_ggx, cull_disabled;

global uniform float rain_wetness;      // 0 dry .. 1 soaked: how much water sits on the glass (drops)
global uniform float rain_intensity;    // 0 .. 1: rain falling NOW -> runners
instance uniform float covered : hint_range(0.0, 1.0) = 0.0;   // 1 = this pane's owner is under cover (Vehicle.RainCoverTick)
instance uniform vec3 pane_axis;        // the LARGEST face's normal, object space (GlassPane.DominantFaceAxis); zero = fall back to the fragment's own

uniform vec4 tint : source_color = vec4(0.62, 0.73, 0.78, 0.26);   // the glass colour + see-through (the StandardMaterial3D.AlbedoColor it replaces)
uniform float metallic : hint_range(0.0, 1.0) = 0.0;
uniform float roughness : hint_range(0.0, 1.0) = 0.06;
uniform float drop_scale = 70.0;                         // drop cells per metre of glass (1.4 cm cells, one drop each)
uniform float drop_amount : hint_range(0.0, 1.0) = 1.0;  // coverage scale
uniform float streak_amount : hint_range(0.0, 1.0) = 1.0;
uniform float refract_strength = 0.020;                  // screen-UV shift through a drop
uniform float max_distance = 22.0;                       // past this the pane is PLAIN GLASS again -- no drops, no runners, no screen-texture read (master 2026-09-06 "add a somewhat strict limit"): every pane in view ran the full effect at any range
uniform int debug_view = 0;                              // harness (UG_GLASSDEBUGVIEW): 1 = frames, 2 = runner mask, 3 = drop mask
uniform sampler2D screen_tex : hint_screen_texture, filter_linear_mipmap;

varying vec3 obj_pos;
varying vec3 pane_u;    // object space, ACROSS the pane      (drop frame)
varying vec3 pane_v;    // object space, DOWN the pane        (drop frame)
varying vec3 pane_h;    // object space, HORIZONTAL in-plane  (runner frame: across)
varying vec3 up_obj;    // object space, world UP             (runner frame: travel)
varying float tilt;     // 0 = flat .. 1 = vertical
varying float face_ok;  // 1 on the big faces, 0 on the thin edges
varying vec2 su;        // the drop frame's axes on SCREEN (MODELVIEW_MATRIX is vertex-stage only)
varying vec2 sv;

float h21(vec2 p){ p = fract(p * vec2(127.32, 311.7)); p += dot(p, p + 34.53); return fract(p.x * p.y); }

void vertex(){
    obj_pos = VERTEX;
    vec3 fn = length(NORMAL) > 0.5 ? normalize(NORMAL) : vec3(0.0, 0.0, 1.0);   // a rip with no normals must not NaN the frame
    vec3 n = length(pane_axis) > 0.5 ? normalize(pane_axis) : fn;               // THE BIG FACE decides the frame, not this fragment's
    // face_ok is DEBUG ONLY. Gating the water on it dried every side window: the ripped glass meshes carry NO vertex
    // normals (sedan_glass_*.txt have not one `vn`) and the loader does not generate any, so NORMAL is unusable and the
    // fallback (0,0,1) matches a windscreen's axis and nothing else -- windscreen and rear glass stayed wet, both side
    // windows went dry (master 2026-09-06 "theres no shader on side windows?"). The thin-edge problem the gate was for
    // is already solved by building the frame from `pane_axis` instead of per fragment: the edges no longer have a
    // "down" of their own, they just carry the face's water, which is what water on a real pane edge does anyway.
    face_ok = step(0.75, abs(dot(fn, n)));
    // world DOWN / UP in object space: the rotation part of MODEL_MATRIX, transposed (a uniform scale normalises away)
    vec3 d = normalize(transpose(mat3(MODEL_MATRIX)) * vec3(0.0, -1.0, 0.0));
    up_obj = -d;
    vec3 dv = d - n * dot(d, n);           // down, projected onto the pane -> the DROP frame's own slope
    float dl = length(dv);
    tilt = clamp(dl, 0.0, 1.0);
    vec3 any_axis = abs(n.x) < 0.9 ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 0.0, 1.0);   // GUARD: a flat pane (sunroof) has no down
    any_axis = normalize(any_axis - n * dot(any_axis, n));
    pane_v = dl > 0.05 ? dv / dl : any_axis;
    pane_u = normalize(cross(n, pane_v));
    vec3 h = cross(up_obj, n);             // in-plane and horizontal: the axis a vertical run is measured ACROSS
    pane_h = length(h) > 0.05 ? normalize(h) : pane_u;
    su = (MODELVIEW_MATRIX * vec4(pane_u, 0.0)).xy;
    sv = (MODELVIEW_MATRIX * vec4(pane_v, 0.0)).xy;
}

void fragment(){
    float rint = clamp(rain_intensity, 0.0, 1.0);
    float wet  = clamp(rain_wetness, 0.0, 1.0);
    float open = 1.0 - clamp(covered, 0.0, 1.0);   // under cover -> dry (see face_ok in vertex(): NOT a gate, the rips have no normals to gate on)
    float dist = length(VERTEX);                     // view-space position = distance to the eye
    // RANGE GATE. Water on glass is a close-up effect -- millimetre drops and 5 mm runs are invisible at range, but the
    // shader still cost a screen-texture read and two noise fields per pixel on every window in the scene. Beyond
    // max_distance there is no water at all, so `cov` stays 0 and the whole refraction block below is skipped.
    float range = 1.0 - smoothstep(max_distance * 0.75, max_distance, dist);
    float detail = smoothstep(14.0, 5.0, dist) * range;   // drops are millimetres: gone past a few metres, no shimmer
    float amt = wet * open * drop_amount * range;                                    // water sitting on the glass
    float run = rint * open * streak_amount * range * smoothstep(0.25, 0.6, tilt);   // runners need rain NOW and a slope to run down

    vec2 su_n = length(su) > 1e-4 ? normalize(su) : vec2(1.0, 0.0);
    vec2 sv_n = length(sv) > 1e-4 ? normalize(sv) : vec2(0.0, -1.0);

    vec2 off = vec2(0.0);   // screen-UV shift of the scene seen through the water
    float cov = 0.0;        // how much of this pixel is water
    float rim = 0.0;        // the bright ring at a drop's edge

    // 1) DROPS -- the PANE's frame, so they follow the glass. Each cell runs its OWN slow cycle: a drop appears, sits,
    //    and fades, then the cell re-rolls a different drop somewhere else in it (master 2026-09-06 "have drops appear
    //    and fade randomly"). Nothing is static, and no two cells are in step.
    if (amt > 0.001 && detail > 0.001) {
        vec2 g = vec2(dot(obj_pos, pane_u), dot(obj_pos, pane_v));
        vec2 cellp = g * drop_scale;
        vec2 id = floor(cellp);
        float rate = 0.10 + 0.28 * h21(id + 5.1);          // this cell's cycles per second (~4-10 s a drop)
        float tt = TIME * rate + h21(id + 2.7) * 9.0;      // ...and its own phase, so the field never pulses together
        float cyc = floor(tt), life = fract(tt);
        vec2 q = id + cyc * 17.3;                          // everything below re-rolls per cycle
        float present = step(1.0 - 0.55 * amt, h21(q));
        vec2 c = id + 0.5 + (vec2(h21(q + 3.1), h21(q + 7.9)) - 0.5) * 0.55;   // a different spot in the cell each time
        float rs = h21(q + 11.3);
        float r = 0.07 + 0.13 * rs * rs + 0.10 * step(0.94, rs);   // radius in cells: mostly ~1.5-3 mm, the odd fat one
        float env = smoothstep(0.0, 0.10, life) * smoothstep(1.0, 0.72, life);   // swells in, holds, fades out
        vec2 dp = (cellp - c) / r;                          // 0 centre .. 1 rim
        float d2 = dot(dp, dp);
        float inside = present * env * detail * smoothstep(1.0, 0.78, d2);
        off += -(dp.x * su_n + dp.y * sv_n) * refract_strength * inside;   // a lens: the view bends toward the rim, inverted
        cov = max(cov, inside);
        rim += present * env * detail * smoothstep(0.5, 0.85, d2) * smoothstep(1.0, 0.9, d2);
    }

    // 2) RUNNERS -- always VERTICAL (master). The travel coordinate is world HEIGHT projected into the pane, not distance
    //    down the slope, so a raked windscreen and an upright side window run at the same gravity-true rate and read
    //    vertical from any angle; the columns are spaced along the pane's HORIZONTAL axis for the same reason.
    if (run > 0.001) {
        float gx = dot(obj_pos, pane_h);     // across, horizontal
        float gy = dot(obj_pos, up_obj);     // world height
        float cols = 14.0;                   // columns per metre (was 9 -- master: thinner)
        float cx = gx * cols;
        float cid = floor(cx);
        float cs = h21(vec2(cid, 5.7));
        float active = step(1.0 - 0.55 * run, cs);                // more rain -> more columns running
        float x0 = 0.5 + (cs - 0.5) * 0.5;                        // where in its column the runner lives
        float w = 0.045 + 0.035 * h21(vec2(cid, 2.3));            // half-width in column units (~3-6 mm)
        float dxs = (fract(cx) - x0) / w;
        float dx = abs(dxs);
        float period = 1.1 + 1.4 * h21(vec2(cid, 9.1));           // metres of fall per pass
        float speed = 0.30 + 0.40 * h21(vec2(cid, 4.4));          // m/s, straight down
        float s = fract((TIME * speed + cs * period + gy) / period);   // 0 at the head, growing UP behind it
        float body = active * (1.0 - smoothstep(0.0, 0.22 + 0.18 * cs, s));
        float tail = body * (1.0 - smoothstep(0.6, 1.0, dx));
        float headAmt = active * smoothstep(0.030, 0.0, s);
        float head = headAmt * (1.0 - smoothstep(0.9, 1.6, dx));
        float stk = max(tail * 0.7, head);
        off += -(dxs * 0.6 * su_n + 0.5 * sv_n) * refract_strength * stk;
        cov = max(cov, stk);
        // the EDGES catch the light -- refraction alone is invisible against a flat sky, the rims are what read as water
        rim += body * smoothstep(0.35, 0.8, dx) * (1.0 - smoothstep(0.9, 1.15, dx)) * 0.7;
        rim += headAmt * smoothstep(0.8, 1.3, dx) * (1.0 - smoothstep(1.5, 1.9, dx));
    }

    vec3 col = tint.rgb;
    float alpha = tint.a;
    vec3 emit = vec3(0.0);
    if (cov > 0.002) {
        // The scene behind the drop, bent. It is ALREADY LIT (it is the rendered frame), so it goes out as EMISSION --
        // through ALBEDO the overcast sky would light it a second time and every drop came out a dark grey disc.
        vec3 bg = textureLod(screen_tex, clamp(SCREEN_UV + off, vec2(0.001), vec2(0.999)), 0.0).rgb;
        float k = cov * 0.9;
        emit = bg * k;
        col = mix(col, vec3(0.0), k);        // the tint stays only where there is no water
        alpha = mix(alpha, 0.95, cov);       // a drop is a solid lens, not tinted air
    }
    emit += rim * 0.20 * max(amt, run);      // glints along the drops' rims
    if (debug_view == 1) { ALBEDO = vec3(0.0); EMISSION = vec3(face_ok, fract(dot(obj_pos, up_obj) * 4.0), fract(dot(obj_pos, pane_h) * 4.0)); ALPHA = 1.0; }
    else if (debug_view == 2) { ALBEDO = vec3(0.0); EMISSION = vec3(cov, run, 0.0); ALPHA = 1.0; }
    else if (debug_view == 3) { ALBEDO = vec3(0.0); EMISSION = vec3(cov, amt, detail); ALPHA = 1.0; }
    else { ALBEDO = col; EMISSION = emit; ALPHA = alpha; }
    METALLIC = metallic;
    ROUGHNESS = mix(roughness, 0.03, cov);
    SPECULAR = 0.5;
}
