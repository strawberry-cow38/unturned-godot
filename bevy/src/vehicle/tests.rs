use super::*;

fn physics_app(sea: Option<f32>) -> App {
    let mut app = App::new();
    app.add_plugins((
        MinimalPlugins,
        TransformPlugin,
        RapierPhysicsPlugin::<NoUserData>::default(),
    ));
    app.insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(20),
    ));
    app.insert_resource(TimestepMode::Fixed {
        dt: 0.02,
        substeps: 1,
    });
    app.insert_resource(WorldState {
        ready: true,
        sea_level: sea,
        ..default()
    });
    app.add_systems(PostUpdate, step_bodies.before(PhysicsSet::SyncBackend));
    app
}
fn body(app: &mut App, key: &str, at: Vec3) -> Entity {
    let d = data::named(key).unwrap();
    let s = &d.spec;
    app.world_mut()
        .spawn((
            VehicleBody {
                net_id: 1,
                type_id: d.type_id,
                variant: 0,
                flags: 1,
                fuel: s.fuel,
                health: s.health * 10.,
                battery: 10000.,
                driver_player_id: 1,
                passengers: vec![],
                recov_counter: 0,
                controls: Controls {
                    engine: true,
                    ..default()
                },
                drivetrain: Drivetrain::new(s),
                buoyancy: Buoyancy::new(s),
            },
            Transform::from_translation(at),
            RigidBody::Dynamic,
            Velocity::default(),
            mass_properties(s),
            s.controller(),
            ExternalForce::default(),
            ExternalImpulse::default(),
            Collider::compound(vec![(
                Vec3::from_array(s.box_center),
                Quat::IDENTITY,
                Collider::cuboid(
                    s.box_size[0] * 0.5,
                    s.box_size[1] * 0.5,
                    s.box_size[2] * 0.5,
                ),
            )]),
            ColliderMassProperties::Density(0.),
            Damping {
                linear_damping: 0.1,
                angular_damping: 0.1,
            },
        ))
        .id()
}
fn ticks(app: &mut App, n: usize) {
    for _ in 0..n {
        app.update();
    }
}
#[test]
fn native_wheels_accelerate_brake_and_turn_against_real_rapier_ground() {
    let mut app = physics_app(None);
    app.world_mut().spawn((
        Collider::cuboid(500., 0.5, 500.),
        Transform::from_xyz(0., -0.5, 0.),
    ));
    let car = body(&mut app, "jeep", Vec3::new(0., 1.8, 0.));
    ticks(&mut app, 150);
    let contact = app
        .world()
        .get::<RayCastVehicleController>(car)
        .unwrap()
        .wheels
        .iter()
        .filter(|w| w.state.is_in_contact)
        .count();
    assert_eq!(contact, 4, "suspension must support the actual body");
    app.world_mut()
        .get_mut::<VehicleBody>(car)
        .unwrap()
        .controls
        .throttle = 1.;
    ticks(&mut app, 250);
    let speed = app.world().get::<Velocity>(car).unwrap().linear.length();
    let position = app.world().get::<Transform>(car).unwrap().translation;
    assert!(
        speed > 5.,
        "engine force must reach physics, speed={speed} at {position:?}"
    );
    assert!(position.z < -10., "source forward is -Z: {position:?}");
    app.world_mut()
        .get_mut::<VehicleBody>(car)
        .unwrap()
        .controls
        .throttle = -1.;
    ticks(&mut app, 50);
    let braked = app.world().get::<Velocity>(car).unwrap().linear.length();
    assert!(
        braked < speed * 0.7,
        "opposing pedal must brake before reverse: {speed} -> {braked}"
    );
    {
        let mut b = app.world_mut().get_mut::<VehicleBody>(car).unwrap();
        b.controls.throttle = 1.;
        b.controls.steer = 0.5;
    }
    ticks(&mut app, 120);
    let pose = app.world().get::<Transform>(car).unwrap();
    let yaw = pose.rotation.to_euler(EulerRot::YXZ).0;
    assert!(yaw < -0.15, "wheel contact must turn chassis: yaw={yaw}");
    assert!(
        (pose.rotation * Vec3::Y).y > 0.6,
        "normal steering must not overturn chassis: {pose:?}"
    );
}
#[test]
fn native_boat_settles_at_buoyant_equilibrium_and_recovers_after_downward_impulse() {
    let mut app = physics_app(Some(0.));
    let boat = body(&mut app, "runabout", Vec3::new(0., 0.5, 0.));
    ticks(&mut app, 700);
    let pose = *app.world().get::<Transform>(boat).unwrap();
    let speed = app.world().get::<Velocity>(boat).unwrap().linear.length();
    assert!(
        pose.translation.y > -1.5 && pose.translation.y < 0.8,
        "boat should displace the surface: {pose:?}"
    );
    assert!(
        speed < 0.5,
        "voxel damping must reach an equilibrium: {speed}"
    );
    assert!(
        (pose.rotation * Vec3::Y).y > 0.95,
        "boat remains upright: {pose:?}"
    );
    app.world_mut().get_mut::<Velocity>(boat).unwrap().linear.y = -3.;
    ticks(&mut app, 400);
    let recovered = app.world().get::<Transform>(boat).unwrap();
    assert!(
        (recovered.translation.y - pose.translation.y).abs() < 0.25,
        "boat restores displaced height: {:?} -> {:?}",
        pose.translation,
        recovered.translation
    );
    assert!(app.world().get::<Velocity>(boat).unwrap().linear.length() < 0.5);
}

#[test]
fn nullable_empty_passenger_block_preserves_vehicle_replica() {
    let row: Replica =
        serde_json::from_value(json!({"netId":7,"typeId":0,"passengers":null})).unwrap();
    assert_eq!(row.net_id, 7);
    assert!(row.passengers.is_none());
}

#[test]
fn native_own_door_probe_rejects_wall_and_standing_person() {
    use bevy::ecs::system::SystemState;
    let mut app = physics_app(None);
    app.world_mut().spawn((
        Collider::cuboid(20., 0.5, 20.),
        Transform::from_xyz(0., -0.5, 0.),
    ));
    let vehicle = app
        .world_mut()
        .spawn((
            Collider::cuboid(1., 1., 2.),
            Transform::from_xyz(0., 1., 0.),
        ))
        .id();
    let player = app
        .world_mut()
        .spawn((
            Collider::capsule_y(0.65, 0.35),
            Transform::from_xyz(-0.5, 1., 0.),
        ))
        .id();
    let wall = app
        .world_mut()
        .spawn((
            Collider::cuboid(0.1, 1., 3.),
            Transform::from_xyz(-1.3, 1., 0.),
        ))
        .id();
    ticks(&mut app, 3);
    let probe = |app: &mut App| {
        let mut state = SystemState::<ReadRapierContext>::new(app.world_mut());
        let read = state.get(app.world()).unwrap();
        let ctx = read.single().unwrap();
        exit::probe(
            &ctx,
            vehicle,
            player,
            &Transform::default(),
            Vec3::new(-0.5, 0., 0.),
            &[Vec3::new(-2., 0., 0.)],
        )
    };
    assert!(
        !probe(&mut app).0,
        "thin fence between seat and clear standing point blocks exit"
    );
    app.world_mut().despawn(wall);
    ticks(&mut app, 2);
    let clear = probe(&mut app);
    assert!(clear.0);
    assert!((clear.1.y - 0.05).abs() < 0.01);
    app.world_mut().spawn((
        Collider::capsule_y(0.65, 0.35),
        Transform::from_xyz(-2., 1., 0.),
    ));
    ticks(&mut app, 2);
    assert!(
        !probe(&mut app).0,
        "standing person at door blocks room, own seated player does not"
    );
}
