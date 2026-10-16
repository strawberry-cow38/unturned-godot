//! Original detached trailer geometry. This fixture does not assert coupling
//! authority; the accepted hitch journey is a separate integration boundary.
use super::*;
use bevy::{
    app::AppExit,
    render::view::screenshot::{Screenshot, ScreenshotCaptured, save_to_disk},
    winit::WinitPlugin,
};
#[derive(Resource)]
struct Capture {
    frame: u32,
    completed: u32,
    camera: Entity,
    body: Entity,
    key: &'static str,
}
#[test]
#[ignore = "Vulkan/display; UG_TRAILER_MODEL=trailer|dinky_trailer UG_TRAILER_SHOT=prefix"]
fn render_original_detached_trailer_and_separate_landing_legs() {
    let key = if std::env::var("UG_TRAILER_MODEL").as_deref() == Ok("dinky_trailer") {
        "dinky_trailer"
    } else {
        "trailer"
    };
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
                    resolution: (1100, 760).into(),
                    title: "Original detached trailer landing legs".into(),
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
    .init_resource::<ContentCache>()
    .insert_resource(ClearColor(Color::srgb(0.12, 0.15, 0.18)))
    .insert_resource(GlobalAmbientLight {
        brightness: 180.,
        ..default()
    });
    let body = crate::vehicle::tests::body(&mut app, key, Vec3::ZERO);
    app.world_mut()
        .entity_mut(body)
        .insert(Visibility::Inherited);
    app.world_mut()
        .run_system_once(
            move |mut commands: Commands,
                  mut cache: ResMut<ContentCache>,
                  mut meshes: ResMut<Assets<Mesh>>,
                  mut materials: ResMut<Assets<StandardMaterial>>,
                  mut images: ResMut<Assets<Image>>,
                  assets: Res<AssetServer>,
                  config: Res<WorldConfig>| {
                crate::vehicle::render::build(
                    &mut commands,
                    body,
                    data::named(key).unwrap(),
                    0,
                    &config.content_root,
                    &mut cache,
                    &mut meshes,
                    &mut materials,
                    &mut images,
                    &assets,
                );
            },
        )
        .unwrap();
    let spec = &data::named(key).unwrap().spec;
    // The collision box intentionally omits the long hitch/bed; frame the
    // actual source body, not that cropped physical fallback.
    let root = &app.world().resource::<WorldConfig>().content_root;
    let mesh =
        &app.world().resource::<ContentCache>().geometry[&root.join(spec.body.as_ref().unwrap())];
    let center = (mesh.min + mesh.max) * 0.5;
    let extent = (mesh.max - mesh.min).length();
    let camera = app
        .world_mut()
        .spawn((
            Camera3d::default(),
            bevy::core_pipeline::tonemapping::Tonemapping::None,
            bevy::core_pipeline::tonemapping::DebandDither::Disabled,
            Projection::Perspective(PerspectiveProjection {
                fov: 45_f32.to_radians(),
                ..default()
            }),
            Transform::from_translation(center + Vec3::new(0.8, 0.45, -0.85) * extent)
                .looking_at(center, Vec3::Y),
        ))
        .id();
    app.world_mut().spawn((
        DirectionalLight {
            illuminance: 3500.,
            ..default()
        },
        Transform::from_xyz(4., 8., -7.).looking_at(Vec3::ZERO, Vec3::Y),
    ));
    app.insert_resource(Capture {
        frame: 0,
        completed: 0,
        camera,
        body,
        key,
    })
    .add_systems(Update, advance)
    .add_systems(
        PostUpdate,
        capture.after(bevy::transform::TransformSystems::Propagate),
    );
    app.run();
}
fn advance(mut state: ResMut<Capture>, mut poses: Query<&mut Transform>) {
    state.frame += 1;
    assert!(state.frame < 150, "trailer capture timeout");
    if state.frame == 50 {
        let spec = &data::named(state.key).unwrap().spec;
        let local = (Vec3::from_array(spec.landing_leg_zone_min)
            + Vec3::from_array(spec.landing_leg_zone_max))
            * 0.5;
        let center = geometry::leg_transform(spec).transform_point(local);
        *poses.get_mut(state.camera).unwrap() =
            Transform::from_translation(center + Vec3::new(3.4, 1.1, -3.6))
                .looking_at(center, Vec3::Y);
    }
}
fn capture(
    mut commands: Commands,
    state: Res<Capture>,
    legs: Query<(&Transform, &Mesh3d, &ChildOf), With<geometry::LandingLegs>>,
    meshes: Res<Assets<Mesh>>,
    mut exit: MessageWriter<AppExit>,
) {
    if state.completed == 2 {
        exit.write(AppExit::Success);
        return;
    }
    let label = match state.frame {
        40 => "whole",
        80 => "legs",
        _ => return,
    };
    let (pose, mesh, parent) = legs.single().unwrap();
    assert_eq!(parent.parent(), state.body);
    assert_eq!(
        *pose,
        geometry::leg_transform(&data::named(state.key).unwrap().spec)
    );
    let prefix =
        std::env::var("UG_TRAILER_SHOT").unwrap_or_else(|_| format!("/tmp/bevy-{}", state.key));
    std::fs::write(format!("{prefix}-{label}.json"), serde_json::to_vec_pretty(&serde_json::json!({
        "fixture":"original detached trailer presentation only; source split/stretch/shared material; no accepted hitch or driving claim",
        "key":state.key,"vertices":meshes.get(&mesh.0).unwrap().count_vertices(),
        "landingLegTransform":pose.to_matrix().to_cols_array()
    })).unwrap()).unwrap();
    commands
        .spawn(Screenshot::primary_window())
        .observe(save_to_disk(format!("{prefix}-{label}.png")))
        .observe(|_: On<ScreenshotCaptured>, mut state: ResMut<Capture>| state.completed += 1);
}
