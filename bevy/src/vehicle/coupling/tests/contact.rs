//! Real solver/ray witnesses for the source approach-only exception. Simple
//! witness slabs isolate query semantics; source rig masses/pins drive selection.
use super::super::physics;
use super::*;
use crate::vehicle;

#[derive(Resource, Default)]
struct Control {
    clear_wheel_exclusion: bool,
    clear_pair_exception: bool,
}
fn negative_control(world: &mut World) {
    let control = world.resource::<Control>();
    let (wheel, pair) = (control.clear_wheel_exclusion, control.clear_pair_exception);
    if wheel {
        for mut c in world
            .query::<&mut RayCastVehicleController>()
            .iter_mut(world)
        {
            c.exclude_rigid_bodies.clear();
        }
    }
    if pair {
        *world.resource_mut::<physics::Exceptions>() = default();
    }
}
fn fixture() -> (App, Entity, Entity, Entity) {
    let mut app = App::new();
    app.add_plugins((
        MinimalPlugins,
        TransformPlugin,
        RapierPhysicsPlugin::<vehicle::PhysicsHooks>::default(),
    ))
    .init_resource::<physics::Exceptions>()
    .init_resource::<Control>()
    .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(20),
    ))
    .insert_resource(TimestepMode::Fixed {
        dt: 0.02,
        substeps: 1,
    })
    .add_systems(
        PostUpdate,
        (physics::approach, negative_control)
            .chain()
            .before(PhysicsSet::SyncBackend),
    );
    let cab = vehicle::tests::body(&mut app, "semi", Vec3::ZERO);
    let trailer = vehicle::tests::body(&mut app, "trailer", Vec3::Z * 9.6);
    app.world_mut()
        .get_mut::<vehicle::VehicleBody>(cab)
        .unwrap()
        .net_id = 10;
    app.world_mut()
        .get_mut::<vehicle::VehicleBody>(trailer)
        .unwrap()
        .net_id = 11;
    let mut wheel = VehicleWheel::new(
        Vec3::ZERO,
        Vec3::NEG_Y,
        Vec3::X,
        0.5,
        0.2,
        WheelTuning::default(),
    );
    wheel.tuning.max_suspension_force = 0.;
    app.world_mut().entity_mut(cab).insert((
        Collider::ball(0.1),
        GravityScale(0.),
        RayCastVehicleController::new(vec![wheel]),
        CollisionGroups::new(Group::GROUP_4, Group::ALL),
    ));
    app.world_mut().entity_mut(trailer).insert((
        RigidBody::Fixed,
        Collider::compound(vec![(
            Vec3::new(0., -0.5, -9.6),
            Quat::IDENTITY,
            Collider::cuboid(2., 0.1, 2.),
        )]),
        CollisionGroups::new(Group::GROUP_4, Group::ALL),
    ));
    app.world_mut()
        .entity_mut(trailer)
        .remove::<RayCastVehicleController>();
    let player = app
        .world_mut()
        .spawn((
            RigidBody::Dynamic,
            Collider::capsule_y(0.3, 0.2),
            Transform::from_xyz(1., 1., 0.),
            Velocity::default(),
            LockedAxes::ROTATION_LOCKED,
            CollisionGroups::new(Group::GROUP_2, Group::GROUP_4),
        ))
        .id();
    (app, cab, trailer, player)
}
#[test]
fn approach_wheel_rays_ignore_actual_trailer_surface_while_player_still_stands_on_it() {
    let (mut app, cab, trailer, player) = fixture();
    app.world_mut()
        .resource_mut::<Control>()
        .clear_wheel_exclusion = true;
    for _ in 0..4 {
        app.update();
    }
    assert_eq!(
        app.world()
            .get::<RayCastVehicleController>(cab)
            .unwrap()
            .wheels[0]
            .state
            .ground_entity,
        Some(trailer),
        "negative control: the ghost marker alone cannot hide a native GROUP4 surface from wheels"
    );
    app.world_mut()
        .resource_mut::<Control>()
        .clear_wheel_exclusion = false;
    for _ in 0..60 {
        app.update();
    }
    assert_eq!(
        app.world()
            .get::<RayCastVehicleController>(cab)
            .unwrap()
            .wheels[0]
            .state
            .ground_entity,
        None,
        "production exclusion reaches the actual suspension ray query"
    );
    let feet = app.world().get::<Transform>(player).unwrap().translation.y - 0.5;
    assert!(
        (feet + 0.4).abs() < 0.02,
        "real player capsule rests on tow-ghost plate: feet={feet}"
    );
}
#[test]
fn approach_exception_suppresses_actual_cab_trailer_contact_but_not_other_body_contacts() {
    let run = |negative: bool| {
        let (mut app, cab, _, _) = fixture();
        app.world_mut()
            .entity_mut(cab)
            .insert(Collider::ball(0.5))
            .remove::<RayCastVehicleController>();
        app.world_mut()
            .resource_mut::<Control>()
            .clear_pair_exception = negative;
        for _ in 0..30 {
            app.update();
        }
        app.world().get::<Transform>(cab).unwrap().translation.y
    };
    let source = run(false);
    let without_exception = run(true);
    assert!(
        source.abs() < 0.001,
        "approach prevents chassis impulse before an accepted link: {source}"
    );
    assert!(
        without_exception > 0.05,
        "negative control actually collides with the trailer: {without_exception}"
    );
}
