import bevy_pbr::render::{pbr_fragment::pbr_input_from_standard_material,forward_io::{VertexOutput,FragmentOutput},pbr_functions::main_pass_post_lighting_processing};
@group(constants::MATERIAL_BIND_GROUP) @binding(100) var<uniform> mask:vec4<f32>;
@group(constants::MATERIAL_BIND_GROUP) @binding(101) var scene:texture_2d<f32>;
@group(constants::MATERIAL_BIND_GROUP) @binding(102) var scene_sampler:sampler;
@group(constants::MATERIAL_BIND_GROUP) @binding(103) var reticle:texture_2d<f32>;
@group(constants::MATERIAL_BIND_GROUP) @binding(104) var reticle_sampler:sampler;
@fragment fn fragment(in:VertexOutput,@builtin(front_facing) front:bool)->FragmentOutput {
    let p=(in.uv-vec2<f32>(0.5))*2.0;
    let a=atan2(p.y,p.x)+mask.y;
    let dd=cos(floor(0.5+a/mask.x)*mask.x-a)*length(p);
    if dd>0.95 {discard;}
    var col=textureSample(scene,scene_sampler,in.uv).rgb;
    if mask.w>1.5 {
        let r=length(p);
        let cross=(abs(p.x)<0.005 || abs(p.y)<0.005) && r>0.067;
        let donut=abs(r-0.05)<0.017;
        if cross || donut {col=vec3<f32>(0.0);}
    } else {
        let uv=(in.uv-vec2<f32>(0.5))/mask.z+vec2<f32>(0.5);
        // Derivatives remain uniform despite the reticle's tiny bounded region.
        let ret=textureSampleLevel(reticle,reticle_sampler,uv,0.0);
        if all(uv>=vec2<f32>(0.0)) && all(uv<=vec2<f32>(1.0)) {
            let tint=select(vec3<f32>(1.0),vec3<f32>(1.0,0.0,0.0),mask.w>0.5);
            col=mix(col,ret.rgb*tint,ret.a);
        }
    }
    let pbr=pbr_input_from_standard_material(in,front);
    var out:FragmentOutput;out.color=main_pass_post_lighting_processing(pbr,vec4<f32>(col,1.0));return out;
}
