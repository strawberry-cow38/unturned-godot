import bevy_pbr::render::mesh_view_types::ClusteredLight;
import bevy_pbr::render::pbr_lighting::{source_light_exponent, getDistanceAttenuation, source_spot_attenuation, source_headlight_fade};
struct Lights { entries: array<ClusteredLight> }
struct Result { entries: array<vec4<u32>> }
@group(0) @binding(0) var<storage, read> lights: Lights;
@group(0) @binding(1) var<storage, read_write> result: Result;
fn record(index: u32, phase: u32) {
    let base = index * 16u + phase * 8u;
    if index >= arrayLength(&lights.entries) || base + 7u >= arrayLength(&result.entries) { return; }
    let light = lights.entries[index];
    let decay = source_light_exponent((light.flags >> 8u) & 4095u);
    let cone = source_light_exponent((light.flags >> 20u) & 4095u);
    let outer = cos(0.6108652382); // source test outer angle35degrees
    result.entries[base] = vec4(index, light.flags, 0u, 0u);
    result.entries[base+1u] = bitcast<vec4<u32>>(light.position_radius);
    result.entries[base+2u] = bitcast<vec4<u32>>(vec4(
        getDistanceAttenuation(4.0, light.color_inverse_square_range.w, decay),
        getDistanceAttenuation(25.0, light.color_inverse_square_range.w, decay),
        getDistanceAttenuation(0.00000001, light.color_inverse_square_range.w, decay),
        getDistanceAttenuation(100.0, light.color_inverse_square_range.w, decay)));
    result.entries[base+3u] = bitcast<vec4<u32>>(vec4(
        source_spot_attenuation(1.0, outer, cone),
        source_spot_attenuation((1.0+outer)*0.5, outer, cone),
        source_spot_attenuation(outer, outer, cone),
        source_spot_attenuation(outer-0.1, outer, cone)));
    result.entries[base+4u] = bitcast<vec4<u32>>(vec4(
        source_headlight_fade(light.flags, 60.0, 60.0),
        source_headlight_fade(light.flags, 66.25, 60.0),
        source_headlight_fade(light.flags, 72.5, 60.0),
        source_headlight_fade(light.flags, 85.0, 60.0)));
    result.entries[base+5u] = bitcast<vec4<u32>>(vec4(
        source_headlight_fade(light.flags, 50.0, 50.0),
        source_headlight_fade(light.flags, 56.25, 50.0),
        source_headlight_fade(light.flags, 62.5, 50.0),
        source_headlight_fade(light.flags, 75.0, 50.0)));
    result.entries[base+6u] = bitcast<vec4<u32>>(vec4(light.shadow_depth_bias, light.shadow_normal_bias, light.shadow_map_near_z, light.spot_light_tan_angle));
    result.entries[base+7u] = bitcast<vec4<u32>>(light.color_inverse_square_range);
}
@compute @workgroup_size(64)
fn before(@builtin(global_invocation_id) id:vec3<u32>) { record(id.x, 0u); }
@compute @workgroup_size(64)
fn after(@builtin(global_invocation_id) id:vec3<u32>) { record(id.x, 1u); }
