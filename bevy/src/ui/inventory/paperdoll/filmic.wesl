// Godot 4.6 tonemap.glsl tonemap_filmic, MIT; licenses/GODOT_POSTPROCESS.txt.
// The isolated source SubViewport uses Filmic with white=1 and no glow.
import bevy_ui_render::ui_vertex_output::UiVertexOutput;
@group(1) @binding(0) var portrait: texture_2d<f32>;
@group(1) @binding(1) var linear_sampler: sampler;
@group(1) @binding(2) var<uniform> parent_alpha: f32;
fn curve(c:vec3<f32>)->vec3<f32>{
    return ((c*(0.88*c+0.06)+0.002)/(c*(0.88*c+0.6)+0.06))-vec3<f32>(0.01/0.3);
}
@fragment fn fragment(in:UiVertexOutput)->@location(0)vec4<f32>{
    let sample=textureSample(portrait,linear_sampler,in.uv);
    let toned=clamp(curve(max(sample.rgb,vec3<f32>(0.)))/curve(vec3<f32>(1.)),vec3<f32>(0.),vec3<f32>(1.));
    // UiMaterial writes linear color; the canvas target performs its one sRGB
    // conversion. Transparency remains the isolated viewport's actual alpha.
    return vec4<f32>(toned,sample.a*parent_alpha);
}
