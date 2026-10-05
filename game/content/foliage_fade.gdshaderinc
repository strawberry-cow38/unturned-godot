// HOW FOLIAGE ERODES WITH DISTANCE -- the ONE definition, shared by grass_displace and foliage_up.
//
// ⚠⚠ THE DEFECT THIS EXISTS TO FIX IS THAT THERE WERE TWO COPIES AND THEY DISAGREED ABOUT WHAT `VERTEX` MEANS.
// grass_displace passed raw VERTEX as a WORLD position, on the stated grounds that `render_mode
// world_vertex_coords` "puts VERTEX in WORLD space in the fragment too". IT DOES NOT. That render_mode changes
// the VERTEX function only; in fragment(), VERTEX is view-space in every shader, world_vertex_coords or not.
//
// So grass computed length(viewPos - cameraWorldPos), which is essentially the camera's distance from the world
// ORIGIN -- 700-1400 m anywhere on PEI, past any fade end -- and every blade read as maximally distant. ALL
// GRASS, EVERYWHERE, at any camera position (master 2026-10-05: "all the grass is gone. flowers remain.. hm..").
//
// ⭐ AND "FLOWERS REMAIN" IS THE MEASUREMENT THAT PROVES THE DIAGNOSIS, not a detail of the report. foliage_up
// converted view->world before calling, so it was right; grass did not, so it was wrong. If VERTEX really were
// world-space in the fragment under that render_mode, the two would have failed the OTHER way round -- flowers
// gone, grass fine. The symptom distinguishes the two theories on its own.
//
// ⭐⭐ WHICH IS WHY THIS IS AN INCLUDE AND WHY IT TAKES A VIEW POSITION. The previous comment correctly said
// "passing an explicit world position makes the function independent of either render_mode" -- and then the two
// call sites passed positions in DIFFERENT SPACES, because each shader had to convert for itself and only one
// did. Taking the raw fragment VERTEX removes the choice: both call sites are now character-for-character
// identical, and the conversion happens once, here. Same rule water_optics.gdshaderinc exists for.

global uniform float foliage_fade_start;   // metres: erosion begins
global uniform float foliage_fade_end;     // metres: nothing left (<= the cell's VisibilityRangeEnd)

/// 1.0 near -> 0.0 by foliage_fade_end, measured from the CAMERA.
///
/// Fading ALPHA rather than discarding: these shaders are alpha-scissor, so lowering ALPHA makes more texels
/// fail the scissor the further out they are -- the most distant grass, densest on screen and several blades
/// per pixel, is the part that stops shading first. A fade-to-transparent would have added cost exactly there.
///
/// ⚠ `view_vertex` is the fragment's own VERTEX, UNCONVERTED. Do not "helpfully" pass a world position: this
/// subtracts the camera itself, and a caller that converts first double-counts the camera's offset.
///
/// ⚠ Guarded: an unregistered global reads 0, and a 0..0 range would erase every blade -- that failure looks
/// like "the foliage did not load", so it falls back to NO FADE rather than to no grass.
float foliage_fade_view(vec3 view_vertex, mat4 inv_view) {
    if (foliage_fade_end <= 0.001) return 1.0;
    vec3 world_pos = (inv_view * vec4(view_vertex, 1.0)).xyz;
    vec3 cam = inv_view[3].xyz;
    return 1.0 - smoothstep(foliage_fade_start, foliage_fade_end, length(world_pos - cam));
}
