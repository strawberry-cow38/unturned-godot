import bevy_pbr::render::{pbr_fragment::pbr_input_from_standard_material,forward_io::{VertexOutput,FragmentOutput},pbr_functions::main_pass_post_lighting_processing,mesh_view_bindings::view};
// Source binoculars.gdshader: a disc in each eyepiece of the CARRIED pair shows the ONE
// magnified render where the disc sits on screen, so the image runs on across both lenses.
// The scene is linear HDR (no tonemap), so unlike the Godot shader no inverse ACES is needed:
// the viewmodel pass tonemaps it once, as the scope glass does.
@group(constants::MATERIAL_BIND_GROUP) @binding(100) var<uniform> params:vec4<f32>;
@group(constants::MATERIAL_BIND_GROUP) @binding(101) var scene:texture_2d<f32>;
@group(constants::MATERIAL_BIND_GROUP) @binding(102) var scene_sampler:sampler;
@fragment fn fragment(in:VertexOutput,@builtin(front_facing) front:bool)->FragmentOutput {
    let c=in.uv-vec2<f32>(0.5);
    let r=length(c)*2.0;
    if r>1.0 {discard;}
    // The arms target is oversized against the window (Viewmodel.ViewportOversize):
    // map this target pixel back to the true screen UV.
    let vp=(in.position.xy-view.viewport.xy)/view.viewport.zw;
    let suv=(vp-vec2<f32>(0.5))*params.yz+vec2<f32>(0.5);
    var col=textureSampleLevel(scene,scene_sampler,suv,0.0).rgb;
    col*=1.0-params.x*smoothstep(0.6,1.0,r);
    let pbr=pbr_input_from_standard_material(in,front);
    var out:FragmentOutput;out.color=main_pass_post_lighting_processing(pbr,vec4<f32>(col,1.0));return out;
}
