//! Accepted source switch state through the real UDP owner and production
//! native focus/release systems. Input edges are injected, not an OS claim.
use super::*;
use bevy::{
    app::AppExit,
    input::keyboard::{Key, KeyboardInput},
    render::view::screenshot::{Screenshot, ScreenshotCaptured, save_to_disk},
    time::TimeUpdateStrategy,
    winit::WinitPlugin,
};
use std::path::Path;

#[derive(Resource)]
struct Host(CoreBridge);
#[derive(Resource)]
struct Proof {
    id: u32,
    stage: usize,
    frame: usize,
    pending: bool,
    records: Vec<Value>,
}

#[test]
#[ignore = "Vulkan/display; UG_SWITCH_SHOT sets capture prefix"]
fn udp_source_switch_taps_update_native_indicator_and_prompt_after_acceptance() {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"));
    let config = WorldConfig::default();
    let port = std::net::UdpSocket::bind("127.0.0.1:0")
        .unwrap()
        .local_addr()
        .unwrap()
        .port();
    let host = CoreBridge::launch(root).unwrap();
    host.request(
        "init",
        json!({"contentRoot":config.content_root,"listenPort":port,
        "spawn":[0,5,0],"allowCheats":true}),
    )
    .unwrap();
    let peer = CoreBridge::launch(root).unwrap();
    let mut state = peer
        .request(
            "init",
            json!({"contentRoot":config.content_root,
        "connectHost":"127.0.0.1","connectPort":port}),
        )
        .unwrap()["state"]
        .clone();
    for step in 0..70 {
        if step == 24 {
            peer.request(
                "action",
                json!({"name":"console","args":{"text":"give 9105"}}),
            )
            .unwrap();
        }
        if step == 44 {
            let p = crate::player::vector(&state["player"]["position"]).unwrap() - Vec3::Z * 2.;
            peer.request(
                "action",
                json!({"name":"placeDeployable","args":{
                "defId":9105,"pos":p.to_array(),"yawDegrees":0}}),
            )
            .unwrap();
        }
        state = peer.request("tick", json!({})).unwrap()["state"].clone();
        host.request("tick", json!({})).unwrap();
        std::thread::sleep(std::time::Duration::from_millis(1));
    }
    let id = state["deployables"]
        .as_array()
        .unwrap()
        .iter()
        .find(|r| r["defId"] == 9105)
        .expect("real accepted source switch")["netId"]
        .as_u64()
        .unwrap() as u32;
    assert_eq!(state["deployables"][0]["toggledOn"], true);
    let mut catalog = ItemCatalog::default();
    catalog.deployables = serde_json::from_value(
        peer.request("catalog", json!({})).unwrap()["result"]["deployables"].clone(),
    )
    .unwrap();
    let mut model = UiModel::default();
    model.adopt_snapshot(&state);
    let mut app = App::new();
    app.add_plugins(
        DefaultPlugins
            .set(WindowPlugin {
                primary_window: Some(Window {
                    title: "Accepted source power switch".into(),
                    resolution: (1000, 760).into(),
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
    .insert_resource(peer)
    .insert_resource(Host(host))
    .insert_resource(GameSnapshot(state))
    .insert_resource(catalog)
    .insert_resource(model)
    .insert_resource(UiState {
        screen: crate::ui::Screen::Playing,
        ..default()
    })
    .init_resource::<Settings>()
    .init_resource::<FrameInput>()
    .init_resource::<InteractionFocus>()
    .init_resource::<ContentCache>()
    .init_resource::<WorldGeometry>()
    .init_resource::<WorldSessionEpoch>()
    .insert_resource(Proof {
        id,
        stage: 0,
        frame: 0,
        pending: false,
        records: vec![],
    })
    .insert_resource(TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(50),
    ))
    .add_plugins((
        RapierPhysicsPlugin::<NoUserData>::default(),
        crate::info_billboard::InfoBillboardPlugin,
        crate::rig::RigPlugin,
        crate::character::CharacterPlugin,
        DeployablePlugin,
    ))
    .add_systems(Startup, scene)
    .add_systems(
        Update,
        advance
            .before(super::super::sync)
            .before(CollectInteractions),
    )
    .add_systems(Update, aim.after(super::super::sync).before(collect))
    .add_systems(Last, capture);
    app.run();
}

fn scene(
    mut commands: Commands,
    mut windows: Query<&mut CursorOptions, With<PrimaryWindow>>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
) {
    let mut cursor = windows.single_mut().unwrap();
    cursor.visible = false;
    cursor.grab_mode = CursorGrabMode::Locked;
    commands.spawn((
        Camera3d::default(),
        PlayerCamera,
        Projection::Perspective(PerspectiveProjection {
            fov: 70_f32.to_radians(),
            ..default()
        }),
        bevy::core_pipeline::tonemapping::Tonemapping::None,
        Transform::IDENTITY,
    ));
    commands.spawn((
        DirectionalLight {
            illuminance: 12000.,
            shadow_maps_enabled: true,
            ..default()
        },
        Transform::from_xyz(4., 10., 4.).looking_at(Vec3::new(0., 5., -2.), Vec3::Y),
    ));
    commands.spawn((
        Mesh3d(meshes.add(Cuboid::new(8., 0.1, 8.))),
        MeshMaterial3d(materials.add(StandardMaterial {
            base_color: Color::srgb(0.26, 0.3, 0.34),
            ..default()
        })),
        Transform::from_xyz(0., 4.95, -2.),
    ));
}
fn aim(
    proof: Res<Proof>,
    replicas: Res<Replicas>,
    bodies: Query<&Transform, Without<PlayerCamera>>,
    mut camera: Query<&mut Transform, With<PlayerCamera>>,
) {
    let Some(body) = replicas.instances.get(&proof.id) else {
        return;
    };
    let Ok(pose) = bodies.get(body.entity) else {
        return;
    };
    let b = replicas.art[&9105].bounds;
    let target = pose.transform_point((b.min + b.max) * 0.5);
    *camera.single_mut().unwrap() =
        Transform::from_translation(target + Vec3::new(0., 0.55, 2.2)).looking_at(target, Vec3::Y);
}
fn advance(
    host: Res<Host>,
    peer: Res<CoreBridge>,
    proof: Res<Proof>,
    mut state: ResMut<GameSnapshot>,
    mut model: ResMut<UiModel>,
    mut events: ResMut<FrameInput>,
    mut focus: ResMut<InteractionFocus>,
    windows: Query<Entity, With<PrimaryWindow>>,
    mut input_stage: Local<Option<(usize, u8)>>,
) {
    events.begin();
    *focus = default();
    state.0 = peer.request("tick", json!({})).unwrap()["state"].clone();
    host.0.request("tick", json!({})).unwrap();
    model.adopt_snapshot(&state.0);
    // ScreenshotCaptured may run before Last increments the frame. Track
    // injection independently so a callback cannot skip the press edge.
    let (stage, edge) = input_stage.get_or_insert((proof.stage, 0));
    if *stage != proof.stage {
        *stage = proof.stage;
        *edge = 0;
    }
    if proof.stage > 0 && proof.stage < 3 && *edge < 2 {
        events.push(WindowEvent::KeyboardInput(KeyboardInput {
            key_code: KeyCode::KeyF,
            logical_key: Key::Character("f".into()),
            state: if *edge == 0 {
                ButtonState::Pressed
            } else {
                ButtonState::Released
            },
            text: None,
            repeat: false,
            window: windows.single().unwrap(),
        }));
        *edge += 1;
    }
}
fn capture(
    mut commands: Commands,
    mut proof: ResMut<Proof>,
    snapshot: Res<GameSnapshot>,
    info: Res<FocusedInfo>,
    indicators: Query<(
        &super::super::switch::Indicator,
        &MeshMaterial3d<StandardMaterial>,
    )>,
    materials: Res<Assets<StandardMaterial>>,
    peer: Res<CoreBridge>,
    mut exit: MessageWriter<AppExit>,
) {
    let prefix = std::env::var("UG_SWITCH_SHOT").unwrap_or_else(|_| "/tmp/bevy-switch".into());
    if proof.stage == 3 {
        std::fs::write(
            format!("{prefix}.json"),
            serde_json::to_vec_pretty(&proof.records).unwrap(),
        )
        .unwrap();
        exit.write(AppExit::Success);
        return;
    }
    proof.frame += 1;
    assert!(
        proof.frame < 180,
        "accepted source switch fixture timed out"
    );
    if proof.pending || proof.frame < 24 {
        return;
    }
    let on = proof.stage != 1;
    let row = snapshot.0["deployables"]
        .as_array()
        .unwrap()
        .iter()
        .find(|r| r["netId"] == proof.id)
        .unwrap();
    if row["toggledOn"] != on {
        return;
    }
    let content = &info
        .0
        .as_ref()
        .expect("production source camera focus")
        .content;
    assert!(
        content
            .prompt
            .contains(if on { "Turn Off" } else { "Turn On" })
    );
    let (_, handle) = indicators
        .iter()
        .find(|(i, _)| i.net_id == proof.id)
        .expect("original source state cube");
    let color = materials.get(&handle.0).unwrap().base_color.to_srgba();
    assert!(if on {
        color.green > color.red
    } else {
        color.red > color.green
    });
    let record = json!({"stage":proof.stage,"state":snapshot.0,"prompt":content.prompt,
        "indicator":[color.red,color.green,color.blue],"power":peer.request("powerView",json!({})).unwrap()["result"]});
    proof.records.push(record);
    proof.pending = true;
    commands
        .spawn(Screenshot::primary_window())
        .observe(save_to_disk(format!("{prefix}-{}.png", proof.stage)))
        .observe(|_: On<ScreenshotCaptured>, mut proof: ResMut<Proof>| {
            proof.stage += 1;
            proof.frame = 0;
            proof.pending = false;
        });
}
