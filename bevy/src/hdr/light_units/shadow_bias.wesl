// Headlight-only source receiver bias. Unmarked lights retain Bevy's exact
// position/depth equations. Source references and projection derivation live in
// HDR_NIGHTVISION_PORT.md. Texel size follows the actual native shadow map.
fn headlight_shadow_receiver(
    surface_to_light: vec3<f32>, normal: vec3<f32>, backward: vec3<f32>,
    native_depth_bias: f32, native_normal_bias: f32, texel_size: f32, flags: u32,
) -> vec3<f32> {
    if (flags & 128u) == 0u {
        return -surface_to_light
            + native_depth_bias * normalize(surface_to_light)
            + normal * native_normal_bias * dot(backward, surface_to_light);
    }
    // Light3D normal bias1, renderer scale10, radial distance and grazing term.
    let grazing = 1.0 - abs(dot(normal, normalize(surface_to_light)));
    return -surface_to_light + normal * length(surface_to_light) * 10.0 * texel_size * grazing;
}
fn headlight_shadow_depth(distance: f32, native_near: f32, range: f32, flags: u32) -> f32 {
    if (flags & 128u) == 0u { return native_near / distance; }
    // Source: finite reverse-Z depth A/z-B, A=n*f/(f-n), n=min(.025,f).
    // Source .03/100 homogeneous bias is multiplied by default positional PCF
    // radius2 (ShadowBlur1). Mapping source depth to native infinite reverse-Z
    // multiplies this bias by native_near/A, preserving the occlusion threshold.
    let source_near = min(0.025, range);
    let projection_scale = native_near * (range - source_near) / (source_near * range);
    return (native_near + 0.0006 * projection_scale) / distance;
}
