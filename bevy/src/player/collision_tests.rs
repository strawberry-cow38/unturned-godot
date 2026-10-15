use super::*;
use bevy::ecs::system::SystemState;

fn app() -> App {
    let mut app = App::new();
    app.add_plugins((
        MinimalPlugins,
        TransformPlugin,
        RapierPhysicsPlugin::<NoUserData>::default(),
    ))
    .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(20),
    ));
    app.world_mut().spawn((
        Collider::cuboid(10., 0.1, 10.),
        Transform::from_xyz(0., -0.1, 0.),
        CollisionGroups::new(Group::GROUP_1, Group::ALL),
    ));
    app
}
fn settle(app: &mut App) {
    for _ in 0..3 {
        app.update();
    }
}

#[test]
fn headroom_and_furniture_exit_use_model_surfaces_instead_of_stripped_chassis() {
    let mut app = app();
    let owner = app.world_mut().spawn_empty().id();
    let chassis = app
        .world_mut()
        .spawn((
            Collider::cuboid(3., 1., 3.),
            Transform::from_xyz(0., 1., 0.),
            CollisionGroups::new(Group::GROUP_4, Group::ALL & !Group::GROUP_2),
        ))
        .id();
    settle(&mut app);
    {
        let mut state = SystemState::<ReadRapierContext>::new(app.world_mut());
        let read = state.get(app.world()).unwrap();
        let context = read.single().unwrap();
        assert!(has_headroom(&context, owner, Vec3::ZERO, 1.2, 2.));
        let want = Vec3::new(1.1, 0.1, 0.);
        assert_eq!(
            prop_exit_position(&context, owner, Vec3::ZERO, want, 2.),
            Some(want),
            "an empty space inside the driving hull is not an overhead or a raised floor"
        );
        let probe = Collider::capsule(Vec3::Y * 0.55, Vec3::Y * 1.7, 0.3);
        let mut old_blocked = false;
        context.intersect_shape(
            Vec3::ZERO,
            Quat::IDENTITY,
            &probe,
            QueryFilter::default(),
            |e, _| {
                old_blocked |= e == chassis;
                true
            },
        );
        assert!(
            old_blocked,
            "the previous unfiltered query reproduces the phantom ceiling"
        );
    }
    app.world_mut().spawn((
        Collider::cuboid(2., 0.1, 2.),
        Transform::from_xyz(0., 1.6, 0.),
        CollisionGroups::new(Group::GROUP_4, Group::GROUP_2),
    ));
    settle(&mut app);
    let mut state = SystemState::<ReadRapierContext>::new(app.world_mut());
    let read = state.get(app.world()).unwrap();
    assert!(
        !has_headroom(&read.single().unwrap(), owner, Vec3::ZERO, 1.2, 2.),
        "the actual model roof must still block standing"
    );
}

#[test]
fn throw_clearance_keeps_source_world_mask_without_mesh_or_enemy_hit_layers() {
    let mut app = app();
    let owner = app.world_mut().spawn_empty().id();
    let eye = Vec3::Y;
    app.world_mut().spawn((
        Collider::cuboid(0.5, 0.5, 0.1),
        Transform::from_xyz(0., 1., -0.4),
        CollisionGroups::new(Group::GROUP_4, Group::ALL & !Group::GROUP_2),
    ));
    app.world_mut().spawn((
        Collider::cuboid(0.5, 0.5, 0.1),
        Transform::from_xyz(0., 1., -0.7),
        CollisionGroups::new(Group::GROUP_5, Group::GROUP_2),
    ));
    let pane = app
        .world_mut()
        .spawn((
            Collider::cuboid(0.5, 0.5, 0.05),
            Transform::from_xyz(0., 1., -1.1),
            CollisionGroups::new(Group::GROUP_4, Group::GROUP_2),
        ))
        .id();
    settle(&mut app);
    let check = |app: &mut App| {
        let mut state = SystemState::<ReadRapierContext>::new(app.world_mut());
        let read = state.get(app.world()).unwrap();
        throw_clearance(&read.single().unwrap(), owner, eye, Vec3::NEG_Z, 1.5)
    };
    assert_eq!(
        check(&mut app),
        None,
        "source pane/model hit layers are outside ThrowOrigin's world mask"
    );
    app.world_mut()
        .entity_mut(pane)
        .insert(CollisionGroups::new(Group::NONE, Group::GROUP_2));
    settle(&mut app);
    assert_eq!(
        check(&mut app),
        None,
        "pane break does not alter the independent source throw-clearance mask"
    );
    let fallback = app
        .world_mut()
        .spawn((
            Collider::cuboid(0.5, 0.5, 0.05),
            Transform::from_xyz(0., 1., -1.1),
            CollisionGroups::new(Group::GROUP_4, Group::ALL),
        ))
        .id();
    settle(&mut app);
    assert!(
        (check(&mut app).unwrap() - 1.05).abs() < 0.001,
        "a vehicle without a source HitMesh keeps its original world-layer chassis"
    );
    app.world_mut().despawn(fallback);
    app.world_mut().spawn((
        Collider::cuboid(1., 1., 0.1),
        Transform::from_xyz(0., 1., -1.4),
        CollisionGroups::new(Group::GROUP_1, Group::ALL),
    ));
    settle(&mut app);
    assert!((check(&mut app).unwrap() - 1.3).abs() < 0.001);
}

#[test]
fn standing_clearance_uses_original_player_mask_including_other_players() {
    let mut app = app();
    let owner = app.world_mut().spawn_empty().id();
    let actor = app
        .world_mut()
        .spawn((
            capsule(2.),
            Transform::default(),
            CollisionGroups::new(Group::GROUP_5, Group::GROUP_2),
        ))
        .id();
    settle(&mut app);
    let room = |app: &mut App| {
        let mut state = SystemState::<ReadRapierContext>::new(app.world_mut());
        let read = state.get(app.world()).unwrap();
        has_headroom(&read.single().unwrap(), owner, Vec3::ZERO, 1.2, 2.)
    };
    assert!(
        room(&mut app),
        "original animals are hit targets, not player obstacles"
    );
    app.world_mut()
        .entity_mut(actor)
        .insert(CollisionGroups::new(Group::GROUP_15, Group::GROUP_2));
    settle(&mut app);
    assert!(
        !room(&mut app),
        "accepted remote player still occupies the standing space"
    );
}
