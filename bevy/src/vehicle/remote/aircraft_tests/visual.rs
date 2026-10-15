//! Actual UDP flags consumed by the production native replica/clock/art/audio
//! path. A trusted fixture host holds the aircraft still and switches its engine;
//! it does not represent two native players flying the aircraft.
use super::*;
use crate::bridge::CoreBridge;
use bevy::{
    app::AppExit,
    core_pipeline::tonemapping::Tonemapping,
    render::view::screenshot::{Screenshot, ScreenshotCaptured, save_to_disk},
    winit::WinitPlugin,
};
use std::{net::UdpSocket, path::Path};

#[derive(Resource)]
struct Journey {
    host: CoreBridge,
    peer: CoreBridge,
    id: u32,
    frame: u32,
    path: String,
}
impl Journey {
    fn new(key: &str, path: String) -> Self {
        let socket = UdpSocket::bind("127.0.0.1:0").unwrap();
        let port = socket.local_addr().unwrap().port();
        drop(socket);
        let root = Path::new(env!("CARGO_MANIFEST_DIR"));
        let host = CoreBridge::launch(root).unwrap();
        host.request(
            "init",
            json!({"contentRoot":WorldConfig::default().content_root,
            "listenPort":port,"spawn":[20,0,20]}),
        )
        .unwrap();
        let peer = CoreBridge::launch(root).unwrap();
        peer.request(
            "init",
            json!({"contentRoot":WorldConfig::default().content_root,
            "connectHost":"127.0.0.1","connectPort":port,"name":"Aircraft clock observer"}),
        )
        .unwrap();
        let definition = data::named(key).unwrap();
        let registration = crate::vehicle::registration(
            definition,
            "remote-clock-fixture".into(),
            Vec3::ZERO,
            0.,
            0,
        );
        let id = host
            .request("worldVehicles", json!({"vehicles":[registration]}))
            .unwrap()["result"]["vehicles"][0]["netId"]
            .as_u64()
            .unwrap() as u32;
        Self {
            host,
            peer,
            id,
            frame: 0,
            path,
        }
    }
    fn engine(&self, running: bool) {
        self.host.request("worldVehicleState", json!({"vehicles":[{"netId":self.id,
            "position":[0,0,0],"eulerDegrees":[0,0,0],"linVel":[0,0,0],"angVel":[0,0,0],"flags":u8::from(running)}]})).unwrap();
    }
    fn step(&self) -> serde_json::Value {
        self.host.request("tick", json!({})).unwrap();
        self.peer.request("tick", json!({})).unwrap()["state"].clone()
    }
}

#[test]
fn actual_udp_flags_start_stop_and_remove_the_native_puppet_and_its_loop() {
    let journey = Journey::new("huey", String::new());
    let (mut app, entity) = super::app("huey");
    let spec = &data::named("huey").unwrap().spec;
    app.world_mut()
        .entity_mut(entity)
        .insert(contacts::ContactState::new(spec));
    app.world_mut()
        .get_mut::<VehicleBody>(entity)
        .unwrap()
        .net_id = journey.id;
    app.world_mut()
        .get_mut::<Transform>(entity)
        .unwrap()
        .translation = Vec3::ZERO;
    let mut vehicles = Vehicles::default();
    vehicles.entities.insert(journey.id, entity);
    let mut model = UiModel::default();
    model.connected = true;
    app.insert_resource(vehicles)
        .insert_resource(model)
        .add_message::<crate::ui::UiIntent>()
        .add_systems(Update, (sync_vehicles, update).chain());
    let step = |app: &mut App, n: usize| {
        for _ in 0..n {
            app.world_mut().resource_mut::<GameSnapshot>().0 = journey.step();
            app.update();
        }
    };
    // Wait for the real peer to have the vehicle before the native diff pass;
    // otherwise its legitimate initial empty registry would retire our fixture.
    let mut state = serde_json::Value::Null;
    for _ in 0..30 {
        state = journey.step();
    }
    assert_eq!(state["connected"], true);
    assert_eq!(state["network"]["ownsWorld"], false);
    app.world_mut().resource_mut::<GameSnapshot>().0 = state;
    app.update();
    assert_eq!(
        app.world().get::<VehicleBody>(entity).unwrap().flight.spool,
        0.
    );
    journey.engine(true);
    step(&mut app, 100);
    let body = app.world().get::<VehicleBody>(entity).unwrap();
    assert_eq!(body.flags & 1, 1);
    assert!(body.flight.spool > 0.1);
    assert_eq!(body.flight.ignition_starts, 1);
    assert!(loop_sound(&mut app, entity).is_some());
    assert_eq!(
        app.world().get::<Transform>(entity).unwrap().translation,
        Vec3::ZERO
    );
    journey.engine(false);
    step(&mut app, 300);
    assert_eq!(
        app.world().get::<VehicleBody>(entity).unwrap().flight.spool,
        0.
    );
    assert!(loop_sound(&mut app, entity).is_none());
    journey
        .host
        .request("worldVehicles", json!({"vehicles":[]}))
        .unwrap();
    step(&mut app, 20);
    assert!(app.world().get_entity(entity).is_err());
    assert!(
        app.world_mut()
            .query::<&audio::VehicleSound>()
            .iter(app.world())
            .next()
            .is_none()
    );
}

#[test]
#[ignore = "requires Vulkan/display and frozen bridge; UG_REMOTE_AIRCRAFT=huey|otter UG_REMOTE_AIRCRAFT_SHOT=path-prefix"]
fn render_udp_aircraft_start_run_and_stop() {
    let key = if std::env::var("UG_REMOTE_AIRCRAFT").as_deref() == Ok("otter") {
        "otter"
    } else {
        "huey"
    };
    let path = std::env::var("UG_REMOTE_AIRCRAFT_SHOT")
        .unwrap_or_else(|_| format!("/tmp/bevy-remote-{key}"));
    let config = WorldConfig::default();
    let journey = Journey::new(key, path);
    let mut app = App::new();
    app.add_plugins(
        DefaultPlugins
            .set(AssetPlugin {
                file_path: config
                    .content_root
                    .canonicalize()
                    .unwrap()
                    .to_string_lossy()
                    .into_owned(),
                ..default()
            })
            .set(WindowPlugin {
                primary_window: Some(Window {
                    title: format!("Actual UDP {key} clock"),
                    resolution: (1000, 700).into(),
                    ..default()
                }),
                ..default()
            })
            .set(WinitPlugin {
                run_on_any_thread: true,
                ..default()
            }),
    )
    .add_plugins(RapierPhysicsPlugin::<NoUserData>::default())
    .init_resource::<ContentCache>()
    .init_resource::<Vehicles>()
    .init_resource::<Settings>()
    .init_resource::<audio::AudioVoices>()
    .insert_resource(config)
    .insert_resource(journey)
    .insert_resource(GameSnapshot(json!({"network":{"ownsWorld":false}})))
    .insert_resource(UiState {
        screen: crate::ui::Screen::Playing,
        networked: true,
        ..default()
    })
    .insert_resource(WorldState {
        ready: true,
        ..default()
    })
    .insert_resource(TimestepMode::Fixed {
        dt: 0.02,
        substeps: 1,
    })
    .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(20),
    ))
    .insert_resource(ClearColor(Color::srgb(0.24, 0.27, 0.30)))
    .add_message::<crate::ui::UiIntent>()
    .add_systems(First, advance)
    .add_systems(Update, (sync_vehicles, update).chain())
    .add_systems(
        PostUpdate,
        (step_bodies, render::aircraft::animate, audio::update)
            .chain()
            .before(PhysicsSet::SyncBackend)
            .before(TransformSystems::Propagate),
    )
    .add_systems(PostUpdate, capture.after(TransformSystems::Propagate));
    let mut model = UiModel::default();
    model.connected = true;
    app.insert_resource(model);
    app.world_mut().spawn((
        Camera3d::default(),
        Tonemapping::TonyMcMapface,
        Transform::from_xyz(13., 8., -14.).looking_at(Vec3::Y * 0.8, Vec3::Y),
    ));
    app.world_mut().spawn((
        DirectionalLight {
            illuminance: 9000.,
            shadow_maps_enabled: true,
            ..default()
        },
        Transform::from_rotation(Quat::from_euler(EulerRot::XYZ, -0.8, -0.6, 0.)),
    ));
    app.insert_resource(GlobalAmbientLight {
        color: Color::WHITE,
        brightness: 300.,
        ..default()
    });
    app.world_mut()
        .run_system_once(
            |mut commands: Commands,
             mut meshes: ResMut<Assets<Mesh>>,
             mut materials: ResMut<Assets<StandardMaterial>>| {
                commands.spawn((
                    Mesh3d(meshes.add(Plane3d::default().mesh().size(60., 60.))),
                    MeshMaterial3d(materials.add(StandardMaterial {
                        base_color: Color::srgb(0.23, 0.25, 0.20),
                        ..default()
                    })),
                    Transform::from_xyz(0., -1.2, 0.),
                ));
            },
        )
        .unwrap();
    app.run();
}

fn advance(mut journey: ResMut<Journey>, mut snapshot: ResMut<GameSnapshot>) {
    journey.frame += 1;
    if [70, 200].contains(&journey.frame) {
        journey.engine(journey.frame == 70);
    }
    snapshot.0 = journey.step();
}

fn capture(
    mut commands: Commands,
    journey: Res<Journey>,
    snapshot: Res<GameSnapshot>,
    bodies: Query<(Entity, &VehicleBody, &Transform, &RigidBody, &ExternalForce)>,
    sounds: Query<(&ChildOf, &AudioPlayer, &PlaybackSettings)>,
) {
    let label = match journey.frame {
        50 => "off",
        180 => "running",
        // Planes reach higher RPM during the same startup interval. Wait past
        // the source's full 5.5s rundown, not the Huey's partial-spool stop time.
        500 => "stopped",
        _ => return,
    };
    assert_eq!(snapshot.0["connected"], true);
    assert_eq!(snapshot.0["network"]["ownsWorld"], false);
    let (entity, body, pose, rigid, force) = bodies
        .iter()
        .find(|(_, b, _, _, _)| b.net_id == journey.id)
        .unwrap();
    assert_eq!(*rigid, RigidBody::KinematicPositionBased);
    assert_eq!(pose.translation, Vec3::ZERO);
    assert_eq!(force.force, Vec3::ZERO);
    let loops: Vec<_> = sounds.iter().filter(|(p, _, s)| p.parent() == entity && matches!(s.mode, bevy::audio::PlaybackMode::Loop))
        .map(|(_, source, settings)| json!({"path":source.0.path().unwrap().path().to_string_lossy(),"pitch":settings.speed})).collect();
    assert_eq!(loops.len(), usize::from(label == "running"));
    if label == "running" {
        assert!(body.flight.spool > 0.1);
    } else {
        assert_eq!(body.flight.spool, 0.);
    }
    let path = format!("{}-{label}.png", journey.path);
    let record = json!({"fixture":"trusted stationary host switches engine; actual UDP replica drives only native presentation",
        "remoteState":snapshot.0,"frame":journey.frame,"spool":body.flight.spool,
        "mainPhase":body.flight.main_phase,"tailPhase":body.flight.tail_phase,"propPhase":body.flight.prop_phase,
        "ignitionStarts":body.flight.ignition_starts,"position":pose.translation.to_array(),"audioLoops":loops});
    std::fs::write(
        Path::new(&path).with_extension("json"),
        serde_json::to_string_pretty(&record).unwrap(),
    )
    .unwrap();
    let done = label == "stopped";
    commands
        .spawn(Screenshot::primary_window())
        .observe(save_to_disk(path))
        .observe(
            move |_: On<ScreenshotCaptured>, mut exit: MessageWriter<AppExit>| {
                if done {
                    exit.write(AppExit::Success);
                }
            },
        );
}
