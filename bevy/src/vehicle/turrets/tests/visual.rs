//! Explicit original-fleet presentation fixture; accepted mount angles are
//! injected here. This does not stand in for a player firing/authority journey.
use super::*;
use bevy::{
    app::AppExit,
    core_pipeline::tonemapping::Tonemapping,
    render::view::screenshot::{Screenshot, ScreenshotCaptured, save_to_disk},
    winit::WinitPlugin,
};

#[derive(Resource)]
struct Shot {
    path: String,
    body: Entity,
    occupied: bool,
}

#[test]
#[ignore = "requires Vulkan/display; UG_TURRET_VEHICLE=tank|hind|huey, UG_TURRET_OCCUPIED=1, UG_TURRET_SHOT=path"]
fn render_original_mounts() {
    let key = match std::env::var("UG_TURRET_VEHICLE").as_deref() {
        Ok("hind") => "hind",
        Ok("huey") => "huey",
        _ => "tank",
    };
    let occupied = std::env::var("UG_TURRET_OCCUPIED").as_deref() == Ok("1");
    let path = std::env::var("UG_TURRET_SHOT").unwrap_or_else(|_| "/tmp/bevy-turrets.png".into());
    let config = WorldConfig::default();
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
                    title: format!("Original {key} mounts"),
                    ..default()
                }),
                ..default()
            })
            .set(WinitPlugin {
                run_on_any_thread: true,
                ..default()
            }),
    )
    .init_resource::<ContentCache>()
    .insert_resource(ClearColor(Color::srgb(0.18, 0.21, 0.25)))
    .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(20),
    ))
    .add_systems(Update, (animate, capture).chain());
    let body = build(&mut app, key, Vec3::ZERO);
    if occupied {
        let mut vehicle = app.world_mut().get_mut::<VehicleBody>(body).unwrap();
        vehicle.driver_player_id = 10;
        vehicle.passengers = vec![20];
    }
    if key == "tank" {
        app.world_mut().get_mut::<Mounts>(body).unwrap().angles = if occupied {
            vec![Vec2::new(30., 12.), Vec2::new(-55., 5.)]
        } else {
            vec![Vec2::ZERO; 2]
        };
    } else {
        let mut mounts = app.world_mut().get_mut::<Mounts>(body).unwrap();
        mounts.crew_alive.fill(true);
        mounts.angles = if key == "hind" {
            vec![Vec2::new(30., -20.)]
        } else {
            vec![Vec2::new(90., -15.), Vec2::new(-90., -15.)]
        };
    }
    app.insert_resource(Shot {
        path: path.clone(),
        body,
        occupied,
    });
    app.world_mut()
        .run_system_once(
            move |mut commands: Commands,
                  mut meshes: ResMut<Assets<Mesh>>,
                  mut materials: ResMut<Assets<StandardMaterial>>| {
                let (eye, target, ground) = if key == "tank" {
                    (Vec3::new(-10., 8., -12.), Vec3::new(0., 2., -0.5), -0.55)
                } else {
                    (Vec3::new(-11., 6., -13.), Vec3::new(0., 1., 0.7), -1.4)
                };
                commands.spawn((
                    Camera3d::default(),
                    Tonemapping::TonyMcMapface,
                    Msaa::Off,
                    Transform::from_translation(eye).looking_at(target, Vec3::Y),
                ));
                commands.spawn((
                    DirectionalLight {
                        illuminance: 12000.,
                        shadow_maps_enabled: true,
                        ..default()
                    },
                    Transform::from_xyz(-5., 9., -6.).looking_at(Vec3::ZERO, Vec3::Y),
                ));
                commands.insert_resource(GlobalAmbientLight {
                    color: Color::WHITE,
                    brightness: 140.,
                    ..default()
                });
                commands.spawn((
                    Mesh3d(meshes.add(Cuboid::new(30., 0.1, 30.))),
                    MeshMaterial3d(materials.add(StandardMaterial {
                        base_color: Color::srgb(0.43, 0.46, 0.47),
                        perceptual_roughness: 1.,
                        ..default()
                    })),
                    Transform::from_xyz(0., ground, 0.),
                ));
            },
        )
        .unwrap();
    app.run();
    assert!(Path::new(&path).is_file());
}
fn capture(
    mut frame: Local<u32>,
    mut commands: Commands,
    shot: Res<Shot>,
    mounts: Query<&Mounts>,
    transforms: Query<&GlobalTransform>,
) {
    *frame += 1;
    if *frame != 70 {
        return;
    }
    let mounts = mounts.get(shot.body).unwrap();
    if let Some(h) = &mounts.hatches {
        assert_eq!(h.top_phase, if shot.occupied { 0. } else { 1. });
        assert_eq!(h.driver_phase, if shot.occupied { 0. } else { 1. });
    }
    for (i, f) in mounts.frames.iter().enumerate() {
        let pose = transforms.get(f.pitch).unwrap();
        println!(
            "[mount] slot{i} pitch-origin={:?} forward={:?}",
            pose.translation(),
            pose.forward()
        );
    }
    commands
        .spawn(Screenshot::primary_window())
        .observe(save_to_disk(shot.path.clone()))
        .observe(
            |_: On<ScreenshotCaptured>, mut exit: MessageWriter<AppExit>| {
                exit.write(AppExit::Success);
            },
        );
}
