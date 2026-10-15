//! Isolated original InfoBillboard presentation, including a real occluding
//! world mesh. Gameplay authority/pickup have their own owner/UDP tests.
use super::*;
use bevy::{
    app::AppExit,
    render::view::screenshot::{Screenshot, ScreenshotCaptured, save_to_disk},
    winit::WinitPlugin,
};

#[derive(Resource, Default)]
struct Capture {
    stage: usize,
    frame: usize,
    pending: bool,
}
#[derive(Component)]
struct Wall;
#[test]
#[ignore = "Vulkan/display; UG_INFO_SHOT sets output prefix"]
fn render_original_panel_occlusion_long_text_and_retirement() {
    let mut app = App::new();
    let config = WorldConfig::default();
    app.add_plugins(
        DefaultPlugins
            .set(WindowPlugin {
                primary_window: Some(Window {
                    resolution: (1000, 760).into(),
                    title: "Original InfoBillboard".into(),
                    ..default()
                }),
                ..default()
            })
            .set(AssetPlugin {
                file_path: config
                    .content_root
                    .canonicalize()
                    .unwrap()
                    .to_string_lossy()
                    .into(),
                ..default()
            })
            .set(WinitPlugin {
                run_on_any_thread: true,
                ..default()
            }),
    )
    .insert_resource(config)
    .init_resource::<crate::content::ContentCache>()
    .add_plugins((
        crate::rig::RigPlugin,
        crate::character::CharacterPlugin,
        InfoBillboardPlugin,
    ))
    .init_resource::<Capture>()
    .insert_resource(ClearColor(Color::srgb(0.15, 0.19, 0.23)))
    .add_systems(Startup, scene)
    .add_systems(Update, present.after(reset).before(render))
    .add_systems(Last, capture);
    app.run();
}
fn scene(
    mut commands: Commands,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
) {
    commands.spawn((
        Camera3d::default(),
        Projection::Perspective(PerspectiveProjection {
            fov: 45_f32.to_radians(),
            ..default()
        }),
        bevy::core_pipeline::tonemapping::Tonemapping::None,
        Transform::from_xyz(0., 1., 4.).looking_at(Vec3::Y, Vec3::Y),
    ));
    commands.spawn((
        Wall,
        Mesh3d(meshes.add(Cuboid::new(4., 3., 0.1))),
        MeshMaterial3d(materials.add(StandardMaterial {
            base_color: Color::srgb(0.25, 0.28, 0.31),
            unlit: true,
            ..default()
        })),
        Transform::from_xyz(0., 1., 2.),
        Visibility::Hidden,
    ));
}
fn present(
    mut info: ResMut<FocusedInfo>,
    capture: Res<Capture>,
    mut wall: Query<&mut Visibility, With<Wall>>,
) {
    *wall.single_mut().unwrap() = if capture.stage == 0 {
        Visibility::Hidden
    } else {
        Visibility::Inherited
    };
    info.0 = (capture.stage < 3).then(|| View {
        position: Vec3::Y,
        content: Content {
            name: if capture.stage == 2 {
                "A deliberately long original generator name".into()
            } else {
                "Generator  · 45m".into()
            },
            name_color: Color::srgb(0.82, 0.83, 0.90),
            prompt: if capture.stage == 2 {
                "Picking up... 81%".into()
            } else {
                "[F] Turn Off · Hold [F]: pick up".into()
            },
            prompt_color: Color::srgb(0.82, 0.83, 0.90),
            bars: [
                Some(Bar {
                    value: 0.74,
                    color: Color::srgb(191. / 255., 31. / 255., 31. / 255.),
                }),
                Some(Bar {
                    value: 0.42,
                    color: Color::srgb(220. / 255., 180. / 255., 19. / 255.),
                }),
                Some(Bar {
                    value: 0.60,
                    color: Color::srgb(53. / 255., 192. / 255., 224. / 255.),
                }),
                None,
                None,
            ],
        },
    });
}
fn capture(
    mut commands: Commands,
    mut state: ResMut<Capture>,
    mut exit: MessageWriter<AppExit>,
    surface: Res<Surface>,
    images: Res<Assets<Image>>,
) {
    state.frame += 1;
    assert!(state.frame < 180, "InfoBillboard renderer timed out");
    if state.stage == 4 {
        exit.write(AppExit::Success);
        return;
    }
    if state.pending || state.frame < 18 {
        return;
    }
    state.pending = true;
    let prefix = std::env::var("UG_INFO_SHOT").unwrap_or_else(|_| "/tmp/bevy-info".into());
    if state.stage < 3 {
        images
            .get(surface.image.as_ref().unwrap())
            .unwrap()
            .clone()
            .try_into_dynamic()
            .unwrap()
            .save(format!("{prefix}-canvas-{}.png", state.stage))
            .unwrap();
    }
    commands
        .spawn(Screenshot::primary_window())
        .observe(save_to_disk(format!("{prefix}-{}.png", state.stage)))
        .observe(|_: On<ScreenshotCaptured>, mut state: ResMut<Capture>| {
            state.pending = false;
            state.stage += 1;
            state.frame = 0;
        });
}
