//! A wind turbine charges a battery that lights a spotlight, and the map's grid
//! box feeds a gas pump, rendered from the accepted state of a real UDP host.
//! Wiring uses the production JSONL intents (not the native wire tool); the
//! look-at panels use the production focus ray. The fixtures' map meshes are
//! absent here (the map object draws them), so their panels float over sockets.
use super::*;
use bevy::{
    app::AppExit,
    render::view::screenshot::{Screenshot, ScreenshotCaptured, save_to_disk},
    time::TimeUpdateStrategy,
    winit::WinitPlugin,
};
use std::path::Path;

#[derive(Resource)]
struct Host(CoreBridge);
#[derive(Resource)]
struct Proof {
    turbine: u32,
    battery: u32,
    lamp: u32,
    grid: u32,
    pump: u32,
    stage: usize,
    frame: usize,
    pending: bool,
    submitted: bool,
    records: Vec<Value>,
}
const STAGES: usize = 7;

#[test]
#[ignore = "Vulkan/display; UG_POWER_SHOT sets capture prefix, UG_WIND the source turbine override"]
fn render_udp_turbine_charges_battery_that_lights_spotlight_and_source_readouts() {
    // The source's own UG_WIND turbine-test override, read by the host's server
    // wind sampler at session start. The client never samples turbine wind.
    if std::env::var_os("UG_WIND").is_none() {
        // SAFETY: this ignored fixture runs alone (`--exact`), before any thread reads the environment.
        unsafe { std::env::set_var("UG_WIND", "0.6") };
    }
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
        json!({"contentRoot":config.content_root,"listenPort":port,"spawn":[0,5,0],"allowCheats":true}),
    )
    .unwrap();
    let peer = CoreBridge::launch(root).unwrap();
    let mut state = peer
        .request(
            "init",
            json!({"contentRoot":config.content_root,"connectHost":"127.0.0.1","connectPort":port}),
        )
        .unwrap()["state"]
        .clone();
    let placements = [
        (9106, -2.4, -3.2, 20.),
        (1450, 0., -1.6, 0.),
        (459, 2.2, -3.4, 200.),
    ];
    // One item at a time: the bare-handed owner cannot hold all three at once.
    for step in 0..130_usize {
        let (slot, phase) = (step.saturating_sub(24) / 30, step.saturating_sub(24) % 30);
        if let Some(&(def, x, z, yaw)) = placements.get(slot).filter(|_| step >= 24) {
            if phase == 0 {
                peer.request(
                    "action",
                    json!({"name":"console","args":{"text":format!("give {def}")}}),
                )
                .unwrap();
            } else if phase == 15 {
                peer.request(
                    "action",
                    json!({"name":"placeDeployable","args":{"defId":def,"pos":[x,5,z],"yawDegrees":yaw}}),
                )
                .unwrap();
            }
        }
        state = peer.request("tick", json!({})).unwrap()["state"].clone();
        host.request("tick", json!({})).unwrap();
        std::thread::sleep(std::time::Duration::from_millis(1));
    }
    let id = |def| {
        state["deployables"]
            .as_array()
            .unwrap()
            .iter()
            .find(|r| r["defId"] == def)
            .expect("accepted placement")["netId"]
            .as_u64()
            .unwrap() as u32
    };
    let (turbine, battery, lamp) = (id(9106), id(1450), id(459));
    // The map's breaker box and forecourt pump, registered the way MapServices does.
    let fixtures = host
        .request(
            "worldServices",
            json!({"fixtures":[{"defId":9200,"position":[-3.6,5,0.4],"yaw":0},
            {"defId":9201,"position":[3.6,5,0.4],"yaw":0}]}),
        )
        .unwrap();
    let fixture = |i: usize| fixtures["result"]["fixtures"][i]["netId"].as_u64().unwrap() as u32;
    let (grid, pump) = (fixture(0), fixture(1));
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
                    title: "Accepted source power".into(),
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
        turbine,
        battery,
        lamp,
        grid,
        pump,
        stage: 0,
        frame: 0,
        pending: false,
        submitted: false,
        records: vec![],
    })
    .insert_resource(TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(50),
    ))
    .insert_resource(GlobalAmbientLight {
        brightness: 40.,
        ..default()
    })
    .insert_resource(ClearColor(Color::srgb(0.05, 0.06, 0.09)))
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
    // Dusk: enough key light to read the bodies, dark enough for the lamp.
    commands.spawn((
        DirectionalLight {
            illuminance: 900.,
            shadow_maps_enabled: true,
            ..default()
        },
        Transform::from_xyz(4., 10., 6.).looking_at(Vec3::new(0., 5., -2.), Vec3::Y),
    ));
    commands.spawn((
        Mesh3d(meshes.add(Cuboid::new(16., 0.1, 18.))),
        MeshMaterial3d(materials.add(StandardMaterial {
            base_color: Color::srgb(0.32, 0.34, 0.36),
            ..default()
        })),
        Transform::from_xyz(0., 4.95, -6.),
    ));
}

fn aim(
    proof: Res<Proof>,
    replicas: Res<Replicas>,
    bodies: Query<&Transform, Without<PlayerCamera>>,
    mut camera: Query<&mut Transform, With<PlayerCamera>>,
) {
    let mut camera = camera.single_mut().unwrap();
    let focus = match proof.stage {
        2 => Some((proof.battery, Vec3::new(0.3, 1.0, 1.9))),
        3 => Some((proof.turbine, Vec3::new(0.2, 0.15, 1.9))),
        // In front of the lit heads, which face +Z after the 200 degree yaw.
        4 => Some((proof.lamp, Vec3::new(-0.7, 0.2, 2.1))),
        5 => Some((proof.pump, Vec3::ZERO)),
        6 => Some((proof.grid, Vec3::ZERO)),
        _ => None,
    };
    let Some((id, offset)) = focus else {
        *camera = Transform::from_xyz(0.6, 7.4, 4.6).looking_at(Vec3::new(0., 5.6, -3.2), Vec3::Y);
        return;
    };
    let Some(body) = replicas.instances.get(&id) else {
        return;
    };
    let Ok(pose) = bodies.get(body.entity) else {
        return;
    };
    if proof.stage >= 5 {
        // Aim near the top of the AddInteractionCollider box (pump to 2.5 m,
        // breaker box to 2.0 m) so the panel above it (2.6/2.4 m) stays in frame.
        let target = pose.translation + Vec3::Y * if proof.stage == 5 { 2.2 } else { 1.7 };
        *camera = Transform::from_translation(target + Vec3::new(0.3, 0.2, 2.0))
            .looking_at(target, Vec3::Y);
        return;
    }
    if proof.stage == 4 {
        // Face-on to the authored head lenses (flat y=-0.258, normal -Y), still
        // inside the 2.6 m focus reach of the body.
        let heads = pose.transform_point(Vec3::new(0., -0.26, -1.35));
        let normal = pose.rotation * -Vec3::Y;
        *camera = Transform::from_translation(heads + normal * 1.9 + Vec3::Y * 0.15)
            .looking_at(heads, Vec3::Y);
        return;
    }
    let b = replicas.art[&body.def_id].bounds;
    let mut target = pose.transform_point((b.min + b.max) * 0.5);
    if proof.stage == 3 {
        target.y = pose.translation.y + 1.2;
    }
    *camera = Transform::from_translation(target + offset).looking_at(target, Vec3::Y);
}

fn advance(
    host: Res<Host>,
    peer: Res<CoreBridge>,
    catalog: Res<ItemCatalog>,
    mut proof: ResMut<Proof>,
    mut state: ResMut<GameSnapshot>,
    mut model: ResMut<UiModel>,
    mut events: ResMut<FrameInput>,
    mut focus: ResMut<InteractionFocus>,
) {
    events.begin();
    *focus = default();
    if proof.stage == 1 && !proof.submitted {
        // Source port order is the wire sub-address.
        let index = |def: u16, kind: &str| {
            let def = catalog.deployables.iter().find(|d| d.id == def).unwrap();
            def.ports.iter().position(|p| p.kind == kind).unwrap()
        };
        for (src, sp, dst, dp) in [
            (
                proof.turbine,
                index(9106, "Output"),
                proof.battery,
                index(1450, "Consumer"),
            ),
            (
                proof.battery,
                index(1450, "Output"),
                proof.lamp,
                index(459, "Consumer"),
            ),
            (
                proof.grid,
                index(9200, "Output"),
                proof.pump,
                index(9201, "Consumer"),
            ),
        ] {
            peer.request(
                "action",
                json!({"name":"connectWire","args":{"srcId":src,"srcPort":sp,"dstId":dst,"dstPort":dp}}),
            )
            .unwrap();
        }
        proof.submitted = true;
    }
    state.0 = peer.request("tick", json!({})).unwrap()["state"].clone();
    host.0.request("tick", json!({})).unwrap();
    model.adopt_snapshot(&state.0);
}
fn capture(
    mut commands: Commands,
    mut proof: ResMut<Proof>,
    snapshot: Res<GameSnapshot>,
    info: Res<FocusedInfo>,
    accepted: Res<super::super::wires::Accepted>,
    lamps: Query<&super::super::lamps::Lamps>,
    lights: Query<(&Visibility, &SpotLight)>,
    peer: Res<CoreBridge>,
    mut exit: MessageWriter<AppExit>,
) {
    let prefix = std::env::var("UG_POWER_SHOT").unwrap_or_else(|_| "/tmp/bevy-power".into());
    if proof.stage == STAGES {
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
        proof.frame < 400,
        "accepted power fixture timed out at stage {}",
        proof.stage
    );
    if proof.pending || proof.frame < 40 {
        return;
    }
    let row = |id: u32| {
        snapshot.0["deployables"]
            .as_array()
            .unwrap()
            .iter()
            .find(|r| r["netId"] == id)
            .cloned()
            .unwrap()
    };
    let wind = row(proof.turbine)["wind"].as_f64().unwrap_or(0.);
    let energy = row(proof.battery)["energy"].as_f64().unwrap_or(0.);
    let lamp = lamps.iter().next().expect("spotlight lamps");
    let wired = accepted.graph.as_ref().is_some_and(|g| g.wires.len() == 3);
    match proof.stage {
        0 => {
            assert!(wind > 0.03, "host-accepted wind turns the turbine");
            if lamp.level != 0. || wired {
                return;
            }
        }
        1 => {
            // Wait for the accepted circuit and the full 1.3 s source envelope.
            if !wired || energy <= 0. || lamp.level < 1. {
                return;
            }
        }
        _ => {
            let Some(view) = &info.0 else { return };
            let expected = [
                "Vehicle Battery",
                "Wind Turbine",
                "Spotlight",
                "Gas Pump",
                "Grid Power",
            ][proof.stage - 2];
            if !view.content.name.starts_with(expected) {
                return;
            }
            // The accepted solve has the pump's 750 W on the grid's one output.
            if proof.stage == 6 && view.content.prompt.starts_with("0 /") {
                return;
            }
        }
    }
    let shown: Vec<_> = lights
        .iter()
        .map(|(v, s)| json!({"visible":*v != Visibility::Hidden,"intensity":s.intensity}))
        .collect();
    let record = json!({
        "stage":proof.stage,"wind":wind,"energy":energy,"lampLevel":lamp.level,"lights":shown,
        "info":info.0.as_ref().map(|v| json!({"name":v.content.name,"prompt":v.content.prompt,
            "bars":v.content.bars.iter().map(|b| b.map(|b| b.value)).collect::<Vec<_>>()})),
        "rows":[row(proof.turbine),row(proof.battery),row(proof.lamp),row(proof.grid),row(proof.pump)],
        "power":peer.request("powerView",json!({})).unwrap()["result"],
        "input":"production JSONL wire intents over real UDP; production focus ray; not the native wire tool"
    });
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
