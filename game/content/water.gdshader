shader_type spatial;
render_mode cull_disabled, diffuse_burley, specular_schlick_ggx;

// NOISE-BASED ocean (master 2026-08-16, "drawing board"): the wave HEIGHT is ANISOTROPIC fbm noise -- organic
// directional swell with NO periodic term, so it physically cannot lattice the way summed sines always do.
// Big swell displaces the vertices (coarse-plane safe); the swell NORMAL, fine chop and ALL foam are
// evaluated per-pixel in the fragment, so nothing keyed to wave height follows the mesh triangles. Transparent,
// depth-tinted, crest foam on the peaks, depth-based shore foam. WaveField.cs is the CPU twin of swell_at().

uniform vec4 water_deep : source_color = vec4(0.04, 0.11, 0.22, 0.72);     // darker navy blue (master); alpha < 1 = see-through
uniform vec4 water_shallow : source_color = vec4(0.10, 0.28, 0.40, 0.26);  // darker blue (master); shallows very transparent -> seabed shows
uniform vec4 foam_color : source_color = vec4(0.92, 0.97, 1.0, 1.0);
uniform float swell_amp = 0.5;        // metres of vertical swell
uniform float swell_dir_deg = 30.0;   // swell travel direction
uniform float swell_fu = 0.081;       // noise freq ALONG travel (lowered 10% -> waves ~10% bigger, master "zoom in")
uniform float swell_fw = 0.027;       // noise freq ALONG the crest (lower = longer crests); fu/fw = 3:1 anisotropy (master's pick)
uniform float swell_speed = 3.0;
uniform float crest_foam = 0.36;      // swell-height fraction above which crest foam kicks in; raised -> fewer peak whitecaps (master)
uniform float shore_depth = 2.2;      // vertical water depth (m) under which shore foam shows (wider = fuller shore band)
uniform float detail_strength = 0.30; // fbm fine-ripple perturbation of the normal
uniform vec4 sky_tint : source_color = vec4(0.52, 0.67, 0.86, 1.0);  // colour reflected at grazing angles (the sky)
uniform float reflectivity = 0.45;    // how strongly the sky shows at grazing angles (Fresnel); lowered -> less white-out at grazing (master)
uniform sampler2D depth_tex : hint_depth_texture, repeat_disable, filter_linear;
uniform sampler2D reflection_tex : filter_linear;   // planar reflection buffer (mirror camera) -- WaterReflection.cs feeds it
uniform bool reflection_on = false;                 // off until the reflection node wires the buffer in
uniform bool reflection_debug = false;              // UG_REFLDEBUG: paint the raw reflection buffer onto the water (check mirror + UV)
uniform float reflection_strength = 1.0;            // UG_REFLSTR: scale the reflection's blend-in for tuning

varying vec3 v_wpos;     // world position of the displaced surface

// GRADIENT (Perlin) noise -- unlike value noise it has NO axis-aligned cell artifacts (value is 0 at every lattice
// point, random gradients interpolated), so the surface doesn't read as blocks/squares or SNAP as it scrolls.
// Same seed/formula in WaveField.cs so the CPU twin still agrees.
// precision-robust hash (Hoskins) -- fract() bounds the input FIRST, so it doesn't degrade at large world coords
// like fract(sin(...)) does. That sin-hash correlates into a GRID once the map's coords get big (the real bug).
float hashv(vec2 p) { vec3 p3 = fract(vec3(p.xyx) * 0.1031); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.x + p3.y) * p3.z); }
vec2 grad2(vec2 ip) { float h = hashv(ip) * 6.2831853; return vec2(cos(h), sin(h)); }
float gnoise(vec2 p) {
    vec2 i = floor(p), f = fract(p);
    vec2 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);   // quintic
    float a = dot(grad2(i), f);
    float b = dot(grad2(i + vec2(1.0, 0.0)), f - vec2(1.0, 0.0));
    float c = dot(grad2(i + vec2(0.0, 1.0)), f - vec2(0.0, 1.0));
    float d = dot(grad2(i + vec2(1.0, 1.0)), f - vec2(1.0, 1.0));
    return mix(mix(a, b, u.x), mix(c, d, u.x), u.y) * 0.8 + 0.5;   // remap ~[-0.6,0.6] -> ~[0,1]
}
float fbm5(vec2 p) { float s = 0.0, a = 0.5; for (int i = 0; i < 5; i++) { s += a * (gnoise(p) - 0.5); p *= 2.03; a *= 0.5; } return s; }
float fbm3(vec2 p) { float s = 0.0, a = 0.5; for (int i = 0; i < 3; i++) { s += a * (gnoise(p) - 0.5); p *= 2.03; a *= 0.5; } return s / 0.4375; }

// ANISOTROPIC swell height at world XZ + phase-time (coarse 3-octave swell; the vertex displaces this)
float swell_at(vec2 wp, float tphase) {
    float a = radians(swell_dir_deg); float c = cos(a), s = sin(a);
    float u = wp.x * c + wp.y * s;    // along travel
    float w = -wp.x * s + wp.y * c;   // along the crest line
    return fbm3(vec2(u * swell_fu + tphase, w * swell_fw));
}

void vertex() {
    vec2 wp = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xz;
    float tphase = TIME * swell_speed * swell_fu;   // scroll the field along the travel axis
    float h = swell_at(wp, tphase);
    VERTEX.y += h * swell_amp;
    v_wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;   // normal is computed PER-PIXEL in the fragment (below)
}

void fragment() {
    // PER-PIXEL SWELL NORMAL: sample the big-wave gradient HERE (not interpolated from the coarse mesh vertices) so
    // the big waves SHADE smooth even on a low-poly grid -> no grid/snap. The vertex still displaces the geometry.
    float tp = TIME * swell_speed * swell_fu;
    float se = 0.6;
    float sh  = swell_at(v_wpos.xz, tp);
    float shx = swell_at(v_wpos.xz + vec2(se, 0.0), tp);
    float shz = swell_at(v_wpos.xz + vec2(0.0, se), tp);
    vec3 swell_n = vec3((sh - shx) * swell_amp / se, 1.0, (sh - shz) * swell_amp / se);
    // DISTANCE LOD: fade the high-freq ripple detail out with distance. At a LOW/grazing angle (standing on the
    // shore) far water crushes many wavelengths into each pixel -> the normals alias into a BUSY shimmer, and the
    // Fresnel (below) turns that into a wall of glitter. Fading detail with distance keeps far/grazing water calm;
    // top-down (heli) is near-field everywhere so it's basically unaffected. (master: busy+opaque at low angle)
    float vdist = length(VERTEX);   // fragment distance from camera (view space)
    float lod = clamp(1.0 - (vdist - 25.0) / 140.0, 0.0, 1.0);   // 1 near (<25m) -> 0 far (>165m)
    // FINE RIPPLE DETAIL: fbm-gradient perturbs the normal (fragment space), LOD-faded with distance
    vec2 np = v_wpos.xz * 0.10 + vec2(TIME * 0.03, TIME * -0.02);   // fine reflection ripples SCALED UP bigger (master)
    float n0 = fbm5(np);
    float nx = fbm5(np + vec2(0.06, 0.0));
    float nz = fbm5(np + vec2(0.0, 0.06));
    vec3 detail = vec3((n0 - nx), 0.0, (n0 - nz)) * detail_strength * 6.0 * lod;
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
    float crest = smoothstep(crest_foam, crest_foam + 0.20, sh) * gate;

    float foam = clamp(max(crest, shore) * 0.8, 0.0, 1.0);   // all foam -20% opacity (master; whitecaps + shore softer)

    // SEE-THROUGH depth-tinted water
    float dt = smoothstep(0.0, shore_depth * 4.0, depth_v);
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
    vec3 water_col = mix(base, refl_src, clamp(fres * reflectivity * reflection_strength, 0.0, 1.0));
    a = mix(a, 1.0, fres * reflectivity * 0.25);   // gentler opacity bump at grazing -> stays see-through low-angle (master)

    ALBEDO = mix(water_col, foam_color.rgb, foam);
    ALPHA = mix(a, 1.0, foam);
    float rough_base = mix(0.35, 0.08, lod);   // rougher (softer, less mirror-bright reflection) far -> calmer grazing; glassy near
    ROUGHNESS = mix(rough_base, 0.9, foam);
    SPECULAR = 0.5;
    METALLIC = 0.0;
    // UG_REFLDEBUG: overwrite the water with the raw reflection buffer to eyeball the mirror camera + UV alignment
    if (reflection_on && reflection_debug) { ALBEDO = texture(reflection_tex, SCREEN_UV).rgb; ALPHA = 1.0; ROUGHNESS = 1.0; }
}
