// Screen-space item outline (our own -- Unturned's is a licensed asset). Fullscreen dilate pass: TEXTURE is an
// offscreen mask that has ONLY the focused item rendered solid (white on transparent, via OutlineOverlay's SubViewport).
// A pixel that is OUTSIDE the mask but within `thickness` px of a mask pixel = the silhouette RIM -> draw outline_color.
// Topology-independent (works on the flat double-sided ripped item meshes where an inverted-hull outline fails).
shader_type canvas_item;

uniform vec3 outline_color : source_color = vec3(1.0);
uniform float thickness = 2.5;

// UNDERWATER: the rim refracts with everything else. This pass is a CanvasLayer drawn OVER the 3D underwater
// quad, so it never saw that quad's refraction -- the world wobbled and the outline stayed welded to the item's
// TRUE silhouette, sliding off it by a few pixels (strawberry 2026-09-15). The fix is to sample the mask through
// the SAME displacement underwater.gdshader samples the screen through: identical expression, identical TIME,
// and the amplitude/speed/submersion are pushed from Underwater.cs so the two cannot drift apart.
//
// Same sign, not the inverse. The underwater pass draws at pixel `uv` whatever the scene had at `uv + warp`, so
// the item's image lands at (its position - warp); sampling the mask at `uv + warp` puts the rim in the same
// place. Reading it as "undo the refraction" and negating gives a rim that wobbles the WRONG WAY at double
// amplitude, which looks like a bug rather than like water.
uniform float submersion = 0.0;      // 0 = above water: the whole block below is then a no-op
uniform float wobble = 0.006;
uniform float wobble_speed = 1.4;

void fragment() {
    vec2 px = thickness / vec2(textureSize(TEXTURE, 0));
    vec2 uv = UV;
    if (submersion > 0.0) {
        float t = TIME * wobble_speed;
        uv = clamp(uv + vec2(
            sin(uv.y * 22.0 + t * 1.7) + 0.6 * sin(uv.y * 41.0 - t * 2.3),
            cos(uv.x * 19.0 + t * 1.3) + 0.6 * cos(uv.x * 37.0 + t * 2.9)
        ) * wobble * submersion, vec2(0.001), vec2(0.999));
    }
    float here = texture(TEXTURE, uv).a;
    if (here > 0.5) {
        COLOR = vec4(0.0);          // inside the item silhouette -> transparent so the real item shows through from below
    } else {
        float m = 0.0;              // max mask coverage among the 8 neighbours at `thickness` px
        m = max(m, texture(TEXTURE, uv + vec2( px.x, 0.0)).a);
        m = max(m, texture(TEXTURE, uv + vec2(-px.x, 0.0)).a);
        m = max(m, texture(TEXTURE, uv + vec2(0.0,  px.y)).a);
        m = max(m, texture(TEXTURE, uv + vec2(0.0, -px.y)).a);
        m = max(m, texture(TEXTURE, uv + vec2( px.x,  px.y)).a);
        m = max(m, texture(TEXTURE, uv + vec2(-px.x, -px.y)).a);
        m = max(m, texture(TEXTURE, uv + vec2( px.x, -px.y)).a);
        m = max(m, texture(TEXTURE, uv + vec2(-px.x,  px.y)).a);
        COLOR = vec4(outline_color, m);   // rarity rim where just outside the silhouette
    }
}
