use super::*;
#[test]
fn renderer_light_units_recover_original_energy_before_source_tone_mapping() {
    let exposure = bevy::camera::Exposure::default().exposure();
    // Godot light_storage.cpp uses energy*PI for nonphysical directional lights;
    // its diffuse shader then evaluates a Lambert/Burley BRDF containing1/PI.
    for energy in [0.015, 0.35, 1., 1.35] {
        let radiance = directional_illuminance(energy) * exposure / std::f32::consts::PI;
        assert!((radiance - energy).abs() < 1e-6);
        assert!((ambient_brightness(energy) * exposure - energy).abs() < 1e-6);
        let point_radiance =
            point_lumens(energy) * exposure / (4. * std::f32::consts::PI) / std::f32::consts::PI;
        assert!((point_radiance - energy).abs() < 1e-6);
    }
}
fn app() -> App {
    let mut app = App::new();
    app.add_plugins((MinimalPlugins, AssetPlugin::default()))
        .init_asset::<Image>()
        .init_resource::<ViewmodelTarget>()
        .insert_resource(SourceSettings {
            enabled: true,
            aces: true,
            intensity: 0.8,
            threshold: 0.9,
            exposure: 1.,
        })
        .init_resource::<crate::nightvision::NightVision>()
        .add_systems(Update, configure);
    app.world_mut().spawn((
        Window {
            resolution: (1280, 720).into(),
            ..default()
        },
        PrimaryWindow,
    ));
    app
}
#[test]
fn separate_viewport_keeps_one_tonemap_and_tracks_resize_and_inactive_arms() {
    let mut app = app();
    let main = app
        .world_mut()
        .spawn((Camera3d::default(), PlayerCamera))
        .id();
    let vm = app
        .world_mut()
        .spawn((Camera3d::default(), ViewmodelCamera))
        .id();
    let ui = app.world_mut().spawn((Camera2d, IsDefaultUiCamera)).id();
    let console = app.world_mut().spawn((Camera2d, ConsoleCamera)).id();
    let reflection = app
        .world_mut()
        .spawn((Camera3d::default(), crate::world::water::ReflectionCamera))
        .id();
    app.update();
    for e in [main, vm, ui, console, reflection] {
        assert!(app.world().get::<Hdr>(e).is_some());
        assert_eq!(
            *app.world()
                .get::<bevy::core_pipeline::tonemapping::Tonemapping>(e)
                .unwrap(),
            bevy::core_pipeline::tonemapping::Tonemapping::None
        );
        assert_eq!(
            *app.world()
                .get::<bevy::core_pipeline::tonemapping::DebandDither>(e)
                .unwrap(),
            bevy::core_pipeline::tonemapping::DebandDither::Disabled,
            "source HDR disables Bevy's tone-map-dependent debanding"
        );
    }
    let RenderTarget::Image(target) = app.world().get::<RenderTarget>(vm).unwrap() else {
        panic!("VM must be isolated")
    };
    let image = target.handle.clone();
    assert_eq!(app.world().get::<Composite>(ui).unwrap().image, image);
    assert!(app.world().get::<Composite>(ui).unwrap().active);
    assert!(app.world().get::<Composite>(console).is_none());
    assert_eq!(
        app.world().get::<HdrSettings>(reflection).unwrap().glow,
        app.world().get::<HdrSettings>(main).unwrap().glow,
        "the source reflection viewport shares the world environment"
    );
    assert_eq!(
        app.world().get::<HdrSettings>(reflection).unwrap().tone,
        app.world().get::<HdrSettings>(main).unwrap().tone,
        "the source reflection texture is completed LDR, not raw exposed HDR"
    );
    assert!(
        app.world().get::<HdrSettings>(ui).is_none(),
        "HUD must not tonemap completed world again"
    );
    let ClearColorConfig::Custom(clear) = app.world().get::<Camera>(vm).unwrap().clear_color else {
        panic!("isolated viewmodel must clear transparently")
    };
    assert_eq!(clear.to_linear(), LinearRgba::NONE);
    assert_eq!(
        app.world()
            .resource::<Assets<Image>>()
            .get(&image)
            .unwrap()
            .texture_descriptor
            .format,
        TextureFormat::Rgba16Float
    );
    let window = app
        .world_mut()
        .query_filtered::<Entity, With<PrimaryWindow>>()
        .single(app.world())
        .unwrap();
    app.world_mut()
        .get_mut::<Window>(window)
        .unwrap()
        .resolution
        .set(1600., 900.);
    app.world_mut().get_mut::<Camera>(vm).unwrap().is_active = false;
    app.update();
    assert_eq!(
        app.world().get::<Composite>(ui).unwrap().image,
        image,
        "resize preserves consumer handle"
    );
    assert!(
        !app.world().get::<Composite>(ui).unwrap().active,
        "hidden viewport must not leave stale hands onscreen"
    );
    let texture = &app
        .world()
        .resource::<Assets<Image>>()
        .get(&image)
        .unwrap()
        .texture_descriptor;
    assert_eq!((texture.size.width, texture.size.height), (1600, 900));
}
#[test]
fn source_night_vision_changes_world_glow_but_not_viewmodel_profile() {
    let source = SourceSettings {
        enabled: true,
        aces: true,
        intensity: 0.8,
        threshold: 0.9,
        exposure: 1.,
    };
    let mil = source.world(true, true, false);
    let civ = source.world(true, false, false);
    assert!((mil.glow - Vec4::new(1.76, 0.45, 0.495, 1.)).length() < 1e-6);
    assert!((civ.glow - Vec4::new(1.36, 0.30, 0.63, 1.)).length() < 1e-6);
    assert_eq!(source.viewmodel().glow, Vec4::new(0.9, 0.15, 1.25, 1.));
    let scope = source.world(true, true, true);
    assert_eq!(
        scope.tone,
        Vec4::ZERO,
        "scope stays HDR and linear for the later viewmodel tone map"
    );
}
