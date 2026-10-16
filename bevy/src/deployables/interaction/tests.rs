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
    let before = app.world().resource::<Gesture>().levels[&4005];
    assert!(before > 0. && before < 1.);
    app.world_mut().resource_mut::<UiState>().screen = crate::ui::Screen::Paused;
    for _ in 0..20 {
        app.update();
    }
    assert_eq!(
        app.world().resource::<Gesture>().levels[&4005],
        before,
        "menu wall-clock time cannot prematurely settle source CanTogglePower"
    );
    app.world_mut().resource_mut::<UiState>().screen = crate::ui::Screen::Playing;
    app.update();
    assert!(app.world().resource::<Gesture>().levels[&4005] > before);
}
