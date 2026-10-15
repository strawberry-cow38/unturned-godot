//! Production widgets driven by real accepted craft/refund state, not mock tiles.
use super::*;
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
    recipe: i32,
}

#[test]
#[ignore = "requires Vulkan/display; UG_CRAFTING_SHOT sets output prefix"]
fn gpu_original_categories_detail_quantity_accepted_queue_refund_search_resize_close() {
    let prefix = std::env::var("UG_CRAFTING_SHOT").unwrap_or_else(|_| "/tmp/bevy-crafting".into());
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR"));
    let bridge = CoreBridge::launch(root).unwrap();
    bridge
        .request("init", json!({"contentRoot":root.join("../game/content")}))
        .unwrap();
    let view = bridge.request("craftingView", json!({})).unwrap()["result"]["view"].clone();
    let recipe = view["tiles"]
        .as_array()
        .unwrap()
        .iter()
        .find(|t| t["name"] == "Birch Plank x2")
        .unwrap()["index"]
        .as_i64()
        .unwrap() as i32;
    let snapshot = bridge.request("snapshot", json!({})).unwrap()["state"].clone();
    let mut model = UiModel::default();
    model.adopt_snapshot(&snapshot);
    model.map_name = "PEI".into();
    let mut app = App::new();
    app.add_plugins(
        DefaultPlugins
            .set(AssetPlugin {
                file_path: root.join("../game/content").to_string_lossy().into_owned(),
                ..default()
            })
            .set(WindowPlugin {
                primary_window: Some(Window {
                    title: "Original crafting and accepted owner queue".into(),
                    resolution: (1280, 720).into(),
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
        screen: Screen::Crafting,
        ..default()
    })
    .init_resource::<Settings>()
    .init_resource::<WorldSessionEpoch>()
    .add_message::<UiIntent>()
    .add_plugins((
        backdrop::BackdropPlugin,
        swoop::SwoopPlugin,
        navbar::NavbarPlugin,
        CraftingPlugin,
    ))
    .insert_resource(Capture {
        prefix: prefix.clone(),
        stage: 0,
        frames: 0,
        requested: false,
        done: default(),
        facts: vec![],
        recipe,
    })
    .add_systems(Startup, scene)
    .add_systems(Update, accepted.before(input))
    .add_systems(
        Update,
        hover_queue.after(render::build).before(render::tooltip),
    )
    .add_systems(Last, capture);
    app.run();
    let facts: Vec<Value> =
        serde_json::from_slice(&std::fs::read(format!("{prefix}.json")).unwrap()).unwrap();
    assert!(facts[0]["tiles"].as_u64().unwrap() > 20);
    assert_eq!(facts[1]["selected"]["craftable"], false);
    assert_eq!(facts[2]["selected"]["quantity"], 2);
    assert_eq!(facts[2]["selected"]["craftable"], true);
    assert_eq!(facts[3]["queueTotal"], 2);
    assert!(
        facts[3]["tooltip"]
            .as_str()
            .unwrap()
            .contains(" · click to cancel")
    );
    assert_eq!(facts[4]["queueTotal"], 1);
    assert_eq!(facts[5]["queueTotal"], 0);
    assert_eq!(facts[6]["viewport"], json!([1024., 768.]));
    assert_eq!(facts[7]["roots"], 0);
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
        "selecting a recipe must actually draw ingredient details"
    );
    assert!(
        decode(1) != decode(2),
        "accepted supplies must enable the quantity/craft widgets"
    );
    assert!(
        decode(3) != decode(4),
        "accepted cancellation must remove one rendered job"
    );
    assert!(
        decode(6) != decode(7),
        "closing must remove the rendered menu"
    );
}
fn hover_queue(
    capture: Res<Capture>,
    menu: Res<Menu>,
    mut windows: Query<(Entity, &mut Window), With<PrimaryWindow>>,
    mut moved: MessageWriter<bevy::window::CursorMoved>,
) {
    let Ok((window, mut info)) = windows.single_mut() else {
        return;
    };
    let layout = render::Layout::new(menu.viewport);
    let position = if capture.stage == 3 {
        Vec2::new(
            layout.queue_row.max.x - 50. * layout.scale,
            layout.queue_row.center().y,
        )
    } else {
        Vec2::ZERO
    };
    info.set_cursor_position(Some(position));
    moved.write(bevy::window::CursorMoved {
        window,
        position,
        delta: None,
    });
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
    let state = bridge.request("tick", json!({})).unwrap()["state"].clone();
    world.resource_mut::<UiModel>().adopt_snapshot(&state);
    world.resource_mut::<GameSnapshot>().0 = state;
}
fn scene(mut commands: Commands) {
    commands.spawn((
        Camera2d,
        Camera {
            order: layers::HUD_ORDER,
            ..default()
        },
        IsDefaultUiCamera,
        Hdr,
        Tonemapping::None,
        DebandDither::Disabled,
    ));
    for y in 0..20 {
        for x in 0..34 {
            commands.spawn((
                Node {
                    position_type: PositionType::Absolute,
                    left: px(x as f32 * 40.),
                    top: px(y as f32 * 40.),
                    width: px(40),
                    height: px(40),
                    ..default()
                },
                GlobalZIndex(-1),
                BackgroundColor(if (x + y) % 2 == 0 {
                    Color::srgb(0.14, 0.31, 0.20)
                } else {
                    Color::srgb(0.08, 0.14, 0.21)
                }),
                Pickable::IGNORE,
            ));
        }
    }
    commands
        .spawn((
            Node {
                position_type: PositionType::Absolute,
                left: px(24),
                bottom: px(24),
                width: percent(23),
                height: px(252),
                ..default()
            },
            backdrop::AboveDashboard,
            GlobalZIndex(21),
            BackgroundColor(Color::srgb(0.55, 0.08, 0.08)),
            Pickable::IGNORE,
        ))
        .with_child((text("SOURCE VITALS", 18., TEXT), Pickable::IGNORE));
}
fn press(world: &mut World, action: impl Fn(&Action) -> bool) {
    let entity = world
        .query::<(Entity, &Action)>()
        .iter(world)
        .find_map(|(e, a)| action(a).then_some(e))
        .unwrap();
    world.trigger(Activate { entity });
}
fn capture(world: &mut World) {
    let mut c = world.remove_resource::<Capture>().unwrap();
    c.frames += 1;
    assert!(c.frames < 600, "Crafting renderer timed out");
    if c.frames > 45 && !c.requested {
        let roots = world
            .query_filtered::<Entity, With<render::Root>>()
            .iter(world)
            .count();
        let tooltip = world
            .query_filtered::<(&Text, &Visibility), With<render::TooltipText>>()
            .iter(world)
            .find_map(|(text, visible)| (*visible != Visibility::Hidden).then(|| text.0.clone()));
        let menu = world.resource::<Menu>();
        c.facts.push(json!({"stage":c.stage,"tiles":menu.options["view"]["tiles"].as_array().map_or(0,Vec::len),"selected":menu.options["view"]["selected"],
            "queueTotal":menu.options["total"],"queue":menu.options["queue"],"viewport":menu.viewport.to_array(),"roots":roots,"tooltip":tooltip}));
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
                1 => press(world, |a| matches!(a,Action::Select(id) if *id==c.recipe)),
                2 => {
                    let ingredients =
                        world.resource::<Menu>().options["view"]["selected"]["ingredients"]
                            .as_array()
                            .unwrap()
                            .clone();
                    let bridge = world.resource::<CoreBridge>();
                    for ing in ingredients {
                        for _ in 0..i(&ing, "amount") * if ing["consume"] == true { 2 } else { 1 } {
                            bridge.request("action",json!({"name":"console","args":{"text":format!("give {}",i(&ing,"id"))}})).unwrap();
                        }
                    }
                    for _ in 0..12 {
                        bridge.request("tick", json!({})).unwrap();
                    }
                    let mut menu = world.resource_mut::<Menu>();
                    menu.browse.quantity = 2;
                    menu.poll = 0.;
                }
                3 => press(world, |a| matches!(a, Action::Craft)),
                4 => press(world, |a| matches!(a, Action::Cancel(0))),
                5 => {
                    press(world, |a| matches!(a, Action::Cancel(0)));
                    let mut menu = world.resource_mut::<Menu>();
                    menu.browse.search = "Birch".into();
                    menu.browse.selected = -1;
                    menu.poll = 0.;
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
