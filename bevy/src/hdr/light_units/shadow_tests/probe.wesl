import unturned_bevy::hdr::light_units::shadow_bias::{headlight_shadow_receiver, headlight_shadow_depth};
struct Case { to_light: vec4<f32>, normal: vec4<f32>, parameters: vec4<f32>, flags: vec4<u32> }
struct Cases { entries: array<Case> }
struct Results { entries: array<vec4<f32>> }
@group(0) @binding(0) var<storage, read> cases: Cases;
@group(0) @binding(1) var<storage, read_write> results: Results;
@compute @workgroup_size(1)
fn evaluate(@builtin(global_invocation_id) id:vec3<u32>) {
    let c = cases.entries[id.x];
    let receiver = headlight_shadow_receiver(c.to_light.xyz, c.normal.xyz, vec3(0.0,0.0,1.0), c.parameters.z, c.parameters.w, c.normal.w, c.flags.x);
    results.entries[id.x] = vec4(receiver, headlight_shadow_depth(-receiver.z, c.parameters.x, c.parameters.y, c.flags.x));
}
