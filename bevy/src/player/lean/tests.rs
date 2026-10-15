use super::*;

#[test]
fn source_key_edges_preserve_tap_shoulders_hold_lean_and_stance_rules() {
    let mut lean = LeanState::default();
    let (keys, shoulder) = lean.keys([true, false], false, 0.02);
    assert_eq!(shoulder, Some(true));
    assert_eq!(keys, [false; 2]);
    for _ in 0..3 {
        assert_eq!(lean.keys([true, false], false, 0.02).0, [false; 2]);
    }
    assert_eq!(lean.keys([true, false], false, 0.02).0, [true, false]);
    let (keys, shoulder) = lean.keys([false, true], false, 0.02);
    assert_eq!(shoulder, Some(false));
    assert_eq!(
        keys, [false; 2],
        "opposite press restarts shared tap window"
    );
    assert_eq!(
        LeanState::default().keys([true, false], true, 0.02).0,
        [true, false]
    );
    for stance in ["stand", "crouch", "prone", "swim"] {
        assert_eq!(choose(true, false, stance, true), (1, false));
        assert_eq!(choose(false, true, stance, true), (-1, false));
        assert_eq!(choose(true, true, stance, true), (0, false));
    }
    for stance in ["sprint", "climb", "driving", "sitting"] {
        assert_eq!(choose(true, false, stance, true), (0, false));
    }
    assert_eq!(choose(true, false, "stand", false), (0, true));
    assert_eq!(choose(false, false, "stand", false), (0, false));
    assert_eq!(struggle(0, 1, true), Some(1));
    assert_eq!(struggle(1, -1, true), Some(0));
    assert_eq!(struggle(-1, -1, true), None);
    assert_eq!(struggle(1, 0, true), None);
    assert_eq!(struggle(0, 1, false), None);
}

fn fixture() -> (App, Entity, Entity) {
    let mut app = App::new();
    let mut model = UiModel::default();
    model.connected = true;
    model.alive = true;
    app.add_plugins((
        MinimalPlugins,
        TransformPlugin,
        RapierPhysicsPlugin::<NoUserData>::default().in_fixed_schedule(),
    ))
    .insert_resource(Time::<Fixed>::from_hz(50.))
    .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(20),
    ))
    .insert_resource(UiState {
        screen: Screen::Playing,
        ..default()
    })
    .insert_resource(model)
    .init_resource::<crate::avatar::Perspective>()
    .init_resource::<crate::vehicle::Occupancy>()
    .init_resource::<crate::world::interactables::PropOccupancy>()
    .init_resource::<crate::world::origin::WorldOrigin>()
    .init_resource::<crate::world::WorldConfig>()
    .init_resource::<WorldState>()
    .init_resource::<Settings>()
    .init_resource::<ButtonInput<KeyCode>>()
    .init_resource::<ButtonInput<MouseButton>>()
    .init_resource::<PlayerInput>()
    .init_resource::<WorldSessionEpoch>()
    .init_resource::<LeanState>()
    .add_systems(FixedUpdate, step.before(PhysicsSet::SyncBackend))
    .add_systems(Update, (ease, camera_follow).chain());
    let owner = app
        .world_mut()
        .spawn((
            PlayerBody {
                velocity: Vec3::ZERO,
                grounded: true,
                height: 2.,
                stance: "stand".into(),
                eye_height: 1.75,
                locomotion: default(),
            },
            Transform::IDENTITY,
            RigidBody::KinematicPositionBased,
            capsule(2.),
            CollisionGroups::new(Group::GROUP_2, Group::ALL),
        ))
        .id();
    let camera = app
        .world_mut()
        .spawn((
            PlayerCamera,
            Transform::IDENTITY,
            Projection::Perspective(PerspectiveProjection::default()),
        ))
        .id();
    for _ in 0..5 {
        app.update();
    }
    (app, owner, camera)
}
fn tick(app: &mut App, ticks: usize) {
    for _ in 0..ticks {
        app.update();
    }
}
#[test]
fn actual_physics_lean_peeks_from_feet_and_ignores_opposite_shoulder_wall() {
    let (mut app, owner, camera) = fixture();
    let wall = app
        .world_mut()
        .spawn((
            Collider::cuboid(0.05, 1., 0.7),
            Transform::from_xyz(-0.45, 1.5, 0.),
            CollisionGroups::new(Group::GROUP_1, Group::ALL),
        ))
        .id();
    // The wall is flush to the left of the player's .35m-radius body.
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .press(KeyCode::KeyE);
    tick(&mut app, 90);
    let pose = *app.world().get::<Transform>(camera).unwrap();
    assert!(
        pose.translation.x > 0.59 && pose.translation.y < 1.65,
        "head must move sideways/down, not merely roll in place: {pose:?}"
    );
    assert!(pose.up().x > 0.33);
    assert_eq!(
        app.world().get::<Transform>(owner).unwrap().translation,
        Vec3::ZERO,
        "lean does not move or duplicate the owner's collision body"
    );
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .release(KeyCode::KeyE);
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .press(KeyCode::KeyQ);
    tick(&mut app, 5);
    let lean = app.world().resource::<LeanState>();
    assert!(lean.obstructed && lean.side == 0);
    assert!(
        lean.angle < -0.1,
        "source blocked return eases rather than snaps"
    );
    app.world_mut().entity_mut(wall).despawn();
    // A dropped item on the requested side does not block the head.
    app.world_mut().spawn((
        Collider::ball(0.2),
        Transform::from_xyz(-0.55, 1.75, 0.),
        CollisionGroups::new(Group::GROUP_3, Group::ALL),
    ));
    tick(&mut app, 100);
    assert!(app.world().get::<Transform>(camera).unwrap().translation.x < -0.59);
    assert!(!app.world().resource::<LeanState>().obstructed);
    // A vehicle is in the exact same obstruction mask as real world cover.
    app.world_mut().spawn((
        Collider::cuboid(0.05, 0.5, 0.3),
        Transform::from_xyz(-0.7, 1.75, 0.),
        CollisionGroups::new(Group::GROUP_4, Group::ALL),
    ));
    tick(&mut app, 5);
    assert!(app.world().resource::<LeanState>().obstructed);
}
#[test]
fn pitched_camera_and_actual_fire_basis_agree_during_lean_but_alt_does_not_steer_gun() {
    let (mut app, owner, camera) = fixture();
    {
        let mut input = app.world_mut().resource_mut::<PlayerInput>();
        input.yaw = 37.;
        input.pitch = 55.;
    }
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .press(KeyCode::KeyQ);
    tick(&mut app, 100);
    let lean = app.world().resource::<LeanState>();
    let body = app.world().get::<PlayerBody>(owner).unwrap();
    let input = app.world().resource::<PlayerInput>();
    let (eyes, direction) = aim_eye(Vec3::ZERO, body, input, Some(lean));
    let pose = app.world().get::<Transform>(camera).unwrap();
    assert!(eyes.distance(pose.translation) < 1e-5);
    assert!(
        direction.distance(*pose.forward()) < 1e-5,
        "roll-before-pitch avoids the source leaned, pitched crosshair miss"
    );
    let naive = Quat::from_euler(
        EulerRot::YXZ,
        37_f32.to_radians(),
        55_f32.to_radians(),
        lean.angle,
    ) * Vec3::NEG_Z;
    assert!(
        direction.angle_between(naive) > 15_f32.to_radians(),
        "the regression detects the wrong Euler order"
    );
    app.world_mut()
        .resource_mut::<crate::avatar::Perspective>()
        .head = Vec2::new(70., -20.);
    tick(&mut app, 1);
    let lean = app.world().resource::<LeanState>();
    let (_, aimed) = aim_eye(
        Vec3::ZERO,
        app.world().get::<PlayerBody>(owner).unwrap(),
        app.world().resource::<PlayerInput>(),
        Some(lean),
    );
    assert!(aimed.distance(direction) < 1e-3);
    assert!(
        aimed.angle_between(*app.world().get::<Transform>(camera).unwrap().forward())
            > 45_f32.to_radians()
    );
}
#[test]
fn real_input_gates_ease_out_and_session_reset_restores_source_eye_height() {
    let (mut app, _, _) = fixture();
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .press(KeyCode::KeyQ);
    tick(&mut app, 80);
    let angle = app.world().resource::<LeanState>().angle;
    app.world_mut().resource_mut::<UiState>().screen = Screen::Inventory;
    tick(&mut app, 3);
    assert_eq!(app.world().resource::<LeanState>().side, 0);
    assert!((0.01..angle).contains(&app.world().resource::<LeanState>().angle));
    app.world_mut().resource_mut::<UiState>().screen = Screen::Paused;
    let frozen = app.world().resource::<LeanState>().angle;
    tick(&mut app, 10);
    assert_eq!(app.world().resource::<LeanState>().angle, frozen);
    app.world_mut().resource_mut::<WorldSessionEpoch>().0 += 1;
    tick(&mut app, 1);
    let reset = app.world().resource::<LeanState>();
    assert_eq!(reset.angle, 0.);
    assert_eq!(reset.eye_height, 1.6);
}

#[test]
fn native_fire_intent_reaches_authority_from_leaned_eye_and_pitched_camera_axis() {
    let (mut app, owner) = crate::player::seated_tests::fixture(None);
    let mut lean = LeanState::default();
    lean.angle = 20_f32.to_radians();
    lean.eye_height = 1.75;
    app.insert_resource(lean);
    {
        let mut input = app.world_mut().resource_mut::<PlayerInput>();
        input.yaw = 37.;
        input.pitch = 55.;
    }
    app.update();
    let state = crate::player::seated_tests::tick(app.world().resource::<CoreBridge>(), 12);
    assert_eq!(state["player"]["ammo"], 29);
    let shot = state["events"]
        .as_array()
        .unwrap()
        .iter()
        .find(|e| e["kind"] == "fire")
        .unwrap();
    let origin = vector(&shot["data"]["origin"]).unwrap();
    let direction = vector(&shot["data"]["dir"]).unwrap();
    // Independent physical-camera result, not the helper used to emit the shot.
    let camera = crate::avatar::camera::follow(
        &mut crate::avatar::Perspective::default(),
        None,
        owner,
        app.world().get::<Transform>(owner).unwrap().translation,
        1.75,
        2.,
        37.,
        55.,
        20_f32.to_radians(),
        0.,
    );
    assert!(
        origin.distance(camera.translation) < 0.025,
        "accepted eye {origin} != camera {}",
        camera.translation
    );
    assert!(
        direction.angle_between(*camera.forward()) < 0.002,
        "accepted direction {direction} differs from pitched, rolled view"
    );
    assert!(
        origin.x.abs() > 0.4,
        "regression rejects the upright origin"
    );
}
