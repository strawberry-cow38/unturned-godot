use super::*;
use crate::ui::{
    backdrop::{AboveDashboard, Backdrop, BackdropPlugin},
    layers::{BelowHud, LayersPlugin},
};
use bevy::{
    app::AppExit,
    render::view::screenshot::{Screenshot, ScreenshotCaptured, save_to_disk},
    winit::WinitPlugin,
};
use std::sync::{Arc, Mutex};
#[derive(Resource)]
struct Capture {
    prefix: String,
    stage: usize,
    frames: u32,
    step: f32,
    requested: bool,
    done: Arc<Mutex<bool>>,
    facts: Vec<serde_json::Value>,
}
#[test]
#[ignore = "requires Vulkan/display; UG_LOW_HEALTH_SHOT sets PNG prefix"]
fn gpu_original_pain_low_health_and_canvas_boundaries() {
    let prefix =
        std::env::var("UG_LOW_HEALTH_SHOT").unwrap_or_else(|_| "/tmp/bevy-low-health".into());
    let mut model = UiModel::default();
    model.connected = true;
    model.health = 100.;
    let mut app = App::new();
    app.add_plugins(
        DefaultPlugins
            .set(WindowPlugin {
                primary_window: Some(Window {
                    resolution: (960, 600).into(),
                    title: "Original pain and low-health canvas".into(),
                    ..default()
                }),
                ..default()
            })
            .set(WinitPlugin {
                run_on_any_thread: true,
                ..default()
            }),
    )
    .add_plugins((
        LayersPlugin,
        BackdropPlugin,
        LowHealthPlugin,
        crate::hdr::HdrPlugin,
        crate::nightvision::NightVisionPlugin,
    ))
    .insert_resource(model)
    .init_resource::<crate::viewmodel::lights::Switches>()
    .init_resource::<HurtFeedback>()
    .init_resource::<WorldSessionEpoch>()
    .insert_resource(UiState {
        screen: crate::ui::Screen::Playing,
        ..default()
    })
    .insert_resource(Capture {
        prefix: prefix.clone(),
        stage: 0,
        frames: 0,
        step: 0.,
        requested: false,
        done: default(),
        facts: vec![],
    })
    .add_systems(Startup, scene)
    .add_systems(Update, freeze.before(update))
    .add_systems(Last, capture);
    app.run();
    let image = |stage| {
        Image::from_buffer(
            &std::fs::read(format!("{prefix}-{stage}.png")).unwrap(),
            bevy::image::ImageType::Extension("png"),
            bevy::image::CompressedImageFormats::NONE,
            true,
            bevy::image::ImageSampler::nearest(),
            bevy::asset::RenderAssetUsages::default(),
        )
        .unwrap()
        .try_into_dynamic()
        .unwrap()
        .into_rgb8()
    };
    let healthy = image(0);
    assert!(
        healthy == image(4),
        "healed state restores the same scene and HUD"
    );
    assert!(healthy != image(1) && image(1) != image(2));
    for stage in 1..=3 {
        let changed = image(stage);
        for y in 35..55 {
            for x in 830..900 {
                assert_eq!(
                    healthy.get_pixel(x, y),
                    changed.get_pixel(x, y),
                    "HUD must remain above pain/lowHP"
                );
            }
        }
    }
    // Compare the actual shader to the independent source canvas equation at
    // center and corner, including pain BEFORE desaturation; no golden images.
    let facts: Vec<serde_json::Value> =
        serde_json::from_slice(&std::fs::read(format!("{prefix}.json")).unwrap()).unwrap();
    for stage in 0..=3 {
        let strength = facts[stage]["strength"].as_f64().unwrap();
        let pain = facts[stage]["pain"].as_f64().unwrap();
        let img = image(stage);
        for (x, y) in [(550u32, 300u32), (940, 580)] {
            let source = healthy.get_pixel(x, y).0.map(|v| v as f64 / 255.);
            let tint = [0.7490196, 0.12156863, 0.12156863];
            let src = std::array::from_fn::<_, 3, _>(|i| source[i] * (1. - pain) + tint[i] * pain);
            let luma = src[0] * 0.299 + src[1] * 0.587 + src[2] * 0.114;
            let dx = ((x as f64 + 0.5) / 960. - 0.5) * 1.6;
            let dy = (y as f64 + 0.5) / 600. - 0.5;
            let t = ((dx.hypot(dy) - 0.25) / 0.6).clamp(0., 1.);
            let vig = t * t * (3. - 2. * t) * strength * 0.8;
            let red = [0.55, 0.02, 0.02];
            for c in 0..3 {
                let desat = src[c] * (1. - strength * 0.85) + luma * strength * 0.85;
                let expected = desat * (1. - vig) + red[c] * vig;
                let got = img.get_pixel(x, y).0[c] as f64 / 255.;
                assert!(
                    (got - expected).abs() < 0.015,
                    "stage{stage} px{x},{y} c{c}: {got} vs{expected}"
                );
            }
        }
    }
    assert_eq!(image(5).dimensions(), (640, 640));
    assert!(
        image(7) != image(8) && image(8) != image(9),
        "both actual NV modes must affect the shared pre-HUD world/VM canvas"
    );
}
fn freeze(mut time: ResMut<Time>, mut capture: ResMut<Capture>) {
    *time = default();
    time.advance_by(std::time::Duration::from_secs_f32(capture.step));
    capture.step = 0.;
}
fn rect(x: f32, y: f32, w: f32, h: f32) -> Node {
    Node {
        position_type: PositionType::Absolute,
        left: px(x),
        top: px(y),
        width: px(w),
        height: px(h),
        ..default()
    }
}
fn scene(
    mut commands: Commands,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
) {
    commands.spawn((
        Camera3d::default(),
        crate::player::PlayerCamera,
        Camera {
            order: 0,
            clear_color: ClearColorConfig::Custom(Color::srgb(0.2, 0.7, 0.35)),
            ..default()
        },
    ));
    commands.spawn((
        Camera3d::default(),
        crate::viewmodel::ViewmodelCamera,
        Camera {
            order: 1,
            ..default()
        },
        bevy::camera::visibility::RenderLayers::layer(1),
    ));
    commands.spawn((
        Mesh3d(meshes.add(Cuboid::new(0.3, 0.12, 0.1))),
        MeshMaterial3d(materials.add(StandardMaterial {
            base_color: Color::srgb(0.85, 0.05, 0.1),
            unlit: true,
            ..default()
        })),
        Transform::from_xyz(0.35, -0.2, -1.5),
        bevy::camera::visibility::RenderLayers::layer(1),
    ));
    commands.spawn((
        Camera2d,
        Camera {
            order: 3,
            clear_color: ClearColorConfig::None,
            ..default()
        },
        IsDefaultUiCamera,
        bevy::camera::Hdr,
        bevy::core_pipeline::tonemapping::Tonemapping::None,
        bevy::core_pipeline::tonemapping::DebandDither::Disabled,
    ));
    commands.spawn((
        rect(20., 140., 280., 210.),
        BelowHud,
        GlobalZIndex(9),
        BackgroundColor(Color::srgb(0.15, 0.4, 0.8)),
    ));
    commands.spawn((
        rect(30., 160., 260., 60.),
        BelowHud,
        GlobalZIndex(9),
        Text::new("DEPARTING MAP\nSOURCE LAYER 9"),
        TextFont {
            font_size: FontSize::Px(20.),
            ..default()
        },
        TextColor(Color::WHITE),
    ));
    commands.spawn((
        rect(820., 25., 110., 55.),
        AboveDashboard,
        GlobalZIndex(21),
        BackgroundColor(Color::srgb(0.8, 0.1, 0.2)),
    ));
    commands.spawn((
        rect(25., 35., 700., 55.),
        GlobalZIndex(20),
        Text::new("HUD 10 - ABOVE PAIN AND LOW HEALTH"),
        TextFont {
            font_size: FontSize::Px(25.),
            ..default()
        },
        TextColor(Color::WHITE),
    ));
}
fn capture(world: &mut World) {
    let mut capture = world.remove_resource::<Capture>().unwrap();
    capture.frames += 1;
    assert!(capture.frames < 300);
    if capture.frames > 45 && !capture.requested {
        let strength = world.resource::<State>().strength;
        let pain = world.resource::<HurtFeedback>().pain_alpha;
        capture
            .facts
            .push(serde_json::json!({"stage":capture.stage,"strength":strength,"pain":pain}));
        let done = capture.done.clone();
        world
            .spawn(Screenshot::primary_window())
            .observe(save_to_disk(format!(
                "{}-{}.png",
                capture.prefix, capture.stage
            )))
            .observe(move |_: On<ScreenshotCaptured>| {
                *done.lock().unwrap() = true;
            });
        capture.requested = true;
    }
    if *capture.done.lock().unwrap() {
        if capture.stage == 9 {
            std::fs::write(
                format!("{}.json", capture.prefix),
                serde_json::to_vec_pretty(&capture.facts).unwrap(),
            )
            .unwrap();
            world.write_message(AppExit::Success);
        } else {
            capture.stage += 1;
            let (health, pain, dt) = match capture.stage {
                1 => (100., 0.6, 0.),
                2 => (15., 0.4, std::f32::consts::LN_2 / 6.),
                3 => (0., 0., 1.),
                4 => (100., 0., 8.),
                5 | 6 => (0., 0., 2.),
                _ => (100., 0., 8.),
            };
            world.resource_mut::<UiModel>().health = health;
            world.resource_mut::<HurtFeedback>().pain_alpha = pain;
            capture.step = dt;
            if capture.stage == 5 {
                for mut window in world.query::<&mut Window>().iter_mut(world) {
                    window.resolution.set(640., 640.);
                }
            }
            if capture.stage == 6 {
                // Both source higher screen-copy layers remain coherent above
                // the low-health pass; ordinary HUD gets blurred by the modal.
                world.spawn((
                    Node {
                        width: percent(100),
                        height: percent(100),
                        ..default()
                    },
                    Backdrop::dashboard(1.),
                ));
                world
                    .spawn((
                        Node {
                            width: percent(100),
                            height: percent(100),
                            ..default()
                        },
                        Backdrop::ammo_pie(1.),
                    ))
                    .with_children(|p| {
                        p.spawn((
                            rect(60., 260., 500., 70.),
                            BackgroundColor(Color::srgb(0.05, 0.2, 0.45)),
                            Text::new("SHARP MODAL ABOVE BOTH COPIES"),
                            TextFont {
                                font_size: FontSize::Px(22.),
                                ..default()
                            },
                            TextColor(Color::WHITE),
                        ));
                    });
            }
            if capture.stage >= 7 {
                let panels: Vec<_> = world
                    .query_filtered::<Entity, With<Backdrop>>()
                    .iter(world)
                    .collect();
                for panel in panels {
                    world.despawn(panel);
                }
                let mut switches = world.resource_mut::<crate::viewmodel::lights::Switches>();
                switches.nightvision = capture.stage != 9;
                switches.military = capture.stage == 7;
            }
            capture.frames = 0;
            capture.requested = false;
            capture.done = default();
        }
    }
    world.insert_resource(capture);
}
