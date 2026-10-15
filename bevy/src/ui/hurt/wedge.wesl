import bevy_ui_render::ui_vertex_output::UiVertexOutput;
@group(1) @binding(0) var<uniform> state: vec4<f32>;
const HALF: f32 = 0.38397243544; // 22 degrees
const STEP: f32 = 0.07679448709; // original ten straight polygon segments
fn linear(c: vec3<f32>) -> vec3<f32> {
    return select(c / 12.92, pow((c + 0.055) / 1.055, vec3<f32>(2.4)), c > vec3<f32>(0.04045));
}
@fragment fn fragment(in: UiVertexOutput) -> @location(0) vec4<f32> {
    let p = (in.uv - vec2<f32>(0.5)) * 180.0;
    let a = atan2(p.x, -p.y) - state.x;
    let delta = atan2(sin(a), cos(a));
    if abs(delta) > HALF { discard; }
    let segment = clamp(floor((delta + HALF) / STEP), 0.0, 9.0);
    let midpoint = -HALF + (segment + 0.5) * STEP;
    // Intersect the same inner/outer polygon edges as DrawColoredPolygon,
    // preserving its faceted band instead of substituting a smooth annulus.
    let distance = length(p) * cos(delta - midpoint);
    let edge = cos(STEP * 0.5);
    if distance < 55.8 * edge || distance > 90.0 * edge { discard; }
    let color = select(vec4<f32>(0.95, 0.15, 0.1, 0.85), vec4<f32>(1.0, 0.35, 0.2, 0.9), state.z > 0.5);
    return vec4<f32>(linear(color.rgb), color.a * state.y);
}
