// Source game/content/water.gdshader and swell.gdshaderinc. Physical wave height
// stays zero (WaveField.Flat); only the optical normal ripples.
import unturned_bevy::world::water_optics::{linear,water_shallow};
import bevy_pbr::render::{
    forward_io::{VertexOutput,FragmentOutput},
    pbr_fragment::pbr_input_from_standard_material,
    pbr_functions::{apply_pbr_lighting,main_pass_post_lighting_processing},
    mesh_view_bindings::view,
    view_transformations::position_ndc_to_world,
};
@if(DEPTH_PREPASS)
import bevy_pbr::prepass::utils::prepass_depth;
@group(constants::MATERIAL_BIND_GROUP) @binding(100) var<uniform> motion:vec4<f32>;
@group(constants::MATERIAL_BIND_GROUP) @binding(101) var<uniform> settings:vec4<f32>;
@group(constants::MATERIAL_BIND_GROUP) @binding(102) var reflection:texture_2d<f32>;
@group(constants::MATERIAL_BIND_GROUP) @binding(103) var reflection_sampler:sampler;

fn hashv(p:vec2<f32>)->f32{
    var p3=fract(vec3<f32>(p.x,p.y,p.x)*0.1031);
    p3+=dot(p3,p3.yzx+33.33);
    return fract((p3.x+p3.y)*p3.z);
}
fn grad2(p:vec2<f32>)->vec2<f32>{let h=hashv(p)*6.2831853;return vec2<f32>(cos(h),sin(h));}
fn gnoise(p:vec2<f32>)->f32{
    let i=floor(p);let f=fract(p);let u=f*f*f*(f*(f*6.0-15.0)+10.0);
    let a=dot(grad2(i),f);let b=dot(grad2(i+vec2<f32>(1.0,0.0)),f-vec2<f32>(1.0,0.0));
    let c=dot(grad2(i+vec2<f32>(0.0,1.0)),f-vec2<f32>(0.0,1.0));
    let d=dot(grad2(i+vec2<f32>(1.0,1.0)),f-vec2<f32>(1.0,1.0));
    return mix(mix(a,b,u.x),mix(c,d,u.x),u.y)*0.8+0.5;
}
fn fbm5(p0:vec2<f32>)->f32{
    var p=p0;var s=0.0;var a=0.5;
    for(var i=0;i<5;i++){s+=a*(gnoise(p)-0.5);p*=2.03;a*=0.5;}
    return s;
}
@fragment
fn fragment(in:VertexOutput,@builtin(front_facing) front:bool)->FragmentOutput{
    var pbr=pbr_input_from_standard_material(in,front);
    let pixel=in.position.xy-view.viewport.xy;
    let uv=pixel/view.viewport.zw;
    let world=in.world_position.xyz;
    let pxz=world.xz+motion.yz;
    let distance=length(world-view.world_position);
    let lod=clamp(1.0-(distance-25.0)/140.0,0.0,1.0);
    let np=pxz*0.055+vec2<f32>(motion.x*0.03,motion.x*-0.02);
    let n0=fbm5(np);let nx=fbm5(np+vec2<f32>(0.06,0.0));let nz=fbm5(np+vec2<f32>(0.0,0.06));
    let normal=normalize(vec3<f32>(n0-nx,0.0,n0-nz)*0.30*6.0*lod+vec3<f32>(0.0,1.0,0.0));
    pbr.world_normal=normal;pbr.N=normal;
    var foam=0.0;
@if(DEPTH_PREPASS){
    if settings.z>0.0 {
        let depth=prepass_depth(in.position,0u);
        if depth>0.0 {
            let bed=position_ndc_to_world(vec3<f32>(uv*vec2<f32>(2.0,-2.0)+vec2<f32>(-1.0,1.0),depth));
            let shore=1.0-smoothstep(0.0,0.85,max(world.y-bed.y,0.0));
            foam=shore*(0.29+0.10*fbm5(pxz*0.4+motion.x*0.15));
        }
    }
}
    foam=clamp(foam,0.0,1.0);
    let body=water_shallow();
    var reflected=linear(vec3<f32>(0.52,0.67,0.86));
    let displaced=clamp(uv+normal.xz*0.04*lod,vec2<f32>(0.001),vec2<f32>(0.999));
    if motion.w>0.0 {
        // Proper camera orientation flips local Y to retain front faces; mirror
        // pixels are flipped back here. The sRGB texture view decodes once.
        reflected=textureSample(reflection,reflection_sampler,vec2<f32>(displaced.x,1.0-displaced.y)).rgb;
        let brightness=dot(reflected,vec3<f32>(0.2126,0.7152,0.0722));
        reflected=mix(reflected,body,smoothstep(0.25,0.85,brightness)*0.75);
    }
    // The final executable source expression is exponent 2, despite the older
    // adjacent comments describing the intermediate 5 and 3 tuning steps.
    let fres=pow(1.0-clamp(dot(normal,pbr.V),0.0,1.0),2.0);
    let mirror=clamp(fres*settings.x,0.0,1.0);
    let albedo=mix(mix(body,reflected,mirror),linear(vec3<f32>(0.92,0.97,1.0)),foam);
    let alpha=clamp(mix(mix(0.10,1.0,mirror),1.0,foam),0.0,1.0);
    pbr.material.base_color=vec4<f32>(albedo,alpha);
    pbr.material.perceptual_roughness=mix(1.0,0.9,foam);
    pbr.material.reflectance=vec3<f32>(0.0);pbr.material.metallic=0.0;
    if motion.w>0.0 && settings.y>0.0 {
        pbr.material.base_color=vec4<f32>(textureSample(reflection,reflection_sampler,vec2<f32>(uv.x,1.0-uv.y)).rgb,1.0);
        pbr.material.perceptual_roughness=1.0;
    }
    var out:FragmentOutput;
    out.color=main_pass_post_lighting_processing(pbr,apply_pbr_lighting(pbr));
    return out;
}
