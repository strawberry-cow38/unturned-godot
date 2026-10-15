// Godot 4.6 tonemap.glsl (ACES fit from BakingLab), MIT; licenses/GODOT_POSTPROCESS.txt.
import bevy_core_pipeline::fullscreen_vertex_shader::FullscreenVertexOutput;
struct Settings { glow: vec4<f32>, tone: vec4<f32> }
@group(0) @binding(0) var source:texture_2d<f32>;
@group(0) @binding(1) var linear_sampler:sampler;
@group(0) @binding(2) var glow:texture_2d<f32>;
@group(0) @binding(3) var<uniform> settings:Settings;
fn aces(input:vec3<f32>)->vec3<f32> {
    let rrt=mat3x3<f32>(vec3<f32>(0.59719,0.35458,0.04823)*1.8,vec3<f32>(0.076,0.90834,0.01566)*1.8,vec3<f32>(0.0284,0.13383,0.83777)*1.8);
    let odt=mat3x3<f32>(vec3<f32>(1.60475,-0.53108,-0.07367),vec3<f32>(-0.10208,1.10813,-0.00605),vec3<f32>(-0.00327,-0.07276,1.07602));
    let c=input*rrt;
    let fitted=(c*(c+0.0245786)-0.000090537)/(c*(0.983729*c+0.432951)+0.238081);
    let white=(1.8*(1.8+0.0245786)-0.000090537)/(1.8*(0.983729*1.8+0.432951)+0.238081);
    return (fitted*odt)/white;
}
// Default project glow upscale mode is bicubic. Four linear taps implement the
// exact source B-spline reconstruction at each selected mip.
fn cubic_weights(x:f32)->vec4<f32>{
    return vec4<f32>((1.-x)*(1.-x)*(1.-x),3.*x*x*x-6.*x*x+4.,-3.*x*x*x+3.*x*x+3.*x+1.,x*x*x)/6.;
}
fn bicubic(uv:vec2<f32>,level:u32)->vec3<f32>{
    let size=vec2<f32>(textureDimensions(glow,level));
    let p=uv*size+0.5; let whole=floor(p); let fraction=fract(p);
    let wx=cubic_weights(fraction.x); let wy=cubic_weights(fraction.y);
    let gx=vec2<f32>(wx.x+wx.y,wx.z+wx.w);let gy=vec2<f32>(wy.x+wy.y,wy.z+wy.w);
    let hx=vec2<f32>(-1.+wx.y/gx.x,1.+wx.w/gx.y);
    let hy=vec2<f32>(-1.+wy.y/gy.x,1.+wy.w/gy.y);
    let p0=(whole+vec2<f32>(hx.x,hy.x)-0.5)/size;
    let p1=(whole+vec2<f32>(hx.y,hy.x)-0.5)/size;
    let p2=(whole+vec2<f32>(hx.x,hy.y)-0.5)/size;
    let p3=(whole+vec2<f32>(hx.y,hy.y)-0.5)/size;
    return gy.x*(gx.x*textureSampleLevel(glow,linear_sampler,p0,f32(level)).rgb+gx.y*textureSampleLevel(glow,linear_sampler,p1,f32(level)).rgb)
        +gy.y*(gx.x*textureSampleLevel(glow,linear_sampler,p2,f32(level)).rgb+gx.y*textureSampleLevel(glow,linear_sampler,p3,f32(level)).rgb);
}
@fragment fn fragment(in:FullscreenVertexOutput)->@location(0)vec4<f32>{
    let src=textureSample(source,linear_sampler,in.uv);
    var color=max(src.rgb*settings.glow.w,vec3<f32>(0.));
    if settings.glow.x>0. {
        let last=textureNumLevels(glow)-1u;
        let bloom=bicubic(in.uv,min(1u,last))*0.8+bicubic(in.uv,min(2u,last))*0.4+bicubic(in.uv,min(3u,last))*0.1;
        let g=clamp(bloom*settings.glow.x,vec3<f32>(0.),vec3<f32>(1.));
        color=color+g-color*g;
    }
    if settings.tone.x>0.5 {color=aces(color);}
    // Offscreen scope targets remain linear HDR and are toned only by the VM.
    if settings.tone.y>0.5 {color=clamp(color,vec3<f32>(0.),vec3<f32>(1.));}
    return vec4<f32>(color,src.a);
}
