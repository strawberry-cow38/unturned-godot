// RAIN ON GLASS (strawberry 2026-09-05 "make a rain on vehicle glass + building editor building glass window shader",
// reworked 2026-09-06 to their notes). Vehicle windscreens / side glass and the building editor's GlassPane windows share
// this ONE material: the plain tinted glass they used to be, plus -- while it rains -- water DROPS clinging to the pane
// and RUNNERS sliding down it.
//
// DROPS ONLY. The runners are their own shader now, rain_glass_runners.gdshader, run as this material's next_pass
// (master 2026-09-06: "runners and drops should be two separate shaders. runners follow the world, drops shouldnt").
// The split is not cosmetic -- the two anchor to different things, which is the whole point:
//   DROPS (here) live in the PANE's own frame. They follow the glass wherever it faces and STICK to it, so a moving
//         windscreen carries its water instead of sliding out from under it.
//   RUNNERS (next_pass) are anchored to the WORLD: a run holds its place as the car drives past it.
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
uniform float refract_strength = 0.020;                  // screen-UV shift through a drop
uniform float max_distance = 22.0;                       // past this the pane is PLAIN GLASS again -- no drops, no runners, no screen-texture read (master 2026-09-06 "add a somewhat strict limit"): every pane in view ran the full effect at any range
uniform int debug_view = 0;                              // harness (UG_GLASSDEBUGVIEW): 1 = frames, 2 = runner mask, 3 = drop mask
uniform sampler2D screen_tex : hint_screen_texture, filter_linear_mipmap;

varying vec3 obj_pos;
varying vec3 pane_u;    // object space, ACROSS the pane      (drop frame)
varying vec3 pane_v;    // object space, DOWN the pane        (drop frame)
varying float face_ok;  // 1 on the big faces, 0 on the thin edges
varying vec2 su;        // the drop frame's axes on SCREEN (MODELVIEW_MATRIX is vertex-stage only)
varying vec2 sv;

float h21(vec2 p){ p = fract(p * vec2(127.32, 311.7)); p += dot(p, p + 34.53); return fract(p.x * p.y); }

void vertex(){
    obj_pos = VERTEX;
    vec3 fn = length(NORMAL) > 0.5 ? normalize(NORMAL) : vec3(0.0, 0.0, 1.0);   // a rip with no normals must not NaN the frame
    vec3 n = length(pane_axis) > 0.5 ? normalize(pane_axis) : fn;               // THE BIG FACE decides the frame, not this fragment's
    // face_ok is DEBUG ONLY -- see the note in the fragment stage: the ripped glass has no vertex normals to gate on.
    face_ok = step(0.75, abs(dot(fn, n)));
    // THE GRID IS WELDED TO THE MESH (master 2026-09-06: "when my car bounces around, the whole rain glass shader shifts
    // around, as if its not part of the glass"). It used to build this frame from WORLD DOWN projected into the pane --
    // which re-derives the pattern's axes from the vehicle's ORIENTATION every frame, so a pitch or a roll rotated the
    // whole drop field inside the glass and the water appeared to swim across it, worst exactly while bouncing. Both
    // axes now come from the pane's own normal and a fixed OBJECT-space reference: nothing here touches MODEL_MATRIX,
    // so a drop cannot move relative to the glass whatever the car does. Gravity belongs to the runners, and they are
    // their own pass now (rain_glass_runners.gdshader) precisely because they DO need the world.
    vec3 ref = abs(n.y) < 0.9 ? vec3(0.0, 1.0, 0.0) : vec3(0.0, 0.0, 1.0);   // any object-space axis not parallel to n
    vec3 v = ref - n * dot(ref, n);
    pane_v = length(v) > 1e-4 ? normalize(v) : normalize(abs(n.x) < 0.9 ? vec3(1.0, 0.0, 0.0) - n * n.x : vec3(0.0, 0.0, 1.0) - n * n.z);
    pane_u = normalize(cross(n, pane_v));
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

    vec2 su_n = length(su) > 1e-4 ? normalize(su) : vec2(1.0, 0.0);
    vec2 sv_n = length(sv) > 1e-4 ? normalize(sv) : vec2(0.0, -1.0);

    vec2 off = vec2(0.0);   // screen-UV shift of the scene seen through the water
    float cov = 0.0;        // how much of this pixel is water
    float rim = 0.0;        // the bright ring at a drop's edge

    // 1) DROPS -- a SCATTERED field, not a lattice. One drop per cell on a regular grid is visible AS a grid the
    //    moment coverage is high ("the shader tiles in a really obvious way across the glass pane", master
    //    2026-09-06), so each cell's drop is jittered anywhere inside it and the 3x3 neighbourhood is scanned:
    //    drops overlap cell boundaries freely and no row or column lines up. Each cell still runs its own slow
    //    cycle -- a drop swells in, sits, fades, and the cell re-rolls a different one somewhere else.
    if (amt > 0.001 && detail > 0.001) {
        vec2 g = vec2(dot(obj_pos, pane_u), dot(obj_pos, pane_v));
        vec2 cellp = g * drop_scale;
        vec2 base = floor(cellp);
        for (int dy = -1; dy <= 1; dy++) {
            for (int dx = -1; dx <= 1; dx++) {
                vec2 id = base + vec2(float(dx), float(dy));
                float rate = 0.10 + 0.28 * h21(id + 5.1);          // this cell's cycles per second (~4-10 s a drop)
                float tt = TIME * rate + h21(id + 2.7) * 9.0;      // ...and its own phase, so the field never pulses together
                float cyc = floor(tt), life = fract(tt);
                vec2 q = id + cyc * 17.3;                          // everything below re-rolls per cycle
                float present = step(1.0 - 0.55 * amt, h21(q));
                if (present < 0.5) continue;
                vec2 c = id + vec2(h21(q + 3.1), h21(q + 7.9));    // ANYWHERE in the cell -- this is what breaks the lattice
                float rs = h21(q + 11.3);
                float r = 0.07 + 0.13 * rs * rs + 0.10 * step(0.94, rs);   // radius in cells: mostly ~1.5-3 mm, the odd fat one
                float env = smoothstep(0.0, 0.10, life) * smoothstep(1.0, 0.72, life);   // swells in, holds, fades out
                vec2 dp = (cellp - c) / r;
                float d2 = dot(dp, dp);
                if (d2 > 1.2) continue;                            // this drop does not reach the pixel
                float inside = env * detail * smoothstep(1.0, 0.78, d2);
                off += -(dp.x * su_n + dp.y * sv_n) * refract_strength * inside;   // a lens: the view bends toward the rim, inverted
                cov = max(cov, inside);
                rim += env * detail * smoothstep(0.5, 0.85, d2) * smoothstep(1.0, 0.9, d2);
            }
        }
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
    emit += rim * 0.20 * amt;      // glints along the drops' rims
    if (debug_view == 1) { ALBEDO = vec3(0.0); EMISSION = vec3(face_ok, fract(dot(obj_pos, pane_v) * 4.0), fract(dot(obj_pos, pane_u) * 4.0)); ALPHA = 1.0; }
    else if (debug_view == 2) { ALBEDO = vec3(0.0); EMISSION = vec3(cov, rint, 0.0); ALPHA = 1.0; }
    else if (debug_view == 3) { ALBEDO = vec3(0.0); EMISSION = vec3(cov, amt, detail); ALPHA = 1.0; }
    else { ALBEDO = col; EMISSION = emit; ALPHA = alpha; }
    METALLIC = metallic;
    ROUGHNESS = mix(roughness, 0.03, cov);
    SPECULAR = 0.5;
}
