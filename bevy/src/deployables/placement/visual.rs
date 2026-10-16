//! Original held-deployable ghost and accepted placement on real meshes, with
//! injected ordered mouse edges against a real local bridge. The fixture
//! forwards the hand intents the way `player.rs` does; it is not an OS journey.
use super::*;
use crate::{
    entities::InteractionFocus,
    ui::{Screen, UiState},
};
use bevy::{
    app::AppExit,
    input::mouse::MouseButtonInput,
    render::view::screenshot::{Screenshot, ScreenshotCaptured, save_to_disk},
    time::TimeUpdateStrategy,
    winit::WinitPlugin,
};
use std::path::Path;

const STAGES: [&str; 4] = ["valid", "invalid", "placed", "wall"];
#[derive(Resource)]
struct Proof {
    feet: Vec3,
    stage: usize,
    frame: usize,
    pending: bool,
    clicked: bool,
    switched: bool,
    records: Vec<Value>,
}
const WALL_X: f32 = -2.2;
fn first_cell(state: &Value, id: u16) -> Cell {
    state["inventory"]["pages"]
        .as_array()
        .unwrap()
        .iter()
        .find_map(|p| {
            p["items"].as_array()?.iter().find(|i| i["id"] == id).map(|i| Cell {
                id,
                page: p["index"].as_u64().unwrap() as u8,
                x: i["x"].as_u64().unwrap() as u8,
                y: i["y"].as_u64().unwrap() as u8,
            })
        })
        .unwrap_or_else(|| panic!("no {id} in the bag"))
}

#[test]
#[ignore = "Vulkan/display; UG_PLACE_SHOT sets capture prefix"]
fn render_source_ghost_valid_invalid_accepted_body_and_wall_mount() {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"));
    let config = WorldConfig::default();
    let bridge = CoreBridge::launch(root).unwrap();
    let mut state = bridge
        .request(
            "init",
            json!({"contentRoot":config.content_root,"spawn":[0,5,0],"allowCheats":true}),
        )
        .unwrap()["state"]
        .clone();
    for id in [458, 458, 1222] {
        bridge
            .request("action", json!({"name":"console","args":{"text":format!("give {id}")}}))
            .unwrap();
        state = super::super::tests::tick(&bridge);
    }
    let jar = first_cell(&state, 458);
    bridge
        .request(
            "action",
            json!({"name":"holdItem","args":{"page":jar.page,"x":jar.x,"y":jar.y}}),
        )
        .unwrap();
    state = super::super::tests::tick(&bridge);
    assert_eq!(Cell::held(&state).map(|c| c.id), Some(458));
    let feet = crate::player::vector(&state["player"]["position"]).unwrap();
    let response = bridge.request("catalog", json!({})).unwrap();
    let mut catalog = ItemCatalog::default();
    catalog.deployables = serde_json::from_value(response["result"]["deployables"].clone()).unwrap();
    catalog.items = serde_json::from_value::<Vec<crate::entities::ItemVisual>>(
        response["result"]["items"].clone(),
    )
    .unwrap()
    .into_iter()
    .map(|i| (i.id, i))
    .collect();
    let mut model = UiModel::default();
    model.adopt_snapshot(&state);
    let mut app = App::new();
    app.add_plugins(
        DefaultPlugins
            .set(WindowPlugin {
                primary_window: Some(Window {
                    title: "Original deployable placement".into(),
                    resolution: (1100, 760).into(),
                    ..default()
                }),
                ..default()
            })
            .set(AssetPlugin {
                file_path: config.content_root.canonicalize().unwrap().to_string_lossy().into(),
                ..default()
            })
            .set(WinitPlugin {
                run_on_any_thread: true,
                ..default()
            }),
    )
    .insert_resource(config)
    .insert_resource(bridge)
    .insert_resource(GameSnapshot(state))
    .insert_resource(catalog)
    .insert_resource(model)
    .insert_resource(UiState {
        screen: Screen::Playing,
        ..default()
    })
    .init_resource::<Settings>()
    .init_resource::<FrameInput>()
    .init_resource::<InteractionFocus>()
    .init_resource::<ContentCache>()
    .init_resource::<WorldGeometry>()
    .init_resource::<WorldSessionEpoch>()
    .add_message::<UiIntent>()
    .add_message::<HandEvent>()
    .insert_resource(Proof {
        feet,
        stage: 0,
        frame: 0,
        pending: false,
        clicked: false,
        switched: false,
        records: vec![],
    })
    .insert_resource(TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(50),
    ))
    .insert_resource(GlobalAmbientLight {
        brightness: 260.,
        ..default()
    })
    .insert_resource(ClearColor(Color::srgb(0.52, 0.62, 0.72)))
    .add_plugins((RapierPhysicsPlugin::<NoUserData>::default(), DeployablePlugin))
    .add_systems(Startup, scene)
    .add_systems(
        Update,
        (advance.before(super::aim), forward.after(super::act)),
    )
    .add_systems(Last, capture);
    app.run();
}

fn scene(
    mut commands: Commands,
    proof: Res<Proof>,
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
        bevy::camera::Hdr,
        Projection::Perspective(PerspectiveProjection {
            fov: 70_f32.to_radians(),
            ..default()
        }),
        Transform::IDENTITY,
    ));
    commands.spawn((
        DirectionalLight {
            illuminance: 9000.,
            shadow_maps_enabled: true,
            ..default()
        },
        Transform::from_xyz(-6., 14., 8.).looking_at(proof.feet, Vec3::Y),
    ));
    // Source terrain under the owner, and a plain wall to the left.
    commands.spawn((
        Mesh3d(meshes.add(Cuboid::new(40., 1., 40.))),
        MeshMaterial3d(materials.add(Color::srgb(0.33, 0.38, 0.29))),
        RigidBody::Fixed,
        Collider::cuboid(20., 0.5, 20.),
        CollisionGroups::new(Group::GROUP_1, Group::ALL),
        Transform::from_translation(proof.feet - Vec3::Y * 0.5),
        crate::world::WorldTerrain,
    ));
    commands.spawn((
        Mesh3d(meshes.add(Cuboid::new(0.3, 3., 6.))),
        MeshMaterial3d(materials.add(Color::srgb(0.62, 0.58, 0.52))),
        RigidBody::Fixed,
        Collider::cuboid(0.15, 1.5, 3.),
        CollisionGroups::new(Group::GROUP_1, Group::ALL),
        Transform::from_translation(proof.feet + Vec3::new(WALL_X - 0.15, 1.5, -2.)),
    ));
}

/// `player.rs` routing of the hand intents to the bridge.
fn forward(mut intents: MessageReader<UiIntent>, bridge: Res<CoreBridge>) {
    for intent in intents.read() {
        if let UiIntent::Action { name, args } = intent {
            bridge
                .request("action", json!({"name":name,"args":args}))
                .unwrap();
        }
    }
}
fn look(proof: &Proof, at: Vec3) -> Transform {
    Transform::from_translation(proof.feet + Vec3::Y * 1.6).looking_at(at, Vec3::Y)
}
fn advance(
    bridge: Res<CoreBridge>,
    mut proof: ResMut<Proof>,
    mut state: ResMut<GameSnapshot>,
    mut model: ResMut<UiModel>,
    mut events: ResMut<FrameInput>,
    mut focus: ResMut<InteractionFocus>,
    windows: Query<Entity, With<PrimaryWindow>>,
    mut camera: Query<&mut Transform, With<PlayerCamera>>,
) {
    events.begin();
    *focus = default();
    state.0 = bridge.request("tick", json!({})).unwrap()["state"].clone();
    model.adopt_snapshot(&state.0);
    let feet = proof.feet;
    let at = match STAGES[proof.stage.min(STAGES.len() - 1)] {
        "valid" => feet - Vec3::Z * 2.5,
        "invalid" => feet + Vec3::new(WALL_X, 1.2, -2.),
        "placed" if !proof.clicked => feet - Vec3::Z * 2.5,
        "placed" => feet + Vec3::new(1.9, 0., -2.6),
        _ => feet + Vec3::new(WALL_X, 1.6, -2.4),
    };
    *camera.single_mut().unwrap() = look(&proof, at);
    if STAGES[proof.stage.min(STAGES.len() - 1)] == "placed" && !proof.clicked && proof.frame > 6 {
        let window = windows.single().unwrap();
        for state in [ButtonState::Pressed, ButtonState::Released] {
            events.push(WindowEvent::MouseButtonInput(MouseButtonInput {
                button: MouseButton::Left,
                state,
                window,
            }));
        }
        proof.clicked = true;
    }
    if STAGES[proof.stage.min(STAGES.len() - 1)] == "wall" && !proof.switched {
        let cell = first_cell(&state.0, 1222);
        bridge
            .request(
                "action",
                json!({"name":"holdItem","args":{"page":cell.page,"x":cell.x,"y":cell.y}}),
            )
            .unwrap();
        proof.switched = true;
    }
}
fn capture(
    mut commands: Commands,
    mut proof: ResMut<Proof>,
    snapshot: Res<GameSnapshot>,
    placer: Res<Placer>,
    hint: Res<hint::Hint>,
    replicas: Res<Replicas>,
    mut exit: MessageWriter<AppExit>,
) {
    let prefix = std::env::var("UG_PLACE_SHOT").unwrap_or_else(|_| "/tmp/bevy-placement".into());
    if proof.stage == STAGES.len() {
        std::fs::write(
            format!("{prefix}.json"),
            serde_json::to_vec_pretty(&proof.records).unwrap(),
        )
        .unwrap();
        exit.write(AppExit::Success);
        return;
    }
    proof.frame += 1;
    assert!(proof.frame < 240, "placement fixture stalled at {}", STAGES[proof.stage]);
    if proof.pending || proof.frame < 16 {
        return;
    }
    let rows = snapshot.0["deployables"].as_array().map_or(0, Vec::len);
    let ready = match STAGES[proof.stage] {
        "valid" => placer.def == Some(458) && placer.aim.valid && rows == 0,
        "invalid" => !placer.aim.valid && hint.text == "Needs flat ground",
        "placed" => {
            proof.clicked
                && placer.gesture.is_none()
                && rows == 1
                && replicas.instances.len() == 1
                && placer.def == Some(458)
                && placer.aim.valid
                && proof.frame > 40
        }
        _ => placer.def == Some(1222) && placer.aim.valid && proof.frame > 30,
    };
    if !ready {
        return;
    }
    let stage = STAGES[proof.stage];
    proof.records.push(json!({"stage":stage,"aim":{
        "valid":placer.aim.valid,"reason":placer.aim.reason,"point":placer.aim.point.to_array(),
        "normal":placer.aim.normal.to_array(),"yaw":placer.aim.yaw},"hint":hint.text,
        "sent":placer.sent,"deployables":snapshot.0["deployables"]}));
    proof.pending = true;
    commands
        .spawn(Screenshot::primary_window())
        .observe(save_to_disk(format!("{prefix}-{}.png", STAGES[proof.stage])))
        .observe(|_: On<ScreenshotCaptured>, mut proof: ResMut<Proof>| {
            proof.stage += 1;
            proof.frame = 0;
            proof.pending = false;
        });
}
