use super::*;
use bevy::ecs::system::RunSystemOnce;

fn snapshot(driver: u32, gunner: u32) -> Value {
    let tank = super::super::super::data::named("tank").unwrap();
    json!({"connected":true,"playerId":1,"player":{"alive":true},
        "vehicles":[{"netId":71,"typeId":tank.type_id,"driverPlayerId":driver,"passengers":[gunner]}],
        "vehicleMounts":[{"vehicleNetId":71,"headOut":false,"mounts":[
            {"index":0,"yaw":37.,"pitch":12.,"ammo":59,"cooldownSeconds":1.5,"crewAlive":true},
            {"index":1,"yaw":-15.,"pitch":5.,"ammo":398,"cooldownSeconds":0.,"crewAlive":true}]}]})
}
#[test]
fn live_seat_priority_keeps_driver_selection_through_gunner_takeover() {
    let mut operator = Operator::default();
    operator.refresh(&snapshot(1, 0), 4);
    assert_eq!(operator.active().unwrap().definition.gun_id, "tank_cannon");
    assert_eq!(operator.active().unwrap().ammo, Some(59));
    assert!(
        operator.trigger_live(false),
        "source fallback driver aims continuously without RMB"
    );
    operator.select(1);
    assert_eq!(operator.active().unwrap().definition.gun_id, "hmg");
    operator.refresh(&snapshot(1, 2), 4);
    assert!(operator.active().is_none());
    assert!(!operator.consumes_slot(1));
    assert_eq!(
        operator.slot, 1,
        "priority changes do not reset weapon selection"
    );
    operator.refresh(&snapshot(1, 0), 4);
    assert_eq!(operator.active().unwrap().ammo, Some(398));
    operator.refresh(&snapshot(2, 1), 4);
    assert_eq!(
        operator.slot, 1,
        "source TrySwitchSeat preserves selection within the same vehicle"
    );
    assert!(operator.look_unlimited());
    operator.toggle_head_out();
    assert!(
        !operator.head_out,
        "request never mutates accepted presentation"
    );
    assert_eq!(operator.head_request, Some(true));
    let mut accepted = snapshot(2, 1);
    accepted["vehicleMounts"][0]["headOut"] = json!(true);
    operator.refresh(&accepted, 4);
    assert!(operator.head_out && operator.head_request.is_none());
    operator.select(1);
    operator.refresh(&accepted, 5);
    assert_eq!(
        operator.slot, 0,
        "session reuse cannot inherit previous selection"
    );
}
#[test]
fn absent_or_invalid_mount_replica_never_invents_ammo() {
    let mut operator = Operator::default();
    let mut state = snapshot(1, 0);
    state["vehicleMounts"] = json!([]);
    operator.refresh(&state, 0);
    assert_eq!(operator.active().unwrap().ammo, None);
    state = snapshot(1, 0);
    state["vehicleMounts"][0]["mounts"][1]["index"] = json!(0);
    operator.refresh(&state, 0);
    assert_eq!(operator.active().unwrap().ammo, None);
    state = snapshot(1, 0);
    state["player"]["alive"] = json!(false);
    operator.refresh(&state, 0);
    assert!(operator.seat.is_none() && operator.active().is_none());
}

#[test]
fn actual_crosshair_skips_nested_own_barrel_and_queries_water_and_remote_bodies() {
    let mut app = App::new();
    app.add_plugins((
        MinimalPlugins,
        TransformPlugin,
        RapierPhysicsPlugin::<NoUserData>::default(),
    ));
    let collider = |z, group| {
        (
            Transform::from_xyz(0., 0., z),
            Collider::cuboid(0.5, 0.5, 0.5),
            CollisionGroups::new(group, Group::ALL),
        )
    };
    let owner = app.world_mut().spawn(collider(0., Group::GROUP_2)).id();
    let hull = app.world_mut().spawn(collider(-2., Group::GROUP_4)).id();
    let yaw = app
        .world_mut()
        .spawn((Transform::IDENTITY, ChildOf(hull)))
        .id();
    app.world_mut()
        .spawn((collider(-2., Group::GROUP_4), ChildOf(yaw)));
    let target = app.world_mut().spawn(collider(-9., Group::GROUP_15)).id();
    app.world_mut().spawn(collider(-20., Group::GROUP_1));
    for _ in 0..3 {
        app.update();
    }
    let ray = move |context: ReadRapierContext,
                    parents: Query<&ChildOf>,
                    oceans: Query<(&crate::world::OceanBox, &Transform)>| {
        crosshair(
            &context.single().unwrap(),
            &Transform::IDENTITY,
            hull,
            owner,
            &parents,
            &oceans,
        )
    };
    let hit = app.world_mut().run_system_once(ray).unwrap();
    assert!(
        hit.distance(Vec3::new(0., 0., -8.5)) < 0.001,
        "own hull/barrel are excluded; accepted remote player is a target: {hit}"
    );
    app.world_mut()
        .entity_mut(target)
        .insert(CollisionGroups::new(Group::GROUP_6, Group::GROUP_1));
    app.update();
    assert!(
        app.world_mut().run_system_once(ray).unwrap().distance(hit) < 0.001,
        "the original zombie layer participates too"
    );
    // A different car's stripped chassis is not its visible model. Keep the
    // unrestricted query membership for enemy hit layers, but ignore that hull.
    app.world_mut().spawn((
        Transform::from_xyz(0., 0., -4.),
        Collider::cuboid(0.5, 0.5, 0.5),
        CollisionGroups::new(Group::GROUP_4, Group::ALL & !Group::GROUP_2),
    ));
    for _ in 0..2 {
        app.update();
    }
    assert!(app.world_mut().run_system_once(ray).unwrap().distance(hit) < 0.001);
    let surface = app
        .world_mut()
        .spawn((
            Transform::from_xyz(0., 0., -6.),
            Collider::cuboid(0.5, 0.5, 0.5),
            CollisionGroups::new(Group::GROUP_4, Group::GROUP_2),
        ))
        .id();
    for _ in 0..2 {
        app.update();
    }
    assert!(
        (app.world_mut().run_system_once(ray).unwrap().z + 5.5).abs() < 0.001,
        "actual vehicle model still intercepts the aim"
    );
    app.world_mut().despawn(surface);
    app.world_mut().spawn((
        crate::world::OceanBox {
            size: Vec3::new(20., 0.2, 20.),
        },
        Transform::from_xyz(0., -2., -10.),
    ));
    let water_hit = app
        .world_mut()
        .run_system_once(
            move |context: ReadRapierContext,
                  parents: Query<&ChildOf>,
                  oceans: Query<(&crate::world::OceanBox, &Transform)>| {
                crosshair(
                    &context.single().unwrap(),
                    &Transform::IDENTITY.looking_to(Vec3::new(0., -1., -1.), Vec3::Y),
                    hull,
                    owner,
                    &parents,
                    &oceans,
                )
            },
        )
        .unwrap();
    assert!(
        water_hit.distance(Vec3::new(0., -1.9, -1.9)) < 0.001,
        "native ocean has no solid player collider but owns the source aim intersection: {water_hit}"
    );
}

#[test]
fn native_fixed_input_refreshes_belt_fire_during_slow_frames_and_releases_for_menus() {
    let bridge = CoreBridge::launch(Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
    bridge
        .request(
            "init",
            json!({"contentRoot":crate::world::WorldConfig::default().content_root,
        "spawn":[0,0,0],"starterKit":true}),
        )
        .unwrap();
    for _ in 0..12 {
        bridge.request("tick", json!({})).unwrap();
    }
    let tank = super::super::super::data::named("tank").unwrap();
    let reply = bridge
        .request(
            "worldVehicles",
            json!({"vehicles":[{"typeId":tank.type_id,
        "key":"native-mount-input","position":[0,0,0],"health":10000,"fuel":100,
        "seatCount":2,"speedMax":40,"climbMax":20,"fallMax":100}]}),
        )
        .unwrap();
    let net_id = reply["result"]["vehicles"][0]["netId"].as_u64().unwrap() as u32;
    bridge
        .request(
            "action",
            json!({"name":"enterVehicle","args":{"netId":net_id,"seat":1}}),
        )
        .unwrap();
    let mut snapshot = Value::Null;
    for _ in 0..12 {
        snapshot = bridge.request("tick", json!({})).unwrap()["state"].clone();
    }
    let rifle = snapshot["weapon"]["ammo"].clone();
    let mut operator = Operator::default();
    operator.refresh(&snapshot, 0);
    operator.select(1);
    assert_eq!(operator.active().unwrap().ammo, Some(400));
    let mut model = UiModel::default();
    model.adopt_snapshot(&snapshot);
    let mut app = App::new();
    app.add_plugins(MinimalPlugins)
        .insert_resource(Time::<Fixed>::from_hz(50.))
        .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
            std::time::Duration::from_millis(200),
        ))
        .insert_resource(bridge)
        .insert_resource(operator)
        .insert_resource(model)
        .insert_resource(GameSnapshot(snapshot))
        .insert_resource(UiState {
            screen: crate::ui::Screen::Playing,
            ..default()
        })
        .init_resource::<Settings>()
        .init_resource::<ButtonInput<KeyCode>>()
        .init_resource::<ButtonInput<MouseButton>>()
        .init_resource::<PlayerInput>()
        .add_systems(
            FixedUpdate,
            (
                send,
                |bridge: Res<CoreBridge>, mut snapshot: ResMut<GameSnapshot>| {
                    snapshot.0 = bridge.request("tick", json!({})).unwrap()["state"].clone();
                },
            )
                .chain(),
        );
    let body = app
        .world_mut()
        .spawn(PlayerBody::for_motion_test(Vec3::ZERO, false, "driving"))
        .id();
    let hull = app.world_mut().spawn_empty().id();
    app.world_mut()
        .insert_resource(super::super::super::Occupancy {
            seat: Some(super::super::super::OccupiedSeat {
                vehicle: hull,
                net_id,
                seat: 1,
                body_pose: Transform::IDENTITY,
                eye_pose: Transform::IDENTITY,
                velocity: Vec3::ZERO,
                driver: false,
                aircraft: false,
            }),
        });
    app.world_mut()
        .spawn((PlayerCamera, Transform::from_xyz(0., 2.8, 0.)));
    app.world_mut()
        .resource_mut::<ButtonInput<MouseButton>>()
        .press(MouseButton::Left);
    for _ in 0..6 {
        app.update();
    }
    let state = &app.world().resource::<GameSnapshot>().0;
    let shots = state["events"]
        .as_array()
        .unwrap()
        .iter()
        .filter(|e| e["kind"] == "mountShot")
        .count();
    assert!(
        shots >= 6,
        "200ms render frames must keep feeding the 50Hz authority, shots={shots}"
    );
    assert_eq!(state["vehicleMounts"][0]["mounts"][0]["ammo"], 60);
    assert_eq!(
        state["weapon"]["ammo"], rifle,
        "separate mounted belt never spends held-rifle ammo"
    );
    app.world_mut().resource_mut::<UiState>().screen = crate::ui::Screen::Inventory;
    for _ in 0..6 {
        app.update();
    }
    let state = &app.world().resource::<GameSnapshot>().0;
    // Shot events can arrive ahead of the throttled mount replica; compare the
    // settled accepted belt after release, rather than predicting its value.
    assert_eq!(
        state["vehicleMounts"][0]["mounts"][1]["ammo"],
        400 - shots,
        "opening a menu releases held fire before its next cycle"
    );
    assert!(app.world().get::<PlayerBody>(body).is_some());
    app.world_mut().resource_mut::<UiState>().screen = crate::ui::Screen::Paused;
    app.world_mut().resource_mut::<Operator>().head_request = Some(true);
    for _ in 0..10 {
        app.world_mut().run_system_once(send).unwrap();
    }
    let bridge = app.world().resource::<CoreBridge>();
    let mut state = Value::Null;
    for _ in 0..12 {
        state = bridge.request("tick", json!({})).unwrap()["state"].clone();
    }
    assert_eq!(
        state["vehicleMounts"][0]["headOut"], false,
        "SP pause cannot enqueue hatch intents for later replay"
    );
    app.world_mut().resource_mut::<UiState>().networked = true;
    app.world_mut().run_system_once(send).unwrap();
    for _ in 0..12 {
        state = app
            .world()
            .resource::<CoreBridge>()
            .request("tick", json!({}))
            .unwrap()["state"]
            .clone();
    }
    assert_eq!(
        state["vehicleMounts"][0]["headOut"], true,
        "an MP menu continues publishing its accepted control lane"
    );
}
