use super::*;
use crate::bridge::CoreBridge;
use serde_json::Value;

#[test]
fn actual_udp_bullet_hurt_drives_owner_flash_and_bearing_without_replay() {
    let port = std::net::UdpSocket::bind("127.0.0.1:0")
        .unwrap()
        .local_addr()
        .unwrap()
        .port();
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR"));
    let host = CoreBridge::launch(root).unwrap();
    let peer = CoreBridge::launch(root).unwrap();
    let content = crate::world::WorldConfig::default().content_root;
    host.request("init", json!({"contentRoot":content,"listenPort":port,"starterKit":true,"allowCheats":true,"spawn":[0,0,0]})).unwrap();
    peer.request(
        "init",
        json!({"contentRoot":content,"connectHost":"127.0.0.1","connectPort":port}),
    )
    .unwrap();
    let pump = |count: usize| {
        let mut snapshot = Value::Null;
        for _ in 0..count {
            host.request("tick", json!({})).unwrap();
            snapshot = peer.request("tick", json!({})).unwrap()["state"].clone();
            std::thread::sleep(Duration::from_millis(1));
        }
        snapshot
    };
    let joined = pump(40);
    assert_eq!(joined["connected"], true);
    peer.request(
        "action",
        json!({"name":"console","args":{"text":"teleport 0 0 -4"}}),
    )
    .unwrap();
    let before = pump(20);
    assert!(
        (crate::player::vector(&before["player"]["position"])
            .unwrap()
            .z
            + 4.)
            .abs()
            < 0.1
    );
    let ammo = host.request("snapshot", json!({})).unwrap()["state"]["player"]["ammo"]
        .as_i64()
        .unwrap();
    host.request(
        "action",
        json!({"name":"fire","args":{"origin":[0,1.6,0],"dir":[0,0,-1],"visual":{"aimAlpha":1}}}),
    )
    .unwrap();
    let accepted = pump(30);
    let hurt = accepted["events"]
        .as_array()
        .unwrap()
        .iter()
        .find(|event| event["kind"] == "hurt")
        .expect("real UDP owner receives the authoritative bullet hurt");
    assert_eq!(hurt["data"]["hasSource"], true);
    assert_eq!(hurt["data"]["damage"], 40., "source rifle damage20 times the shared head multiplier2");
    assert!(accepted["player"]["hp"].as_f64().unwrap() < before["player"]["hp"].as_f64().unwrap());
    assert_eq!(
        host.request("snapshot", json!({})).unwrap()["state"]["player"]["ammo"],
        ammo - 1
    );

    let mut app = app();
    app.world_mut().resource_mut::<UiState>().networked = true;
    app.world_mut()
        .resource_mut::<UiModel>()
        .adopt_snapshot(&accepted);
    let feet = crate::player::vector(&accepted["player"]["position"]).unwrap();
    let world = app.world_mut();
    for mut pose in world
        .query_filtered::<&mut Transform, With<PlayerBody>>()
        .iter_mut(world)
    {
        pose.translation = feet;
    }
    let event = CoreEvent {
        sequence: hurt["seq"].as_u64().unwrap(),
        kind: hurt["kind"].as_str().unwrap().into(),
        data: hurt["data"].clone(),
    };
    app.world_mut().write_message(event.clone());
    app.update();
    let feedback = app.world().resource::<HurtFeedback>();
    assert!(feedback.pain_alpha > 0.);
    assert_eq!(feedback.marks.len(), 1);
    assert!(
        feedback.marks[0].direction.dot(Vec3::Z) > 0.95,
        "mark points back toward the actual shooter"
    );
    assert!(feedback.marks[0].critical);
    let alpha = feedback.pain_alpha;
    app.world_mut().write_message(event);
    app.update();
    let feedback = app.world().resource::<HurtFeedback>();
    assert_eq!(feedback.marks.len(), 1);
    assert!(feedback.pain_alpha < alpha);
    assert_eq!(
        app.world().resource::<UiModel>().health,
        accepted["player"]["hp"].as_f64().unwrap() as f32
    );
}
