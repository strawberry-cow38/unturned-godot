// Godot4.6 copy.glsl non-glow screen mip Gaussian, MIT; licenses/GODOT_POSTPROCESS.txt.
@group(0) @binding(0) var source: texture_2d<f32>;
@group(0) @binding(1) var linear_sampler: sampler;
@group(0) @binding(2) var destination: texture_storage_2d<rgba8unorm,write>;
var<workgroup> samples: array<vec4<f32>,256>;
var<workgroup> horizontal: array<vec4<f32>,128>;
const KERNEL: array<f32,4> = array<f32,4>(0.214607,0.189879,0.131514,0.071303);
fn to_srgb(c:vec3<f32>)->vec3<f32> {
    return select(12.92*c,1.055*pow(max(c,vec3<f32>(0.0)),vec3<f32>(1.0/2.4))-0.055,c>vec3<f32>(0.0031308));
}
@compute @workgroup_size(8,8,1)
fn screen_copy(@builtin(global_invocation_id) global:vec3<u32>) {
    if any(global.xy>=textureDimensions(destination)) {return;}
    let original=textureLoad(source,vec2<i32>(global.xy),0);
    // Source non-HDR canvas backbuffer is RGBA8 UNORM containing sRGB values.
    textureStore(destination,vec2<i32>(global.xy),vec4<f32>(to_srgb(original.rgb),original.a));
}
@compute @workgroup_size(8,8,1)
fn gaussian(@builtin(global_invocation_id) global:vec3<u32>,@builtin(local_invocation_id) local:vec3<u32>) {
    let size=vec2<f32>(textureDimensions(destination));
    let uv=clamp((vec2<f32>(global.xy+local.xy)-3.5)/size,0.5/size,1.0-1.5/size);
    let index=local.x*2u+local.y*32u;
    samples[index]=textureSampleLevel(source,linear_sampler,uv,0.0);
    samples[index+1u]=textureSampleLevel(source,linear_sampler,uv+vec2<f32>(1.0/size.x,0.0),0.0);
    samples[index+16u]=textureSampleLevel(source,linear_sampler,uv+vec2<f32>(0.0,1.0/size.y),0.0);
    samples[index+17u]=textureSampleLevel(source,linear_sampler,uv+1.0/size,0.0);
    workgroupBarrier();
    let read=local.x+local.y*32u+4u;
    var top=samples[read]*KERNEL[0];
    var bottom=samples[read+16u]*KERNEL[0];
    for (var i=1u;i<4u;i+=1u) {
        top+=(samples[read+i]+samples[read-i])*KERNEL[i];
        bottom+=(samples[read+16u+i]+samples[read+16u-i])*KERNEL[i];
    }
    let write=local.y*2u+local.x*16u;
    horizontal[write]=top;horizontal[write+1u]=bottom;
    workgroupBarrier();
    if any(global.xy>=textureDimensions(destination)) {return;}
    let vertical=local.y+local.x*16u+4u;
    var color=horizontal[vertical]*KERNEL[0];
    for (var i=1u;i<4u;i+=1u) {color+=(horizontal[vertical+i]+horizontal[vertical-i])*KERNEL[i];}
    textureStore(destination,vec2<i32>(global.xy),color);
}
