//! Production dashboard widgets driven by an actual integrated owner session.
//! Scripted pointer edges exercise the same controller as the OS input batch.
use super::{controller::Menu, model::*, *};
use crate::{
    bridge::CoreBridge,
    player::{GameSnapshot, WorldSessionEpoch},
    settings::Settings,
    ui::{Screen, UiIntent, UiModel, UiState, input_order::FrameInput},
    world::WorldConfig,
};
use bevy::{
    app::AppExit,
    camera::{Hdr, visibility::RenderLayers},
    core_pipeline::tonemapping::{DebandDither, Tonemapping},
    input::{ButtonState, mouse::MouseButtonInput},
    render::view::screenshot::{Screenshot, ScreenshotCaptured, save_to_disk},
    window::{CursorMoved, PrimaryWindow, WindowEvent},
    winit::WinitPlugin,
};
use serde_json::{Value, json};
use std::sync::{Arc, Mutex};

#[derive(Resource)]
struct Capture {
    prefix: String,
    stage: usize,
    frames: u32,
    pending: bool,
    done: Arc<Mutex<bool>>,
    facts: Vec<Value>,
    magazine: Cell,
    money: bool,
    sent: Vec<Value>,
}
fn action(bridge: &CoreBridge, name: &str, args: Value) {
    let result = bridge
        .request("action", json!({"name":name,"args":args}))
        .unwrap();
    assert_eq!(result["result"]["queued"], true, "{result}");
}
fn tick(bridge: &CoreBridge, count: usize) -> Value {
    let mut state = Value::Null;
    for _ in 0..count {
        state = bridge.request("tick", json!({})).unwrap()["state"].clone();
    }
    state
}
fn view(bridge: &CoreBridge) -> api::View {
    serde_json::from_value(bridge.request("inventoryView", json!({})).unwrap()["result"].clone())
        .unwrap()
}
#[test]
#[ignore = "requires Vulkan/display; UG_INVENTORY_SHOT sets output prefix"]
fn gpu_original_dashboard_context_accepted_split_and_magazine_wheel() {
    run(false);
}
#[test]
#[ignore = "requires Vulkan/display; UG_INVENTORY_SHOT sets output prefix"]
fn gpu_original_currency_preview_and_accepted_exact_split() {
    run(true);
}
fn run(money: bool) {
    let prefix =
        std::env::var("UG_INVENTORY_SHOT").unwrap_or_else(|_| "/tmp/bevy-inventory".into());
    let config = WorldConfig::default();
    let bridge = CoreBridge::launch(std::path::Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
    bridge.request("init", json!({"contentRoot":config.content_root,"starterKit":true,"allowCheats":true,"name":"Inventory test"})).unwrap();
    for id in [24, 23, 27] {
        action(&bridge, "console", json!({"text":format!("give {id}")}));
        tick(&bridge, 8);
        let current = view(&bridge);
        if let Some(row) = current
            .items
            .iter()
            .find(|row| row.address.id == id && row.clothing_slot.is_some())
        {
            let slot = row.clothing_slot.unwrap();
            for intent in commands::dispatch(
                Intent::Wear {
                    from: row.address.cell(),
                    slot,
                },
                &current,
                &[],
            )
            .commands
            {
                if let UiIntent::Action { name, args } = intent {
                    action(&bridge, &name, args);
                }
            }
            tick(&bridge, 8);
        }
    }
    for id in if money { [1055, 1054] } else { [6, 95] } {
        action(&bridge, "console", json!({"text":format!("give {id}")}));
    }
    let snapshot = tick(&bridge, 20);
    let current = view(&bridge);
    let magazine = current
        .items
        .iter()
        .find(|r| {
            current
                .tile(r.address.cell())
                .is_some_and(|t| t.amount > 5 && if money { t.money } else { r.magazine })
        })
        .unwrap()
        .address
        .cell();
    let mut model = UiModel::default();
    model.adopt_snapshot(&snapshot);
    model.map_name = "PEI".into();
    let mut app = App::new();
    app.add_plugins(
        DefaultPlugins
            .set(AssetPlugin {
                file_path: config
                    .content_root
                    .canonicalize()
                    .unwrap()
                    .to_string_lossy()
                    .into(),
                ..default()
            })
            .set(WindowPlugin {
                primary_window: Some(Window {
                    title: "Original Inventory dashboard: accepted owner".into(),
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
    .insert_resource(config)
    .insert_resource(bridge)
    .insert_resource(crate::avatar::remotes::tests::source_catalog())
    .insert_resource(GameSnapshot(snapshot))
    .insert_resource(model)
    .insert_resource(UiState {
        screen: Screen::Inventory,
        ..default()
    })
    .init_resource::<WorldSessionEpoch>()
    .init_resource::<Settings>()
    .init_resource::<FrameInput>()
    .init_resource::<crate::ui::crafting::Menu>()
    .init_resource::<crate::viewmodel::lighting::NearbyLight>()
    .add_message::<UiIntent>()
    .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(20),
    ))
    .add_plugins((
        crate::content::ContentPlugin,
        crate::rig::RigPlugin,
        crate::character::CharacterPlugin,
    ))
    .add_plugins((
        crate::ui::backdrop::BackdropPlugin,
        crate::ui::swoop::SwoopPlugin,
        crate::ui::navbar::NavbarPlugin,
        crate::ui::vitals::VitalsPlugin,
        InventoryPlugin,
    ))
    .insert_resource(Capture {
        prefix: prefix.clone(),
        stage: 0,
        frames: 0,
        pending: false,
        done: default(),
        facts: vec![],
        magazine,
        money,
        sent: vec![],
    })
    .add_systems(Startup, scene)
    .add_systems(Update, accepted.before(controller::update))
    .add_systems(Last, capture);
    app.run();
    let facts: Vec<Value> =
        serde_json::from_slice(&std::fs::read(format!("{prefix}.json")).unwrap()).unwrap();
    assert_eq!(facts.len(), 4);
    assert!(facts[0]["portrait"].as_bool().unwrap());
    assert_eq!(facts[1]["selected"], true);
    let sent = facts[3]["sent"].as_array().unwrap();
    assert!(sent.iter().any(|r| r["name"] == "splitItem"));
    if money {
        assert_eq!(facts[0]["amount"], 150);
        assert_eq!(facts[2]["amount"], 150, "preview must not debit the wallet");
        assert_eq!(facts[2]["previewAmount"], 51);
        assert_eq!(facts[3]["wallets"], json!([99, 51]));
        assert_eq!(
            sent.len(),
            1,
            "one source preview drop, no optimistic second split"
        );
    } else {
        assert_eq!(
            facts[2]["amount"].as_u64().unwrap(),
            facts[0]["amount"].as_u64().unwrap() - 3
        );
        assert!(facts[3]["rounds"].as_u64().unwrap() > 0);
        assert!(sent.iter().any(|r| r["name"] == "magazineTransfer"));
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
        "selection panel must change actual pixels"
    );
    assert!(
        decode(2) != decode(3),
        "accepted split or magazine wheel must change actual pixels"
    );
    for stage in 0..4 {
        assert!(
            std::fs::metadata(format!("{prefix}-{stage}.png"))
                .unwrap()
                .len()
                > 20_000
        );
    }
}
fn accepted(world: &mut World) {
    let intents: Vec<_> = world.resource_mut::<Messages<UiIntent>>().drain().collect();
    let mut sent = vec![];
    let bridge = world.resource::<CoreBridge>();
    for intent in intents {
        if let UiIntent::Action { name, args } = intent {
            action(bridge, &name, args.clone());
            sent.push(json!({"name":name,"args":args}));
        }
    }
    let state = tick(bridge, 1);
    world.resource_mut::<Capture>().sent.extend(sent);
    world.resource_mut::<UiModel>().adopt_snapshot(&state);
    world.resource_mut::<GameSnapshot>().0 = state;
}
fn scene(mut commands: Commands) {
    commands.spawn((
        Camera2d,
        Camera {
            order: crate::ui::layers::HUD_ORDER,
            clear_color: ClearColorConfig::Custom(Color::srgb(0.12, 0.14, 0.17)),
            ..default()
        },
        IsDefaultUiCamera,
        Hdr,
        Tonemapping::None,
        DebandDither::Disabled,
    ));
    commands.insert_resource(GlobalAmbientLight {
        color: Color::srgb(0.55, 0.57, 0.6),
        brightness: crate::hdr::ambient_brightness(0.6),
        ..default()
    });
    commands.spawn((
        crate::environment::Sun,
        DirectionalLight {
            illuminance: crate::hdr::directional_illuminance(1.),
            ..default()
        },
        Transform::default(),
        RenderLayers::layer(0),
    ));
    for y in 0..25 {
        for x in 0..40 {
            commands.spawn((
                tiles::at(x as f32 * 40., y as f32 * 40., 40., 40.),
                GlobalZIndex(-1),
                BackgroundColor(if (x + y) % 2 == 0 {
                    Color::srgb(0.14, 0.31, 0.20)
                } else {
                    Color::srgb(0.08, 0.14, 0.21)
                }),
            ));
        }
    }
}
fn pointer(world: &mut World, point: Vec2, button: MouseButton, state: ButtonState) {
    let window = world
        .query_filtered::<Entity, With<PrimaryWindow>>()
        .single(world)
        .unwrap();
    let mut frame = world.resource_mut::<FrameInput>();
    frame.push(WindowEvent::CursorMoved(CursorMoved {
        window,
        position: point,
        delta: None,
    }));
    frame.push(WindowEvent::MouseButtonInput(MouseButtonInput {
        window,
        button,
        state,
    }));
}
fn point(world: &World, cell: Cell) -> Vec2 {
    let menu = world.resource::<Menu>();
    let grid = menu
        .hits
        .grids
        .iter()
        .find(|g| g.page == cell.page)
        .unwrap();
    grid.rect.min + Vec2::new(cell.x as f32, cell.y as f32) * CELL + Vec2::splat(18.)
}
fn split(world: &mut World, cell: Cell, amount: u16) {
    let (preview, to) = {
        let menu = world.resource::<Menu>();
        let grid = menu.hits.grids.iter().find(|g| g.page == 2).unwrap();
        let page = menu.pages.iter().find(|p| p.index == 2).unwrap();
        let item = at(&menu.pages, cell).unwrap();
        let size = footprint(item, 0);
        let dest = (0..page.height)
            .flat_map(|y| (0..page.width).map(move |x| Cell::at(2, x, y)))
            .find(|cell| {
                u32::from(cell.x) + size.x <= u32::from(page.width)
                    && u32::from(cell.y) + size.y <= u32::from(page.height)
                    && (0..size.y).all(|dy| {
                        (0..size.x).all(|dx| {
                            at(
                                &menu.pages,
                                Cell::at(2, cell.x + dx as u8, cell.y + dy as u8),
                            )
                            .is_none()
                        })
                    })
                    && grid.clip.contains(point(world, *cell))
            })
            .unwrap();
        (menu.hits.split_preview.unwrap().center(), dest)
    };
    world.resource_mut::<Menu>().gesture.split_amount = amount;
    pointer(world, preview, MouseButton::Left, ButtonState::Pressed);
    let drop = point(world, to);
    pointer(world, drop, MouseButton::Left, ButtonState::Released);
}

fn capture(world: &mut World) {
    world.resource_mut::<FrameInput>().begin();
    let mut c = world.remove_resource::<Capture>().unwrap();
    if c.pending {
        if !*c.done.lock().unwrap() {
            world.insert_resource(c);
            return;
        }
        *c.done.lock().unwrap() = false;
        c.pending = false;
        c.frames = 0;
        c.stage += 1;
        match c.stage {
            1 => {
                let p = point(world, c.magazine);
                pointer(world, p, MouseButton::Right, ButtonState::Pressed);
                pointer(world, p, MouseButton::Right, ButtonState::Released);
            }
            2 if c.money => {
                world.resource_mut::<Menu>().gesture.split_amount = 51;
            }
            2 => split(world, c.magazine, 3),
            3 if c.money => split(world, c.magazine, 51),
            3 => {
                let mut menu = world.resource_mut::<Menu>();
                let view = menu.view.clone();
                assert!(menu.magazines.unload(&view, c.magazine));
                menu.dirty();
            }
            _ => {
                std::fs::write(
                    format!("{}.json", c.prefix),
                    serde_json::to_vec_pretty(&c.facts).unwrap(),
                )
                .unwrap();
                world.write_message(AppExit::Success);
                world.insert_resource(c);
                return;
            }
        }
    }
    c.frames += 1;
    if c.frames == 40 {
        let menu = world.resource::<Menu>();
        let amount = menu.view.tile(c.magazine).unwrap().amount;
        let rounds = menu.magazines.operations.first().map_or(0, |o| o.done);
        let wallets: Vec<_> = menu
            .view
            .tiles
            .cells
            .iter()
            .filter(|c| c.tile.money)
            .map(|c| c.tile.amount)
            .collect();
        let facts = json!({"wallets":wallets,"previewAmount":menu.gesture.split_amount,"stage":c.stage,"amount":amount,"rounds":rounds,"selected":menu.gesture.selected.is_some(),"portrait":world.resource::<paperdoll::Portrait>().root.is_some(),"sourceViewport":world.resource::<paperdoll::stage::Surface>().size.to_array(),"sent":c.sent,"state":world.resource::<GameSnapshot>().0});
        c.facts.push(facts);
        let done = c.done.clone();
        world
            .spawn(Screenshot::primary_window())
            .observe(save_to_disk(format!("{}-{}.png", c.prefix, c.stage)))
            .observe(move |_: On<ScreenshotCaptured>| *done.lock().unwrap() = true);
        c.pending = true;
    }
    assert!(c.frames < 300, "Inventory screenshot timed out");
    world.insert_resource(c);
}
