use super::*;
use bevy::{
    app::AppExit,
    render::view::screenshot::{Screenshot, ScreenshotCaptured, save_to_disk},
    winit::WinitPlugin,
};

#[derive(Resource)]
struct Capture {
    journey: Journey,
    frame: u32,
    completed: u8,
}

#[test]
#[ignore = "requires Vulkan/display and immutable protocol69 bridge; UG_GLAZING_SHOT sets PNG prefix"]
fn render_actual_udp_original_police_glass_break_burst_and_repair() {
    let mut app = App::new();
    let config = WorldConfig::default();
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
                    title: "Original police glazing / accepted UDP".into(),
                    resolution: (1100, 720).into(),
                    ..default()
                }),
                ..default()
            })
            .set(WinitPlugin {
                run_on_any_thread: true,
                ..default()
            }),
    )
    .add_plugins(RapierPhysicsPlugin::<NoUserData>::default())
    .init_resource::<ContentCache>()
    .insert_resource(config)
    .init_resource::<collision::Shapes>()
    .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(20),
    ))
    .init_resource::<crate::nightvision::NightVision>()
    .add_plugins(crate::hdr::HdrPlugin)
    .add_systems(Update, (collision::install, adopt).chain())
    .insert_resource(ClearColor(Color::srgb(0.12, 0.15, 0.19)));
    let journey = Journey::install(&mut app);
    app.insert_resource(Capture {
        journey,
        frame: 0,
        completed: 0,
    })
    .add_systems(Startup, scene)
    .add_systems(First, advance);
    app.run();
}
fn scene(
    mut commands: Commands,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
    capture: Res<Capture>,
) {
    let center = capture.journey.center;
    commands.spawn((
        Camera3d::default(),
        crate::player::PlayerCamera,
        Transform::from_translation(center + Vec3::new(-2.2, 0.8, -3.5))
            .looking_at(center + Vec3::new(0., -0.1, 0.25), Vec3::Y),
    ));
    commands.insert_resource(GlobalAmbientLight {
        brightness: crate::hdr::ambient_brightness(0.22),
        color: Color::WHITE,
        ..default()
    });
    commands.spawn((
        DirectionalLight {
            illuminance: crate::hdr::directional_illuminance(0.8),
            shadow_maps_enabled: true,
            ..default()
        },
        Transform::from_xyz(-4., 7., -5.).looking_at(Vec3::ZERO, Vec3::Y),
    ));
    commands.spawn((
        Mesh3d(meshes.add(Plane3d::default().mesh().size(30., 30.))),
        MeshMaterial3d(materials.add(StandardMaterial {
            base_color: Color::srgb(0.35, 0.38, 0.40),
            perceptual_roughness: 1.,
            ..default()
        })),
        Transform::IDENTITY,
    ));
    // Original interior and wheel behind the actual transparent pane supply
    // the visual reference. No replacement pane or collider is added.
}
fn advance(world: &mut World) {
    let mut capture = world.remove_resource::<Capture>().unwrap();
    capture.frame += 1;
    if capture.frame == 55 {
        capture.journey.shot(world);
    }
    if capture.frame == 105 {
        capture.journey.repair(world);
    }
    capture.journey.pump(world, 1);
    let label = match capture.frame {
        45 => Some("intact"),
        60 => Some("break-burst"),
        95 => Some("broken"),
        135 => Some("repaired"),
        _ => None,
    };
    if let Some(label) = label {
        let prefix =
            std::env::var("UG_GLAZING_SHOT").unwrap_or_else(|_| "/tmp/bevy-vehicle-glass".into());
        let path = format!("{prefix}-{label}");
        let j = &capture.journey;
        let emitters=world.query::<(&crate::particles::Emitter,&Mesh3d)>().iter(world).map(|(e,m)|json!({
            "amount":e.source_spec().amount,"vertices":world.resource::<Assets<Mesh>>().get(&m.0).map(Mesh::count_vertices)
        })).collect::<Vec<_>>();
        std::fs::write(format!("{path}.json"),serde_json::to_string_pretty(&json!({
            "fixture":"stationary original police; production native geometry upload; normal host Eaglefire shot; actual UDP accepted mask and peer RepairGlass; no trusted direct damage",
            "frame":capture.frame,"peerNetwork":j.peer_state["network"],"vehicle":j.row(),
            "paneVisible":world.get::<Visibility>(j.pane)==Some(&Visibility::Inherited),
            "paneGroups":format!("{:?}",world.get::<CollisionGroups>(j.pane)),"hostAmmo":j.host_state["weapon"]["ammo"],"emitters":emitters,
            "limitation":"plain source tint pending original RainGlass shader; stationary fixture does not validate fullgame driving or bus hinges"
        })).unwrap()).unwrap();
        world
            .spawn(Screenshot::primary_window())
            .observe(save_to_disk(format!("{path}.png")))
            .observe(|_: On<ScreenshotCaptured>, mut c: ResMut<Capture>| c.completed += 1);
    }
    if capture.frame > 145 && capture.completed == 4 {
        world.write_message(AppExit::Success);
    }
    assert!(capture.frame < 350, "all four glazing captures must finish");
    world.insert_resource(capture);
}
