import bevy_core_pipeline::fullscreen_vertex_shader::FullscreenVertexOutput;
import bevy_render::color_operations::{linear_to_srgb,srgb_to_linear};
@group(0) @binding(0) var world:texture_2d<f32>;
@group(0) @binding(1) var linear_sampler:sampler;
@group(0) @binding(2) var viewmodel:texture_2d<f32>;
@fragment fn fragment(in:FullscreenVertexOutput)->@location(0)vec4<f32>{
    let background=textureSample(world,linear_sampler,in.uv);
    let arms=textureSample(viewmodel,linear_sampler,in.uv);
    // Source TextureRect is on the non-HDR CanvasLayer, after each independent
    // 3D viewport has already applied its own glow/ACES exactly once.
    let composed=mix(linear_to_srgb(background.rgb),linear_to_srgb(arms.rgb),arms.a);
    return vec4<f32>(srgb_to_linear(composed),background.a);
}
