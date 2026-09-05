// RAIN ON GLASS (strawberry 2026-09-05 "make a rain on vehicle glass + building editor building glass window shader").
// Vehicle windscreens / side glass and the building editor's GlassPane windows share this ONE material: the plain tinted
// glass they used to be, plus -- while it rains -- water BEADS clinging to the pane and RUNNERS streaking down it.
//
// Everything is computed in the pane's OWN space (object space), so the water sticks to a moving windscreen instead of
// sliding over it. "Down" is WORLD down projected onto the pane: a raked windscreen (the ship's is 11 deg, a bus's is
// upright) streaks along its own slope, and a flat sunroof only beads, because there is nothing to run down (a flat pane
// has no down -- the vertex shader picks any in-plane axis rather than normalising ~0; tinyclaw).
//
// Each bead is a little lens: it shows the scene behind it BENT (screen texture, shifted radially, inverted like a real
// drop), which is what makes water on glass read as water and not as grey speckle. Beads fade out past a few metres --
// they are a centimetre wide and would only shimmer -- the runners carry the read from afar.
//
// SHELTER comes from the CPU, per instance (`covered`), NOT from the roof map: rain_roof stores the topmost solid at an
// XZ, and a car's own roof is above its own side windows, a house's roof above its own windows -- gate glass on "anything
// above" and no window anywhere is ever wet (tinyclaw's catch); gate on "cover well above" and a tall building's lower
// windows dry while its upper ones bead. Only the owner knows what a pane belongs to: Vehicle.RainCoverTick casts up
// from ABOVE its own roof and sets `covered` on its panes (a car in a garage dries, a car in the open beads); a building
// window is exterior by definition and never sets it.
shader_type spatial;
render_mode diffuse_burley, specular_schlick_ggx, cull_disabled;

global uniform float rain_wetness;      // 0 dry .. 1 soaked: how much water sits on the glass (beads)
global uniform float rain_intensity;    // 0 .. 1: rain falling NOW -> runners
instance uniform float covered : hint_range(0.0, 1.0) = 0.0;   // 1 = this pane's owner is under cover (set per MeshInstance3D by Vehicle.RainCoverTick)

uniform vec4 tint : source_color = vec4(0.62, 0.73, 0.78, 0.26);   // the glass colour + see-through (the StandardMaterial3D.AlbedoColor it replaces)
uniform float metallic : hint_range(0.0, 1.0) = 0.0;
uniform float roughness : hint_range(0.0, 1.0) = 0.06;
uniform float bead_scale = 40.0;                          // bead cells per metre of glass (2.5 cm cells, one drop each)
uniform float bead_amount : hint_range(0.0, 1.0) = 1.0;  // coverage scale (1 = ~45 % of cells hold a drop in a downpour)
uniform float streak_amount : hint_range(0.0, 1.0) = 1.0;
uniform float refract_strength = 0.028;                  // screen-UV shift through a drop (~3 % of the frame at the rim)
uniform int debug_view = 0;                              // harness only (UG_GLASSDEBUGVIEW): 1 = tilt (R) + down-coordinate stripes (G) + across (B); 2 = runner mask; 3 = bead mask
uniform sampler2D screen_tex : hint_screen_texture, filter_linear_mipmap;

varying vec3 obj_pos;
varying vec3 pane_u;    // object space, ACROSS the pane
varying vec3 pane_v;    // object space, DOWN the pane (world down projected onto it)
varying float tilt;     // 0 = flat .. 1 = vertical
varying vec2 su;        // the pane's axes on SCREEN (view space xy), so a drop bends the scene radially whatever the pane's orientation
varying vec2 sv;        //   (MODELVIEW_MATRIX is a VERTEX-stage built-in; computed here, normalised in the fragment)

float h21(vec2 p){ p = fract(p * vec2(127.32, 311.7)); p += dot(p, p + 34.53); return fract(p.x * p.y); }

void vertex(){
    obj_pos = VERTEX;
    vec3 n = length(NORMAL) > 0.5 ? normalize(NORMAL) : vec3(0.0, 0.0, 1.0);   // a rip with no normals must not NaN the frame
    // world DOWN in object space: the rotation part of MODEL_MATRIX, transposed (a uniform scale normalises away)
    vec3 d = normalize(transpose(mat3(MODEL_MATRIX)) * vec3(0.0, -1.0, 0.0));
    vec3 dv = d - n * dot(d, n);           // down, projected onto the pane
    float dl = length(dv);
    tilt = clamp(dl, 0.0, 1.0);
    vec3 any_axis = abs(n.x) < 0.9 ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 0.0, 1.0);   // GUARD: a flat pane has no down
    any_axis = normalize(any_axis - n * dot(any_axis, n));
    pane_v = dl > 0.05 ? dv / dl : any_axis;
    pane_u = normalize(cross(n, pane_v));
    su = (MODELVIEW_MATRIX * vec4(pane_u, 0.0)).xy;
    sv = (MODELVIEW_MATRIX * vec4(pane_v, 0.0)).xy;
}

void fragment(){
    float rint = clamp(rain_intensity, 0.0, 1.0);
    float wet  = clamp(rain_wetness, 0.0, 1.0);
    float open = 1.0 - clamp(covered, 0.0, 1.0);   // see SHELTER above
    float amt = wet * open * bead_amount;                                              // water sitting on the glass
    float run = rint * open * streak_amount * smoothstep(0.25, 0.6, tilt);             // runners need rain NOW and a slope

    vec2 g = vec2(dot(obj_pos, pane_u), dot(obj_pos, pane_v));   // metres across / DOWN the pane, stuck to it
    float dist = length(VERTEX);                                  // view-space position = distance to the eye
    float detail = smoothstep(14.0, 5.0, dist);                   // beads are ~1 cm: gone past a few metres, no shimmer
    vec2 su_n = length(su) > 1e-4 ? normalize(su) : vec2(1.0, 0.0);   // screen-space pane axes (see the varyings)
    vec2 sv_n = length(sv) > 1e-4 ? normalize(sv) : vec2(0.0, -1.0);

    vec2 off = vec2(0.0);   // screen-UV shift of the scene seen through the water
    float cov = 0.0;        // how much of this pixel is water
    float rim = 0.0;        // the bright ring at a drop's edge

    // 1) BEADS: one jittered drop per cell, present when its seed falls under the coverage the wetness sets.
    if (amt > 0.001 && detail > 0.001) {
        vec2 cellp = g * bead_scale;
        vec2 id = floor(cellp);
        float seed = h21(id);
        float present = step(1.0 - 0.38 * amt, seed);
        vec2 c = id + 0.5 + (vec2(h21(id + 3.1), h21(id + 7.9)) - 0.5) * 0.4;    // centre, kept inside its cell
        float rs = h21(id + 11.3);
        float r = 0.10 + 0.16 * rs * rs + 0.12 * step(0.92, rs);                 // radius in cells: mostly small (~3 mm), a few fat ones (~1 cm)
        vec2 dp = (cellp - c) / r;                                                // 0 centre .. 1 rim
        float d2 = dot(dp, dp);
        float inside = present * detail * smoothstep(1.0, 0.78, d2);
        off += -(dp.x * su_n + dp.y * sv_n) * refract_strength * inside;              // a lens: the view bends toward the rim, inverted
        cov = max(cov, inside);
        rim += present * detail * smoothstep(0.5, 0.85, d2) * smoothstep(1.0, 0.9, d2);
    }

    // 2) RUNNERS: columns across the pane; each active column has a fat drop sliding DOWN it, trailing a thinning tail.
    if (run > 0.001) {
        float cols = 9.0;
        float cx = g.x * cols;
        float cid = floor(cx);
        float cs = h21(vec2(cid, 5.7));
        float active = step(1.0 - 0.6 * run, cs);                 // more rain -> more columns running
        float x0 = 0.5 + (cs - 0.5) * 0.5;                        // where in its column the runner lives
        float w = 0.07 + 0.06 * h21(vec2(cid, 2.3));               // half-width in column units (~0.8 .. 1.4 cm)
        float dxs = (fract(cx) - x0) / w;
        float dx = abs(dxs);
        float period = 1.2 + 1.5 * h21(vec2(cid, 9.1));            // metres of pane per pass
        float speed = 0.35 + 0.45 * h21(vec2(cid, 4.4));           // m/s down the glass
        float s = fract((TIME * speed + cs * period - g.y) / period);   // 0 at the head, growing UP the pane behind it
        float body = active * (1.0 - smoothstep(0.0, 0.25 + 0.2 * cs, s));       // along the trail, fading up the pane behind the head
        float tail = body * (1.0 - smoothstep(0.6, 1.0, dx));
        float headAmt = active * smoothstep(0.035, 0.0, s);
        float head = headAmt * (1.0 - smoothstep(0.9, 1.7, dx));
        float stk = max(tail * 0.7, head);
        off += -(dxs * 0.6 * su_n + 0.5 * sv_n) * refract_strength * stk;
        cov = max(cov, stk);
        // Refraction alone is INVISIBLE against a flat sky (bending a uniform grey shows nothing -- the first render had
        // runners in the mask and none on screen). What reads as a runner is its EDGES catching the light, same as the
        // bead rims: a highlight down both sides of the trail and a ring round the head.
        rim += body * smoothstep(0.35, 0.8, dx) * (1.0 - smoothstep(0.9, 1.15, dx)) * 0.7;
        rim += headAmt * smoothstep(0.8, 1.3, dx) * (1.0 - smoothstep(1.5, 1.9, dx));
    }

    vec3 col = tint.rgb;
    float alpha = tint.a;
    vec3 emit = vec3(0.0);
    if (cov > 0.002) {
        // The scene behind the drop, bent. It is ALREADY LIT (it is the rendered frame), so it goes out as EMISSION --
        // through ALBEDO it would be lit a second time by the overcast sky and every drop came out a dark grey disc.
        vec3 bg = textureLod(screen_tex, clamp(SCREEN_UV + off, vec2(0.001), vec2(0.999)), 0.0).rgb;
        float k = cov * 0.9;
        emit = bg * k;
        col = mix(col, vec3(0.0), k);        // the tint stays only where there is no water
        alpha = mix(alpha, 0.95, cov);       // a drop is a solid lens, not tinted air
    }
    emit += rim * 0.20 * max(amt, run);      // glints along the drops' rims
    if (debug_view == 1) { ALBEDO = vec3(0.0); EMISSION = vec3(tilt, fract(g.y * 4.0), fract(g.x * 4.0)); ALPHA = 1.0; }
    else if (debug_view == 2) { ALBEDO = vec3(0.0); EMISSION = vec3(cov, run, 0.0); ALPHA = 1.0; }
    else if (debug_view == 3) { ALBEDO = vec3(0.0); EMISSION = vec3(cov, amt, detail); ALPHA = 1.0; }
    else { ALBEDO = col; EMISSION = emit; ALPHA = alpha; }
    METALLIC = metallic;
    ROUGHNESS = mix(roughness, 0.03, cov);
    SPECULAR = 0.5;
}
