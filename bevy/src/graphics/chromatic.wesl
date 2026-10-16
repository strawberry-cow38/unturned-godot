// Port of game/content/chromatic.gdshader. Radial r^falloff offset normalised
// to the corner, swept over N taps weighted by a triangular R/G/B response.
import bevy_core_pipeline::fullscreen_vertex_shader::FullscreenVertexOutput;
import bevy_render::color_operations::{linear_to_srgb,srgb_to_linear};
struct Lens {settings:vec4<f32>}
@group(0)@binding(0)var source:texture_2d<f32>;
@group(0)@binding(1)var linear_sampler:sampler;
@group(0)@binding(2)var<uniform> lens:Lens;
@fragment fn fragment(in:FullscreenVertexOutput)->@location(0)vec4<f32>{
    let uv=in.uv;
    let from_centre=uv-vec2<f32>(0.5);
    let r=length(from_centre)/0.7071068;
    let mag=lens.settings.x*pow(clamp(r,0.,1.),lens.settings.z)*0.05;
    var dir=vec2<f32>(0.);
    if r>1e-5 {dir=normalize(from_centre);}
    let n=i32(clamp(lens.settings.y,2.,16.));
    var sum=vec3<f32>(0.);
    var wsum=vec3<f32>(0.);
    for(var i=0;i<n;i++){
        let t=f32(i)/f32(n-1);
        let w=vec3<f32>(max(0.,1.-abs(t)*2.),max(0.,1.-abs(t-0.5)*2.),max(0.,1.-abs(t-1.)*2.));
        let suv=clamp(uv+dir*mag*t,vec2<f32>(0.),vec2<f32>(1.));
        // The original canvas shader reads the display-referred sRGB screen.
        sum+=linear_to_srgb(textureSampleLevel(source,linear_sampler,suv,0.).rgb)*w;
        wsum+=w;
    }
    return vec4<f32>(srgb_to_linear(sum/max(wsum,vec3<f32>(1e-4))),1.);
}
