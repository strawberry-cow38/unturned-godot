use super::super::{physics, recovery, state, support};
use super::*;
use crate::player::GameSnapshot;
use crate::vehicle::{self, VehicleBody, collision, contacts, remote};
use serde_json::json;

pub(super) fn app() -> App {
    let mut app = App::new();
    app.add_plugins((
        MinimalPlugins,
        TransformPlugin,
        AssetPlugin::default(),
        RapierPhysicsPlugin::<vehicle::PhysicsHooks>::default(),
    ))
    .init_asset::<Mesh>()
    .init_asset::<Image>()
    .init_asset::<StandardMaterial>()
    .init_resource::<ContentCache>()
    .init_resource::<WorldConfig>()
    .init_resource::<collision::Hulls>()
    .init_resource::<super::super::physics::Exceptions>()
    .init_resource::<super::super::recovery::Recoveries>()
    .add_message::<crate::entities::CoreEvent>()
    .insert_resource(GameSnapshot(
        json!({"playerId":1,"network":{"ownsWorld":false}}),
    ))
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
    .add_systems(
        Update,
        (
            collision::update,
            super::super::recovery::apply_adoptions,
            super::super::physics::sync,
        )
            .chain(),
    )
    .add_systems(
        PostUpdate,
        (
            super::super::physics::approach,
            super::super::physics::clamp,
            vehicle::step_bodies,
        )
            .chain()
            .before(PhysicsSet::SyncBackend),
    );
    app.update();
    for mut config in app
        .world_mut()
        .query::<&mut RapierConfiguration>()
        .iter_mut(app.world_mut())
    {
        config.gravity = Vec3::ZERO;
    }
    app
}
pub(super) fn body(app: &mut App, key: &str, id: u32, at: Vec3) -> Entity {
    let entity = vehicle::tests::body(app, key, at);
    app.world_mut()
        .get_mut::<VehicleBody>(entity)
        .unwrap()
        .net_id = id;
    app.world_mut().entity_mut(entity).insert((
        CollisionGroups::new(Group::GROUP_4, Group::ALL & !Group::GROUP_3),
        contacts::ContactState::new(&data::named(key).unwrap().spec),
        remote::Puppet::default(),
    ));
    let definition = data::named(key).unwrap();
    app.world_mut()
        .run_system_once(
            move |mut commands: Commands,
                  mut cache: ResMut<ContentCache>,
                  mut meshes: ResMut<Assets<Mesh>>,
                  mut materials: ResMut<Assets<StandardMaterial>>,
                  mut images: ResMut<Assets<Image>>,
                  assets: Res<AssetServer>| {
                vehicle::render::build(
                    &mut commands,
                    entity,
                    definition,
                    0,
                    &WorldConfig::default().content_root,
                    &mut cache,
                    &mut meshes,
                    &mut materials,
                    &mut images,
                    &assets,
                );
            },
        )
        .unwrap();
    entity
}
fn pair(app: &mut App) -> (Entity, Entity) {
    let a = &data::named("semi").unwrap().spec;
    let b = &data::named("trailer").unwrap().spec;
    let cab = body(app, "semi", 10, Vec3::Y * 5.);
    let trailer = body(
        app,
        "trailer",
        11,
        Vec3::Y * 5. + Vec3::from_array(a.fifth_wheel) - Vec3::from_array(b.kingpin),
    );
    app.world_mut()
        .get_mut::<VehicleBody>(trailer)
        .unwrap()
        .driver_player_id = 0;
    set_link(app, cab, trailer, true, 1);
    (cab, trailer)
}
fn set_link(app: &mut App, cab: Entity, trailer: Entity, linked: bool, driver: u32) {
    let mut c = app.world_mut().get_mut::<VehicleBody>(cab).unwrap();
    c.driver_player_id = driver;
    c.coupling = state::Accepted {
        trailer: if linked { 11 } else { 0 },
        ..default()
    };
    app.world_mut()
        .get_mut::<VehicleBody>(trailer)
        .unwrap()
        .coupling = state::Accepted {
        cab: if linked { 10 } else { 0 },
        driver: if linked { driver } else { 0 },
        ..default()
    };
}
fn pins(app: &App, cab: Entity, trailer: Entity) -> (Vec3, Vec3) {
    (
        app.world()
            .get::<Transform>(cab)
            .unwrap()
            .transform_point(data::named("semi").unwrap().spec.fifth_wheel.into()),
        app.world()
            .get::<Transform>(trailer)
            .unwrap()
            .transform_point(data::named("trailer").unwrap().spec.kingpin.into()),
    )
}
#[test]
fn original_two_mass_joint_moves_both_bodies_and_retracts_support_through_real_bake() {
    let mut app = app();
    let (cab, trailer) = pair(&mut app);
    for _ in 0..6 {
        app.update();
    }
    assert!(app.world().get::<ImpulseJoint>(trailer).is_some());
    assert_eq!(
        app.world().get::<RigidBody>(trailer),
        Some(&RigidBody::Dynamic)
    );
    assert_eq!(
        app.world()
            .get::<VehicleBody>(trailer)
            .unwrap()
            .driver_player_id,
        0,
        "no invented seat"
    );
    assert!(
        app.world().get::<collision::PendingHull>(trailer).is_none(),
        "shipped source bake installed"
    );
    let support = app.world().get::<support::Supports>(trailer).unwrap();
    assert!(support.raised);
    assert_eq!(
        app.world()
            .get::<Collider>(trailer)
            .unwrap()
            .as_compound()
            .unwrap()
            .raw
            .shapes()
            .len()
            + 1,
        support.deployed.as_compound().unwrap().raw.shapes().len()
    );
    assert!(
        app.world_mut()
            .query_filtered::<(&ChildOf, &Visibility), With<geometry::LandingLegs>>()
            .iter(app.world())
            .filter(|(p, _)| p.parent() == trailer)
            .all(|(_, v)| *v == Visibility::Hidden)
    );
    app.world_mut().get_mut::<Velocity>(cab).unwrap().linear = Vec3::NEG_Z;
    let start = app.world().get::<Transform>(trailer).unwrap().translation;
    // Observe reciprocal momentum on the first solver step, before the free
    // airborne rig can swing into its source separate sleeper surface.
    app.update();
    let cab_speed = app.world().get::<Velocity>(cab).unwrap().linear.z;
    let trailer_speed = app.world().get::<Velocity>(trailer).unwrap().linear.z;
    assert!(
        trailer_speed < -0.05,
        "real point joint accelerates the6000kg trailer: {trailer_speed}"
    );
    assert!(
        cab_speed > -0.95 && cab_speed < -0.05,
        "reciprocal mass slows the7800kg cab: {cab_speed}"
    );
    for _ in 0..29 {
        app.update();
    }
    let (a, b) = pins(&app, cab, trailer);
    assert!(
        a.distance(b) < 0.08,
        "real free point joint holds pins after free rotation: {}",
        a.distance(b)
    );
    assert!(
        app.world()
            .get::<Transform>(trailer)
            .unwrap()
            .translation
            .distance(start)
            > 0.05,
        "the second body is physical, not a fixed presentation child"
    );
    set_link(&mut app, cab, trailer, false, 1);
    app.update();
    assert!(app.world().get::<ImpulseJoint>(trailer).is_none());
    assert!(
        !app.world()
            .get::<support::Supports>(trailer)
            .unwrap()
            .raised
    );
    assert!(
        app.world_mut()
            .query_filtered::<(&ChildOf, &Visibility), With<geometry::LandingLegs>>()
            .iter(app.world())
            .filter(|(p, _)| p.parent() == trailer)
            .all(|(_, v)| *v == Visibility::Inherited)
    );
}
#[test]
fn host_remote_pair_is_kinematic_and_host_exit_takes_both_last_accepted_poses() {
    let mut app = app();
    let (cab, trailer) = pair(&mut app);
    app.world_mut().resource_mut::<GameSnapshot>().0 =
        json!({"playerId":1,"network":{"ownsWorld":true}});
    set_link(&mut app, cab, trailer, true, 2);
    for _ in 0..3 {
        app.update();
    }
    assert!(app.world().get::<ImpulseJoint>(trailer).is_none());
    for e in [cab, trailer] {
        assert_eq!(
            app.world().get::<RigidBody>(e),
            Some(&RigidBody::KinematicPositionBased)
        );
    }
    // Exercise the real replica updater, including raw-current/next pose handoff.
    let rows = json!([{"netId":10,"typeId":12,"driverPlayerId":2,"coupledTrailerNetId":11,
        "position":[3,5,0],"linVel":[2,0,0]},
        {"netId":11,"typeId":13,"position":[3,5,9.6],"linVel":[2,0,0]}]);
    app.world_mut().resource_mut::<GameSnapshot>().0["vehicles"] = rows;
    app.world_mut().run_system_once(remote::update).unwrap();
    set_link(&mut app, cab, trailer, true, 0);
    app.world_mut().resource_mut::<GameSnapshot>().0["vehicles"][0]["driverPlayerId"] = json!(0);
    app.world_mut().run_system_once(remote::update).unwrap();
    recovery::apply_adoptions(app.world_mut());
    assert_eq!(
        app.world().get::<Transform>(cab).unwrap().translation,
        Vec3::new(3., 5., 0.)
    );
    assert_eq!(
        app.world().get::<Transform>(trailer).unwrap().translation,
        Vec3::new(3., 5., 9.6)
    );
    app.update();
    for e in [cab, trailer] {
        assert_eq!(app.world().get::<RigidBody>(e), Some(&RigidBody::Dynamic));
    }
    assert!(app.world().get::<ImpulseJoint>(trailer).is_some());
}
#[test]
fn source_jackknife_stop_rotates_about_kingpin_and_only_cancels_outward_yaw() {
    let mut app = app();
    let (cab, trailer) = pair(&mut app);
    let pin = Vec3::from_array(data::named("trailer").unwrap().spec.kingpin);
    let world_pin = pins(&app, cab, trailer).1;
    let rotation = Quat::from_rotation_y(110_f32.to_radians());
    *app.world_mut().get_mut::<Transform>(trailer).unwrap() =
        Transform::from_translation(world_pin - rotation * pin).with_rotation(rotation);
    app.world_mut()
        .get_mut::<Velocity>(trailer)
        .unwrap()
        .angular = Vec3::new(1., 2., 3.);
    physics::clamp(app.world_mut());
    let pose = app.world().get::<Transform>(trailer).unwrap();
    assert!(pose.transform_point(pin).distance(world_pin) < 1e-5);
    assert!((pose.rotation.to_euler(EulerRot::YXZ).0.to_degrees() - 90.).abs() < 0.01);
    assert_eq!(
        app.world().get::<Velocity>(trailer).unwrap().angular,
        Vec3::new(1., 0., 3.)
    );
}
#[test]
fn nearest_approach_ghost_keeps_player_surface_and_releases_groups_without_retracting_legs() {
    let mut app = app();
    let (cab, trailer) = pair(&mut app);
    set_link(&mut app, cab, trailer, false, 1);
    app.update();
    assert!(
        app.world()
            .get::<CollisionGroups>(trailer)
            .unwrap()
            .memberships
            .contains(geometry::GHOST_TRAILER)
    );
    assert!(
        app.world()
            .get::<CollisionGroups>(trailer)
            .unwrap()
            .filters
            .contains(Group::GROUP_2)
    );
    assert!(
        app.world()
            .get::<RayCastVehicleController>(cab)
            .unwrap()
            .exclude_rigid_bodies
            .contains(&trailer)
    );
    assert!(
        app.world()
            .resource::<physics::Exceptions>()
            .contains(cab, trailer)
    );
    assert!(
        !app.world()
            .get::<support::Supports>(trailer)
            .unwrap()
            .raised,
        "approach alone never retracts"
    );
    recovery::teleport(
        app.world_mut(),
        cab,
        recovery::AdoptPose {
            pose: Transform::from_xyz(50., 5., 0.),
            velocity: Velocity::default(),
        },
    );
    physics::approach(app.world_mut());
    assert!(
        !app.world()
            .get::<CollisionGroups>(trailer)
            .unwrap()
            .memberships
            .contains(geometry::GHOST_TRAILER)
    );
    assert!(
        !app.world()
            .get::<RayCastVehicleController>(cab)
            .unwrap()
            .exclude_rigid_bodies
            .contains(&trailer)
    );
    assert!(
        !app.world()
            .resource::<physics::Exceptions>()
            .contains(cab, trailer)
    );
}

#[test]
fn unattended_pair_parks_together_at_stream_boundary_and_driven_pair_keeps_ownership() {
    let mut app = app();
    let (cab, trailer) = pair(&mut app);
    app.world_mut().resource_mut::<GameSnapshot>().0 =
        json!({"playerId":1,"network":{"ownsWorld":true}});
    let mut region = crate::world::WorldGeometry::default();
    region.loaded_center = Vec3::ZERO;
    region.loaded_radius = 10.;
    app.insert_resource(region);
    set_link(&mut app, cab, trailer, true, 0);
    for e in [cab, trailer] {
        app.world_mut()
            .get_mut::<Transform>(e)
            .unwrap()
            .translation
            .z += 9.;
    }
    for _ in 0..3 {
        app.update();
    }
    for e in [cab, trailer] {
        assert_eq!(
            app.world().get::<RigidBody>(e),
            Some(&RigidBody::Fixed),
            "one member outside parks the entire unattended rig"
        );
    }
    set_link(&mut app, cab, trailer, true, 1);
    app.update();
    for e in [cab, trailer] {
        assert_eq!(
            app.world().get::<RigidBody>(e),
            Some(&RigidBody::Dynamic),
            "accepted driver retains both physics bodies"
        );
    }
    set_link(&mut app, cab, trailer, true, 0);
    for (e, at) in [(cab, Vec3::Y * 5.), (trailer, Vec3::new(0., 5., 9.6))] {
        recovery::teleport(
            app.world_mut(),
            e,
            recovery::AdoptPose {
                pose: Transform::from_translation(at),
                velocity: Velocity::default(),
            },
        );
    }
    app.update();
    for e in [cab, trailer] {
        assert_eq!(
            app.world().get::<RigidBody>(e),
            Some(&RigidBody::Dynamic),
            "resident rig wakes together"
        );
    }
}

#[test]
fn host_keeps_accepted_remote_pair_deceleration_damage_without_simulating_either_body() {
    let mut app = app();
    let (cab, trailer) = pair(&mut app);
    set_link(&mut app, cab, trailer, true, 2);
    app.insert_resource(crate::ui::UiState {
        screen: crate::ui::Screen::Playing,
        networked: true,
        ..default()
    })
    .init_resource::<crate::entities::ServiceManifest>()
    .add_message::<contacts::DamageRequest>();
    app.world_mut().resource_mut::<GameSnapshot>().0 = json!({"playerId":1,"network":{"ownsWorld":true},"vehicles":[
        {"netId":10,"typeId":12,"driverPlayerId":2,"coupledTrailerNetId":11,"position":[0,5,0],"linVel":[0,0,-10]},
        {"netId":11,"typeId":13,"position":[0,5,9.6],"linVel":[0,0,-10]}]});
    app.update();
    for entity in [cab, trailer] {
        assert_eq!(
            app.world().get::<RigidBody>(entity),
            Some(&RigidBody::KinematicPositionBased)
        );
    }
    app.world_mut().run_system_once(remote::update).unwrap();
    app.world_mut().run_system_once(contacts::detect).unwrap();
    assert_eq!(
        app.world_mut()
            .resource_mut::<Messages<contacts::DamageRequest>>()
            .drain()
            .count(),
        0
    );
    for row in app.world_mut().resource_mut::<GameSnapshot>().0["vehicles"]
        .as_array_mut()
        .unwrap()
    {
        row["linVel"] = json!([0, 0, 0]);
    }
    app.world_mut().run_system_once(remote::update).unwrap();
    app.world_mut().run_system_once(contacts::detect).unwrap();
    let hits = app
        .world_mut()
        .resource_mut::<Messages<contacts::DamageRequest>>()
        .drain()
        .collect::<Vec<_>>();
    assert_eq!(
        hits.len(),
        2,
        "accepted velocity facts still feed host-owned crash damage"
    );
    assert!(hits.iter().all(|hit| (hit.amount - 197.6).abs() < 0.01));
    for entity in [cab, trailer] {
        assert_eq!(
            app.world().get::<RigidBody>(entity),
            Some(&RigidBody::KinematicPositionBased)
        );
    }
    app.world_mut().resource_mut::<GameSnapshot>().0["network"]["ownsWorld"] = json!(false);
    for entity in [cab, trailer] {
        let kind = app.world().get::<VehicleBody>(entity).unwrap().type_id;
        app.world_mut()
            .get_mut::<contacts::ContactState>(entity)
            .unwrap()
            .adopt_velocity(&data::definition(kind).unwrap().spec, Vec3::NEG_Z * 10.);
    }
    app.world_mut().run_system_once(contacts::detect).unwrap();
    assert_eq!(
        app.world_mut()
            .resource_mut::<Messages<contacts::DamageRequest>>()
            .drain()
            .count(),
        0,
        "remote peer never authors damage"
    );
}
