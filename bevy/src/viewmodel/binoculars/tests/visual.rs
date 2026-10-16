//! Opt-in Vulkan fixture: the carried pair with its eyepiece PiP, then the pair raised
//! (overlay + main FOV / 4) over a plain range of coloured boards.
use super::*;
use crate::{bridge::CoreBridge, entities::CoreEvent, use_items::HandEvent};
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
const STAGES: [(&str, bool, usize); 2] = [("lowered", false, 60), ("raised", true, 20)];

#[test]
#[ignore = "Vulkan/display; UG_BINOCULARS_SHOT sets the capture prefix"]
fn render_carried_and_raised_binoculars() {
    let bridge = CoreBridge::launch(Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
    bridge.request("init", json!({"contentRoot":WorldConfig::default().content_root,"name":"binoculars"})).unwrap();
    let items: Vec<crate::entities::ItemVisual> =
        serde_json::from_value(bridge.request("catalog", json!({})).unwrap()["result"]["items"].clone()).unwrap();
    let mut catalog = ItemCatalog::default();
    catalog.items = items.into_iter().filter(|i| i.id == 333).map(|i| (i.id, i)).collect();
    let config = WorldConfig::default();
    let mut app = App::new();
    app.add_plugins(
        DefaultPlugins
            .set(WindowPlugin {
                primary_window: Some(Window { title: "Original binoculars".into(), resolution: (1280, 720).into(), ..default() }),
                ..default()
            })
            .set(AssetPlugin {
                file_path: config.content_root.canonicalize().unwrap().to_string_lossy().into(),
                ..default()
            })
            .set(WinitPlugin { run_on_any_thread: true, ..default() }),
    )
    .init_asset::<SkinnedMeshInverseBindposes>()
    .insert_resource(config)
    .init_resource::<crate::world::WorldGeometry>()
    .init_resource::<crate::impact::surfaces::SurfaceTable>()
    .init_resource::<crate::player::WorldSessionEpoch>()
    .init_resource::<ContentCache>()
    .insert_resource(catalog)
    .init_resource::<Settings>()
    .init_resource::<crate::use_items::HandUse>()
    .init_resource::<crate::vehicle::Occupancy>()
    .init_resource::<crate::recoil::Recoil>()
    .init_resource::<crate::avatar::Perspective>()
    .add_message::<CoreEvent>()
    .add_message::<HandEvent>()
    .insert_resource(UiState { screen: Screen::Playing, ..default() })
    .insert_resource(GameSnapshot(json!({"connected":true,"playerId":1,
        "player":{"alive":true,"heldItemId":333,"stance":"stand"},
        "weapon":{"held":{"id":333,"page":2,"x":0,"y":0}}})))
    .insert_resource(ClearColor(Color::srgb(0.55, 0.68, 0.82)))
    .insert_resource(GlobalAmbientLight { brightness: 400., ..default() })
    .add_plugins((crate::rig::RigPlugin, crate::particles::ParticlePlugin, ViewmodelPlugin, crate::ui::tank_optics::TankOpticsPlugin))
    .insert_resource(Shots {
        frame: 0,
        stage: 0,
        pending: false,
        prefix: std::env::var("UG_BINOCULARS_SHOT").unwrap_or_else(|_| "/tmp/bevy-binoculars".into()),
    })
    .add_systems(Startup, scene)
    .add_systems(Last, capture);
    app.run();
}
fn scene(mut commands: Commands, mut meshes: ResMut<Assets<Mesh>>, mut materials: ResMut<Assets<StandardMaterial>>) {
    commands.spawn((Camera3d::default(), PlayerCamera, Transform::from_xyz(0., 1.6, 0.)));
    commands.spawn((
        Camera2d,
        Camera { order: 2, clear_color: ClearColorConfig::None, ..default() },
        IsDefaultUiCamera,
    ));
    commands.spawn((
        DirectionalLight { illuminance: 9000., ..default() },
        Transform::from_xyz(-3., 8., 4.).looking_at(Vec3::ZERO, Vec3::Y),
    ));
    commands.spawn((
        Mesh3d(meshes.add(Plane3d::default().mesh().size(400., 400.))),
        MeshMaterial3d(materials.add(Color::srgb(0.33, 0.45, 0.28))),
    ));
    // Near boards flank the view; far ones sit dead ahead for the zoom.
    let colours = [(0.8, 0.2, 0.2), (0.95, 0.8, 0.2), (0.2, 0.4, 0.9), (0.95, 0.95, 0.95), (0.2, 0.2, 0.2)];
    for (i, (x, z, s)) in [(-6., -15., 3.), (6., -15., 3.), (0., -40., 2.), (-1.2, -40., 1.), (1.2, -40., 1.)].into_iter().enumerate() {
        let (r, g, b) = colours[i];
        commands.spawn((
            Mesh3d(meshes.add(Cuboid::new(s, s, 0.2))),
            MeshMaterial3d(materials.add(Color::srgb(r, g, b))),
            Transform::from_xyz(x, 1.6 + if i > 2 { 1.2 } else { 0. }, z),
        ));
    }
}
fn capture(
    mut commands: Commands,
    mut shots: ResMut<Shots>,
    mut binoculars: ResMut<Binoculars>,
    state: Res<ViewmodelState>,
) {
    if shots.pending || shots.stage >= STAGES.len() {
        return;
    }
    let (name, raised, hold) = STAGES[shots.stage];
    binoculars.force_raise = raised;
    shots.frame += 1;
    if shots.frame < hold || state.gun.is_none() {
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
