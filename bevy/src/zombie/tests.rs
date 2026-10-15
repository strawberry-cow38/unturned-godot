use super::*;
fn app() -> App {
    let mut app = App::new();
    app.add_plugins((
        MinimalPlugins,
        AssetPlugin::default(),
        TransformPlugin,
        RapierPhysicsPlugin::<NoUserData>::default().in_fixed_schedule(),
        crate::rig::RigPlugin,
    ))
    .init_asset::<Mesh>()
    .init_asset::<Image>()
    .init_asset::<StandardMaterial>()
    .init_asset::<SkinnedMeshInverseBindposes>()
    .init_asset::<bevy::shader::Shader>()
    .init_resource::<crate::content::ContentCache>()
    .init_resource::<WorldConfig>()
    .init_resource::<Zombies>()
    .init_resource::<WorldSessionEpoch>()
    .insert_resource(UiState {
        screen: crate::ui::Screen::Playing,
        ..default()
    })
    .insert_resource(GameSnapshot(json!({"zombies":[]})))
    .insert_resource(Time::<Fixed>::from_hz(50.))
    .insert_resource(TimestepMode::Fixed {
        dt: 0.02,
        substeps: 1,
    })
    .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(20),
    ))
    .add_plugins(crate::character::CharacterPlugin)
    .add_systems(Update, sync.before(RigAnimation))
    .add_systems(FixedUpdate, drive.before(PhysicsSet::SyncBackend))
    .add_systems(
        FixedUpdate,
        resolved.after(PhysicsSet::Writeback).before(RigFixedUpdate),
    );
    app.world_mut().spawn((
        RigidBody::Fixed,
        Collider::cuboid(100., 0.1, 100.),
        Transform::from_xyz(0., -0.1, 0.),
        CollisionGroups::new(Group::GROUP_1, Group::ALL),
    ));
    app
}
fn replica(id: u32, position: Vec3, desired: Vec2) -> serde_json::Value {
    json!({"netId":id,"hasBody":true,"position":position.to_array(),"hostPosition":position.to_array(),"yaw":0.,"hostYaw":0.,
        "velocity":[0,0,0],"desiredVelocity":desired.to_array(),"dead":false,"variant":2,"shirtId":-1,"pantsId":-1,"hatId":-1,"gearId":-1})
}
fn advance(app: &mut App, n: usize) {
    for _ in 0..n {
        app.update();
    }
}
fn entity(app: &App, id: u32) -> Entity {
    app.world().resource::<Zombies>().entities[&id]
}
#[test]
fn one_hot_body_walks_on_real_floor_with_source_heading_and_cadence() {
    let mut app = app();
    app.world_mut().resource_mut::<GameSnapshot>().0 =
        json!({"zombies":[replica(17,Vec3::new(0.,2.,0.),Vec2::new(0.,-4.5))]});
    advance(&mut app, 110);
    let entity = entity(&app, 17);
    let pose = app.world().get::<Transform>(entity).unwrap();
    let body = app.world().get::<ZombieBody>(entity).unwrap();
    assert!(
        pose.translation.z < -9. && pose.translation.z > -10.5,
        "actual displacement {:?}",
        pose.translation
    );
    assert!(
        pose.translation.y.abs() < 0.03,
        "source feet remain grounded: {:?}",
        pose.translation
    );
    assert!((body.velocity.xz().length() - 4.5).abs() < 0.05);
    assert!((pose.rotation * Vec3::NEG_Z).distance(Vec3::NEG_Z) < 0.01);
    let rig = app.world().get::<RigPlayback>(body.rig).unwrap();
    assert_eq!(rig.clip, "Move_2");
    assert!((rig.speed - 4.5 / 6.87).abs() < 0.01);
    assert!(rig.time > 0.);
    assert!(app.world().get::<RigFixedAnimation>(body.rig).is_some());
    assert_eq!(
        app.world_mut()
            .query::<&ZombieBody>()
            .iter(app.world())
            .count(),
        1
    );
}
#[test]
fn pinned_body_uses_real_slide_normal_then_escapes_along_wall() {
    let mut app = app();
    app.world_mut().spawn((
        RigidBody::Fixed,
        Collider::cuboid(0.1, 2., 12.),
        Transform::from_xyz(1.5, 2., 0.),
        CollisionGroups::new(Group::GROUP_1, Group::ALL),
    ));
    app.world_mut().resource_mut::<GameSnapshot>().0 =
        json!({"zombies":[replica(18,Vec3::ZERO,Vec2::new(4.5,0.))]});
    advance(&mut app, 90);
    let entity = entity(&app, 18);
    let first = app.world().get::<Transform>(entity).unwrap().translation;
    assert!(
        first.x < 1.01 && first.x > 0.98,
        "capsule must stop at wall: {first:?}"
    );
    advance(&mut app, 75);
    let second = app.world().get::<Transform>(entity).unwrap().translation;
    assert!(
        second.z.abs() > 1.,
        "source escape must use actual contact normal: {second:?}"
    );
    assert!(second.x < 1.01, "must never tunnel through wall");
}
#[test]
fn source_window_detects_oscillation_and_origin_shift_preserves_progress() {
    let mut m = Motion::default();
    for n in 0..51 {
        m.record(
            Vec3::new(if n % 2 == 0 { 0.1 } else { -0.1 }, 0., 0.),
            Vec2::X * 4.5,
            0.02,
        );
    }
    assert!(
        m.escape_time > 0.99,
        "window net progress, not per-frame distance"
    );
    let drive = m.drive(Vec2::X * 4.5, Vec3::NEG_X, 0.02);
    assert!(drive.y.abs() > 4.4 && drive.x > 0. && drive.x < 0.6);
    let mut app = app();
    app.world_mut().resource_mut::<GameSnapshot>().0 =
        json!({"zombies":[replica(19,Vec3::new(1000.,0.,0.),Vec2::ZERO)]});
    advance(&mut app, 2);
    let entity = entity(&app, 19);
    app.world_mut()
        .get_mut::<ZombieBody>(entity)
        .unwrap()
        .motion = m;
    let before = app.world().get::<ZombieBody>(entity).unwrap().motion;
    shift_origin(app.world_mut(), Vec3::new(1024., 0., -256.));
    let after = app.world().get::<ZombieBody>(entity).unwrap().motion;
    assert_eq!(
        Vec3::from_array(after.window_position),
        Vec3::from_array(before.window_position) - Vec3::new(1024., 0., -256.)
    );
    assert_eq!(after.window_time, before.window_time);
    assert_eq!(after.escape_time, before.escape_time);
}
#[test]
fn authoritative_death_disables_live_capsule_builds_ragdoll_and_retires_all_bodies() {
    let mut app = app();
    let mut row = replica(20, Vec3::ZERO, Vec2::ZERO);
    app.world_mut().resource_mut::<GameSnapshot>().0 = json!({"zombies":[row.clone()]});
    advance(&mut app, 8);
    let entity = entity(&app, 20);
    let rig = app.world().get::<ZombieBody>(entity).unwrap().rig;
    let head = app.world().get::<RigPlayback>(rig).unwrap().bones["Skull"];
    let before = app
        .world()
        .get::<GlobalTransform>(head)
        .unwrap()
        .translation();
    row["dead"] = json!(true);
    row["deathImpulse"] = json!([3., 2., 0.]);
    app.world_mut().resource_mut::<GameSnapshot>().0 = json!({"zombies":[row]});
    advance(&mut app, 70);
    assert!(app.world().get::<ColliderDisabled>(entity).is_some());
    assert!(
        app.world()
            .get::<KinematicCharacterController>(entity)
            .is_none()
    );
    assert!(app.world().get::<RigPlayback>(rig).unwrap().paused);
    assert_eq!(
        app.world_mut()
            .query::<&RigidBody>()
            .iter(app.world())
            .filter(|b| **b == RigidBody::Dynamic)
            .count(),
        11,
        "original physical bones"
    );
    let after = app
        .world()
        .get::<GlobalTransform>(head)
        .unwrap()
        .translation();
    assert!(
        after.is_finite() && after.distance(before) > 0.2,
        "death must hand original rig to real physics: {before:?}->{after:?}"
    );
    app.world_mut().resource_mut::<GameSnapshot>().0 = json!({"zombies":[]});
    advance(&mut app, 3);
    assert_eq!(
        app.world_mut()
            .query::<&ZombieBody>()
            .iter(app.world())
            .count(),
        0
    );
    assert_eq!(
        app.world_mut()
            .query::<&RigidBody>()
            .iter(app.world())
            .filter(|b| **b == RigidBody::Dynamic)
            .count(),
        0,
        "retirement must remove separate physical container"
    );
}
#[test]
fn pause_and_epoch_preserve_source_motion_and_rebuild_loaded_corpse() {
    let mut app = app();
    let mut row = replica(21, Vec3::new(3., 0., 4.), Vec2::ZERO);
    row["motion"] = json!({"windowPosition":[2.,0.,3.],"windowTime":0.4,"escapeTime":0.7,"positionInitialized":true});
    app.world_mut().resource_mut::<GameSnapshot>().0 = json!({"zombies":[row.clone()]});
    advance(&mut app, 2);
    let first = entity(&app, 21);
    let before = app.world().get::<ZombieBody>(first).unwrap().motion;
    assert_eq!(before.window_position, [2., 0., 3.]);
    assert_eq!(before.escape_time, 0.7);
    app.world_mut().resource_mut::<UiState>().screen = crate::ui::Screen::Paused;
    advance(&mut app, 10);
    assert_eq!(
        app.world()
            .get::<ZombieBody>(first)
            .unwrap()
            .motion
            .window_time,
        before.window_time
    );
    row["dead"] = json!(true);
    row["deathImpulse"] = json!([0., 0., 0.]);
    app.world_mut().resource_mut::<GameSnapshot>().0 = json!({"zombies":[row]});
    app.world_mut().resource_mut::<WorldSessionEpoch>().0 += 1;
    advance(&mut app, 3);
    let second = entity(&app, 21);
    assert_ne!(first, second);
    assert!(app.world().get_entity(first).is_err());
    assert!(app.world().get::<ZombieBody>(second).unwrap().dead);
    assert!(app.world().get::<ColliderDisabled>(second).is_some());
}
