//! Renderer over a real UDP owner. Commands use the production JSONL intents;
//! this isolates accepted geometry and does not claim an OS wire-tool journey.
use super::*;
use bevy::{
    app::AppExit,
    render::view::screenshot::{Screenshot, ScreenshotCaptured, save_to_disk},
    winit::WinitPlugin,
};
use std::path::Path;

#[derive(Resource)]
struct Host(CoreBridge);
#[derive(Resource)]
struct Proof {
    source: u32,
    consumer: u32,
    wire: Option<u32>,
    stage: usize,
    frame: usize,
    pending: bool,
    submitted: bool,
    prefix: String,
}

#[test]
#[ignore = "Vulkan/display; UG_WIRE_SHOT sets capture prefix"]
fn render_udp_accepted_wire_and_accepted_removal_on_source_devices() {
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
    for stage in 0..3 {
        for _ in 0..24 {
            state = peer.request("tick", json!({})).unwrap()["state"].clone();
            host.request("tick", json!({})).unwrap();
            std::thread::sleep(std::time::Duration::from_millis(1));
        }
        if stage == 0 {
            for id in [458, 459] {
                peer.request(
                    "action",
                    json!({"name":"console","args":{"text":format!("give {id}")}}),
                )
                .unwrap();
            }
        } else if stage == 1 {
            for (def, x, yaw) in [(458, -1.2, 27.), (459, 1.2, -34.)] {
                peer.request(
                    "action",
                    json!({"name":"placeDeployable","args":{
                    "defId":def,"pos":[x,5,-2],"yawDegrees":yaw}}),
                )
                .unwrap();
            }
        }
    }
    let rows = state["deployables"].as_array().unwrap();
    let id = |def| {
        rows.iter().find(|r| r["defId"] == def).unwrap()["netId"]
            .as_u64()
            .unwrap() as u32
    };
    let (source, consumer) = (id(458), id(459));
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
                    title: "Accepted source wires".into(),
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
    .init_resource::<ContentCache>()
    .init_resource::<WorldGeometry>()
    .init_resource::<WorldSessionEpoch>()
    .insert_resource(GlobalAmbientLight {
        brightness: 200.,
        ..default()
    })
    .insert_resource(ClearColor(Color::srgb(0.24, 0.27, 0.30)))
    .insert_resource(Proof {
        source,
        consumer,
        wire: None,
        stage: 0,
        frame: 0,
        pending: false,
        submitted: false,
        prefix: std::env::var("UG_WIRE_SHOT").unwrap_or_else(|_| "/tmp/bevy-wire".into()),
    })
    .add_plugins(DeployablePlugin)
    .add_systems(Startup, scene)
    .add_systems(Update, advance.before(super::super::super::sync))
    .add_systems(Last, capture);
    app.run();
}

fn scene(
    mut commands: Commands,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
) {
    commands.spawn((
        Camera3d::default(),
        Transform::from_xyz(3.8, 8., 3.).looking_at(Vec3::new(0., 5.5, -2.), Vec3::Y),
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
    peer: Res<CoreBridge>,
    host: Res<Host>,
    mut snapshot: ResMut<GameSnapshot>,
    mut proof: ResMut<Proof>,
) {
    if proof.pending {
        return;
    }
    if proof.stage > 0 && !proof.submitted {
        let (name, args) = if proof.stage == 1 {
            (
                "connectWire",
                json!({"srcId":proof.source,"srcPort":0,"dstId":proof.consumer,"dstPort":0}),
            )
        } else {
            ("removeWire", json!({"wireId":proof.wire.unwrap()}))
        };
        peer.request("action", json!({"name":name,"args":args}))
            .unwrap();
        proof.submitted = true;
    }
    snapshot.0 = peer.request("tick", json!({})).unwrap()["state"].clone();
    host.0.request("tick", json!({})).unwrap();
}

fn capture(
    mut commands: Commands,
    mut proof: ResMut<Proof>,
    accepted: Res<Accepted>,
    wires: Query<&WireSegment>,
) {
    if proof.pending {
        return;
    }
    proof.frame += 1;
    if proof.frame < 40 {
        return;
    }
    let graph = accepted
        .graph
        .as_ref()
        .expect("versioned accepted remote graph");
    let ids: Vec<_> = wires.iter().map(|w| w.0).collect();
    let expected = usize::from(proof.stage == 1);
    assert_eq!(
        ids.len(),
        expected,
        "native geometry must follow accepted topology"
    );
    assert_eq!(graph.wires.len(), expected);
    if expected == 1 {
        proof.wire = Some(ids[0]);
    }
    let path = format!("{}-{}.png", proof.prefix, proof.stage);
    std::fs::write(
        format!("{}-{}.json", proof.prefix, proof.stage),
        serde_json::to_string_pretty(&json!({
            "stage":proof.stage,"sourceId":proof.source,"consumerId":proof.consumer,"wires":ids,
            "stateHash":graph.state_hash,"readOnly":graph.read_only,"version":graph.version,
            "input":"production JSONL intent over real UDP; wire tool input not yet installed"
        }))
        .unwrap(),
    )
    .unwrap();
    proof.pending = true;
    commands
        .spawn(Screenshot::primary_window())
        .observe(save_to_disk(path))
        .observe(
            |_captured: On<ScreenshotCaptured>,
             mut proof: ResMut<Proof>,
             mut exit: MessageWriter<AppExit>| {
                proof.stage += 1;
                proof.frame = 0;
                proof.pending = false;
                proof.submitted = false;
                if proof.stage == 3 {
                    exit.write(AppExit::Success);
                }
            },
        );
}
