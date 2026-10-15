// Godot4.6 RendererSceneRenderRD::_render_buffers_copy_screen_texture uses a
// plain copy followed by CopyEffects::make_mipmap (2x2 box average). Explicit
// integer taps preserve that footprint for odd dimensions as well as even ones.
import bevy_core_pipeline::fullscreen_vertex_shader::FullscreenVertexOutput;
@group(0) @binding(0) var source:texture_2d<f32>;
@group(0) @binding(1) var linear_sampler:sampler;
@fragment
fn copy(in:FullscreenVertexOutput)->@location(0) vec4<f32>{
    return textureLoad(source,vec2<i32>(in.position.xy),0);
}
@fragment
fn fragment(in:FullscreenVertexOutput)->@location(0) vec4<f32>{
    let p=vec2<i32>(in.position.xy)*2;
    return (textureLoad(source,p,0)+textureLoad(source,p+vec2<i32>(0,1),0)
           +textureLoad(source,p+vec2<i32>(1,0),0)+textureLoad(source,p+vec2<i32>(1,1),0))*0.25;
}
