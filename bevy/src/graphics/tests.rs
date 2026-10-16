use super::*;
use crate::settings::Quality;
use bevy::camera::ImageRenderTarget;

fn app(settings: Settings) -> App {
    let mut app = App::new();
    app.add_plugins(MinimalPlugins)
        .insert_resource(settings)
        .init_resource::<UiScale>()
        .init_resource::<DirectionalLightShadowMap>()
        .init_resource::<Applied>()
        .insert_resource(crate::world::water::WaterSettings::default())
        .insert_resource(ReflectionEnv { enabled: true, every: None })
        .add_systems(Update, (window, presentation, cameras, sun, chromatic::update));
    app
}

struct Rig {
    world: Entity,
    hud: Entity,
    canvas: Entity,
    image: Entity,
    viewmodel: Entity,
    sun: Entity,
}
fn rig(app: &mut App) -> Rig {
    let w = app.world_mut();
    Rig {
        world: w.spawn((Camera3d::default(), crate::player::PlayerCamera)).id(),
        hud: w.spawn(Camera2d).id(),
        canvas: w.spawn((Camera2d, crate::ui::layers::SceneCanvas)).id(),
        image: w
            .spawn((
                Camera3d::default(),
                RenderTarget::Image(ImageRenderTarget {
                    handle: Handle::default(),
                    scale_factor: 1.,
                }),
                Msaa::Sample4,
            ))
            .id(),
        viewmodel: w.spawn((Camera3d::default(), crate::viewmodel::ViewmodelCamera, Msaa::Sample4)).id(),
        sun: w
            .spawn((crate::environment::Sun, DirectionalLight { shadow_maps_enabled: true, ..default() }))
            .id(),
    }
}
fn msaa(app: &App, e: Entity) -> Msaa {
    *app.world().get::<Msaa>(e).unwrap()
}

#[test]
fn every_aa_mode_reaches_all_window_cameras_and_spares_image_targets() {
    let mut app = app(Settings::default());
    let r = rig(&mut app);
    app.update();
    // Source default FXAA: no multisampling, FXAA on the world camera only.
    for e in [r.world, r.hud, r.canvas] {
        assert_eq!(msaa(&app, e), Msaa::Off);
    }
    assert!(app.world().get::<Fxaa>(r.world).is_some());
    assert!(app.world().get::<Fxaa>(r.hud).is_none());
    assert_eq!(msaa(&app, r.image), Msaa::Sample4, "image targets keep their own sample count");
    assert_eq!(msaa(&app, r.viewmodel), Msaa::Sample4, "the hands composite is not a window view");
    for (mode, samples) in [
        (AntiAliasing::Msaa2x, Msaa::Sample2),
        (AntiAliasing::Msaa4x, Msaa::Sample4),
        (AntiAliasing::Msaa8x, Msaa::Sample8),
        (AntiAliasing::Off, Msaa::Off),
    ] {
        app.world_mut().resource_mut::<Settings>().graphics.anti_aliasing = mode;
        app.update();
        for e in [r.world, r.hud, r.canvas] {
            assert_eq!(msaa(&app, e), samples, "{mode:?}");
        }
        assert!(app.world().get::<Fxaa>(r.world).is_none());
        assert!(app.world().get::<TemporalAntiAliasing>(r.world).is_none());
    }
    app.world_mut().resource_mut::<Settings>().graphics.anti_aliasing = AntiAliasing::Taa;
    app.update();
    assert_eq!(msaa(&app, r.world), Msaa::Off, "TAA requires Msaa::Off");
    assert!(app.world().get::<TemporalAntiAliasing>(r.world).is_some());
    assert_eq!(app.world().resource::<Applied>().msaa, 1);
    app.world_mut().resource_mut::<Settings>().graphics.anti_aliasing = AntiAliasing::Fxaa;
    app.update();
    assert!(app.world().get::<TemporalAntiAliasing>(r.world).is_none());
    assert!(app.world().get::<bevy::render::camera::MipBias>(r.world).is_none(), "TAA's mip bias leaves with it");
    assert!(app.world().get::<bevy::render::camera::TemporalJitter>(r.world).is_none());
}

#[test]
fn ambient_occlusion_only_runs_without_multisampling() {
    let mut settings = Settings::default();
    settings.graphics.ambient_occlusion = true;
    settings.graphics.anti_aliasing = AntiAliasing::Msaa4x;
    let mut app = app(settings);
    let r = rig(&mut app);
    app.update();
    assert!(app.world().get::<ScreenSpaceAmbientOcclusion>(r.world).is_none());
    app.world_mut().resource_mut::<Settings>().graphics.anti_aliasing = AntiAliasing::Fxaa;
    app.update();
    assert!(app.world().get::<ScreenSpaceAmbientOcclusion>(r.world).is_some());
    app.world_mut().resource_mut::<Settings>().graphics.ambient_occlusion = false;
    app.update();
    assert!(app.world().get::<ScreenSpaceAmbientOcclusion>(r.world).is_none());
}

#[test]
fn shadow_quality_and_distance_drive_the_sun() {
    let mut app = app(Settings::default());
    let r = rig(&mut app);
    app.update();
    // Source default High = 4096 atlas (2048 per Bevy cascade), 120 m.
    assert_eq!(app.world().resource::<DirectionalLightShadowMap>().size, 2048);
    let cascades = app.world().get::<bevy::light::CascadeShadowConfig>(r.sun).unwrap();
    assert!((cascades.bounds.last().unwrap() - 120.).abs() < 0.01);
    assert_eq!(*app.world().get::<ShadowFilteringMethod>(r.world).unwrap(), ShadowFilteringMethod::Gaussian);
    {
        let mut s = app.world_mut().resource_mut::<Settings>();
        s.graphics.shadows = Quality::Off;
        s.graphics.shadow_distance = 300.;
    }
    app.update();
    assert!(!app.world().get::<DirectionalLight>(r.sun).unwrap().shadow_maps_enabled);
    let cascades = app.world().get::<bevy::light::CascadeShadowConfig>(r.sun).unwrap();
    assert!((cascades.bounds.last().unwrap() - 300.).abs() < 0.01);
    assert_eq!(*app.world().get::<ShadowFilteringMethod>(r.world).unwrap(), ShadowFilteringMethod::Hardware2x2);
    app.world_mut().resource_mut::<Settings>().graphics.shadows = Quality::Ultra;
    app.update();
    assert!(app.world().get::<DirectionalLight>(r.sun).unwrap().shadow_maps_enabled);
    assert_eq!(app.world().resource::<DirectionalLightShadowMap>().size, 4096);
}

#[test]
fn ui_scale_water_mirror_and_lens_follow_the_rows() {
    let mut app = app(Settings::default());
    let r = rig(&mut app);
    app.update();
    // Planar water default Medium: every second frame.
    let water = app.world().resource::<crate::world::water::WaterSettings>();
    assert!(water.enabled);
    assert_eq!(water.every_frames, 2);
    assert_eq!(app.world().get::<chromatic::Lens>(r.canvas).unwrap().settings.w, 0., "aberration off by default");
    {
        let mut s = app.world_mut().resource_mut::<Settings>();
        s.graphics.ui_scale = 1.3;
        s.graphics.planar_reflection = Quality::Off;
        s.graphics.chromatic_aberration = true;
        s.graphics.chromatic_amount = 0.6;
    }
    app.update();
    assert_eq!(app.world().resource::<UiScale>().0, 1.3);
    assert!(!app.world().resource::<crate::world::water::WaterSettings>().enabled);
    let lens = app.world().get::<chromatic::Lens>(r.canvas).unwrap().settings;
    assert_eq!(lens, Vec4::new(0.6, chromatic::SAMPLES, chromatic::FALLOFF, 1.));
    assert!(app.world().get::<chromatic::Lens>(r.world).is_none(), "layer 7 is a canvas pass");
    app.world_mut().resource_mut::<Settings>().graphics.planar_reflection = Quality::Low;
    app.update();
    assert_eq!(app.world().resource::<crate::world::water::WaterSettings>().every_frames, 4);
}

#[test]
fn unsupported_sample_counts_fall_back_to_the_next_lower_supported_count() {
    assert_eq!(msaa_samples(AntiAliasing::Msaa8x, |n| n <= 4), (4, Some(8)));
    assert_eq!(msaa_samples(AntiAliasing::Msaa2x, |n| n == 4), (1, Some(2)));
    assert_eq!(msaa_samples(AntiAliasing::Msaa4x, |_| true), (4, None));
    assert_eq!(msaa_samples(AntiAliasing::Fxaa, |_| false), (1, None));
}

#[test]
fn particle_quality_scales_count_and_size_but_never_below_one() {
    let spec = crate::particles::Spec { amount: 10, size: Vec2::new(0.2, 0.4), ..default() };
    let low = crate::particles::scale_quality(spec.clone(), Quality::Low.effect_multiplier());
    assert_eq!(low.amount, 5);
    assert_eq!(low.size, Vec2::new(0.1, 0.2));
    let off = crate::particles::scale_quality(spec.clone(), Quality::Off.effect_multiplier());
    assert_eq!(off.amount, 1);
    let high = crate::particles::scale_quality(spec, Quality::High.effect_multiplier());
    assert_eq!(high.amount, 10, "the source default leaves emitters unchanged");
}
