import bevy_core_pipeline::fullscreen_vertex_shader::FullscreenVertexOutput;
import bevy_render::color_operations::{linear_to_srgb,srgb_to_linear};
struct Tube {tint_gain:vec4<f32>,optics:vec4<f32>,time:vec4<f32>}
@group(0)@binding(0)var source:texture_2d<f32>;
@group(0)@binding(1)var linear_sampler:sampler;
@group(0)@binding(2)var<uniform> tube:Tube;
fn hash12(p:vec2<f32>)->f32 {var p3=fract(vec3<f32>(p.x,p.y,p.x)*0.1031);p3+=dot(p3,p3.yzx+33.33);return fract((p3.x+p3.y)*p3.z);}
@fragment fn fragment(in:FullscreenVertexOutput)->@location(0)vec4<f32>{
    // Original non-HDR CanvasLayer shader receives display-referred sRGB.
    let c=linear_to_srgb(textureSample(source,linear_sampler,in.uv).rgb);
    let lum=dot(c,vec3<f32>(0.299,0.587,0.114));
    let amp=1.-exp(-lum*tube.tint_gain.w);
    let n=(hash12(in.uv*1024.+vec2<f32>(fract(tube.time.x*13.)*100.))-0.5)*tube.optics.x;
    var col=tube.tint_gain.rgb*(amp+n)+vec3<f32>(max(lum*tube.tint_gain.w-1.2,0.)*tube.optics.z);
    let d=length((in.uv-0.5)*vec2<f32>(1.,0.85));
    col*=1.-tube.optics.y*smoothstep(0.42,0.80,d);
    return vec4<f32>(srgb_to_linear(clamp(col,vec3<f32>(0.),vec3<f32>(1.))),1.);
}
