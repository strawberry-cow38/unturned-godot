use super::*;
use crate::ui::Screen;
use std::time::Duration;

#[test]
fn source_leaf_rotates_around_fixed_hinge_with_matching_world_box() {
    for yaw in [0., 0.7, 1.57] {
        let d = Definition {
            base: Vec3::new(12., 4., -17.),
            yaw,
        };
        for swing in [0., 0.5, 1.] {
            let pose = d.pose(swing);
            assert!((pose.transform_point(Vec3::new(-0.5, -1., 0.)) - d.hinge()).length() < 1e-5);
            let geometry = d.geometry(swing);
            let vertices = geometry["meshes"][0]["vertices"].as_array().unwrap();
            assert_eq!(vertices.len(), 36);
            for p in vertices {
                let p = crate::player::vector(p).unwrap();
                let local = pose.rotation.inverse() * (p - pose.translation);
                assert!((local.abs() - SIZE * 0.5).length() < 1e-5);
            }
        }
    }
}
fn app(enabled: bool) -> App {
    let config = WorldConfig {
        map_name: "Fixture".into(),
        map_root: None,
        ..default()
    };
    let bridge = CoreBridge::launch(std::path::Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
    let reply = bridge
        .request(
            "init",
            json!({"contentRoot":config.content_root,"spawn":[-3,0,-345],"starterKit":true}),
        )
        .unwrap();
    let mut options = crate::cli::Options::default();
    options.demo_furniture = enabled;
    let mut model = UiModel::default();
    model.adopt_snapshot(&reply["state"]);
    let mut app = App::new();
    app.add_plugins((
        MinimalPlugins,
        TransformPlugin,
        AssetPlugin::default(),
        RapierPhysicsPlugin::<NoUserData>::default(),
    ))
    .insert_resource(config)
    .insert_resource(crate::LaunchOptions(options))
    .insert_resource(bridge)
    .insert_resource(model)
    .insert_resource(GameSnapshot(reply["state"].clone()))
    .insert_resource(UiState {
        screen: Screen::Playing,
        ..default()
    })
    .insert_resource(WorldState {
        ready: true,
        ..default()
    })
    .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
        Duration::from_millis(20),
    ))
    .insert_resource(Time::<Fixed>::from_hz(50.))
    .init_resource::<WorldSessionEpoch>()
    .init_resource::<WorldGeometry>()
    .init_resource::<WorldOrigin>()
    .init_resource::<Settings>()
    .init_resource::<InteractionFocus>()
    .init_resource::<super::super::interactables::PropOccupancy>()
    .init_resource::<crate::vehicle::Occupancy>()
    .init_resource::<ButtonInput<KeyCode>>()
    .init_resource::<ButtonInput<MouseButton>>()
    .init_asset::<AudioSource>()
    .init_asset::<Mesh>()
    .init_asset::<StandardMaterial>()
    .add_plugins(DemoDoorPlugin);
    app
}
fn tick(app: &mut App, count: usize) {
    for _ in 0..count {
        let state = app
            .world()
            .resource::<CoreBridge>()
            .request("tick", json!({}))
            .unwrap()["state"]
            .clone();
        app.world_mut().resource_mut::<GameSnapshot>().0 = state;
        app.update();
    }
}
fn action(app: &App, name: &str, args: Value) {
    app.world()
        .resource::<CoreBridge>()
        .request("action", json!({"name":name,"args":args}))
        .unwrap();
}
#[test]
fn actual_accepted_toggle_animates_direct_body_and_disconnect_retires_it() {
    let mut app = app(true);
    app.update();
    tick(&mut app, 8);
    let entity = app.world().resource::<Demo>().entity.unwrap();
    assert_eq!(
        app.world().get::<Transform>(entity).unwrap().translation,
        Vec3::new(-3., 1., -348.)
    );
    assert_eq!(
        app.world()
            .get::<Collider>(entity)
            .unwrap()
            .as_cuboid()
            .unwrap()
            .half_extents(),
        SIZE * 0.5
    );
    assert_eq!(
        app.world()
            .get::<CollisionGroups>(entity)
            .unwrap()
            .memberships,
        Group::GROUP_1
    );
    action(&app, "toggleDoor", json!({"netId":ID}));
    tick(&mut app, 30);
    assert_eq!(
        state(&app.world().resource::<GameSnapshot>().0),
        Some((true, false))
    );
    assert_eq!(app.world().resource::<Demo>().swing, 1.);
    assert!(
        (app.world().get::<Transform>(entity).unwrap().translation - Vec3::new(-3.5, 1., -348.5))
            .length()
            < 1e-5
    );
    // Original source cooldown refuses another immediate toggle, then allows it.
    tick(&mut app, 15);
    action(&app, "toggleDoor", json!({"netId":ID}));
    tick(&mut app, 30);
    assert_eq!(app.world().resource::<Demo>().swing, 0.);
    app.world_mut().resource_mut::<UiModel>().connected = false;
    app.world_mut().resource_mut::<GameSnapshot>().0 = Value::Null;
    app.update();
    assert!(app.world().get_entity(entity).is_err());
}
#[test]
fn tap_release_and_hold_lock_send_once_and_wait_for_authority() {
    let mut app = app(true);
    app.update();
    tick(&mut app, 8);
    app.world_mut()
        .resource_mut::<InteractionFocus>()
        .offer_manual(1., "Door", "toggleDoor", json!({"netId":ID}));
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .press(KeyCode::KeyF);
    app.update();
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .clear();
    tick(&mut app, 8);
    assert_eq!(
        state(&app.world().resource::<GameSnapshot>().0),
        Some((false, false)),
        "press alone cannot toggle"
    );
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .release(KeyCode::KeyF);
    app.update();
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .clear();
    tick(&mut app, 40);
    assert_eq!(
        state(&app.world().resource::<GameSnapshot>().0),
        Some((true, false))
    );
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .press(KeyCode::KeyF);
    app.update();
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .clear();
    tick(&mut app, 60);
    assert_eq!(
        state(&app.world().resource::<GameSnapshot>().0),
        Some((true, true))
    );
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .release(KeyCode::KeyF);
    app.update();
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .clear();
    tick(&mut app, 40);
    assert_eq!(
        state(&app.world().resource::<GameSnapshot>().0),
        Some((true, true)),
        "hold release cannot also toggle"
    );
}
#[test]
fn default_retail_never_registers_and_eight_accepted_shots_remove_source250hp_leaf() {
    let mut app = app(false);
    app.update();
    tick(&mut app, 8);
    assert!(app.world().resource::<Demo>().definition.is_none());
    assert!(state(&app.world().resource::<GameSnapshot>().0).is_none());
    app.world_mut()
        .resource_mut::<crate::LaunchOptions>()
        .0
        .demo_furniture = true;
    app.update();
    tick(&mut app, 8);
    let entity = app.world().resource::<Demo>().entity.unwrap();
    for shot in 1..=8 {
        action(&app, "fire", json!({"origin":[-3,1.6,-345],"dir":[0,0,-1]}));
        tick(&mut app, 12);
        assert_eq!(app.world().get_entity(entity).is_ok(), shot < 8);
    }
    assert!(state(&app.world().resource::<GameSnapshot>().0).is_none());
    assert!(app.world().get::<Collider>(entity).is_none());
    assert_eq!(
        app.world().resource::<GameSnapshot>().0["weapon"]["ammo"],
        22
    );
}
#[test]
fn origin_shift_preserves_hinge_and_forces_host_geometry_refresh() {
    let mut app = app(true);
    app.update();
    tick(&mut app, 8);
    let before = app.world().resource::<Demo>().definition.unwrap();
    shift_origin(app.world_mut(), Vec3::new(256., 0., -512.));
    let after = app.world().resource::<Demo>().definition.unwrap();
    assert!((after.hinge() - (before.hinge() - Vec3::new(256., 0., -512.))).length() < 1e-5);
    assert!(app.world().resource::<Demo>().exported.is_none());
}

#[test]
fn remote_replica_never_registers_local_authority_and_hold_cancels_on_focus_loss() {
    let mut app = app(true);
    app.world_mut().resource_mut::<GameSnapshot>().0["network"] =
        json!({"mode":"client","ownsWorld":false});
    app.world_mut().resource_mut::<GameSnapshot>().0["interactables"]["doors"] =
        json!([{"netId":1,"open":false,"locked":false}]);
    app.update();
    assert!(app.world().resource::<Demo>().entity.is_some());
    assert!(!app.world().resource::<Demo>().registered);
    assert!(app.world().resource::<Demo>().exported.is_none());
    let actual = app
        .world()
        .resource::<CoreBridge>()
        .request("snapshot", json!({}))
        .unwrap();
    assert!(state(&actual["state"]).is_none());
    app.world_mut()
        .resource_mut::<InteractionFocus>()
        .offer_manual(1., "Door", "toggleDoor", json!({"netId":ID}));
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .press(KeyCode::KeyF);
    app.update();
    assert!(app.world().resource::<Hold>().elapsed.is_some());
    *app.world_mut().resource_mut::<InteractionFocus>() = default();
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .clear();
    app.update();
    assert!(app.world().resource::<Hold>().elapsed.is_none());
}
