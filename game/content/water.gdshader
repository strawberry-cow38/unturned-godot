shader_type spatial;
render_mode cull_disabled, diffuse_burley, specular_schlick_ggx;

// ============================================================================================================
// CRYSTAL-CLEAR WATER: ripples and real reflections, on a FLAT surface.
// Master 2026-10-09, after the strip: "crystal clear water with ripples, and real reflections".
//
// ⭐ RIPPLES, NOT WAVES -- and that distinction is the whole design. The swell this file used to displace
// vertices by is still gone (WaveField.Flat), so the surface is geometrically flat and boats, rain and
// Terrain.WaterSurfaceY all agree with the picture. A ripple is a NORMAL, not a height: it costs no geometry,
// cannot facet on the coarse ocean grid, and cannot drift from the CPU twin, because there is nothing to drift.
//
// ⭐ "CRYSTAL CLEAR" IS AN ALPHA THAT MOVES WITH THE VIEW, not a low number. Straight down the surface is
// almost not there (clarity) and you read the seabed; at a grazing angle Fresnel takes it to opaque and you
// read the reflection. That is the same thing real water does, and it is why clear water does not look like
// weak water -- you lose nothing looking down and gain a mirror looking across.
//
// The noise comes from swell.gdshaderinc for its HASH, not its swell: hashv is the Hoskins hash that replaced
// fract(sin()), which gridded at real-map world coordinates. Ripples are sampled in world XZ out at thousands
// of units, so this file would reproduce that exact bug with any other hash.
// ============================================================================================================

#include "res://content/swell.gdshaderinc"
#include "res://content/water_optics.gdshaderinc"

uniform float clarity : hint_range(0.0, 1.0) = 0.10;   // surface alpha looking straight DOWN -- the see-through end
uniform float ripple_freq = 0.055;      // world-space ripple frequency (the tuned fine-detail scale)
uniform float ripple_strength = 0.30;   // how hard a ripple tilts the surface
uniform vec4 sky_tint : source_color = vec4(0.52, 0.67, 0.86, 1.0);
uniform float reflectivity = 0.80;
uniform sampler2D reflection_tex : filter_linear;
uniform bool reflection_on = false;
uniform bool reflection_debug = false;
uniform float reflection_strength = 1.0;

// -- SHORE FOAM, back on (master 2026-10-09: "add back the shore foam only, but slightly reduced") --
// ⭐ THE OLD EFFECTIVE NUMBER, THEN REDUCED -- not a fresh guess. Pre-strip this was
// `(0.45 + 0.15*fbm5(...)) * 0.8`, the 0.8 being an all-foam cut master asked for separately, so what actually
// reached the screen was 0.36 +/- 0.06. "Slightly reduced" is that, 20% down: 0.29 +/- 0.05. Reading the old
// value out of the two places it was split across beats inventing a number that merely looks smaller.
uniform bool shore_foam_on = true;
uniform float shore_foam_amount = 0.29;   // mean peak opacity of the band (old effective: 0.36)
uniform float shore_foam_swing = 0.10;    // how much the noise breathes it (old effective: 0.12)
uniform vec4 foam_color : source_color = vec4(0.92, 0.97, 1.0, 1.0);
uniform sampler2D depth_tex : hint_depth_texture, repeat_disable, filter_linear;

varying vec3 v_wpos;

void vertex() {
    v_wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;   // flat: the mesh IS the surface
}

void fragment() {
    // DISTANCE LOD on the ripple detail. Kept from the pre-strip shader because it fixes a real complaint, and
    // reflections make it matter MORE, not less: at a grazing angle far water crushes many ripples into each
    // pixel, every normal goes edge-on at once, and the Fresnel term turns that aliasing into a wall of
    // glitter. Master's word for it was "busy". Near water keeps its full detail.
    float vdist = length(VERTEX);
    float lod = clamp(1.0 - (vdist - 25.0) / 140.0, 0.0, 1.0);

    vec2 np = v_wpos.xz * ripple_freq + vec2(TIME * 0.03, TIME * -0.02);
    float n0 = fbm5(np);
    float nx = fbm5(np + vec2(0.06, 0.0));
    float nz = fbm5(np + vec2(0.0, 0.06));
    // ⚠ The epsilon stays in NOISE space, not world space, so it tracks ripple_freq automatically -- scaling it
    // alongside would widen the gradient stencil and soften exactly what the frequency just sharpened.
    vec3 wn = normalize(vec3((n0 - nx), 0.0, (n0 - nz)) * ripple_strength * 6.0 * lod + vec3(0.0, 1.0, 0.0));
    NORMAL = normalize((VIEW_MATRIX * vec4(wn, 0.0)).xyz);

    float foam = 0.0;
    if (shore_foam_on) {
        float raw = texture(depth_tex, SCREEN_UV).r;
        vec4 vp = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, raw, 1.0);
        vp.xyz /= vp.w;
        float bed_y = (INV_VIEW_MATRIX * vec4(vp.xyz, 1.0)).y;
        // Depth-based, so the band follows the real shoreline and is angle-independent -- it does not slide
        // around as the camera moves the way a view-dependent fake would.
        float shore = 1.0 - smoothstep(0.0, 0.85, max(v_wpos.y - bed_y, 0.0));
        // ...modulated by its own slow noise, which is what stops it reading as a painted ring around the island.
        // The strip had flattened this to a constant; a flat band is the thing that looks wrong, not the amount.
        foam = shore * (shore_foam_amount + shore_foam_swing * fbm5(v_wpos.xz * 0.4 + TIME * 0.15));
    }
    foam = clamp(foam, 0.0, 1.0);

    // REAL REFLECTION: the planar mirror buffer (WaterReflection.cs renders the world a second time about the
    // water plane), UV-perturbed by the ripple normal so the reflection breaks up with the surface.
    vec3 refl_src = sky_tint.rgb;
    if (reflection_on) {
        vec2 refl_uv = clamp(SCREEN_UV + wn.xz * 0.04 * lod, vec2(0.001), vec2(0.999));
        vec4 refl = texture(reflection_tex, refl_uv);
        // Gate by DARKNESS, not alpha: the mirror viewport has no environment, so empty sky comes back near-white.
        // Dark samples are reflected geometry (trees, props, ground) and get reflected; bright ones fall back to
        // the flat sky tint. ⚠ 0.72/0.90 are the numbers this was tuned at -- widening the top is how bright sky
        // starts compositing as though it were geometry, which is the "trees in the reflection are just white"
        // report. Not a knob to open up.
        float refl_geo = 1.0 - smoothstep(0.72, 0.90, dot(refl.rgb, vec3(0.3333)));
        refl_src = mix(sky_tint.rgb, refl.rgb, refl_geo);
    }

    float fres = pow(1.0 - clamp(dot(NORMAL, VIEW), 0.0, 1.0), 2.5);
    float mirror = clamp(fres * reflectivity * reflection_strength, 0.0, 1.0);

    // The body tint comes from the SHARED optics include rather than a colour of this file's own, because the
    // submerged view veils with the same one -- two shaders each holding a water colour is precisely how the
    // surface and the underwater fog stopped matching last time.
    vec3 body = water_shallow.rgb;
    ALBEDO = mix(mix(body, refl_src, mirror), foam_color.rgb, foam);
    // Clear looking down, mirror looking across. Foam is opaque wherever it is.
    ALPHA = clamp(mix(mix(clarity, 1.0, mirror), 1.0, foam), 0.0, 1.0);
    ROUGHNESS = mix(0.08, 0.9, foam);   // glassy, so the ripples catch a specular highlight
    SPECULAR = 0.5 * clamp(reflectivity / 0.45, 0.0, 1.0);
    METALLIC = 0.0;
    if (reflection_on && reflection_debug) { ALBEDO = texture(reflection_tex, SCREEN_UV).rgb; ALPHA = 1.0; ROUGHNESS = 1.0; }
}
