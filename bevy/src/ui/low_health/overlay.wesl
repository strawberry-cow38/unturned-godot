import bevy_core_pipeline::fullscreen_vertex_shader::FullscreenVertexOutput;
struct Feedback { parameters: vec4<f32> }
@group(0) @binding(0) var source: texture_2d<f32>;
@group(0) @binding(1) var linear_sampler: sampler;
@group(0) @binding(2) var<uniform> settings: Feedback;
fn to_srgb(c: vec3<f32>) -> vec3<f32> {
    return select(12.92*c, 1.055*pow(max(c,vec3<f32>(0.0)),vec3<f32>(1.0/2.4))-0.055, c>vec3<f32>(0.0031308));
}
fn to_linear(c: vec3<f32>) -> vec3<f32> {
    return select(c/12.92, pow((c+0.055)/1.055,vec3<f32>(2.4)), c>vec3<f32>(0.04045));
}
@fragment fn fragment(in: FullscreenVertexOutput) -> @location(0) vec4<f32> {
    let original = textureSampleLevel(source, linear_sampler, in.uv, 0.0);
    // Godot non-HDR canvas blending occurs before the first screen copy in
    // HUD layer10, so its LowHealthOverlay samples the already painted pain.
    let src = mix(to_srgb(original.rgb), vec3<f32>(0.7490196,0.12156863,0.12156863), settings.parameters.y);
    let strength = settings.parameters.x;
    let luma = dot(src, vec3<f32>(0.299,0.587,0.114));
    let desat = mix(src, vec3<f32>(luma), strength*0.85);
    let size = vec2<f32>(textureDimensions(source));
    let uv = (in.uv-0.5)*vec2<f32>(size.x/size.y,1.0);
    let vig = smoothstep(0.25,0.85,length(uv))*strength;
    let result = mix(desat, vec3<f32>(0.55,0.02,0.02), vig*0.8);
    return vec4<f32>(to_linear(result),1.0);
}
