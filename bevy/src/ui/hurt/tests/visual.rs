use super::*;
use bevy::{
    app::AppExit,
    asset::RenderAssetUsages,
    camera::Hdr,
    core_pipeline::tonemapping::{DebandDither, Tonemapping},
    image::ImageSampler,
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
}

#[test]
#[ignore = "requires Vulkan/display; UG_HURT_SHOT sets PNG prefix"]
fn gpu_direction_marks_follow_the_camera_fade_and_restore_the_scene() {
    let prefix = std::env::var("UG_HURT_SHOT").unwrap_or_else(|_| "/tmp/bevy-hurt".into());
    let mut model = UiModel::default();
    model.connected = true;
    let mut app = App::new();
    app.add_plugins(
        DefaultPlugins
            .set(WindowPlugin {
                primary_window: Some(Window {
                    title: "Original damage direction markers".into(),
                    resolution: (640, 480).into(),
                    ..default()
                }),
                ..default()
            })
            .set(WinitPlugin {
                run_on_any_thread: true,
                ..default()
            }),
    )
    .add_plugins(HurtPlugin)
    .insert_resource(model)
    .insert_resource(UiState {
        screen: crate::ui::Screen::Playing,
        ..default()
    })
    .init_resource::<WorldSessionEpoch>()
    .add_message::<CoreEvent>()
    .insert_resource(Capture {
        prefix: prefix.clone(),
        stage: 0,
        frames: 0,
        step: 0.,
        requested: false,
        done: default(),
    })
    .add_systems(Startup, scene)
    .add_systems(Update, freeze.before(HurtUpdate))
    .add_systems(Last, capture);
    app.run();
    let decode = |stage| {
        Image::from_buffer(
            &std::fs::read(format!("{prefix}-{stage}.png")).unwrap(),
            bevy::image::ImageType::Extension("png"),
            bevy::image::CompressedImageFormats::NONE,
            true,
            ImageSampler::nearest(),
            RenderAssetUsages::default(),
        )
        .unwrap()
        .try_into_dynamic()
        .unwrap()
        .into_rgb8()
    };
    let baseline = decode(0);
    let hit = decode(1);
    let turn = decode(2);
    let fade = decode(3);
    assert_eq!(baseline, decode(4), "expired marks restore original pixels");
    for (x, y) in [(320, 165), (395, 240)] {
        assert_ne!(
            baseline.get_pixel(x, y),
            hit.get_pixel(x, y),
            "front and right hit marks must be visible"
        );
    }
    assert_eq!(
        baseline.get_pixel(320, 240),
        hit.get_pixel(320, 240),
        "source band leaves the aim point clear"
    );
    assert_eq!(baseline.get_pixel(245, 240), hit.get_pixel(245, 240));
    assert_ne!(
        baseline.get_pixel(245, 240),
        turn.get_pixel(245, 240),
        "turning right moves original frontal hit to the left"
    );
    assert_eq!(baseline.get_pixel(395, 240), turn.get_pixel(395, 240));
    assert!(
        fade.get_pixel(245, 240)[0] < turn.get_pixel(245, 240)[0],
        "half-life fades the same projected wedge"
    );
}

fn scene(mut commands: Commands) {
    commands.spawn((
        Camera2d,
        IsDefaultUiCamera,
        Hdr,
        Tonemapping::None,
        DebandDither::Disabled,
    ));
    commands.spawn((PlayerCamera, Transform::IDENTITY));
    commands.spawn((
        PlayerBody::for_motion_test(Vec3::ZERO, true, "stand"),
        Transform::IDENTITY,
    ));
    commands.spawn((
        Node {
            width: percent(100),
            height: percent(100),
            ..default()
        },
        BackgroundColor(Color::srgb(0.12, 0.18, 0.25)),
        GlobalZIndex(0),
    ));
    commands.spawn((
        Node {
            position_type: PositionType::Absolute,
            left: px(16),
            top: px(16),
            ..default()
        },
        Text::new("DAMAGE BEARINGS  |  source radius 90px"),
        TextFont {
            font_size: FontSize::Px(16.),
            ..default()
        },
        TextColor(Color::WHITE),
        GlobalZIndex(20),
    ));
}
fn freeze(mut time: ResMut<Time>, mut capture: ResMut<Capture>) {
    *time = default();
    time.advance_by(Duration::from_secs_f32(capture.step));
    capture.step = 0.;
}
fn capture(world: &mut World) {
    let mut capture = world.remove_resource::<Capture>().unwrap();
    capture.frames += 1;
    assert!(capture.frames < 500, "hurt fixture timed out");
    if capture.frames > 30 && !capture.requested {
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
        if capture.stage == 4 {
            world.write_message(AppExit::Success);
        } else {
            capture.stage += 1;
            match capture.stage {
                1 => {
                    for (sequence, damage, source) in
                        [(1, 12., [0., 0., -5.]), (2, 30., [5., 0., 0.])]
                    {
                        world.write_message(CoreEvent {
                            sequence,
                            kind: "hurt".into(),
                            data: json!({"damage":damage,"hasSource":true,"sourcePos":source}),
                        });
                    }
                }
                2 => {
                    for mut pose in world
                        .query_filtered::<&mut Transform, With<PlayerCamera>>()
                        .iter_mut(world)
                    {
                        pose.rotation = Quat::from_rotation_y(-std::f32::consts::FRAC_PI_2);
                    }
                }
                3 => capture.step = 2.25,
                _ => capture.step = 0.75,
            }
            capture.frames = 0;
            capture.requested = false;
            capture.done = default();
        }
    }
    world.insert_resource(capture);
}
