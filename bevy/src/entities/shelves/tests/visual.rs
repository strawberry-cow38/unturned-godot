//! Main.cs UG_SHELFDEMO (Shelf_1 at (0,0,-4.5), a stocked front and its
//! yaw-180 back twin, the source camera) through the production digest ->
//! sync -> offer -> tag path. The gondola body itself is drawn here as
//! StoreShelf.BuildVisual would; in the game it is the map's world object.
use super::*;
use bevy::{
    app::AppExit,
    render::view::screenshot::{Screenshot, ScreenshotCaptured, save_to_disk},
    winit::WinitPlugin,
};

#[derive(Resource)]
struct Shot(String, u32, bool);

#[test]
#[ignore = "Vulkan/display + bridge; UG_SHELF_SHOT=PREFIX, UG_SHELF_FOCUS=item name (tag), UG_SHELF_CAM=top|side|back"]
fn render_source_shelfdemo_through_the_production_display() {
    let config = WorldConfig::default();
    let bridge = bridge();
    let catalog = catalog(&bridge);
    let kind = display_kind(&catalog, "Shelf_1");
    let golden: Vec<Value> = serde_json::from_str(include_str!("../godot_shelfdemo.json")).unwrap();
    let digest = |yaw: i64| {
        golden
            .iter()
            .filter(|g| g["yaw"].as_i64().unwrap().abs() == yaw)
            .map(|g| {
                let key = g["cell"].as_u64().unwrap();
                json!({"cell":(key & 0xff) * 8 + (key >> 8),"itemId":g["id"],"rot":0})
            })
            .collect::<Vec<_>>()
    };
    let rows = json!({"connected":true,"player":{"alive":true},"containers":[
        {"netId":1,"kindId":kind,"position":[0,0,-4.5],"yaw":0,"width":8,"height":3,"display":digest(0)},
        {"netId":2,"kindId":kind,"position":[0,0,-4.5],"yaw":180,"width":8,"height":3,"display":digest(180)}]});
    let mut model = UiModel::default();
    model.adopt_snapshot(&rows);
    let mut app = App::new();
    app.add_plugins(
        DefaultPlugins
            .set(WindowPlugin {
                primary_window: Some(Window { title: "Original store shelf".into(), resolution: (1280, 720).into(), ..default() }),
                ..default()
            })
            .set(AssetPlugin { file_path: config.content_root.canonicalize().unwrap().to_string_lossy().into(), ..default() })
            .set(WinitPlugin { run_on_any_thread: true, ..default() }),
    )
    .add_plugins((crate::content::ContentPlugin, crate::info_billboard::InfoBillboardPlugin, items::material::AlphaDepthPlugin))
    .init_resource::<crate::character::nameplate::OriginalFont>()
    .insert_resource(config.clone())
    .insert_resource(bridge)
    .insert_resource(catalog)
    .insert_resource(GameSnapshot(rows))
    .insert_resource(model)
    .insert_resource(UiState { screen: crate::ui::Screen::Playing, ..default() })
    .init_resource::<WorldSessionEpoch>()
    .init_resource::<InteractionFocus>()
    .init_resource::<items::Models>()
    .init_resource::<ServiceManifest>()
    .init_resource::<Shelves>()
    .init_resource::<FocusedShelfItem>()
    // BuildCrateDemo: colour background, colour ambient (0.6,0.62,0.65) x0.75.
    .insert_resource(ClearColor(Color::srgb(0.42, 0.55, 0.72)))
    .insert_resource(GlobalAmbientLight { color: Color::srgb(0.6, 0.62, 0.65), brightness: 300., ..default() })
    .insert_resource(Shot(std::env::var("UG_SHELF_SHOT").unwrap_or_else(|_| "/tmp/bevy-shelf".into()), 0, false))
    .add_systems(Update, (reset_focus, sync, aim, offer).chain().before(ApplyInteractions))
    .add_systems(Update, (tag.after(ApplyInteractions), capture.after(tag)))
    .configure_sets(Update, ApplyInteractions.after(CollectInteractions));
    let world = app.world_mut();
    let shelf = world.resource_scope(|world, mut cache: Mut<ContentCache>| {
        let mut meshes = world.resource_mut::<Assets<Mesh>>();
        cache.load_mesh(&config.content_root.join("objects/Shelf_1.obj"), &mut meshes).unwrap().0
    });
    // StoreShelf.ShelfMat: the tiny palette texel, Nearest.
    let texture: Handle<Image> = world
        .resource::<AssetServer>()
        .load_builder()
        .with_settings(|s: &mut bevy::image::ImageLoaderSettings| s.sampler = bevy::image::ImageSampler::nearest())
        .load("objects/Shelf_1_tex.png");
    let material = world.resource_mut::<Assets<StandardMaterial>>().add(StandardMaterial {
        base_color_texture: Some(texture),
        perceptual_roughness: 1.,
        double_sided: true,
        cull_mode: None,
        ..default()
    });
    world.spawn((
        Mesh3d(shelf),
        MeshMaterial3d(material),
        Transform::from_xyz(0., 0., -4.5).with_rotation(layout::upright()),
    ));
    let plane = world.resource_mut::<Assets<Mesh>>().add(Plane3d::default().mesh().size(60., 60.));
    let ground = world.resource_mut::<Assets<StandardMaterial>>().add(StandardMaterial {
        base_color: Color::srgb(0.30, 0.34, 0.28),
        ..default()
    });
    world.spawn((Mesh3d(plane), MeshMaterial3d(ground)));
    world.spawn((
        DirectionalLight { illuminance: 6000., ..default() },
        Transform::from_rotation(Quat::from_euler(EulerRot::YXZ, (-46f32).to_radians(), (-52f32).to_radians(), 0.)),
    ));
    world.spawn((
        PointLight { intensity: 400_000., range: 24., ..default() },
        Transform::from_xyz(2., 3., -1.5),
    ));
    let (eye, at) = match std::env::var("UG_SHELF_CAM").as_deref() {
        Ok("top") => (Vec3::new(0.2, 3.9, -2.4), Vec3::new(0., 1.3, -4.6)),
        Ok("side") => (Vec3::new(6.6, 1.5, -4.5), Vec3::new(0., 1.2, -4.5)),
        Ok("back") => (Vec3::new(-3.2, 2.0, -10.4), Vec3::new(0., 1.2, -4.5)),
        _ => (Vec3::new(3.4, 2.0, 1.2), Vec3::new(0., 1.2, -4.5)),
    };
    world.spawn((
        Camera3d::default(),
        bevy::camera::Hdr,
        Projection::Perspective(PerspectiveProjection { fov: 55f32.to_radians(), ..default() }),
        Transform::from_translation(eye).looking_at(at, Vec3::Y),
    ));
    app.run();
}
/// The player's eye, aimed at UG_SHELF_FOCUS's model from 0.9 m in its aisle.
fn aim(
    mut commands: Commands,
    items: Query<(&ShelfItem, &Transform, &ChildOf)>,
    roots: Query<&Transform, With<ShelfRoot>>,
    eye: Query<Entity, With<PlayerCamera>>,
) {
    let Ok(name) = std::env::var("UG_SHELF_FOCUS") else {
        return;
    };
    let Some((item, local, parent)) = items.iter().find(|(i, ..)| i.name == name && i.net_id == 1) else {
        return;
    };
    let root = roots.get(parent.parent()).unwrap();
    let target = item_matrix(root, local).transform_point3(item.center);
    let pose = Transform::from_translation(target + Vec3::new(0., 0.25, 0.9)).looking_at(target, Vec3::Y);
    if let Ok(entity) = eye.single() {
        commands.entity(entity).insert(pose);
    } else {
        commands.spawn((PlayerCamera, pose));
    }
}
fn capture(mut commands: Commands, mut shot: ResMut<Shot>, items: Query<&ShelfItem>, focused: Res<FocusedShelfItem>) {
    shot.1 += 1;
    if shot.2 || shot.1 < 60 {
        return;
    }
    assert_eq!(items.iter().count(), 25);
    if let Ok(name) = std::env::var("UG_SHELF_FOCUS") {
        assert_eq!(focused.0.as_ref().map(|f| f.2.as_str()), Some(name.as_str()), "the look settles on it");
    }
    shot.2 = true;
    commands
        .spawn(Screenshot::primary_window())
        .observe(save_to_disk(format!("{}.png", shot.0)))
        .observe(|_: On<ScreenshotCaptured>, mut exit: MessageWriter<AppExit>| {
            exit.write(AppExit::Success);
        });
}
