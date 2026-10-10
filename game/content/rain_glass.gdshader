// RAIN ON GLASS (strawberry 2026-09-05 "make a rain on vehicle glass + building editor building glass window shader",
// reworked 2026-09-06 to their notes). Vehicle windscreens / side glass and the building editor's GlassPane windows share
// this ONE material: the plain tinted glass they used to be, plus -- while it rains -- water DROPS clinging to the pane
// and RUNNERS sliding down it.
//
// DROPS AND RUNNERS, IN ONE PASS. They were two shaders for a month (master 2026-09-06: "runners and drops should be
// two separate shaders. runners follow the world, drops shouldnt") and are one again (master 2026-10-10: "shrink the
// size of the raindrop impacts on car glass, having them appear more sharply, and then turn into runners instead of a
// separate shader"). What forced the split was that the two anchor to DIFFERENT THINGS, and that is still true -- it
// is just held inside one shader now, as two frames rather than two materials:
//   DROPS live in the PANE's own frame. They follow the glass wherever it faces and STICK to it, so a moving
//         windscreen carries its water instead of sliding out from under it.
//   RUNNERS are anchored to the WORLD: a run holds its place as the car drives past it, and falls at a true rate
//         whatever the pane's rake.
// ⭐ AND A DROP BECOMES A RUNNER. A drop used to swell, sit and FADE -- water vanishing off glass, which is precisely
// what made long rivulets have to be an unrelated effect bolted on beside it. Now each drop clings for a while and
// then loses to gravity: it accelerates downhill and drags a narrowing trail. That hand-off cannot be expressed by
// two passes that do not know about each other, so folding them together is what the behaviour needed, not a tidy-up.
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
uniform float drop_run : hint_range(0.0, 2.0) = 1.8;     // cells a released drop travels before its cycle ends (~2.6 cm): its TRAIL.
                                                         // Capped at 2 in the fragment -- see the scan-bounds note there; it is not a free dial.
// The long rivulets, folded in from what used to be rain_glass_runners.gdshader (master 2026-10-10: "...and then turn
// into runners instead of a separate shader").
uniform float streak_amount : hint_range(0.0, 1.0) = 1.0;
uniform float streak_alpha : hint_range(0.0, 1.0) = 0.5;   // master 2026-09-06: "increase the transparency of the rain runners on car windows by 50%"
uniform float streak_refract = 0.038;                      // a rivulet is a fat lens: it bends the scene behind it far more than a bead does
uniform float rim_gain = 0.55;                             // refraction alone is invisible against a flat sky; the lit EDGES are what read as water
uniform float max_distance = 22.0;                       // past this the pane is PLAIN GLASS again -- no drops, no runners, no screen-texture read (master 2026-09-06 "add a somewhat strict limit"): every pane in view ran the full effect at any range
uniform int debug_view = 0;                              // harness (UG_GLASSDEBUGVIEW): 1 = frames, 2 = runner mask, 3 = drop mask
uniform sampler2D screen_tex : hint_screen_texture, filter_linear_mipmap;

varying vec3 obj_pos;
varying vec3 wpos;      // world: the long runners' PHASE axis is world height (gravity is world, not the pane's rake)
varying vec3 pane_u;    // object space, ACROSS the pane      (drop frame)
varying vec3 pane_v;    // object space, the pane's OTHER in-plane axis (drop frame). NOT "down" -- it is built from a
                        // fixed object reference and its sign is arbitrary. That was free while drops only sat still;
                        // now that they RUN, downhill comes from fall_dir below and never from this axis's sign.
varying vec2 fall_dir;  // WORLD DOWN expressed in the pane's own (u,v) frame -- a DIRECTION, never dotted with a world
                        // position, so the distance-scaled "boil" that killed the old runners' across-axis cannot happen
varying float fall_tilt;// how much of world down lies IN the pane: 0 a sunroof (nothing runs), 1 an upright window
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
    // axes now come from the pane's own normal and a fixed OBJECT-space reference, so a drop's PLACE cannot move
    // relative to the glass whatever the car does. Gravity enters only as fall_dir/fall_tilt below -- a unit direction
    // and a scalar, never an axis dotted with a world position, which is the distinction that keeps it stable.
    vec3 ref = abs(n.y) < 0.9 ? vec3(0.0, 1.0, 0.0) : vec3(0.0, 0.0, 1.0);   // any object-space axis not parallel to n
    vec3 v = ref - n * dot(ref, n);
    pane_v = length(v) > 1e-4 ? normalize(v) : normalize(abs(n.x) < 0.9 ? vec3(1.0, 0.0, 0.0) - n * n.x : vec3(0.0, 0.0, 1.0) - n * n.z);
    pane_u = normalize(cross(n, pane_v));
    su = (MODELVIEW_MATRIX * vec4(pane_u, 0.0)).xy;
    sv = (MODELVIEW_MATRIX * vec4(pane_v, 0.0)).xy;
    wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    // DOWNHILL, for the drops that run. World down, brought into object space and flattened into the pane; its length
    // is exactly how steep the pane is (1 upright, 0 flat), and its direction in (u,v) is which way water goes. One
    // derivation answers both "how much" and "which way", and it is orientation-dependent BY DESIGN -- that is what
    // makes a runner obey gravity rather than the mesh. ⚠ The forbidden thing is dotting an orientation-derived AXIS
    // with a WORLD POSITION (error scales with distance from origin: ~7 m at 800 m, a boil not a swim). Nothing here
    // does: the drop coordinates stay object-space and only this unit direction comes from the model matrix.
    vec3 odown = normalize(transpose(mat3(MODEL_MATRIX)) * vec3(0.0, -1.0, 0.0));
    vec3 inplane = odown - n * dot(odown, n);
    fall_tilt = clamp(length(inplane), 0.0, 1.0);
    fall_dir = fall_tilt > 1e-4 ? normalize(vec2(dot(inplane, pane_u), dot(inplane, pane_v))) : vec2(0.0, 1.0);
}

void fragment(){
	// NO manual back-face flip here (removed 2026-10-04). cull_disabled ALREADY makes Godot negate the normal of a
	// back-facing fragment before this function runs (DO_SIDE_CHECK, scene_forward_clustered.glsl; true of custom
	// shaders and built-in materials alike). The `NORMAL = FRONT_FACING ? NORMAL : -NORMAL` that used to sit here
	// undid that, so every back face was lit from the wrong side -- the always-dark back of a flag, a guard rail
	// seen from behind. Measured on a bare quad: back face lit WITHOUT the line, black WITH it.
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
    //    2026-09-06), so each cell's drop is jittered anywhere inside it and a neighbourhood is scanned: drops
    //    overlap cell boundaries freely and no row or column lines up. Each cell still runs its own slow cycle --
    //    a drop swells in, clings, RUNS, and the cell re-rolls a different one somewhere else.
    if (amt > 0.001 && detail > 0.001) {
        vec2 g = vec2(dot(obj_pos, pane_u), dot(obj_pos, pane_v));
        vec2 cellp = g * drop_scale;
        vec2 base = floor(cellp);
        // ⚠ THE SCAN REACHES FURTHER ALONG THE FALL AXIS THAN ACROSS IT. A drop that runs leaves the cell it was
        // rolled in, and a pixel only ever finds drops whose ORIGIN cell it scans -- so a 3x3 neighbourhood clips the
        // bead the instant it travels more than a cell and the run just vanishes mid-slide. drop_run is capped at 2
        // cells of travel for exactly this reason: these bounds are what make it visible, not a cosmetic choice.
        float runCells = min(drop_run, 2.0);
        for (int dy = -2; dy <= 2; dy++) {
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
                // SMALLER (strawberry 2026-10-10: "shrink the size of the raindrop impacts on car glass").
                // Was 0.07 + 0.13*rs^2 + 0.10*step(0.94): ~1.5-3 mm with fat ones to 4 mm. Now ~1-2 mm, and the
                // occasional big one is rarer and no longer twice everything else.
                float r = 0.042 + 0.078 * rs * rs + 0.055 * step(0.965, rs);
                // ⭐ A DROP NOW RUNS (master, same message: "...and then turn into runners instead of a
                // separate shader"). Its life is two acts rather than swell-sit-fade:
                //   hold   -- it clings where it landed, growing
                //   release-- it loses the fight with gravity, accelerates DOWN the pane and drags a tail
                // The old fade-out is gone: a drop that simply vanished is what made runners need to be a
                // separate, unrelated effect in the first place.
                float hold = 0.34 + 0.34 * h21(q + 19.7);          // some cling far longer than others
                float slip = max(0.0, life - hold) / max(0.0001, 1.0 - hold);
                // ⚠ GATED ON THE PANE'S TILT, the same scalar the runners below use and for the same reason:
                // a sunroof has nothing to run down. Both come from fall_dir/fall_tilt in vertex() -- see the
                // note there for why a unit direction is safe where an axis dotted with a world position BOILS.
                float fall = slip * slip * runCells * smoothstep(0.25, 0.6, fall_tilt);   // accelerating, not linear
                vec2 cNow = c + fall_dir * fall;                   // downhill, in the pane's own frame
                // ⭐ SHARPER (master: "having them appear more sharply"). Swell-in is quick and the body edge is
                // tight: a drop should read as a bead with a hard rim, not a soft blob fading into the glass.
                float env = smoothstep(0.0, 0.04, life) * (1.0 - smoothstep(0.86, 1.0, life));
                // Distance to the PATH, not just to the head: the capsule from where it clung to where it is
                // now IS the runner. Width tapers along it, so the trail narrows behind the bead.
                vec2 pa = cellp - c, ba = cNow - c;
                float hseg = fall > 1e-5 ? clamp(dot(pa, ba) / max(1e-5, dot(ba, ba)), 0.0, 1.0) : 0.0;
                float trailW = r * mix(0.30, 1.0, hseg);           // thin where it started, full at the bead
                vec2 dseg = pa - ba * hseg;
                float dTrail = length(dseg) / max(1e-5, trailW);
                vec2 dp = (cellp - cNow) / r;
                float dBead = length(dp);
                if (min(dBead, dTrail) > 1.25) continue;           // neither the bead nor its trail reaches here
                // The BEAD is a lens (hard edge, bends the scene behind it). The TRAIL is a film: no lensing, and it
                // thins AND dims toward the tail -- a streak the drop has left behind is already draining away, so a
                // uniformly bright ribbon reads as painted-on rather than as water that went past.
                float bead  = 1.0 - smoothstep(0.86, 1.04, dBead);
                float trail = (1.0 - smoothstep(0.80, 1.05, dTrail)) * mix(0.26, 0.80, hseg) * step(1e-5, fall);
                float inside = env * detail * max(bead, trail);
                off += -(dp.x * su_n + dp.y * sv_n) * refract_strength * env * detail * bead;
                cov = max(cov, inside);
                rim += env * detail * smoothstep(0.62, 0.94, dBead) * (1.0 - smoothstep(0.98, 1.08, dBead));
            }
        }
    }

    // 2) LONG RUNNERS -- the rivulets that cross the whole pane, which lived in rain_glass_runners.gdshader as this
    //    material's next_pass until master folded them in here. The code is unchanged where it was hard-won, because
    //    the two things it gets right are not obvious:
    //      ACROSS (which column a rivulet runs in) is OBJECT space, welded to the pane -- it reuses the drop frame's
    //             own pane_u, so a rivulet cannot slide sideways because the car turned.
    //      PHASE  (where the head is along its run) is WORLD HEIGHT. Gravity is world, so a raked windscreen and an
    //             upright side window run at the same true rate.
    //    A 1-D column lattice needs no neighbourhood scan: fract() gives it unbounded reach for free, which is why
    //    these can be metres long while a released drop's trail is centimetres.
    float rcov = 0.0, rrim = 0.0;
    float run = rint * open * streak_amount * range * smoothstep(0.25, 0.6, fall_tilt);   // 0 on a flat pane
    if (run > 0.001) {
        float gx = dot(obj_pos, pane_u);   // ACROSS: welded to the pane
        float gy = wpos.y;                 // PHASE: world height -- gravity, not the pane's slope
        float cols = 14.0;                 // columns per metre
        float cx = gx * cols;
        float cid = floor(cx);
        float cs = h21(vec2(cid, 5.7));
        float active = step(1.0 - 0.55 * run, cs);                // more rain -> more columns running
        float x0 = 0.5 + (cs - 0.5) * 0.5;                        // where in its column this runner lives
        float w = 0.045 + 0.035 * h21(vec2(cid, 2.3));            // half-width in column units (~3-6 mm)
        float dxs = (fract(cx) - x0) / w;
        float dx = abs(dxs);
        float period = 1.1 + 1.4 * h21(vec2(cid, 9.1));           // metres of fall per pass
        float speed = 0.30 + 0.40 * h21(vec2(cid, 4.4));          // m/s, straight down
        float sfall = fract((TIME * speed + cs * period + gy) / period);   // 0 at the head, growing UP behind it
        float body = active * (1.0 - smoothstep(0.0, 0.22 + 0.18 * cs, sfall));
        float tail = body * (1.0 - smoothstep(0.6, 1.0, dx));
        float headAmt = active * smoothstep(0.030, 0.0, sfall);
        float head = headAmt * (1.0 - smoothstep(0.9, 1.6, dx));
        rcov = max(tail * 0.7, head) * streak_alpha;
        rrim = (body * smoothstep(0.35, 0.8, dx) * (1.0 - smoothstep(0.9, 1.15, dx)) * 0.7
             + headAmt * smoothstep(0.8, 1.3, dx) * (1.0 - smoothstep(1.5, 1.9, dx))) * streak_alpha;
        vec2 rsu = normalize(vec2(dFdx(gx), dFdy(gx)) + vec2(1e-6, 0.0));   // the across axis, on screen
        vec2 rsv = normalize(vec2(dFdx(gy), dFdy(gy)) + vec2(0.0, -1e-6));  // ...and the fall axis
        off += -(dxs * 0.6 * rsu + 0.5 * rsv) * streak_refract * rcov;
    }

    vec3 col = tint.rgb;
    float alpha = tint.a;
    vec3 emit = vec3(0.0);
    float water = clamp(max(cov, rcov), 0.0, 1.0);
    if (water > 0.002) {
        // The scene behind the drop, bent. It is ALREADY LIT (it is the rendered frame), so it goes out as EMISSION --
        // through ALBEDO the overcast sky would light it a second time and every drop came out a dark grey disc.
        vec3 bg = textureLod(screen_tex, clamp(SCREEN_UV + off, vec2(0.001), vec2(0.999)), 0.0).rgb;
        float k = water * 0.9;
        emit = bg * k * (1.0 + rrim * 0.35);   // the rivulet's rim brightens what it bends, as it did in its own pass
        col = mix(col, vec3(0.0), k);        // the tint stays only where there is no water
        alpha = mix(alpha, 0.95, water);     // a drop is a solid lens, not tinted air
    }
    emit += rim * 0.20 * amt + rrim * rim_gain;   // glints along the beads' rims, and along the rivulets' edges
    if (debug_view == 1) { ALBEDO = vec3(0.0); EMISSION = vec3(face_ok, fract(dot(obj_pos, pane_v) * 4.0), fract(dot(obj_pos, pane_u) * 4.0)); ALPHA = 1.0; }
    else if (debug_view == 2) { ALBEDO = vec3(0.0); EMISSION = vec3(rcov, rrim, rint); ALPHA = 1.0; }   // the RUNNER field, now in this pass
    else if (debug_view == 3) { ALBEDO = vec3(0.0); EMISSION = vec3(cov, amt, detail); ALPHA = 1.0; }     // the DROP field
    else { ALBEDO = col; EMISSION = emit; ALPHA = alpha; }
    METALLIC = metallic;
    ROUGHNESS = mix(roughness, 0.03, water);
    SPECULAR = 0.5;
}
