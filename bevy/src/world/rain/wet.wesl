import bevy_pbr::render::{pbr_fragment::pbr_input_from_standard_material,mesh_view_bindings::view};
import unturned_bevy::world::rain::surface::{RainUniform,roof_shelter,puddle_mask,impacts};
@if(PREPASS_PIPELINE)
import bevy_pbr::{prepass::io::{VertexOutput,FragmentOutput},deferred::functions::deferred_output};
@else
import bevy_pbr::render::{forward_io::{VertexOutput,FragmentOutput},pbr_functions::{apply_pbr_lighting,main_pass_post_lighting_processing}};
@group(constants::MATERIAL_BIND_GROUP) @binding(100) var<uniform> rain:RainUniform;
@group(constants::MATERIAL_BIND_GROUP) @binding(101) var roof:texture_2d<f32>;
@group(constants::MATERIAL_BIND_GROUP) @binding(102) var<uniform> surface:vec4<f32>;
@fragment fn fragment(in:VertexOutput,@builtin(front_facing) front:bool)->FragmentOutput{
    var pbr=pbr_input_from_standard_material(in,front);let position=in.world_position.xyz;
    let up=smoothstep(0.45,0.80,pbr.world_normal.y);let sheltered=1.0-roof_shelter(roof,rain.roof_rect,position,0.3);
    let wet=clamp(rain.weather.x,0.0,1.0)*up*sheltered;let rint=clamp(rain.weather.y,0.0,1.0)*sheltered;
    let absolute=position.xz+rain.slant_origin.zw;let range=1.0-smoothstep(42.0,60.0,distance(position,view.world_position));
    let pud=puddle_mask(absolute,clamp(rain.weather.z,0.0,1.0),up*sheltered,range)*surface.y;
    var sp=0.0;var crown=0.0;
    if rint>0.0 && range>0.01 {
        let away=position.xz-view.world_position.xz;var direction=vec2<f32>(0.0);if length(away)>0.001{direction=normalize(away);}
        let impact=impacts(absolute,rain.weather.w,rint,direction,rain.slant_origin.xy);
        sp=impact.x*rint*up*pud;crown=impact.y*rint*up*sheltered*range;
    }
    pbr.material.base_color=vec4<f32>(pbr.material.base_color.rgb*mix(1.0,0.58,wet)*mix(1.0,0.62,pud)+sp*0.18*2.5+crown*0.18*4.5,1.0);
    pbr.material.perceptual_roughness=mix(mix(surface.x,0.42,wet),0.13,pud);
    pbr.material.metallic=0.0;pbr.material.reflectance=vec3<f32>(mix(0.5+wet*0.12,0.78,pud));
@if(PREPASS_PIPELINE)
    return deferred_output(in,pbr);
@if(!PREPASS_PIPELINE){
    var out:FragmentOutput;out.color=main_pass_post_lighting_processing(pbr,apply_pbr_lighting(pbr));return out;
}
}
