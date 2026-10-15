import bevy_core_pipeline::fullscreen_vertex_shader::FullscreenVertexOutput;
struct Settings { parameters:vec4<f32> }
@group(0) @binding(0) var source:texture_2d<f32>;
@group(0) @binding(1) var linear_sampler:sampler;
@group(0) @binding(2) var screen_mips:texture_2d<f32>;
@group(0) @binding(3) var<uniform> settings:Settings;
fn to_srgb(c:vec3<f32>)->vec3<f32> {return select(12.92*c,1.055*pow(max(c,vec3<f32>(0.0)),vec3<f32>(1.0/2.4))-0.055,c>vec3<f32>(0.0031308));}
fn to_linear(c:vec3<f32>)->vec3<f32> {return select(c/12.92,pow((c+0.055)/1.055,vec3<f32>(2.4)),c>vec3<f32>(0.04045));}
@fragment fn fragment(in:FullscreenVertexOutput)->@location(0) vec4<f32> {
    let original=textureSampleLevel(source,linear_sampler,in.uv,0.0);
    let px=1.0/vec2<f32>(textureDimensions(source));
    var c=vec3<f32>(0.0);var total=0.0;
    for(var x=-2;x<=2;x+=1){for(var y=-2;y<=2;y+=1){
        let w=1.0/(1.0+f32(x*x+y*y));
        c+=textureSampleLevel(screen_mips,linear_sampler,in.uv+vec2<f32>(f32(x),f32(y))*px*settings.parameters.z,settings.parameters.y).rgb*w;
        total+=w;
    }}
    c/=total;
    if settings.parameters.w > 0.5 {
        // InventoryUI.BACKDROP_BLUR, reused by CraftingMenu/SkillsUI/MapUI.
        c=mix(c,vec3<f32>(0.03,0.04,0.06),0.40);
    } else {
        let d=(in.uv-0.5)*vec2<f32>(px.y/px.x,1.0);
        let v=smoothstep(0.12,0.80,length(d));
        c=mix(c*0.80,vec3<f32>(0.025,0.03,0.045),0.30+0.50*v);
    }
    let fade=settings.parameters.x;
    return vec4<f32>(to_linear(mix(to_srgb(original.rgb),c,fade)),mix(original.a,1.0,fade));
}
