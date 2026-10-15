//! Original ordinary-cabin meshes/rig viewed through the production seated-eye
//! query. Supply a source driver/passenger case; no authority is synthesized here.
use super::*;
use crate::avatar::{LocalAvatar, Perspective, camera::VehicleCameras};
use bevy::{
    app::AppExit,
    camera::visibility::RenderLayers,
    core_pipeline::tonemapping::Tonemapping,
    mesh::skinning::SkinnedMeshInverseBindposes,
    render::view::screenshot::{Screenshot, ScreenshotCaptured, save_to_disk},
    winit::WinitPlugin,
};

#[derive(Resource)]
struct Case {
    path: String,
    camera: Entity,
    yaw: f32,
}
#[test]
#[ignore = "requires Vulkan/display; UG_SEATED_VIEW=driver|left|right|passenger, UG_SEATED_SHOT=path"]
fn render_source_ordinary_seated_camera() {
    let path =
        std::env::var("UG_SEATED_SHOT").unwrap_or_else(|_| "/tmp/bevy-seated-camera.png".into());
    let mode = std::env::var("UG_SEATED_VIEW").unwrap_or_else(|_| "driver".into());
    let index = usize::from(mode == "passenger");
    let yaw = match mode.as_str() {
        "left" => 160.,
        "right" => -160.,
        _ => 0.,
    };
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
                    title: format!("Original jeep {mode} eye"),
                    ..default()
                }),
                ..default()
            })
            .set(WinitPlugin {
                run_on_any_thread: true,
                ..default()
            }),
    )
    .add_plugins((crate::rig::RigPlugin, crate::character::CharacterPlugin))
    .init_resource::<ContentCache>()
    .init_resource::<Perspective>()
    .insert_resource(config)
    .insert_resource(ClearColor(Color::srgb(0.12, 0.18, 0.25)))
    .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(20),
    ))
    .add_systems(Update, place.before(crate::rig::RigAnimation))
    .add_systems(Update, capture.after(place));
    let vehicle = build(&mut app, "jeep", Vec3::ZERO);
    let definition = crate::vehicle::data::named("jeep").unwrap();
    let local = Vec3::from_array(definition.body_seats[index]);
    let fallback = if index == 0 {
        if definition.spec.driver_eye == [0.; 3] {
            Vec3::new(-0.4, 1.85, 0.4)
        } else {
            Vec3::from_array(definition.spec.driver_eye)
        }
    } else {
        Vec3::from_array(definition.seats[index]) + Vec3::Y * 1.05
    };
    app.insert_resource(Occupancy {
        seat: Some(crate::vehicle::OccupiedSeat {
            vehicle,
            net_id: 1,
            seat: index as u8,
            body_pose: Transform::from_translation(local),
            eye_pose: Transform::from_translation(fallback),
            velocity: Vec3::ZERO,
            driver: index == 0,
            aircraft: false,
        }),
    });
    let camera = app
        .world_mut()
        .run_system_once(
            move |mut commands: Commands,
                  mut library: ResMut<crate::rig::RigLibrary>,
                  mut meshes: ResMut<Assets<Mesh>>,
                  mut binds: ResMut<Assets<SkinnedMeshInverseBindposes>>,
                  mut materials: ResMut<Assets<StandardMaterial>>,
                  config: Res<WorldConfig>| {
                let rig = crate::rig::spawn_rig(
                    &mut commands,
                    &mut library,
                    &mut meshes,
                    &mut binds,
                    &config.content_root.join("rig.json"),
                    materials.add(StandardMaterial::default()),
                    Transform::from_translation(local),
                    false,
                    if index == 0 { "Idle_Drive" } else { "Idle_Sit" },
                )
                .unwrap();
                commands.entity(rig).insert((
                    LocalAvatar,
                    crate::character::Appearance {
                        shirt: 24,
                        pants: 2,
                        face: Some(0),
                        ..default()
                    },
                    crate::character::CharacterLayers(RenderLayers::layer(15)),
                ));
                let camera = commands
                    .spawn((
                        crate::player::PlayerCamera,
                        Camera3d::default(),
                        Tonemapping::TonyMcMapface,
                        Msaa::Off,
                        RenderLayers::layer(0),
                        Projection::Perspective(PerspectiveProjection {
                            fov: 60_f32.to_radians(),
                            ..default()
                        }),
                        Transform::default(),
                    ))
                    .id();
                commands.spawn((
                    DirectionalLight {
                        illuminance: 12000.,
                        shadow_maps_enabled: true,
                        ..default()
                    },
                    RenderLayers::from_layers(&[0, 15]),
                    Transform::from_xyz(4., 9., -7.).looking_at(Vec3::ZERO, Vec3::Y),
                ));
                commands.insert_resource(GlobalAmbientLight {
                    color: Color::WHITE,
                    brightness: 140.,
                    ..default()
                });
                commands.spawn((
                    Mesh3d(meshes.add(Cuboid::new(100., 0.1, 100.))),
                    MeshMaterial3d(materials.add(Color::srgb(0.3, 0.35, 0.25))),
                    Transform::from_xyz(0., -0.55, 0.),
                ));
                for i in -3..=3 {
                    for side in [-1., 1.] {
                        commands.spawn((
                            Mesh3d(meshes.add(Cuboid::new(1., 4., 1.))),
                            MeshMaterial3d(materials.add(Color::srgb(
                                0.5 + (i as f32) * 0.025,
                                0.35,
                                0.2,
                            ))),
                            Transform::from_xyz(i as f32 * 4., 1.5, side * 13.),
                        ));
                    }
                }
                camera
            },
        )
        .unwrap();
    app.insert_resource(Case { path, camera, yaw });
    app.run();
}
fn place(
    mut cameras: VehicleCameras,
    seat: Res<Occupancy>,
    view: Res<Perspective>,
    case: Res<Case>,
    time: Res<Time>,
    mut camera: Query<&mut Transform, With<crate::player::PlayerCamera>>,
) {
    if let Some(eye) = cameras.seated_eye(seat.seat, &view, case.yaw, time.delta_secs(), false) {
        if let Ok(mut pose) = camera.get_mut(case.camera) {
            *pose = eye.with_rotation(
                eye.rotation
                    * Quat::from_euler(
                        EulerRot::YXZ,
                        case.yaw.to_radians(),
                        -8.75_f32.to_radians(),
                        0.,
                    ),
            );
        }
    }
}
fn capture(
    mut frames: Local<u32>,
    case: Res<Case>,
    camera: Query<&Transform, With<crate::player::PlayerCamera>>,
    seat: Res<Occupancy>,
    mut commands: Commands,
) {
    *frames += 1;
    if *frames != 70 {
        return;
    }
    let pose = camera.get(case.camera).unwrap();
    std::fs::write(
        std::path::Path::new(&case.path).with_extension("json"),
        serde_json::to_string_pretty(&json!({"yaw":case.yaw,"seat":seat.seat.unwrap().seat,
            "camera":{"position":pose.translation.to_array(),"rotation":pose.rotation.to_array()}}))
        .unwrap(),
    )
    .unwrap();
    commands
        .spawn(Screenshot::primary_window())
        .observe(save_to_disk(case.path.clone()))
        .observe(
            |_: On<ScreenshotCaptured>, mut exit: MessageWriter<AppExit>| {
                exit.write(AppExit::Success);
            },
        );
}

#[test]
fn actual_tank_optics_replace_skull_raise_and_driver_peek() {
    let mut app = app();
    app.add_plugins(crate::rig::RigPlugin)
        .init_asset::<SkinnedMeshInverseBindposes>();
    let vehicle = build(&mut app, "tank", Vec3::ZERO);
    let hull = Transform::from_xyz(12., 3., -7.).with_rotation(Quat::from_euler(
        EulerRot::YXZ,
        0.8,
        -0.1,
        0.2,
    ));
    *app.world_mut().get_mut::<Transform>(vehicle).unwrap() = hull;
    app.world_mut()
        .run_system_once(
            |mut commands: Commands,
             mut library: ResMut<crate::rig::RigLibrary>,
             mut meshes: ResMut<Assets<Mesh>>,
             mut binds: ResMut<Assets<SkinnedMeshInverseBindposes>>,
             mut materials: ResMut<Assets<StandardMaterial>>| {
                let root = crate::rig::spawn_rig(
                    &mut commands,
                    &mut library,
                    &mut meshes,
                    &mut binds,
                    &WorldConfig::default().content_root.join("rig.json"),
                    materials.add(StandardMaterial::default()),
                    Transform::from_xyz(-8., 4., 17.),
                    false,
                    "Idle_Sit",
                )
                .unwrap();
                commands.entity(root).insert(LocalAvatar);
            },
        )
        .unwrap();
    for _ in 0..3 {
        app.update();
    }
    for (index, head_out) in [(0, false), (1, false), (1, true)] {
        let d = crate::vehicle::data::named("tank").unwrap();
        let eye = {
            let mut mounts = app.world_mut().get_mut::<Mounts>(vehicle).unwrap();
            mounts.head_out = head_out;
            mounts.angles[0].x = 70.;
            mounts.seat_eye(d, index).unwrap()
        };
        let seated = crate::vehicle::OccupiedSeat {
            vehicle,
            net_id: 1,
            seat: index as u8,
            body_pose: hull,
            eye_pose: hull.mul_transform(Transform::from_translation(eye)),
            driver: index == 0,
            aircraft: false,
            velocity: Vec3::ZERO,
        };
        let actual = app
            .world_mut()
            .run_system_once(move |mut cameras: VehicleCameras| {
                cameras
                    .seated_eye(Some(seated), &Perspective::default(), 160., 1., false)
                    .unwrap()
            })
            .unwrap();
        assert!(actual.translation.distance(hull.transform_point(eye)) < 1e-5);
        assert!(actual.rotation.abs_diff_eq(hull.rotation, 1e-6));
    }
}
