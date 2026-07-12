// Screen-space item outline (our own -- Unturned's is a licensed asset). Fullscreen dilate pass: TEXTURE is an
// offscreen mask that has ONLY the focused item rendered solid (white on transparent, via OutlineOverlay's SubViewport).
// A pixel that is OUTSIDE the mask but within `thickness` px of a mask pixel = the silhouette RIM -> draw outline_color.
// Topology-independent (works on the flat double-sided ripped item meshes where an inverted-hull outline fails).
shader_type canvas_item;

uniform vec3 outline_color : source_color = vec3(1.0);
uniform float thickness = 2.5;

void fragment() {
    vec2 px = thickness / vec2(textureSize(TEXTURE, 0));
    float here = texture(TEXTURE, UV).a;
    if (here > 0.5) {
        COLOR = vec4(0.0);          // inside the item silhouette -> transparent so the real item shows through from below
    } else {
        float m = 0.0;              // max mask coverage among the 8 neighbours at `thickness` px
        m = max(m, texture(TEXTURE, UV + vec2( px.x, 0.0)).a);
        m = max(m, texture(TEXTURE, UV + vec2(-px.x, 0.0)).a);
        m = max(m, texture(TEXTURE, UV + vec2(0.0,  px.y)).a);
        m = max(m, texture(TEXTURE, UV + vec2(0.0, -px.y)).a);
        m = max(m, texture(TEXTURE, UV + vec2( px.x,  px.y)).a);
        m = max(m, texture(TEXTURE, UV + vec2(-px.x, -px.y)).a);
        m = max(m, texture(TEXTURE, UV + vec2( px.x, -px.y)).a);
        m = max(m, texture(TEXTURE, UV + vec2(-px.x,  px.y)).a);
        COLOR = vec4(outline_color, m);   // rarity rim where just outside the silhouette
    }
}
