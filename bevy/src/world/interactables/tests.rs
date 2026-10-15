use super::*;

fn fixture() -> LeafDefinition {
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../game/content/objects");
    let catalog = std::fs::read_to_string(root.join("doors.txt")).unwrap();
    let row: Vec<_> = catalog
        .lines()
        .find(|line| line.starts_with("Fridge_0 "))
        .unwrap()
        .split_whitespace()
        .collect();
    let curve = |name: &str| {
        Some(
            std::fs::read_to_string(root.join(format!("door_curves/Fridge_0_{name}.txt")))
                .unwrap()
                .lines()
                .map(|line| {
                    let values: Vec<f32> = line
                        .split_whitespace()
                        .map(|n| n.parse().unwrap())
                        .collect();
                    [values[0], values[1]]
                })
                .collect(),
        )
    };
    let n = |index: usize| row[index].parse::<f32>().unwrap();
    LeafDefinition {
        mesh: format!("objects/{}", row[1]),
        pivot: [n(2), n(3), n(4)],
        axis: [n(5), n(6), n(7)],
        angle_degrees: n(8),
        duration_seconds: n(9),
        sound: row.get(11).map(|s| (*s).to_owned()),
        open_curve: curve("open"),
        close_curve: curve("close"),
    }
}

#[test]
fn original_fridge_curves_keep_overshoot_and_closing_time_direction() {
    let leaf = fixture();
    assert!((easing(0.5, true, leaf.open_curve.as_deref()) - 0.978407).abs() < 1e-6);
    assert!((easing(0.5, false, leaf.close_curve.as_deref()) - 0.074965).abs() < 1e-6);
    assert!(easing(0.642857, true, leaf.open_curve.as_deref()) > 1.07);
    assert!(easing(1. - 0.642857, false, leaf.close_curve.as_deref()) < -0.017);
    assert_eq!(easing(0., false, leaf.close_curve.as_deref()), 0.);
    assert_eq!(easing(1., false, leaf.close_curve.as_deref()), 1.);
}

#[test]
fn visible_leaf_and_collision_rotate_about_the_same_extracted_hinge() {
    let leaf = fixture();
    let pivot = Vec3::from_array(leaf.pivot);
    let base = Transform::from_xyz(117., 52., -80.)
        .with_rotation(Quat::from_euler(EulerRot::YXZ, 0.7, -1.57, 0.))
        .with_scale(Vec3::new(1.2, 0.9, 1.4));
    let data = ObjData::load(
        &std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("../game/content")
            .join(&leaf.mesh),
    )
    .unwrap();
    for t in [0., 0.25, 0.5, 0.75, 1.] {
        let local = leaf_pose(&leaf, t, true, false);
        assert!((local.transform_point(pivot) - pivot).length() < 1e-5);
        let world = base.to_matrix() * local.to_matrix();
        let point = Vec3::from_array(data.positions[0]);
        let expected = base.transform_point(pivot + local.rotation * (point - pivot));
        assert!((world.transform_point3(point) - expected).length() < 1e-4);
    }
}

#[test]
fn chair_pelvis_matches_original_idle_sit_animation_instead_of_rest_height() {
    let rig: Value = serde_json::from_str(
        &std::fs::read_to_string(
            std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../game/content/rig.json"),
        )
        .unwrap(),
    )
    .unwrap();
    let hip = rig["anims"]["Idle_Sit"]["tracks"]["Spine"]["pos"][0][2]
        .as_f64()
        .unwrap() as f32;
    let seat = SeatDefinition {
        net_id: 7,
        source_index: 10,
        name: "Bench_Wood_0".into(),
        position: [3., 0.72, 9.],
        face: [1., 0., 0.],
        ground_y: 0.,
    };
    let posed = seat_pose(&seat);
    assert!((posed.body_pose.translation.y + hip - seat.position[1]).abs() < 0.0001);
    assert!((posed.eye_pose.translation.y - (seat.position[1] - 0.422 + 1.6)).abs() < 1e-6);
    assert!((*posed.body_pose.forward() - Vec3::X).length() < 1e-6);
    assert_eq!(posed.exit_position, Vec3::new(4.1, 0.1, 9.));
}

#[test]
fn container_and_object_door_ids_do_not_share_replica_or_geometry_state() {
    let snapshot = json!({"containers":[{"netId":1,"doorsOpen":true}],"interactables":{"objectDoors":[{"netId":1,"open":false,"reverse":true}]}});
    assert_eq!(door_state(&snapshot, 1, true, false), Some((true, false)));
    assert_eq!(door_state(&snapshot, 1, false, false), Some((false, true)));
    assert_eq!(door_state(&snapshot, 2, false, false), None);
    assert_ne!(dynamic_key((true, 1)), dynamic_key((false, 1)));
}

#[test]
fn real_rapier_leaf_releases_the_closed_position_and_rechecks_blocking_capsules() {
    use bevy::ecs::system::SystemState;
    let mut app = App::new();
    app.add_plugins((
        MinimalPlugins,
        TransformPlugin,
        RapierPhysicsPlugin::<NoUserData>::default().in_fixed_schedule(),
    ));
    app.insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(20),
    ));
    app.insert_resource(Time::<Fixed>::from_hz(50.));
    app.add_systems(FixedUpdate, animate.before(PhysicsSet::SyncBackend));
    let definition = fixture();
    let data = ObjData::load(
        &std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("../game/content")
            .join(&definition.mesh),
    )
    .unwrap();
    let center = (data.min + data.max) * 0.5;
    let half = ((data.max - data.min).abs() * 0.5).max(Vec3::splat(0.075));
    let parent_pose = Transform::from_xyz(7., 2., -11.)
        .with_rotation(Quat::from_euler(
            EulerRot::YXZ,
            0.7,
            -std::f32::consts::FRAC_PI_2,
            0.,
        ))
        .with_scale(Vec3::splat(1.2));
    let parent = app
        .world_mut()
        .spawn((
            parent_pose,
            Visibility::Inherited,
            RigidBody::Fixed,
            Collider::cuboid(0.1, 0.1, 0.1),
        ))
        .id();
    let leaf = app
        .world_mut()
        .spawn((
            ChildOf(parent),
            leaf_pose(&definition, 0., false, false),
            Collider::compound(vec![(
                center,
                Quat::IDENTITY,
                Collider::cuboid(half.x, half.y, half.z),
            )]),
            ColliderDisabled,
            DoorLeaf {
                parent,
                net_id: 1,
                source_index: 0,
                definition: definition.clone(),
                parent_pose,
                swing: 0.,
                target: false,
                reverse: false,
                solid: false,
                dirty: true,
                center,
                half,
                collision: Arc::new(ObjData::default()),
                container: true,
                lid_rises_when_lit: false,
            },
        ))
        .id();
    for _ in 0..3 {
        app.update();
    }
    let mut state = SystemState::<ReadRapierContext>::new(app.world_mut());
    let closed = parent_pose.to_matrix();
    let normal = parent_pose.rotation * Vec3::Y;
    let mut ray = |world: &World, at: Vec3| {
        let read = state.get(world).unwrap();
        read.single()
            .unwrap()
            .cast_ray(
                at + normal * 3.,
                -normal,
                6.,
                true,
                QueryFilter::default().predicate(&|hit, _| hit == leaf),
            )
            .is_some()
    };
    assert!(ray(app.world(), closed.transform_point3(center)));
    app.world_mut().get_mut::<DoorLeaf>(leaf).unwrap().target = true;
    app.update();
    assert!(app.world().get::<ColliderDisabled>(leaf).is_some());
    assert!(
        !ray(app.world(), closed.transform_point3(center)),
        "moving leaves must not obstruct the closed doorway"
    );
    for _ in 0..100 {
        app.update();
    }
    assert!(app.world().get::<ColliderDisabled>(leaf).is_none());
    assert!(
        !ray(app.world(), closed.transform_point3(center)),
        "the old closed collider must not survive the swing"
    );
    let opened = closed * leaf_pose(&definition, 1., true, false).to_matrix();
    assert!(ray(app.world(), opened.transform_point3(center)));
    assert!(
        (app.world()
            .get::<GlobalTransform>(leaf)
            .unwrap()
            .transform_point(center)
            - opened.transform_point3(center))
        .length()
            < 0.0001,
        "render transform must follow collision pose: local={:?} global={:?} expected={:?}",
        app.world().get::<Transform>(leaf),
        app.world().get::<GlobalTransform>(leaf),
        opened
    );
    let blocker = app
        .world_mut()
        .spawn((
            Transform::from_translation(closed.transform_point3(center)),
            Collider::capsule_y(0.65, 0.35),
        ))
        .id();
    for _ in 0..3 {
        app.update();
    }
    {
        let read = state.get(app.world()).unwrap();
        let context = read.single().unwrap();
        assert!(!leaf_clear(&context, closed, center, half, |hit| hit == blocker));
    }
    app.world_mut()
        .get_mut::<Transform>(blocker)
        .unwrap()
        .translation += Vec3::X * 10.;
    for _ in 0..3 {
        app.update();
    }
    let read = state.get(app.world()).unwrap();
    assert!(
        leaf_clear(&read.single().unwrap(), closed, center, half, |hit| hit
            == blocker),
        "a held endpoint must become solid after the body leaves"
    );
}

#[test]
fn only_source_barbecue_metadata_keeps_a_lit_lid_open() {
    let snapshot = json!({"containers":[{"netId":1,"doorsOpen":false,"cookerOn":true}]});
    assert_eq!(door_state(&snapshot, 1, true, true), Some((true, false)));
    assert_eq!(door_state(&snapshot, 1, true, false), Some((false, false)));
}

#[test]
fn authored_container_leaf_export_hits_the_visible_front_and_follows_opening() {
    let content = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../game/content");
    let text = std::fs::read_to_string(content.join("objects/doors.txt")).unwrap();
    let row: Vec<_> = text
        .lines()
        .find(|s| s.starts_with("Container_0 "))
        .unwrap()
        .split_whitespace()
        .collect();
    let n = |i: usize| row[i].parse::<f32>().unwrap();
    let definition = LeafDefinition {
        mesh: format!("objects/{}", row[1]),
        pivot: [n(2), n(3), n(4)],
        axis: [n(5), n(6), n(7)],
        angle_degrees: n(8),
        duration_seconds: n(9),
        sound: None,
        open_curve: None,
        close_curve: None,
    };
    let data = ObjData::load(&content.join(&definition.mesh)).unwrap();
    let center = (data.min + data.max) * 0.5;
    let half = ((data.max - data.min).abs() * 0.5).max(Vec3::splat(0.075));
    assert!(
        center.y > 3.,
        "fixture must retain the authored front offset"
    );
    let bridge = CoreBridge::launch(std::path::Path::new(env!("CARGO_MANIFEST_DIR")))
        .expect("Build original bridge with bevy/check.sh before integration tests");
    bridge
        .request("init", json!({"contentRoot":content,"spawn":[0.,0.,0.]}))
        .unwrap();
    let mut app = App::new();
    app.add_plugins(MinimalPlugins)
        .insert_resource(bridge)
        .init_resource::<WorldGeometry>()
        .init_resource::<ServiceManifest>()
        .init_resource::<Registry>()
        .init_resource::<GameSnapshot>()
        .init_resource::<crate::player::WorldSessionEpoch>()
        .add_systems(Update, export_geometry);
    let parent_pose = Transform::from_xyz(7., 2., -11.)
        .with_rotation(Quat::from_rotation_x(-std::f32::consts::FRAC_PI_2))
        .with_scale(Vec3::splat(1.2));
    let parent = app.world_mut().spawn_empty().id();
    let leaf = app
        .world_mut()
        .spawn((
            leaf_pose(&definition, 0., false, false),
            DoorLeaf {
                parent,
                net_id: 18,
                source_index: 0,
                definition: definition.clone(),
                parent_pose,
                swing: 0.,
                target: false,
                reverse: false,
                solid: true,
                dirty: true,
                center,
                half,
                collision: Arc::new(leaf_collision(center, half)),
                container: false,
                lid_rises_when_lit: false,
            },
        ))
        .id();
    let ray = |world: &World, pose: Mat4| {
        let at = pose.transform_point3(center);
        let normal = pose.transform_vector3(Vec3::Y).normalize();
        let reply = world
            .resource::<CoreBridge>()
            .request(
                "worldRay",
                json!({"from":(at+normal*2.).to_array(),"to":(at-normal*2.).to_array()}),
            )
            .unwrap();
        reply["result"].clone()
    };
    let closed = parent_pose.to_matrix();
    // Negative control reproduces the previous exporter: a valid box at the
    // prop origin, missing the leaf's large authored offset. The same authority
    // ray must miss it, then hit the corrected production export below.
    let legacy: Vec<_> = leaf_collision(Vec3::ZERO, half)
        .positions
        .iter()
        .map(|p| closed.transform_point3(Vec3::from_array(*p)).to_array())
        .collect();
    app.world()
        .resource::<CoreBridge>()
        .request(
            "worldDynamicGeometry",
            json!({"key":dynamic_key((false,18)),"meshes":[{"vertices":legacy}]}),
        )
        .unwrap();
    assert_eq!(
        ray(app.world(), closed)["hit"],
        false,
        "old uncentred export must reproduce the missed front leaf"
    );
    app.world_mut().resource_mut::<GameSnapshot>().0 =
        json!({"network":{"mode":"client","ownsWorld":false}});
    app.update();
    assert_eq!(
        ray(app.world(), closed)["hit"],
        false,
        "remote render leaves must not register authority geometry"
    );
    assert!(app.world().get::<DoorLeaf>(leaf).unwrap().dirty);
    app.world_mut().resource_mut::<GameSnapshot>().0 =
        json!({"network":{"mode":"listen","ownsWorld":true}});
    app.update();
    assert_eq!(
        ray(app.world(), closed)["hit"],
        true,
        "accepted shots must hit the actual visible front leaf, not a box at the prop origin"
    );
    let opened = leaf_pose(&definition, 1., true, false);
    *app.world_mut().get_mut::<Transform>(leaf).unwrap() = opened;
    app.world_mut().get_mut::<DoorLeaf>(leaf).unwrap().dirty = true;
    app.update();
    assert_eq!(
        ray(app.world(), closed)["hit"],
        false,
        "closed-position authority box must retire on opening"
    );
    assert_eq!(
        ray(app.world(), closed * opened.to_matrix())["hit"],
        true,
        "authority box must rotate around the same extracted hinge"
    );
    // Real host disposal previously left dirty static leaves repeatedly sending
    // worldDynamicGeometry against no session when the menu snapshot became null.
    app.world()
        .resource::<CoreBridge>()
        .request("disconnect", json!({}))
        .unwrap();
    app.world_mut().resource_mut::<GameSnapshot>().0 = Value::Null;
    app.world_mut().get_mut::<DoorLeaf>(leaf).unwrap().dirty = true;
    app.update();
    assert!(
        app.world().resource::<Registry>().exported.is_empty(),
        "disposed authority must clear publication cache"
    );
    assert!(
        app.world().get::<DoorLeaf>(leaf).unwrap().dirty,
        "future host must still publish this leaf afresh"
    );
}
