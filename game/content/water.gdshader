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
uniform float reflectivity = 1.0;     // master 2026-10-09: "increase the reflectivity of water by a lot"
// ⭐⭐ HOW FAR A *BRIGHT* REFLECTION IS PULLED BACK TOWARD THE WATER'S OWN COLOUR. This is the whole answer to
// "very reflective" AND "not milky", which sound contradictory and are not.
//
// PEI's retail Lighting.dat puts the midday horizon at (0.784,0.784,0.784) -- near WHITE -- and water at a
// grazing angle mirrors the horizon band almost exclusively. So a strong mirror reflects a white sheet, and
// that is not a bug: it is what a mirror of that sky IS. Reproduced on purpose once the harness was given the
// real sky gradient; it went from blue to milk without the shader changing at all.
//
// Damping by BRIGHTNESS separates the two things master wants differently treated: reflected SCENERY (hills,
// trees, buildings) is dark and passes through untouched, so the water stays very reflective of the world;
// reflected SKY is bright and gets pulled toward the water colour, so it reads as blue water rather than a
// white sheet. ⚠ This is the good version of the darkness heuristic I deleted two commits ago -- that one
// SWAPPED to a hand-picked constant on a hard threshold and ran on inflated (un-linearised) values, which is
// how it mistook sunlit trees for sky. This blends smoothly, toward the body colour the water already has,
// on correct values.
uniform float sky_damp : hint_range(0.0, 1.0) = 0.75;
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

// The EXACT sRGB -> linear transfer, not pow(2.2). The approximation left the reflected sky about 6% bright
// against a known-answer check (reflected sky vs the real sky in the same frame); this lands on it. Cheap, and
// the whole point of the correction is that the number is right.
vec3 srgb_to_linear(vec3 c) {
    return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c));
}

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
        // ⭐ USED WHOLE, no gate. The mirror buffer now contains the scene's OWN sky (WaterReflection no longer
        // clears it to transparent), so a bright sample IS the sky rather than something to be guessed at and
        // replaced. The brightness heuristic that used to live here existed only because the buffer had no sky in
        // it, and it could not tell a sunlit canopy from a cloud -- which is exactly how reflected trees came out
        // white. Deleting the guess is the fix; `sky_tint` survives only for the no-mirror fallback above.
        // ⚠⚠ sRGB -> LINEAR ON THE WAY OUT OF THE MIRROR. MEASURED, not assumed: the harness environment's
        // BackgroundColor is linear (0.5,0.6,0.75); the MAIN view renders that sky as srgb(127,153,191) -- the
        // components straight through -- while the same sky sampled out of this buffer comes back
        // srgb(196,211,233), which is that colour sRGB-ENCODED. The SubViewport's render target is encoded on
        // write and sampling it does not undo that, so every reflection arrived ~50% too bright. That is
        // master's "the reflections are white in game", and it is why reflected trees read white before: the
        // brightness heuristic that used to guess sky-vs-geometry was being fed inflated values.
        refl_src = srgb_to_linear(clamp(texture(reflection_tex, refl_uv).rgb, 0.0, 1.0));
        // Bright = sky, dark = scenery. Rec.709 luma, so a bright-but-coloured reflection (sunlit sand) is
        // judged on how bright it actually reads rather than on one channel.
        float rl = dot(refl_src, vec3(0.2126, 0.7152, 0.0722));
        refl_src = mix(refl_src, water_shallow.rgb, smoothstep(0.25, 0.85, rl) * sky_damp);
    }

    // ⭐⭐ BACK TO SCHLICK'S 5.0, AND THE OLD COMMENT IS WHY. It had been lowered to 2.5 to widen reflection into
    // mid angles, justified like this: "Schlick's physical 5.0 is for a FLAT OPTICAL INTERFACE; stylised water is
    // chop-perturbed over a whole pixel footprint, which flattens the real curve the same way." That compensation
    // was correct for a chopped sea. The surface is now literally a flat optical interface, so the compensation is
    // backwards -- 2.5 put most of the visible sea above 60% mirror, which is master's "very milky and white in
    // game". At 5.0 the near and middle water reads through to the seabed (which is what "crystal clear" asked
    // for) and the mirror concentrates toward the horizon, where a flat surface genuinely does reflect.
    // ⭐ 5.0 -> 3.0, AND THIS IS NOT A REVERSAL OF THE 2.5 -> 5.0 THAT KILLED THE MILK. Two different causes
    // were conflated in that one number. The milk was mostly the sRGB bug one commit earlier: the mirror was
    // handing back colours ~50% too bright, so WIDENING the reflective band spread a wrong colour over more sea.
    // 5.0 cured the symptom by reflecting less of it. With the colour correct, the band can open back up and what
    // arrives is a real reflection rather than a wash -- which is what "more reflective" has to mean on water
    // that is already a flat mirror. Stopped at 3.0 rather than going back to 2.5: 5.0 is the physical value for
    // a flat interface, so every step below it is a stylistic lie, and the point is to spend it where it buys a
    // visible reflection (the mid angles a player actually looks at) without flattening the near water you can
    // see the seabed through.
    float fres = pow(1.0 - clamp(dot(NORMAL, VIEW), 0.0, 1.0), 2.0);
    float mirror = clamp(fres * reflectivity * reflection_strength, 0.0, 1.0);

    // The body tint comes from the SHARED optics include rather than a colour of this file's own, because the
    // submerged view veils with the same one -- two shaders each holding a water colour is precisely how the
    // surface and the underwater fog stopped matching last time.
    vec3 body = water_shallow.rgb;
    ALBEDO = mix(mix(body, refl_src, mirror), foam_color.rgb, foam);
    // Clear looking down, mirror looking across. Foam is opaque wherever it is.
    ALPHA = clamp(mix(mix(clarity, 1.0, mirror), 1.0, foam), 0.0, 1.0);
    // ⭐⭐ MATTE, AND NO SPECULAR -- BECAUSE THE MIRROR ALREADY REFLECTS THE SKY. A glossy surface makes Godot
    // reflect the environment through its OWN specular/IBL path, and this shader is simultaneously reflecting
    // that same sky explicitly through reflection_tex. With PEI's near-white midday horizon that double-count is
    // most of the milk: ablating these two dropped the horizon band from srgb(175,194,223) to (119,143,182) in
    // one step, far more than damping the reflection itself achieved.
    // ⚠ It does NOT cost reflectivity -- the opposite. What the engine's lobe was adding was white GLARE spread
    // over the ripples, not an image; the hills and shoreline are more legible in the water with it off. A
    // planar-reflection shader should own its reflection outright rather than get a second, blurrier one for
    // free from the environment.
    ROUGHNESS = mix(1.0, 0.9, foam);
    SPECULAR = 0.0;   // see ROUGHNESS above: reflection_tex is the reflection, the engine must not add a second
    METALLIC = 0.0;
    if (reflection_on && reflection_debug) { ALBEDO = srgb_to_linear(clamp(texture(reflection_tex, SCREEN_UV).rgb, 0.0, 1.0)); ALPHA = 1.0; ROUGHNESS = 1.0; }
}
