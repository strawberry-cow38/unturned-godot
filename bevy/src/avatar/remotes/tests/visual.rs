//! Opt-in render fixture for the production replica/rig/clothing path. This is
//! fixed replica data, not evidence of a complete multiplayer gameplay journey.
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
#[ignore = "requires a graphics display; writes UG_REMOTE_POSE_SHOT or /tmp/bevy-remote-poses.png"]
fn render_remote_pose_fixture() {
    let config = WorldConfig::default();
    let path = std::env::var("UG_REMOTE_POSE_SHOT")
        .unwrap_or_else(|_| "/tmp/bevy-remote-poses.png".into());
    let mut players = vec![row(1, Vec3::ZERO)];
    let held = std::env::var_os("UG_REMOTE_POSE_HELD").is_some();
    let lamps = std::env::var("UG_REMOTE_POSE_LAMPS").ok();
    let profiles = std::env::var_os("UG_REMOTE_POSE_PROFILES").is_some();
    let beds = std::env::var_os("UG_REMOTE_POSE_BEDS").is_some();
    // UG_REMOTE_POSE_GESTURES=6,11,..: every peer standing, with these replicated gestures
    // (SURRENDER_START 6, ARREST_START 11) through the production overlay.
    let gestures: Option<Vec<u64>> = std::env::var("UG_REMOTE_POSE_GESTURES")
        .ok()
        .map(|v| v.split(',').filter_map(|g| g.trim().parse().ok()).collect());
    for (i, stance) in [0, 2, 3, 4].into_iter().enumerate() {
        let mut peer = row(i as u16 + 2, Vec3::new((i as f32 - 1.5) * 2., 0., 0.));
        peer["stance"] = json!(stance);
        if held {
            peer["heldItemId"] = json!([4, 121, 4, 0][i]);
            peer["attachments"] = json!({"sightId":21,"magazineId":6,"barrelId":7});
        }
        if let Some(mode) = &lamps {
            peer["stance"] = json!(0);
            peer["profile"]["face"] = json!(0);
            peer["worn"]["hat"] = json!(0);
            peer["heldItemId"] = json!(276);
            peer["worn"]["glasses"] = json!([1199, 334, 1044, 0][i]);
            peer["lights"] = json!({"worn":mode == "on", "held":mode == "on"});
        }
        if profiles {
            peer["stance"] = json!(0);
            peer["worn"]["hat"] = json!(0);
            peer["profile"]["face"] = json!(0);
        }
        if let Some(gestures) = &gestures {
            peer["stance"] = json!(0);
            peer["gesture"] = json!(gestures.get(i).copied().unwrap_or(0));
        }
        if beds && i < 2 {
            let yaw = i as f32 * std::f32::consts::FRAC_PI_2;
            let base = Vec3::new((i as f32 - 1.5) * 2., 0., 0.);
            peer["position"] = json!((base + Quat::from_rotation_y(yaw) * Vec3::new(0., 0.45, 1.)).to_array());
            peer["yaw"] = json!(yaw.to_degrees());
            peer["stance"] = json!(4);
            peer["worn"]["hat"] = json!(0);
        }
        players.push(peer);
    }
    let mut services = crate::entities::ServiceManifest::default();
    let seats = if beds {
        services.seats = vec![json!({"netId":9,"recline":true}), json!({"netId":10,"recline":true})];
        json!([{"netId":9,"occupant":2},{"netId":10,"occupant":3}])
    } else { json!([]) };
    let mut app = App::new();
    app.insert_resource(Shot(path.clone()))
        .insert_resource(ClearColor(Color::srgb(0.12, 0.15, 0.19)))
        .add_plugins(
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
                        resolution: (1280, 720).into(),
                        title: "Remote-player production pose fixture".into(),
                        ..default()
                    }),
                    ..default()
                })
                .set(WinitPlugin {
                    run_on_any_thread: true,
                    ..default()
                }),
        )
        .insert_resource(config)
        .init_resource::<WorldSessionEpoch>()
        .insert_resource(source_catalog())
        .insert_resource(services)
        .insert_resource(GameSnapshot(json!({"connected":true,"playerId":1,
            "players":players,"vehicles":[],"interactables":{"seats":seats}})))
        .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
            std::time::Duration::from_millis(20),
        ))
        .add_plugins((
            crate::content::ContentPlugin,
            crate::rig::RigPlugin,
            crate::character::CharacterPlugin,
            RemotePlayersPlugin,
        ))
        .add_systems(Startup, scene)
        .add_systems(Update, fixture_profiles.after(RemotePlayersUpdate))
        .add_systems(Update, capture);
    app.run();
    assert!(
        std::path::Path::new(&path).is_file(),
        "render did not save the fixture"
    );
}

fn scene(
    mut commands: Commands,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
) {
    let side = std::env::var_os("UG_REMOTE_POSE_SIDE").is_some();
    let lamps = std::env::var_os("UG_REMOTE_POSE_LAMPS").is_some();
    commands.spawn((
        Camera3d::default(),
        crate::player::PlayerCamera,
        Tonemapping::TonyMcMapface,
        Msaa::Off,
        Transform::from_xyz(if side { -5. } else { 0. }, 3.5, -10.)
            .looking_at(Vec3::new(0., 0.75, 0.), Vec3::Y),
    ));
    commands.spawn((
        DirectionalLight {
            illuminance: if lamps { 150. } else { 8000. },
            shadow_maps_enabled: true,
            ..default()
        },
        Transform::from_xyz(-3., 6., -5.).looking_at(Vec3::ZERO, Vec3::Y),
    ));
    commands.insert_resource(GlobalAmbientLight {
        color: Color::WHITE,
        brightness: if lamps { 12. } else { 100. },
        ..default()
    });
    commands.spawn((
        Mesh3d(meshes.add(Cuboid::new(12., 0.1, 8.))),
        MeshMaterial3d(materials.add(StandardMaterial {
            base_color: Color::srgb(0.5, 0.5, 0.5),
            perceptual_roughness: 1.,
            ..default()
        })),
        Transform::from_xyz(0., -0.05, 0.),
    ));
    if std::env::var_os("UG_REMOTE_POSE_PROFILES").is_some() {
        commands.spawn((
            Mesh3d(meshes.add(Cuboid::new(1.7, 1.4, 0.2))),
            MeshMaterial3d(materials.add(Color::srgb(0.3, 0.35, 0.4))),
            Transform::from_xyz(3., 2.2, -0.65),
        ));
    }
    if std::env::var_os("UG_REMOTE_POSE_BEDS").is_some() {
        // Original Bed.cs fixture, not the unrelated retail Bed_* prop art.
        let mesh = meshes.add(Cuboid::new(1., 0.45, 2.));
        let material = materials.add(StandardMaterial {
            base_color: Color::srgb(0.55, 0.52, 0.60), perceptual_roughness: 1., ..default()
        });
        for i in 0..2 {
            commands.spawn((Mesh3d(mesh.clone()), MeshMaterial3d(material.clone()),
                Transform::from_xyz((i as f32 - 1.5) * 2., 0.225, 0.)
                    .with_rotation(Quat::from_rotation_y(i as f32 * std::f32::consts::FRAC_PI_2))));
        }
    }
}

fn fixture_profiles(
    mut commands: Commands,
    mut done: Local<bool>,
    players: Query<(Entity, &RemotePlayer)>,
    mut images: ResMut<Assets<Image>>,
) {
    if *done || std::env::var_os("UG_REMOTE_POSE_PROFILES").is_none() || players.iter().count() != 4
    {
        return;
    }
    use crate::character::nameplate::{Nameplate, decode_picture};
    let valid = images.add(decode_picture(
        include_bytes!("../../../character/nameplate/tests/avatar.png"),
        128,
    ));
    let broken = images.add(decode_picture(b"broken PNG", 128));
    for (body, player) in &players {
        let (name, picture) = match player.id {
            2 => ("Ålice", Some(valid.clone())),
            3 => ("No picture", None),
            4 => ("Broken picture", Some(broken.clone())),
            _ => ("Hidden by wall", Some(valid.clone())),
        };
        commands.spawn((
            Nameplate {
                name: name.into(),
                picture,
            },
            Transform::from_xyz(0., Nameplate::HEIGHT, 0.),
            ChildOf(body),
        ));
    }
    *done = true;
}

fn capture(mut frames: Local<u32>, mut commands: Commands, path: Res<Shot>) {
    *frames += 1;
    if *frames == 60 {
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
