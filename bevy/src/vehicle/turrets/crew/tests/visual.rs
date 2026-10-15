//! Explicit accepted-crew presentation fixture; no AI, flight or damage authority.
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
    expected: usize,
    wreck: bool,
}
#[test]
#[ignore = "requires Vulkan/display; UG_CREW_VEHICLE=huey|orca UG_CREW_DEAD=1 UG_CREW_SIDE=1 UG_CREW_WRECK=1 UG_CREW_SHOT=png"]
fn render_original_door_crew() {
    let key = if std::env::var("UG_CREW_VEHICLE").as_deref() == Ok("orca") {
        "orca"
    } else {
        "huey"
    };
    let dead = std::env::var("UG_CREW_DEAD").as_deref() == Ok("1");
    let other = std::env::var("UG_CREW_SIDE").as_deref() == Ok("1");
    let wreck = std::env::var("UG_CREW_WRECK").as_deref() == Ok("1");
    let path = std::env::var("UG_CREW_SHOT").unwrap_or_else(|_| "/tmp/bevy-crew.png".into());
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
                    title: "Original accepted door crew".into(),
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
        crate::rig::RigPlugin,
        crate::character::CharacterPlugin,
        CrewPlugin,
    ))
    .init_resource::<ContentCache>()
    .init_resource::<WorldSessionEpoch>()
    .init_resource::<ItemCatalog>()
    .insert_resource(config)
    .insert_resource(GameSnapshot(json!({"connected":true})))
    .insert_resource(ClearColor(Color::srgb(0.24, 0.27, 0.30)))
    .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(20),
    ))
    .add_systems(
        Update,
        (
            super::super::super::input::adopt,
            super::super::super::animate,
        )
            .chain()
            .in_set(VehicleUpdate),
    )
    .add_systems(
        PostUpdate,
        capture.after(bevy::transform::TransformSystems::Propagate),
    );
    app.world_mut().resource_mut::<ItemCatalog>().npc_crew = Some(definition());
    let body = build(&mut app, key, 17);
    if wreck {
        app.world_mut().get_mut::<VehicleBody>(body).unwrap().flags |= 32;
    }
    let rows = [!dead, true]
        .into_iter()
        .enumerate()
        .map(|(i, alive)| {
            json!({"index":i,
        "yaw":if i==0 {90} else {-90},"pitch":-15,"ammo":400,"cooldownSeconds":0,"crewAlive":alive})
        })
        .collect::<Vec<_>>();
    app.world_mut().resource_mut::<GameSnapshot>().0 = json!({"connected":true,"network":{"mode":"client","ownsWorld":false},
        "vehicleMounts":[{"vehicleNetId":17,"headOut":false,"mounts":rows}]});
    app.insert_resource(Shot {
        path: path.clone(),
        expected: if dead { 1 } else { 2 },
        wreck,
    });
    app.world_mut()
        .run_system_once(
            move |mut commands: Commands,
                  mut meshes: ResMut<Assets<Mesh>>,
                  mut materials: ResMut<Assets<StandardMaterial>>| {
                let side = if other { 1. } else { -1. };
                commands.spawn((
                    Camera3d::default(),
                    Tonemapping::TonyMcMapface,
                    Msaa::Off,
                    Transform::from_xyz(side * 4.7, 2.1, -3.1)
                        .looking_at(Vec3::new(side * 1.0, 1., -0.26), Vec3::Y),
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
                    Transform::from_xyz(0., -1.4, 0.),
                ));
                commands.entity(body).insert(Visibility::Inherited);
            },
        )
        .unwrap();
    app.run();
    assert!(std::path::Path::new(&path).is_file());
}
fn capture(
    mut frame: Local<u32>,
    mut commands: Commands,
    shot: Res<Shot>,
    crew: Query<(&Crew, &RigPlayback, &Transform, &GlobalTransform)>,
    bodies: Query<&VehicleBody>,
    skins: Query<&MeshMaterial3d<StandardMaterial>>,
    materials: Res<Assets<StandardMaterial>>,
    rig_skins: Query<&crate::rig::RigSkin, With<Crew>>,
) {
    *frame += 1;
    if *frame != 70 {
        return;
    }
    assert_eq!(crew.iter().count(), shot.expected);
    if shot.wreck {
        assert!(bodies.iter().all(|body| body.flags & 32 != 0));
        for skin in &rig_skins {
            let material = materials.get(&skins.get(skin.0).unwrap().0).unwrap();
            assert_eq!(material.base_color, Color::srgb(0.05, 0.05, 0.05));
        }
    }
    for (crew, rig, local, world) in &crew {
        assert!(crew.attached);
        assert!(rig.clip.is_empty() && rig.paused);
        println!(
            "[crew] held={} bind=true local={:?} world={:?}",
            crew.gun,
            local.translation,
            world.translation()
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
