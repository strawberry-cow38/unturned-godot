use super::*;
use bevy::{
    app::AppExit,
    asset::RenderAssetUsages,
    camera::{Viewport, visibility::NoFrustumCulling},
    core_pipeline::tonemapping::{DebandDither, Tonemapping},
    image::{CompressedImageFormats, ImageLoaderSettings, ImageSampler, ImageType},
    light::NotShadowCaster,
    render::view::screenshot::{Screenshot, ScreenshotCaptured, save_to_disk},
    winit::WinitPlugin,
};

#[derive(Resource)]
struct CapturePath(String);

/// This is a simultaneous-view contract, not a stored visual golden. The same
/// source star must face a front, orthogonal and reflected view in one frame.
/// The old main-camera CPU orientation is a deliberately selectable negative
/// control: its orthogonal view has no star and fails the same image assertions.
#[test]
#[ignore = "requires Vulkan/display; UG_FLASH_VIEWS_SHOT=path, optional UG_FLASH_CPU_REFERENCE=1 negative control"]
fn render_source_flash_in_front_side_and_reflected_views() {
    let path = std::env::var("UG_FLASH_VIEWS_SHOT")
        .unwrap_or_else(|_| "/tmp/bevy-mounted-flash-views.png".into());
    let config = crate::world::WorldConfig::default();
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
                    resolution: (1152, 384).into(),
                    title: "Source mounted flash: front / side / reflected".into(),
                    ..default()
                }),
                ..default()
            })
            .set(WinitPlugin {
                run_on_any_thread: true,
                ..default()
            }),
    )
    .add_plugins(FlashMaterialPlugin)
    .insert_resource(ClearColor(Color::BLACK))
    .insert_resource(CapturePath(path.clone()))
    .add_systems(Startup, setup)
    .add_systems(Update, capture);
    app.run();
    let bitmap = Image::from_buffer(
        &std::fs::read(&path).unwrap(),
        ImageType::Extension("png"),
        CompressedImageFormats::NONE,
        true,
        ImageSampler::nearest(),
        RenderAssetUsages::default(),
    )
    .unwrap()
    .try_into_dynamic()
    .unwrap()
    .into_rgb8();
    assert_eq!(bitmap.dimensions(), (1152, 384));
    let mut bounds: Vec<(u32, u32, u32)> = Vec::new();
    for view in 0..3 {
        let (mut count, mut left, mut right, mut top, mut bottom) = (0, 384, 0, 384, 0);
        for y in 0..384 {
            for x in 0..384 {
                let pixel = bitmap.get_pixel(view * 384 + x, y);
                if pixel.0.iter().copied().max().unwrap() < 24 {
                    continue;
                }
                count += 1;
                left = left.min(x);
                right = right.max(x);
                top = top.min(y);
                bottom = bottom.max(y);
            }
        }
        assert!(
            count > 1000,
            "view {view} lost its billboard: {count} pixels"
        );
        bounds.push((count, right - left, bottom - top));
    }
    for view in 1..3 {
        assert!(
            (bounds[view].0 as f32 / bounds[0].0 as f32 - 1.).abs() < 0.03,
            "view {view} must retain source star area: {bounds:?}"
        );
        assert!(
            bounds[view].1.abs_diff(bounds[0].1) <= 2 && bounds[view].2.abs_diff(bounds[0].2) <= 2,
            "view {view} must retain source dimensions: {bounds:?}"
        );
    }
    println!("simultaneous source flash view bounds: {bounds:?}");
}

fn setup(
    mut commands: Commands,
    assets: Res<AssetServer>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<FlashMaterial>>,
    mut plain: ResMut<Assets<StandardMaterial>>,
) {
    let texture = assets
        .load_builder()
        .with_settings(|settings: &mut ImageLoaderSettings| {
            settings.sampler = ImageSampler::linear();
        })
        .load("muzzleflash.png");
    let source = source_material(texture);
    let mut star = commands.spawn((
        Name::new("Same original star in three simultaneous views"),
        Mesh3d(meshes.add(Rectangle::new(2.6, 2.6))),
        Transform::IDENTITY,
        roll_tag(0.61),
        NoFrustumCulling,
        NotShadowCaster,
    ));
    if std::env::var("UG_FLASH_CPU_REFERENCE").as_deref() == Ok("1") {
        star.insert((
            MeshMaterial3d(plain.add(source.base)),
            Transform::from_rotation(Quat::from_rotation_z(-0.61)),
        ));
    } else {
        star.insert(MeshMaterial3d(materials.add(source)));
    }
    for (index, position) in [
        Vec3::new(0., 0., 5.),
        Vec3::new(5., 0., 0.),
        Vec3::new(0., -4., 3.),
    ]
    .into_iter()
    .enumerate()
    {
        commands.spawn((
            Camera3d::default(),
            Camera {
                order: index as isize,
                viewport: Some(Viewport {
                    physical_position: UVec2::new(index as u32 * 384, 0),
                    physical_size: UVec2::splat(384),
                    ..default()
                }),
                ..default()
            },
            Projection::Perspective(PerspectiveProjection {
                fov: 60_f32.to_radians(),
                ..default()
            }),
            Tonemapping::None,
            DebandDither::Disabled,
            Msaa::Off,
            Transform::from_translation(position).looking_at(Vec3::ZERO, Vec3::Y),
        ));
    }
}

fn capture(mut frames: Local<u32>, path: Res<CapturePath>, mut commands: Commands) {
    *frames += 1;
    if *frames != 70 {
        return;
    }
    commands
        .spawn(Screenshot::primary_window())
        .observe(save_to_disk(path.0.clone()))
        .observe(
            |_: On<ScreenshotCaptured>, mut exit: MessageWriter<AppExit>| {
                exit.write(AppExit::Success);
            },
        );
}
