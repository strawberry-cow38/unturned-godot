shader_type spatial;
render_mode cull_disabled, diffuse_burley, specular_schlick_ggx;

// ============================================================================================================
// FLAT WATER. Master 2026-10-09: "rip out our entire water system until we are left with a totally flat water
// plane with no effects. keep caustics and shore foam so we could revert those if we want them."
//
// What this file used to be: 277 lines of anisotropic fbm swell (vertex displacement + a per-pixel swell
// normal), fine-ripple chop, crest-foam whitecaps, depth-tinted transparency, a Fresnel sky term, a planar
// mirror reflection, rain rings, and a storm response that pushed all of the above. It is all gone. Recover any
// of it with `git show 2d10f3d0:game/content/water.gdshader` -- this strip is one commit, so the old file is one
// command away and nothing was lost.
//
// WHAT SURVIVES, AND WHY ONLY THIS:
//   * SHORE FOAM, behind `shore_foam_on` (OFF). Master named it as a keeper, so it is here as a switch rather
//     than in the history -- flip the bool and the band is back, untouched.
//   * The water_optics include, because it OWNS water_deep/water_shallow and underwater.gdshader veils the
//     submerged view with the same colours. Stripping the surface must not make the two disagree -- that exact
//     mismatch is a bug master already caught once ("the color of the underwater fog doesnt match the surface").
//
// ⭐ THE GEOMETRY IS NOT FLATTENED HERE. The plane is flat because WaveField.Flat zeroes the swell at its
// source, which also flattens the CPU twin (buoyancy, WaterSurfaceY) and rain_streak.gdshader, which reads the
// same surface height. Flattening only the visual would have floated boats on an invisible swell.
// ============================================================================================================

#include "res://content/water_optics.gdshaderinc"

uniform vec4 foam_color : source_color = vec4(0.92, 0.97, 1.0, 1.0);
uniform bool shore_foam_on = false;   // master's keeper: the shoreline band, one flip from back
uniform float shore_depth = 2.2;      // vertical water depth (m) under which shore foam shows
uniform sampler2D depth_tex : hint_depth_texture, repeat_disable, filter_linear;

varying vec3 v_wpos;

void vertex() {
    // No displacement. The mesh is the surface.
    v_wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
    // Straight up, everywhere. No swell gradient, no chop.
    NORMAL = normalize((VIEW_MATRIX * vec4(0.0, 1.0, 0.0, 0.0)).xyz);

    float foam = 0.0;
    if (shore_foam_on) {
        // KEPT VERBATIM from the stripped shader: reconstruct the opaque scene behind the water and foam where
        // the water is shallow over it. Depth-based, so it is angle-independent and follows the real shoreline.
        float raw = texture(depth_tex, SCREEN_UV).r;
        vec4 vp = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, raw, 1.0);
        vp.xyz /= vp.w;
        float bed_y = (INV_VIEW_MATRIX * vec4(vp.xyz, 1.0)).y;
        float depth_v = max(v_wpos.y - bed_y, 0.0);
        foam = (1.0 - smoothstep(0.0, 0.85, depth_v)) * 0.45;
    }

    // ONE flat colour and ONE alpha -- no depth ramp, so a shallow is the same water as a deep. The alpha is
    // water_deep's: the sea stays see-through enough that the caustics read when they are switched back on.
    ALBEDO = mix(water_deep.rgb, foam_color.rgb, foam);
    ALPHA = mix(water_deep.a, 1.0, foam);
    ROUGHNESS = 1.0;   // fully matte: no sheen, and no sun glint (SPECULAR 0) -- a glint is an effect
    SPECULAR = 0.0;
    METALLIC = 0.0;
}
