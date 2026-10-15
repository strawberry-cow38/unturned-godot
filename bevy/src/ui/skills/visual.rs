//! Render the production graph from accepted owner data and activate real widgets.
use super::*;
use crate::{
    bridge::CoreBridge,
    ui::backdrop::{AboveDashboard, BackdropPlugin},
};
use bevy::{
    app::AppExit,
    camera::Hdr,
    core_pipeline::tonemapping::{DebandDither, Tonemapping},
    render::view::screenshot::{Screenshot, ScreenshotCaptured, save_to_disk},
    winit::WinitPlugin,
};
use std::sync::{Arc, Mutex};

#[derive(Resource)]
struct Capture {
    prefix: String,
    stage: usize,
    frames: usize,
    requested: bool,
    done: Arc<Mutex<bool>>,
    facts: Vec<Value>,
}

#[test]
#[ignore = "requires Vulkan/display; UG_SKILLS_SHOT sets output prefix"]
fn gpu_original_graph_buttons_accepted_unlock_pan_resize_and_close() {
    let prefix = std::env::var("UG_SKILLS_SHOT").unwrap_or_else(|_| "/tmp/bevy-skills".into());
    let bridge = tests::bridge();
    bridge
        .request("action", json!({"name":"console","args":{"text":"xp 500"}}))
        .unwrap();
    tests::pump(&bridge);
    let snapshot = tests::snapshot(&bridge);
    let mut model = UiModel::default();
    model.adopt_snapshot(&snapshot);
    model.map_name = "PEI".into();
    let mut app = App::new();
    app.add_plugins(
        DefaultPlugins
            .set(WindowPlugin {
                primary_window: Some(Window {
                    title: "Original Skills graph and accepted Learn".into(),
                    resolution: (1600, 1000).into(),
                    ..default()
                }),
                ..default()
            })
            .set(WinitPlugin {
                run_on_any_thread: true,
                ..default()
            }),
    )
    .insert_resource(bridge)
    .insert_resource(GameSnapshot(snapshot))
    .insert_resource(model)
    .insert_resource(UiState {
        screen: Screen::Skills,
        ..default()
    })
    .init_resource::<WorldSessionEpoch>()
    .init_resource::<Settings>()
    .add_message::<UiIntent>()
    .add_plugins((
        BackdropPlugin,
        swoop::SwoopPlugin,
        super::super::navbar::NavbarPlugin,
        SkillsPlugin,
    ))
    .insert_resource(Capture {
        prefix: prefix.clone(),
        stage: 0,
        frames: 0,
        requested: false,
        done: default(),
        facts: vec![],
    })
    .add_systems(Startup, scene)
    .add_systems(Update, accepted.before(sync))
    .add_systems(Last, capture);
    app.run();
    let facts: Vec<Value> =
        serde_json::from_slice(&std::fs::read(format!("{prefix}.json")).unwrap()).unwrap();
    assert_eq!(
        facts
            .iter()
            .map(|s| s["experience"].as_u64().unwrap())
            .collect::<Vec<_>>(),
        [500, 500, 480, 440, 380, 380, 380, 380]
    );
    assert_eq!(facts[2]["learned"], json!(["heavy_lifter"]));
    assert_eq!(
        facts[4]["learned"],
        json!(["heavy_lifter", "steady_hands", "zeroing"])
    );
    assert_eq!(facts[5]["reason"], "Needs Sharpening.");
    assert_eq!(facts[7]["roots"], 0);
    assert_eq!(facts[7]["capturesInput"], false);
    for stage in 0..8 {
        assert!(
            std::fs::metadata(format!("{prefix}-{stage}.png"))
                .unwrap()
                .len()
                > 10_000
        );
    }
    let decode = |stage| {
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
    assert!(
        decode(0) != decode(1),
        "selecting a node must render its detail panel"
    );
    assert!(
        decode(1) != decode(2),
        "accepted Learn must recolor the node and XP"
    );
    assert!(
        decode(6) != decode(7),
        "closing must remove the rendered dashboard"
    );
}

fn accepted(world: &mut World) {
    let intents: Vec<_> = world.resource_mut::<Messages<UiIntent>>().drain().collect();
    let bridge = world.resource::<CoreBridge>();
    for intent in intents {
        if let UiIntent::Action { name, args } = intent {
            bridge
                .request("action", json!({"name":name,"args":args}))
                .unwrap();
        }
    }
    bridge.request("tick", json!({})).unwrap();
    let snapshot = tests::snapshot(bridge);
    world.resource_mut::<GameSnapshot>().0 = snapshot;
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
fn scene(mut commands: Commands) {
    commands.spawn((
        Camera2d,
        Camera {
            order: crate::ui::layers::HUD_ORDER,
            ..default()
        },
        IsDefaultUiCamera,
        Hdr,
        Tonemapping::None,
        DebandDither::Disabled,
    ));
    for y in 0..25 {
        for x in 0..40 {
            commands.spawn((
                rect(x as f32 * 40., y as f32 * 40., 40., 40.),
                GlobalZIndex(-1),
                BackgroundColor(if (x + y) % 2 == 0 {
                    Color::srgb(0.14, 0.31, 0.20)
                } else {
                    Color::srgb(0.08, 0.14, 0.21)
                }),
            ));
        }
    }
    commands
        .spawn((
            Node {
                position_type: PositionType::Absolute,
                left: px(16),
                bottom: px(20),
                width: px(240),
                height: px(50),
                ..default()
            },
            AboveDashboard,
            GlobalZIndex(21),
            BackgroundColor(Color::srgb(0.7, 0.1, 0.1)),
        ))
        .with_child((
            Text::new("SOURCE VITALS"),
            TextFont {
                font_size: FontSize::Px(22.),
                ..default()
            },
            TextColor(Color::WHITE),
        ));
}
fn select(world: &mut World, id: &str) {
    let button = world
        .query::<(Entity, &Action)>()
        .iter(world)
        .find_map(|(e, a)| matches!(a,Action::Select(candidate) if candidate==id).then_some(e))
        .unwrap();
    world.trigger(Activate { entity: button });
}
fn learn(world: &mut World) {
    let button = world
        .query::<(Entity, &Action)>()
        .iter(world)
        .find_map(|(e, a)| matches!(a, Action::Learn).then_some(e))
        .unwrap();
    world.trigger(Activate { entity: button });
}
fn capture(world: &mut World) {
    let mut c = world.remove_resource::<Capture>().unwrap();
    c.frames += 1;
    assert!(c.frames < 600, "Skills render timed out");
    if c.frames > 45 && !c.requested {
        if c.stage == 5 {
            let (text, layout) = world
                .query::<(&Text, &bevy::text::TextLayoutInfo)>()
                .iter(world)
                .find(|(text, _)| text.0.starts_with("REQUIRES") && text.0.contains('✓'))
                .unwrap();
            assert!(
                layout.glyphs.len() >= text.0.chars().filter(|c| !c.is_whitespace()).count(),
                "both original prerequisite state glyphs must be rasterized"
            );
        }
        let root_facts: Vec<_> = world
            .query_filtered::<(Entity, &ComputedNode, &UiTransform, Option<&backdrop::Backdrop>, Option<&UiTargetCamera>), With<render::Root>>()
            .iter(world)
            .map(|(e,n,t,b,cam)|json!({"id":e.to_bits(),"size":n.size().to_array(),"offset":format!("{:?}",t.translation),"fade":b.map(|v|v.fade),"camera":cam.map(|v|v.0.to_bits())}))
            .collect();
        eprintln!(
            "Skills stage{} roots{} time{} delta{}",
            c.stage,
            serde_json::to_string(&root_facts).unwrap(),
            world.resource::<Time>().elapsed_secs(),
            world.resource::<Time>().delta_secs()
        );
        let roots = world
            .query_filtered::<Entity, With<render::Root>>()
            .iter(world)
            .count();
        let menu = world.resource::<Menu>();
        let node = menu
            .tree
            .nodes
            .iter()
            .find(|n| Some(&n.id) == menu.selected.as_ref());
        let learned: Vec<_> = menu
            .tree
            .nodes
            .iter()
            .filter(|n| n.learned)
            .map(|n| n.id.clone())
            .collect();
        c.facts.push(json!({"stage":c.stage,"experience":menu.tree.experience,"selected":menu.selected,"learned":learned,"reason":node.map(|n|n.reason.as_str()),"pan":menu.pan.to_array(),"viewport":menu.viewport.to_array(),"roots":roots,"rootFacts":root_facts,"capturesInput":world.resource::<UiState>().captures_input()}));
        let done = c.done.clone();
        world
            .spawn(Screenshot::primary_window())
            .observe(save_to_disk(format!("{}-{}.png", c.prefix, c.stage)))
            .observe(move |_: On<ScreenshotCaptured>| {
                *done.lock().unwrap() = true;
            });
        c.requested = true;
    }
    if *c.done.lock().unwrap() {
        if c.stage == 7 {
            std::fs::write(
                format!("{}.json", c.prefix),
                serde_json::to_vec_pretty(&c.facts).unwrap(),
            )
            .unwrap();
            world.write_message(AppExit::Success);
        } else {
            c.stage += 1;
            c.frames = 0;
            c.requested = false;
            *c.done.lock().unwrap() = false;
            match c.stage {
                1 => select(world, "heavy_lifter"),
                2 => learn(world),
                3 => {
                    select(world, "steady_hands");
                    learn(world);
                    select(world, "zeroing");
                    let mut menu = world.resource_mut::<Menu>();
                    menu.pan = geometry::Graph::new(&menu.tree).clamp(
                        Vec2::new(menu.pan.x, -530.),
                        geometry::Layout::new(menu.viewport).clip.size(),
                    );
                }
                4 => {
                    learn(world);
                    select(world, "breath_control");
                }
                5 => {
                    select(world, "improvised_armour");
                    learn(world);
                    world.resource_mut::<Menu>().pan.y = -250.;
                }
                6 => {
                    world
                        .query_filtered::<&mut Window, With<PrimaryWindow>>()
                        .single_mut(world)
                        .unwrap()
                        .resolution
                        .set(1024., 768.);
                }
                _ => world.resource_mut::<UiState>().close_overlay(),
            }
        }
    }
    world.insert_resource(c);
}
