//! Production input/widgets against separate authoritative and client UDP sessions.
use super::*;
use crate::{bridge::CoreBridge, world::WorldConfig};

fn bridge() -> CoreBridge {
    CoreBridge::launch(std::path::Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap()
}
fn action(bridge: &CoreBridge, name: &str, args: Value) {
    let reply = bridge
        .request("action", json!({"name":name,"args":args}))
        .unwrap();
    assert_eq!(reply["result"]["queued"], true, "{reply}");
}
fn pump(host: &CoreBridge, peer: &CoreBridge, app: &mut App, n: usize) {
    for _ in 0..n {
        host.request("tick", json!({})).unwrap();
        app.world_mut().resource_mut::<GameSnapshot>().0 =
            peer.request("tick", json!({})).unwrap()["state"].clone();
        std::thread::sleep(std::time::Duration::from_millis(1));
    }
}
fn submit(app: &mut App, peer: &CoreBridge) -> Value {
    let intents = super::super::super::tests::events(app);
    let [UiIntent::Action { name, args }] = intents.as_slice() else {
        panic!("one original UI intent");
    };
    assert_eq!(name, "vehicleLighting");
    action(peer, name, args.clone());
    args.clone()
}
#[test]
fn actual_udp_ctrl_patterns_and_mechanics_fix_wait_for_accepted_vehicle_state() {
    let config = WorldConfig::default();
    let port = std::net::UdpSocket::bind("127.0.0.1:0")
        .unwrap()
        .local_addr()
        .unwrap()
        .port();
    let host = bridge();
    host.request(
        "init",
        json!({"contentRoot":config.content_root,"listenPort":port,
        "allowCheats":true,"starterKit":true,"seed":0,"spawn":[0,0,0]}),
    )
    .unwrap();
    let id = host
        .request(
            "worldVehicles",
            json!({"vehicles":[{
        "typeId":data::named("police").unwrap().type_id,"position":[0,0,0],
        "health":6000,"fuel":1000,"battery":10000,"seatCount":4}]}),
        )
        .unwrap()["result"]["vehicles"][0]["netId"]
        .as_u64()
        .unwrap() as u32;
    host.request(
        "worldVehicleLighting",
        json!({"netId":id,"lamps":[
        {"label":"headlight_l","center":[0,1.6,-2]},
        {"label":"headlight_r","center":[1,1.6,-2]}]}),
    )
    .unwrap();
    host.request(
        "worldDynamicGeometry",
        json!({"key":format!("vehicle:{id}"),"meshes":[{
        "vehicleNetId":id,"vertices":[[-1,0,-2],[1,0,-2],[0,3,-2]]}]}),
    )
    .unwrap();
    for _ in 0..20 {
        host.request("tick", json!({})).unwrap();
    }
    action(
        &host,
        "fire",
        json!({"origin":[0,1.6,0],"dir":[0,0,-1],"visual":{"aimAlpha":1}}),
    );
    for _ in 0..15 {
        host.request("tick", json!({})).unwrap();
    }
    let peer = bridge();
    peer.request(
        "init",
        json!({"contentRoot":config.content_root,"connectHost":"127.0.0.1","connectPort":port}),
    )
    .unwrap();
    let mut app = setup();
    let world = app.world_mut();
    for mut body in world.query::<&mut VehicleBody>().iter_mut(world) {
        body.net_id = id;
    }
    let metadata = host.request("catalog", json!({})).unwrap();
    app.world_mut()
        .resource_mut::<ItemCatalog>()
        .vehicle_lighting =
        Some(serde_json::from_value(metadata["result"]["vehicleLighting"].clone()).unwrap());
    app.world_mut().resource_mut::<UiState>().screen = Screen::Playing;
    pump(&host, &peer, &mut app, 30);
    assert_eq!(
        row(&app.world().resource::<GameSnapshot>().0, id).unwrap()["brokenLampMask"],
        1
    );
    action(&peer, "enterVehicle", json!({"netId":id,"seat":0}));
    pump(&host, &peer, &mut app, 20);
    assert_eq!(
        app.world().resource::<GameSnapshot>().0["network"]["ownsWorld"],
        false
    );
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .press(KeyCode::ControlLeft);
    super::super::super::tests::step(&mut app, 0);
    super::super::super::tests::clear(&mut app);
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .release(KeyCode::ControlLeft);
    super::super::super::tests::step(&mut app, 219);
    assert_eq!(submit(&mut app, &peer)["action"], "ToggleSiren");
    assert_eq!(
        row(&app.world().resource::<GameSnapshot>().0, id).unwrap()["flags"]
            .as_u64()
            .unwrap()
            & 8,
        0,
        "UI leaves the accepted switch untouched until the host responds"
    );
    pump(&host, &peer, &mut app, 20);
    assert_ne!(
        row(&app.world().resource::<GameSnapshot>().0, id).unwrap()["flags"]
            .as_u64()
            .unwrap()
            & 8,
        0
    );
    super::super::super::tests::clear(&mut app);
    super::super::super::tests::open(&mut app);
    app.world_mut()
        .resource_mut::<Menu>()
        .radial
        .as_mut()
        .unwrap()
        .highlight = Some(2);
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .release(KeyCode::ControlLeft);
    super::super::super::tests::step(&mut app, 1);
    assert_eq!(submit(&mut app, &peer)["value"], 2);
    assert_eq!(
        row(&app.world().resource::<GameSnapshot>().0, id).unwrap()["lightbarPattern"],
        0
    );
    pump(&host, &peer, &mut app, 20);
    assert_eq!(
        row(&app.world().resource::<GameSnapshot>().0, id).unwrap()["lightbarPattern"],
        2
    );
    action(&peer, "exitVehicle", json!({}));
    pump(&host, &peer, &mut app, 20);
    app.world_mut().resource_mut::<UiState>().screen = Screen::Mechanics;
    app.world_mut().resource_mut::<Menu>().mechanics = Some(id);
    render(&mut app);
    let buttons = fixes(&mut app);
    let left = buttons.iter().find(|(_, index)| *index == 0).unwrap().0;
    app.world_mut().trigger(Activate { entity: left });
    let sent = submit(&mut app, &peer);
    assert_eq!(sent["action"], "RepairLamp");
    assert_eq!(sent["value"], 0);
    assert_eq!(
        row(&app.world().resource::<GameSnapshot>().0, id).unwrap()["brokenLampMask"],
        1
    );
    pump(&host, &peer, &mut app, 20);
    render(&mut app);
    assert_eq!(
        row(&app.world().resource::<GameSnapshot>().0, id).unwrap()["brokenLampMask"],
        0
    );
    assert_eq!(fixes(&mut app), buttons);
    assert!(
        app.world()
            .entity(left)
            .contains::<bevy::ui::InteractionDisabled>()
    );
}
