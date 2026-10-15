//! Renderer-only material fixture: actual original police windshield, unchanged
//! weather uniforms, fixed shader times. Authority break/repair is covered by
//! glazing::tests::authority, rather than manufactured by this shader fixture.
use super::*;
use bevy::{
    app::AppExit,
    render::view::screenshot::{Screenshot, ScreenshotCaptured, save_to_disk},
    winit::WinitPlugin,
};

#[derive(Resource)]
struct Capture {
    frame: u32,
    completed: u8,
    body: Entity,
    images: Vec<Vec<u8>>,
}

#[test]
#[ignore = "requires Vulkan/display; UG_RAIN_GLASS_SHOT is output prefix"]
fn render_original_windshield_rain_dry_run_and_shelter() {
    let mut app = App::new();
    let config = WorldConfig::default();
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
                    title: "Original RainGlass / source windshield".into(),
                    resolution: (1100, 720).into(),
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
    .init_resource::<ContentCache>()
    .init_resource::<RainState>()
    .init_resource::<WreckMaterial>()
    .insert_resource(GameSnapshot(serde_json::json!({})))
    .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(20),
    ))
    .insert_resource(ClearColor(Color::srgb(0.15, 0.18, 0.22)))
    .add_plugins(RainGlassPlugin);
    let body = vehicle(&mut app, "police", Transform::IDENTITY);
    app.world_mut()
        .entity_mut(body)
        .insert(Visibility::Inherited);
    let panes = app.world().get::<Glazing>(body).unwrap().panes.clone();
    let pane = panes[0];
    for other in &panes[1..] {
        app.world_mut()
            .entity_mut(*other)
            .insert(Visibility::Hidden);
    }
    let center = app.world().get::<Pane>(pane).unwrap().center;
    let normal = -app.world().get::<RainPane>(pane).unwrap().axis;
    let eye = center + normal * 1.4;
    app.world_mut().spawn((
        Camera3d::default(),
        bevy::core_pipeline::tonemapping::Tonemapping::None,
        bevy::core_pipeline::tonemapping::DebandDither::Disabled,
        Transform::from_translation(eye).looking_at(center, Vec3::Y),
    ));
    // Checker behind the actual pane makes refraction visible without any
    // checker texture coordinates participating in the pane's rain field.
    app.world_mut()
        .run_system_once(
            move |mut commands: Commands,
                  mut meshes: ResMut<Assets<Mesh>>,
                  mut materials: ResMut<Assets<StandardMaterial>>| {
                let quad = meshes.add(Rectangle::new(0.18, 0.18));
                let colors = [Color::srgb(0.16, 0.20, 0.26), Color::srgb(0.75, 0.65, 0.45)];
                let mats: Vec<_> = colors
                    .into_iter()
                    .map(|c| {
                        materials.add(StandardMaterial {
                            base_color: c,
                            unlit: true,
                            cull_mode: None,
                            ..default()
                        })
                    })
                    .collect();
                let back =
                    Transform::from_translation(center - normal * 0.6).looking_to(-normal, Vec3::Y);
                for y in -10i32..11 {
                    for x in -15i32..16 {
                        commands.spawn((
                            Mesh3d(quad.clone()),
                            MeshMaterial3d(mats[((x + y + 40) % 2) as usize].clone()),
                            Transform::from_translation(back.transform_point(Vec3::new(
                                x as f32 * 0.18,
                                y as f32 * 0.18,
                                0.,
                            )))
                            .with_rotation(back.rotation),
                        ));
                    }
                }
                commands.insert_resource(GlobalAmbientLight {
                    brightness: 180.,
                    ..default()
                });
                commands.spawn((
                    DirectionalLight {
                        illuminance: 3500.,
                        ..default()
                    },
                    Transform::from_xyz(-3., 7., -5.).looking_at(center, Vec3::Y),
                ));
            },
        )
        .unwrap();
    app.insert_resource(Capture {
        frame: 0,
        completed: 0,
        body,
        images: Vec::new(),
    })
    .add_systems(First, advance)
    .add_systems(PostUpdate, fixed_shader_time.after(materials));
    app.run();
}
fn fixed_shader_time(capture: Res<Capture>, mut assets: ResMut<Assets<GlassMaterial>>) {
    for (_, mut material) in assets.iter_mut() {
        material.uniform.weather = Vec4::new(
            1.,
            1.,
            if capture.frame < 50 || capture.frame >= 110 {
                1.
            } else {
                0.
            },
            if capture.frame < 80 { 1.2 } else { 2.4 },
        );
    }
}
fn advance(world: &mut World) {
    let mut capture = world.remove_resource::<Capture>().unwrap();
    capture.frame += 1;
    let label = match capture.frame {
        40 => Some("dry"),
        70 => Some("wet"),
        100 => Some("later"),
        130 => Some("covered"),
        _ => None,
    };
    if let Some(label) = label {
        let prefix =
            std::env::var("UG_RAIN_GLASS_SHOT").unwrap_or_else(|_| "/tmp/bevy-rain-glass".into());
        let count = world
            .query::<&MeshMaterial3d<GlassMaterial>>()
            .iter(world)
            .count();
        assert_eq!(count, 6, "source panes have one custom material each");
        std::fs::write(format!("{prefix}-{label}.json"),serde_json::to_string_pretty(&serde_json::json!({
            "fixture":"renderer-only original police windshield, fixed source weather/time; no authority mutation",
            "frame":capture.frame,"paneCount":count,"body":format!("{:?}",capture.body),
            "weather":[1,1],"covered":capture.frame<50||capture.frame>=110,
            "shaderTime":if capture.frame<80{1.2}else{2.4},"oneSceneCopy":true
        })).unwrap()).unwrap();
        world
            .spawn(Screenshot::primary_window())
            .observe(save_to_disk(format!("{prefix}-{label}.png")))
            .observe(
                |event: On<ScreenshotCaptured>, mut capture: ResMut<Capture>| {
                    capture
                        .images
                        .push(event.image.data.as_ref().unwrap().clone());
                    capture.completed += 1;
                },
            );
    }
    if capture.frame > 140 && capture.completed == 4 {
        let changed = |a: usize, b: usize| {
            capture.images[a]
                .chunks_exact(4)
                .zip(capture.images[b].chunks_exact(4))
                .filter(|(left, right)| (0..3).any(|i| left[i].abs_diff(right[i]) > 3))
                .count()
        };
        assert!(
            changed(0, 1) > 1000,
            "actual wet shader must alter the dry scene, not silently fail pipeline compilation"
        );
        assert!(
            changed(1, 2) > 500,
            "released drops and runners must visibly advance with original TIME"
        );
        assert_eq!(
            changed(0, 3),
            0,
            "covered pane returns to identical dry optics"
        );
        world.write_message(AppExit::Success);
    }
    assert!(capture.frame < 300, "all RainGlass screenshots complete");
    world.insert_resource(capture);
}
