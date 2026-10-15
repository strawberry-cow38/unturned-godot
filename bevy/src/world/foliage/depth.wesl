// Bevy StandardMaterial prepass sampling, with the same source foliage erosion
// as the color pass. Invisible far blades must not occlude the water/depth view.
import bevy_pbr::{
    prepass::io::{VertexOutput,FragmentOutput},
    render::{pbr_bindings,pbr_types,pbr_prepass_functions,mesh_bindings::mesh,mesh_view_bindings::view},
};
@if(BINDLESS)
import bevy_pbr::render::pbr_bindings::material_indices;
@if(BINDLESS)
import bevy_render::bindless::{bindless_samplers_filtering,bindless_textures_2d};
@group(constants::MATERIAL_BIND_GROUP) @binding(100) var<uniform> fade:vec4<f32>;

fn discard_foliage(in:VertexOutput) {
@if(MAY_DISCARD) {
@if(BINDLESS) {
    let slot=mesh[in.instance_index].material_and_lightmap_bind_group_slot & 0xffffu;
    let material=pbr_bindings::material_array[material_indices[slot].material];
}
@else {
    let material=pbr_bindings::material;
}
    var color=material.base_color;
@if(VERTEX_COLORS)
    color*=in.color;
@if(VERTEX_UVS) {
    let uv=(material.uv_transform*vec3<f32>(in.uv,1.0)).xy;
    if (material.flags & pbr_types::STANDARD_MATERIAL_FLAGS_BASE_COLOR_TEXTURE_BIT)!=0u {
@if(BINDLESS) {
        color*=textureSampleBias(bindless_textures_2d[material_indices[slot].base_color_texture],bindless_samplers_filtering[material_indices[slot].base_color_sampler],uv,view.mip_bias);
}
@else {
        color*=textureSampleBias(pbr_bindings::base_color_texture,pbr_bindings::base_color_sampler,uv,view.mip_bias);
}
    }
}
    if fade.y>0.001 {color.a*=1.0-smoothstep(fade.x,fade.y,length(in.world_position.xyz-view.world_position));}
    pbr_prepass_functions::prepass_alpha_discard(material.flags,material.alpha_cutoff,color);
}
}
@if(PREPASS_FRAGMENT)
@fragment
fn fragment(in:VertexOutput)->FragmentOutput {
    discard_foliage(in);
    var out:FragmentOutput;
@if(UNCLIPPED_DEPTH_ORTHO_EMULATION)
    out.frag_depth=in.unclipped_depth;
@if(NORMAL_PREPASS)
    out.normal=vec4<f32>(select(in.world_normal,vec3<f32>(0.0,1.0,0.0),fade.z>0.5)*0.5+vec3<f32>(0.5),1.0);
@if(MOTION_VECTOR_PREPASS)
    out.motion_vector=pbr_prepass_functions::calculate_motion_vector(in.world_position,in.previous_world_position);
    return out;
}
@else
@fragment
fn fragment(in:VertexOutput) {discard_foliage(in);}
