//! Opt-in render fixture uses the shipped grass/flower/bush meshes and production
//! forward/shadow shader. Its controlled wind/field inputs are cosmetic fixtures.
use super::*;
use crate::{
    content::{ContentCache, ContentPlugin},
    player::{PlayerCamera, WorldSessionEpoch},
    ui::UiModel,
    world::WorldConfig,
};
use bevy::{
    app::AppExit,
    core_pipeline::tonemapping::Tonemapping,
    render::view::screenshot::{Screenshot, ScreenshotCaptured, save_to_disk},
    winit::WinitPlugin,
};
#[derive(Resource)]
struct Fixture {
    path: String,
    mode: u8,
}
#[test]
#[ignore = "requires a graphics display; writes UG_FOLIAGE_SHOT"]
fn render_original_foliage() {
    let config = WorldConfig::default();
    let path = std::env::var("UG_FOLIAGE_SHOT").unwrap_or_else(|_| "/tmp/bevy-foliage.png".into());
    let mode = std::env::var("UG_FOLIAGE_MODE")
        .ok()
        .and_then(|v| v.parse().ok())
        .unwrap_or(0);
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
                    resolution: (1280, 720).into(),
                    title: "Original foliage motion fixture".into(),
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
    .insert_resource(Fixture {
        path: path.clone(),
        mode,
    })
    .insert_resource(ClearColor(Color::srgb(0.34, 0.47, 0.60)))
    .init_resource::<WorldSessionEpoch>()
    .init_resource::<UiModel>()
    .init_resource::<WorldOrigin>()
    .insert_resource(RainState::default())
    .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(20),
    ))
    .add_plugins((ContentPlugin, FoliagePlugin))
    .add_systems(Startup, scene)
    .add_systems(
        PostUpdate,
        controlled_field.after(disturbance::gather).before(update),
    )
    .add_systems(Update, capture);
    app.run();
    assert!(std::path::Path::new(&path).is_file());
}
fn scene(
    mut commands: Commands,
    config: Res<WorldConfig>,
    mut cache: ResMut<ContentCache>,
    assets: Res<AssetServer>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
) {
    commands.spawn((
        Camera3d::default(),
        bevy::core_pipeline::prepass::DepthPrepass,
        PlayerCamera,
        Tonemapping::TonyMcMapface,
        Msaa::Off,
        Transform::from_xyz(8., 5., 12.).looking_at(Vec3::new(0., 0.4, 0.), Vec3::Y),
    ));
    commands.spawn((
        DirectionalLight {
            illuminance: 8000.,
            shadow_maps_enabled: true,
            ..default()
        },
        Transform::from_xyz(-5., 10., 8.).looking_at(Vec3::ZERO, Vec3::Y),
    ));
    commands.insert_resource(GlobalAmbientLight {
        color: Color::WHITE,
        brightness: 100.,
        ..default()
    });
    commands.spawn((
        Mesh3d(meshes.add(Plane3d::default().mesh().size(26., 22.))),
        MeshMaterial3d(materials.add(StandardMaterial {
            base_color: Color::srgb(0.2, 0.28, 0.08),
            perceptual_roughness: 1.,
            ..default()
        })),
        Transform::from_xyz(0., -0.1, 0.),
    ));
    for (name, kind) in [
        ("foliage/grass_00", Kind::Grass),
        ("foliage/flowers_00", Kind::Flowers),
    ] {
        let (_, data) = cache
            .load_mesh(
                &config.content_root.join(format!("{name}.obj")),
                &mut meshes,
            )
            .unwrap();
        let mut batch = Batch::default();
        for z in -4..4 {
            for x in -5..5 {
                if kind == Kind::Flowers && (x + z) % 5 != 0 {
                    continue;
                }
                batch.append(
                    &data,
                    Transform::from_xyz(
                        x as f32 * 1.05,
                        if kind == Kind::Flowers { 0.05 } else { 0. },
                        z as f32 * 1.1,
                    )
                    .with_rotation(Quat::from_rotation_y((x * 17 + z * 13) as f32)),
                );
            }
        }
        let material = cache.material(
            Some(&format!("{name}_tex.png")),
            true,
            &assets,
            &mut materials,
        );
        commands.spawn((
            Mesh3d(meshes.add(batch.mesh())),
            MeshMaterial3d(material),
            FoliageSurface::ground(name, 160.),
        ));
    }
    for (name, at) in [
        ("resources/Bush_0_0", Vec3::new(-4., 0., -2.)),
        ("resources/Bush_1_0", Vec3::new(2.5, 0., -3.)),
    ] {
        let (mesh, _) = cache
            .load_mesh(
                &config.content_root.join(format!("{name}.obj")),
                &mut meshes,
            )
            .unwrap();
        let material = cache.material(
            Some(&format!("{name}_tex.png")),
            true,
            &assets,
            &mut materials,
        );
        commands.spawn((
            Mesh3d(mesh),
            MeshMaterial3d(material),
            FoliageSurface::canopy(),
            Transform::from_translation(at),
        ));
    }
}
fn controlled_field(
    fixture: Res<Fixture>,
    mut rain: ResMut<RainState>,
    mut field: ResMut<disturbance::Field>,
) {
    // All three renders use the same nonzero wind and source phase, so only the
    // selected source field changes. Flowers intentionally ignore that field.
    rain.wind = Vec4::new(0.8, 0.6, 0.7, 1.5);
    field.slots = match fixture.mode {
        1 => vec![disturbance::Slot {
            position: Vec3::new(0., 0.5, 1.).extend(3.3),
            response: Vec4::new(1.4, 1.4, 0., 3.5),
        }],
        2 => vec![disturbance::Slot {
            position: Vec3::new(-2., 2., 0.).extend(9.),
            response: Vec4::new(6., 1.6, 1., 18.),
        }],
        _ => vec![],
    };
}
fn capture(mut frame: Local<u32>, mut commands: Commands, fixture: Res<Fixture>) {
    *frame += 1;
    if *frame == 70 {
        commands
            .spawn(Screenshot::primary_window())
            .observe(save_to_disk(fixture.path.clone()))
            .observe(
                |_: On<ScreenshotCaptured>, mut exit: MessageWriter<AppExit>| {
                    exit.write(AppExit::Success);
                },
            );
    }
}
