// Godot 4.6 Forward+ copy.glsl Gaussian glow, MIT; see licenses/GODOT_POSTPROCESS.txt.
struct Settings { glow: vec4<f32>, tone: vec4<f32> }
@group(0) @binding(0) var source: texture_2d<f32>;
@group(0) @binding(1) var linear_sampler: sampler;
@group(0) @binding(2) var destination: texture_storage_2d<rgba16float, write>;
@group(0) @binding(3) var<uniform> settings: Settings;
var<workgroup> samples: array<vec4<f32>,256>;
var<workgroup> horizontal: array<vec4<f32>,128>;
const KERNEL: array<f32,5> = array<f32,5>(0.2024,0.1790,0.1240,0.0672,0.0285);
fn gaussian(global:vec3<u32>, local:vec3<u32>, first:bool) {
    let size=vec2<f32>(textureDimensions(destination));
    let uv=clamp((vec2<f32>(global.xy+local.xy)-3.5)/size,0.5/size,1.-1.5/size);
    let index=local.x*2u+local.y*32u;
    samples[index]=textureSampleLevel(source,linear_sampler,uv,0.);
    samples[index+1u]=textureSampleLevel(source,linear_sampler,uv+vec2<f32>(1./size.x,0.),0.);
    samples[index+16u]=textureSampleLevel(source,linear_sampler,uv+vec2<f32>(0.,1./size.y),0.);
    samples[index+17u]=textureSampleLevel(source,linear_sampler,uv+1./size,0.);
    let cap_luma=vec3<f32>(0.299,0.587,0.114)/12.;
    if first {
        samples[index]/=1.+dot(samples[index].rgb,cap_luma);
        samples[index+1u]/=1.+dot(samples[index+1u].rgb,cap_luma);
        samples[index+16u]/=1.+dot(samples[index+16u].rgb,cap_luma);
        samples[index+17u]/=1.+dot(samples[index+17u].rgb,cap_luma);
    }
    workgroupBarrier();
    let read=local.x+local.y*32u+4u;
    var top=samples[read]*KERNEL[0];
    var bottom=samples[read+16u]*KERNEL[0];
    for (var i=1u;i<5u;i+=1u) {
        top+=(samples[read+i]+samples[read-i])*KERNEL[i];
        bottom+=(samples[read+16u+i]+samples[read+16u-i])*KERNEL[i];
    }
    let write=local.y*2u+local.x*16u;
    horizontal[write]=top;
    horizontal[write+1u]=bottom;
    workgroupBarrier();
    if any(global.xy>=textureDimensions(destination)) {return;}
    let vertical=local.y+local.x*16u+4u;
    var color=horizontal[vertical]*KERNEL[0];
    for (var i=1u;i<5u;i+=1u) {color+=(horizontal[vertical+i]+horizontal[vertical-i])*KERNEL[i];}
    if first {
        color/=1.-dot(color.rgb,cap_luma);
        color*=settings.glow.w;
        let luminance=max(color.r,max(color.g,color.b));
        let feedback=max(smoothstep(settings.glow.z,settings.glow.z+2.,luminance),settings.glow.y);
        color=min(color*feedback,vec4<f32>(12.));
    }
    textureStore(destination,vec2<i32>(global.xy),color);
}
@compute @workgroup_size(8,8,1) fn gather(@builtin(global_invocation_id) global:vec3<u32>,@builtin(local_invocation_id) local:vec3<u32>) {gaussian(global,local,true);}
@compute @workgroup_size(8,8,1) fn blur(@builtin(global_invocation_id) global:vec3<u32>,@builtin(local_invocation_id) local:vec3<u32>) {gaussian(global,local,false);}
