use super::*;
mod visual;

#[test]
fn original_godot_segment_bases_match_diagonal_vertical_and_threshold_cases() {
    let rows: Value =
        serde_json::from_str(include_str!("../../../data/wire-source-reference.json")).unwrap();
    for row in rows.as_array().unwrap() {
        let points: Vec<_> = row["points"]
            .as_array()
            .unwrap()
            .iter()
            .map(|p| crate::player::vector(p).unwrap())
            .collect();
        for (i, pair) in points.windows(2).enumerate() {
            let observed = &row["segments"][i];
            let pose = segment::pose(pair[0], pair[1]);
            assert_eq!(pose.is_some(), observed["visible"].as_bool().unwrap());
            if let Some(pose) = pose {
                assert!(
                    pose.translation
                        .distance(crate::player::vector(&observed["position"]).unwrap())
                        < 2e-6
                );
                for (j, axis) in [Vec3::X, Vec3::Y, Vec3::Z].into_iter().enumerate() {
                    let actual = pose.rotation * (pose.scale * axis);
                    let expected = crate::player::vector(&observed["basis"][j]).unwrap();
                    assert!(actual.distance(expected) < 2e-6, "case {row} basis{j}");
                }
                assert_eq!(observed["radius"].as_f64().unwrap() as f32, 0.018);
                assert_eq!(observed["radialSegments"], 6);
            }
        }
    }
}

#[test]
fn source_segment_stretches_in_local_y_and_hides_short_or_nonfinite_edges() {
    let a = Vec3::new(3., -2., 7.);
    for delta in [Vec3::X, Vec3::Y, -Vec3::Y, Vec3::Z, Vec3::new(-3., 4., -5.)] {
        let b = a + delta;
        let t = segment::pose(a, b).unwrap();
        assert!(t.transform_point(Vec3::Y * -0.5).distance(a) < 2e-6);
        assert!(t.transform_point(Vec3::Y * 0.5).distance(b) < 2e-6);
        assert_eq!(t.scale.x, 1.);
        assert_eq!(t.scale.z, 1.);
    }
    assert!(segment::pose(a, a + Vec3::X * 0.00001).is_none());
    assert!(segment::pose(a, Vec3::splat(f32::NAN)).is_none());
    let near_up = segment::pose(Vec3::ZERO, Vec3::new(0.001, 1., 0.)).unwrap();
    assert_eq!(
        near_up.rotation,
        Quat::IDENTITY,
        "source threshold retains exact identity"
    );
}

#[test]
fn real_accepted_wire_follows_authored_body_ports_and_waits_for_accepted_removal() {
    let bridge = super::super::tests::initialize();
    for id in [458, 459] {
        bridge
            .request(
                "action",
                json!({"name":"console","args":{"text":format!("give {id}")}}),
            )
            .unwrap();
        super::super::tests::tick(&bridge);
    }
    for (def, x, yaw) in [(458, -1., 27.), (459, 1., -34.)] {
        bridge
            .request(
                "action",
                json!({"name":"placeDeployable","args":{
            "defId":def,"pos":[x,5,-2],"yawDegrees":yaw}}),
            )
            .unwrap();
        super::super::tests::tick(&bridge);
    }
    let state = super::super::tests::tick(&bridge);
    let rows = state["deployables"].as_array().unwrap();
    let id = |def| {
        rows.iter().find(|r| r["defId"] == def).unwrap()["netId"]
            .as_u64()
            .unwrap() as u32
    };
    let (gen_id, lamp_id) = (id(458), id(459));
    let mut app = super::super::tests::fixture(bridge, state);
    app.update();
    app.world()
        .resource::<CoreBridge>()
        .request(
            "action",
            json!({"name":"connectWire","args":{
        "srcId":gen_id,"srcPort":0,"dstId":lamp_id,"dstPort":0}}),
        )
        .unwrap();
    app.update();
    assert_eq!(
        app.world_mut()
            .query::<&WireSegment>()
            .iter(app.world())
            .count(),
        0,
        "queued intent cannot invent a local wire"
    );
    let state = super::super::tests::tick(app.world().resource::<CoreBridge>());
    app.world_mut().resource_mut::<GameSnapshot>().0 = state;
    app.update();
    app.update();
    let mut q = app
        .world_mut()
        .query::<(Entity, &WireSegment, &Transform)>();
    let wire_rows: Vec<_> = q.iter(app.world()).map(|(e, w, t)| (e, w.0, *t)).collect();
    assert_eq!(
        wire_rows.len(),
        1,
        "real accepted source graph must materialize"
    );
    let (mut entity, wire_id, transform) = wire_rows[0];
    assert!(
        app.world().get::<Collider>(entity).is_none(),
        "source Wire is non-interactive"
    );
    let mut bodies = app.world_mut().query::<(&Deployed, &Transform)>();
    let body_poses: HashMap<_, _> = bodies
        .iter(app.world())
        .map(|(d, t)| (d.net_id, *t))
        .collect();
    let catalog = app.world().resource::<ItemCatalog>();
    for (id, def, local) in [(gen_id, 458, Vec3::Y * -0.5), (lamp_id, 459, Vec3::Y * 0.5)] {
        let definition = catalog.deployables.iter().find(|d| d.id == def).unwrap();
        let expected = body_poses[&id].transform_point(Vec3::from(definition.ports[0].pos));
        assert!(
            transform.transform_point(local).distance(expected) < 2e-5,
            "authored socket belongs to standing body, not MeshEuler child"
        );
    }
    let material = app
        .world()
        .get::<MeshMaterial3d<StandardMaterial>>(entity)
        .unwrap();
    let material = app
        .world()
        .resource::<Assets<StandardMaterial>>()
        .get(&material.0)
        .unwrap();
    assert_eq!(material.base_color, Color::srgb(0.06, 0.06, 0.07));
    assert_eq!(material.metallic, 0.2);
    assert_eq!(material.perceptual_roughness, 0.6);
    let hash = app.world().resource::<GameSnapshot>().0["powerStateHash"]
        .as_u64()
        .unwrap();
    app.world_mut().resource_mut::<GameSnapshot>().0["powerStateHash"] = json!(hash ^ 1);
    app.update();
    assert!(
        app.world().resource::<Accepted>().graph.is_none(),
        "mismatched views cannot render stale wires"
    );
    assert!(app.world().get_entity(entity).is_err());
    app.world_mut().resource_mut::<GameSnapshot>().0["powerStateHash"] = json!(hash);
    app.update();
    entity = app
        .world_mut()
        .query::<(Entity, &WireSegment)>()
        .iter(app.world())
        .find(|(_, w)| w.0 == wire_id)
        .unwrap()
        .0;
    assert!(
        app.world().resource::<Accepted>().graph.is_some(),
        "A→failed B→A must refetch rather than pin an empty cache at A"
    );
    app.world()
        .resource::<CoreBridge>()
        .request(
            "action",
            json!({"name":"removeWire","args":{"wireId":wire_id}}),
        )
        .unwrap();
    app.update();
    assert!(
        app.world().get_entity(entity).is_ok(),
        "pending remove cannot optimistically erase accepted wire"
    );
    let state = super::super::tests::tick(app.world().resource::<CoreBridge>());
    app.world_mut().resource_mut::<GameSnapshot>().0 = state;
    app.update();
    assert!(app.world().get_entity(entity).is_err());
    assert!(
        app.world()
            .resource::<Accepted>()
            .graph
            .as_ref()
            .unwrap()
            .wires
            .is_empty()
    );
}
