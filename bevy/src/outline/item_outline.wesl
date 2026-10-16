// item_outline.gdshader: a fullscreen dilate over the focus mask. Outside the
// silhouette but within `thickness` px of it is the rim; inside is clear so the
// real object shows through. Underwater it samples through the same warp.
import bevy_ui_render::ui_vertex_output::UiVertexOutput;
@group(1) @binding(0) var<uniform> color: vec4<f32>;
// thickness px, submersion, wobble amplitude, TIME * wobble_speed
@group(1) @binding(1) var<uniform> params: vec4<f32>;
@group(1) @binding(2) var mask: texture_2d<f32>;
@group(1) @binding(3) var mask_sampler: sampler;
// Level 0 explicitly: the early return makes later samples non-uniform.
// The mask is white on black, so red carries the source's coverage (`.a`)
// without depending on how the image target stores alpha.
fn at(uv: vec2<f32>) -> f32 { return textureSampleLevel(mask, mask_sampler, uv, 0.0).r; }
@fragment fn fragment(in: UiVertexOutput) -> @location(0) vec4<f32> {
    let px = params.x / vec2<f32>(textureDimensions(mask, 0));
    var uv = in.uv;
    if params.y > 0.0 {
        let t = params.w;
        uv = clamp(uv + vec2<f32>(
            sin(uv.y * 22.0 + t * 1.7) + 0.6 * sin(uv.y * 41.0 - t * 2.3),
            cos(uv.x * 19.0 + t * 1.3) + 0.6 * cos(uv.x * 37.0 + t * 2.9)
        ) * params.z * params.y, vec2<f32>(0.001), vec2<f32>(0.999));
    }
    if at(uv) > 0.5 { return vec4<f32>(0.0); }
    var m = 0.0;
    m = max(m, at(uv + vec2<f32>( px.x, 0.0)));
    m = max(m, at(uv + vec2<f32>(-px.x, 0.0)));
    m = max(m, at(uv + vec2<f32>(0.0,  px.y)));
    m = max(m, at(uv + vec2<f32>(0.0, -px.y)));
    m = max(m, at(uv + vec2<f32>( px.x,  px.y)));
    m = max(m, at(uv + vec2<f32>(-px.x, -px.y)));
    m = max(m, at(uv + vec2<f32>( px.x, -px.y)));
    m = max(m, at(uv + vec2<f32>(-px.x,  px.y)));
    return vec4<f32>(color.rgb, m);
}
