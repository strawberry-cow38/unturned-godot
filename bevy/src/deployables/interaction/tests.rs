use super::*;
use bevy::{
    input::keyboard::{Key, KeyboardInput},
    time::TimeUpdateStrategy,
};
use std::path::Path;

fn pump(host: &CoreBridge, app: &mut App, count: usize) {
    let mut state = Value::Null;
    for _ in 0..count {
        state = app
            .world()
            .resource::<CoreBridge>()
            .request("tick", json!({}))
            .unwrap()["state"]
            .clone();
        host.request("tick", json!({})).unwrap();
        std::thread::sleep(std::time::Duration::from_millis(1));
    }
    app.world_mut()
        .resource_mut::<UiModel>()
        .adopt_snapshot(&state);
    app.world_mut().resource_mut::<GameSnapshot>().0 = state;
}
fn press(app: &mut App, window: Entity, down: bool) {
    let mut events = app.world_mut().resource_mut::<FrameInput>();
    events.begin();
    events.push(WindowEvent::KeyboardInput(KeyboardInput {
        key_code: KeyCode::KeyF,
        logical_key: Key::Character("f".into()),
        state: if down {
            ButtonState::Pressed
        } else {
            ButtonState::Released
        },
        text: None,
        repeat: false,
        window,
    }));
}
fn free_look(app: &mut App, window: Entity, down: bool) {
    app.world_mut()
        .resource_mut::<FrameInput>()
        .push(WindowEvent::KeyboardInput(KeyboardInput {
            key_code: KeyCode::AltLeft,
            logical_key: Key::Alt,
            state: if down {
                ButtonState::Pressed
            } else {
                ButtonState::Released
            },
            text: None,
            repeat: false,
            window,
        }));
}
fn frames(app: &mut App, count: usize) {
    for _ in 0..count {
        app.update();
        app.world_mut().resource_mut::<FrameInput>().begin();
    }
}
#[test]
fn udp_owner_pickup_requires_captured_source_focus_then_waits_for_accepted_removal_and_refund() {
    let port = std::net::UdpSocket::bind("127.0.0.1:0")
        .unwrap()
        .local_addr()
        .unwrap()
        .port();
    let root = Path::new(env!("CARGO_MANIFEST_DIR"));
    let host = CoreBridge::launch(root).unwrap();
    host.request(
        "init",
        json!({"contentRoot":WorldConfig::default().content_root,
        "listenPort":port,"allowCheats":true,"spawn":[0,5,0]}),
    )
    .unwrap();
    let peer = CoreBridge::launch(root).unwrap();
    let init = peer
        .request(
            "init",
            json!({"contentRoot":WorldConfig::default().content_root,
        "connectHost":"127.0.0.1","connectPort":port}),
        )
        .unwrap();
    let mut app = super::super::tests::fixture(peer, init["state"].clone());
    app.insert_resource(UiState {
        screen: crate::ui::Screen::Playing,
        ..default()
    })
    .init_resource::<Settings>()
    .init_resource::<FrameInput>()
    .init_resource::<InteractionFocus>()
    .insert_resource(TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(200),
    ))
    .insert_resource(TimestepMode::Fixed {
        dt: 0.02,
        substeps: 1,
    })
    .add_plugins(RapierPhysicsPlugin::<NoUserData>::default())
    .add_systems(
        Update,
        (|mut f: ResMut<InteractionFocus>| *f = default()).before(CollectInteractions),
    );
    let window = app
        .world_mut()
        .spawn((
            PrimaryWindow,
            CursorOptions {
                visible: false,
                grab_mode: CursorGrabMode::Locked,
                ..default()
            },
        ))
        .id();
    pump(&host, &mut app, 40);
    app.world()
        .resource::<CoreBridge>()
        .request(
            "action",
            json!({"name":"console","args":{"text":"give 1916"}}),
        )
        .unwrap();
    pump(&host, &mut app, 20);
    let position =
        crate::player::vector(&app.world().resource::<GameSnapshot>().0["player"]["position"])
            .unwrap()
            - Vec3::Z * 2.;
    app.world().resource::<CoreBridge>().request("action",json!({"name":"placeDeployable","args":{"defId":1916,"pos":position.to_array(),"yawDegrees":0}})).unwrap();
    pump(&host, &mut app, 20);
    let id = app.world().resource::<GameSnapshot>().0["deployables"][0]["netId"]
        .as_u64()
        .unwrap() as u32;
    frames(&mut app, 3);
    let instance = &app.world().resource::<Replicas>().instances[&id];
    let entity = instance.entity;
    let bounds = app.world().resource::<Replicas>().art[&1916].bounds;
    let pose = *app.world().get::<Transform>(entity).unwrap();
    let center = pose.transform_point((bounds.min + bounds.max) * 0.5);
    let camera = app
        .world_mut()
        .spawn((
            PlayerCamera,
            Transform::from_translation(center + Vec3::Z * 2.4).looking_at(center, Vec3::Y),
        ))
        .id();
    frames(&mut app, 3);
    assert!(
        app.world()
            .resource::<FocusedInfo>()
            .0
            .as_ref()
            .unwrap()
            .content
            .prompt
            .contains("Hold [F]")
    );
    free_look(&mut app, window, true);
    press(&mut app, window, true);
    // The final polled modifier is released, but the original F edge occurred
    // while free-looking and must be consumed without starting a pickup.
    free_look(&mut app, window, false);
    frames(&mut app, 2);
    assert_eq!(
        app.world().resource::<Gesture>().hold.target(),
        None,
        "a new Interact press during event-time free-look cannot start a hold"
    );
    press(&mut app, window, false);
    frames(&mut app, 1);
    let wall = app
        .world_mut()
        .spawn((
            RigidBody::Fixed,
            Collider::cuboid(1., 1., 0.1),
            CollisionGroups::new(Group::GROUP_1, Group::ALL),
            Transform::from_translation(center + Vec3::Z),
        ))
        .id();
    frames(&mut app, 2);
    press(&mut app, window, true);
    frames(&mut app, 8);
    assert!(
        app.world().resource::<Hovered>().0.is_none(),
        "wall must terminate the actual source ray"
    );
    assert!(app.world().resource::<Gesture>().hold.target().is_none());
    app.world_mut().despawn(wall);
    press(&mut app, window, false);
    frames(&mut app, 2);
    // Moving out of reach while held cancels rather than banking progress.
    press(&mut app, window, true);
    frames(&mut app, 2);
    assert!(app.world().resource::<Gesture>().hold.progress() >= 0.2);
    app.world_mut()
        .get_mut::<Transform>(camera)
        .unwrap()
        .translation += Vec3::Z * 4.;
    frames(&mut app, 2);
    assert_eq!(app.world().resource::<Gesture>().hold.target(), None);
    app.world_mut()
        .get_mut::<Transform>(camera)
        .unwrap()
        .translation -= Vec3::Z * 4.;
    frames(&mut app, 8);
    assert!(
        app.world().get_entity(entity).is_ok(),
        "looking back while still held must not start another gesture"
    );
    press(&mut app, window, false);
    frames(&mut app, 1);
    press(&mut app, window, true);
    frames(&mut app, 8);
    assert!(
        app.world().get_entity(entity).is_ok(),
        "request cannot optimistically retire its replica"
    );
    assert_eq!(app.world().resource::<Gesture>().hold.target(), None);
    pump(&host, &mut app, 25);
    frames(&mut app, 2);
    assert!(
        app.world().get_entity(entity).is_err(),
        "accepted removal retires actual rendered hierarchy"
    );
    let state = &app.world().resource::<GameSnapshot>().0;
    assert!(
        state["deployables"]
            .as_array()
            .unwrap()
            .iter()
            .all(|r| r["netId"] != id)
    );
    let amount: u64 = state["inventory"]["pages"]
        .as_array()
        .unwrap()
        .iter()
        .flat_map(|p| p["items"].as_array().into_iter().flatten())
        .filter(|i| i["id"] == 1916)
        .map(|i| i["amount"].as_u64().unwrap())
        .sum();
    assert_eq!(
        amount, 1,
        "only accepted owner inventory refunds the original item"
    );
}

#[test]
fn singleplayer_pause_retains_an_unsettled_generator_ramp_and_resume_continues_it() {
    let bridge = CoreBridge::launch(Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
    let mut state = bridge
        .request(
            "init",
            json!({"contentRoot":WorldConfig::default().content_root,"spawn":[0,5,0]}),
        )
        .unwrap()["state"]
        .clone();
    state["deployables"] = json!([{"netId":4005,"defId":458,"position":[0,5,-2],"yaw":0,
        "health":450,"fuel":10000,"onFire":false,"toggledOn":true}]);
    let mut app = super::super::tests::fixture(bridge, state);
    app.insert_resource(UiState {
        screen: crate::ui::Screen::Playing,
        ..default()
    })
    .insert_resource(TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(200),
    ));
    for _ in 0..3 {
        app.update();
    }
    let before = app.world().resource::<super::super::engine::Ramps>().levels[&4005];
    assert!(before > 0. && before < 1.);
    app.world_mut().resource_mut::<UiState>().screen = crate::ui::Screen::Paused;
    for _ in 0..20 {
        app.update();
    }
    assert_eq!(
        app.world().resource::<super::super::engine::Ramps>().levels[&4005],
        before,
        "menu wall-clock time cannot prematurely settle source CanTogglePower"
    );
    app.world_mut().resource_mut::<UiState>().screen = crate::ui::Screen::Playing;
    app.update();
    assert!(app.world().resource::<super::super::engine::Ramps>().levels[&4005] > before);
}

fn mouse(app: &mut App, window: Entity, button: MouseButton) {
    let mut events = app.world_mut().resource_mut::<FrameInput>();
    events.begin();
    for state in [ButtonState::Pressed, ButtonState::Released] {
        events.push(WindowEvent::MouseButtonInput(
            bevy::input::mouse::MouseButtonInput {
                button,
                state,
                window,
            },
        ));
    }
}
fn look_at(app: &mut App, camera: Entity, target: Vec3) {
    *app.world_mut().get_mut::<Transform>(camera).unwrap() =
        Transform::from_translation(target + Vec3::new(0., 0.2, 2.)).looking_at(target, Vec3::Y);
    frames(app, 3);
}
fn panel(app: &App) -> (String, String, f32) {
    let view = app
        .world()
        .resource::<FocusedInfo>()
        .0
        .clone()
        .expect("focused panel");
    (
        view.content.name,
        view.content.prompt,
        view.content.bars[0].map_or(-1., |b| b.value),
    )
}
fn row(app: &App, id: u32) -> Value {
    app.world().resource::<GameSnapshot>().0["deployables"]
        .as_array()
        .unwrap()
        .iter()
        .find(|r| r["netId"] == id)
        .cloned()
        .unwrap()
}
/// The held can as the accepted owner inventory reports it.
fn can_fuel(app: &App) -> f32 {
    let view = app
        .world()
        .resource::<CoreBridge>()
        .request("inventoryView", json!({}))
        .unwrap();
    view["result"]["items"]
        .as_array()
        .unwrap()
        .iter()
        .find(|i| i["address"]["id"] == 28)
        .unwrap()["fuel"]
        .as_f64()
        .unwrap() as f32
}

#[test]
fn udp_owner_fills_a_held_can_at_a_focused_pump_then_pours_it_into_a_focused_generator_from_accepted_state()
 {
    let port = std::net::UdpSocket::bind("127.0.0.1:0")
        .unwrap()
        .local_addr()
        .unwrap()
        .port();
    let root = Path::new(env!("CARGO_MANIFEST_DIR"));
    let host = CoreBridge::launch(root).unwrap();
    host.request(
        "init",
        json!({"contentRoot":WorldConfig::default().content_root,
        "listenPort":port,"allowCheats":true,"spawn":[0,5,0]}),
    )
    .unwrap();
    let peer = CoreBridge::launch(root).unwrap();
    let init = peer
        .request(
            "init",
            json!({"contentRoot":WorldConfig::default().content_root,
        "connectHost":"127.0.0.1","connectPort":port}),
        )
        .unwrap();
    let items: Vec<crate::entities::ItemVisual> = serde_json::from_value(
        peer.request("catalog", json!({})).unwrap()["result"]["items"].clone(),
    )
    .unwrap();
    let mut app = super::super::tests::fixture(peer, init["state"].clone());
    app.world_mut().resource_mut::<ItemCatalog>().items =
        items.into_iter().map(|i| (i.id, i)).collect();
    app.insert_resource(UiState {
        screen: crate::ui::Screen::Playing,
        ..default()
    })
    .init_resource::<Settings>()
    .init_resource::<FrameInput>()
    .init_resource::<InteractionFocus>()
    .insert_resource(TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(200),
    ))
    .insert_resource(TimestepMode::Fixed {
        dt: 0.02,
        substeps: 1,
    })
    .add_plugins(RapierPhysicsPlugin::<NoUserData>::default())
    .add_systems(
        Update,
        (|mut f: ResMut<InteractionFocus>| *f = default()).before(CollectInteractions),
    );
    let window = app
        .world_mut()
        .spawn((
            PrimaryWindow,
            CursorOptions {
                visible: false,
                grab_mode: CursorGrabMode::Locked,
                ..default()
            },
        ))
        .id();
    pump(&host, &mut app, 40);
    let me = crate::player::vector(&app.world().resource::<GameSnapshot>().0["player"]["position"])
        .unwrap();
    // The host registers the map's breaker box and forecourt pump (MapServices shape).
    let (grid_at, pump_at) = (me + Vec3::new(4., 0., -3.), me + Vec3::new(0., 0., -4.));
    let fixtures = host.request("worldServices", json!({"fixtures":[
        {"defId":9200,"position":grid_at.to_array(),"yaw":0},{"defId":9201,"position":pump_at.to_array(),"yaw":0}]})).unwrap();
    let id = |i: usize| fixtures["result"]["fixtures"][i]["netId"].as_u64().unwrap() as u32;
    let (grid, gas) = (id(0), id(1));
    let send = |app: &App, name: &str, args: Value| {
        app.world()
            .resource::<CoreBridge>()
            .request("action", json!({"name":name,"args":args}))
            .unwrap();
    };
    send(&app, "console", json!({"text":"give 28"}));
    send(&app, "console", json!({"text":"give 458"}));
    pump(&host, &mut app, 20);
    let gen_at = me + Vec3::new(-3., 0., -3.);
    send(
        &app,
        "placeDeployable",
        json!({"defId":458,"pos":gen_at.to_array(),"yawDegrees":0}),
    );
    pump(&host, &mut app, 20);
    let generator = app.world().resource::<GameSnapshot>().0["deployables"]
        .as_array()
        .unwrap()
        .iter()
        .find(|r| r["defId"] == 458)
        .unwrap()["netId"]
        .as_u64()
        .unwrap() as u32;
    // Burn a little of the fresh tank so the pour has room (setup via direct intents).
    send(
        &app,
        "toggleDeployable",
        json!({"netId":generator,"on":true}),
    );
    pump(&host, &mut app, 40);
    send(
        &app,
        "toggleDeployable",
        json!({"netId":generator,"on":false}),
    );
    pump(&host, &mut app, 20);
    let tank = row(&app, generator)["fuel"].as_f64().unwrap() as f32;
    assert!(
        tank < 150_000. && tank > 140_000.,
        "a short run burned some fuel ({tank})"
    );
    let cell = app.world().resource::<GameSnapshot>().0["inventory"]["pages"]
        .as_array()
        .unwrap()
        .iter()
        .flat_map(|p| {
            p["items"]
                .as_array()
                .unwrap()
                .iter()
                .map(move |i| (p["index"].clone(), i.clone()))
        })
        .find(|(_, i)| i["id"] == 28)
        .unwrap();
    send(
        &app,
        "holdItem",
        json!({"page":cell.0,"x":cell.1["x"],"y":cell.1["y"]}),
    );
    pump(&host, &mut app, 20);
    assert_eq!(
        app.world().resource::<GameSnapshot>().0["weapon"]["held"]["id"],
        28,
        "accepted held can"
    );
    assert_eq!(can_fuel(&app), 0., "a fresh Portable Gas Can is empty");
    frames(&mut app, 2);

    let camera = app
        .world_mut()
        .spawn((PlayerCamera, Transform::default()))
        .id();
    look_at(&mut app, camera, pump_at + Vec3::Y * 1.2);
    let (name, prompt, fill) = panel(&app);
    assert_eq!(name, "Gas Pump");
    // Original quirk: the prompt reads the mains, OnExtractFuel only the pump's own wire.
    assert_eq!(prompt, "100% station fuel · [RMB] with a gas can to fill");
    assert_eq!(fill, 1.);
    mouse(&mut app, window, MouseButton::Right);
    frames(&mut app, 1);
    pump(&host, &mut app, 25);
    assert_eq!(can_fuel(&app), 0., "an unwired pump is refused by the host");
    send(
        &app,
        "connectWire",
        json!({"srcId":grid,"srcPort":0,"dstId":gas,"dstPort":0}),
    );
    pump(&host, &mut app, 20);
    frames(&mut app, 2);
    mouse(&mut app, window, MouseButton::Right);
    frames(&mut app, 1);
    assert_eq!(can_fuel(&app), 0., "the intent alone fills nothing");
    pump(&host, &mut app, 25);
    frames(&mut app, 2);
    assert_eq!(
        can_fuel(&app),
        20_000.,
        "accepted owner echo: a full 20 L can"
    );
    let percent = row(&app, gas)["fuel"].as_f64().unwrap() as f32;
    assert!(
        percent < 100. && percent > 99.,
        "the shared station tank drained ({percent})"
    );
    assert!(
        (panel(&app).2 - percent / 100.).abs() < 1e-6,
        "the bar reads the accepted fill"
    );

    look_at(&mut app, camera, gen_at + Vec3::Y * 0.4);
    assert!(panel(&app).0.starts_with("Generator"), "{:?}", panel(&app));
    mouse(&mut app, window, MouseButton::Left);
    frames(&mut app, 1);
    assert_eq!(
        row(&app, generator)["fuel"].as_f64().unwrap() as f32,
        tank,
        "the intent alone pours nothing"
    );
    pump(&host, &mut app, 25);
    assert_eq!(
        row(&app, generator)["fuel"].as_f64().unwrap() as f32,
        150_000.,
        "accepted pour tops the tank"
    );
    assert_eq!(
        can_fuel(&app),
        20_000. - (150_000. - tank),
        "the can keeps what did not fit"
    );
}
