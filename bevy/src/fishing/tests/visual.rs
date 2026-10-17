//! Opt-in Vulkan fixture: the source bobber and line on open water, floating on the line and
//! tugged under in the catch window (SpawnBobber / UpdateFishLine / TickFishing).
use super::*;
use bevy::{
    app::AppExit,
    render::view::screenshot::{Screenshot, ScreenshotCaptured, save_to_disk},
    winit::WinitPlugin,
};

#[derive(Resource)]
struct Shots {
    frame: usize,
    stage: usize,
    pending: bool,
    prefix: String,
}
/// (name, server state adopted, seconds since the bite).
const STAGES: [(&str, u8, f32); 2] = [("line", LINE, 0.), ("bite", BITE, 0.5)];

#[test]
#[ignore = "Vulkan/display; UG_FISHING_SHOT sets the capture prefix"]
fn render_bobber_and_line_on_open_water() {
    let config = WorldConfig::default();
    let rod: crate::entities::ItemVisual = serde_json::from_value(json!({"id":ROD,"name":"Fishing Rod","type":"fisher",
        "asset":null,"mesh":null,"texture":null,"icon":null,"handKind":"fisher"})).unwrap();
    let mut catalog = ItemCatalog::default();
    catalog.items.insert(ROD, rod);
    let mut app = App::new();
    app.add_plugins(
        DefaultPlugins
            .set(WindowPlugin {
                primary_window: Some(Window { title: "Original fishing".into(), resolution: (1280, 720).into(), ..default() }),
                ..default()
            })
            .set(AssetPlugin {
                file_path: config.content_root.canonicalize().unwrap().to_string_lossy().into(),
                ..default()
            })
            .set(WinitPlugin { run_on_any_thread: true, ..default() }),
    )
    .insert_resource(catalog)
    .insert_resource(GameSnapshot(json!({"connected":true,"player":{"alive":true,"heldItemId":ROD},
        "weapon":{"held":{"id":ROD,"page":2,"x":0,"y":0}}})))
    .insert_resource({
        let mut model = UiModel::default();
        model.connected = true;
        model
    })
    .insert_resource(UiState { screen: Screen::Playing, ..default() })
    .init_resource::<Settings>()
    .init_resource::<FrameInput>()
    .init_resource::<WorldSessionEpoch>()
    .insert_resource(WorldState { sea_level: Some(0.), ..default() })
    .add_message::<CoreEvent>()
    .insert_resource(ClearColor(Color::srgb(0.55, 0.68, 0.82)))
    .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(std::time::Duration::from_millis(20)))
    .insert_resource(GlobalAmbientLight { brightness: 400., ..default() })
    .add_plugins(FishingPlugin)
    .insert_resource(Shots {
        frame: 0,
        stage: 0,
        pending: false,
        prefix: std::env::var("UG_FISHING_SHOT").unwrap_or_else(|_| "/tmp/bevy-fishing".into()),
    })
    .add_systems(Startup, scene)
    .add_systems(Last, capture);
    app.run();
}
fn scene(mut commands: Commands, mut meshes: ResMut<Assets<Mesh>>, mut materials: ResMut<Assets<StandardMaterial>>) {
    commands.spawn((
        Camera3d::default(),
        PlayerCamera,
        Transform::from_xyz(0., 1.8, 0.).looking_at(Vec3::new(0., 0., -6.), Vec3::Y),
    ));
    commands.spawn((
        DirectionalLight { illuminance: 9000., ..default() },
        Transform::from_xyz(-3., 8., 4.).looking_at(Vec3::ZERO, Vec3::Y),
    ));
    // A pier to stand on and the open sea beyond it.
    commands.spawn((
        Mesh3d(meshes.add(Cuboid::new(3., 0.4, 3.))),
        MeshMaterial3d(materials.add(Color::srgb(0.45, 0.33, 0.22))),
        Transform::from_xyz(0., 0.0, 0.),
    ));
    commands.spawn((
        Mesh3d(meshes.add(Plane3d::default().mesh().size(400., 400.))),
        MeshMaterial3d(materials.add(StandardMaterial {
            base_color: Color::srgba(0.13, 0.29, 0.44, 0.74),
            alpha_mode: AlphaMode::Blend,
            perceptual_roughness: 0.12,
            ..default()
        })),
    ));
}
fn capture(mut commands: Commands, mut shots: ResMut<Shots>, mut fishing: ResMut<Fishing>) {
    if shots.pending || shots.stage >= STAGES.len() {
        return;
    }
    let (name, state, bite) = STAGES[shots.stage];
    shots.frame += 1;
    if shots.frame == 2 {
        // A cast that landed 6 m out, then the server's answer.
        fishing.held = rod();
        fishing.bobber = Vec3::new(0.4, 0., -6.);
        fishing.phase = Phase::Cast;
        fishing.adopt(state);
        if let Phase::Line { bite: Some(t) } = &mut fishing.phase {
            *t = bite;
        }
    }
    if shots.frame < 60 {
        return;
    }
    shots.pending = true;
    commands
        .spawn(Screenshot::primary_window())
        .observe(save_to_disk(format!("{}-{name}.png", shots.prefix)))
        .observe(|_: On<ScreenshotCaptured>, mut shots: ResMut<Shots>, mut exit: MessageWriter<AppExit>| {
            shots.stage += 1;
            shots.frame = 0;
            shots.pending = false;
            if shots.stage == STAGES.len() {
                exit.write(AppExit::Success);
            }
        });
}
