import bevy_pbr::render::pbr_fragment::pbr_input_from_standard_material;
import bevy_pbr::render::mesh_view_bindings::view;
@if(PREPASS_PIPELINE)
import bevy_pbr::{prepass::io::{VertexOutput,FragmentOutput},deferred::functions::deferred_output};
@else
import bevy_pbr::render::{forward_io::{VertexOutput,FragmentOutput},pbr_functions::{apply_pbr_lighting,main_pass_post_lighting_processing}};
@group(constants::MATERIAL_BIND_GROUP) @binding(100) var<uniform> skin:vec4<f32>;
@group(constants::MATERIAL_BIND_GROUP) @binding(101) var<uniform> settings:vec4<f32>;
@group(constants::MATERIAL_BIND_GROUP) @binding(102) var<uniform> flags:vec4<u32>;
@group(constants::MATERIAL_BIND_GROUP) @binding(103) var shirt:texture_2d<f32>;
@group(constants::MATERIAL_BIND_GROUP) @binding(104) var shirt_sampler:sampler;
@group(constants::MATERIAL_BIND_GROUP) @binding(105) var shirt_emission:texture_2d<f32>;
@group(constants::MATERIAL_BIND_GROUP) @binding(106) var shirt_emission_sampler:sampler;
@group(constants::MATERIAL_BIND_GROUP) @binding(107) var shirt_metallic:texture_2d<f32>;
@group(constants::MATERIAL_BIND_GROUP) @binding(108) var shirt_metallic_sampler:sampler;
@group(constants::MATERIAL_BIND_GROUP) @binding(109) var pants:texture_2d<f32>;
@group(constants::MATERIAL_BIND_GROUP) @binding(110) var pants_sampler:sampler;
@group(constants::MATERIAL_BIND_GROUP) @binding(111) var pants_emission:texture_2d<f32>;
@group(constants::MATERIAL_BIND_GROUP) @binding(112) var pants_emission_sampler:sampler;
@group(constants::MATERIAL_BIND_GROUP) @binding(113) var pants_metallic:texture_2d<f32>;
@group(constants::MATERIAL_BIND_GROUP) @binding(114) var pants_metallic_sampler:sampler;
// Original tatter.gdshaderinc; fixed garment noise grows holes as condition falls.
fn hash(p0:vec2<f32>)->f32{var p=fract(p0*vec2<f32>(123.34,456.21));p+=dot(p,p+45.32);return fract(p.x*p.y);}
fn noise(p:vec2<f32>)->f32{let i=floor(p);var f=fract(p);f=f*f*(3.0-2.0*f);return mix(mix(hash(i),hash(i+vec2<f32>(1.0,0.0)),f.x),mix(hash(i+vec2<f32>(0.0,1.0)),hash(i+vec2<f32>(1.0)),f.x),f.y);}
fn tatter(p:vec2<f32>,wear:f32)->vec3<f32>{
    if wear<=0.001{return vec3<f32>(1.0,0.0,0.0);}
    let n=noise(p)*0.55+noise(p*2.17+7.3)*0.3+noise(p*4.61+3.1)*0.15;
    let speck=noise(p*5.3+11.0);let open=clamp((wear-0.30)/0.70,0.0,1.0);let cut=0.335*open;
    let keep=select(1.0,step(cut,n),open>0.0);let rim=select(0.0,(1.0-step(cut+0.05,n))*keep,open>0.0);
    let grime=clamp(wear*0.5+step(1.0-wear*0.25,speck)*0.4+step(1.0-wear*0.42,n)*0.55,0.0,1.0);
    return vec3<f32>(keep,rim,grime);
}
fn tint(rgb:vec3<f32>,t:vec3<f32>)->vec3<f32>{return mix(rgb,rgb*vec3<f32>(0.70,0.64,0.56)+vec3<f32>(0.035,0.028,0.016),t.z)*(1.0-t.y*0.65);}
fn has(bit:u32)->bool{return (flags.x&(1u<<bit))!=0u;}
@fragment
fn fragment(in:VertexOutput,@builtin(front_facing) front:bool)->FragmentOutput{
    if settings.z>0.0 && distance(in.world_position.xyz,view.world_position)<settings.z{discard;}
    var pbr=pbr_input_from_standard_material(in,front);
    var su=in.uv;let flip=ceil(su.x*4.0)*0.25-fract(su.x*4.0)*0.25;
    su.x=mix(su.x,flip,settings.w*select(0.0,1.0,su.x<0.5&&su.y>0.75));
    var sa=select(vec4<f32>(0.0),textureSample(shirt,shirt_sampler,su),has(0u));
    var pa=select(vec4<f32>(0.0),textureSample(pants,pants_sampler,in.uv),has(3u));
    let se=select(vec4<f32>(0.0),textureSample(shirt_emission,shirt_emission_sampler,su),has(1u));
    let pe=select(vec4<f32>(0.0),textureSample(pants_emission,pants_emission_sampler,in.uv),has(4u));
    let sm=select(vec4<f32>(0.0),textureSample(shirt_metallic,shirt_metallic_sampler,su),has(2u));
    let pm=select(vec4<f32>(0.0),textureSample(pants_metallic,pants_metallic_sampler,in.uv),has(5u));
    let st=vec2<f32>(textureDimensions(shirt));let pt=vec2<f32>(textureDimensions(pants));
    let ts=tatter(floor(su*st)/st*34.0+vec2<f32>(1.37,7.1),settings.x);
    let tp=tatter(floor(in.uv*pt)/pt*34.0+vec2<f32>(5.9,2.3),settings.y);
    sa=vec4<f32>(tint(sa.rgb,ts),sa.a*ts.x);pa=vec4<f32>(tint(pa.rgb,tp),pa.a*tp.x);
    pbr.material.base_color=vec4<f32>(mix(mix(skin.rgb,sa.rgb,sa.a),pa.rgb,pa.a),1.0);
    pbr.material.emissive=vec4<f32>(mix(se.rgb*sa.a,pe.rgb,pa.a)*2.0,1.0);
    pbr.material.metallic=mix(sm.r*sa.a,pm.r,pa.a);
    pbr.material.perceptual_roughness=1.0-mix(sm.a*sa.a,pm.a,pa.a);
@if(PREPASS_PIPELINE)
    return deferred_output(in,pbr);
@if(!PREPASS_PIPELINE){
    var out:FragmentOutput;out.color=main_pass_post_lighting_processing(pbr,apply_pbr_lighting(pbr));return out;
}
}
