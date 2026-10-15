//! Original bus model/material with explicit presentation rows. This exercises
//! geometry/hinges only; actual command/UDP authority has a separate journey.
use super::*;
use crate::content::ContentCache;
use bevy::{
    app::AppExit,
    render::view::screenshot::{Screenshot, ScreenshotCaptured, save_to_disk},
    winit::WinitPlugin,
};

#[derive(Resource)]
struct Capture {
    frame: u32,
    completed: u32,
    body: Entity,
    camera: Entity,
    journey: Option<accepted::Journey>,
}
#[test]
#[ignore = "Vulkan/display; UG_BIFOLD_SHOT=output prefix"]
fn render_original_bus_closed_half_open_and_inside_step() {
    run(false);
}
#[test]
#[ignore = "Vulkan/display; actual UDP host/peer and native key consumer"]
fn render_actual_udp_driver_toggle_closed_half_open_and_reclosed() {
    run(true);
}
fn run(accepted: bool) {
    // Pump the actual UDP owner before Winit initialization; no trusted phase API.
    let journey = accepted.then(accepted::Journey::new);
    let initial = journey
        .as_ref()
        .map(|j| j.pump(1))
        .unwrap_or(serde_json::json!({}));
    let mut app = App::new();
    app.add_plugins(
        DefaultPlugins
            .set(AssetPlugin {
                file_path: WorldConfig::default()
                    .content_root
                    .canonicalize()
                    .unwrap()
                    .to_string_lossy()
                    .into_owned(),
                ..default()
            })
            .set(WindowPlugin {
                primary_window: Some(Window {
                    resolution: (1100, 760).into(),
                    title: "Original bus folding door geometry".into(),
                    ..default()
                }),
                ..default()
            })
            .set(WinitPlugin {
                run_on_any_thread: true,
                ..default()
            }),
    )
    .init_resource::<ContentCache>()
    .insert_resource(GameSnapshot(initial))
    .init_resource::<crate::settings::Settings>()
    .insert_resource(crate::ui::UiState {
        screen: crate::ui::Screen::Playing,
        networked: true,
        ..default()
    })
    .add_message::<crate::ui::UiIntent>()
    .insert_resource(ClearColor(Color::srgb(0.12, 0.15, 0.18)))
    .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(20),
    ))
    .add_systems(Update, adopt);
    let body = bus(&mut app, Transform::IDENTITY);
    if let Some(j) = &journey {
        app.world_mut().get_mut::<VehicleBody>(body).unwrap().net_id = j.id;
    }
    let camera = app
        .world_mut()
        .spawn((
            Camera3d::default(),
            bevy::core_pipeline::tonemapping::Tonemapping::None,
            bevy::core_pipeline::tonemapping::DebandDither::Disabled,
            Projection::Perspective(PerspectiveProjection {
                fov: 45_f32.to_radians(),
                ..default()
            }),
            Transform::from_xyz(5.8, 2.8, -4.6).looking_at(Vec3::new(1., 0.85, -2.75), Vec3::Y),
        ))
        .id();
    app.world_mut().spawn((
        DirectionalLight {
            illuminance: 3500.,
            ..default()
        },
        Transform::from_xyz(4., 8., -7.).looking_at(Vec3::ZERO, Vec3::Y),
    ));
    app.insert_resource(GlobalAmbientLight {
        brightness: 180.,
        ..default()
    })
    .insert_resource(Capture {
        frame: 0,
        completed: 0,
        body,
        camera,
        journey,
    })
    .add_systems(First, advance)
    .add_systems(PostUpdate, capture.after(TransformSystems::Propagate));
    app.run();
}
fn advance(world: &mut World) {
    let mut state = world.remove_resource::<Capture>().unwrap();
    state.frame += 1;
    if let Some(journey) = &state.journey {
        if matches!(state.frame, 38 | 100) {
            world
                .resource_mut::<ButtonInput<KeyCode>>()
                .press(KeyCode::ControlLeft);
            world.run_system_once(input::input).unwrap();
            let intents = world
                .resource_mut::<Messages<crate::ui::UiIntent>>()
                .drain()
                .collect::<Vec<_>>();
            assert_eq!(
                intents.len(),
                1,
                "actual native driver key must request exactly one toggle"
            );
            for intent in intents {
                if let crate::ui::UiIntent::Action { name, args } = intent {
                    journey
                        .peer
                        .request("action", serde_json::json!({"name":name,"args":args}))
                        .unwrap();
                }
            }
            world
                .resource_mut::<ButtonInput<KeyCode>>()
                .release(KeyCode::ControlLeft);
        }
        world.resource_mut::<GameSnapshot>().0 = journey.pump(1);
    } else {
        let phase = if state.frame < 40 {
            0.
        } else if state.frame < 70 {
            0.5
        } else {
            1.
        };
        world.resource_mut::<GameSnapshot>().0 =
            serde_json::json!({"vehicles":[{"netId":1,"doorOpen":phase>0.,"doorFold":phase}]});
        if state.frame == 100 {
            *world.get_mut::<Transform>(state.camera).unwrap() =
                Transform::from_xyz(-0.1, 1.2, -1.2).looking_at(Vec3::new(1.1, 0.2, -2.8), Vec3::Y);
        }
    }
    assert!(state.frame < 190, "bus capture timeout");
    world.insert_resource(state);
}
fn capture(
    mut commands: Commands,
    state: Res<Capture>,
    doors: Query<&Built>,
    poses: Query<&GlobalTransform>,
    snapshot: Res<GameSnapshot>,
    mut exit: MessageWriter<AppExit>,
) {
    if state.completed == 4 {
        exit.write(AppExit::Success);
        return;
    }
    let label = if state.journey.is_some() {
        match state.frame {
            30 => "closed",
            55 => "half",
            85 => "open",
            145 => "reclosed",
            _ => return,
        }
    } else {
        match state.frame {
            30 => "closed",
            60 => "half",
            90 => "open",
            120 => "inside",
            _ => return,
        }
    };
    let door = doors.get(state.body).unwrap();
    if state.journey.is_some() {
        assert_eq!(snapshot.0["network"]["ownsWorld"], false);
        match label {
            "closed" | "reclosed" => assert_eq!(door.phase, 0.),
            "open" => assert_eq!(door.phase, 1.),
            "half" => assert!(door.phase > 0.1 && door.phase < 0.9),
            _ => {}
        }
    }
    let prefix = std::env::var("UG_BIFOLD_SHOT").unwrap_or_else(|_| "/tmp/bevy-bus-door".into());
    std::fs::write(format!("{prefix}-{label}.json"),serde_json::to_vec_pretty(&serde_json::json!({
        "fixture":if state.journey.is_some(){"actual UDP host/remote; scripted native VehicleDoor key consumer, accepted phase only; not a fullgame input journey"}else{"presentation-only original bus model, source cut/cap/pocket and nested hinge transforms; no command authority claim"},
        "player":snapshot.0["player"],"vehicles":snapshot.0["vehicles"],"network":snapshot.0["network"],
        "phase":door.phase,"open":door.open,
        "hingeA":poses.get(door.a).unwrap().to_matrix().to_cols_array(),
        "hingeB":poses.get(door.b.unwrap()).unwrap().to_matrix().to_cols_array()
    })).unwrap()).unwrap();
    commands
        .spawn(Screenshot::primary_window())
        .observe(save_to_disk(format!("{prefix}-{label}.png")))
        .observe(|_: On<ScreenshotCaptured>, mut state: ResMut<Capture>| state.completed += 1);
}
