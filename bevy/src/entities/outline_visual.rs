//! Main.BuildItemTest with UG_FOCUS: three dropped items (13, 4, 14), the
//! middle one looked at through the production item focus, outlined by the
//! source OutlineOverlay port. Rows are injected; the bridge supplies the
//! real catalog. Rows sit at their settled rest height (the source harness
//! drops real bodies; nothing settles an injected row), so the AR-15's origin
//! is 0.21 m up, where its 1.15x box rests after the replica's drop pose.
use super::*;
use crate::{outline::OutlinePlugin, player::PlayerBody};
use bevy::{
    app::AppExit,
    camera::RenderTarget,
    render::view::screenshot::{Screenshot, ScreenshotCaptured, save_to_disk},
    winit::WinitPlugin,
};
use bevy_rapier3d::prelude::*;

#[derive(Resource)]
struct Shot(String, u32, bool);

#[test]
#[ignore = "Vulkan/display; UG_OUTLINE_SHOT=PNG"]
fn render_source_item_focus_outline_beside_its_neighbours() {
    let config = WorldConfig::default();
    let bridge = CoreBridge::launch(std::path::Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
    bridge.request("init", json!({"contentRoot":config.content_root,"name":"outline","spawn":[0,0,9]})).unwrap();
    let visuals: Vec<ItemVisual> =
        serde_json::from_value(bridge.request("catalog", json!({})).unwrap()["result"]["items"].clone()).unwrap();
    let mut catalog = ItemCatalog::default();
    catalog.items = visuals.into_iter().map(|i| (i.id, i)).collect();
    let rows = json!({"connected":true,"worldItems":[
        {"netId":1,"itemId":13,"amount":1,"position":[-1.7,0.0,0.0]},
        {"netId":2,"itemId":4,"amount":1,"position":[0.0,0.21,0.0]},
        {"netId":3,"itemId":14,"amount":1,"position":[1.7,0.0,0.0]}]});
    let mut model = UiModel::default();
    model.adopt_snapshot(&rows);
    let mut app = App::new();
    app.add_plugins(
        DefaultPlugins
            .set(WindowPlugin {
                primary_window: Some(Window { title: "Original item outline".into(), resolution: (1280, 720).into(), ..default() }),
                ..default()
            })
            .set(AssetPlugin { file_path: config.content_root.canonicalize().unwrap().to_string_lossy().into(), ..default() })
            .set(WinitPlugin { run_on_any_thread: true, ..default() }),
    )
    .add_plugins((
        RapierPhysicsPlugin::<NoUserData>::default(),
        crate::content::ContentPlugin,
        items::material::AlphaDepthPlugin,
        OutlinePlugin,
    ))
    .insert_resource(config)
    .insert_resource(bridge)
    .insert_resource(catalog)
    .insert_resource(GameSnapshot(rows))
    .insert_resource(model)
    .insert_resource(UiState { screen: crate::ui::Screen::Playing, ..default() })
    .init_resource::<Settings>()
    .init_resource::<ServiceManifest>()
    .init_resource::<Replicas>()
    .init_resource::<items::Models>()
    .init_resource::<InteractionFocus>()
    .insert_resource(ClearColor(Color::srgb(0.29, 0.33, 0.38)))
    .insert_resource(GlobalAmbientLight { brightness: 300., ..default() })
    .insert_resource(Shot(std::env::var("UG_OUTLINE_SHOT").unwrap_or_else(|_| "/tmp/bevy-outline.png".into()), 0, false))
    .add_systems(
        Update,
        (sync_world_items, reset_focus, interact_items, capture).chain().before(ApplyInteractions),
    )
    .configure_sets(Update, ApplyInteractions.after(CollectInteractions))
    .add_systems(Update, apply_marker.in_set(ApplyInteractions));
    let world = app.world_mut();
    let ground = world.resource_mut::<Assets<Mesh>>().add(Cuboid::new(24., 1., 8.));
    let dirt = world.resource_mut::<Assets<StandardMaterial>>().add(StandardMaterial {
        base_color: Color::srgb(0.22, 0.24, 0.22),
        perceptual_roughness: 1.,
        ..default()
    });
    world.spawn((Mesh3d(ground), MeshMaterial3d(dirt), Transform::from_xyz(0., -0.5, 0.), Collider::cuboid(12., 0.5, 4.)));
    world.spawn((
        DirectionalLight { illuminance: 8000., shadow_maps_enabled: true, ..default() },
        Transform::from_xyz(2., 6., 4.).looking_at(Vec3::ZERO, Vec3::Y),
    ));
    world.spawn((PlayerBody::for_motion_test(Vec3::ZERO, true, "stand"), Transform::from_xyz(0., 0., 9.), Collider::capsule_y(0.5, 0.3)));
    // BuildItemTest's camera, aimed at the middle item's rest height.
    world.spawn((
        Camera3d::default(),
        PlayerCamera,
        bevy::camera::Hdr,
        Projection::Perspective(PerspectiveProjection { fov: 52f32.to_radians(), ..default() }),
        Transform::from_xyz(0., 1.5, 5.535).looking_at(Vec3::new(0., 0.04, 0.), Vec3::Y),
    ));
    app.run();
}
fn apply_marker() {}
fn capture(
    mut commands: Commands,
    mut shot: ResMut<Shot>,
    outline: Res<crate::outline::Outline>,
    focus: Res<InteractionFocus>,
    cameras: Query<(&Camera, Option<&RenderTarget>)>,
    visibility: Query<&InheritedVisibility>,
) {
    shot.1 += 1;
    if shot.2 || shot.1 < 90 {
        return;
    }
    let mask = outline.mask.clone().unwrap();
    eprintln!(
        "outline mirrors={} mask={:?} ui={:?} overlay_visible={:?}",
        outline.mirrors.len(),
        cameras.get(mask.camera).map(|(c, t)| (c.is_active, t.is_some())),
        cameras.get(mask.ui).map(|(c, _)| c.is_active),
        visibility.get(mask.overlay)
    );
    commands
        .spawn(Screenshot::image(mask.image.clone()))
        .observe(save_to_disk(shot.0.replace(".png", "-mask.png")));
    assert!(focus.selected("pickupItem", 2), "the middle AR-15 is the looked-at item");
    assert!(outline.target.is_some());
    shot.2 = true;
    commands
        .spawn(Screenshot::primary_window())
        .observe(save_to_disk(shot.0.clone()))
        .observe(|_: On<ScreenshotCaptured>, mut exit: MessageWriter<AppExit>| {
            exit.write(AppExit::Success);
        });
}
