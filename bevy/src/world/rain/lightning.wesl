import bevy_core_pipeline::fullscreen_vertex_shader::FullscreenVertexOutput;
import bevy_render::color_operations::{linear_to_srgb,srgb_to_linear};
@group(0) @binding(0) var source:texture_2d<f32>;
@group(0) @binding(1) var linear_sampler:sampler;
@group(0) @binding(2) var<uniform> flash:vec4<f32>;
@fragment fn fragment(in:FullscreenVertexOutput)->@location(0) vec4<f32>{
    let color=textureSample(source,linear_sampler,in.uv);
    return vec4<f32>(srgb_to_linear(clamp(linear_to_srgb(color.rgb)+flash.rgb*flash.a,vec3<f32>(0.),vec3<f32>(1.))),color.a);
}
