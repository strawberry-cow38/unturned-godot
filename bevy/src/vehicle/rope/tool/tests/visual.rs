//! Original rope tool and committed rope on real meshes. Injected ordered
//! mouse edges with a real local authority; not a full-game OS journey.
use super::*;
use crate::{content::ContentCache, deployables::DeployablePlugin, world::WorldGeometry};
use bevy::{
    app::AppExit,
    ecs::system::RunSystemOnce,
    render::view::screenshot::{Screenshot, ScreenshotCaptured, save_to_disk},
    winit::WinitPlugin,
};

#[derive(Resource)]
struct Proof {
    tower: u32,
    towed: u32,
    stage: usize,
    frame: usize,
    pending: bool,
    acted: bool,
    prefix: String,
}
const STAGES: [&str; 6] = ["nubs", "preview-free", "preview-tie", "tied", "towing", "clearing"];

#[test]
#[ignore = "Vulkan/display; UG_ROPE_SHOT sets capture prefix"]
fn render_source_rope_tool_preview_accepted_rope_tow_and_clear() {
    let mut cpu = rig();
    let (tower, towed) = (cpu.tower, cpu.towed);
    let bridge = cpu.app.world_mut().remove_resource::<CoreBridge>().unwrap();
    let catalog = cpu.app.world_mut().remove_resource::<ItemCatalog>().unwrap();
    let snapshot = cpu.app.world_mut().remove_resource::<GameSnapshot>().unwrap();
    drop(cpu);
    let config = WorldConfig::default();
    let mut model = UiModel::default();
    model.adopt_snapshot(&snapshot.0);
    let mut app = App::new();
    app.add_plugins(
        DefaultPlugins
            .set(WindowPlugin {
                primary_window: Some(Window {
                    title: "Original tow rope".into(),
                    resolution: (1100, 760).into(),
                    ..default()
                }),
                ..default()
            })
            .set(AssetPlugin {
                file_path: config.content_root.canonicalize().unwrap().to_string_lossy().into(),
                ..default()
            })
            .set(WinitPlugin { run_on_any_thread: true, ..default() }),
    )
    .add_plugins((RapierPhysicsPlugin::<NoUserData>::default(), DeployablePlugin))
    .insert_resource(config)
    .insert_resource(bridge)
    .insert_resource(snapshot)
    .insert_resource(catalog)
    .insert_resource(model)
    .insert_resource(UiState { screen: Screen::Playing, ..default() })
    .init_resource::<Settings>()
    .init_resource::<FrameInput>()
    .init_resource::<ContentCache>()
    .init_resource::<WorldGeometry>()
    .init_resource::<WorldSessionEpoch>()
    .init_resource::<RopeTool>()
    .init_resource::<Preview>()
    .init_resource::<Vehicles>()
    .insert_resource(crate::world::WorldState { ready: true, ..default() })
    .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(std::time::Duration::from_millis(20)))
    .insert_resource(TimestepMode::Fixed { dt: 0.02, substeps: 1 })
    .insert_resource(GlobalAmbientLight { brightness: 220., ..default() })
    .insert_resource(ClearColor(Color::srgb(0.24, 0.27, 0.30)))
    .add_systems(Startup, |mut cursor: Query<&mut CursorOptions, With<PrimaryWindow>>| {
        let mut cursor = cursor.single_mut().unwrap();
        cursor.visible = false;
        cursor.grab_mode = CursorGrabMode::Locked;
    })
    .add_systems(Update, (advance, nubs, input, present).chain())
    .add_systems(
        PostUpdate,
        (
            super::super::super::draw,
            super::super::super::super::step_bodies.before(PhysicsSet::SyncBackend),
        ),
    )
    .add_systems(Last, capture);
    super::super::super::install_draw_resources(&mut app);
    // Real sedan bodies with source render meshes over a ground plane.
    for (id, z) in [(tower, 0.), (towed, SPAN + 3.)] {
        let e = super::super::super::super::tests::body(&mut app, "sedan", Vec3::new(0., 1.2, z));
        {
            let mut b = app.world_mut().get_mut::<VehicleBody>(e).unwrap();
            b.net_id = id;
            b.driver_player_id = if id == tower { 1 } else { 0 };
            b.controls = default();
        }
        app.world_mut().entity_mut(e).insert(Visibility::Inherited);
        app.world_mut().resource_mut::<Vehicles>().entities.insert(id, e);
        app.world_mut()
            .run_system_once(
                move |mut commands: Commands,
                      mut cache: ResMut<ContentCache>,
                      mut meshes: ResMut<Assets<Mesh>>,
                      mut materials: ResMut<Assets<StandardMaterial>>,
                      mut images: ResMut<Assets<Image>>,
                      assets: Res<AssetServer>,
                      config: Res<WorldConfig>| {
                    crate::vehicle::render::build(
                        &mut commands,
                        e,
                        data::named("sedan").unwrap(),
                        0,
                        &config.content_root,
                        &mut cache,
                        &mut meshes,
                        &mut materials,
                        &mut images,
                        &assets,
                    );
                },
            )
            .unwrap();
    }
    let ground_mesh = app.world_mut().resource_mut::<Assets<Mesh>>().add(Cuboid::new(200., 0.1, 200.));
    let ground_material = app.world_mut().resource_mut::<Assets<StandardMaterial>>().add(Color::srgb(0.32, 0.34, 0.30));
    app.world_mut().spawn((
        Mesh3d(ground_mesh),
        MeshMaterial3d(ground_material),
        Collider::cuboid(100., 0.05, 100.),
        CollisionGroups::new(Group::GROUP_1, Group::ALL),
        Transform::from_xyz(0., -0.05, 0.),
    ));
    app.world_mut().spawn((
        DirectionalLight { illuminance: 9000., shadow_maps_enabled: true, ..default() },
        Transform::from_xyz(-10., 20., 6.).looking_at(Vec3::ZERO, Vec3::Y),
    ));
    app.world_mut().spawn((
        Camera3d::default(),
        PlayerCamera,
        bevy::camera::Hdr,
        Projection::Perspective(PerspectiveProjection { fov: 70_f32.to_radians(), ..default() }),
        Transform::default(),
    ));
    app.insert_resource(Proof {
        tower,
        towed,
        stage: 0,
        frame: 0,
        pending: false,
        acted: false,
        prefix: std::env::var("UG_ROPE_SHOT").unwrap_or_else(|_| "/tmp/bevy-rope-tool".into()),
    });
    app.run();
}

fn advance(
    mut proof: ResMut<Proof>,
    bridge: Res<CoreBridge>,
    mut snapshot: ResMut<GameSnapshot>,
    mut model: ResMut<UiModel>,
    mut events: ResMut<FrameInput>,
    windows: Query<Entity, With<PrimaryWindow>>,
    mut camera: Query<&mut Transform, With<PlayerCamera>>,
    mut bodies: Query<(&mut VehicleBody, &Transform), Without<PlayerCamera>>,
) {
    events.begin();
    // The authority tick and accepted rope mirror, as sync_vehicles would.
    let state = bridge.request("tick", json!({})).unwrap()["state"].clone();
    let rows = state["vehicles"].as_array().cloned().unwrap_or_default();
    for (mut body, _) in &mut bodies {
        let id = body.net_id;
        if let Some(row) = rows.iter().find(|r| r["netId"] == id) {
            body.rope = Tow {
                towed: row["towedNetId"].as_u64().unwrap_or(0) as u32,
                rest: row["towRestLen"].as_f64().unwrap_or(0.) as f32,
                by: rows
                    .iter()
                    .find(|r| r["towedNetId"] == id)
                    .map_or(0, |r| r["netId"].as_u64().unwrap() as u32),
            };
        }
    }
    model.adopt_snapshot(&state);
    snapshot.0 = state;
    if proof.pending {
        return;
    }
    let spec = &data::named("sedan").unwrap().spec;
    let node = |id: u32, rear: bool| {
        bodies
            .iter()
            .find(|(b, _)| b.net_id == id)
            .map(|(_, p)| p.transform_point(tow_local(spec, rear)))
            .unwrap()
    };
    let (tower, towed) = (proof.tower, proof.towed);
    let side = Vec3::new(4.5, 2.2, 0.);
    let (at, from) = match proof.stage {
        0 | 5 => (node(tower, true), node(tower, true) + side),
        // Start on the aimed rear node, then swing the aim to a free end.
        1 if !proof.acted => (node(tower, true), node(tower, true) + side),
        1 => (node(tower, true) + Vec3::new(-1.5, 0.3, 2.5), node(tower, true) + side),
        2 | 3 => (node(towed, false), node(towed, false) + side),
        _ => {
            let mid = (node(tower, true) + node(towed, false)) * 0.5;
            (mid, mid + Vec3::new(11., 4., 0.))
        }
    };
    *camera.single_mut().unwrap() = Transform::from_translation(from).looking_at(at, Vec3::Y);
    if proof.acted {
        return;
    }
    proof.acted = true;
    let window = windows.single().unwrap();
    let mut click = |button: MouseButton, release: bool| {
        events.push(WindowEvent::MouseButtonInput(MouseButtonInput { button, state: ButtonState::Pressed, window }));
        if release {
            events.push(WindowEvent::MouseButtonInput(MouseButtonInput { button, state: ButtonState::Released, window }));
        }
    };
    match proof.stage {
        1 | 3 => click(MouseButton::Left, true),
        5 => click(MouseButton::Right, false),
        4 => {
            for (mut body, _) in &mut bodies {
                if body.net_id == tower {
                    body.controls.throttle = 1.;
                    body.controls.engine = true;
                }
            }
        }
        _ => {}
    }
}
fn capture(
    mut commands: Commands,
    mut proof: ResMut<Proof>,
    tool: Res<RopeTool>,
    bodies: Query<(&VehicleBody, &Transform)>,
) {
    if proof.pending {
        return;
    }
    proof.frame += 1;
    let target = match proof.stage {
        4 => 220,
        5 => 28,
        _ => 30,
    };
    if proof.frame < target {
        return;
    }
    let (tower, towed) = (proof.tower, proof.towed);
    let row = |id: u32| bodies.iter().find(|(b, _)| b.net_id == id).unwrap();
    match proof.stage {
        0 => assert_eq!(tool.hud, "[LMB] start tow", "looked {:?}", tool.looked),
        1 => assert_eq!(tool.start, Some(tower)),
        2 => assert!(tool.hud.ends_with("[LMB] tie"), "{}", tool.hud),
        3 => assert_eq!(row(tower).0.rope.towed, towed, "accepted rope"),
        4 => assert!(
            row(towed).1.translation.z < SPAN + 3. - 5.,
            "towed car pulled: {:?}",
            row(towed).1.translation
        ),
        5 => assert!(tool.hud.starts_with("clearing tow rope... "), "{}", tool.hud),
        _ => unreachable!(),
    }
    let name = STAGES[proof.stage];
    std::fs::write(
        format!("{}-{name}.json", proof.prefix),
        serde_json::to_string_pretty(&json!({
            "stage": name, "hud": tool.hud, "towerRope": row(tower).0.rope.towed, "restLen": row(tower).0.rope.rest,
            "tower": row(tower).1.translation.to_array(), "towed": row(towed).1.translation.to_array(),
            "input": "injected ordered mouse edges; real local authority acceptance",
        }))
        .unwrap(),
    )
    .unwrap();
    proof.pending = true;
    commands
        .spawn(Screenshot::primary_window())
        .observe(save_to_disk(format!("{}-{name}.png", proof.prefix)))
        .observe(
            |_: On<ScreenshotCaptured>, mut proof: ResMut<Proof>, mut exit: MessageWriter<AppExit>| {
                proof.stage += 1;
                proof.frame = 0;
                proof.pending = false;
                proof.acted = false;
                if proof.stage == STAGES.len() {
                    exit.write(AppExit::Success);
                }
            },
        );
}
