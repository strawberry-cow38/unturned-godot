import bevy_pbr::render::{pbr_fragment::pbr_input_from_standard_material,forward_io::{VertexOutput,FragmentOutput},pbr_functions::main_pass_post_lighting_processing};
@group(constants::MATERIAL_BIND_GROUP) @binding(100) var<uniform> mode:vec4<f32>;
@fragment fn fragment(in:VertexOutput,@builtin(front_facing) front:bool)->FragmentOutput{
    var pbr=pbr_input_from_standard_material(in,front);
    var color=pbr.material.base_color;
    if mode.x<0.5 {
        let uv=in.uv;
        let edge=smoothstep(0.0,0.45,uv.x)*(1.0-smoothstep(0.55,1.0,uv.x))*smoothstep(0.0,0.18,uv.y)*(1.0-smoothstep(0.82,1.0,uv.y));
        color.a*=edge;
    }
    var out:FragmentOutput;out.color=main_pass_post_lighting_processing(pbr,color);return out;
}
