//! Original catalog puppets through production spawning, dressing and focus.
//! These are explicit diagnostic positions, not shipped authored map placements.
use super::*;
use bevy::{
    app::AppExit,
    core_pipeline::tonemapping::Tonemapping,
    render::view::screenshot::{Screenshot, ScreenshotCaptured, save_to_disk},
    winit::WinitPlugin,
};

#[derive(Resource)]
struct Shot(String);

#[test]
#[ignore = "requires Vulkan/display; writes UG_NPC_SHOT or /tmp/bevy-npc-characters.png"]
fn render_original_npc_characters() {
    let config = WorldConfig::default();
    let bridge = CoreBridge::launch(std::path::Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
    let reply = bridge
        .request("init", json!({"contentRoot":config.content_root}))
        .unwrap();
    let catalog = bridge.request("npcCatalog", json!({})).unwrap()["result"].clone();
    let mut services = ServiceManifest {
        map: "NpcVisualFixture".into(),
        ..default()
    };
    for (key, x) in [("Captain", -1.45), ("Chef", 0.), ("Medic", 1.45)] {
        let character = catalog["characters"]
            .as_array()
            .unwrap()
            .iter()
            .find(|c| c["key"] == key)
            .unwrap();
        services
            .npcs
            .push(json!({"key":key,"character":character,"position":[x,0.,0.],"yaw":0.}));
    }
    let mut model = UiModel::default();
    model.adopt_snapshot(&reply["state"]);
    let path =
        std::env::var("UG_NPC_SHOT").unwrap_or_else(|_| "/tmp/bevy-npc-characters.png".into());
    let mut app = App::new();
    app.add_plugins(
        DefaultPlugins
            .set(AssetPlugin {
                file_path: config
                    .content_root
                    .canonicalize()
                    .unwrap()
                    .to_string_lossy()
                    .into_owned(),
                ..default()
            })
            .set(WindowPlugin {
                primary_window: Some(Window {
                    resolution: (1280, 800).into(),
                    title: "Original NPC rigs, hat and look focus".into(),
                    ..default()
                }),
                ..default()
            })
            .set(WinitPlugin {
                run_on_any_thread: true,
                ..default()
            }),
    )
    .add_plugins((
        RigPlugin,
        CharacterPlugin,
        RapierPhysicsPlugin::<NoUserData>::default(),
        NpcPlugin,
    ))
    .insert_resource(bridge)
    .insert_resource(config)
    .insert_resource(model)
    .insert_resource(services)
    .insert_resource(GameSnapshot(reply["state"].clone()))
    .insert_resource(Shot(path.clone()))
    .insert_resource(UiState {
        screen: Screen::Playing,
        ..default()
    })
    .insert_resource(ClearColor(Color::srgb(0.16, 0.19, 0.24)))
    .init_resource::<ContentCache>()
    .init_resource::<WorldSessionEpoch>()
    .init_resource::<PlayerInput>()
    .init_resource::<WorldGeometry>()
    .init_resource::<Settings>()
    .init_resource::<InteractionFocus>()
    .add_message::<UiIntent>()
    .add_systems(
        Update,
        crate::entities::reset_focus.before(CollectInteractions),
    )
    .add_systems(Startup, scene)
    .add_systems(Update, capture);
    app.run();
    assert!(std::path::Path::new(&path).is_file());
}
fn scene(
    mut commands: Commands,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
) {
    commands.spawn((
        Camera3d::default(),
        Projection::Perspective(PerspectiveProjection {
            fov: 75_f32.to_radians(),
            ..default()
        }),
        PlayerCamera,
        Tonemapping::TonyMcMapface,
        Msaa::Off,
        Transform::from_xyz(0., 1.25, -2.55).looking_at(Vec3::new(0., 1.25, 0.), Vec3::Y),
    ));
    commands.spawn((
        PlayerBody::for_motion_test(Vec3::ZERO, true, "stand"),
        Transform::from_xyz(0., 0., -2.55),
    ));
    commands.spawn((
        DirectionalLight {
            illuminance: 10000.,
            shadow_maps_enabled: true,
            ..default()
        },
        Transform::from_xyz(-3., 6., -5.).looking_at(Vec3::ZERO, Vec3::Y),
    ));
    commands.insert_resource(GlobalAmbientLight {
        color: Color::WHITE,
        brightness: 120.,
        ..default()
    });
    commands.spawn((
        Mesh3d(meshes.add(Cuboid::new(8., 0.1, 6.))),
        MeshMaterial3d(materials.add(StandardMaterial {
            base_color: Color::srgb(0.45, 0.48, 0.50),
            perceptual_roughness: 1.,
            ..default()
        })),
        Transform::from_xyz(0., -0.05, 0.),
        Collider::cuboid(4., 0.05, 3.),
        RigidBody::Fixed,
        CollisionGroups::new(Group::GROUP_1, Group::ALL),
    ));
}
fn capture(
    mut frames: Local<u32>,
    mut commands: Commands,
    path: Res<Shot>,
    npcs: Query<&Npc>,
    plates: Query<(&Transform, &NameplateEnabled)>,
) {
    *frames += 1;
    if *frames == 80 {
        assert_eq!(npcs.iter().count(), 3);
        let chef = npcs.iter().find(|n| n.speaker.key == "Chef").unwrap();
        let (transform, gate) = plates.get(chef.plate).unwrap();
        assert!(gate.0, "productionphysicsfocusmustshowchefplate");
        assert!(transform.translation.y > 2.05);
        println!(
            "[npcplate] Chef plateY={} measured={}",
            transform.translation.y, chef.measured
        );
        commands
            .spawn(Screenshot::primary_window())
            .observe(save_to_disk(path.0.clone()))
            .observe(
                |_: On<ScreenshotCaptured>, mut exit: MessageWriter<AppExit>| {
                    exit.write(AppExit::Success);
                },
            );
    }
}
