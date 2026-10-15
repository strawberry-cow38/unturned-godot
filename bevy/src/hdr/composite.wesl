import bevy_core_pipeline::fullscreen_vertex_shader::FullscreenVertexOutput;
import bevy_render::color_operations::{linear_to_srgb,srgb_to_linear};
@group(0) @binding(0) var world:texture_2d<f32>;
@group(0) @binding(1) var linear_sampler:sampler;
@group(0) @binding(2) var viewmodel:texture_2d<f32>;
struct Mapping { sizes:vec4<f32>, roll:vec4<f32>, };
@group(0) @binding(3) var<uniform> mapping:Mapping;
@fragment fn fragment(in:FullscreenVertexOutput)->@location(0)vec4<f32>{
    let background=textureSample(world,linear_sampler,in.uv);
    let pixel=(in.uv-vec2<f32>(0.5))*mapping.sizes.xy;
    let rotated=vec2<f32>(mapping.roll.x*pixel.x-mapping.roll.y*pixel.y,
                          mapping.roll.y*pixel.x+mapping.roll.x*pixel.y);
    let uv=vec2<f32>(0.5)+rotated/mapping.sizes.zw;
    // The original TextureRect has transparent space outside its rotated image.
    let sample=textureSample(viewmodel,linear_sampler,uv);
    let arms=select(vec4<f32>(0.0),sample,all(uv>=vec2<f32>(0.0))&&all(uv<=vec2<f32>(1.0)));
    // Source TextureRect is on the non-HDR CanvasLayer, after each independent
    // 3D viewport has already applied its own glow/ACES exactly once.
    let composed=mix(linear_to_srgb(background.rgb),linear_to_srgb(arms.rgb),arms.a);
    return vec4<f32>(srgb_to_linear(composed),background.a);
}
