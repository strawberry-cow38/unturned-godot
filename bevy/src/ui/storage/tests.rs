use super::*;
use crate::{
    vehicle::{VehicleBody, data},
    world::WorldConfig,
};
use bevy::ecs::system::RunSystemOnce;

fn bridge() -> CoreBridge {
    CoreBridge::launch(std::path::Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap()
}
fn action(b: &CoreBridge, name: &str, args: Value) {
    let r = b
        .request("action", json!({"name":name,"args":args}))
        .unwrap();
    assert_eq!(r["result"]["queued"], true, "{r}");
}
fn setup() -> (CoreBridge, App, u32) {
    let host = bridge();
    let port = std::net::UdpSocket::bind("127.0.0.1:0")
        .unwrap()
        .local_addr()
        .unwrap()
        .port();
    let config = WorldConfig::default();
    host.request("init", json!({"contentRoot":config.content_root,"listenPort":port,"starterKit":true,"spawn":[0,0,0]})).unwrap();
    let spec = crate::vehicle::registration(
        data::named("police").unwrap(),
        "storage-test".into(),
        Vec3::new(0., 0., -1.),
        0.,
        0,
    );
    let id = host
        .request("worldVehicles", json!({"vehicles":[spec]}))
        .unwrap()["result"]["vehicles"][0]["netId"]
        .as_u64()
        .unwrap() as u32;
    let peer = bridge();
    peer.request(
        "init",
        json!({"contentRoot":config.content_root,"connectHost":"127.0.0.1","connectPort":port}),
    )
    .unwrap();
    let mut app = App::new();
    app.insert_resource(peer)
        .init_resource::<Lifecycle>()
        .init_resource::<Scrolls>()
        .init_resource::<UiModel>()
        .init_resource::<UiState>()
        .init_resource::<GameSnapshot>()
        .init_resource::<WorldSessionEpoch>()
        .init_resource::<Settings>()
        .init_resource::<ButtonInput<KeyCode>>()
        .init_resource::<ButtonInput<MouseButton>>()
        .init_resource::<InteractionFocus>();
    app.world_mut().resource_mut::<UiState>().screen = Screen::Playing;
    let body = crate::vehicle::tests::body(&mut app, "police", Vec3::ZERO);
    app.world_mut().get_mut::<VehicleBody>(body).unwrap().net_id = id;
    pump(&host, &mut app, 40);
    (host, app, id)
}
fn pump(host: &CoreBridge, app: &mut App, count: usize) {
    for _ in 0..count {
        host.request("tick", json!({})).unwrap();
        let state = app
            .world()
            .resource::<CoreBridge>()
            .request("tick", json!({}))
            .unwrap()["state"]
            .clone();
        app.world_mut()
            .resource_mut::<UiModel>()
            .adopt_snapshot(&state);
        app.world_mut().resource_mut::<GameSnapshot>().0 = state;
        app.world_mut().run_system_once(adopt).unwrap();
        std::thread::sleep(std::time::Duration::from_millis(1));
    }
}
#[test]
fn source_container_order_names_and_zero_sized_pages_do_not_invent_pockets() {
    let mut model = UiModel::default();
    model.storage = serde_json::from_value(json!({"netId":17,"label":"Glovebox","compartments":[
        {"page":10,"label":"Driver's seat"},{"page":12,"label":"Rear left"}]}))
    .unwrap();
    model.pages = [7, 9, 10, 12]
        .map(|index| InventoryPage {
            index,
            width: 4,
            height: 2,
            ..default()
        })
        .to_vec();
    assert_eq!(
        container_pages(&model),
        [
            (9, "Freezer".into()),
            (7, "Glovebox".into()),
            (10, "Driver's seat".into()),
            (12, "Rear left".into()),
            (8, "Nearby".into())
        ]
    );
}
#[test]
fn actual_f_trunk_waits_for_udp_acceptance_and_leaving_panel_closes_the_view() {
    let (host, mut app, id) = setup();
    app.world_mut()
        .resource_mut::<InteractionFocus>()
        .offer_manual(2., "Open trunk", "openVehicleTrunk", json!({"netId":id}));
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .press(KeyCode::KeyF);
    app.world_mut().run_system_once(open_trunk).unwrap();
    assert_eq!(
        app.world().resource::<UiState>().screen,
        Screen::Playing,
        "a queued command cannot open storage"
    );
    assert_eq!(app.world().resource::<UiModel>().storage.net_id, 0);
    pump(&host, &mut app, 15);
    assert_eq!(app.world().resource::<UiState>().screen, Screen::Inventory);
    assert_eq!(app.world().resource::<UiModel>().storage.label, "Trunk");
    assert!(
        app.world()
            .resource::<UiModel>()
            .pages
            .iter()
            .any(|p| p.index == 7 && p.width == 6 && p.height == 4)
    );
    app.world_mut().resource_mut::<UiState>().screen = Screen::Crafting;
    app.world_mut().run_system_once(adopt).unwrap();
    pump(&host, &mut app, 12);
    assert_eq!(app.world().resource::<UiModel>().storage.net_id, 0);
    assert_eq!(app.world().resource::<UiState>().screen, Screen::Crafting);
}
#[test]
fn closing_cabin_before_accepted_reply_never_reopens_it_and_fresh_inventory_gets_seat_pockets() {
    let (host, mut app, id) = setup();
    action(
        app.world().resource::<CoreBridge>(),
        "enterVehicle",
        json!({"netId":id,"seat":1}),
    );
    pump(&host, &mut app, 12);
    app.world_mut().resource_mut::<UiState>().screen = Screen::Inventory;
    app.world_mut().run_system_once(adopt).unwrap(); // pending request; no authority tick yet
    assert_eq!(app.world().resource::<UiModel>().storage.net_id, 0);
    app.world_mut().resource_mut::<UiState>().screen = Screen::Playing;
    app.world_mut().run_system_once(adopt).unwrap();
    pump(&host, &mut app, 15);
    assert_eq!(app.world().resource::<UiState>().screen, Screen::Playing);
    assert_eq!(app.world().resource::<UiModel>().storage.net_id, 0);
    app.world_mut().resource_mut::<UiState>().screen = Screen::Inventory;
    app.world_mut().run_system_once(adopt).unwrap();
    pump(&host, &mut app, 15);
    let view = &app.world().resource::<UiModel>().storage;
    assert_eq!(view.label, "Glovebox");
    assert_eq!(view.compartments.len(), 4);
    assert_eq!(view.compartments[0].label, "Driver's seat");
    host.request(
        "worldVehicleExit",
        json!({"netId":id,"seat":1,"clear":true,"position":[2,0,0]}),
    )
    .unwrap();
    action(
        app.world().resource::<CoreBridge>(),
        "exitVehicle",
        json!({}),
    );
    pump(&host, &mut app, 15);
    assert_eq!(app.world().resource::<UiModel>().storage.net_id, 0);
    assert_eq!(
        app.world().resource::<UiState>().screen,
        Screen::Inventory,
        "source force-close removes grids, not the dashboard"
    );
}

#[test]
fn cabin_open_close_reopen_before_any_reply_preserves_the_latest_inventory_request() {
    let (host, mut app, id) = setup();
    action(
        app.world().resource::<CoreBridge>(),
        "enterVehicle",
        json!({"netId":id,"seat":1}),
    );
    pump(&host, &mut app, 12);
    for screen in [Screen::Inventory, Screen::Playing, Screen::Inventory] {
        app.world_mut().resource_mut::<UiState>().screen = screen;
        app.world_mut().run_system_once(adopt).unwrap();
    }
    pump(&host, &mut app, 20);
    assert_eq!(app.world().resource::<UiState>().screen, Screen::Inventory);
    assert_eq!(
        app.world().resource::<UiModel>().storage.label,
        "Glovebox",
        "source OpenInventory sends again whenever no accepted crate is latched"
    );
}

#[path = "visual.rs"]
mod visual;
