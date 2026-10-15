import bevy_ui_render::ui_vertex_output::UiVertexOutput;
@group(1) @binding(0) var<uniform> settings: vec4<f32>;
@group(1) @binding(1) var mask: texture_2d<f32>;
@group(1) @binding(2) var mask_sampler: sampler;
@group(1) @binding(3) var reticle: texture_2d<f32>;
@group(1) @binding(4) var reticle_sampler: sampler;
@fragment
fn fragment(in: UiVertexOutput) -> @location(0) vec4<f32> {
    let aspect = settings.x;
    if settings.y < 0.5 { return vec4<f32>(0.0); }
    if settings.y < 1.5 {
        // Source binoculars_overlay.gdshader uniformly covers the window; the
        // original 1024x576 mask is cropped, never stretched into oval lenses.
        var uv = in.uv - vec2<f32>(0.5);
        let image_aspect = 1024.0 / 576.0;
        if aspect < image_aspect { uv.x *= aspect / image_aspect; }
        else { uv.y *= image_aspect / aspect; }
        uv += vec2<f32>(0.5);
        var alpha = 1.0;
        if all(uv >= vec2<f32>(0.0)) && all(uv <= vec2<f32>(1.0)) {
            alpha = textureSampleLevel(mask, mask_sampler, uv, 0.0).a;
        }
        return vec4<f32>(0.0, 0.0, 0.0, alpha);
    }
    // Exact source tank_gunsight.gdshader: radius is a fraction of HEIGHT.
    let p = (in.uv - vec2<f32>(0.5)) * vec2<f32>(aspect, 1.0) / 0.47;
    let r = length(p);
    if r >= 1.0 { return vec4<f32>(0.0, 0.0, 0.0, 1.0); }
    let t = textureSampleLevel(reticle, reticle_sampler, p * 0.5 + vec2<f32>(0.5), 0.0);
    // Transparent source pixels are white. Alpha weighting suppresses the
    // otherwise visible pale fringe at bilinearly filtered black reticle edges.
    let mark = t.rgb * t.a;
    let rim = smoothstep(0.975, 1.0, r);
    return vec4<f32>(mark * (1.0 - rim), max(t.a, rim));
}
