// Source muzzleflash.gdshader: roll in the quad plane, then replace its basis
// with the current camera's right/up. Scope and reflection views each get their
// own billboard. The shared StandardMaterial keeps source-linear HDR2.6/Add.
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
    let roll = bitcast<f32>(mesh_functions::get_tag(input.instance_index));
    let c = cos(roll);
    let s = sin(roll);
    // Godot's column-major mat2(vec2(c,-s),vec2(s,c)).
    let local = vec2<f32>(c * vertex.position.x + s * vertex.position.y,
                        -s * vertex.position.x + c * vertex.position.y);
    let right = view.world_from_view[0].xyz;
    let up = view.world_from_view[1].xyz;
    let normal = view.world_from_view[2].xyz;
    var output: VertexOutput;
    output.world_position = vec4<f32>(model[3].xyz
        + right * local.x * length(model[0].xyz)
        + up * local.y * length(model[1].xyz), 1.0);
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
