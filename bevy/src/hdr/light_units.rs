//! Source local-light falloff applied at Bevy's shared PBR shader seam.
//!
//! The source omni/spot lights currently materialized by the native adapters all
//! use decay1 and cone attenuation1. Keeping the rest of Bevy's imported module
//! intact preserves its BRDF, shadow sampling, light textures and material paths.
//! Nondefault source siren/headlight/streetlight exponents need additional
//! per-light GPU metadata when those renderers are integrated.
use bevy::{asset::AssetEventSystems, prelude::*, shader::Source};

const SHADER_PATH: &str = "embedded://bevy_pbr/render/pbr_lighting.wesl";
const MARKER: &str = "// UNTURNED_SOURCE_LOCAL_LIGHT_FALLOFF_V1\n";
const DISTANCE: &str = "return getRangeFalloff(distanceSquare, inverseRangeSquared) * 1.0 / max(distanceSquare, 0.0001);";
const SOURCE_DISTANCE: &str = "return getRangeFalloff(distanceSquare, inverseRangeSquared) / max(sqrt(max(distanceSquare, 0.0)), 0.0001);";
const ANGLE: &str = "let spot_attenuation = attenuation * attenuation;";
const SOURCE_ANGLE: &str = "// Godot spot rim uses the outer angle directly, independent of an inner cone.\n    let source_cos_outer = -(*light).light_custom_data.w / (*light).light_custom_data.z;\n    let source_rim = max(0.0001, (1.0 - max(cd, source_cos_outer)) / (1.0 - source_cos_outer));\n    let spot_attenuation = 1.0 - source_rim;";

#[derive(Resource)]
struct SourceLightShader(Handle<Shader>);

pub(super) fn install(app: &mut App) {
    let shader = app.world().resource::<AssetServer>().load(SHADER_PATH);
    app.insert_resource(SourceLightShader(shader))
        .add_systems(PostUpdate, adapt_loaded_shader.before(AssetEventSystems));
}

/// Runs before asset notifications are published, so extraction never sees an
/// unadapted first load or hot reload. It changes the existing asset in place:
/// imports retain their canonical path and dependent pipelines recompile once.
fn adapt_loaded_shader(shader: Res<SourceLightShader>, mut shaders: ResMut<Assets<Shader>>) {
    let Some(asset) = shaders.get(&shader.0) else {
        return;
    };
    let Source::Wesl(source) = &asset.source else {
        panic!("{SHADER_PATH} is no longer WESL; review the source light adapter");
    };
    let Some(source) = adapt_source(source).unwrap_or_else(|reason| {
        panic!(
            "{SHADER_PATH}: {reason}; review hdr/light_units.rs against the pinned Bevy renderer"
        )
    }) else {
        return;
    };
    shaders.get_mut(&shader.0).unwrap().source = Source::Wesl(source.into());
}

fn adapt_source(source: &str) -> Result<Option<String>, &'static str> {
    if source.starts_with(MARKER) {
        return Ok(None);
    }
    // Do not silently retain a changed upstream equation or partially adapt it.
    if source.matches(DISTANCE).count() != 1 {
        return Err("the upstream distance equation changed");
    }
    if source.matches(ANGLE).count() != 1 {
        return Err("the upstream spot equation changed");
    }
    Ok(Some(format!(
        "{MARKER}{}",
        source
            .replacen(DISTANCE, SOURCE_DISTANCE, 1)
            .replacen(ANGLE, SOURCE_ANGLE, 1)
    )))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn original() -> String {
        format!("// retained upstream BRDF and imports\n{DISTANCE}\n{ANGLE}\n// retained shadows")
    }

    #[test]
    fn adaptation_is_narrow_idempotent_and_rejects_changed_upstream_equations() {
        let result = adapt_source(&original()).unwrap().unwrap();
        assert!(result.starts_with(MARKER));
        assert!(result.contains("// retained upstream BRDF and imports"));
        assert!(result.ends_with("// retained shadows"));
        assert!(result.contains(SOURCE_DISTANCE));
        assert!(result.contains(SOURCE_ANGLE));
        assert!(adapt_source(&result).unwrap().is_none());
        assert!(adapt_source(&original().replace(DISTANCE, "changed")).is_err());
        assert!(adapt_source(&original().replace(ANGLE, "changed")).is_err());
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
        app.insert_resource(SourceLightShader(handle.clone()))
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
