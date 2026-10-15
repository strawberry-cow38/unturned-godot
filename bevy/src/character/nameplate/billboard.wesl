// Label3D/Sprite3D billboard in each view, including reflection cameras.
// Keep normal material depth testing: an occluded name cannot show through walls.
import bevy_pbr::render::{
    mesh_functions,
    mesh_view_bindings::view,
    forward_io::{Vertex, VertexOutput, decompress_vertex},
    view_transformations::position_world_to_clip,
};
@vertex
fn vertex(input: Vertex) -> VertexOutput {
    let vertex = decompress_vertex(input, input.instance_index);
    let model = mesh_functions::get_world_from_local(input.instance_index);
    let right = view.world_from_view[0].xyz;
    let up = view.world_from_view[1].xyz;
    let normal = view.world_from_view[2].xyz;
    let origin = model[3].xyz;
    var output: VertexOutput;
    output.world_position = vec4<f32>(origin
        + right * vertex.position.x * length(model[0].xyz)
        + up * vertex.position.y * length(model[1].xyz), 1.0);
    output.position = position_world_to_clip(output.world_position.xyz);
@if(VERTEX_NORMALS)
    output.world_normal = normal;
@if(VERTEX_UVS_A)
    output.uv = vertex.uv;
@if(VERTEX_UVS_B)
    output.uv_b = vertex.uv_b;
@if(VERTEX_TANGENTS)
    output.world_tangent = vec4<f32>(right, 1.0);
@if(VERTEX_COLORS)
    output.color = vertex.color;
@if(VERTEX_OUTPUT_INSTANCE_INDEX)
    output.instance_index = input.instance_index;
@if(VISIBILITY_RANGE_DITHER)
    output.visibility_range_dither = mesh_functions::get_visibility_range_dither_level(input.instance_index, model[3]);
    return output;
}
