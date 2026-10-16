//! Source tool rendering driven by injected ordered mouse edges. The retained
//! real local server accepts all mutations; this is not an OS-input claim.
use super::*;
use bevy::{
    app::AppExit,
    render::view::screenshot::{Screenshot, ScreenshotCaptured, save_to_disk},
    winit::WinitPlugin,
};

#[derive(Resource)]
struct Proof {
    source: Address,
    consumer: Address,
    points: [Vec3; 2],
    stage: usize,
    frame: usize,
    pending: bool,
    submitted: bool,
    prefix: String,
}
#[test]
#[ignore = "Vulkan/display; UG_WIRE_TOOL_SHOT sets capture prefix"]
fn render_source_ports_routing_accepted_connection_and_hold_clear() {
    let (mut cpu, _, consumer, source) = super::fixture();
    let points = [
        cpu.world().resource::<Frame>().endpoints[&source].position,
        cpu.world().resource::<Frame>().endpoints[&consumer].position,
    ];
    let bridge = cpu.world_mut().remove_resource::<CoreBridge>().unwrap();
    let snapshot = cpu.world_mut().remove_resource::<GameSnapshot>().unwrap();
    let catalog = cpu.world_mut().remove_resource::<ItemCatalog>().unwrap();
    let mut model = UiModel::default();
    model.adopt_snapshot(&snapshot.0);
    drop(cpu);
    let config = WorldConfig::default();
    let mut app = App::new();
    app.add_plugins(
        DefaultPlugins
            .set(WindowPlugin {
                primary_window: Some(Window {
                    title: "Original wire tool".into(),
                    resolution: (1100, 760).into(),
                    ..default()
                }),
                ..default()
            })
            .set(AssetPlugin {
                file_path: config
                    .content_root
                    .canonicalize()
                    .unwrap()
                    .to_string_lossy()
                    .into(),
                ..default()
            })
            .set(WinitPlugin {
                run_on_any_thread: true,
                ..default()
            }),
    )
    .insert_resource(config)
    .insert_resource(bridge)
    .insert_resource(snapshot)
    .insert_resource(catalog)
    .insert_resource(model)
    .insert_resource(UiState {
        screen: Screen::Playing,
        ..default()
    })
    .init_resource::<Settings>()
    .init_resource::<FrameInput>()
    .init_resource::<ContentCache>()
    .init_resource::<WorldGeometry>()
    .init_resource::<WorldSessionEpoch>()
    .insert_resource(TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(50),
    ))
    .insert_resource(GlobalAmbientLight {
        brightness: 200.,
        ..default()
    })
    .insert_resource(ClearColor(Color::srgb(0.24, 0.27, 0.30)))
    .insert_resource(Proof {
        source,
        consumer,
        points,
        stage: 0,
        frame: 0,
        pending: false,
        submitted: false,
        prefix: std::env::var("UG_WIRE_TOOL_SHOT").unwrap_or_else(|_| "/tmp/bevy-wire-tool".into()),
    })
    .add_plugins((
        RapierPhysicsPlugin::<NoUserData>::default(),
        DeployablePlugin,
    ))
    .add_systems(Startup, scene)
    .add_systems(
        Update,
        advance
            .before(crate::deployables::sync)
            .before(super::super::gather),
    )
    .add_systems(Last, capture);
    app.run();
}
fn scene(
    mut commands: Commands,
    proof: Res<Proof>,
    mut cursor: Query<&mut CursorOptions, With<PrimaryWindow>>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
) {
    let mut cursor = cursor.single_mut().unwrap();
    cursor.visible = false;
    cursor.grab_mode = CursorGrabMode::Locked;
    let point = proof.points[0];
    // Production HdrPlugin marks the player camera Hdr. The layer40 label
    // camera only shares (and so clears) the window's main texture when HDR and
    // MSAA match; an SDR fixture camera left its texture uncleared and
    // accumulated every previous label. Fixture lights stay SDR-tuned, so the
    // default tonemapper is kept; it does not affect target sharing.
    commands.spawn((
        Camera3d::default(),
        PlayerCamera,
        bevy::camera::Hdr,
        Projection::Perspective(PerspectiveProjection {
            fov: 70_f32.to_radians(),
            ..default()
        }),
        Transform::from_translation(point + Vec3::Z * 2.2).looking_at(point, Vec3::Y),
    ));
    commands.spawn((
        DirectionalLight {
            illuminance: 12000.,
            shadow_maps_enabled: true,
            ..default()
        },
        Transform::from_xyz(-4., 12., 4.).looking_at(Vec3::new(0., 5., -2.), Vec3::Y),
    ));
    commands.spawn((
        Mesh3d(meshes.add(Cuboid::new(10., 0.1, 10.))),
        MeshMaterial3d(materials.add(StandardMaterial {
            base_color: Color::srgb(0.5, 0.52, 0.54),
            ..default()
        })),
        Transform::from_xyz(0., 4.95, -2.),
    ));
}
fn advance(
    bridge: Res<CoreBridge>,
    mut snapshot: ResMut<GameSnapshot>,
    mut proof: ResMut<Proof>,
    mut events: ResMut<FrameInput>,
    windows: Query<Entity, With<PrimaryWindow>>,
    mut camera: Query<&mut Transform, With<PlayerCamera>>,
) {
    events.begin();
    snapshot.0 = bridge.request("tick", json!({})).unwrap()["state"].clone();
    if proof.pending || proof.submitted {
        return;
    }
    let window = windows.single().unwrap();
    let point = proof.points[usize::from(proof.stage >= 2)];
    *camera.single_mut().unwrap() =
        Transform::from_translation(point + Vec3::Z * 2.2).looking_at(point, Vec3::Y);
    let button = match proof.stage {
        1 | 3 => Some(MouseButton::Left),
        4 => Some(MouseButton::Right),
        _ => None,
    };
    if let Some(button) = button {
        events.push(WindowEvent::MouseButtonInput(MouseButtonInput {
            button,
            state: ButtonState::Pressed,
            window,
        }));
        if button == MouseButton::Left {
            events.push(WindowEvent::MouseButtonInput(MouseButtonInput {
                button,
                state: ButtonState::Released,
                window,
            }));
        }
    }
    proof.submitted = true;
}
fn capture(
    mut commands: Commands,
    mut proof: ResMut<Proof>,
    gesture: Res<tool::Gesture>,
    frame: Res<Frame>,
    graph: Res<wires::Accepted>,
    sockets: Query<(&Socket, &Visibility)>,
) {
    if proof.pending {
        return;
    }
    proof.frame += 1;
    let target = if proof.stage == 4 { 8 } else { 35 };
    if proof.frame < target {
        return;
    }
    let graph = graph.graph.as_ref().unwrap();
    assert!(frame.active, "source tool must own captured on-foot input");
    assert_eq!(
        frame.hovered,
        Some(if proof.stage < 2 {
            proof.source
        } else {
            proof.consumer
        })
    );
    assert_eq!(
        sockets
            .iter()
            .filter(|(_, v)| **v != Visibility::Hidden)
            .count(),
        5,
        "all ordinary source power cubes show with tool"
    );
    match proof.stage {
        0 => assert!(gesture.start.is_none()),
        1 | 2 => assert_eq!(gesture.start, Some(proof.source)),
        3 | 4 => {
            assert!(gesture.start.is_none());
            assert_eq!(graph.wires.len(), 1);
        }
        5 => assert!(
            graph.wires.is_empty(),
            "full hold must receive accepted removal"
        ),
        _ => unreachable!(),
    }
    let name = [
        "focus", "route", "target", "accepted", "clearing", "removed",
    ][proof.stage];
    std::fs::write(format!("{}-{name}.json",proof.prefix),serde_json::to_string_pretty(&json!({
        "stage":name,"hud":gesture.readout(),"wireCount":graph.wires.len(),"hoveredDevice":frame.hovered.map(|a|a.device),
        "sourceId":proof.source.device,"consumerId":proof.consumer.device,
        "input":"injected ordered mouse edges; real local server acceptance"
    })).unwrap()).unwrap();
    proof.pending = true;
    commands
        .spawn(Screenshot::primary_window())
        .observe(save_to_disk(format!("{}-{name}.png", proof.prefix)))
        .observe(
            |_captured: On<ScreenshotCaptured>,
             mut proof: ResMut<Proof>,
             mut exit: MessageWriter<AppExit>| {
                proof.stage += 1;
                proof.frame = 0;
                proof.pending = false;
                proof.submitted = false;
                if proof.stage == 6 {
                    exit.write(AppExit::Success);
                }
            },
        );
}
