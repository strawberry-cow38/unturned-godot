// game/content/deadzone.gdshader, unchanged arithmetic: a pow(t,1.6) ramp drains
// colour toward Rec.709 luma and multiplies a signed per-cell grain that crawls.
import bevy_core_pipeline::fullscreen_vertex_shader::FullscreenVertexOutput;
import bevy_render::color_operations::{linear_to_srgb,srgb_to_linear};
struct Grain {params:vec4<f32>,amounts:vec4<f32>}
@group(0)@binding(0)var source:texture_2d<f32>;
@group(0)@binding(1)var linear_sampler:sampler;
@group(0)@binding(2)var<uniform> grain:Grain;
fn hash(p:vec2<f32>)->f32 {return fract(sin(dot(p,vec2<f32>(12.9898,78.233)))*43758.5453);}
@fragment fn fragment(in:FullscreenVertexOutput)->@location(0)vec4<f32>{
    // The original non-HDR CanvasLayer shader receives display-referred sRGB.
    let src=linear_to_srgb(textureSample(source,linear_sampler,in.uv).rgb);
    let t=clamp(grain.params.x,0.,1.);
    let ramp=pow(t,1.6);
    let luma=dot(src,vec3<f32>(0.2126,0.7152,0.0722));
    let drained=mix(src,vec3<f32>(luma),grain.amounts.y*ramp);
    // One hash per grain_px cell of screen pixels; the two time rates are folded
    // with mod so the seed never grows past sin's precision.
    let cell=floor(in.position.xy/max(grain.params.z,1.));
    let tseed=vec2<f32>(grain.params.y*60.,grain.params.y*37.)%vec2<f32>(1024.);
    let n=hash(cell+tseed);
    let g=(n-0.5)*2.*grain.amounts.x*ramp;
    let grained=drained*(1.+g);
    return vec4<f32>(srgb_to_linear(clamp(grained,vec3<f32>(0.),vec3<f32>(1.))),1.);
}
