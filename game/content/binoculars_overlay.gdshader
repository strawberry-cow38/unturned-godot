// The retail binoculars overlay (ui/player/overlay/binoculars.png, PlayerLifeUI.binocularsOverlay): black with two
// round holes, drawn over the zoomed main camera while the pair is at the eyes -- retail's "dumb overlay" (master).
shader_type canvas_item;
uniform sampler2D mask : filter_linear;
void fragment() {
    COLOR = vec4(0.0, 0.0, 0.0, texture(mask, UV).a);   // the retail png is black with alpha 255 outside the lenses, 0 inside
}
