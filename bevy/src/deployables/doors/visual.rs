//! The LocalOnly placeable door on real meshes against a real local bridge:
//! the source ghost, the placer's own door after the place gesture, and its F
//! swing. Injected ordered input; not an OS journey.
use super::*;
use crate::{
    ui::{Screen, UiIntent},
    use_items::{Cell, HandEvent},
};
use bevy::{
    app::AppExit,
    input::{
        keyboard::{Key, KeyboardInput},
        mouse::MouseButtonInput,
    },
    render::view::screenshot::{Screenshot, ScreenshotCaptured, save_to_disk},
    time::TimeUpdateStrategy,
    window::{CursorGrabMode, CursorOptions},
    winit::WinitPlugin,
};
use std::path::Path;

const STAGES: [&str; 3] = ["ghost", "closed", "open"];
#[derive(Resource)]
struct Proof {
    feet: Vec3,
    stage: usize,
    frame: usize,
    pending: bool,
    clicked: bool,
    toggled: bool,
    records: Vec<Value>,
}

#[test]
#[ignore = "Vulkan/display; UG_DOOR_SHOT sets capture prefix"]
fn render_local_door_ghost_placed_and_swung_open() {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"));
    let config = WorldConfig::default();
    let bridge = CoreBridge::launch(root).unwrap();
    bridge
        .request("init", json!({"contentRoot":config.content_root,"spawn":[0,5,0],"allowCheats":true}))
        .unwrap();
    bridge
        .request("action", json!({"name":"console","args":{"text":"give 9162"}}))
        .unwrap();
    let mut state = super::super::tests::tick(&bridge);
    let cell = state["inventory"]["pages"]
        .as_array()
        .unwrap()
        .iter()
        .find_map(|p| {
            p["items"].as_array()?.iter().find(|i| i["id"] == 9162).map(|i| Cell {
                id: 9162,
                page: p["index"].as_u64().unwrap() as u8,
                x: i["x"].as_u64().unwrap() as u8,
                y: i["y"].as_u64().unwrap() as u8,
            })
        })
        .unwrap();
    bridge
        .request("action", json!({"name":"holdItem","args":{"page":cell.page,"x":cell.x,"y":cell.y}}))
        .unwrap();
    state = super::super::tests::tick(&bridge);
    let feet = crate::player::vector(&state["player"]["position"]).unwrap();
    let response = bridge.request("catalog", json!({})).unwrap();
    let mut catalog = ItemCatalog::default();
    catalog.deployables = serde_json::from_value(response["result"]["deployables"].clone()).unwrap();
    catalog.items = serde_json::from_value::<Vec<crate::entities::ItemVisual>>(response["result"]["items"].clone())
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
                    title: "Original placed door".into(),
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
        toggled: false,
        records: vec![],
    })
    .insert_resource(TimeUpdateStrategy::ManualDuration(std::time::Duration::from_millis(50)))
    .insert_resource(GlobalAmbientLight {
        brightness: 260.,
        ..default()
    })
    .insert_resource(ClearColor(Color::srgb(0.52, 0.62, 0.72)))
    .add_plugins((RapierPhysicsPlugin::<NoUserData>::default(), DeployablePlugin))
    .add_systems(Startup, scene)
    .add_systems(Update, advance.before(CollectInteractions).before(super::super::sync))
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
    commands.spawn((
        Mesh3d(meshes.add(Cuboid::new(40., 1., 40.))),
        MeshMaterial3d(materials.add(Color::srgb(0.33, 0.38, 0.29))),
        RigidBody::Fixed,
        Collider::cuboid(20., 0.5, 20.),
        CollisionGroups::new(Group::GROUP_1, Group::ALL),
        Transform::from_translation(proof.feet - Vec3::Y * 0.5),
        crate::world::WorldTerrain,
    ));
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
    let spot = feet - Vec3::Z * 2.5;
    let window = windows.single().unwrap();
    let stage = STAGES[proof.stage.min(STAGES.len() - 1)];
    *camera.single_mut().unwrap() = match stage {
        "ghost" => Transform::from_translation(feet + Vec3::new(0., 1.6, 0.9)).looking_at(spot, Vec3::Y),
        "closed" if !proof.clicked => Transform::from_translation(feet + Vec3::Y * 1.6).looking_at(spot, Vec3::Y),
        _ => Transform::from_translation(feet + Vec3::new(0.6, 1.7, 1.2)).looking_at(spot + Vec3::new(-1.1, 1.2, 0.), Vec3::Y),
    };
    if stage == "closed" && !proof.clicked && proof.frame > 6 {
        for state in [ButtonState::Pressed, ButtonState::Released] {
            events.push(WindowEvent::MouseButtonInput(MouseButtonInput {
                button: MouseButton::Left,
                state,
                window,
            }));
        }
        proof.clicked = true;
    }
    if stage == "open" && !proof.toggled && proof.frame > 4 {
        for state in [ButtonState::Pressed, ButtonState::Released] {
            events.push(WindowEvent::KeyboardInput(KeyboardInput {
                key_code: KeyCode::KeyF,
                logical_key: Key::Character("f".into()),
                state,
                text: None,
                repeat: false,
                window,
            }));
        }
        proof.toggled = true;
    }
}
fn capture(
    mut commands: Commands,
    mut proof: ResMut<Proof>,
    snapshot: Res<GameSnapshot>,
    placer: Res<super::super::placement::Placer>,
    doors: Query<&PlacedDoor>,
    poses: Query<(&Transform, Option<&Name>), Or<(With<PlayerCamera>, With<super::super::placement::ghost::GhostRoot>)>>,
    mut exit: MessageWriter<AppExit>,
) {
    let prefix = std::env::var("UG_DOOR_SHOT").unwrap_or_else(|_| "/tmp/bevy-door".into());
    if proof.stage == STAGES.len() {
        std::fs::write(format!("{prefix}.json"), serde_json::to_vec_pretty(&proof.records).unwrap()).unwrap();
        exit.write(AppExit::Success);
        return;
    }
    proof.frame += 1;
    assert!(proof.frame < 300, "door fixture stalled at {} {:?}", STAGES[proof.stage], placer.aim);
    if proof.pending || proof.frame < 16 {
        return;
    }
    let door = doors.iter().next();
    let ready = match STAGES[proof.stage] {
        "ghost" => placer.aim.valid && placer.active && proof.frame > 30,
        "closed" => door.is_some_and(|d| !d.open && d.solid) && proof.frame > 40,
        _ => door.is_some_and(|d| d.open && d.swing >= 1. && d.solid),
    };
    if !ready {
        return;
    }
    let stage = STAGES[proof.stage];
    proof.records.push(json!({"stage":stage,"aim":{"valid":placer.aim.valid,"point":placer.aim.point.to_array(),"yaw":placer.aim.yaw},
        "door":door.map(|d| json!({"open":d.open,"swing":d.swing,"solid":d.solid})),"sent":placer.sent,
        "poses":poses.iter().map(|(t, n)| json!({"name":n.map(|n| n.as_str().to_owned()),"t":t.translation.to_array(),"r":t.rotation.to_array()})).collect::<Vec<_>>(),
        "deployables":snapshot.0["deployables"],"inventory":snapshot.0["inventory"]["pages"]}));
    proof.pending = true;
    commands
        .spawn(Screenshot::primary_window())
        .observe(save_to_disk(format!("{prefix}-{stage}.png")))
        .observe(|_: On<ScreenshotCaptured>, mut proof: ResMut<Proof>| {
            proof.stage += 1;
            proof.frame = 0;
            proof.pending = false;
        });
}
