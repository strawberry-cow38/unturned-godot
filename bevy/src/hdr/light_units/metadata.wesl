import bevy_pbr::render::mesh_view_types::ClusteredLight;

struct Lights { entries: array<ClusteredLight> }
struct Profiles { count: u32, entries: array<vec2<u32>> }
@group(0) @binding(0) var<storage,read_write> lights: Lights;
@group(0) @binding(1) var<storage,read> profiles: Profiles;

@compute @workgroup_size(64)
fn apply_profiles(@builtin(global_invocation_id) id: vec3<u32>) {
    if id.x >= profiles.count { return; }
    let profile = profiles.entries[id.x];
    if profile.x >= arrayLength(&lights.entries) { return; }
    lights.entries[profile.x].flags = (lights.entries[profile.x].flags & 127u) | profile.y;
}
