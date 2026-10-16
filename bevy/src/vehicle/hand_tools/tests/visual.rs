//! Source spraypaint, spare tire and carjack on real sedan meshes. Injected
//! ordered LMB edges against a real local authority; accepted rows drive the
//! native paint/tire presentation and the host shove. Not a full-game journey.
use super::*;
use bevy::{
    app::AppExit,
    render::view::screenshot::{Screenshot, ScreenshotCaptured, save_to_disk},
    winit::WinitPlugin,
};

#[derive(Resource)]
struct Proof {
    car: u32,
    reference: u32,
    stage: usize,
    frame: usize,
    pending: bool,
    acted: bool,
    last_event: u64,
    lift: f32,
    prefix: String,
}
/// Turquoise (1842 = 25C891) contrasts with the random-hue spawn paint.
const TURQUOISE: u16 = 1842;
const STAGES: [&str; 5] = ["spawn-paint", "sprayed", "flat", "refitted", "jacked"];

#[test]
#[ignore = "Vulkan/display; UG_HAND_TOOLS_SHOT sets capture prefix"]
fn render_source_spraypaint_spare_and_carjack_on_accepted_state() {
    let bridge = CoreBridge::launch(Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
    bridge.request("init", json!({"contentRoot":WorldConfig::default().content_root,"name":"hand-tools-visual","spawn":[3,0,1],"allowCheats":true})).unwrap();
    let sedan = data::named("sedan").unwrap();
    let fleet = bridge
        .request("worldVehicles", json!({"vehicles":[
            registration(sedan, "sprayed".into(), Vec3::new(0., 0.9, 0.), 0., 3),
            registration(sedan, "reference".into(), Vec3::new(-4., 0.9, 0.), 0., 3)]}))
        .unwrap();
    let id = |i: usize| fleet["result"]["vehicles"][i]["netId"].as_u64().unwrap() as u32;
    let (car, reference) = (id(0), id(1));
    for item in [TURQUOISE, SPARE, JACK] {
        bridge.request("action", json!({"name":"console","args":{"text":format!("give {item}")}})).unwrap();
    }
    let visuals: Vec<ItemVisual> = serde_json::from_value(bridge.request("catalog", json!({})).unwrap()["result"]["items"].clone()).unwrap();
    let mut catalog = ItemCatalog::default();
    catalog.items = visuals.into_iter().map(|i| (i.id, i)).collect();
    let config = WorldConfig::default();
    let mut app = App::new();
    app.add_plugins(
        DefaultPlugins
            .set(WindowPlugin {
                primary_window: Some(Window {
                    title: "Original vehicle hand tools".into(),
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
    .add_plugins(RapierPhysicsPlugin::<NoUserData>::default())
    .insert_resource(config)
    .insert_resource(bridge)
    .insert_resource(catalog)
    .insert_resource(GameSnapshot(Value::Null))
    .init_resource::<UiModel>()
    .insert_resource(UiState { screen: Screen::Playing, ..default() })
    .init_resource::<Settings>()
    .init_resource::<FrameInput>()
    .init_resource::<ContentCache>()
    .init_resource::<WorldSessionEpoch>()
    .init_resource::<crate::vehicle::Vehicles>()
    .init_resource::<HandTools>()
    .init_resource::<PendingJacks>()
    .init_resource::<LookedVehicle>()
    .add_message::<HandEvent>()
    .add_message::<CoreEvent>()
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
    .add_systems(Update, (advance, input, read_jacks, repaint, crate::vehicle::tires::update).chain())
    .add_systems(
        PostUpdate,
        (crate::vehicle::step_bodies, apply_jacks).chain().before(PhysicsSet::SyncBackend),
    )
    .add_systems(Last, capture);
    for (id, x) in [(car, 0.), (reference, -4.)] {
        let e = body(&mut app, "sedan", Vec3::new(x, 0.9, 0.));
        {
            let mut b = app.world_mut().get_mut::<VehicleBody>(e).unwrap();
            b.net_id = id;
            b.variant = 3;
            b.driver_player_id = 0;
            b.controls = default();
        }
        app.world_mut().entity_mut(e).insert(Visibility::Inherited);
        app.world_mut().resource_mut::<crate::vehicle::Vehicles>().entities.insert(id, e);
        if id == car {
            app.insert_resource(LookedVehicle(Some(e)));
        }
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
                        3,
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
        crate::player::PlayerCamera,
        bevy::camera::Hdr,
        Projection::Perspective(PerspectiveProjection { fov: 60_f32.to_radians(), ..default() }),
        Transform::from_xyz(6.5, 3.2, 7.5).looking_at(Vec3::new(-2., 0.7, 0.), Vec3::Y),
    ));
    app.insert_resource(Proof {
        car,
        reference,
        stage: 0,
        frame: 0,
        pending: false,
        acted: false,
        last_event: 0,
        lift: 0.,
        prefix: std::env::var("UG_HAND_TOOLS_SHOT").unwrap_or_else(|_| "/tmp/bevy-hand-tools".into()),
    });
    app.run();
}

fn cell_of(state: &Value, id: u16) -> Option<(Value, Value, Value)> {
    state["inventory"]["pages"].as_array()?.iter().find_map(|p| {
        p["items"].as_array()?.iter().find(|i| i["id"] == id).map(|i| (p["index"].clone(), i["x"].clone(), i["y"].clone()))
    })
}
#[allow(clippy::too_many_arguments)]
fn advance(
    mut proof: ResMut<Proof>,
    bridge: Res<CoreBridge>,
    mut snapshot: ResMut<GameSnapshot>,
    mut model: ResMut<UiModel>,
    mut events: ResMut<FrameInput>,
    mut core: MessageWriter<CoreEvent>,
    windows: Query<Entity, With<PrimaryWindow>>,
    mut bodies: Query<(&mut VehicleBody, &Transform, &Velocity)>,
) {
    events.begin();
    // The authority tick and the accepted rows, as sync_vehicles adopts them.
    let state = bridge.request("tick", json!({})).unwrap()["state"].clone();
    let rows = state["vehicles"].as_array().cloned().unwrap_or_default();
    for (mut body, pose, velocity) in &mut bodies {
        let id = body.net_id;
        if let Some(row) = rows.iter().find(|r| r["netId"] == id) {
            body.paint_rgb = row["paintRgb"].as_u64().map(|v| v as u32);
            body.popped_tire_mask = row["poppedTireMask"].as_u64().unwrap_or(0) as u8;
        }
        if id == proof.car && proof.stage == 4 {
            proof.lift = proof.lift.max(pose.translation.y);
            let _ = velocity;
        }
    }
    for event in state["events"].as_array().into_iter().flatten() {
        let seq = event["seq"].as_u64().unwrap_or(0);
        if seq > proof.last_event {
            proof.last_event = seq;
            core.write(CoreEvent { sequence: seq, kind: event["kind"].as_str().unwrap_or("").into(), data: event["data"].clone() });
        }
    }
    model.adopt_snapshot(&state);
    snapshot.0 = state.clone();
    if proof.pending || proof.acted {
        return;
    }
    let hold = |id: u16| {
        let cell = cell_of(&state, id).expect("item in the bag");
        bridge.request("action", json!({"name":"holdItem","args":{"page":cell.0,"x":cell.1,"y":cell.2}})).unwrap();
    };
    let window = windows.single().unwrap();
    let mut click = || {
        events.push(WindowEvent::MouseButtonInput(MouseButtonInput { button: MouseButton::Left, state: ButtonState::Pressed, window }));
        events.push(WindowEvent::MouseButtonInput(MouseButtonInput { button: MouseButton::Left, state: ButtonState::Released, window }));
    };
    // Each stage acts once after the hold has been accepted (a couple of frames).
    match (proof.stage, proof.frame) {
        (1, 1) => hold(TURQUOISE),
        (1, 4) => { click(); proof.acted = true; }
        (2, 1) => {
            let car = proof.car;
            bridge.request("worldVehicleDamage", json!({"netId":car,"bodyPart":"tire:1","amount":50})).unwrap();
            proof.acted = true;
        }
        (3, 1) => hold(SPARE),
        (3, 4) => { click(); proof.acted = true; }
        (4, 1) => hold(JACK),
        (4, 4) => { click(); proof.acted = true; }
        _ => {}
    }
}
fn capture(mut commands: Commands, mut proof: ResMut<Proof>, bodies: Query<(&VehicleBody, &Transform, &BodyPaint)>) {
    if proof.pending {
        return;
    }
    proof.frame += 1;
    // Spray 2.8 s, spare 2.8 s (on at 75%), jack 1.967 s (lift at 75%) + flight.
    let target = [40, 170, 40, 170, 102][proof.stage];
    if proof.frame < target {
        return;
    }
    let (car, reference) = (proof.car, proof.reference);
    let row = |id: u32| bodies.iter().find(|(b, ..)| b.net_id == id).unwrap();
    match proof.stage {
        0 => assert_eq!(row(car).0.paint_rgb, None),
        1 => {
            assert_eq!(row(car).0.paint_rgb, Some(0x25c891), "accepted respray");
            assert_eq!(row(car).2.rgb, Some(0x25c891), "native body repainted");
            assert_eq!(row(reference).2.rgb, None, "the other car keeps its spawn paint");
        }
        2 => assert_eq!(row(car).0.popped_tire_mask, 2),
        3 => assert_eq!(row(car).0.popped_tire_mask, 0, "accepted refit"),
        4 => assert!(proof.lift > 1.6, "host shove lifted the car: peak y {}", proof.lift),
        _ => unreachable!(),
    }
    let name = STAGES[proof.stage];
    std::fs::write(
        format!("{}-{name}.json", proof.prefix),
        serde_json::to_string_pretty(&json!({
            "stage": name, "paintRgb": row(car).0.paint_rgb, "rendered": row(car).2.rgb,
            "poppedTireMask": row(car).0.popped_tire_mask, "car": row(car).1.translation.to_array(), "peakY": proof.lift,
            "input": "injected ordered LMB edges; real local authority acceptance",
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
