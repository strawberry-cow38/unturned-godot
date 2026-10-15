use super::*;
use crate::vehicle::{data, exit};

fn probe(
    app: &mut App,
    body: Entity,
    player: Entity,
    pose: Transform,
    seat: Vec3,
    doors: &[Vec3],
) -> (bool, Vec3) {
    let mut state = SystemState::<(ReadRapierContext, Query<&ChildOf>)>::new(app.world_mut());
    let (physics, parents) = state.get(app.world()).unwrap();
    exit::probe(
        &physics.single().unwrap(),
        &parents,
        body,
        player,
        &pose,
        seat,
        doors,
    )
}

#[test]
fn actual_police_driver_exits_own_glass_but_a_living_person_still_blocks_the_door() {
    let mut app = app();
    let pose = Transform::from_xyz(8., 1., -13.).with_rotation(Quat::from_rotation_y(0.7));
    let body = build(&mut app, "police", pose);
    let d = data::named("police").unwrap();
    let seat = Vec3::from_array(d.seats[0]);
    let doors = exit::doors(
        d,
        0,
        &pose,
        app.world().resource::<ContentCache>(),
        &WorldConfig::default().content_root,
    );
    app.world_mut().spawn((
        Collider::cuboid(30., 0.1, 30.),
        Transform::from_xyz(0., -0.1, 0.),
        CollisionGroups::new(Group::GROUP_1, Group::ALL),
    ));
    let player = app
        .world_mut()
        .spawn((
            Collider::capsule_y(0.65, 0.35),
            Transform::from_translation(pose.transform_point(seat) + Vec3::Y),
            CollisionGroups::new(Group::GROUP_2, Group::ALL),
        ))
        .id();
    for _ in 0..3 {
        app.update();
    }
    let from = pose.transform_point(seat) + Vec3::Y * 0.9;
    let to = pose.transform_point(doors[0]) + Vec3::Y * 0.9;
    let old_hit = app
        .world_mut()
        .run_system_once(move |physics: ReadRapierContext| {
            let exclude =
                |e, _: &bevy_rapier3d::rapier::geometry::Collider| e != body && e != player;
            physics.single().unwrap().cast_ray(
                from,
                (to - from).normalize(),
                from.distance(to),
                true,
                QueryFilter::default().predicate(&exclude),
            )
        })
        .unwrap()
        .expect("negative control must reproduce the real own-pane obstruction");
    assert_eq!(
        app.world().get::<ChildOf>(old_hit.0).unwrap().parent(),
        body
    );
    assert!(
        app.world().get::<Pane>(old_hit.0).is_some()
            || app.world().get::<Name>(old_hit.0).unwrap().as_str()
                == d.spec.body.as_deref().unwrap(),
        "the original police body/pane geometry, not a fabricated wall, blocks the old probe"
    );
    let clear = probe(&mut app, body, player, pose, seat, &doors);
    assert!(
        clear.0,
        "own rendered body and panes must never trap the driver"
    );
    assert!((clear.1.y - 0.05).abs() < 0.001);
    let person = app
        .world_mut()
        .spawn((
            Collider::capsule_y(0.65, 0.35),
            Transform::from_translation(clear.1 + Vec3::Y),
            CollisionGroups::new(Group::GROUP_15, Group::GROUP_2),
        ))
        .id();
    for _ in 0..2 {
        app.update();
    }
    assert!(
        !probe(&mut app, body, player, pose, seat, &doors).0,
        "another living player's capsule still blocks the standing room"
    );
    app.world_mut().despawn(person);
    for _ in 0..2 {
        app.update();
    }
    assert!(probe(&mut app, body, player, pose, seat, &doors).0);
}

#[test]
fn exit_ignores_nested_own_bodies_for_room_but_retains_its_deck_as_ground() {
    let mut app = app();
    let body = app.world_mut().spawn(Transform::IDENTITY).id();
    let hinge = app
        .world_mut()
        .spawn((Transform::IDENTITY, ChildOf(body)))
        .id();
    let player = app
        .world_mut()
        .spawn((
            Collider::capsule_y(0.65, 0.35),
            Transform::from_xyz(0., 2., 0.),
        ))
        .id();
    // An independent nested rigid body also belongs to the source exclusion.
    app.world_mut().spawn((
        RigidBody::Fixed,
        Collider::cuboid(0.3, 0.3, 0.3),
        Transform::from_xyz(-1., 1.9, 0.),
        CollisionGroups::new(Group::GROUP_4, Group::GROUP_2),
        ChildOf(hinge),
    ));
    app.world_mut().spawn((
        Collider::cuboid(3., 0.1, 3.),
        Transform::from_xyz(0., 0.7, 0.),
        CollisionGroups::new(Group::GROUP_4, Group::GROUP_2),
        ChildOf(hinge),
    ));
    for _ in 0..3 {
        app.update();
    }
    let (clear, stand) = probe(
        &mut app,
        body,
        player,
        Transform::IDENTITY,
        Vec3::Y,
        &[Vec3::new(-2., 1., 0.)],
    );
    assert!(
        clear,
        "all descendant physics bodies are ignored along the path and in the standing capsule"
    );
    assert!(
        (stand.y - 0.85).abs() < 0.001,
        "the downward ray deliberately sees the own deck: {stand:?}"
    );
}
