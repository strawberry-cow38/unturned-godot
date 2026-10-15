//! Actual UI extraction/rendering at deterministic source clock positions.
//! The fixture pauses only its frame delta while pipelines/screenshots settle.
use super::*;
use crate::ui::{
    InventoryPage, Recipe,
    backdrop::{AboveDashboard, BackdropPlugin},
};
use bevy::{
    app::AppExit,
    asset::RenderAssetUsages,
    camera::Hdr,
    core_pipeline::tonemapping::{DebandDither, Tonemapping},
    image::ImageSampler,
    render::{
        render_resource::{Extent3d, TextureDimension, TextureFormat},
        view::screenshot::{Screenshot, ScreenshotCaptured, save_to_disk},
    },
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
#[ignore = "requires Vulkan/display; UG_SWOOP_SHOT sets PNG prefix"]
fn gpu_source_menu_swoop_fades_slides_switches_and_restores_the_scene() {
    let prefix = std::env::var("UG_SWOOP_SHOT").unwrap_or_else(|_| "/tmp/bevy-menu-swoop".into());
    let mut model = UiModel::default();
    model.connected = true;
    model.map_name = "PEI".into();
    model.pages = vec![InventoryPage {
        index: 2,
        width: 8,
        height: 4,
        ..default()
    }];
    model.recipes = vec![Recipe {
        name: "Original crafting panel".into(),
        craftable: true,
        seconds: 2.,
        operation: "Craft".into(),
        ..default()
    }];
    let mut app = App::new();
    app.add_plugins(
        DefaultPlugins
            .set(WindowPlugin {
                primary_window: Some(Window {
                    title: "Original MenuSwoop clock and dashboard layers".into(),
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
    .add_plugins((BackdropPlugin, SwoopPlugin))
    .insert_resource(model)
    .insert_resource(UiState {
        screen: Screen::Playing,
        ..default()
    })
    .init_resource::<crate::settings::Settings>()
    .init_resource::<crate::entities::ItemCatalog>()
    .init_resource::<crate::profile::LocalProfile>()
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
    // Run after Bevy's fixed loop restores generic Time from Time<Virtual>.
    .add_systems(
        Update,
        freeze_delta
            .in_set(crate::ui::UiUpdate)
            .before(crate::ui::rebuild_overlay),
    )
    .add_systems(
        Update,
        crate::ui::rebuild_overlay.in_set(crate::ui::UiUpdate),
    )
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
    let before = decode(0);
    assert!(
        before == decode(9),
        "completed close must restore the original world/HUD pixels"
    );
    assert!(
        decode(1) != decode(2),
        "partial and completed opening must render differently"
    );
    assert!(
        decode(3) != decode(4),
        "tab crossfade must retire its previous panel"
    );
    assert!(
        decode(5) != before,
        "closing pixels remain visible after input closes"
    );
    // Vitals are above the open dashboard and the ducked closing map.
    for stage in [1, 2, 3, 4, 5, 6, 7, 8] {
        let image = decode(stage);
        for y in 560..590 {
            for x in 20..240 {
                assert_eq!(
                    before.get_pixel(x, y),
                    image.get_pixel(x, y),
                    "vitals obscured at stage{stage}, {x},{y}"
                );
            }
        }
    }
}
fn freeze_delta(mut time: ResMut<Time>, mut capture: ResMut<Capture>) {
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
fn scene(mut commands: Commands, mut images: ResMut<Assets<Image>>) {
    commands.spawn((
        Camera2d,
        Camera {
            order: 2,
            ..default()
        },
        IsDefaultUiCamera,
        Hdr,
        Tonemapping::None,
        DebandDither::Disabled,
    ));
    let mut pixels = vec![];
    for y in 0..640 {
        for x in 0..1024 {
            let c = if (x / 16 + y / 16) % 2 == 0 { 100 } else { 35 };
            pixels.extend_from_slice(&[c, c + 25, c + 35, 255]);
        }
    }
    let mut image = Image::new(
        Extent3d {
            width: 1024,
            height: 640,
            depth_or_array_layers: 1,
        },
        TextureDimension::D2,
        pixels,
        TextureFormat::Rgba8UnormSrgb,
        RenderAssetUsages::default(),
    );
    image.sampler = ImageSampler::nearest();
    commands.spawn((
        Node {
            width: percent(100),
            height: percent(100),
            ..default()
        },
        ImageNode::new(images.add(image)),
        GlobalZIndex(0),
    ));
    commands.spawn((
        rect(16., 550., 240., 50.),
        AboveDashboard,
        GlobalZIndex(21),
        BackgroundColor(Color::srgb(0.9, 0.1, 0.1)),
    ));
    commands.spawn((
        rect(24., 555., 220., 40.),
        AboveDashboard,
        GlobalZIndex(22),
        Text::new("LAYER 12 VITALS"),
        TextFont {
            font_size: FontSize::Px(22.),
            ..default()
        },
        TextColor(Color::WHITE),
    ));
}
fn capture(world: &mut World) {
    let mut capture = world.remove_resource::<Capture>().unwrap();
    capture.frames += 1;
    assert!(capture.frames < 600, "swoop capture timed out");
    if capture.frames > 45 && !capture.requested {
        let clocks = world.resource::<Clocks>();
        let (index, expected) = match capture.stage {
            1 => (0, 0.25),
            2 => (0, 1.),
            3 => (1, 0.25),
            4 => (1, 1.),
            5 => (1, 0.5),
            6 => (2, 1.),
            7 | 8 => (2, 0.5),
            _ => (2, 0.),
        };
        assert!(
            (clocks.0[index].t - expected).abs() < 0.00001,
            "fixture source time advanced while awaiting screenshot: stage{} expected{expected}, actual{}",
            capture.stage,
            clocks.0[index].t
        );
        let roots:Vec<_> = world.query::<(Entity,&Swoop,Option<&Backdrop>,&UiTransform)>().iter(world)
            .map(|(entity,swoop,backdrop,transform)| serde_json::json!({"entity":entity.to_bits(),"screen":format!("{:?}",swoop.0),"fade":backdrop.map(|b|b.fade),"offsetY":format!("{:?}",transform.translation.y)})).collect();
        let state = world.resource::<UiState>();
        capture.facts.push(serde_json::json!({"stage":capture.stage,"screen":format!("{:?}",state.screen),"capturesInput":state.captures_input(),"roots":roots}));
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
            let (screen, step) = match capture.stage {
                1 => (Screen::Inventory, SECONDS * 0.25),
                2 => (Screen::Inventory, SECONDS * 0.75),
                3 => (Screen::Crafting, SECONDS * 0.25),
                4 => (Screen::Crafting, SECONDS * 0.75),
                5 => (Screen::Playing, SECONDS * 0.5),
                6 => (Screen::Information, SECONDS),
                7 => (Screen::Playing, SECONDS * 0.5),
                8 => (Screen::Information, 0.),
                _ => (Screen::Playing, SECONDS),
            };
            world.resource_mut::<UiState>().screen = screen;
            capture.step = step;
            if capture.stage == 6 || capture.stage == 8 {
                world
                    .spawn((
                        Node {
                            width: percent(100),
                            height: percent(100),
                            ..default()
                        },
                        Swoop(Screen::Information),
                        Backdrop::dashboard(1.),
                        GlobalZIndex(20),
                    ))
                    .with_children(|p| {
                        p.spawn((
                            rect(16., 80., 992., 525.),
                            BackgroundColor(Color::srgb(0.1, 0.4, 0.55)),
                        ));
                        p.spawn((
                            rect(40., 100., 600., 50.),
                            Text::new("INFORMATION - SOURCE LAYER 11"),
                            TextFont {
                                font_size: FontSize::Px(30.),
                                ..default()
                            },
                            TextColor(Color::WHITE),
                        ));
                    });
            }
            if capture.stage == 7 || capture.stage == 9 {
                let maps: Vec<_> = world
                    .query::<(Entity, &Swoop)>()
                    .iter(world)
                    .filter(|(_, s)| s.0 == Screen::Information)
                    .map(|(e, _)| e)
                    .collect();
                for entity in maps {
                    world.entity_mut(entity).insert(Retiring);
                }
            }
            capture.frames = 0;
            capture.requested = false;
            capture.done = default();
        }
    }
    world.insert_resource(capture);
}
