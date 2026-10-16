//! Real UDP accepted relationship, one local two-mass rig and accepted release.
//! Scripted production intents/controllers, not full-game OS input.
use super::super::{physics, recovery, support};
use super::*;
use crate::{
    entities::CoreEvent,
    player::GameSnapshot,
    ui::{Screen, UiIntent, UiModel, UiState},
    vehicle::{self, VehicleBody, Vehicles},
};
use bevy::{
    app::AppExit,
    render::view::screenshot::{Screenshot, ScreenshotCaptured, save_to_disk},
    winit::WinitPlugin,
};
use serde_json::json;

#[derive(Resource)]
struct Scene {
    journey: super::accepted::Journey,
    cab: Entity,
    trailer: Entity,
    camera: Entity,
    phase: usize,
    entered: usize,
    frames: u32,
    pending: bool,
    start: Vec3,
}
#[test]
#[ignore = "Vulkan/display and immutable hitch74+ bridge; actual UDP drive/legs/release"]
fn gpu_accepted_hitch_two_mass_drive_and_release() {
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
                    resolution: (1280, 800).into(),
                    title: "Accepted original fifth-wheel rig".into(),
                    ..default()
                }),
                ..default()
            })
            .set(WinitPlugin {
                run_on_any_thread: true,
                ..default()
            }),
    )
    .add_plugins(RapierPhysicsPlugin::<vehicle::PhysicsHooks>::default())
    .insert_resource(config)
    .init_resource::<ContentCache>()
    .init_resource::<vehicle::collision::Hulls>()
    .init_resource::<physics::Exceptions>()
    .init_resource::<recovery::Recoveries>()
    .init_resource::<UiModel>()
    .init_resource::<Vehicles>()
    .init_resource::<vehicle::Occupancy>()
    .init_resource::<GameSnapshot>()
    .add_message::<CoreEvent>()
    .add_message::<UiIntent>()
    .insert_resource(UiState {
        screen: Screen::Playing,
        networked: true,
        ..default()
    })
    .insert_resource(crate::world::WorldState {
        ready: true,
        ..default()
    })
    .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(20),
    ))
    .insert_resource(TimestepMode::Fixed {
        dt: 0.02,
        substeps: 1,
    })
    .insert_resource(ClearColor(Color::srgb(0.12, 0.15, 0.18)))
    .insert_resource(GlobalAmbientLight {
        brightness: 180.,
        ..default()
    });
    let journey = super::accepted::Journey::new(&mut app);
    let cab = super::physics::body(&mut app, "semi", journey.cab, Vec3::ZERO);
    let trailer = super::physics::body(&mut app, "trailer", journey.trailer, Vec3::Z * 9.9);
    for e in [cab, trailer] {
        app.world_mut().entity_mut(e).insert(Visibility::Inherited);
    }
    app.world_mut()
        .resource_mut::<Vehicles>()
        .entities
        .extend([(journey.cab, cab), (journey.trailer, trailer)]);
    let mesh = app
        .world_mut()
        .resource_mut::<Assets<Mesh>>()
        .add(Cuboid::new(160., 0.1, 160.));
    let material = app
        .world_mut()
        .resource_mut::<Assets<StandardMaterial>>()
        .add(Color::srgb(0.28, 0.30, 0.27));
    app.world_mut().spawn((
        Mesh3d(mesh),
        MeshMaterial3d(material),
        Collider::cuboid(80., 0.05, 80.),
        CollisionGroups::new(Group::GROUP_1, Group::ALL),
        Transform::from_xyz(0., -0.4, 0.),
    ));
    app.world_mut().spawn((
        DirectionalLight {
            illuminance: 3500.,
            shadow_maps_enabled: true,
            ..default()
        },
        Transform::from_xyz(-15., 25., 8.).looking_at(Vec3::ZERO, Vec3::Y),
    ));
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
            Transform::default(),
        ))
        .id();
    app.insert_resource(Scene {
        journey,
        cab,
        trailer,
        camera,
        phase: 0,
        entered: usize::MAX,
        frames: 0,
        pending: false,
        start: Vec3::ZERO,
    })
    .add_systems(
        Update,
        (
            advance,
            vehicle::sync_vehicles,
            recovery::apply_adoptions,
            vehicle::remote::update,
            recovery::receive,
            recovery::apply,
            vehicle::collision::update,
            physics::sync,
            vehicle::adopt_occupancy,
            controls,
        )
            .chain(),
    )
    .add_systems(
        PostUpdate,
        (physics::approach, physics::clamp, vehicle::step_bodies)
            .chain()
            .before(PhysicsSet::SyncBackend),
    )
    .add_systems(
        PostUpdate,
        vehicle::publish_bodies.after(PhysicsSet::Writeback),
    )
    .add_systems(Last, capture);
    app.run();
}
fn advance(world: &mut World) {
    world.resource_scope(|world,mut scene:Mut<Scene>| {
        if scene.phase>=4{return;}
        if scene.entered!=scene.phase {
            scene.entered=scene.phase;scene.frames=0;
            let c=scene.journey.cab;let t=scene.journey.trailer;
            match scene.phase {
                1=>{
                    scene.journey.action_world(world,"console",json!({"text":"teleport 2 0 5.7"}));scene.journey.pump_world(world,12);
                    scene.journey.action_world(world,"vehicleHitch",json!({"trailerNetId":t,"origin":[2,0.7,5.7],"direction":[-1,0,0]}));
                    scene.journey.pump_world(world,16);
                },
                2=>{
                    scene.journey.action_world(world,"console",json!({"text":"teleport 0 0 1"}));scene.journey.pump_world(world,12);
                    scene.journey.action_world(world,"enterVehicle",json!({"netId":c,"seat":0}));scene.journey.pump_world(world,16);
                    assert_eq!(world.resource::<GameSnapshot>().0["player"]["vehicleNetId"],c);
                    scene.start=world.get::<Transform>(scene.cab).unwrap().translation;
                },
                3=>{
                    scene.journey.action_world(world,"exitVehicle",json!({}));scene.journey.pump_world(world,16);
                    let state=&world.resource::<GameSnapshot>().0;
                    let row=state["vehicles"].as_array().unwrap().iter().find(|v|v["netId"]==t).unwrap();
                    let replica:vehicle::Replica=serde_json::from_value(row.clone()).unwrap();
                    let pose=vehicle::replica_pose(&replica);
                    let feet=pose.transform_point(Vec3::new(2.,0.,-3.9));
                    let origin=feet+Vec3::Y*0.7;let direction=pose.rotation*Vec3::NEG_X;
                    scene.journey.action_world(world,"console",json!({"text":format!("teleport {} {} {}",feet.x,feet.y,feet.z)}));scene.journey.pump_world(world,12);
                    scene.journey.action_world(world,"vehicleHitch",json!({"trailerNetId":t,"origin":origin.to_array(),"direction":direction.to_array()}));scene.journey.pump_world(world,16);
                },_=>{}
            }
        }
        scene.journey.pump_world(world,1);
        scene.frames+=1;
    });
}
fn controls(
    mut scene: ResMut<Scene>,
    mut bodies: Query<&mut VehicleBody>,
    mut poses: Query<&mut Transform>,
) {
    if scene.phase == 2 {
        let mut cab = bodies.get_mut(scene.cab).unwrap();
        cab.flags |= 1;
        cab.controls.engine = true;
        cab.controls.throttle = 1.;
    }
    let cab = *poses.get(scene.cab).unwrap();
    let trailer = *poses.get(scene.trailer).unwrap();
    let center = (cab.translation + trailer.translation) * 0.5 + Vec3::Y;
    *poses.get_mut(scene.camera).unwrap() =
        Transform::from_translation(center + Vec3::new(23., 13., -27.)).looking_at(center, Vec3::Y);
    if scene.phase == 2 && scene.frames == 1 {
        scene.start = cab.translation;
    }
}
fn capture(world: &mut World) {
    let scene = world.resource::<Scene>();
    if scene.phase >= 4 {
        world
            .resource_mut::<Messages<AppExit>>()
            .write(AppExit::Success);
        return;
    }
    if scene.pending
        || scene.entered != scene.phase
        || scene.frames < if scene.phase == 2 { 180 } else { 35 }
    {
        return;
    }
    let (phase, cab, trailer, start) = (scene.phase, scene.cab, scene.trailer, scene.start);
    let state = world.resource::<GameSnapshot>().0.clone();
    let body = world.get::<VehicleBody>(cab).unwrap();
    let other = world.get::<VehicleBody>(trailer).unwrap();
    let raised = world.get::<support::Supports>(trailer).unwrap().raised;
    assert_eq!(raised, phase == 1 || phase == 2);
    assert_eq!(body.coupling.trailer != 0, raised);
    assert_eq!(world.get::<ImpulseJoint>(trailer).is_some(), phase == 2);
    let cab_pose = *world.get::<Transform>(cab).unwrap();
    let trailer_pose = *world.get::<Transform>(trailer).unwrap();
    let gap = cab_pose
        .transform_point(data::named("semi").unwrap().spec.fifth_wheel.into())
        .distance(
            trailer_pose.transform_point(data::named("trailer").unwrap().spec.kingpin.into()),
        );
    if phase == 2 {
        assert!(
            cab_pose.translation.distance(start) > 0.5,
            "real controller must move both original bodies"
        );
        assert!(gap < 0.15, "point joint pin gap {gap}");
    }
    let names = ["detached", "coupled", "driven", "released"];
    let stem = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("artifacts")
        .join(format!("bevy-coupling-accepted-{}", names[phase]));
    let proof = json!({"phase":names[phase],"state":state,"cabPose":cab_pose.translation.to_array(),"trailerPose":trailer_pose.translation.to_array(),
        "cabDriver":body.driver_player_id,"trailerDriver":other.driver_player_id,"effectiveTrailerDriver":super::super::state::driver(other),"raised":raised,"joint":world.get::<ImpulseJoint>(trailer).is_some(),"pinGap":gap});
    std::fs::write(
        stem.with_extension("json"),
        serde_json::to_vec_pretty(&proof).unwrap(),
    )
    .unwrap();
    world.resource_mut::<Scene>().pending = true;
    world
        .spawn(Screenshot::primary_window())
        .observe(save_to_disk(stem.with_extension("png")))
        .observe(|_: On<ScreenshotCaptured>, mut scene: ResMut<Scene>| {
            scene.phase += 1;
            scene.pending = false;
        });
}
