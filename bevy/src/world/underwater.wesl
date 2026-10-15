// Original underwater.gdshader + water_optics.gdshaderinc. Source-color uniforms
// are explicitly decoded; the scene texture's sRGB view performs its own decode.
import bevy_core_pipeline::fullscreen_vertex_shader::FullscreenVertexOutput;
import unturned_bevy::world::water_optics::{linear,water_veil};
struct Optics {
    clip_to_view:mat4x4<f32>,
    state:vec4<f32>,
    extinction:vec4<f32>,
    warp:vec4<f32>,
};
@group(0) @binding(0) var screen:texture_2d<f32>;
@group(0) @binding(1) var linear_sampler:sampler;
@group(0) @binding(2) var<uniform> optics:Optics;
@if(MULTISAMPLED)
@group(0) @binding(3) var depth:texture_depth_multisampled_2d;
@else
@group(0) @binding(3) var depth:texture_depth_2d;
@fragment
fn fragment(in:FullscreenVertexOutput)->@location(0) vec4<f32>{
    let uv=in.uv;
    let sub=optics.state.w;
    let t=optics.state.z*optics.warp.y;
    let warp=vec2<f32>(
        sin(uv.y*22.0+t*1.7)+0.6*sin(uv.y*41.0-t*2.3),
        cos(uv.x*19.0+t*1.3)+0.6*cos(uv.x*37.0+t*2.9)
    )*optics.warp.x*sub;
    let wuv=clamp(uv+warp,vec2<f32>(0.001),vec2<f32>(0.999));
    let pixel=vec2<i32>(wuv*vec2<f32>(textureDimensions(depth)));
    let d=textureLoad(depth,pixel,0);
    let view=optics.clip_to_view*vec4<f32>(wuv*vec2<f32>(2.0,-2.0)+vec2<f32>(-1.0,1.0),d,1.0);
    let path=max(-view.z/max(view.w,0.000001),0.0);
    let transmit=exp(-max(optics.extinction.xyz,vec3<f32>(0.0))*path);
    let legacy_fade=1.0-0.98*exp(-0.032*path);
    let scatter=mix(legacy_fade,1.0-transmit.g,optics.state.y);
    let lod=4.2*scatter*smoothstep(0.35,1.85,path)*sub;
    let incoming=textureSampleLevel(screen,linear_sampler,wuv,lod).rgb;
    let legacy_tint=linear(vec3<f32>(0.17,0.42,0.51));
    let legacy=mix(incoming*vec3<f32>(1.0-0.28*legacy_fade,1.0-0.22*legacy_fade,1.0),legacy_tint,legacy_fade);
    let veil=water_veil(optics.state.x);
    var col=mix(legacy,incoming*transmit+veil*(1.0-transmit),optics.state.y);
    let deep=clamp(optics.state.x/18.0,0.0,1.0);
    col=mix(col,mix(legacy_tint,veil,optics.state.y)*0.45,deep*0.55);
    col*=1.0-0.30*sub*smoothstep(0.25,0.85,distance(uv,vec2<f32>(0.5)));
    let original=textureSampleLevel(screen,linear_sampler,uv,0.0);
    return vec4<f32>(mix(original.rgb,col,clamp(sub,0.0,1.0)),original.a);
}
