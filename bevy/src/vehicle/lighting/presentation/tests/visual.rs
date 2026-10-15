use super::*;
use bevy::{
    app::AppExit,
    render::view::screenshot::{Screenshot, ScreenshotCaptured, save_to_disk},
    winit::WinitPlugin,
};

#[test]
#[ignore = "requires Vulkan/display and frozen protocol68 bridge; UG_VEHICLE_LIGHT_SHOT=path-prefix"]
fn render_actual_udp_low_high_damage_and_siren() {
    let mut app = App::new();
    let root = WorldConfig::default().content_root.canonicalize().unwrap();
    app.add_plugins(
        DefaultPlugins
            .set(AssetPlugin {
                file_path: root.to_string_lossy().into_owned(),
                ..default()
            })
            .set(WindowPlugin {
                primary_window: Some(Window {
                    title: "Original vehicle lighting / actual UDP".into(),
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
    .init_resource::<crate::nightvision::NightVision>()
    .add_plugins(crate::hdr::HdrPlugin)
    .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(20),
    ))
    .insert_resource(ClearColor(Color::srgb(0.008, 0.012, 0.02)));
    let journey = install(&mut app);
    app.insert_resource(journey)
        .insert_resource(Captures::default())
        .add_systems(Startup, scene)
        .add_systems(First, advance_scene);
    app.run();
}
#[derive(Resource, Default)]
struct Captures(u8);
fn scene(
    mut commands: Commands,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
) {
    commands.spawn((
        Camera3d::default(),
        crate::player::PlayerCamera,
        Transform::from_xyz(-9., 7., -16.).looking_at(Vec3::new(0., 0.7, -3.), Vec3::Y),
    ));
    commands.insert_resource(GlobalAmbientLight {
        color: Color::srgb(0.55, 0.65, 1.),
        brightness: crate::hdr::ambient_brightness(0.025),
        ..default()
    });
    commands.spawn((
        Mesh3d(meshes.add(Plane3d::default().mesh().size(220., 220.))),
        MeshMaterial3d(materials.add(StandardMaterial {
            base_color: Color::srgb(0.30, 0.31, 0.32),
            perceptual_roughness: 1.,
            ..default()
        })),
        Transform::from_xyz(0., -0.2, -25.),
    ));
    let obstacle = meshes.add(Cuboid::new(0.5, 1.5, 0.5));
    let material = materials.add(StandardMaterial {
        base_color: Color::srgb(0.65, 0.63, 0.58),
        perceptual_roughness: 1.,
        ..default()
    });
    for x in [-1., 1.] {
        commands.spawn((
            Mesh3d(obstacle.clone()),
            MeshMaterial3d(material.clone()),
            Transform::from_xyz(x, 0.55, -7.),
        ));
    }
}
fn advance_scene(world: &mut World) {
    let mut journey = world.remove_resource::<Journey>().unwrap();
    journey.frame += 1;
    match journey.frame {
        50 | 100 => journey.command("CycleHeadlights", 0),
        150 => {
            journey.damage(world, 0);
            let center = world
                .get::<BuiltLighting>(journey.body)
                .unwrap()
                .lamps
                .iter()
                .find(|l| l.label == "headlight_l")
                .unwrap()
                .center;
            let mut camera =
                world.query_filtered::<&mut Transform, With<crate::player::PlayerCamera>>();
            for mut pose in camera.iter_mut(world) {
                *pose = Transform::from_translation(center + Vec3::new(-1.6, 0.8, -2.6))
                    .looking_at(center + Vec3::new(0., 0., -0.4), Vec3::Y);
            }
        }
        170 => {
            let mut camera =
                world.query_filtered::<&mut Transform, With<crate::player::PlayerCamera>>();
            for mut pose in camera.iter_mut(world) {
                *pose =
                    Transform::from_xyz(-9., 7., -16.).looking_at(Vec3::new(0., 0.7, -3.), Vec3::Y);
            }
        }
        200 => {
            journey.command("RepairLamp", 0);
            journey.command("SetPattern", 0);
        }
        _ => (),
    }
    journey.pump_world(world, 1);
    let label = match journey.frame {
        40 => Some("off"),
        85 => Some("low"),
        135 => Some("high"),
        156 => Some("break-burst"),
        185 => Some("broken-left"),
        235 => Some("siren"),
        _ => None,
    };
    if let Some(label) = label {
        let prefix = std::env::var("UG_VEHICLE_LIGHT_SHOT")
            .unwrap_or_else(|_| "/tmp/bevy-vehicle-light".into());
        let path = format!("{prefix}-{label}");
        let runtime = world.get::<Runtime>(journey.body).unwrap();
        let lights=runtime.lights.iter().map(|l|{
            let spot=world.get::<SpotLight>(l.entity);
            let point=world.get::<PointLight>(l.entity);
            json!({"name":world.get::<Name>(l.entity).map(Name::as_str),"visible":world.get::<Visibility>(l.entity)==Some(&Visibility::Inherited),
                "range":spot.map(|s|s.range).or(point.map(|p|p.range)),"lumens":spot.map(|s|s.intensity).or(point.map(|p|p.intensity))})
        }).collect::<Vec<_>>();
        let particles = world
            .iter_entities()
            .filter(|e| e.contains::<crate::particles::Emitter>())
            .map(|e| {
                json!({"entity":e.id().to_bits(),"vertices":e.get::<Mesh3d>()
                .and_then(|h|world.resource::<Assets<Mesh>>().get(&h.0))
                .map(Mesh::count_vertices)})
            })
            .collect::<Vec<_>>();
        let evidence = json!({"fixture":"original stationary police body; actual UDP driver switches and ordinary host Fire against uploaded original triangles",
            "frame":journey.frame,"shots":journey.shots,"peerNetwork":journey.peer_state["network"],"vehicle":journey.row(),"lights":lights,
            "particles":particles,"sirenClock":runtime.clock.siren_time,"sirenLenses":runtime.clock.siren_lit});
        std::fs::write(
            format!("{path}.json"),
            serde_json::to_string_pretty(&evidence).unwrap(),
        )
        .unwrap();
        world
            .spawn(Screenshot::primary_window())
            .observe(save_to_disk(format!("{path}.png")))
            .observe(|_: On<ScreenshotCaptured>, mut shots: ResMut<Captures>| shots.0 += 1);
    }
    if journey.frame > 250 && world.resource::<Captures>().0 == 6 {
        world.write_message(AppExit::Success);
    }
    assert!(journey.frame < 500, "all six captures must complete");
    world.insert_resource(journey);
}
