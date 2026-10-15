//! Explicit optic presentation fixture, not a mounted firing/authority claim.
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
    zoom: f32,
    mode: Mode,
}
#[test]
#[ignore = "requires Vulkan/display; UG_TANK_OPTIC=periscope|gunsight UG_TANK_ZOOM=8|12|4 UG_TANK_SQUARE=1 UG_TANK_SHOT=png"]
fn render_original_tank_optics() {
    let mode = if std::env::var("UG_TANK_OPTIC").as_deref() == Ok("periscope") {
        Mode::Periscope
    } else {
        Mode::Gunsight
    };
    let index = match std::env::var("UG_TANK_ZOOM").as_deref() {
        Ok("12") => 1,
        Ok("4") => 2,
        _ => 0,
    };
    let square = std::env::var("UG_TANK_SQUARE").as_deref() == Ok("1");
    let path = std::env::var("UG_TANK_SHOT").unwrap_or("/tmp/bevy-tank-optic.png".into());
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
                    resolution: if square {
                        (900, 900).into()
                    } else {
                        (1280, 720).into()
                    },
                    title: "Original tank optics".into(),
                    ..default()
                }),
                ..default()
            })
            .set(WinitPlugin {
                run_on_any_thread: true,
                ..default()
            }),
    )
    .init_resource::<UiState>()
    .init_resource::<Perspective>()
    .init_resource::<WorldSessionEpoch>()
    .init_resource::<Settings>()
    .insert_resource(config)
    .insert_resource(GameSnapshot(
        serde_json::json!({"connected":true,"player":{"alive":true}}),
    ))
    .add_plugins(TankOpticsPlugin)
    .insert_resource(TankOptics { index, ..default() })
    .insert_resource(Shot {
        path: path.clone(),
        zoom: if mode == Mode::Gunsight {
            [8., 12., 4.][index]
        } else {
            1.
        },
        mode,
    })
    .add_systems(Startup, scene)
    .add_systems(Update, reset_camera.before(apply))
    .add_systems(Update, capture.after(apply));
    let body = crate::vehicle::tests::body(&mut app, "tank", Vec3::ZERO);
    app.insert_resource(Occupancy {
        seat: Some(crate::vehicle::OccupiedSeat {
            vehicle: body,
            net_id: 1,
            seat: if mode == Mode::Gunsight { 1 } else { 0 },
            body_pose: default(),
            eye_pose: default(),
            velocity: Vec3::ZERO,
            driver: mode == Mode::Periscope,
            aircraft: false,
        }),
    });
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
        PlayerCamera,
        Tonemapping::None,
        Transform::from_xyz(0., 0., 0.),
        Msaa::Off,
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
        Text::new("HUD above original layer-6 optic"),
        TextFont {
            font_size: FontSize::Px(22.),
            ..default()
        },
        TextColor(Color::WHITE),
        Node {
            position_type: PositionType::Absolute,
            left: px(24),
            bottom: px(24),
            ..default()
        },
    ));
    let mesh = meshes.add(Cuboid::new(5., 5., 0.1));
    let colors = [Color::srgb(0.20, 0.35, 0.42), Color::srgb(0.72, 0.79, 0.81)];
    let mats = colors.map(|c| {
        materials.add(StandardMaterial {
            base_color: c,
            unlit: true,
            ..default()
        })
    });
    for row in -12i32..=12 {
        for col in -20i32..=20 {
            commands.spawn((
                Mesh3d(mesh.clone()),
                MeshMaterial3d(mats[((row + col).unsigned_abs() % 2) as usize].clone()),
                Transform::from_xyz(col as f32 * 5., row as f32 * 5., -100.),
            ));
        }
    }
}
fn capture(
    mut frames: Local<u32>,
    mut commands: Commands,
    shot: Res<Shot>,
    optics: Res<TankOptics>,
    camera: Query<&Projection, With<PlayerCamera>>,
) {
    *frames += 1;
    if *frames != 45 {
        return;
    }
    assert_eq!(optics.mode, shot.mode);
    assert_eq!(optics.zoom(), shot.zoom);
    let Projection::Perspective(p) = camera.single().unwrap() else {
        panic!("perspective")
    };
    assert!((p.fov.to_degrees() - 75. / shot.zoom).abs() < 1e-4);
    println!(
        "[tank-optics] mode={:?} zoom={} fov={}",
        optics.mode,
        optics.zoom(),
        p.fov.to_degrees()
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
