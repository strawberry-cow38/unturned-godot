//! Vulkan fixture: production vitals/status/feedback HUD and the deadzone grain
//! over a scene canvas, plus an actual bridge drink round trip. Stages 0-4 and 8
//! use injected owner replicas (status bits, comfort, dose, events); stages 5-7
//! read the real host's accepted snapshot after the same fluid_hand request the
//! simulate system makes (injected click, accepted result).
use crate::{
    bridge::CoreBridge,
    entities::{ItemCatalog, ItemVisual},
    player::GameSnapshot,
    ui::{UiModel, UiState, layers::LayersPlugin},
    world::WorldConfig,
};
use bevy::{
    app::AppExit,
    prelude::*,
    render::view::screenshot::{Screenshot, ScreenshotCaptured, save_to_disk},
    winit::WinitPlugin,
};
use serde_json::{Value, json};
use std::sync::{Arc, Mutex};

#[derive(Resource)]
struct Capture {
    prefix: String,
    stage: usize,
    frames: u32,
    requested: bool,
    done: Arc<Mutex<bool>>,
    facts: Vec<Value>,
}
#[derive(Resource)]
struct Host(CoreBridge);

fn tick(host: &CoreBridge, n: usize) -> Value {
    let mut state = Value::Null;
    for _ in 0..n {
        state = host.request("tick", json!({})).unwrap()["state"].clone();
    }
    state
}

#[test]
#[ignore = "requires Vulkan/display and the built bridge; UG_SURVIVAL_SHOT sets PNG prefix"]
fn gpu_status_temperature_deadzone_and_drink_round_trip() {
    let prefix = std::env::var("UG_SURVIVAL_SHOT").unwrap_or_else(|_| "/tmp/bevy-survival".into());
    let config = WorldConfig::default();
    let host = CoreBridge::launch(std::path::Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
    host.request("init", json!({"contentRoot":config.content_root,"starterKit":true,"allowCheats":true,"spawn":[0,0,0]}))
        .unwrap();
    let items: Vec<ItemVisual> =
        serde_json::from_value(host.request("catalog", json!({})).unwrap()["result"]["items"].clone()).unwrap();
    let mut catalog = ItemCatalog::default();
    catalog.items = items.into_iter().map(|i| (i.id, i)).collect();
    let mut model = UiModel::default();
    let snapshot = tick(&host, 4);
    model.adopt_snapshot(&snapshot);
    let mut app = App::new();
    app.add_plugins(
        DefaultPlugins
            .set(AssetPlugin {
                file_path: config.content_root.to_string_lossy().into_owned(),
                ..default()
            })
            .set(WindowPlugin {
                primary_window: Some(Window {
                    resolution: (1280, 720).into(),
                    title: "Original survival feedback".into(),
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
        crate::ui::backdrop::BackdropPlugin,
        crate::hdr::HdrPlugin,
        crate::nightvision::NightVisionPlugin,
        crate::ui::vitals::VitalsPlugin,
        crate::ui::feedback::FeedbackPlugin,
        super::deadzone::DeadzonePlugin,
    ))
    .configure_sets(Update, crate::ui::UiUpdate)
    .insert_resource(model)
    .insert_resource(catalog)
    .insert_resource(GameSnapshot(snapshot))
    .insert_resource(Host(host))
    .init_resource::<crate::viewmodel::lights::Switches>()
    .insert_resource(UiState {
        screen: crate::ui::Screen::Playing,
        ..default()
    })
    .insert_resource(Capture {
        prefix: prefix.clone(),
        stage: 0,
        frames: 0,
        requested: false,
        done: default(),
        facts: vec![],
    })
    .add_systems(Startup, scene)
    .add_systems(Update, stage_state.before(crate::ui::UiUpdate))
    .add_systems(Last, capture);
    app.run();
    let facts: Vec<Value> =
        serde_json::from_slice(&std::fs::read(format!("{prefix}.json")).unwrap()).unwrap();
    // Accepted round trip: the click's toast came from a clone preview, the bottle
    // and the water bar changed only through the host's echo.
    assert!(facts[5]["line"].as_str().unwrap().ends_with("[LMB] drink"));
    assert!(facts[6]["line"].as_str().unwrap().starts_with("drank 1.0 L"));
    assert!(facts[7]["line"].as_str().unwrap().starts_with("Bottled Water -- empty (1.0 L)"));
    // A fresh host owner is already at full water; the raise itself is the
    // managed HeldBottleDrinksAndFills test. Water never drops on a drink.
    assert!(facts[7]["water"].as_f64().unwrap() >= facts[5]["water"].as_f64().unwrap());
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
            clear_color: ClearColorConfig::Custom(Color::srgb(0.45, 0.66, 0.86)),
            ..default()
        },
        Transform::from_xyz(0., 1.7, 6.).looking_at(Vec3::new(0., 0.8, 0.), Vec3::Y),
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
    let lit = |c: Color| StandardMaterial {
        base_color: c,
        unlit: true,
        ..default()
    };
    commands.spawn((
        Mesh3d(meshes.add(Plane3d::default().mesh().size(60., 60.))),
        MeshMaterial3d(materials.add(lit(Color::srgb(0.25, 0.55, 0.2)))),
    ));
    for (i, color) in [
        Color::srgb(0.85, 0.15, 0.1),
        Color::srgb(0.95, 0.75, 0.1),
        Color::srgb(0.15, 0.35, 0.9),
    ]
    .into_iter()
    .enumerate()
    {
        commands.spawn((
            Mesh3d(meshes.add(Cuboid::new(1.2, 1.6, 1.2))),
            MeshMaterial3d(materials.add(lit(color))),
            Transform::from_xyz(i as f32 * 2. - 2., 0.8, -1.),
        ));
    }
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
}

/// Keeps the accepted host snapshot flowing for the real stages.
fn stage_state(
    capture: Res<Capture>,
    host: Res<Host>,
    mut model: ResMut<UiModel>,
    mut snapshot: ResMut<GameSnapshot>,
) {
    if (5..=7).contains(&capture.stage) {
        let state = tick(&host.0, 1);
        let feedback = model.feedback.clone();
        model.adopt_snapshot(&state);
        model.feedback = feedback;
        snapshot.0 = state;
    }
    // Software rendering is slow: hold the timed lines at their raise so the
    // capture shows them (the timers themselves are covered by unit tests).
    if capture.stage == 6 && !model.feedback.fluid.text.is_empty() {
        model.feedback.fluid.left = crate::ui::feedback::FLUID_TOAST_SECONDS;
    }
    if capture.stage == 8 {
        model.feedback.alert.left = 2.;
        model.feedback.toast.left = 2.5;
    }
}

fn capture(world: &mut World) {
    let mut capture = world.remove_resource::<Capture>().unwrap();
    capture.frames += 1;
    assert!(capture.frames < 400);
    if capture.frames > 40 && !capture.requested {
        let line = {
            let snapshot = world.resource::<GameSnapshot>();
            let catalog = world.resource::<ItemCatalog>();
            crate::ui::feedback::fluid_line(&snapshot.0, catalog, &world.resource::<UiModel>().feedback)
        };
        let model = world.resource::<UiModel>();
        capture.facts.push(json!({"stage":capture.stage,"line":line,"water":model.water,
            "comfort":model.temperature.comfort,"radiation":model.radiation,
            "status":[model.bleeding,model.broken,model.food<=0.,model.water<=0.,model.in_deadzone],
            "alert":model.feedback.alert.text,"toast":model.feedback.toast.text}));
        let done = capture.done.clone();
        world
            .spawn(Screenshot::primary_window())
            .observe(save_to_disk(format!("{}-{}.png", capture.prefix, capture.stage)))
            .observe(move |_: On<ScreenshotCaptured>| {
                *done.lock().unwrap() = true;
            });
        capture.requested = true;
    }
    if *capture.done.lock().unwrap() {
        if capture.stage == 8 {
            std::fs::write(
                format!("{}.json", capture.prefix),
                serde_json::to_vec_pretty(&capture.facts).unwrap(),
            )
            .unwrap();
            world.write_message(AppExit::Success);
        } else {
            capture.stage += 1;
            advance(world, capture.stage);
            capture.frames = 0;
            capture.requested = false;
            capture.done = default();
        }
    }
    world.insert_resource(capture);
}

fn advance(world: &mut World, stage: usize) {
    let mut model = world.resource_mut::<UiModel>();
    match stage {
        // Cold, every source status condition at once, thermometer carried.
        1 => {
            model.temperature.comfort = -0.6;
            model.temperature.body_c = 1.;
            model.temperature.has_thermometer = true;
            model.bleeding = true;
            model.broken = true;
            model.food = 0.;
            model.water = 0.;
            model.in_deadzone = true;
        }
        // Hot, statuses clear again.
        2 => {
            model.temperature.comfort = 0.8;
            model.temperature.body_c = 37.6;
            model.bleeding = false;
            model.broken = false;
            model.food = 0.8;
            model.water = 0.7;
            model.in_deadzone = false;
        }
        // Deadzone grain from the dose: tame, then full.
        3 => {
            model.temperature.comfort = 0.;
            model.temperature.body_c = 19.;
            model.in_deadzone = true;
            model.radiation = 0.45;
        }
        4 => model.radiation = 1.,
        // Real host: hold the starter bottle through the accepted selection.
        5 => {
            model.radiation = 0.;
            model.in_deadzone = false;
            drop(model);
            let host = &world.resource::<Host>().0;
            let state = tick(host, 2);
            let bottle = state["inventory"]["pages"]
                .as_array()
                .unwrap()
                .iter()
                .find_map(|p| {
                    p["items"].as_array().unwrap().iter().find(|i| i["id"] == 14).map(|i| (p["index"].clone(), i["x"].clone(), i["y"].clone()))
                })
                .unwrap();
            host.request("action", json!({"name":"holdItem","args":{"page":bottle.0,"x":bottle.1,"y":bottle.2}}))
                .unwrap();
            tick(host, 20);
        }
        // The click: the same request simulate makes, then the accepted echo.
        6 => {
            drop(model);
            let snapshot = world.resource::<GameSnapshot>().0.clone();
            let held = world.resource::<ItemCatalog>().items.get(&14).cloned();
            let host_feedback = {
                let host = &world.resource::<Host>().0;
                let mut feedback = world.resource::<UiModel>().feedback.clone();
                let started = super::fluid_hand(host, &snapshot, held.as_ref(), true, false, &mut feedback).unwrap();
                assert_eq!(started, Some(14), "accepted request plays the drink clip");
                feedback
            };
            world.resource_mut::<UiModel>().feedback = host_feedback;
        }
        // After the toast: the accepted empty bottle and raised water.
        7 => {
            model.feedback.fluid = default();
        }
        8 => {
            model.feedback.adopt(&json!({"kind":"vehicleExitRefused","data":{"netId":1,"seat":0}}));
            model.feedback.adopt(&json!({"kind":"pickupDenied","data":{"netId":2}}));
        }
        _ => {}
    }
}
