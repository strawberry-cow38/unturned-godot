//! Original generated wall surfaces only: independent opening entities and
//! furniture are deliberately outside this stage consumer's current boundary.
use super::*;
use bevy::{
    app::AppExit,
    core_pipeline::tonemapping::Tonemapping,
    render::view::screenshot::{Screenshot, ScreenshotCaptured, save_to_disk},
    winit::WinitPlugin,
};

#[derive(Resource)]
struct Capture {
    prefix: std::path::PathBuf,
    view: View,
    count: usize,
}

#[test]
#[ignore = "requires Vulkan/display; UG_BUILDING_SHOT=output-prefix"]
fn render_original_generated_surfaces_and_cutaway() {
    let view = source(json!({"spec":{"kind":"Residential","seed":17,"minFloors":2,"maxFloors":2}}));
    let prefix = std::env::var_os("UG_BUILDING_SHOT")
        .map(std::path::PathBuf::from)
        .unwrap_or_else(|| "/tmp/bevy-building".into());
    let mut app = App::new();
    app.add_plugins(
        DefaultPlugins
            .set(WindowPlugin {
                primary_window: Some(Window {
                    resolution: (1280, 800).into(),
                    title: "Original generated building surfaces".into(),
                    ..default()
                }),
                ..default()
            })
            .set(WinitPlugin {
                run_on_any_thread: true,
                ..default()
            }),
    )
    .insert_resource(ClearColor(Color::srgb(0.24, 0.27, 0.30)))
    .insert_resource(GlobalAmbientLight {
        color: Color::WHITE,
        brightness: 140.,
        ..default()
    })
    .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(20),
    ))
    .add_systems(Update, advance);
    replace(app.world_mut(), &view).unwrap();
    app.world_mut().spawn((
        Camera3d::default(),
        Projection::Perspective(PerspectiveProjection {
            fov: 60_f32.to_radians(),
            ..default()
        }),
        Tonemapping::AcesFitted,
        Msaa::Sample4,
        Transform::from_xyz(44., 2034., 44.).looking_at(Vec3::new(1.5, 2004., 1.5), Vec3::Y),
    ));
    app.world_mut().spawn((
        DirectionalLight {
            illuminance: 12000.,
            shadow_maps_enabled: true,
            ..default()
        },
        Transform::from_xyz(-30., 2040., 20.).looking_at(Vec3::new(0., 2000., 0.), Vec3::Y),
    ));
    let mesh = app
        .world_mut()
        .resource_mut::<Assets<Mesh>>()
        .add(Cuboid::new(80., 0.3, 80.));
    let material = app
        .world_mut()
        .resource_mut::<Assets<StandardMaterial>>()
        .add(StandardMaterial {
            base_color: Color::srgb(0.43, 0.46, 0.47),
            perceptual_roughness: 1.,
            ..default()
        });
    app.world_mut().spawn((
        Mesh3d(mesh),
        MeshMaterial3d(material),
        Transform::from_xyz(0., 1999.85, 0.),
    ));
    app.insert_resource(Capture {
        prefix: prefix.clone(),
        view,
        count: 0,
    });
    app.run();
    for stage in ["full", "cutaway"] {
        let path = prefix.with_file_name(format!(
            "{}-{stage}.png",
            prefix.file_name().unwrap().to_string_lossy()
        ));
        assert!(path.is_file(), "missing {}", path.display());
    }
}

fn advance(
    mut frame: Local<u32>,
    mut commands: Commands,
    mut capture: ResMut<Capture>,
    mut walls: Query<(&Wall, &mut Visibility)>,
    visible: Query<&ViewVisibility, With<Mesh3d>>,
) {
    *frame += 1;
    if *frame == 80 {
        // Original EditorBuildings.HideAbove(3), including its stair exception.
        for (wall, mut visibility) in &mut walls {
            let source = &capture.view.walls[wall.0];
            if source.kind != "Stairs" && source.position[1] - capture.view.stage_origin[1] > 3. {
                *visibility = Visibility::Hidden;
            }
        }
    }
    if ![60, 100].contains(&*frame) {
        return;
    }
    assert!(
        visible.iter().filter(|v| v.get()).count() > 20,
        "actual wall render meshes are visible"
    );
    let stage = if *frame == 60 { "full" } else { "cutaway" };
    let path = capture.prefix.with_file_name(format!(
        "{}-{stage}.png",
        capture.prefix.file_name().unwrap().to_string_lossy()
    ));
    let hidden = walls
        .iter()
        .filter(|(_, v)| **v == Visibility::Hidden)
        .count();
    if stage == "full" {
        assert_eq!(hidden, 0);
    } else {
        assert!(hidden > 0);
    }
    std::fs::write(
        path.with_extension("json"),
        serde_json::to_string_pretty(&json!({
            "source":"EditorBuildings.Generate Residential seed17 floors2",
            "stageOrigin":capture.view.stage_origin,"wallCount":capture.view.walls.len(),
            "ramps":capture.view.ramps.len(),"hiddenWalls":hidden,
            "openingEntities":false,"furniture":false,"frame":*frame
        }))
        .unwrap(),
    )
    .unwrap();
    capture.count += 1;
    let last = capture.count == 2;
    commands
        .spawn(Screenshot::primary_window())
        .observe(save_to_disk(path))
        .observe(
            move |_: On<ScreenshotCaptured>, mut exit: MessageWriter<AppExit>| {
                if last {
                    exit.write(AppExit::Success);
                }
            },
        );
}
