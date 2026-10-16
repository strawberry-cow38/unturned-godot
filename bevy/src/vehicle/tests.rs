use super::*;

pub(super) fn physics_app(sea: Option<f32>) -> App {
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
pub(crate) fn body(app: &mut App, key: &str, at: Vec3) -> Entity {
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
                engine_health: s.health * 10. * 0.4,
                engine_drowned: false,
                popped_tire_mask: 0,
                glass_mask: 0,
                broken_glass_mask: 0,
                on_fire: false,
                burn_seconds: 0.,
                wreck_salvageable: false,
                driver_player_id: 1,
                coupling: default(),
                passengers: vec![],
                recov_counter: 0,
                controls: Controls {
                    engine: true,
                    ..default()
                },
                drivetrain: Drivetrain::new(s),
                buoyancy: Buoyancy::new(s),
                flight: flight::Flight::new(s, 0.),
                engine: audio::Engine::new(0., true),
                swamp: water::Swamp::new(s),
                wreck: default(),
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
fn origin_shift_moves_cached_seat_and_spawn_positions_without_moving_body_twice() {
    let mut world = World::new();
    let pose = Transform::from_xyz(801., 7., -804.);
    let body = world.spawn(pose).id();
    world.insert_resource(Vehicles {
        points: vec![spawns::SpawnPoint {
            index: 9,
            type_id: 1,
            position: pose.translation,
            yaw: 42.,
            water: false,
        }],
        ..default()
    });
    world.insert_resource(Occupancy {
        seat: Some(OccupiedSeat {
            vehicle: body,
            net_id: 14,
            seat: 0,
            body_pose: pose,
            eye_pose: pose.with_translation(pose.translation + Vec3::Y),
            velocity: Vec3::Z * 7.,
            driver: true,
            aircraft: false,
        }),
    });
    let shift = Vec3::new(800., 0., -800.);
    shift_origin(&mut world, shift);
    assert_eq!(
        world.resource::<Vehicles>().points[0].position,
        Vec3::new(1., 7., -4.)
    );
    let seat = world.resource::<Occupancy>().seat.unwrap();
    assert_eq!(seat.body_pose.translation, Vec3::new(1., 7., -4.));
    assert_eq!(seat.eye_pose.translation, Vec3::new(1., 8., -4.));
    assert_eq!(seat.velocity, Vec3::Z * 7.);
    assert_eq!(
        *world.get::<Transform>(body).unwrap(),
        pose,
        "the exclusive root pass owns the one physical transform shift"
    );
}
#[test]
fn explicit_tire_harness_accepts_only_source_supported_wheels_and_aircraft_registration_is_exact() {
    let jeep = data::named("jeep").unwrap();
    assert_eq!(harness_tires("0,3,0,-1,4,99,bad", &jeep.spec), vec![0, 3]);
    assert!(harness_tires("0,1", &data::named("jet").unwrap().spec).is_empty());
    let helicopter = data::named("huey").unwrap();
    assert_eq!(
        registration(jeep, "car".into(), Vec3::ZERO, 0., 0)["isHeli"],
        false
    );
    assert_eq!(
        registration(helicopter, "heli".into(), Vec3::ZERO, 0., 0)["isHeli"],
        true
    );
}
#[test]
fn accepted_explosion_charred_meshes_and_impulse_reach_the_single_native_body_once() {
    let mut app = physics_app(None);
    app.add_plugins(bevy::asset::AssetPlugin::default())
        .init_asset::<Mesh>()
        .init_asset::<Image>()
        .init_asset::<StandardMaterial>()
        .init_asset::<AudioSource>()
        .init_resource::<ContentCache>()
        .init_resource::<WorldConfig>()
        .init_resource::<Settings>()
        .init_resource::<damage::WreckMaterial>()
        .add_message::<crate::entities::CoreEvent>()
        .add_systems(Update, damage::update);
    let car = body(&mut app, "jeep", Vec3::new(0., 2., 0.));
    app.world_mut().get_mut::<VehicleBody>(car).unwrap().flags = 32;
    let material = app
        .world_mut()
        .resource_mut::<Assets<StandardMaterial>>()
        .add(StandardMaterial::default());
    let child = app
        .world_mut()
        .spawn((
            MeshMaterial3d(material.clone()),
            Transform::default(),
            ChildOf(car),
        ))
        .id();
    app.world_mut().write_message(crate::entities::CoreEvent {
        sequence: 51,
        kind: "vehicleExploded".into(),
        data: json!({"netId":1,"impulse":{"x":0,"y":18000,"z":0},"torqueImpulse":{"x":2800,"y":0,"z":0}}),
    });
    ticks(&mut app, 3);
    let native = app.world().get::<VehicleBody>(car).unwrap();
    assert!(native.wreck.charred && native.wreck.impulse.is_none());
    let replacement = &app
        .world()
        .get::<MeshMaterial3d<StandardMaterial>>(child)
        .unwrap()
        .0;
    assert_ne!(
        replacement, &material,
        "the actual rendered child must receive its own charred material"
    );
    let charred = app
        .world()
        .resource::<Assets<StandardMaterial>>()
        .get(replacement)
        .unwrap();
    assert_eq!(charred.base_color, Color::srgb(0.05, 0.05, 0.05));
    assert!(
        app.world()
            .get::<RayCastVehicleController>(car)
            .unwrap()
            .wheels
            .iter()
            .all(|w| w.tuning.max_suspension_force == 0.)
    );
    let velocity = app.world().get::<Velocity>(car).unwrap();
    assert!(
        velocity.linear.y > 8. && velocity.linear.y < 11.,
        "one source impulse, not every fixed tick: {:?}",
        velocity.linear
    );
    assert!(velocity.angular.x > 0.1);
}
#[test]
fn pause_preserves_native_vehicle_ignition_and_water_clocks() {
    let mut app = physics_app(Some(0.));
    let car = body(&mut app, "jeep", Vec3::new(0., -3., 0.));
    app.world_mut()
        .get_mut::<VehicleBody>(car)
        .unwrap()
        .engine
        .cranking = 2.;
    app.insert_resource(UiState {
        screen: crate::ui::Screen::Paused,
        ..default()
    });
    ticks(&mut app, 10);
    let native = app.world().get::<VehicleBody>(car).unwrap();
    assert_eq!(native.engine.cranking, 2.);
    assert!(!native.engine_drowned && native.swamp.elapsed == 0.);
}
#[test]
fn unattended_punctured_car_keeps_its_parking_brake_on_a_real_slope() {
    let mut app = physics_app(None);
    let slope = Quat::from_rotation_x(0.18);
    app.world_mut().spawn((
        RigidBody::Fixed,
        Collider::cuboid(30., 0.25, 50.),
        Transform::from_xyz(0., -0.25, 0.).with_rotation(slope),
    ));
    let car = body(&mut app, "jeep", Vec3::new(0., 2., 0.));
    {
        let mut b = app.world_mut().get_mut::<VehicleBody>(car).unwrap();
        b.driver_player_id = 0;
        b.flags = 0;
        b.controls.engine = false;
        b.popped_tire_mask = 9;
    }
    app.world_mut().get_mut::<Transform>(car).unwrap().rotation = slope;
    ticks(&mut app, 200);
    let start = app.world().get::<Transform>(car).unwrap().translation;
    ticks(&mut app, 250);
    let end = app.world().get::<Transform>(car).unwrap().translation;
    assert!(
        end.distance(start) < 0.8,
        "unattended parking brake must prevent downhill escape despite two popped tires: start={start}, end={end}"
    );
    let brakes = &app
        .world()
        .get::<RayCastVehicleController>(car)
        .unwrap()
        .wheels;
    assert!(
        brakes.iter().all(|w| w.brake > 0.),
        "all wheels park; rear-only is the driver's handbrake turn"
    );
}
#[test]
fn native_car_drowns_and_cannot_restart_while_its_trapped_air_escapes() {
    let mut app = physics_app(Some(0.));
    let car = body(&mut app, "jeep", Vec3::new(0., -3., 0.));
    app.world_mut().get_mut::<VehicleBody>(car).unwrap().flags = 1 | 2 | 4 | 8 | 64 | 128;
    app.world_mut()
        .get_mut::<VehicleBody>(car)
        .unwrap()
        .controls
        .throttle = 1.;
    ticks(&mut app, 3);
    let native = app.world().get::<VehicleBody>(car).unwrap();
    assert!(native.engine_drowned);
    assert_eq!(native.engine_health, 0.);
    assert_eq!(
        native.flags & (1 | 2 | 4 | 8 | 64 | 128),
        0,
        "water disables actual engine, lamps, siren and alarm"
    );
    assert!(
        app.world()
            .get::<RayCastVehicleController>(car)
            .unwrap()
            .wheels
            .iter()
            .all(|w| w.engine_force == 0.)
    );
    // Holding the accelerator cannot restore the drowned engine. After the trapped
    // air has escaped the actual Rapier body sinks, with drag instead of free fall.
    ticks(&mut app, 600);
    let native = app.world().get::<VehicleBody>(car).unwrap();
    assert_eq!(native.engine_health, 0.);
    assert!(app.world().get::<Velocity>(car).unwrap().linear.y < -1.);
    assert!(app.world().get::<Velocity>(car).unwrap().linear.y > -15.);
}
#[test]
fn native_interior_wheel_uses_the_same_axis_and_angle_as_the_hand_anchor() {
    let mut app = physics_app(None);
    let car = body(&mut app, "jeep", Vec3::new(0., 2., 0.));
    let wheel = app
        .world_mut()
        .spawn((render::SteeringWheel(car), Transform::default()))
        .id();
    app.add_systems(Update, render::animate_steering);
    app.world_mut()
        .get_mut::<VehicleBody>(car)
        .unwrap()
        .controls
        .steer = 1.;
    ticks(&mut app, 30);
    // Update visuals once after the last physical steer integration.
    app.world_mut()
        .run_system_cached(render::animate_steering)
        .unwrap();
    let (pivot, axis, angle) = app
        .world()
        .get::<VehicleBody>(car)
        .unwrap()
        .steering_wheel()
        .unwrap();
    assert!(pivot.length() > 0.5 && angle < -0.1);
    let rotation = app.world().get::<Transform>(wheel).unwrap().rotation;
    assert!(rotation.abs_diff_eq(Quat::from_axis_angle(axis, angle), 1e-6));
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
        let mut state = SystemState::<(ReadRapierContext, Query<&ChildOf>)>::new(app.world_mut());
        let (read, parents) = state.get(app.world()).unwrap();
        let ctx = read.single().unwrap();
        exit::probe(
            &ctx,
            &parents,
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

#[test]
fn parked_vehicle_retains_pose_after_terrain_unloads_and_wakes_when_region_returns() {
    let mut app = physics_app(None);
    let mut geometry = WorldGeometry::default();
    geometry.loaded_radius = 10.;
    app.insert_resource(geometry);
    let car = body(&mut app, "jeep", Vec3::new(100., 1.8, 0.));
    app.world_mut()
        .get_mut::<VehicleBody>(car)
        .unwrap()
        .driver_player_id = 0;
    ticks(&mut app, 200);
    assert_eq!(
        *app.world().get::<RigidBody>(car).unwrap(),
        RigidBody::Fixed
    );
    assert!(
        (app.world().get::<Transform>(car).unwrap().translation.y - 1.8).abs() < 0.01,
        "unloaded terrain cannot swallow a parked car"
    );
    app.world_mut()
        .resource_mut::<WorldGeometry>()
        .loaded_center = Vec3::new(100., 0., 0.);
    app.world_mut().spawn((
        Collider::cuboid(10., 0.5, 10.),
        Transform::from_xyz(100., -0.5, 0.),
    ));
    ticks(&mut app, 200);
    assert_eq!(
        *app.world().get::<RigidBody>(car).unwrap(),
        RigidBody::Dynamic
    );
    assert!(
        app.world()
            .get::<RayCastVehicleController>(car)
            .unwrap()
            .wheels
            .iter()
            .all(|w| w.state.is_in_contact)
    );
}

#[test]
fn native_rotary_fleet_climbs_within_authority_envelope_and_hands_off_sinks() {
    for definition in data::FLEET.iter().filter(|d| d.spec.heli) {
        let mut app = physics_app(None);
        let heli = body(&mut app, &definition.key, Vec3::new(0., 100., 0.));
        {
            let mut b = app.world_mut().get_mut::<VehicleBody>(heli).unwrap();
            b.flight.spool = 1.;
            b.flight.collective = 1.;
            b.controls.throttle = 1.;
        }
        ticks(&mut app, 700);
        let climb = app.world().get::<Velocity>(heli).unwrap().linear.y;
        assert!(
            climb > 1. && climb < definition.spec.heli_climb_max * 0.95,
            "{} climb {climb} must remain inside the actual owner envelope",
            definition.key
        );
        app.world_mut()
            .get_mut::<VehicleBody>(heli)
            .unwrap()
            .controls
            .throttle = 0.;
        ticks(&mut app, 600);
        let sink = app.world().get::<Velocity>(heli).unwrap().linear.y;
        assert!(
            sink < -0.4 && sink > -3.,
            "{} hands-off must descend gently, got {sink}",
            definition.key
        );
    }
}

#[test]
fn native_aircraft_cyclic_and_rudder_turn_the_actual_body_in_source_directions() {
    for key in ["huey", "otter"] {
        for axis in 0..3 {
            let mut app = physics_app(None);
            let aircraft = body(&mut app, key, Vec3::new(0., 100., 0.));
            {
                let mut b = app.world_mut().get_mut::<VehicleBody>(aircraft).unwrap();
                b.flight.spool = 1.;
                b.flight.collective = 0.6;
                match axis {
                    0 => b.controls.pitch = 0.5,
                    1 => b.controls.roll = 0.5,
                    _ => b.controls.yaw = 0.5,
                }
            }
            if key == "otter" {
                app.world_mut()
                    .get_mut::<Velocity>(aircraft)
                    .unwrap()
                    .linear = Vec3::NEG_Z * 16.;
            }
            ticks(&mut app, 25);
            let pose = app.world().get::<Transform>(aircraft).unwrap();
            let nose = pose.rotation * Vec3::NEG_Z;
            let up = pose.rotation * Vec3::Y;
            let direction = match axis {
                0 => nose.y,
                1 => up.x,
                _ => nose.x,
            };
            assert!(
                direction > 0.015,
                "{key} positive axis {axis} is nose up, bank right, or nose right: nose={nose:?} up={up:?}"
            );
        }
    }
}
