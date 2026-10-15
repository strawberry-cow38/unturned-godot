// Source water_optics.gdshaderinc: the surface and submerged veil share colors.
// Source-color uniforms in Godot are decoded to linear before shader evaluation.
fn linear(c:vec3<f32>)->vec3<f32>{
    return select(c/12.92,pow((c+0.055)/1.055,vec3<f32>(2.4)),c>=vec3<f32>(0.04045));
}
fn water_shallow()->vec3<f32>{return linear(vec3<f32>(0.060,0.245,0.44));}
fn water_deep()->vec3<f32>{return linear(vec3<f32>(0.025,0.105,0.26));}
fn water_veil(depth:f32)->vec3<f32>{
    return mix(water_shallow(),water_deep(),clamp(depth/12.0,0.0,1.0))*2.2;
}
