use super::*;
use crate::deployables::placement::udp_tests::{Udp, frames, launch};

#[test]
fn open_nearest_crate_is_the_source_proximity_by_crate_origin() {
    let feet = Vec3::new(10., 2., 10.);
    let crates = [(1, feet + Vec3::new(2.4, 0., 0.)), (2, feet + Vec3::new(0., 0., -1.)), (3, feet + Vec3::X * 2.6)];
    assert_eq!(nearest(feet, crates.into_iter()), Some(2));
    assert_eq!(nearest(feet, crates[..1].iter().copied()), Some(1));
    assert_eq!(nearest(feet, crates[2..].iter().copied()), None, "2.5 m is the reach");
}

fn place(u: &mut Udp, id: u16, at: Vec3) -> u32 {
    u.peer("console", json!({"text":format!("give {id}")}));
    u.pump(10);
    let cell = u.cell(id);
    u.peer(
        "placeDeployable",
        json!({"defId":id,"pos":at.to_array(),"yawDegrees":90,"page":cell.page,"x":cell.x,"y":cell.y}),
    );
    u.pump(20);
    u.state()["deployables"]
        .as_array()
        .unwrap()
        .iter()
        .find(|r| r["defId"] == id)
        .expect("accepted placement")["netId"]
        .as_u64()
        .unwrap() as u32
}
fn candidate(u: &Udp, action: &str, id: u32) -> bool {
    u.app
        .world()
        .resource::<InteractionFocus>()
        .selected(action, id)
}

#[test]
fn udp_fridge_is_a_refrigerator_body_without_focus_that_opens_by_proximity() {
    let mut u = launch(&[]);
    u.app.add_systems(
        Update,
        (|mut f: ResMut<InteractionFocus>| *f = default()).before(CollectInteractions),
    );
    let at = u.feet - Vec3::Z * 1.8;
    let id = place(&mut u, 9130, at);
    frames(&mut u.app, 3);
    let replicas = u.app.world().resource::<Replicas>();
    let entity = replicas.instances[&id].entity;
    let world = u.app.world();
    assert!(world.get::<Fridge>(entity).is_some(), "Refrigerator body, not Deployable.Spawn");
    let pose = *world.get::<Transform>(entity).unwrap();
    assert!(pose.translation.distance(at) < 0.02, "Refrigerator.Spawn stands on the point");
    assert!(
        pose.rotation.angle_between(Quat::from_rotation_y(90_f32.to_radians())) < 0.01,
        "yawed only, never stood up"
    );
    // The consumer port hangs off the yawed node at its source position.
    let mut sockets = u.app.world_mut().query::<(&ChildOf, &Transform, &Name)>();
    let socket = sockets
        .iter(u.app.world())
        .find(|(p, _, n)| p.parent() == entity && n.as_str().contains("socket"))
        .map(|(_, t, _)| t.translation)
        .expect("Refrigerator consumer socket");
    assert!(socket.distance(Vec3::new(0., 0.25, -0.36)) < 1e-5);
    // Looking straight at it: no deployable focus (it is a StorageCrate), but
    // F still finds it as the nearest crate, with no prompt.
    u.look(at + Vec3::Y * 0.9);
    let diagnostic = u.app.world().resource::<InteractionDiagnostics>();
    assert_eq!(diagnostic.offered, None, "no hold-F pickup or readout on a fridge");
    assert!(candidate(&u, "openStorage", id));
    assert!(
        u.app.world().resource::<InteractionFocus>().opens_storage(),
        "ranks below crop harvest, as OpenNearestCrate does"
    );
    assert!(u.app.world().resource::<crate::info_billboard::FocusedInfo>().0.is_none());
    // Beyond 2.5 m of the player's origin the fallback is gone.
    let away = u.feet + Vec3::Z * 3.;
    u.peer("console", json!({"text":format!("teleport {} {} {}", away.x, away.y, away.z)}));
    u.pump(15);
    let feet = crate::player::vector(&u.state()["player"]["position"]).unwrap();
    assert!(feet.distance(at) > OPEN_REACH, "teleported out of reach: {feet}");
    frames(&mut u.app, 2);
    assert!(!candidate(&u, "openStorage", id));
}

#[test]
fn udp_campfire_keeps_its_deployable_focus_and_opens_only_by_the_fallback() {
    let mut u = launch(&[]);
    u.app.add_systems(
        Update,
        (|mut f: ResMut<InteractionFocus>| *f = default()).before(CollectInteractions),
    );
    let at = u.feet - Vec3::Z * 1.6;
    let id = place(&mut u, 362, at);
    frames(&mut u.app, 3);
    let entity = u.app.world().resource::<Replicas>().instances[&id].entity;
    assert!(u.app.world().get::<Fridge>(entity).is_none(), "a Deployable body with a crate child");
    // Looked at: the ordinary deployable focus (hold-F pickup) owns F.
    u.look(at + Vec3::Y * 0.1);
    assert_eq!(
        u.app.world().resource::<InteractionDiagnostics>().offered,
        Some(id)
    );
    assert!(candidate(&u, "ordinaryDeployable", id));
    assert!(
        !u.app.world().resource::<InteractionFocus>().opens_storage(),
        "a looked-at deployable sits above crop harvest"
    );
    // Looking away, still within 2.5 m: OpenNearestCrate takes F.
    u.look(u.feet + Vec3::new(3., 1.6, 3.));
    assert!(candidate(&u, "openStorage", id));
}
