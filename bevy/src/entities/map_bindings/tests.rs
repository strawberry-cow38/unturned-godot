use super::*;

#[test]
fn registry_changes_invalidate_bindings_but_animation_and_vitals_do_not() {
    let mut snapshot = json!({"containers":[{"netId":1,"kindId":2,"position":[1,2,3],"yaw":90,"doorsOpen":false}],
        "deployables":[{"netId":3,"defId":4,"position":[5,6,7],"yaw":180,"fuel":20}]});
    let old = registry_signature(&snapshot);
    snapshot["containers"][0]["doorsOpen"] = json!(true);
    snapshot["deployables"][0]["fuel"] = json!(10);
    assert_eq!(old, registry_signature(&snapshot));
    snapshot["containers"][0]["netId"] = json!(5);
    assert_ne!(old, registry_signature(&snapshot));
    snapshot["containers"][0]["netId"] = json!(1);
    snapshot["deployables"][0]["position"] = json!([1, 2, 3]);
    assert_ne!(old, registry_signature(&snapshot));
}

fn bridge() -> CoreBridge {
    CoreBridge::launch(std::path::Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap()
}
fn application(bridge: CoreBridge) -> App {
    let mut app = App::new();
    let config = WorldConfig {
        map_root: Some(
            WorldConfig::default()
                .content_root
                .join("test-map-no-retail-required"),
        ),
        ..default()
    };
    app.add_plugins(MinimalPlugins)
        .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
            std::time::Duration::from_millis(260),
        ))
        .insert_resource(bridge)
        .insert_resource(config)
        .init_resource::<WorldGeometry>()
        .insert_resource(WorldState {
            ready: true,
            spawn: Vec3::new(5., 10., 15.),
            ..default()
        })
        .init_resource::<GameSnapshot>()
        .init_resource::<UiModel>()
        .init_resource::<ServiceManifest>()
        .init_resource::<Bindings>()
        .init_resource::<WorldSessionEpoch>()
        .add_systems(Update, adopt);
    app
}
fn pump(host: &CoreBridge, app: &mut App, count: usize) {
    for _ in 0..count {
        host.request("tick", json!({})).unwrap();
        let reply = app
            .world()
            .resource::<CoreBridge>()
            .request("tick", json!({}))
            .unwrap();
        app.world_mut()
            .resource_mut::<UiModel>()
            .adopt_snapshot(&reply["state"]);
        app.world_mut().resource_mut::<GameSnapshot>().0 = reply["state"].clone();
        app.update();
        std::thread::sleep(std::time::Duration::from_millis(2));
    }
}
#[test]
fn native_binding_consumer_adopts_real_udp_late_ids_without_registering_authority() {
    let port = std::net::UdpSocket::bind("127.0.0.1:0")
        .unwrap()
        .local_addr()
        .unwrap()
        .port();
    let content = WorldConfig::default().content_root;
    let host = bridge();
    host.request(
        "init",
        json!({"contentRoot":content,"mapId":"PEI","listenPort":port,"spawn":[0,35,0]}),
    )
    .unwrap();
    let remote = bridge();
    remote.request("init", json!({"contentRoot":content,"mapId":"PEI","connectHost":"127.0.0.1","connectPort":port})).unwrap();
    let mut app = application(remote);
    pump(&host, &mut app, 30);
    assert!(app.world().resource::<UiModel>().connected);
    let manifest = app.world().resource::<ServiceManifest>();
    assert_eq!(
        manifest.map, "PEI",
        "production consumer must adopt read-only mapBindings"
    );
    assert!(!manifest.object_doors.is_empty());
    assert!(
        manifest.container_doors.is_empty(),
        "missing replica IDs cannot be invented from ordinals"
    );
    assert!(!manifest.seats.is_empty());
    let initial_revision = manifest.revision;
    let expected = host.request("mapServices", json!({"contentRoot":content,"mapName":"PEI","mapRoot":content.join("test-map-no-retail-required")})).unwrap();
    pump(&host, &mut app, 45);
    let manifest = app.world().resource::<ServiceManifest>();
    assert!(
        manifest.revision > initial_revision,
        "late registry membership must rebuild native bindings"
    );
    assert_eq!(
        manifest.container_doors,
        expected["result"]["containerDoors"]
            .as_array()
            .unwrap()
            .clone()
    );
    assert_eq!(
        manifest.resources.len(),
        expected["result"]["resources"].as_array().unwrap().len()
    );
    for row in expected["result"]["objects"].as_array().unwrap() {
        let actual = &manifest.objects[&(row["sourceIndex"].as_u64().unwrap() as usize)];
        for field in [
            "containerNetId",
            "fixtureNetId",
            "objectDoorNetId",
            "seatNetIds",
        ] {
            assert_eq!(
                actual[field], row[field],
                "source row {} {field}",
                row["sourceIndex"]
            );
        }
    }
    let diagnostics = app
        .world()
        .resource::<CoreBridge>()
        .request("diagnostics", json!({}))
        .unwrap();
    assert_eq!(diagnostics["result"]["host"], false);
    let stable_revision = manifest.revision;
    pump(&host, &mut app, 3);
    assert_eq!(
        app.world().resource::<ServiceManifest>().revision,
        stable_revision,
        "unchanged registry must keep existing animated leaves"
    );
    app.world()
        .resource::<CoreBridge>()
        .request("disconnect", json!({}))
        .unwrap();
    app.world_mut().resource_mut::<GameSnapshot>().0 = Value::Null;
    app.world_mut().resource_mut::<UiModel>().connected = false;
    app.update();
    assert!(app.world().resource::<ServiceManifest>().objects.is_empty());
    assert!(app.world().resource::<ServiceManifest>().seats.is_empty());
}

#[test]
fn accepted_holiday_rebuild_preserves_live_pose_and_defers_bindings_until_geometry_matches() {
    let mut app = application(bridge());
    // The host owns this holiday; this renderer must not consult its own date.
    let snapshot = json!({"connected":true,"network":{"ownsWorld":false,"mode":"client","holiday":"CHRISTMAS"}});
    app.world_mut()
        .resource_mut::<UiModel>()
        .adopt_snapshot(&snapshot);
    app.world_mut().resource_mut::<GameSnapshot>().0 = snapshot;
    app.world_mut().resource_mut::<WorldGeometry>().holiday = Some("NONE".into());
    app.world_mut().resource_mut::<ServiceManifest>().adopt(
        "old",
        "NONE",
        &json!({"seats":[{"netId":99}]}),
    );
    app.update();
    assert_eq!(app.world().resource::<WorldConfig>().holiday, "CHRISTMAS");
    assert!(
        app.world().resource::<WorldState>().ready,
        "holiday change must use streaming rebuild, not reset the session/world frame"
    );
    assert_eq!(
        app.world().resource::<WorldState>().spawn,
        Vec3::new(5., 10., 15.)
    );
    assert_eq!(
        app.world().resource::<ServiceManifest>().seats.len(),
        1,
        "old leaves remain valid until the existing geometry is rebuilt"
    );
    app.update();
    assert!(
        app.world().resource::<Bindings>().signature.is_none(),
        "new bindings wait for matching rendered collision geometry"
    );
    assert!(
        app.world().resource::<Bindings>().last_error.is_none(),
        "no request may reach this deliberately uninitialized authority"
    );
}
