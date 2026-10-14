// Source: game/DayNightCycle.cs SkyShader / original Skybox-Sky.shader.
import bevy_pbr::render::forward_io::VertexOutput;
import bevy_pbr::render::mesh_view_bindings::view;

struct SkyParameters {
    top: vec4<f32>, horizon: vec4<f32>, ground: vec4<f32>, sun: vec4<f32>,
    direction: vec4<f32>, rim: vec4<f32>, cloud_ambient: vec4<f32>,
    offsets: vec4<f32>, factors: vec4<f32>, moon: vec4<f32>,
}
@group(constants::MATERIAL_BIND_GROUP) @binding(0) var<uniform> sky: SkyParameters;
@group(constants::MATERIAL_BIND_GROUP) @binding(1) var clouds: texture_2d<f32>;
@group(constants::MATERIAL_BIND_GROUP) @binding(2) var clouds_sampler: sampler;
@group(constants::MATERIAL_BIND_GROUP) @binding(3) var stars: texture_2d<f32>;
@group(constants::MATERIAL_BIND_GROUP) @binding(4) var stars_sampler: sampler;

@fragment
fn fragment(in: VertexOutput) -> @location(0) vec4<f32> {
    let view_dir = normalize(in.world_position.xyz - view.world_position);
    let ray_dir = -view_dir;
    let sun_dir = sky.direction.xyz;
    let moon_dir = -sun_dir;
    let scale = 1.0 - pow(1.0 - clamp(abs(ray_dir.y), 0.0, 1.0), 4.0);
    let over_horizon = select(0.0, 1.0, ray_dir.y < 0.0);
    var col = mix(sky.horizon.xyz, select(sky.ground.xyz, sky.top.xyz, ray_dir.y < 0.0), scale);
    let sun_alpha = smoothstep(0.993, 0.995, dot(ray_dir, sun_dir)) * over_horizon;
    let moon_center = -moon_dir;
    let moon_distance = dot(view_dir, moon_center);
    let nearest_sq = 1.0 - moon_distance * moon_distance;
    let moon_mask = step(0.0, moon_distance) * over_horizon * step(nearest_sq, 0.01);
    let within_moon = sqrt(max(0.0, 0.01 - nearest_sq));
    let normal = normalize(view_dir * (moon_distance - within_moon) - moon_center);
    let moon_lit = clamp(dot(normal, vec3<f32>(0.0, 1.0, 0.0)), 0.0, 1.0);
    if view_dir.y > 0.0001 {
        var coord = ray_dir.xz / ray_dir.y;
        coord += vec2<f32>(sky.factors.w / 20.0 * 0.01, sky.factors.w * 0.004);
        let star = textureSampleLevel(stars, stars_sampler, coord * 0.6, 0.0);
        let mask = clamp(-ray_dir.y, 0.0, 1.0) * (1.0 - moon_mask);
        col = mix(col, star.rgb, max(0.0, star.a) * mask);
    }
    col = mix(col, sky.sun.xyz * 4.0, sun_alpha);
    col = mix(col, sky.moon.xyz, moon_mask * moon_lit);
    if view_dir.y > 0.0001 {
        let tc = view_dir.xz / view_dir.y;
        var macro_alpha = textureSampleLevel(clouds, clouds_sampler, tc * 0.1 + sky.offsets.xy, 0.0).r;
        macro_alpha += sky.factors.z * 0.25 * textureSampleLevel(clouds, clouds_sampler, tc * 0.1 + 0.5 + sky.offsets.zw, 0.0).r;
        macro_alpha = clamp((macro_alpha - sky.factors.x) * sky.factors.y, 0.0, 1.0);
        let sun_factor = clamp(sun_dir.y * -2.0 + 1.0, 0.0, 1.0) * clamp(0.5 - dot(view_dir, sun_dir), 0.0, 1.0);
        let moon_factor = clamp(moon_dir.y * -2.0 + 1.0, 0.0, 1.0) * clamp(-dot(view_dir, moon_dir), 0.0, 1.0);
        let medium = textureSampleLevel(clouds, clouds_sampler, tc * 0.2 + sky.offsets.xy * 4.0, 0.0).g;
        let small = textureSampleLevel(clouds, clouds_sampler, tc + sky.offsets.xy * 20.0, 0.0).b;
        var body = sky.cloud_ambient.xyz + sky.rim.xyz;
        body = mix(body, sky.sun.xyz, sun_factor * medium * 0.5);
        body = mix(body, sky.moon.xyz, moon_factor * medium * 0.05);
        var rim = sky.cloud_ambient.xyz + sky.horizon.xyz + sky.rim.xyz;
        rim = mix(rim, sky.sun.xyz, sun_factor);
        rim = mix(rim, sky.moon.xyz, moon_factor * 0.25);
        let body_alpha = clamp(macro_alpha + macro_alpha * medium + macro_alpha * small, 0.0, 1.0);
        let alpha = body_alpha * clamp(view_dir.y * 2.0, 0.0, 1.0);
        col = mix(col, mix(rim, body, body_alpha), alpha);
    }
    return vec4<f32>(col, 1.0);
}
