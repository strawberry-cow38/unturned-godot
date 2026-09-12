shader_type spatial;
render_mode cull_disabled, diffuse_burley, specular_schlick_ggx;
// The swell field itself lives in its own include: the RAIN shader needs the same surface height, to the metre,
// to know where a drop stops. See swell.gdshaderinc (and WaveField.cs, the CPU twin).
#include "res://content/swell.gdshaderinc"

// NOISE-BASED ocean (master 2026-08-16, "drawing board"): the wave HEIGHT is ANISOTROPIC fbm noise -- organic
// directional swell with NO periodic term, so it physically cannot lattice the way summed sines always do.
// Big swell displaces the vertices (coarse-plane safe); the swell NORMAL, fine chop and ALL foam are
// evaluated per-pixel in the fragment, so nothing keyed to wave height follows the mesh triangles. Transparent,
// depth-tinted, crest foam on the peaks, depth-based shore foam. WaveField.cs is the CPU twin of swell_at().

// SLIGHTLY MORE OPAQUE AND MORE BLUE (strawberry 2026-09-08). Both moved a step, not a leap: deep alpha
// 0.72 -> 0.82 and shallow 0.26 -> 0.36, so the seabed still shows through the shallows (which is the whole
// reason the shallow alpha is low) but the water reads as water rather than as tinted glass. The hue shift is in
// the RATIO rather than the brightness -- red down, blue up, green roughly held -- because that is what makes a
// body of water look blue instead of merely darker.
uniform vec4 water_deep : source_color = vec4(0.012, 0.072, 0.30, 0.94);   // master 2026-09-12 "more blue, less transparent, more foggy": deeper navy, near-opaque
uniform vec4 water_shallow : source_color = vec4(0.045, 0.22, 0.52, 0.58); // shallows still read as shallow, but bluer and much less see-through
uniform vec4 foam_color : source_color = vec4(0.92, 0.97, 1.0, 1.0);
uniform float crest_foam = 0.36;      // swell-height fraction above which crest foam kicks in; raised -> fewer peak whitecaps (master)
uniform float shore_depth = 2.2;      // vertical water depth (m) under which shore foam shows (wider = fuller shore band)
// SILENCE BOTH FOAMS AT ONCE (strawberry 2026-09-09: "remove water reflections and foam in the bake").
// crest_foam can be pushed out of reach, but SHORE foam has no uniform of its own -- its band is hardcoded
// off the reconstructed depth below -- so a multiplier on the COMBINED foam is the only way to switch the
// pair off from outside the shader. Defaults 1.0: every existing render is byte-identical.
uniform float foam_amount : hint_range(0.0, 1.0) = 1.0;
uniform float detail_strength = 0.30; // fbm fine-ripple perturbation of the normal
uniform vec4 sky_tint : source_color = vec4(0.52, 0.67, 0.86, 1.0);  // colour reflected at grazing angles (the sky)
uniform float reflectivity = 0.45;    // how strongly the sky shows at grazing angles (Fresnel); lowered -> less white-out at grazing (master)
uniform sampler2D depth_tex : hint_depth_texture, repeat_disable, filter_linear;
uniform sampler2D reflection_tex : filter_linear;   // planar reflection buffer (mirror camera) -- WaterReflection.cs feeds it
uniform bool reflection_on = false;                 // off until the reflection node wires the buffer in
uniform bool reflection_debug = false;              // UG_REFLDEBUG: paint the raw reflection buffer onto the water (check mirror + UV)
uniform float reflection_strength = 1.0;            // UG_REFLSTR: scale the reflection's blend-in for tuning

varying vec3 v_wpos;     // world position of the displaced surface
// RAIN ON OPEN WATER (master 2026-09-06 "add rain ripple impacts to water surface"). The same expanding rings the
// roads and puddles show, on the sea -- rain does not stop at the shoreline, and a lake in a downpour that stays
// glassy is the one surface in the scene that looks wrong. Kept CHEAP and near-field: the field is only evaluated
// while it is actually raining and within rain_ring_distance, so a calm day and the far horizon cost nothing.
global uniform float rain_intensity;
uniform float rain_ring_distance = 70.0;
uniform float rain_ring_strength : hint_range(0.0, 2.0) = 0.9;
uniform float ring_normal_gain = 2.5;   // how hard a ring tilts the surface (see the NORMAL tweak at the end of fragment)

// WEATHER DRIVES THE SEA (master 2026-09-12 "have weather affect the water"). All four ride the SAME
// `rain_intensity` global the rain rings already read -- one weather signal, not a second one invented here that
// could drift out of step with the sky, the streaks and the wetness. At intensity 0 every term below is exactly
// 1.0/unchanged, so fair weather is byte-identical to before.
uniform float storm_chop = 2.1;        // ripple-detail multiplier at full rain: wind-driven chop
uniform float storm_foam = 0.55;       // crest_foam THRESHOLD multiplier: lower bar -> whitecaps appear sooner
uniform float storm_mirror = 0.42;     // reflectivity multiplier: a broken surface scatters instead of mirroring
uniform vec3 storm_tint : source_color = vec3(0.10, 0.14, 0.17);   // overcast grey-green the body colour shifts toward
uniform float storm_tint_amt = 0.40;   // how far toward storm_tint at full rain
float wr_h21(vec2 p){ p = fract(p * vec2(127.32, 311.7)); p += dot(p, p + 34.53); return fract(p.x * p.y); }
// Voronoi-ish scatter of expanding rings, the same shape wet_surface uses so water and wet road ring alike.
float wr_rings(vec2 wxz, float t, float amt){
    float acc = 0.0;
    float gate = 1.0 - clamp(amt, 0.0, 1.0) * 0.35;   // fraction of cells striking at once; heavy rain should look busy
    for (int k = 0; k < 2; k++){
        float sc = 0.9 + float(k) * 2.0;   // cells ~1.1 m and ~0.34 m across. The first pass used 1.6/4.4 -- rings ~20 cm and ~8 cm, which on open water from standing height were specks (the render showed them drawing and unreadable)
        vec2 g = wxz * sc + float(k) * 21.0;
        vec2 base = floor(g);
        for (int dy = -1; dy <= 1; dy++){
            for (int dx = -1; dx <= 1; dx++){
                vec2 id = base + vec2(float(dx), float(dy));
                float seed = wr_h21(id);
                float tt = t * (0.65 + seed * 0.7) + seed;
                float cyc = floor(tt), life = fract(tt);
                vec2 q = id + cyc * 13.7;
                vec2 centre = id + vec2(wr_h21(q + 1.3), wr_h21(q + 7.7));
                float rad = life * (0.16 + seed * 0.20);
                float ring = smoothstep(0.05, 0.0, abs(length(g - centre) - rad));
                acc += ring * (1.0 - life) * step(gate, wr_h21(q)) * (0.55 + seed * 0.45);
            }
        }
    }
    return acc;
}

void vertex() {
    vec2 wp = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xz;
    float tphase = swell_phase(TIME);
    VERTEX.y += swell_at(wp, tphase) * SWELL_AMP;
    v_wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;   // normal is computed PER-PIXEL in the fragment (below)
}

void fragment() {
    // PER-PIXEL SWELL NORMAL: sample the big-wave gradient HERE (not interpolated from the coarse mesh vertices) so
    // the big waves SHADE smooth even on a low-poly grid -> no grid/snap. The vertex still displaces the geometry.
    float tp = swell_phase(TIME);
    float se = 0.6;
    float sh  = swell_at(v_wpos.xz, tp);
    float shx = swell_at(v_wpos.xz + vec2(se, 0.0), tp);
    float shz = swell_at(v_wpos.xz + vec2(0.0, se), tp);
    vec3 swell_n = vec3((sh - shx) * SWELL_AMP / se, 1.0, (sh - shz) * SWELL_AMP / se);
    // DISTANCE LOD: fade the high-freq ripple detail out with distance. At a LOW/grazing angle (standing on the
    // shore) far water crushes many wavelengths into each pixel -> the normals alias into a BUSY shimmer, and the
    // Fresnel (below) turns that into a wall of glitter. Fading detail with distance keeps far/grazing water calm;
    // top-down (heli) is near-field everywhere so it's basically unaffected. (master: busy+opaque at low angle)
    float vdist = length(VERTEX);   // fragment distance from camera (view space)
    float lod = clamp(1.0 - (vdist - 25.0) / 140.0, 0.0, 1.0);   // 1 near (<25m) -> 0 far (>165m)
    // FINE RIPPLE DETAIL: fbm-gradient perturbs the normal (fragment space), LOD-faded with distance
    // 0.10 -> 0.055: the ripple CREASES roughly double in size (strawberry 2026-09-12 "up the size of the ripple
    // creases"; second time this has been scaled up, hence the note). The finite-difference epsilon below stays at
    // 0.06 ON PURPOSE -- it is in NOISE space, not world space, so it tracks the pattern automatically and the
    // creases keep their sharpness instead of flattening as they grow. Scaling it with the frequency would widen
    // the gradient stencil and soften exactly what was asked to be made bigger.
    vec2 np = v_wpos.xz * 0.055 + vec2(TIME * 0.03, TIME * -0.02);
    float n0 = fbm5(np);
    float nx = fbm5(np + vec2(0.06, 0.0));
    float nz = fbm5(np + vec2(0.0, 0.06));
    float storm = clamp(rain_intensity, 0.0, 1.0);
    // CHOP. The same fbm that makes the calm-water creases, pushed harder -- so a storm roughens the surface
    // the sea already has rather than layering a separate "storm pattern" over it.
    vec3 detail = vec3((n0 - nx), 0.0, (n0 - nz)) * detail_strength * 6.0 * lod * mix(1.0, storm_chop, storm);
    vec3 wn = normalize(swell_n + detail);
    NORMAL = normalize((VIEW_MATRIX * vec4(wn, 0.0)).xyz);

    // SHORE FOAM: reconstruct the opaque scene behind the water, foam where the water is shallow over it.
    float raw = texture(depth_tex, SCREEN_UV).r;
    vec4 vp = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, raw, 1.0);
    vp.xyz /= vp.w;
    float bed_y = (INV_VIEW_MATRIX * vec4(vp.xyz, 1.0)).y;
    float depth_v = max(v_wpos.y - bed_y, 0.0);
    float shore = 1.0 - smoothstep(0.0, 0.85, depth_v);       // WIDER band = more shore-foam AREA (master "up the amount")
    shore *= 0.45 + 0.15 * fbm5(v_wpos.xz * 0.4 + TIME * 0.15);  // ...but lower intensity -> 20% less opaque (master)

    // CREST FOAM, PER-PIXEL. It used to threshold a `v_crest` varying -- the swell height sampled at the mesh
    // CORNERS and linearly smeared between them. On the real map's coarse (~5 m) water grid that puts a hard
    // smoothstep edge across an interpolated ramp, so the whitecap boundary follows the triangle edges instead
    // of the wave: straight diagonal foam seams and caps that pop as the crest crosses a vertex. Exactly the
    // disease the swell NORMAL was moved to the fragment to cure, left behind on the foam.
    //
    // `sh` above is the same quantity evaluated HERE, per pixel, and it is already computed for the normal --
    // so this costs nothing. It is genuinely identical at a vertex: the vertex samples swell_at() at the
    // undisplaced world XZ, displacement is Y-only so v_wpos.xz is that same XZ, and the interpolation of a
    // planar XZ across a triangle is exact. Same input, same function, finer sampling.
    float capMask = fbm5(v_wpos.xz * 0.09 + vec2(TIME * 0.04, TIME * -0.03));
    float gate = smoothstep(-0.2, 0.05, capMask);   // stricter -> fewer peaks foam (master: tone down peak foam amount)
    // WHITECAPS. Lower the swell-height BAR in a storm rather than adding foam directly: the caps still appear
    // on real crests, there are just more of them, so they stay tied to the wave field instead of floating on it.
    float crest_bar = crest_foam * mix(1.0, storm_foam, storm);
    float crest = smoothstep(crest_bar, crest_bar + 0.20, sh) * gate;

    float foam = clamp(max(crest, shore) * 0.8 * foam_amount, 0.0, 1.0);   // all foam -20% opacity (master; whitecaps + shore softer)

    // SEE-THROUGH depth-tinted water
    // FOGGIER: reach the deep colour in roughly half the water depth it used to take, so the seabed is swallowed
    // sooner instead of staying legible well out from shore (master 2026-09-12 "more foggy"). shore_depth still
    // owns the FOAM band -- this is only the colour/opacity ramp, which is why it gets its own multiplier.
    float dt = smoothstep(0.0, shore_depth * 2.0, depth_v);
    vec3 base = mix(water_shallow.rgb, water_deep.rgb, dt);
    float a = mix(water_shallow.a, water_deep.a, dt);

    // FRESNEL SKY REFLECTION: grazing angles reflect the sky, so the horizon brightens + the water reads as reflective
    // (real water isn't flat-dark edge-on). Uses the ripple-perturbed normal, so the reflection shimmers with the chop.
    float fres = pow(1.0 - clamp(dot(NORMAL, VIEW), 0.0, 1.0), 4.0);
    // REFLECTION: sample the planar-reflection buffer (mirror camera), UV perturbed by the wave normal so the
    // reflection SHIMMERS with the chop. Where the buffer has coverage (alpha>0) that's a reflected tree/prop/
    // building; where it's empty, fall back to the flat sky tint. Both are Fresnel-gated below, so the reflection
    // only shows where the water reads edge-on -- top-down you see through, and it costs nothing there.
    vec3 refl_src = sky_tint.rgb;
    if (reflection_on) {
        vec2 refl_uv = clamp(SCREEN_UV + wn.xz * 0.04 * lod, vec2(0.001), vec2(0.999));   // exact negative-det mirror -> sample straight by SCREEN_UV; wn.xz ripple-distorts. (UG_REFLLOOKAT fallback needs a Y-flip; not wired since the exact mirror is default.)
        vec4 refl = texture(reflection_tex, refl_uv);
        // the mirror SubViewport renders empty sky as bright/near-white (no environment in the buffer), so gate by
        // DARKNESS instead of alpha: dark samples are reflected geometry (trees/props/ground) -> reflect them; bright
        // samples are empty sky -> fall back to the flat sky tint (no white-out of the water).
        float refl_geo = 1.0 - smoothstep(0.72, 0.90, dot(refl.rgb, vec3(0.3333)));
        refl_src = mix(sky_tint.rgb, refl.rgb, refl_geo);
    }
    // OVERCAST BODY COLOUR: a storm sky lights the water greyer, and this is also the only one of the four that
    // shows from straight down, where Fresnel contributes nothing.
    base = mix(base, storm_tint, storm * storm_tint_amt);
    // BROKEN SURFACE SCATTERS. Chop destroys the mirror, so the sky reflection has to fall off with weather --
    // otherwise a storm reads as a ROUGHER surface that is somehow still glassy, which looks wrong at the exact
    // grazing angles a player on a shore actually stands at.
    float refl_w = reflectivity * mix(1.0, storm_mirror, storm);
    vec3 water_col = mix(base, refl_src, clamp(fres * refl_w * reflection_strength, 0.0, 1.0));
    a = mix(a, 1.0, fres * refl_w * 0.25);   // gentler opacity bump at grazing -> stays see-through low-angle (master)

    // rain rings: a bright dimple where each drop lands, and a rougher patch around it so the reflection breaks up
    float rint_w = clamp(rain_intensity, 0.0, 1.0);
    float ring = 0.0;
    if (rint_w > 0.02) {
        float rrange = 1.0 - smoothstep(rain_ring_distance * 0.7, rain_ring_distance, length(VERTEX));
        if (rrange > 0.01) ring = wr_rings(v_wpos.xz, TIME, rint_w) * rint_w * rrange * rain_ring_strength;
    }
    ALBEDO = mix(water_col, foam_color.rgb, foam) + vec3(ring * 0.30);
    ALPHA = mix(a, 1.0, foam);
    float rough_base = mix(0.35, 0.08, lod);   // rougher (softer, less mirror-bright reflection) far -> calmer grazing; glassy near
    ROUGHNESS = mix(mix(rough_base, 0.9, foam), 0.55, clamp(ring, 0.0, 1.0));   // a struck patch scatters instead of mirroring
    // ...and TILT the surface where a ring passes. A brightness ring alone is a decal painted on flat water -- it
    // does not catch the sky differently, so it stays a faint smudge however bright it is. The screen-space
    // gradient of the ring is the direction the little wave is leaning, which is enough to make it break the
    // reflection like real disturbed water. (Gated on ring itself, so the distance cutoff's own branch -- where
    // ring is zero either side -- never feeds this a garbage derivative.)
    if (ring > 0.002) {
        vec2 rg = clamp(vec2(dFdx(ring), dFdy(ring)) * ring_normal_gain, vec2(-0.6), vec2(0.6));
        NORMAL = normalize(NORMAL + vec3(rg, 0.0));
    }
    // THE SUN'S OWN GLINT RIDES ON `reflectivity` TOO. The Fresnel blend above is only the SKY term; Godot's
    // specular lobe is a separate path, and zeroing reflectivity for the map bake still left a sun streak
    // burned across the middle of the sea. Scaling by the same uniform keeps "how reflective is this water"
    // as ONE number instead of two that can disagree -- and at the default 0.45 this is exactly the 0.5 it
    // has always been, so nothing else changes.
    SPECULAR = 0.5 * clamp(reflectivity / 0.45, 0.0, 1.0);
    METALLIC = 0.0;
    // UG_REFLDEBUG: overwrite the water with the raw reflection buffer to eyeball the mirror camera + UV alignment
    if (reflection_on && reflection_debug) { ALBEDO = texture(reflection_tex, SCREEN_UV).rgb; ALPHA = 1.0; ROUGHNESS = 1.0; }
}
