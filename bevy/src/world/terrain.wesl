import bevy_pbr::render::pbr_fragment::pbr_input_from_standard_material;
import unturned_bevy::world::water_optics::linear;
@if(PREPASS_PIPELINE)
import bevy_pbr::{prepass::io::{VertexOutput,FragmentOutput},deferred::functions::deferred_output};
@else
import bevy_pbr::render::{forward_io::{VertexOutput,FragmentOutput},pbr_functions::{apply_pbr_lighting,main_pass_post_lighting_processing}};
@group(constants::MATERIAL_BIND_GROUP) @binding(100) var<uniform> rect:vec4<f32>;
@group(constants::MATERIAL_BIND_GROUP) @binding(101) var<uniform> settings:vec4<f32>;
@group(constants::MATERIAL_BIND_GROUP) @binding(102) var albedos:texture_2d_array<f32>;
@group(constants::MATERIAL_BIND_GROUP) @binding(103) var albedo_sampler:sampler;
@group(constants::MATERIAL_BIND_GROUP) @binding(104) var splat0:texture_2d<f32>;
@group(constants::MATERIAL_BIND_GROUP) @binding(105) var splat_sampler0:sampler;
@group(constants::MATERIAL_BIND_GROUP) @binding(106) var splat1:texture_2d<f32>;
@group(constants::MATERIAL_BIND_GROUP) @binding(107) var splat_sampler1:sampler;
@group(constants::MATERIAL_BIND_GROUP) @binding(108) var<uniform> boundary:vec4<f32>;
@group(constants::MATERIAL_BIND_GROUP) @binding(109) var<uniform> water:vec4<f32>;

// Terrain.TERRAIN_SHADER's precision-robust gradient noise, without retuning.
fn chashv(p:vec2<f32>)->f32{
    var p3=fract(vec3<f32>(p.x,p.y,p.x)*0.1031);
    p3+=dot(p3,p3.yzx+33.33);
    return fract((p3.x+p3.y)*p3.z);
}
fn cgrad(p:vec2<f32>)->vec2<f32>{let h=chashv(p)*6.2831853;return vec2<f32>(cos(h),sin(h));}
fn cnoise(p:vec2<f32>)->f32{
    let i=floor(p);let f=fract(p);let u=f*f*f*(f*(f*6.0-15.0)+10.0);
    let a=dot(cgrad(i),f);let b=dot(cgrad(i+vec2<f32>(1.0,0.0)),f-vec2<f32>(1.0,0.0));
    let c=dot(cgrad(i+vec2<f32>(0.0,1.0)),f-vec2<f32>(0.0,1.0));
    let d=dot(cgrad(i+vec2<f32>(1.0,1.0)),f-vec2<f32>(1.0,1.0));
    return mix(mix(a,b,u.x),mix(c,d,u.x),u.y)*0.8+0.5;
}
fn cfbm(p0:vec2<f32>)->f32{
    var p=p0;var s=0.0;var a=0.5;
    for(var i=0;i<4;i++){s+=a*cnoise(p);p*=2.03;a*=0.5;}
    return s;
}
fn caustics(p:vec2<f32>,t:f32)->f32{
    let a=cfbm(p+vec2<f32>(t,t*0.4));
    let b=cfbm(p*1.31+vec2<f32>(-t*0.7,t*0.55)+17.3);
    return pow(clamp(1.0-abs(a-b)*4.0,0.0,1.0),4.0);
}
@fragment
fn fragment(in:VertexOutput,@builtin(front_facing) is_front:bool)->FragmentOutput{
    var pbr=pbr_input_from_standard_material(in,is_front);
    let world=in.world_position.xz;
    let absolute=world+boundary.yz;
    let jitter=(vec2<f32>(cnoise(absolute*1.9),cnoise(absolute*1.9+37.1))-0.5)*2.0*settings.x*vec2<f32>(1.0,boundary.x);
    let uv=(world-rect.xy)/rect.zw+jitter/vec2<f32>(textureDimensions(splat0));
    let w0=textureSample(splat0,splat_sampler0,uv);let w1=textureSample(splat1,splat_sampler1,uv);
    let weights=array<f32,8>(w0.x,w0.y,w0.z,w0.w,w1.x,w1.y,w1.z,w1.w);
    var best=0;var weight=weights[0];
    for(var i=1;i<8;i++){if weights[i]>weight{best=i;weight=weights[i];}}
    let tiled=absolute/settings.y;
    let dx=dpdx(tiled);let dy=dpdy(tiled);
    pbr.material.base_color=vec4<f32>(textureSampleGrad(albedos,albedo_sampler,tiled,best,dx,dy).rgb,1.0);
    pbr.material.perceptual_roughness=1.0;
    var snow=0.0;
    if settings.z>0.0 && best==2{
        snow=clamp(smoothstep(0.35,0.95,settings.z*1.45-cnoise(absolute*0.07)*0.45),0.0,1.0);
        pbr.material.base_color=vec4<f32>(mix(pbr.material.base_color.rgb,textureSampleGrad(albedos,albedo_sampler,tiled,6,dx,dy).rgb,snow),1.0);
        pbr.material.perceptual_roughness=mix(1.0,0.82,snow);
    }
    // Terrain.cs retains the web with strength0 since 2026-10-09. Keep its
    // source formula available without paying the noise cost while disabled.
    let depth=water.x-in.world_position.y;
    if water.z>0.0 && depth>0.0{
        var cp=mat2x2<f32>(vec2<f32>(0.87,0.5),vec2<f32>(-0.5,0.87))*(absolute*0.11);
        cp+=0.8*(vec2<f32>(cfbm(cp*0.5),cfbm(cp*0.5+7.0))-0.5);
        let caust=max(caustics(cp,water.y*0.25),caustics(cp*1.6+9.0,-water.y*0.2));
        let fade=clamp(depth/0.4,0.0,1.0)*(1.0-clamp(depth/9.0,0.0,1.0));
        pbr.material.base_color=vec4<f32>(pbr.material.base_color.rgb+linear(vec3<f32>(0.55,0.9,1.0))*caust*water.z*fade,1.0);
    }
    // Source dry-to-wet base response; roof shelter and puddle/impact shaders are
    // separate consumers and are deliberately not approximated here.
    if settings.w>0.0 && snow<0.98{
        var up=smoothstep(0.35,0.75,pbr.world_normal.y);
        if best==2 || best==0 || best==7{up=0.0;}
        let wet=clamp(settings.w,0.0,1.0)*up;
        pbr.material.base_color=vec4<f32>(pbr.material.base_color.rgb*mix(1.0,0.60,wet),1.0);
        pbr.material.perceptual_roughness=mix(1.0,select(0.72,0.42,best==4),wet);
    }
@if(PREPASS_PIPELINE)
    return deferred_output(in,pbr);
@if(!PREPASS_PIPELINE){
    var out:FragmentOutput;
    out.color=main_pass_post_lighting_processing(pbr,apply_pbr_lighting(pbr));
    return out;
}
}
