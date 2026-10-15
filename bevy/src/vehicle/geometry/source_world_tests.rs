use super::*;

#[test]
fn original_fallback_shapes_keep_scaled_compound_geometry_without_broadening_combat() {
    let mut collider = Collider::compound(vec![
        (
            Vec3::new(2., 0., 0.),
            Quat::IDENTITY,
            Collider::cuboid(1., 2., 3.),
        ),
        (
            Vec3::new(-2., 0., 0.),
            Quat::IDENTITY,
            Collider::convex_hull(&[Vec3::ZERO, Vec3::X, Vec3::Y, Vec3::Z]).unwrap(),
        ),
    ]);
    collider.set_scale(Vec3::new(2., 3., 4.), 10);
    let pose = GlobalTransform::from(
        Transform::from_xyz(13., 2., -7.)
            .with_rotation(Quat::from_rotation_y(0.4))
            .with_scale(Vec3::new(2., 3., 4.)),
    );
    let mut rows = Vec::new();
    source_world::append(&mut rows, &collider, &pose, 7);
    assert_eq!(rows.len(), 2);
    let (_, rotation, translation) = pose.to_scale_rotation_translation();
    for row in &rows {
        assert_eq!(row["sourceWorld"], true);
        assert_eq!(row["combat"], false);
        assert_eq!(row["vehicleNetId"], 7);
    }
    let points: Vec<[f32; 3]> = serde_json::from_value(rows[0]["vertices"].clone()).unwrap();
    let points: Vec<_> = points
        .into_iter()
        .map(|p| rotation.inverse() * (Vec3::from_array(p) - translation))
        .collect();
    let lo = points
        .iter()
        .fold(Vec3::splat(f32::INFINITY), |a, p| a.min(*p));
    let hi = points
        .iter()
        .fold(Vec3::splat(f32::NEG_INFINITY), |a, p| a.max(*p));
    assert!(
        (lo - Vec3::new(2., -6., -12.)).length() < 0.0001,
        "raw compound offset/scale: {lo}"
    );
    assert!(
        (hi - Vec3::new(6., 6., 12.)).length() < 0.0001,
        "global scale must not be applied twice: {hi}"
    );
    assert_eq!(
        rows[1]["indices"].as_array().unwrap().len(),
        12,
        "actual tetrahedron, not its AABB"
    );
}

fn pump(bridge: &CoreBridge) -> serde_json::Value {
    let mut state = serde_json::Value::Null;
    for _ in 0..12 {
        state = bridge.request("tick", json!({})).unwrap()["state"].clone();
    }
    state
}
fn station(bridge: &CoreBridge) -> Vec<String> {
    bridge
        .request(
            "action",
            json!({"name":"craftingContext","args":{"eye":[0,1.75,2]}}),
        )
        .unwrap();
    let state = pump(bridge);
    let fact = state["events"]
        .as_array()
        .unwrap()
        .iter()
        .rev()
        .find(|e| e["kind"] == "craftingContext")
        .unwrap();
    assert_eq!(fact["data"]["result"], "Ready");
    serde_json::from_value(fact["data"]["tags"].clone()).unwrap()
}

#[test]
fn actual_native_uploader_station_query_distinguishes_model_and_changed_fallback_collider() {
    let bridge = CoreBridge::launch(std::path::Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
    bridge.request("init",json!({"contentRoot":WorldConfig::default().content_root,"spawn":[0,0,2],"allowCheats":true,"starterKit":true})).unwrap();
    bridge
        .request(
            "action",
            json!({"name":"console","args":{"text":"give 1916"}}),
        )
        .unwrap();
    pump(&bridge);
    bridge
        .request(
            "action",
            json!({"name":"placeDeployable","args":{"defId":1916,"pos":[0,0,0],"yawDegrees":0}}),
        )
        .unwrap();
    let registration = crate::vehicle::registration(
        data::named("jeep").unwrap(),
        "station obstruction".into(),
        Vec3::new(0., 1., 1.),
        0.,
        0,
    );
    let id = bridge
        .request("worldVehicles", json!({"vehicles":[registration]}))
        .unwrap()["result"]["vehicles"][0]["netId"]
        .as_u64()
        .unwrap() as u32;
    let state = pump(&bridge);
    assert!(
        state["deployables"]
            .as_array()
            .unwrap()
            .iter()
            .any(|d| d["defId"] == 1916)
    );
    assert_eq!(station(&bridge).len(), 1);
    let mut model = UiModel::default();
    model.adopt_snapshot(&state);
    let mut app = App::new();
    app.add_plugins((MinimalPlugins, TransformPlugin, AssetPlugin::default()))
        .init_asset::<Mesh>()
        .insert_resource(bridge)
        .insert_resource(model)
        .insert_resource(GameSnapshot(state))
        .init_resource::<crate::player::WorldSessionEpoch>()
        .init_resource::<WorldConfig>()
        .init_resource::<WorldGeometry>()
        .init_resource::<GeometryUploads>()
        .insert_resource(WorldState {
            ready: true,
            ..default()
        })
        .add_systems(PostUpdate, upload.after(TransformSystems::Propagate));
    let body = crate::vehicle::tests::body(&mut app, "jeep", Vec3::new(0., 1., 1.));
    app.world_mut().get_mut::<VehicleBody>(body).unwrap().net_id = id;
    app.world_mut().entity_mut(body).insert((
        Collider::cuboid(1., 1., 0.1),
        CollisionGroups::new(Group::GROUP_4, Group::GROUP_1 | Group::GROUP_4),
    ));
    let mesh = app
        .world_mut()
        .resource_mut::<Assets<Mesh>>()
        .add(Cuboid::new(2., 2., 0.2));
    app.world_mut().spawn((
        Mesh3d(mesh),
        Transform::IDENTITY,
        Visibility::Inherited,
        ChildOf(body),
    ));
    app.update();
    let bridge = app.world().resource::<CoreBridge>();
    assert_eq!(
        station(bridge).len(),
        1,
        "source HitMesh never has world bit0"
    );
    assert_eq!(
        bridge
            .request("worldRay", json!({"from":[0,1.75,2],"to":[0,0,0]}))
            .unwrap()["result"]["hit"],
        true,
        "same rendered model still blocks the established combat ray"
    );
    app.world_mut()
        .get_mut::<CollisionGroups>(body)
        .unwrap()
        .filters
        .insert(Group::GROUP_2);
    app.update();
    assert!(
        station(app.world().resource::<CoreBridge>()).is_empty(),
        "fallback chassis retains world bit0"
    );
    // No pose, artwork, tick or revision changes: completed physics geometry
    // itself must invalidate the uploader's previous stamp.
    app.world_mut()
        .entity_mut(body)
        .insert(Collider::compound(vec![(
            Vec3::X * 10.,
            Quat::IDENTITY,
            Collider::cuboid(1., 1., 0.1),
        )]));
    app.update();
    assert_eq!(
        station(app.world().resource::<CoreBridge>()).len(),
        1,
        "changed actual collider must replace old world obstruction"
    );
    app.world_mut().entity_mut(body).despawn();
    app.update();
    assert_eq!(
        app.world()
            .resource::<CoreBridge>()
            .request("worldRay", json!({"from":[0,1.75,2],"to":[0,0,0]}))
            .unwrap()["result"]["hit"],
        false,
        "retiring the vehicle clears both authority geometry classes"
    );
}
