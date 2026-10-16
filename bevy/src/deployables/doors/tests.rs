use super::*;
use crate::deployables::placement::udp_tests::{frames, launch};

fn mesh(prop: &str) -> crate::content::ObjData {
    crate::content::ObjData::load(&WorldConfig::default().content_root.join(format!("objects/{prop}.obj"))).unwrap()
}

#[test]
fn wooden_leaves_rebase_the_hinge_clamp_to_ninety_and_refuse_two_hinges() {
    let root = WorldConfig::default().content_root;
    let pine = mesh("Door_Pine");
    let leaf = wooden_leaf(&root, "Door_Pine", (pine.min + pine.max) * 0.5).unwrap();
    // The anim row's +1.125 against a leaf spanning x -2.35..0.10: the hinge
    // lands 0.10 inside its +X edge, the inset the authored frame had.
    assert!((leaf.pivot.x - (pine.max.x - 0.1)).abs() < 1e-3, "{leaf:?}");
    assert_eq!(leaf.angle, 90., "retail 100 clamped, sign kept");
    assert_eq!(leaf.axis, Vec3::new(0., 0., -1.));
    assert_eq!(leaf.duration, 0.4667);
    let gate = wooden_leaf(&root, "Gate_Birch", Vec3::ZERO).unwrap();
    assert_eq!((gate.angle, gate.axis), (90., Vec3::X));
    assert!(wooden_leaf(&root, "Doubledoor_Pine", Vec3::ZERO).is_none());
}

#[test]
fn spawn_prop_stands_doors_up_and_lays_hatches_flat_on_the_point() {
    let point = Vec3::new(4., 2., -3.);
    for (prop, height) in [("Door_Pine", None), ("Gate_Birch", None), ("Hatch_Birch", Some(0.4))] {
        let data = mesh(prop);
        let bounds = geometry::Bounds { min: data.min, max: data.max };
        let host = host(point, 37., bounds);
        let world = host.compute_affine() * Transform::from_rotation(stand()).compute_affine();
        let ys: Vec<f32> = bounds.corners().iter().map(|c| world.transform_point3(*c).y).collect();
        let (low, high) = ys.iter().fold((f32::INFINITY, f32::NEG_INFINITY), |(a, b), y| (a.min(*y), b.max(*y)));
        assert!((low - point.y).abs() < 1e-4, "{prop} sits on the point");
        // The placed height is the mesh's own Z extent: these rips stand up on +Z.
        let expected = height.unwrap_or(data.max.z - data.min.z);
        assert!((high - low - expected).abs() < 1e-3, "{prop} height {} vs {expected}", high - low);
        // +270 about X takes the rip's +Z to +Y: the authored top is up.
        let top = world.transform_point3(Vec3::Z * data.max.z).y;
        let bottom = world.transform_point3(Vec3::Z * data.min.z).y;
        assert!(top > bottom, "{prop} stands the right way up");
    }
}

#[test]
fn udp_placed_door_is_local_only_spent_by_the_host_and_swings_on_f() {
    let mut u = launch(&[9160]);
    let cell = u.hold(9160);
    let at = u.feet - Vec3::Z * 2.5;
    u.look(at);
    assert!(u.placer().aim.valid, "{:?}", u.placer().aim);
    u.press(MouseButton::Left);
    frames(&mut u.app, 4);
    assert!(u.placer().gesture.is_none());
    let mut doors = u.app.world_mut().query::<&PlacedDoor>();
    assert_eq!(doors.iter(u.app.world()).count(), 1, "DoorDeploy.SpawnFor on the placer's machine");
    u.pump(20);
    assert_eq!(u.amount(9160), 0, "the host spent the named jar {cell:?}");
    assert!(
        u.state()["deployables"].as_array().unwrap().is_empty(),
        "LocalOnly: no host entity, nothing replicated"
    );
    let door = |u: &mut crate::deployables::placement::udp_tests::Udp| {
        let mut q = u.app.world_mut().query::<&PlacedDoor>();
        let d = q.single(u.app.world()).unwrap();
        (d.swing, d.open, d.solid, d.id)
    };
    let (swing, open, solid, id) = door(&mut u);
    assert_eq!((swing, open, solid), (0., false, true), "closed and solid at rest");
    u.app.add_systems(
        Update,
        (|mut f: ResMut<InteractionFocus>| *f = default()).before(CollectInteractions),
    );
    u.look(at + Vec3::Y * 1.2);
    assert!(u.app.world().resource::<InteractionFocus>().selected(ACTION, id));
    u.key(KeyCode::KeyF, "f");
    // The toggle lands in Update; the swing advances in the next FixedUpdate.
    frames(&mut u.app, 2);
    let (swing, open, solid, _) = door(&mut u);
    assert!(open && swing > 0. && swing < 1. && !solid, "never solid mid-swing: {swing}");
    frames(&mut u.app, 4);
    let (swing, open, solid, _) = door(&mut u);
    assert!(open && swing == 1. && solid, "open and solid at rest");
    // A new world session leaves no local door behind.
    u.app.world_mut().resource_mut::<WorldSessionEpoch>().0 += 1;
    frames(&mut u.app, 2);
    let mut q = u.app.world_mut().query::<&PlacedDoor>();
    assert_eq!(q.iter(u.app.world()).count(), 0);
}
