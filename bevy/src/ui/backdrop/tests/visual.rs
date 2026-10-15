//! Real UI/camera ordering proof: scene pattern and HUD are behind the blur;
//! modal and console controls stay sharp. No stored image golden is used.
use super::*;
use bevy::{
    app::AppExit,
    asset::RenderAssetUsages,
    image::ImageSampler,
    render::{
        render_resource::{Extent3d, TextureDimension, TextureFormat},
        view::screenshot::{Screenshot, ScreenshotCaptured, save_to_disk},
    },
    window::PrimaryWindow,
    winit::WinitPlugin,
};
use std::sync::{Arc, Mutex};
#[derive(Resource)]
struct Capture {
    prefix: String,
    stage: u32,
    age: u32,
    requested: bool,
    done: Arc<Mutex<bool>>,
}
#[derive(Resource)]
struct DashboardFixture(bool);
#[test]
#[ignore = "requires Vulkan/display; UG_BACKDROP_SHOT sets PNG prefix"]
fn gpu_blurs_prior_hud_preserves_modal_console_and_recovers_on_resize() {
    run(false);
}
#[test]
#[ignore = "requires Vulkan/display; UG_BACKDROP_SHOT sets PNG prefix"]
fn gpu_dashboard_keeps_layer12_vitals_above_panels_without_radial_vignette() {
    run(true);
}
fn run(dashboard: bool) {
    let prefix =
        std::env::var("UG_BACKDROP_SHOT").unwrap_or_else(|_| "/tmp/bevy-modal-backdrop".into());
    let mut app = App::new();
    app.add_plugins(
        DefaultPlugins
            .set(WindowPlugin {
                primary_window: Some(Window {
                    title: "Source HUD-inclusive modal frost".into(),
                    resolution: (1024, 640).into(),
                    ..default()
                }),
                ..default()
            })
            .set(WinitPlugin {
                run_on_any_thread: true,
                ..default()
            }),
    )
    .add_plugins(BackdropPlugin)
    .insert_resource(DashboardFixture(dashboard))
    .insert_resource(Capture {
        prefix: prefix.clone(),
        stage: 0,
        age: 0,
        requested: false,
        done: default(),
    })
    .add_systems(Startup, scene)
    .add_systems(Update, capture);
    app.run();
    let images: Vec<_> = (0..4)
        .map(|stage| {
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
        })
        .collect();
    assert_eq!(images[0].dimensions(), (1024, 640));
    assert_eq!(images[3].dimensions(), (800, 480));
    // Rectangles entirely inside the sharp modal/console control, including
    // their high-frequency stripes. Neither is part of the screen-copy source.
    let mut sharp = vec![
        ("modal", (450, 240, 650, 400)),
        ("console", (820, 48, 1000, 112)),
    ];
    if dashboard {
        sharp.push(("vitals", (48, 288, 232, 352)));
    }
    for (name, rect) in sharp {
        for y in rect.1..rect.3 {
            for x in rect.0..rect.2 {
                assert_eq!(
                    images[0].get_pixel(x, y),
                    images[1].get_pixel(x, y),
                    "{name} at{x},{y}"
                );
            }
        }
    }
    assert_eq!(
        images[0], images[2],
        "closing frost restores the untouched prior UI composition"
    );
    let variation = |image: usize, rect: (u32, u32, u32, u32)| -> u64 {
        let mut sum = 0;
        for y in rect.1..rect.3 {
            for x in rect.0..rect.2 - 1 {
                let a = images[image].get_pixel(x, y);
                let b = images[image].get_pixel(x + 1, y);
                sum +=
                    a.0.iter()
                        .zip(b.0)
                        .map(|(a, b)| a.abs_diff(b) as u64)
                        .sum::<u64>();
            }
        }
        sum
    };
    let hud_before = variation(0, (40, 130, 240, 190));
    let hud_after = variation(1, (40, 130, 240, 190));
    let scene_before = variation(0, (48, 450, 300, 570));
    let scene_after = variation(1, (48, 450, 300, 570));
    assert!(
        hud_before > 10000 && hud_after < hud_before / 4,
        "actual prior HUD is blurred: {hud_before}→{hud_after}"
    );
    assert!(
        scene_before > 10000 && scene_after < scene_before / 4,
        "scene is blurred: {scene_before}→{scene_after}"
    );
    let vitals_before = variation(0, (48, 288, 232, 352));
    let vitals_after = variation(1, (48, 288, 232, 352));
    assert!(vitals_before > 10000);
    if dashboard {
        assert_eq!(vitals_before, vitals_after, "layer12 vitals remain sharp");
        let p = images[1].get_pixel(50, 300);
        assert!(
            p[0] > p[2],
            "red vitals must paint over the blue layer11 panel: {p:?}"
        );
        // With this uniform checker input, source dashboard has no edge vignette.
        let edge = images[1].get_pixel(24, 600);
        let middle = images[1].get_pixel(512, 600);
        assert!(
            edge.0.iter().zip(middle.0).all(|(a, b)| a.abs_diff(b) <= 1),
            "flat dashboard tint differs at edge: {edge:?}/{middle:?}"
        );
    } else {
        assert!(
            vitals_after < vitals_before / 4,
            "layer60 screen copy must include vitals"
        );
    }
    std::fs::write(format!("{prefix}.json"),serde_json::to_vec_pretty(&serde_json::json!({"style":if dashboard{"dashboard"}else{"ammoPie"},"hudVariationBefore":hud_before,"hudVariationAfter":hud_after,"sceneVariationBefore":scene_before,"sceneVariationAfter":scene_after,"vitalsVariationBefore":vitals_before,"vitalsVariationAfter":vitals_after,"modalAndConsoleUnchanged":true,"closedRestoresBaseline":true,"resized":[800,480]})).unwrap()).unwrap();
}
fn camera(order: isize) -> impl Bundle {
    (
        Camera2d,
        Camera {
            order,
            clear_color: ClearColorConfig::None,
            ..default()
        },
        Hdr,
        Tonemapping::None,
        DebandDither::Disabled,
    )
}
fn image(images: &mut Assets<Image>, size: UVec2, checker: bool) -> Handle<Image> {
    let mut data = Vec::new();
    for y in 0..size.y {
        for x in 0..size.x {
            let light = if checker {
                ((x / 8) + (y / 8)) % 2 == 0
            } else {
                (x / 4) % 2 == 0
            };
            let c = if light { 230 } else { 20 };
            data.extend_from_slice(&[c, c, c, 255]);
        }
    }
    let mut image = Image::new(
        Extent3d {
            width: size.x,
            height: size.y,
            depth_or_array_layers: 1,
        },
        TextureDimension::D2,
        data,
        TextureFormat::Rgba8UnormSrgb,
        RenderAssetUsages::default(),
    );
    image.sampler = ImageSampler::nearest();
    images.add(image)
}
fn node(x: f32, y: f32, w: f32, h: f32) -> Node {
    Node {
        position_type: PositionType::Absolute,
        left: px(x),
        top: px(y),
        width: px(w),
        height: px(h),
        ..default()
    }
}
fn scene(mut commands: Commands, mut images: ResMut<Assets<Image>>, style: Res<DashboardFixture>) {
    commands.spawn((camera(2), IsDefaultUiCamera));
    let console = commands.spawn(camera(CONSOLE_ORDER)).id();
    let pattern = image(&mut images, UVec2::new(1024, 640), true);
    let stripes = image(&mut images, UVec2::new(200, 80), false);
    commands.spawn((
        Node {
            width: percent(100),
            height: percent(100),
            ..default()
        },
        ImageNode::new(pattern).with_mode(NodeImageMode::Stretch),
        GlobalZIndex(0),
    ));
    commands.spawn((
        node(40., 120., 200., 80.),
        ImageNode::new(stripes.clone()).with_mode(NodeImageMode::Stretch),
        GlobalZIndex(10),
    ));
    commands.spawn((
        node(40., 280., 200., 80.),
        AboveDashboard,
        GlobalZIndex(21),
        ImageNode {
            image: stripes.clone(),
            image_mode: NodeImageMode::Stretch,
            color: Color::srgb(1., 0.2, 0.2),
            ..default()
        },
    ));
    commands
        .spawn((
            Node {
                width: percent(100),
                height: percent(100),
                ..default()
            },
            if style.0 {
                Backdrop::dashboard(0.)
            } else {
                Backdrop::ammo_pie(0.)
            },
            GlobalZIndex(if style.0 { 20 } else { 60 }),
        ))
        .with_children(|p| {
            if style.0 {
                // A real layer11 panel overlaps the layer12 vitals; the bars must
                // paint above it even though this root is spawned later.
                p.spawn((
                    node(40., 280., 200., 80.),
                    BackgroundColor(Color::srgb(0., 0., 1.)),
                ));
            }
            p.spawn((
                node(440., 230., 220., 180.),
                ImageNode {
                    image: stripes.clone(),
                    image_mode: NodeImageMode::Stretch,
                    color: Color::srgb(0.2, 0.9, 1.),
                    ..default()
                },
            ));
            p.spawn((
                node(440., 420., 260., 30.),
                Text::new("SHARP MODAL"),
                TextFont {
                    font_size: FontSize::Px(24.),
                    ..default()
                },
                TextColor(Color::WHITE),
            ));
        });
    commands.spawn((
        node(810., 40., 200., 80.),
        ImageNode {
            image: stripes,
            image_mode: NodeImageMode::Stretch,
            color: Color::srgb(0.2, 1., 0.2),
            ..default()
        },
        UiTargetCamera(console),
        GlobalZIndex(100),
    ));
}
fn capture(
    mut commands: Commands,
    mut state: ResMut<Capture>,
    mut roots: Query<&mut Backdrop>,
    mut windows: Query<&mut Window, With<PrimaryWindow>>,
    mut exit: MessageWriter<AppExit>,
) {
    state.age += 1;
    assert!(state.age < 800, "backdrop fixture timed out");
    if state.age > 60 && !state.requested {
        let done = state.done.clone();
        commands
            .spawn(Screenshot::primary_window())
            .observe(save_to_disk(format!(
                "{}-{}.png",
                state.prefix, state.stage
            )))
            .observe(move |_: On<ScreenshotCaptured>| {
                *done.lock().unwrap() = true;
            });
        state.requested = true;
    }
    if !*state.done.lock().unwrap() {
        return;
    }
    if state.stage == 3 {
        exit.write(AppExit::Success);
        return;
    }
    state.stage += 1;
    for mut backdrop in &mut roots {
        backdrop.fade = if state.stage == 2 { 0. } else { 1. };
    }
    if state.stage == 3 {
        windows.single_mut().unwrap().resolution.set(800., 480.);
    }
    state.age = 0;
    state.requested = false;
    state.done = default();
}
