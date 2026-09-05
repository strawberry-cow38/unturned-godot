// The retail binoculars overlay (ui/player/overlay/binoculars.png, PlayerLifeUI.binocularsOverlay): black with two
// round holes, drawn over the zoomed main camera while the pair is at the eyes -- retail's "dumb overlay" (master).
// Drawn ASPECT-CORRECT: the png is 16:9; on any other screen it is scaled uniformly to COVER (holes stay round, the
// excess is cropped) instead of stretched (master 2026-09-05 "the overlay is stretched vertically").
shader_type canvas_item;
uniform sampler2D mask : filter_linear;
uniform float screen_aspect = 1.7778;   // width / height, set by BinocularsView
const float IMAGE_ASPECT = 1024.0 / 576.0;
void fragment() {
    vec2 uv = UV - 0.5;
    if (screen_aspect < IMAGE_ASPECT) uv.x *= screen_aspect / IMAGE_ASPECT;   // narrower screen: sample a narrower band of the image
    else uv.y *= IMAGE_ASPECT / screen_aspect;                                // wider screen: a shorter band
    uv += 0.5;
    float a = (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) ? 1.0 : texture(mask, uv).a;   // off the image = housing
    COLOR = vec4(0.0, 0.0, 0.0, a);   // the retail png is black with alpha 255 outside the lenses, 0 inside
}
