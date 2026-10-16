//! Placed storage on real meshes against a real local bridge: the fridge ghost
//! placed by injected LMB, the accepted Refrigerator body and label, a looked-at
//! campfire keeping its deployable panel, and a wire into the fridge's port.
use super::*;
use crate::{
    settings::Settings,
    ui::{Screen, UiIntent, input_order::FrameInput},
    use_items::{Cell, HandEvent},
};
use bevy::{
    app::AppExit,
    input::{ButtonState, mouse::MouseButtonInput},
    render::view::screenshot::{Screenshot, ScreenshotCaptured, save_to_disk},
    time::TimeUpdateStrategy,
    window::{CursorGrabMode, CursorOptions, PrimaryWindow, WindowEvent},
    winit::WinitPlugin,
};
use std::path::Path;

const STAGES: [&str; 4] = ["fridge-ghost", "fridge-placed", "campfire", "wired"];
#[derive(Resource)]
struct Proof {
    feet: Vec3,
    stage: usize,
    frame: usize,
    pending: bool,
    clicked: bool,
    staged: usize,
    records: Vec<Value>,
}
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
fn row(state: &Value, def: u16) -> Option<u32> {
    state["deployables"]
        .as_array()?
        .iter()
        .find(|r| r["defId"] == def)
        .map(|r| r["netId"].as_u64().unwrap() as u32)
}
fn hold(bridge: &CoreBridge, state: &Value, id: u16) {
    let cell = first_cell(state, id);
    bridge
        .request("action", json!({"name":"holdItem","args":{"page":cell.page,"x":cell.x,"y":cell.y}}))
        .unwrap();
}

#[test]
#[ignore = "Vulkan/display; UG_STORAGE_SHOT sets capture prefix"]
fn render_fridge_placement_body_label_campfire_focus_and_wired_port() {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"));
    let config = WorldConfig::default();
    let bridge = CoreBridge::launch(root).unwrap();
    let mut state = bridge
        .request("init", json!({"contentRoot":config.content_root,"spawn":[0,5,0],"allowCheats":true}))
        .unwrap()["state"]
        .clone();
    for id in [9130, 362, 458] {
        bridge
            .request("action", json!({"name":"console","args":{"text":format!("give {id}")}}))
            .unwrap();
        state = super::super::tests::tick(&bridge);
    }
    hold(&bridge, &state, 9130);
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
                    title: "Original placed storage".into(),
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
        staged: 0,
        records: vec![],
    })
    .insert_resource(TimeUpdateStrategy::ManualDuration(std::time::Duration::from_millis(50)))
    .insert_resource(GlobalAmbientLight {
        brightness: 260.,
        ..default()
    })
    .insert_resource(ClearColor(Color::srgb(0.52, 0.62, 0.72)))
    .add_plugins((
        RapierPhysicsPlugin::<NoUserData>::default(),
        crate::info_billboard::InfoBillboardPlugin,
        crate::rig::RigPlugin,
        crate::character::CharacterPlugin,
        DeployablePlugin,
    ))
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
    placer: Res<super::super::placement::Placer>,
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
    let fridge = feet + Vec3::new(0., 0., -2.4);
    let campfire = feet + Vec3::new(1.8, 0., -2.2);
    let generator = feet + Vec3::new(-2.2, 0., -2.6);
    let eye = feet + Vec3::Y * 1.6;
    let stage = STAGES[proof.stage.min(STAGES.len() - 1)];
    *camera.single_mut().unwrap() = match stage {
        "fridge-ghost" => Transform::from_translation(eye + Vec3::Z * 1.2).looking_at(fridge, Vec3::Y),
        "fridge-placed" if !proof.clicked => Transform::from_translation(eye).looking_at(fridge, Vec3::Y),
        "fridge-placed" => Transform::from_translation(feet + Vec3::new(0.9, 1.7, 0.4))
            .looking_at(fridge + Vec3::Y * 1.1, Vec3::Y),
        "campfire" => Transform::from_translation(campfire + Vec3::new(0., 1.1, 1.6))
            .looking_at(campfire + Vec3::Y * 0.15, Vec3::Y),
        _ => Transform::from_translation(feet + Vec3::new(-0.6, 1.8, 0.8))
            .looking_at(feet + Vec3::new(-1.1, 0.6, -2.5), Vec3::Y),
    };
    if stage == "fridge-placed" && !proof.clicked && proof.frame > 6
        // Only once the ghost answers the aim (the window may take focus late).
        && placer.active
        && placer.aim.valid
    {
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
    let place = |def: u16, at: Vec3, yaw: f32| {
        bridge
            .request(
                "action",
                json!({"name":"placeDeployable","args":{"defId":def,"pos":at.to_array(),"yawDegrees":yaw}}),
            )
            .unwrap();
    };
    if stage == "campfire" && proof.staged == 0 {
        place(362, campfire, 0.);
        proof.staged = 1;
    }
    if stage == "wired" && proof.staged == 1 {
        place(458, generator, 90.);
        bridge
            .request("action", json!({"name":"console","args":{"text":"give 65"}}))
            .unwrap();
        proof.staged = 2;
    } else if stage == "wired"
        && proof.staged == 2
        && state.0["inventory"]["pages"].as_array().is_some_and(|pages| {
            pages.iter().any(|p| p["items"].as_array().is_some_and(|i| i.iter().any(|i| i["id"] == 65)))
        })
    {
        hold(&bridge, &state.0, 65);
        proof.staged = 3;
    } else if stage == "wired" && proof.staged == 3
        && let (Some(g), Some(f)) = (row(&state.0, 458), row(&state.0, 9130))
    {
        bridge
            .request("action", json!({"name":"connectWire","args":{"srcId":g,"srcPort":0,"dstId":f,"dstPort":0}}))
            .unwrap();
        proof.staged = 4;
    }
}
fn capture(
    mut commands: Commands,
    mut proof: ResMut<Proof>,
    snapshot: Res<GameSnapshot>,
    replicas: Res<Replicas>,
    diagnostic: Res<InteractionDiagnostics>,
    focus: Res<InteractionFocus>,
    info: Res<crate::info_billboard::FocusedInfo>,
    fridges: Query<(), With<Fridge>>,
    mut exit: MessageWriter<AppExit>,
) {
    let prefix = std::env::var("UG_STORAGE_SHOT").unwrap_or_else(|_| "/tmp/bevy-storage".into());
    if proof.stage == STAGES.len() {
        std::fs::write(format!("{prefix}.json"), serde_json::to_vec_pretty(&proof.records).unwrap()).unwrap();
        exit.write(AppExit::Success);
        return;
    }
    proof.frame += 1;
    assert!(
        proof.frame < 300,
        "storage fixture stalled at {}: offered {:?}, instances {:?}, rows {}",
        STAGES[proof.stage],
        diagnostic.offered,
        replicas.instances.keys().collect::<Vec<_>>(),
        snapshot.0["deployables"]
    );
    if proof.pending || proof.frame < 16 {
        return;
    }
    let fridge = row(&snapshot.0, 9130);
    let ready = match STAGES[proof.stage] {
        "fridge-ghost" => fridge.is_none(),
        "fridge-placed" => {
            fridge.is_some_and(|id| replicas.instances.get(&id).is_some_and(|i| fridges.contains(i.entity)))
                && diagnostic.offered.is_none()
                && proof.frame > 40
        }
        "campfire" => row(&snapshot.0, 362).is_some_and(|id| diagnostic.offered == Some(id)) && info.0.is_some(),
        _ => snapshot.0["powerStateHash"].is_u64() && proof.staged == 4 && proof.frame > 60,
    };
    if !ready {
        return;
    }
    let stage = STAGES[proof.stage];
    proof.records.push(json!({"stage":stage,"offered":diagnostic.offered,
        "openStorageCandidate":fridge.is_some_and(|id| focus.selected("openStorage", id)),
        "panel":info.0.as_ref().map(|v| v.content.prompt.clone()),
        "deployables":snapshot.0["deployables"]}));
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
