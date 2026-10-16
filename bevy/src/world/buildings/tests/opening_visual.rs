//! Isolated opening rig, paired with building_openings_reference.py's actual
//! Godot DoorDeploy/ObjectDoor/GlassPane rig. Walls were proved separately.
use super::*;
use crate::world::buildings::openings;
use bevy::{
    app::AppExit,
    core_pipeline::tonemapping::Tonemapping,
    render::view::screenshot::{Screenshot, ScreenshotCaptured, save_to_disk},
    winit::WinitPlugin,
};
#[derive(Resource, Default)]
struct Capture {
    frame: u32,
    done: usize,
    images: Vec<Vec<u8>>,
    prefix: std::path::PathBuf,
}
#[test]
#[ignore = "requires Vulkan/display; UG_BUILDING_OPENINGS_SHOT=output-prefix"]
fn render_original_fitted_openings_closed_half_open_and_rain() {
    let mut text = String::new();
    for (i, action) in ["swing", "double", "pocket"].iter().enumerate() {
        text += &format!(
            "wall {} 0 0 0 4 .7 0 4 0 Wall\n  open .5 0 2.5 2.85 999 0 0 0 0 0 0 Door_Pine 0 swing start front action {action}\n",
            -6 + i as i32 * 4
        );
    }
    text += "wall 0 0 0 0 12 .7 0 4 0 Wall\n open 6.8 .5 2.4 2.6 999 0 1 0 1 0 0\n";
    let mut view = source(json!({"text":text}));
    for wall in &mut view.walls {
        wall.front.triangles.clear();
        wall.back.triangles.clear();
        wall.trim.triangles.clear();
        wall.solids.shapes.clear();
        wall.trim_solids.shapes.clear();
    }
    let prefix = std::env::var_os("UG_BUILDING_OPENINGS_SHOT")
        .map(std::path::PathBuf::from)
        .unwrap_or_else(|| "/tmp/bevy-building-openings".into());
    let mut app = App::new();
    app.add_plugins(
        DefaultPlugins
            .set(WindowPlugin {
                primary_window: Some(Window {
                    resolution: (1280, 800).into(),
                    title: "Original fitted opening rig".into(),
                    ..default()
                }),
                ..default()
            })
            .set(WinitPlugin {
                run_on_any_thread: true,
                ..default()
            }),
    )
    .add_plugins(BuildingOpeningPlugin)
    .insert_resource(ClearColor(Color::srgb(0.24, 0.27, 0.30)))
    .insert_resource(GlobalAmbientLight {
        color: Color::WHITE,
        brightness: 140.,
        ..default()
    })
    .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(20),
    ))
    .init_resource::<crate::world::rain::RainState>()
    .insert_resource(Capture {
        prefix: prefix.clone(),
        ..default()
    })
    .add_systems(Update, advance);
    replace(app.world_mut(), &view).unwrap();
    app.world_mut().spawn((
        Camera3d::default(),
        Projection::Perspective(PerspectiveProjection {
            fov: 50_f32.to_radians(),
            ..default()
        }),
        Tonemapping::AcesFitted,
        Msaa::Sample4,
        Transform::from_xyz(12., 6., 16.).looking_at(Vec3::new(2., 1.5, 0.), Vec3::Y),
    ));
    app.world_mut().spawn((
        DirectionalLight {
            illuminance: 12000.,
            shadow_maps_enabled: true,
            ..default()
        },
        Transform::from_xyz(-10., 20., 10.).looking_at(Vec3::ZERO, Vec3::Y),
    ));
    box_mesh(
        app.world_mut(),
        Vec3::new(2., -0.15, 0.),
        Vec3::new(24., 0.3, 12.),
        Color::srgb(0.43, 0.46, 0.47),
    );
    for y in 0..8 {
        for x in 0..8 {
            box_mesh(
                app.world_mut(),
                Vec3::new(6.8 + x as f32 * 0.34, 0.6 + y as f32 * 0.34, -0.5),
                Vec3::new(0.34, 0.34, 0.1),
                if (x + y) % 2 == 0 {
                    Color::srgb(0.8, 0.72, 0.2)
                } else {
                    Color::srgb(0.15, 0.3, 0.6)
                },
            );
        }
    }
    app.run();
    for stage in ["closed", "half", "open", "wet"] {
        assert!(
            prefix
                .with_file_name(format!(
                    "{}-{stage}.png",
                    prefix.file_name().unwrap().to_string_lossy()
                ))
                .is_file()
        );
    }
}
fn box_mesh(world: &mut World, at: Vec3, size: Vec3, color: Color) {
    let mesh = world
        .resource_mut::<Assets<Mesh>>()
        .add(Cuboid::from_size(size));
    let material = world
        .resource_mut::<Assets<StandardMaterial>>()
        .add(StandardMaterial {
            base_color: color,
            perceptual_roughness: 1.,
            ..default()
        });
    world.spawn((
        Mesh3d(mesh),
        MeshMaterial3d(material),
        Transform::from_translation(at),
    ));
}
fn advance(world: &mut World) {
    let mut capture = world.remove_resource::<Capture>().unwrap();
    capture.frame += 1;
    let frame = capture.frame;
    if frame == 90 || frame == 125 {
        for wall in 0..3 {
            openings::preview_door(
                world,
                wall,
                0,
                if frame == 90 { 0.5 } else { 1. },
                true,
                false,
            )
            .unwrap();
        }
    }
    if frame == 160 {
        let mut rain = world.resource_mut::<crate::world::rain::RainState>();
        rain.wetness = 1.;
        rain.intensity = 1.;
    }
    let stage = match frame {
        80 => Some("closed"),
        115 => Some("half"),
        150 => Some("open"),
        190 => Some("wet"),
        _ => None,
    };
    if let Some(stage) = stage {
        let path = capture.prefix.with_file_name(format!(
            "{}-{stage}.png",
            capture.prefix.file_name().unwrap().to_string_lossy()
        ));
        let leaves:Vec<_>=world.query::<(&openings::DoorLeaf,&GlobalTransform,Option<&ColliderDisabled>)>().iter(world)
            .map(|(d,t,disabled)|json!({"wall":d.wall,"opening":d.opening,"pose":t.to_matrix().to_cols_array(),"disabled":disabled.is_some()})).collect();
        assert_eq!(leaves.len(), 3);
        std::fs::write(
            path.with_extension("json"),
            serde_json::to_string_pretty(
                &json!({"stage":stage,"leaves":leaves,"authorityInstalled":false}),
            )
            .unwrap(),
        )
        .unwrap();
        world
            .spawn(Screenshot::primary_window())
            .observe(save_to_disk(path))
            .observe(|shot: On<ScreenshotCaptured>, mut c: ResMut<Capture>| {
                c.images.push(shot.image.data.clone().unwrap());
                c.done += 1;
            });
    }
    if capture.done == 4 {
        let changed = capture.images[2]
            .chunks_exact(4)
            .zip(capture.images[3].chunks_exact(4))
            .filter(|(a, b)| (0..3).any(|i| a[i].abs_diff(b[i]) > 3))
            .count();
        assert!(
            changed > 100,
            "wet shared GlassPane shader must actually draw, changed {changed}"
        );
        world.write_message(AppExit::Success);
    }
    assert!(frame < 400, "opening capture timeout");
    world.insert_resource(capture);
}
