//! Source local-light falloff applied at Bevy's shared PBR shader seam.
//!
//! Keeping the rest of Bevy's imported module intact preserves its BRDF, shadow
//! sampling, light textures and material paths. Authored per-light exponents
//! travel in unused bits of the pinned renderer's clustered-light flags.
mod metadata;
use bevy::{asset::AssetEventSystems, prelude::*, shader::Source};
pub(crate) use metadata::SourceFalloff;

const SHADER_PATH: &str = "embedded://bevy_pbr/render/pbr_lighting.wesl";
const SHADOW_PATH: &str = "embedded://bevy_pbr/render/shadows.wesl";
const SHADOW_MARKER: &str = "// UNTURNED_SOURCE_HEADLIGHT_SHADOW_FADE_V1\n";
const MARKER: &str = "// UNTURNED_SOURCE_LOCAL_LIGHT_FALLOFF_V3\n";
const SIGNATURE: &str =
    "fn getDistanceAttenuation(distanceSquare: f32, inverseRangeSquared: f32) -> f32 {";
const SOURCE_SIGNATURE: &str = "fn source_light_exponent(code: u32) -> f32 {\n    return select(f32(code) * 0.001, 1.0, code == 0u);\n}\nfn source_headlight_fade(flags: u32, distance: f32, begin: f32) -> f32 {\n    return select(1.0, smoothstep(0.0, 1.0, 1.0 - (distance - begin) / 25.0), (flags & 128u) != 0u);\n}\nfn source_spot_attenuation(cd: f32, cos_outer: f32, decay: f32) -> f32 {\n    let rim = max(0.0001, (1.0 - max(cd, cos_outer)) / (1.0 - cos_outer));\n    return 1.0 - pow(rim, decay);\n}\nfn getDistanceAttenuation(distanceSquare: f32, inverseRangeSquared: f32, decay: f32) -> f32 {";
const CALL: &str = "getDistanceAttenuation(distance_square, (*light).color_inverse_square_range.w)";
const SOURCE_CALL: &str = "getDistanceAttenuation(distance_square, (*light).color_inverse_square_range.w, source_light_exponent(((*light).flags >> 8u) & 4095u))";
const DISTANCE: &str = "return getRangeFalloff(distanceSquare, inverseRangeSquared) * 1.0 / max(distanceSquare, 0.0001);";
const SOURCE_DISTANCE: &str = "return getRangeFalloff(distanceSquare, inverseRangeSquared) * pow(max(sqrt(max(distanceSquare, 0.0)), 0.0001), -decay);";
const ANGLE: &str = "let spot_attenuation = attenuation * attenuation;";
const SOURCE_ANGLE: &str = "// Godot spot rim uses the outer angle directly, independent of an inner cone.\n    let source_cos_outer = -(*light).light_custom_data.w / (*light).light_custom_data.z;\n    let spot_attenuation = source_spot_attenuation(cd, source_cos_outer, source_light_exponent(((*light).flags >> 20u) & 4095u));";
const RADIANCE: &str = "rangeAttenuation * texture_sample;";
const SOURCE_RADIANCE: &str = "rangeAttenuation * texture_sample * source_headlight_fade((*light).flags, length(view_bindings::view.world_position - (*light).position_radius.xyz), 60.0);";
const SHADOW_SIGNATURE: &str = "fn fetch_spot_shadow(";
const SHADOW_WRAPPER: &str = r#"
fn fetch_spot_shadow(light_id: u32, frag_position: vec4<f32>, surface_normal: vec3<f32>, near_z: f32, frag_coord_xy: vec2<f32>) -> f32 {
    let light = &view_bindings::clustered_lights.data[light_id];
    let distance = length(view_bindings::view.world_position - (*light).position_radius.xyz);
    let fade = source_headlight_fade((*light).flags, distance, 50.0);
    return mix(1.0, source_fetch_spot_shadow_unfaded(light_id, frag_position, surface_normal, near_z, frag_coord_xy), fade);
}
"#;

#[derive(Resource)]
struct SourceLightShader(Handle<Shader>, Handle<Shader>);

pub(super) fn install(app: &mut App) {
    metadata::install(app);
    let shader = app.world().resource::<AssetServer>().load(SHADER_PATH);
    let shadows = app.world().resource::<AssetServer>().load(SHADOW_PATH);
    app.insert_resource(SourceLightShader(shader, shadows))
        .add_systems(PostUpdate, adapt_loaded_shader.before(AssetEventSystems));
}

/// Runs before asset notifications are published, so extraction never sees an
/// unadapted first load or hot reload. It changes the existing asset in place:
/// imports retain their canonical path and dependent pipelines recompile once.
fn adapt_loaded_shader(shader: Res<SourceLightShader>, mut shaders: ResMut<Assets<Shader>>) {
    for (handle, path, adapt) in [
        (
            &shader.0,
            SHADER_PATH,
            adapt_source as fn(&str) -> Result<Option<String>, &'static str>,
        ),
        (&shader.1, SHADOW_PATH, adapt_shadows),
    ] {
        let Some(asset) = shaders.get(handle) else {
            continue;
        };
        let Source::Wesl(source) = &asset.source else {
            panic!("{path} is no longer WESL");
        };
        if let Some(source) = adapt(source)
            .unwrap_or_else(|reason| panic!("{path}: {reason}; review source light adapter"))
        {
            shaders.get_mut(handle).unwrap().source = Source::Wesl(source.into());
        }
    }
}

fn adapt_shadows(source: &str) -> Result<Option<String>, &'static str> {
    if source.starts_with(SHADOW_MARKER) {
        return Ok(None);
    }
    if source.matches(SHADOW_SIGNATURE).count() != 1 {
        return Err("upstream spot shadow signature changed");
    }
    Ok(Some(format!(
        "{SHADOW_MARKER}import bevy_pbr::render::pbr_lighting::source_headlight_fade;\n{}{SHADOW_WRAPPER}",
        source.replacen(SHADOW_SIGNATURE, "fn source_fetch_spot_shadow_unfaded(", 1)
    )))
}

fn adapt_source(source: &str) -> Result<Option<String>, &'static str> {
    if source.starts_with(MARKER) {
        return Ok(None);
    }
    // Do not silently retain a changed upstream equation or partially adapt it.
    for (fragment, error) in [
        (SIGNATURE, "the upstream distance signature changed"),
        (CALL, "the upstream distance call changed"),
        (DISTANCE, "the upstream distance equation changed"),
        (ANGLE, "the upstream spot equation changed"),
        (RADIANCE, "the upstream point radiance return changed"),
    ] {
        if source.matches(fragment).count() != 1 {
            return Err(error);
        }
    }
    Ok(Some(format!(
        "{MARKER}{}",
        source
            .replacen(SIGNATURE, SOURCE_SIGNATURE, 1)
            .replacen(CALL, SOURCE_CALL, 1)
            .replacen(DISTANCE, SOURCE_DISTANCE, 1)
            .replacen(ANGLE, SOURCE_ANGLE, 1)
            .replacen(RADIANCE, SOURCE_RADIANCE, 1)
    )))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn original() -> String {
        format!(
            "// retained upstream BRDF and imports\n{SIGNATURE}\n{DISTANCE}\n}}\n{CALL}\n{ANGLE}\n{RADIANCE}\n// retained shadows"
        )
    }

    #[test]
    fn adaptation_is_narrow_idempotent_and_rejects_changed_upstream_equations() {
        let result = adapt_source(&original()).unwrap().unwrap();
        assert!(result.starts_with(MARKER));
        assert!(result.contains("// retained upstream BRDF and imports"));
        assert!(result.ends_with("// retained shadows"));
        assert!(result.contains(SOURCE_DISTANCE));
        assert!(result.contains(SOURCE_ANGLE));
        assert!(result.contains(SOURCE_SIGNATURE));
        assert!(result.contains(SOURCE_CALL));
        assert!(adapt_source(&result).unwrap().is_none());
        assert!(adapt_source(&original().replace(DISTANCE, "changed")).is_err());
        assert!(adapt_source(&original().replace(ANGLE, "changed")).is_err());
        assert!(adapt_source(&original().replace(CALL, "changed")).is_err());
        assert!(adapt_source(&original().replace(SIGNATURE, "changed")).is_err());
        assert!(adapt_source(&format!("{}{DISTANCE}", original())).is_err());
    }

    #[test]
    fn loaded_and_reloaded_shared_shader_is_adapted_before_asset_publication() {
        let mut app = App::new();
        app.add_plugins((MinimalPlugins, AssetPlugin::default()))
            .init_asset::<Shader>();
        let handle = app
            .world_mut()
            .resource_mut::<Assets<Shader>>()
            .add(Shader::from_wesl(original(), SHADER_PATH));
        app.insert_resource(SourceLightShader(handle.clone(), Handle::default()))
            .add_systems(PostUpdate, adapt_loaded_shader.before(AssetEventSystems));
        app.update();
        let import = {
            let asset = app
                .world()
                .resource::<Assets<Shader>>()
                .get(&handle)
                .unwrap();
            assert!(matches!(&asset.source, Source::Wesl(s) if s.starts_with(MARKER)));
            asset.import_path.clone()
        };
        *app.world_mut()
            .resource_mut::<Assets<Shader>>()
            .get_mut(&handle)
            .unwrap() = Shader::from_wesl(original(), SHADER_PATH);
        app.update();
        let asset = app
            .world()
            .resource::<Assets<Shader>>()
            .get(&handle)
            .unwrap();
        assert_eq!(
            asset.import_path, import,
            "all material imports retain the same shader"
        );
        assert!(matches!(&asset.source, Source::Wesl(s) if s.starts_with(MARKER)));
    }

    #[test]
    fn headlight_shadow_wrapper_preserves_sampling_and_rejects_upstream_drift() {
        let source = "// upstream sampling\nfn fetch_spot_shadow(light_id: u32) -> f32 { return source_sampler(light_id); }";
        let adapted = adapt_shadows(source).unwrap().unwrap();
        assert!(adapted.contains("return source_sampler(light_id);"));
        assert!(adapted.contains("source_headlight_fade((*light).flags, distance, 50.0)"));
        assert!(adapted.contains("mix(1.0, source_fetch_spot_shadow_unfaded"));
        assert!(SOURCE_RADIANCE.contains("60.0"));
        assert!(SOURCE_SIGNATURE.contains("smoothstep(0.0, 1.0, 1.0 - (distance - begin) / 25.0)"));
        assert!(adapt_shadows(&adapted).unwrap().is_none());
        assert!(adapt_shadows("renamed_upstream_shadow").is_err());
        assert!(adapt_shadows(&format!("{source}{source}")).is_err());
    }

    #[test]
    fn source_distance_and_outer_cone_recover_authored_profiles() {
        // Independent values from Godot get_omni_attenuation and spot_rim.
        let distance =
            |d: f32, range: f32| (1. - (d / range).powi(4)).max(0.).powi(2) / d.max(0.0001);
        assert!((distance(2., 10.) - 0.49840128).abs() < 1e-7);
        assert!((distance(5., 10.) - 0.17578125).abs() < 1e-7);
        assert_eq!(distance(10., 10.), 0.);
        assert_eq!(distance(11., 10.), 0.);
        let outer = 35_f32.to_radians().cos();
        let cone = |cos: f32| 1. - ((1. - cos.max(outer)) / (1. - outer)).max(0.0001);
        assert!((cone(1.) - 0.9999).abs() < 1e-7);
        assert!((cone((1. + outer) * 0.5) - 0.5).abs() < 1e-6);
        assert!(cone(outer).abs() < 1e-7);
        assert!(cone(outer - 0.1).abs() < 1e-7);
    }
}
