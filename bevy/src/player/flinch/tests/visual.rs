//! Render the production spring, using an explicit accepted-event fixture.
//! This does not claim a playable grenade or mounted authority journey.
use super::*;
use bevy::{
    app::AppExit,
    core_pipeline::tonemapping::Tonemapping,
    render::view::screenshot::{Screenshot, ScreenshotCaptured, save_to_disk},
    winit::WinitPlugin,
};
#[derive(Resource)]
struct Frames(u32);
#[test]
#[ignore = "requires Vulkan/display; UG_FLINCH_SHOT_PREFIX controls three PNG/JSON captures"]
fn render_source_camera_spring() {
    let mut app = App::new();
    let mut model = UiModel::default();
    model.connected = true;
    app.add_plugins(
        DefaultPlugins
            .set(WindowPlugin {
                primary_window: Some(Window {
                    resolution: (1280, 720).into(),
                    title: "Original accepted camera spring".into(),
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
    .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(20),
    ))
    .insert_resource(model)
    .insert_resource(UiState {
        screen: crate::ui::Screen::Playing,
        ..default()
    })
    .init_resource::<WorldSessionEpoch>()
    .init_resource::<PlayerInput>()
    .init_resource::<Perspective>()
    .init_resource::<ItemCatalog>()
    .insert_resource(Frames(0))
    .add_message::<CoreEvent>()
    .add_plugins(FlinchPlugin)
    .add_systems(Startup, scene)
    .add_systems(
        Update,
        (
            reset_camera.in_set(PlayerCameraUpdate),
            source_event.in_set(crate::entities::CoreEventsReady),
            capture.after(FlinchUpdate),
        ),
    );
    app.run();
}
fn scene(
    mut commands: Commands,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
) {
    let pose = Transform::from_xyz(0., 1.75, 0.);
    commands.spawn((
        PlayerCamera,
        Camera3d::default(),
        pose,
        Base(pose),
        Tonemapping::None,
        Msaa::Off,
    ));
    commands.spawn((
        PlayerBody::for_motion_test(Vec3::ZERO, true, "stand"),
        Transform::IDENTITY,
    ));
    commands.spawn((
        Camera2d,
        Camera {
            order: 2,
            clear_color: ClearColorConfig::None,
            ..default()
        },
        IsDefaultUiCamera,
    ));
    commands.spawn((
        Text::new("Accepted blast camera spring - HUD remains upright"),
        TextFont {
            font_size: FontSize::Px(24.),
            ..default()
        },
        TextColor(Color::WHITE),
        Node {
            position_type: PositionType::Absolute,
            left: px(24),
            top: px(24),
            ..default()
        },
    ));
    let mesh = meshes.add(Cuboid::new(2., 2., 0.1));
    let mats = [Color::srgb(0.16, 0.31, 0.37), Color::srgb(0.65, 0.76, 0.79)].map(|base_color| {
        materials.add(StandardMaterial {
            base_color,
            unlit: true,
            ..default()
        })
    });
    for row in -9i32..=9 {
        for column in -14i32..=14 {
            commands.spawn((
                Mesh3d(mesh.clone()),
                MeshMaterial3d(mats[((row + column).unsigned_abs() % 2) as usize].clone()),
                Transform::from_xyz(column as f32 * 2., row as f32 * 2., -18.),
            ));
        }
    }
    let red = materials.add(StandardMaterial {
        base_color: Color::srgb(0.85, 0.17, 0.10),
        unlit: true,
        ..default()
    });
    commands.spawn((
        Mesh3d(meshes.add(Cuboid::new(0.16, 5., 0.3))),
        MeshMaterial3d(red.clone()),
        Transform::from_xyz(0., 1.75, -15.),
    ));
    commands.spawn((
        Mesh3d(meshes.add(Cuboid::new(5., 0.16, 0.3))),
        MeshMaterial3d(red),
        Transform::from_xyz(0., 1.75, -15.),
    ));
}
fn source_event(mut frames: ResMut<Frames>, mut events: MessageWriter<CoreEvent>) {
    frames.0 += 1;
    if frames.0 == 45 {
        events.write(CoreEvent {
            sequence: 1,
            kind: "grenadeExploded".into(),
            data: serde_json::json!({
                "pos":{"x":-5.,"y":1.75,"z":-5.},"radius":8.,"itemId":254
            }),
        });
    }
}
fn capture(
    mut commands: Commands,
    frames: Res<Frames>,
    state: Res<Flinch>,
    camera: Query<&Transform, With<PlayerCamera>>,
    input: Res<PlayerInput>,
) {
    let phase = match frames.0 {
        35 => "baseline",
        45 => "kick",
        130 => "settled",
        _ => return,
    };
    let prefix = std::env::var("UG_FLINCH_SHOT_PREFIX").unwrap_or("/tmp/bevy-flinch".into());
    let pose = camera.single().unwrap();
    let evidence = serde_json::json!({"frame":frames.0,"phase":phase,"flinchDegrees":state.angle_degrees(),"cameraPosition":pose.translation.to_array(),
        "cameraRotation":pose.rotation.to_array(),"aimYaw":input.world_yaw(),"aimPitch":input.pitch(),"fixture":"production spring, injected accepted frag event"});
    std::fs::write(
        format!("{prefix}-{phase}.json"),
        serde_json::to_vec_pretty(&evidence).unwrap(),
    )
    .unwrap();
    println!("[flinch] {evidence}");
    let mut shot = commands.spawn(Screenshot::primary_window());
    shot.observe(save_to_disk(format!("{prefix}-{phase}.png")));
    if phase == "settled" {
        shot.observe(
            |_: On<ScreenshotCaptured>, mut exit: MessageWriter<AppExit>| {
                exit.write(AppExit::Success);
            },
        );
    }
}
