// Source game/content/grass_displace.gdshader and foliage_up.gdshader. Both sides
// light like the ground; StandardMaterial's backface normal inversion is undone.
import bevy_pbr::render::{
    pbr_fragment::pbr_input_from_standard_material,
    pbr_functions::alpha_discard,
    mesh_view_bindings::view,
};
@if(PREPASS_PIPELINE)
import bevy_pbr::{prepass::io::{VertexOutput,FragmentOutput},deferred::functions::deferred_output};
@else
import bevy_pbr::render::{forward_io::{VertexOutput,FragmentOutput},pbr_functions::{apply_pbr_lighting,main_pass_post_lighting_processing}};

@group(constants::MATERIAL_BIND_GROUP) @binding(100) var<uniform> fade:vec4<f32>;
@fragment
fn fragment(in:VertexOutput,@builtin(front_facing) is_front:bool)->FragmentOutput{
    var pbr=pbr_input_from_standard_material(in,is_front);
    pbr.N=vec3<f32>(0.0,1.0,0.0);
    pbr.world_normal=pbr.N;
    pbr.clearcoat_N=pbr.N;
    if fade.y>0.001{
        pbr.material.base_color.a*=1.0-smoothstep(fade.x,fade.y,length(in.world_position.xyz-view.world_position));
    }
    pbr.material.base_color=alpha_discard(pbr.material.flags,pbr.material.alpha_cutoff,pbr.material.base_color);
@if(PREPASS_PIPELINE)
    return deferred_output(in,pbr);
@if(!PREPASS_PIPELINE){
    var out:FragmentOutput;
    out.color=main_pass_post_lighting_processing(pbr,apply_pbr_lighting(pbr));
    return out;
}
}
