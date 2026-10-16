//! Main.BuildCropTest's showcase through the production replica path: a young
//! (Foliage_0) crop left, a grown (Foliage_1) crop right, on the source dirt
//! plane under its camera. Rows are injected; the authority path is covered by
//! the integrated test.
use super::*;
use bevy::{
    app::AppExit,
    render::view::screenshot::{Screenshot, ScreenshotCaptured, save_to_disk},
    winit::WinitPlugin,
};

#[derive(Resource)]
struct Shot(String, u32, bool);

#[test]
#[ignore = "Vulkan/display; UG_CROP_SHOT=PREFIX, UG_CROP=name"]
fn render_source_crop_showcase_young_left_grown_right() {
    let config = WorldConfig::default();
    let name = std::env::var("UG_CROP").unwrap_or_else(|_| "carrot".into());
    let seed = registry(&std::fs::read_to_string(config.content_root.join("crops.tsv")).unwrap())
        .into_iter()
        .find(|(_, d)| d.name == name)
        .unwrap()
        .0;
    let rows = json!({"connected":true,"crops":[
        {"netId":1,"seedId":seed,"position":[-0.5,0,0],"grown":false},
        {"netId":2,"seedId":seed,"position":[0.5,0,0],"grown":true}]});
    let mut model = UiModel::default();
    model.adopt_snapshot(&rows);
    let mut app = App::new();
    app.add_plugins(
        DefaultPlugins
            .set(WindowPlugin {
                primary_window: Some(Window { title: "Original crop showcase".into(), resolution: (900, 900).into(), ..default() }),
                ..default()
            })
            .set(AssetPlugin { file_path: config.content_root.canonicalize().unwrap().to_string_lossy().into(), ..default() })
            .set(WinitPlugin { run_on_any_thread: true, ..default() }),
    )
    .add_plugins(crate::content::ContentPlugin)
    .insert_resource(config)
    .insert_resource(GameSnapshot(rows))
    .insert_resource(model)
    .init_resource::<WorldSessionEpoch>()
    .init_resource::<Crops>()
    // BuildCropTest: colour background, colour ambient (0.72,0.72,0.74) x1.
    .insert_resource(ClearColor(Color::srgb(0.5, 0.6, 0.72)))
    .insert_resource(GlobalAmbientLight { color: Color::srgb(0.72, 0.72, 0.74), brightness: 400., ..default() })
    .insert_resource(Shot(std::env::var("UG_CROP_SHOT").unwrap_or_else(|_| "/tmp/bevy-crop".into()), 0, false))
    .add_systems(Update, (sync, capture).chain());
    let world = app.world_mut();
    let plane = world.resource_mut::<Assets<Mesh>>().add(Plane3d::default().mesh().size(6., 6.));
    let dirt = world.resource_mut::<Assets<StandardMaterial>>().add(StandardMaterial {
        base_color: Color::srgb(0.38, 0.32, 0.2),
        perceptual_roughness: 1.,
        ..default()
    });
    world.spawn((Mesh3d(plane), MeshMaterial3d(dirt)));
    world.spawn((
        DirectionalLight { illuminance: 9000., ..default() },
        Transform::from_rotation(Quat::from_euler(EulerRot::YXZ, (-40f32).to_radians(), (-50f32).to_radians(), 0.)),
    ));
    world.spawn((
        Camera3d::default(),
        bevy::camera::Hdr,
        Projection::Perspective(PerspectiveProjection { fov: 45f32.to_radians(), ..default() }),
        Transform::from_xyz(0., 0.85, 2.).looking_at(Vec3::new(0., 0.2, 0.), Vec3::Y),
    ));
    app.run();
}
fn capture(mut commands: Commands, mut shot: ResMut<Shot>, crops: Query<&CropReplica>) {
    shot.1 += 1;
    if shot.2 || shot.1 < 40 {
        return;
    }
    let mut grown: Vec<_> = crops.iter().map(|c| (c.net_id, c.grown)).collect();
    grown.sort();
    assert_eq!(grown, [(1, false), (2, true)]);
    shot.2 = true;
    commands
        .spawn(Screenshot::primary_window())
        .observe(save_to_disk(format!("{}.png", shot.0)))
        .observe(|_: On<ScreenshotCaptured>, mut exit: MessageWriter<AppExit>| {
            exit.write(AppExit::Success);
        });
}
