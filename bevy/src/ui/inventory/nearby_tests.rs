//! Actual received world identity, source gestures and accepted-only handoff.
use super::super::{api, commands, model::*};
use super::*;
use bevy::input::keyboard::{Key, KeyboardInput};

fn action(bridge: &CoreBridge, name: &str, args: serde_json::Value) {
    let reply = bridge
        .request("action", json!({"name":name,"args":args}))
        .unwrap();
    assert_eq!(reply["result"]["queued"], true, "{reply}");
}
fn drop_host(host: &CoreBridge, app: &mut App, id: u16) {
    action(host, "console", json!({"text":format!("give {id}")}));
    pump(host, app, 12);
    let v: api::View =
        serde_json::from_value(host.request("inventoryView", json!({})).unwrap()["result"].clone())
            .unwrap();
    let row = v.items.iter().find(|r| r.address.id == id).unwrap();
    action(
        host,
        "dropItem",
        json!({"page":row.address.page,"x":row.address.x,"y":row.address.y,"point":[0,1.75,-1]}),
    );
    pump(host, app, 12);
}
fn open(host: &CoreBridge, app: &mut App) {
    action(
        app.world().resource::<CoreBridge>(),
        "nearbyInventory",
        json!({"action":"Open","eye":[0,1.75,0]}),
    );
    pump(host, app, 16);
    step(app, 0.11);
}
fn nearby(app: &App) -> api::Item {
    app.world()
        .resource::<Menu>()
        .view
        .items
        .iter()
        .find(|r| r.address.page == AREA)
        .unwrap()
        .clone()
}
fn deliver(host: &CoreBridge, app: &mut App) -> Vec<serde_json::Value> {
    let intents = app
        .world_mut()
        .resource_mut::<Messages<UiIntent>>()
        .drain()
        .collect::<Vec<_>>();
    let mut result = vec![];
    for intent in intents {
        if let UiIntent::Action { name, mut args } = intent {
            assert_eq!(name, "nearbyInventory");
            args["eye"] = json!([0, 1.75, 0]);
            action(app.world().resource::<CoreBridge>(), &name, args.clone());
            result.push(args);
        }
    }
    pump(host, app, 16);
    step(app, 0.11);
    result
}
#[test]
fn received_nearby_token_ctrl_grab_keeps_ground_until_authority_reply() {
    let (host, mut app, window) = setup();
    drop_host(&host, &mut app, 6);
    open(&host, &mut app);
    let row = nearby(&app);
    let cell = row.address.cell();
    assert_ne!(row.address.world_id, 0);
    assert_ne!(row.address.token, 0);
    assert!(!row.address.addressable());
    assert_eq!(
        row.actions
            .iter()
            .map(|a| a.action.as_str())
            .collect::<Vec<_>>(),
        ["transfer", "close"]
    );
    let before = app
        .world()
        .resource::<Menu>()
        .view
        .tile(cell)
        .unwrap()
        .amount;
    let own_amount = |app: &App| {
        app.world()
            .resource::<Menu>()
            .view
            .tiles
            .cells
            .iter()
            .filter(|r| r.page < OWN_PAGES && r.tile.id == 6)
            .map(|r| u32::from(r.tile.amount))
            .sum::<u32>()
    };
    let own_before = own_amount(&app);
    let rect = Rect::from_corners(Vec2::new(640., 72.), Vec2::new(1072., 504.));
    app.world_mut().resource_mut::<Menu>().hits.grids = vec![Grid {
        page: AREA,
        rect,
        clip: rect,
        slot: false,
    }];
    let p = rect.min + Vec2::new(cell.x as f32, cell.y as f32) * CELL + Vec2::splat(20.);
    app.world_mut()
        .resource_mut::<FrameInput>()
        .push(WindowEvent::KeyboardInput(KeyboardInput {
            key_code: KeyCode::ControlLeft,
            logical_key: Key::Control,
            state: ButtonState::Pressed,
            text: None,
            repeat: false,
            window,
        }));
    pointer(
        &mut app,
        window,
        p,
        Some((MouseButton::Left, ButtonState::Pressed)),
    );
    pointer(
        &mut app,
        window,
        p,
        Some((MouseButton::Left, ButtonState::Released)),
    );
    step(&mut app, 0.1);
    assert_eq!(
        app.world()
            .resource::<Menu>()
            .view
            .tile(cell)
            .unwrap()
            .amount,
        before,
        "queued intent does not edit accepted AREA"
    );
    let sent = deliver(&host, &mut app);
    assert_eq!(own_amount(&app), own_before + u32::from(before));
    assert_eq!(sent.len(), 1);
    assert_eq!(sent[0]["worldId"], row.address.world_id);
    assert_eq!(sent[0]["token"], row.address.token);
    assert_eq!(sent[0]["destination"], 255);
    assert!(
        app.world()
            .resource::<Menu>()
            .view
            .items
            .iter()
            .all(|r| r.address.page != AREA)
    );
    assert!(
        app.world()
            .resource::<Menu>()
            .view
            .items
            .iter()
            .any(|r| r.address.page < OWN_PAGES && r.address.id == 6)
    );
    assert!(
        !app.world().resource::<GameSnapshot>().0["worldItems"]
            .as_array()
            .unwrap()
            .iter()
            .any(|i| i["netId"] == row.address.world_id)
    );
}
#[test]
fn source_context_store_uses_current_accepted_crate_and_repaints_remote_view() {
    let (host, mut app, _) = setup();
    drop_host(&host, &mut app, 6);
    let reg = crate::vehicle::registration(
        crate::vehicle::data::named("police").unwrap(),
        "nearby-native".into(),
        Vec3::new(0., 0., -1.),
        0.,
        0,
    );
    let id = host
        .request("worldVehicles", json!({"vehicles":[reg]}))
        .unwrap()["result"]["vehicles"][0]["netId"]
        .as_u64()
        .unwrap();
    pump(&host, &mut app, 12);
    for bridge in [&host, app.world().resource::<CoreBridge>()] {
        action(
            bridge,
            "openVehicleStorage",
            json!({"vehicleNetId":id,"kind":0}),
        );
    }
    pump(&host, &mut app, 16);
    open(&host, &mut app);
    let row = nearby(&app);
    assert_ne!(app.world().resource::<Menu>().view.open_crate_id, 0);
    assert_eq!(row.actions[0].label, "Store");
    app.world_mut().resource_mut::<Menu>().gesture.selected = Some(row.address.cell());
    app.world_mut()
        .resource_mut::<Menu>()
        .pending_actions
        .push(controller::Action::Context("transfer".into()));
    step(&mut app, 0.1);
    let sent = deliver(&host, &mut app);
    assert_eq!(sent.len(), 1);
    assert_ne!(sent[0]["crateId"], 0);
    assert_eq!(sent[0]["destination"], 7);
    let other: api::View =
        serde_json::from_value(host.request("inventoryView", json!({})).unwrap()["result"].clone())
            .unwrap();
    assert!(
        other
            .items
            .iter()
            .any(|r| r.address.page == 7 && r.address.id == 6)
    );
    assert!(
        app.world()
            .resource::<Menu>()
            .view
            .items
            .iter()
            .all(|r| r.address.page != AREA)
    );
}
#[test]
fn unsupported_ground_origins_and_destinations_never_emit_legacy_grid_commands() {
    let area = Cell::at(AREA, 0, 0);
    let own = Cell::at(2, 0, 0);
    let view = api::View::default();
    for intent in [
        Intent::Move {
            from: own,
            to: area,
            rotation: 0,
        },
        Intent::Split {
            from: own,
            to: area,
            amount: 1,
            rotation: 0,
        },
        Intent::Unwear { slot: 3, to: area },
        Intent::Move {
            from: area,
            to: own,
            rotation: 0,
        },
        Intent::Drop(area),
        Intent::Equip(area),
    ] {
        assert!(commands::dispatch(intent, &view, &[]).commands.is_empty());
    }
}
