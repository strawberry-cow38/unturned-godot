//! Manual render verification of original AlphaDepthPrePass thresholds and art.
//! cargo run --example alpha_depth -- --shot /tmp/alpha.png [--real|--shadow] [--side]
#[path = "../src/content.rs"]
mod content;
#[path = "../src/entities/items/material.rs"]
mod material;
use bevy::{
    app::AppExit,
    asset::RenderAssetUsages,
    core_pipeline::{prepass::DepthPrepass, tonemapping::Tonemapping},
    image::{ImageFilterMode, ImageSampler, ImageSamplerDescriptor},
    prelude::*,
    render::{
        render_resource::{Extent3d, TextureDimension, TextureFormat},
        view::screenshot::{Screenshot, ScreenshotCaptured, save_to_disk},
    },
};
use material::{AlphaDepth, AlphaDepthPlugin};
#[derive(Resource)]
struct Options {
    path: String,
    real: bool,
    side: bool,
    shadow: bool,
}
fn main() {
    let args: Vec<_> = std::env::args().collect();
    let path = args
        .windows(2)
        .find(|v| v[0] == "--shot")
        .map(|v| v[1].clone())
        .unwrap_or("/tmp/alpha-depth.png".into());
    App::new()
        .insert_resource(Options {
            path,
            real: args.contains(&"--real".into()),
            side: args.contains(&"--side".into()),
            shadow: args.contains(&"--shadow".into()),
        })
        .insert_resource(ClearColor(Color::srgb(0.12, 0.15, 0.19)))
        .add_plugins(
            DefaultPlugins
                .set(AssetPlugin {
                    file_path: std::path::Path::new("../game/content")
                        .canonicalize()
                        .unwrap()
                        .to_string_lossy()
                        .into_owned(),
                    ..default()
                })
                .set(WindowPlugin {
                    primary_window: Some(Window {
                        resolution: (1280, 720).into(),
                        title: "AlphaDepthPrePass source validation".into(),
                        ..default()
                    }),
                    ..default()
                }),
        )
        .add_plugins((AlphaDepthPlugin, content::ContentPlugin))
        .add_systems(Startup, setup)
        .add_systems(Update, capture)
        .run();
}
fn image(alpha: u8) -> Image {
    let mut image = Image::new(
        Extent3d {
            width: 2,
            height: 1,
            depth_or_array_layers: 1,
        },
        TextureDimension::D2,
        vec![0, 255, 0, alpha, 255, 0, 0, 255],
        TextureFormat::Rgba8UnormSrgb,
        RenderAssetUsages::default(),
    );
    image.sampler = ImageSampler::Descriptor(ImageSamplerDescriptor {
        mag_filter: ImageFilterMode::Nearest,
        min_filter: ImageFilterMode::Nearest,
        ..default()
    });
    image
}
fn pair() -> content::ObjData {
    let mut data = content::ObjData::default();
    // Front triangles deliberately precede rear triangles in ONE transparent
    // mesh. The rear opaque texel overwrites the front without a depth prepass.
    for (z, uv) in [(0., [0.25, 0.5]), (-0.3, [0.75, 0.5])] {
        for [x, y] in [
            [-0.4, -0.4],
            [0.4, -0.4],
            [0.4, 0.4],
            [-0.4, -0.4],
            [0.4, 0.4],
            [-0.4, 0.4],
        ] {
            data.positions.push([x, y, z]);
            data.normals.push([0., 0., 1.]);
            data.uvs.push(uv);
            data.colors.push([1.; 4]);
        }
    }
    data
}
fn label(commands: &mut Commands, text: &str, left: f32, top: f32) {
    commands.spawn((
        Text::new(text),
        TextFont {
            font_size: FontSize::Px(20.),
            ..default()
        },
        TextColor(Color::WHITE),
        Node {
            position_type: PositionType::Absolute,
            left: px(left),
            top: px(top),
            ..default()
        },
    ));
}
fn setup(
    mut commands: Commands,
    options: Res<Options>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
    mut images: ResMut<Assets<Image>>,
    assets: Res<AssetServer>,
    mut cache: ResMut<content::ContentCache>,
) {
    commands.spawn((
        Camera2d,
        Camera {
            order: 1,
            clear_color: ClearColorConfig::None,
            ..default()
        },
        IsDefaultUiCamera,
    ));
    if options.shadow {
        commands.spawn((
            Camera3d::default(),
            DepthPrepass,
            Tonemapping::TonyMcMapface,
            Msaa::Off,
            Transform::from_xyz(0., 6., 8.).looking_at(Vec3::new(0., 0., -1.), Vec3::Y),
        ));
        commands.spawn((
            DirectionalLight {
                illuminance: 8000.,
                shadow_maps_enabled: true,
                ..default()
            },
            Transform::from_xyz(0., 3., 5.).looking_at(Vec3::ZERO, Vec3::Y),
        ));
        commands.insert_resource(GlobalAmbientLight {
            color: Color::WHITE,
            brightness: 100.,
            ..default()
        });
        commands.spawn((
            Mesh3d(meshes.add(Cuboid::new(9., 0.05, 7.))),
            MeshMaterial3d(materials.add(StandardMaterial {
                base_color: Color::srgb(0.7, 0.7, 0.7),
                perceptual_roughness: 1.,
                ..default()
            })),
            Transform::from_xyz(0., -0.03, -1.),
        ));
        let mut data = pair();
        data.positions.truncate(6);
        data.normals.truncate(6);
        data.uvs.truncate(6);
        data.colors.truncate(6);
        let mesh = meshes.add(data.mesh());
        for (i, alpha) in [13u8, 25, 26, 252, 253, 255].into_iter().enumerate() {
            let texture = images.add(image(alpha));
            let mat = materials.add(StandardMaterial {
                base_color_texture: Some(texture),
                alpha_mode: AlphaMode::Blend,
                cull_mode: None,
                double_sided: true,
                ..default()
            });
            commands.spawn((
                AlphaDepth,
                Mesh3d(mesh.clone()),
                MeshMaterial3d(mat),
                Transform::from_xyz((i as f32 - 2.5) * 1.05, 1., 0.),
            ));
        }
        label(
            &mut commands,
            "Source shadow threshold0.1: first two panels cast no shadow; remaining four do",
            24.,
            24.,
        );
        label(
            &mut commands,
            "Left to right alpha bytes:13,25,26,252,253,255 (out of255)",
            24.,
            54.,
        );
    } else if !options.real {
        commands.spawn((
            Camera3d::default(),
            DepthPrepass,
            Tonemapping::None,
            Msaa::Off,
            Transform::from_xyz(0., 0., 8.).looking_at(Vec3::ZERO, Vec3::Y),
        ));
        let mesh = meshes.add(pair().mesh());
        for (i, alpha) in [13u8, 25, 26, 252, 253, 255].into_iter().enumerate() {
            let texture = images.add(image(alpha));
            let mat = materials.add(StandardMaterial {
                base_color_texture: Some(texture),
                alpha_mode: AlphaMode::Blend,
                unlit: true,
                cull_mode: None,
                double_sided: true,
                ..default()
            });
            let x = (i as f32 - 2.5) * 1.05;
            commands.spawn((
                Mesh3d(mesh.clone()),
                MeshMaterial3d(mat.clone()),
                Transform::from_xyz(x, 0.7, 0.),
            ));
            commands.spawn((
                AlphaDepth,
                Mesh3d(mesh.clone()),
                MeshMaterial3d(mat),
                Transform::from_xyz(x, -0.7, 0.),
            ));
        }
        label(
            &mut commands,
            "Top: Blend only - rear red overwrites every front triangle",
            24.,
            24.,
        );
        label(
            &mut commands,
            "Bottom: source depth prepass - only alpha253/255 and255/255 stay green",
            24.,
            54.,
        );
        label(&mut commands, "Front alpha byte", 24., 620.);
        for (i, alpha) in [13, 25, 26, 252, 253, 255].into_iter().enumerate() {
            let center = 640. + (i as f32 - 2.5) * 1.05 * 360.
                / (8. * (std::f32::consts::PI / 8.).tan());
            label(&mut commands, &alpha.to_string(), center - 15., 620.);
        }
    } else {
        commands.spawn((
            Camera3d::default(),
            DepthPrepass,
            Tonemapping::TonyMcMapface,
            Msaa::Off,
            Transform::from_xyz(if options.side { 4. } else { 0. }, 3.5, 7.)
                .looking_at(Vec3::new(0., 0.5, 0.), Vec3::Y),
        ));
        commands.spawn((
            DirectionalLight {
                illuminance: 8000.,
                shadow_maps_enabled: true,
                ..default()
            },
            Transform::from_xyz(-3., 6., 4.).looking_at(Vec3::ZERO, Vec3::Y),
        ));
        commands.insert_resource(GlobalAmbientLight {
            color: Color::WHITE,
            brightness: 300.,
            ..default()
        });
        let tile = meshes.add(Cuboid::new(1., 0.05, 1.));
        for z in -2..=2 {
            for x in -4..=4 {
                let mat = materials.add(StandardMaterial {
                    base_color: if (x + z) % 2 == 0 {
                        Color::srgb(0.8, 0.8, 0.8)
                    } else {
                        Color::srgb(0.2, 0.25, 0.3)
                    },
                    perceptual_roughness: 1.,
                    ..default()
                });
                commands.spawn((
                    Mesh3d(tile.clone()),
                    MeshMaterial3d(mat),
                    Transform::from_xyz(x as f32, -0.03, z as f32),
                ));
            }
        }
        let root = std::path::Path::new("../game/content/items");
        let manifest: serde_json::Value =
            serde_json::from_slice(&std::fs::read(root.join("items_manifest.json")).unwrap())
                .unwrap();
        for (i, id) in [9301u16, 9317, 9347].into_iter().enumerate() {
            let entry = &manifest[id.to_string()];
            let (mesh, data) = cache
                .load_mesh(&root.join(entry["obj"].as_str().unwrap()), &mut meshes)
                .unwrap();
            let extent = data.max - data.min;
            let scale = 1.2 / extent.max_element();
            let texture = assets
                .load_builder()
                .with_settings(|settings: &mut bevy::image::ImageLoaderSettings| {
                    settings.sampler = ImageSampler::nearest()
                })
                .load(format!("items/{}", entry["tex"].as_str().unwrap()));
            let material = materials.add(StandardMaterial {
                base_color_texture: Some(texture),
                alpha_mode: AlphaMode::Blend,
                perceptual_roughness: if entry["metal"] == true { 0.3 } else { 0.8 },
                cull_mode: None,
                double_sided: true,
                ..default()
            });
            for j in 0..2 {
                let position = Vec3::new(
                    (i as f32 - 1.) * 2.2 + j as f32 * 0.2,
                    0.05 - data.min.y * scale,
                    j as f32 * -0.35,
                );
                commands.spawn((
                    AlphaDepth,
                    Mesh3d(mesh.clone()),
                    MeshMaterial3d(material.clone()),
                    Transform::from_translation(position).with_scale(Vec3::splat(scale)),
                ));
            }
        }
        label(
            &mut commands,
            "Original overlapping Drinking Glass9301 / Pen9317 / Ice9347",
            24.,
            24.,
        );
        label(
            &mut commands,
            "Shared-mesh Blend + depth0.99 + shadow0.1; check both viewpoints",
            24.,
            54.,
        );
    }
}
fn capture(mut frame: Local<u32>, mut commands: Commands, options: Res<Options>) {
    *frame += 1;
    if *frame == 60 {
        commands
            .spawn(Screenshot::primary_window())
            .observe(save_to_disk(options.path.clone()))
            .observe(
                |_: On<ScreenshotCaptured>, mut exit: MessageWriter<AppExit>| {
                    exit.write(AppExit::Success);
                },
            );
    }
}
