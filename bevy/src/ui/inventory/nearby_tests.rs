//! Actual received world identity, source gestures and accepted-only handoff.
use super::super::{api, commands, model::*};
use super::*;
use crate::ui::{InventoryItem, InventoryPage};
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
            assert!(
                matches!(
                    name.as_str(),
                    "nearbyInventory" | "nearbyOperation" | "nearbyMagazineTransfer"
                ),
                "{name}"
            );
            // Mirror production player::ui_intents: only listed actions get the
            // completed camera; an unlisted Nearby sender is refused by the bridge.
            if crate::player::needs_eye(&name) {
                args["eye"] = json!([0, 1.75, 0]);
            }
            action(app.world().resource::<CoreBridge>(), &name, args.clone());
            args["name"] = json!(name);
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
        ["transfer", "unload", "close"],
        "source order: Take, then a loaded magazine's Unload"
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
fn ground_view(crate_id: u32) -> (api::View, Vec<InventoryPage>) {
    let row = api::Item {
        address: api::Address {
            page: AREA,
            world_id: 41,
            scan_id: 3,
            revision: 9,
            id: 6,
            token: 77,
            ..default()
        },
        actions: vec![],
        ..default()
    };
    let storage = InventoryPage {
        index: STORAGE,
        width: u8::from(crate_id != 0) * 4,
        height: u8::from(crate_id != 0) * 4,
        items: vec![],
    };
    (
        api::View {
            items: vec![row],
            open_crate_id: crate_id,
            ..default()
        },
        vec![storage],
    )
}
fn sent(out: commands::Dispatch) -> Vec<(String, serde_json::Value)> {
    out.commands
        .into_iter()
        .map(|intent| match intent {
            UiIntent::Action { name, args } => (name, args),
            other => panic!("{other:?}"),
        })
        .collect()
}
#[test]
fn ground_rows_route_only_place_split_and_empty_through_command87() {
    let area = Cell::at(AREA, 0, 0);
    let own = Cell::at(2, 3, 1);
    let (view, pages) = ground_view(0);
    let moved = sent(commands::dispatch(
        Intent::Move {
            from: area,
            to: own,
            rotation: 3,
        },
        &view,
        &pages,
    ));
    assert_eq!(moved.len(), 1);
    assert_eq!(moved[0].0, "nearbyOperation");
    assert_eq!(
        moved[0].1,
        json!({"operation":"Place","scanId":3,"revision":9,"worldId":41,"token":77,"x":0,"y":0,
            "amount":0,"crateId":0,"page":2,"toX":3,"toY":1,"toRot":3})
    );
    let split = sent(commands::dispatch(
        Intent::Split {
            from: area,
            amount: 4,
            to: Cell::at(STORAGE, 1, 2),
            rotation: 0,
        },
        &view,
        &pages,
    ));
    assert!(split.is_empty(), "a container cell needs the accepted open crate");
    let (open_view, open_pages) = ground_view(60);
    let split = sent(commands::dispatch(
        Intent::Split {
            from: area,
            amount: 4,
            to: Cell::at(STORAGE, 1, 2),
            rotation: 0,
        },
        &open_view,
        &open_pages,
    ));
    assert_eq!(split[0].1["operation"], "Split");
    assert_eq!(split[0].1["amount"], 4);
    assert_eq!(split[0].1["crateId"], 60);
    assert_eq!(split[0].1["page"], STORAGE);
    // Source refusals: no ground drop/equip/wear/paperdoll take, and nothing
    // may name AREA as a destination (TryDrag/QuickTransfer/Split guards).
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
            to: Cell::at(AREA, 2, 2),
            rotation: 0,
        },
        Intent::Drop(area),
        Intent::Equip(area),
        Intent::Wear {
            from: area,
            slot: 3,
        },
        Intent::TakeToPaperdoll(area),
        Intent::Bind {
            from: area,
            hotbar: 3,
        },
    ] {
        assert!(
            commands::dispatch(intent.clone(), &open_view, &open_pages)
                .commands
                .is_empty(),
            "{intent:?}"
        );
    }
    let row = &open_view.items[0];
    for refused in ["equip", "hold", "drop", "salvage", "learn", "autoDrink", "dequip"] {
        assert!(commands::context_refusal(refused, row).is_some(), "{refused}");
    }
    for allowed in ["transfer", "close", "emptyFuel", "unload"] {
        assert!(commands::context_refusal(allowed, row).is_none(), "{allowed}");
    }
    let mut unreceived = row.clone();
    unreceived.address.revision = 0;
    for needs_identity in ["emptyFuel", "unload"] {
        assert!(
            commands::context_refusal(needs_identity, &unreceived).is_some(),
            "{needs_identity}"
        );
    }
    let empty = sent(commands::context("emptyFuel", row, &open_view, &open_pages));
    assert_eq!(empty[0].1["operation"], "EmptyFuel");
    assert_eq!(empty[0].1["crateId"], 0);
    assert_eq!(empty[0].1["page"], 0);
}
fn ground_grids(app: &mut App) -> (Rect, Rect) {
    let area = Rect::from_corners(Vec2::new(640., 72.), Vec2::new(1072., 504.));
    let pocket = Rect::from_corners(Vec2::new(0., 72.), Vec2::new(360., 288.));
    app.world_mut().resource_mut::<Menu>().hits.grids = vec![
        Grid {
            page: AREA,
            rect: area,
            clip: area,
            slot: false,
        },
        Grid {
            page: 2,
            rect: pocket,
            clip: pocket,
            slot: false,
        },
    ];
    (area, pocket)
}
fn cell_item(app: &App, page: u8, x: u8, y: u8) -> Option<InventoryItem> {
    app.world()
        .resource::<Menu>()
        .pages
        .iter()
        .find(|p| p.index == page)?
        .items
        .iter()
        .find(|i| i.x == x && i.y == y)
        .cloned()
}
#[test]
fn ground_rmb_carry_and_rotated_drag_wait_for_command87_acceptance() {
    let (host, mut app, window) = setup();
    let round = owned_round(&app);
    spawn_ground(
        &host,
        &mut app,
        json!([{"itemId":round,"amount":12,"position":[0,1.75,-1]},
            {"itemId":6,"amount":17,"position":[0.4,1.75,-1]}]),
    );
    open(&host, &mut app);
    let row = nearby_id(&app, round);
    let total = app
        .world()
        .resource::<Menu>()
        .view
        .tile(row.address.cell())
        .unwrap()
        .amount;
    assert_eq!(total, 12, "loose round stack");
    assert!(cell_item(&app, 2, 0, 1).is_none() && cell_item(&app, 2, 4, 0).is_none());
    let (area, pocket) = ground_grids(&mut app);
    let at = |cell: Cell| area.min + Vec2::new(cell.x as f32, cell.y as f32) * CELL + Vec2::splat(20.);
    // Source RMB carry: one unit after the six-pixel slop, DoSplitTo on release.
    let p = at(row.address.cell());
    pointer(
        &mut app,
        window,
        p,
        Some((MouseButton::Right, ButtonState::Pressed)),
    );
    pointer(&mut app, window, p + Vec2::new(10., 0.), None);
    let into = pocket.min + Vec2::new(0., CELL) + Vec2::splat(30.);
    pointer(
        &mut app,
        window,
        into,
        Some((MouseButton::Right, ButtonState::Released)),
    );
    step(&mut app, 0.11);
    let menu = app.world().resource::<Menu>();
    assert_eq!(menu.view.tile(row.address.cell()).unwrap().amount, total);
    assert!(
        cell_item(&app, 2, 0, 1).is_none(),
        "no optimistic split cell"
    );
    let split = deliver(&host, &mut app);
    assert_eq!(split.len(), 1);
    assert_eq!(
        (
            &split[0]["name"],
            &split[0]["operation"],
            &split[0]["amount"],
            &split[0]["page"],
            &split[0]["toX"],
            &split[0]["toY"],
            &split[0]["toRot"],
        ),
        (
            &json!("nearbyOperation"),
            &json!("Split"),
            &json!(1),
            &json!(2),
            &json!(0),
            &json!(1),
            &json!(0)
        )
    );
    assert_eq!(split[0]["worldId"], row.address.world_id);
    assert_eq!(split[0]["token"], row.address.token);
    assert_eq!(cell_item(&app, 2, 0, 1).map(|i| (i.id, i.amount)), Some((round, 1)));
    assert_eq!(ground_amount(&app, row.address.world_id), Some(total - 1));
    // Whole-row drag of the ground magazine with source R rotation.
    let row = nearby_id(&app, 6);
    let total = ground_amount(&app, row.address.world_id).unwrap();
    let p = at(row.address.cell());
    pointer(
        &mut app,
        window,
        p,
        Some((MouseButton::Left, ButtonState::Pressed)),
    );
    app.world_mut()
        .resource_mut::<FrameInput>()
        .push(WindowEvent::KeyboardInput(KeyboardInput {
            key_code: KeyCode::KeyR,
            logical_key: Key::Character("r".into()),
            state: ButtonState::Pressed,
            text: None,
            repeat: false,
            window,
        }));
    let release = pocket.min + Vec2::new(4. * CELL + 20., 20.);
    pointer(
        &mut app,
        window,
        release,
        Some((MouseButton::Left, ButtonState::Released)),
    );
    step(&mut app, 0.11);
    assert!(cell_item(&app, 2, 4, 0).is_none(), "no optimistic placement");
    let placed = deliver(&host, &mut app);
    assert_eq!(placed.len(), 1);
    assert_eq!(placed[0]["operation"], "Place");
    assert_eq!(
        (&placed[0]["page"], &placed[0]["toX"], &placed[0]["toY"], &placed[0]["toRot"]),
        (&json!(2), &json!(4), &json!(0), &json!(1))
    );
    let landed = cell_item(&app, 2, 4, 0).expect("accepted rotated cell");
    assert_eq!((landed.id, landed.rot, landed.amount), (6, 1, total));
    let menu = app.world().resource::<Menu>();
    assert_eq!(
        menu.view
            .items
            .iter()
            .filter(|r| r.address.page == AREA)
            .map(|r| r.address.id)
            .collect::<Vec<_>>(),
        [round],
        "only the remaining rounds stay on the ground"
    );
    assert!(menu.notice.is_empty(), "{}", menu.notice);
    assert!(
        !app.world().resource::<GameSnapshot>().0["worldItems"]
            .as_array()
            .unwrap()
            .iter()
            .any(|i| i["netId"] == row.address.world_id)
    );
}
#[test]
fn ground_split_strip_and_context_empty_send_exact_operations_only() {
    let (host, mut app, window) = setup();
    let round = owned_round(&app);
    drop_host(&host, &mut app, 28);
    spawn_ground(
        &host,
        &mut app,
        json!([{"itemId":round,"amount":20,"position":[0.4,1.75,-1]}]),
    );
    open(&host, &mut app);
    let rows = app
        .world()
        .resource::<Menu>()
        .view
        .items
        .iter()
        .filter(|r| r.address.page == AREA)
        .cloned()
        .collect::<Vec<_>>();
    let can = rows.iter().find(|r| r.address.id == 28).unwrap().clone();
    assert!(rows.iter().any(|r| r.address.id == round));
    assert_eq!(
        can.actions
            .iter()
            .map(|a| a.action.as_str())
            .collect::<Vec<_>>(),
        ["emptyFuel", "transfer", "close"]
    );
    {
        let mut menu = app.world_mut().resource_mut::<Menu>();
        menu.gesture.selected = Some(can.address.cell());
        menu.pending_actions
            .push(controller::Action::Context("emptyFuel".into()));
    }
    step(&mut app, 0.11);
    let empty = deliver(&host, &mut app);
    assert_eq!(empty.len(), 1);
    assert_eq!(empty[0]["operation"], "EmptyFuel");
    assert_eq!(empty[0]["worldId"], can.address.world_id);
    assert_eq!(
        app.world().resource::<GameSnapshot>().0["nearby"]["result"],
        "Transferred"
    );
    // Split strip: the preview slot is dragged out with the edited count.
    let (_, pocket) = ground_grids(&mut app);
    let rounds = nearby_id(&app, round);
    let preview = Rect::from_corners(Vec2::new(700., 600.), Vec2::new(780., 680.));
    {
        let mut menu = app.world_mut().resource_mut::<Menu>();
        menu.gesture.selected = Some(rounds.address.cell());
        menu.gesture.split_amount = 5;
        menu.hits.split_preview = Some(preview);
    }
    pointer(
        &mut app,
        window,
        preview.center(),
        Some((MouseButton::Left, ButtonState::Pressed)),
    );
    let into = pocket.min + Vec2::new(CELL, CELL) + Vec2::splat(30.);
    pointer(
        &mut app,
        window,
        into,
        Some((MouseButton::Left, ButtonState::Released)),
    );
    step(&mut app, 0.11);
    let split = deliver(&host, &mut app);
    assert_eq!(split.len(), 1);
    assert_eq!(
        (&split[0]["operation"], &split[0]["amount"], &split[0]["toX"], &split[0]["toY"]),
        (&json!("Split"), &json!(5), &json!(1), &json!(1))
    );
    assert_eq!(cell_item(&app, 2, 1, 1).map(|i| (i.id, i.amount)), Some((round, 5)));
    assert_eq!(ground_amount(&app, rounds.address.world_id), Some(15));
}
fn nearby_id(app: &App, id: u16) -> api::Item {
    app.world()
        .resource::<Menu>()
        .view
        .items
        .iter()
        .find(|r| r.address.page == AREA && r.address.id == id)
        .unwrap()
        .clone()
}
#[test]
fn ground_refusals_keep_source_snap_home_and_send_nothing() {
    let (host, mut app, window) = setup();
    drop_host(&host, &mut app, 6);
    open(&host, &mut app);
    let row = nearby(&app);
    let (area, _) = ground_grids(&mut app);
    let p = area.min
        + Vec2::new(row.address.x as f32, row.address.y as f32) * CELL
        + Vec2::splat(20.);
    // Source excludes AREA from the right-half drop and paperdoll equip.
    {
        let mut menu = app.world_mut().resource_mut::<Menu>();
        menu.hits.paperdoll = Some(Rect::from_corners(Vec2::new(380., 100.), Vec2::new(620., 600.)));
        menu.hits.viewport = Vec2::new(1280., 720.);
    }
    for release in [Vec2::new(1200., 650.), Vec2::new(500., 300.)] {
        pointer(
            &mut app,
            window,
            p,
            Some((MouseButton::Left, ButtonState::Pressed)),
        );
        pointer(
            &mut app,
            window,
            release,
            Some((MouseButton::Left, ButtonState::Released)),
        );
        step(&mut app, 0.11);
    }
    for refused in ["drop", "equip"] {
        {
            let mut menu = app.world_mut().resource_mut::<Menu>();
            menu.notice.clear();
            menu.gesture.selected = Some(row.address.cell());
            menu.pending_actions
                .push(controller::Action::Context(refused.into()));
        }
        step(&mut app, 0.11);
        assert_eq!(
            app.world().resource::<Menu>().notice,
            "Take this item into your inventory first.",
            "{refused}"
        );
    }
    assert!(
        app.world_mut()
            .resource_mut::<Messages<UiIntent>>()
            .drain()
            .next()
            .is_none()
    );
    pump(&host, &mut app, 8);
    step(&mut app, 0.11);
    assert_eq!(nearby(&app).address.world_id, row.address.world_id);
}
/// The loose round matching the setup magazine, from the accepted view.
fn owned_round(app: &App) -> u16 {
    app.world()
        .resource::<Menu>()
        .view
        .items
        .iter()
        .find(|r| r.address.page < OWN_PAGES && r.address.id == 6)
        .unwrap()
        .round_id
}
fn spawn_ground(host: &CoreBridge, app: &mut App, items: serde_json::Value) {
    host.request("worldSpawn", json!({ "items": items })).unwrap();
    pump(host, app, 8);
}
fn ground_amount(app: &App, world_id: u32) -> Option<u16> {
    let menu = app.world().resource::<Menu>();
    let row = menu
        .view
        .items
        .iter()
        .find(|r| r.address.page == AREA && r.address.world_id == world_id)?;
    menu.view.tile(row.address.cell()).map(|t| t.amount)
}
#[test]
fn ground_magazine_unload_job_steps_on_event60_and_nearby_echo_only() {
    let (host, mut app, _) = setup();
    spawn_ground(
        &host,
        &mut app,
        json!([{"itemId":6,"amount":4,"quality":70,"position":[0,1.75,-1]}]),
    );
    open(&host, &mut app);
    let magazine = nearby_id(&app, 6);
    assert!(magazine.actions.iter().any(|a| a.action == "unload"));
    let world = magazine.address.world_id;
    let round = magazine.round_id;
    let owned = |app: &App| {
        app.world()
            .resource::<Menu>()
            .view
            .tiles
            .cells
            .iter()
            .filter(|c| c.page < OWN_PAGES && c.tile.id == round)
            .map(|c| c.tile.amount)
            .sum::<u16>()
    };
    {
        let mut menu = app.world_mut().resource_mut::<Menu>();
        menu.gesture.selected = Some(magazine.address.cell());
        menu.pending_actions
            .push(controller::Action::Context("unload".into()));
    }
    step(&mut app, 0.11);
    assert_eq!(app.world().resource::<Menu>().magazines.operations.len(), 1);
    let mut seen = vec![];
    for expected in [3u16, 2, 1] {
        // One source half-second step, then nothing until the accepted reply,
        // the owner snapshot and the repainted Nearby revision all arrive.
        step(&mut app, 0.5);
        let sent = deliver(&host, &mut app);
        assert_eq!(sent.len(), 1, "exactly one round per accepted step");
        assert_eq!(sent[0]["name"], "nearbyMagazineTransfer");
        assert_eq!(sent[0]["unloading"], true);
        assert_eq!(sent[0]["magWorldId"], world);
        assert_eq!(sent[0]["roundId"], round);
        seen.push(sent[0]["revision"].as_u64().unwrap());
        assert_eq!(ground_amount(&app, world), Some(expected));
        assert_eq!(owned(&app), 4 - expected);
    }
    assert!(
        seen.windows(2).all(|w| w[1] > w[0]),
        "each step names the newest received revision: {seen:?}"
    );
    assert_eq!(app.world().resource::<Menu>().magazines.operations.len(), 1);
}
#[test]
fn ground_rounds_dragged_onto_ground_magazine_load_through_findstack_order() {
    let (host, mut app, window) = setup();
    spawn_ground(
        &host,
        &mut app,
        json!([{"itemId":6,"amount":10,"position":[0,1.75,-1]}]),
    );
    open(&host, &mut app);
    let round = nearby_id(&app, 6).round_id;
    spawn_ground(
        &host,
        &mut app,
        json!([{"itemId":round,"amount":3,"position":[0.3,1.75,-1]}]),
    );
    action(
        app.world().resource::<CoreBridge>(),
        "nearbyInventory",
        json!({"action":"Open","eye":[0,1.75,0]}),
    );
    pump(&host, &mut app, 16);
    step(&mut app, 0.11);
    let magazine = nearby_id(&app, 6);
    let rounds = nearby_id(&app, round);
    let (area, _) = ground_grids(&mut app);
    let at = |cell: Cell| area.min + Vec2::new(cell.x as f32, cell.y as f32) * CELL + Vec2::splat(20.);
    let (from, to) = (at(rounds.address.cell()), at(magazine.address.cell()));
    pointer(
        &mut app,
        window,
        from,
        Some((MouseButton::Left, ButtonState::Pressed)),
    );
    pointer(
        &mut app,
        window,
        to,
        Some((MouseButton::Left, ButtonState::Released)),
    );
    step(&mut app, 0.11);
    assert!(app.world().resource::<Menu>().notice.is_empty());
    assert_eq!(app.world().resource::<Menu>().magazines.operations.len(), 1);
    for expected in [11u16, 12] {
        step(&mut app, 0.5);
        let sent = deliver(&host, &mut app);
        assert_eq!(sent.len(), 1);
        assert_eq!(sent[0]["name"], "nearbyMagazineTransfer");
        assert_eq!(sent[0]["unloading"], false);
        assert_eq!(sent[0]["magWorldId"], magazine.address.world_id);
        assert_eq!(sent[0]["roundWorldId"], rounds.address.world_id);
        assert_eq!(ground_amount(&app, magazine.address.world_id), Some(expected));
    }
    assert_eq!(ground_amount(&app, rounds.address.world_id), Some(1));
    assert!(
        app.world()
            .resource::<Menu>()
            .view
            .tiles
            .cells
            .iter()
            .all(|c| c.page >= OWN_PAGES || c.tile.id != round),
        "no owned rounds were invented or spent"
    );
}
