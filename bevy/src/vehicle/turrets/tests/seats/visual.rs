//! Small production-mesh/rig fixture for the original tank anchor transforms.
//! Authority transport is tested separately; this fixture supplies accepted rows.
use super::*;
use bevy::{
    app::AppExit,
    camera::visibility::RenderLayers,
    core_pipeline::tonemapping::Tonemapping,
    mesh::skinning::SkinnedMeshInverseBindposes,
    render::view::screenshot::{Screenshot, ScreenshotCaptured, save_to_disk},
    winit::WinitPlugin,
};

#[derive(Resource)]
struct Shot {
    path: String,
    rig: Entity,
    camera: Entity,
    interior: bool,
    gunner: bool,
    yaw: f32,
}

#[test]
#[ignore = "requires Vulkan/display; UG_TANK_SEAT_VIEW=body|driver|gunner, UG_TANK_HEAD_OUT=1, UG_TANK_SEAT_SHOT=path"]
fn render_original_tank_seat() {
    let path =
        std::env::var("UG_TANK_SEAT_SHOT").unwrap_or_else(|_| "/tmp/bevy-tank-seat.png".into());
    let view = std::env::var("UG_TANK_SEAT_VIEW").unwrap_or_else(|_| "body".into());
    let interior = view != "body";
    let gunner = view != "driver";
    let head_out = std::env::var("UG_TANK_HEAD_OUT").as_deref() == Ok("1");
    let yaw = -50_f32.to_radians();
    let config = WorldConfig::default();
    let mut app = App::new();
    app.add_plugins(DefaultPlugins
        .set(AssetPlugin {file_path:config.content_root.canonicalize().unwrap().to_string_lossy().into_owned(), ..default()})
        .set(WindowPlugin {primary_window:Some(Window {resolution:(1280,800).into(), title:format!("Source tank {view} headOut={head_out}"), ..default()}), ..default()})
        .set(WinitPlugin {run_on_any_thread:true, ..default()}))
        .add_plugins((crate::rig::RigPlugin, crate::character::CharacterPlugin))
        .init_resource::<ContentCache>().init_resource::<Occupancy>()
        .insert_resource(config)
        .insert_resource(ClearColor(Color::srgb(0.18,0.21,0.25)))
        .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(std::time::Duration::from_millis(20)))
        .insert_resource(GameSnapshot(json!({"playerId":7,"vehicleMounts":[{"vehicleNetId":1,"headOut":head_out,"gunnerRise":if head_out {1.}else{0.},
            "mounts":[{"index":0,"yaw":yaw.to_degrees(),"pitch":12.,"ammo":100,"cooldownSeconds":0.,"crewAlive":true},
                      {"index":1,"yaw":0.,"pitch":0.,"ammo":100,"cooldownSeconds":0.,"crewAlive":false}]}]})))
        .add_systems(Update,(input::adopt,animate,adopt_occupancy,place).chain().before(crate::rig::RigAnimation))
        .add_systems(Update,capture.after(place));
    let tank = build(&mut app, "tank", Vec3::ZERO);
    app.world_mut().get_mut::<Transform>(tank).unwrap().rotation =
        Quat::from_rotation_y(25_f32.to_radians());
    {
        let mut body = app.world_mut().get_mut::<VehicleBody>(tank).unwrap();
        body.driver_player_id = if gunner { 0 } else { 7 };
        body.passengers = if gunner { vec![7] } else { vec![] };
    }
    let (rig, camera) = app
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
                    Transform::IDENTITY,
                    false,
                    if gunner { "Idle_Sit" } else { "Idle_Drive" },
                )
                .unwrap();
                commands.entity(rig).insert((
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
                        Camera3d::default(),
                        Tonemapping::TonyMcMapface,
                        Msaa::Off,
                        RenderLayers::from_layers(if interior { &[0] } else { &[0, 15] }),
                        Projection::Perspective(PerspectiveProjection {
                            fov: if view == "gunner" && !head_out {
                                9_f32.to_radians()
                            } else {
                                60_f32.to_radians()
                            },
                            ..default()
                        }),
                        Transform::from_xyz(8., 7., -9.)
                            .looking_at(Vec3::new(0., 2.7, 0.), Vec3::Y),
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
                    Mesh3d(meshes.add(Cuboid::new(120., 0.1, 120.))),
                    MeshMaterial3d(materials.add(StandardMaterial {
                        base_color: Color::srgb(0.43, 0.46, 0.47),
                        perceptual_roughness: 1.,
                        ..default()
                    })),
                    Transform::from_xyz(0., -0.55, 0.),
                ));
                for i in -4..=4 {
                    commands.spawn((
                        Mesh3d(meshes.add(Cuboid::new(1., 6., 1.))),
                        MeshMaterial3d(materials.add(StandardMaterial {
                            base_color: if i % 2 == 0 {
                                Color::srgb(0.25, 0.52, 0.68)
                            } else {
                                Color::srgb(0.7, 0.44, 0.22)
                            },
                            ..default()
                        })),
                        Transform::from_xyz(i as f32 * 6., 2.5, -32.),
                    ));
                }
                (rig, camera)
            },
        )
        .unwrap();
    app.insert_resource(Shot {
        path: path.clone(),
        rig,
        camera,
        interior,
        gunner,
        yaw,
    });
    app.run();
    assert!(Path::new(&path).is_file());
}
fn place(shot: Res<Shot>, occupancy: Res<Occupancy>, mut poses: Query<&mut Transform>) {
    let seat = occupancy.seat.unwrap();
    *poses.get_mut(shot.rig).unwrap() = seat.body_pose;
    if shot.interior {
        *poses.get_mut(shot.camera).unwrap() = seat.eye_pose.with_rotation(
            seat.eye_pose.rotation * Quat::from_rotation_y(if shot.gunner { shot.yaw } else { 0. }),
        );
    }
}
fn capture(
    mut frame: Local<u32>,
    mut commands: Commands,
    shot: Res<Shot>,
    occupancy: Res<Occupancy>,
    rigs: Query<&crate::rig::RigPlayback>,
) {
    *frame += 1;
    if *frame != 70 {
        return;
    }
    assert_eq!(
        rigs.get(shot.rig).unwrap().clip,
        if shot.gunner {
            "Idle_Sit"
        } else {
            "Idle_Drive"
        }
    );
    let seat = occupancy.seat.unwrap();
    println!(
        "source seat{} body={:?} eye={:?}",
        seat.seat, seat.body_pose, seat.eye_pose
    );
    commands
        .spawn(Screenshot::primary_window())
        .observe(save_to_disk(shot.path.clone()))
        .observe(
            |_: On<ScreenshotCaptured>, mut exit: MessageWriter<AppExit>| {
                exit.write(AppExit::Success);
            },
        );
}
