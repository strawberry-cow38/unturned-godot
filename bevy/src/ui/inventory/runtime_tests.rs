//! Real UDP ownership boundary exercised through the production input controller.
use super::controller::Menu;
use super::*;
use crate::{
    bridge::CoreBridge,
    player::{GameSnapshot, WorldSessionEpoch},
    settings::Settings,
    ui::{Screen, UiIntent, UiModel, UiState, input_order::FrameInput},
    world::WorldConfig,
};
use bevy::{
    ecs::system::RunSystemOnce,
    input::{ButtonState, mouse::MouseButtonInput},
    window::{CursorMoved, PrimaryWindow, WindowEvent},
};
use serde_json::json;

fn setup() -> (CoreBridge, App, Entity) {
    let launch = || CoreBridge::launch(std::path::Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
    let host = launch();
    let port = std::net::UdpSocket::bind("127.0.0.1:0")
        .unwrap()
        .local_addr()
        .unwrap()
        .port();
    let root = WorldConfig::default().content_root;
    host.request(
        "init",
        json!({"contentRoot":root,"listenPort":port,"allowCheats":true,"spawn":[0,0,0]}),
    )
    .unwrap();
    let peer = launch();
    peer.request(
        "init",
        json!({"contentRoot":root,"connectHost":"127.0.0.1","connectPort":port}),
    )
    .unwrap();
    let mut app = App::new();
    app.insert_resource(peer)
        .init_resource::<Menu>()
        .init_resource::<Time>()
        .init_resource::<UiModel>()
        .init_resource::<UiState>()
        .init_resource::<Settings>()
        .init_resource::<GameSnapshot>()
        .init_resource::<WorldSessionEpoch>()
        .init_resource::<FrameInput>()
        .init_resource::<crate::ui::crafting::Menu>()
        .init_resource::<paperdoll::Portrait>()
        .init_resource::<paperdoll::stage::Surface>()
        .add_message::<UiIntent>();
    let window = app
        .world_mut()
        .spawn((
            Window {
                resolution: (1280, 720).into(),
                ..default()
            },
            PrimaryWindow,
        ))
        .id();
    pump(&host, &mut app, 40);
    for id in [6, 95] {
        app.world()
            .resource::<CoreBridge>()
            .request(
                "action",
                json!({"name":"console","args":{"text":format!("give {id}")}}),
            )
            .unwrap();
    }
    pump(&host, &mut app, 16);
    app.world_mut().resource_mut::<UiState>().screen = Screen::Inventory;
    step(&mut app, 0.1);
    assert!(!app.world().resource::<Menu>().view.items.is_empty());
    (host, app, window)
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
        std::thread::sleep(std::time::Duration::from_millis(1));
    }
}
fn step(app: &mut App, delta: f32) {
    app.world_mut()
        .resource_mut::<Time>()
        .advance_by(std::time::Duration::from_secs_f32(delta));
    app.world_mut().run_system_once(controller::update).unwrap();
    app.world_mut().resource_mut::<FrameInput>().begin();
}
fn flush(app: &mut App) -> Vec<(String, serde_json::Value)> {
    let intents = app
        .world_mut()
        .resource_mut::<Messages<UiIntent>>()
        .drain()
        .collect::<Vec<_>>();
    let mut result = vec![];
    for intent in intents {
        if let UiIntent::Action { name, args } = intent {
            let reply = app
                .world()
                .resource::<CoreBridge>()
                .request("action", json!({"name":name,"args":args}))
                .unwrap();
            assert_eq!(reply["result"]["queued"], true, "{reply}");
            result.push((name, args));
        }
    }
    result
}
fn pointer(app: &mut App, window: Entity, p: Vec2, button: Option<(MouseButton, ButtonState)>) {
    let mut frame = app.world_mut().resource_mut::<FrameInput>();
    frame.push(WindowEvent::CursorMoved(CursorMoved {
        window,
        position: p,
        delta: None,
    }));
    if let Some((button, state)) = button {
        frame.push(WindowEvent::MouseButtonInput(MouseButtonInput {
            button,
            state,
            window,
        }));
    }
}
#[test]
fn ordered_drag_sends_one_udp_move_and_keeps_original_cells_until_acceptance() {
    use model::*;
    let (host, mut app, window) = setup();
    let pages = app.world().resource::<Menu>().pages.clone();
    let page = pages.iter().find(|p| p.index == 2).unwrap();
    let item = page
        .items
        .iter()
        .find(|i| i.width == 1 && i.height == 1)
        .unwrap()
        .clone();
    let from = Cell::item(2, &item);
    let to = (0..page.height)
        .flat_map(|y| (0..page.width).map(move |x| Cell::at(2, x, y)))
        .find(|c| at(&pages, *c).is_none())
        .unwrap();
    let rect = Rect::from_corners(
        Vec2::new(640., 72.),
        Vec2::new(640., 72.) + Vec2::new(page.width as f32, page.height as f32) * CELL,
    );
    app.world_mut().resource_mut::<Menu>().hits.grids = vec![Grid {
        page: 2,
        rect,
        clip: rect,
        slot: false,
    }];
    let point = |c: Cell| rect.min + Vec2::new(c.x as f32, c.y as f32) * CELL + Vec2::splat(15.);
    pointer(
        &mut app,
        window,
        point(from),
        Some((MouseButton::Left, ButtonState::Pressed)),
    );
    pointer(
        &mut app,
        window,
        point(to),
        Some((MouseButton::Left, ButtonState::Released)),
    );
    step(&mut app, 0.1);
    assert_eq!(
        app.world().resource::<Menu>().pages,
        pages,
        "input must not move its own replica"
    );
    let sent = flush(&mut app);
    assert_eq!(sent.len(), 1);
    assert_eq!(sent[0].0, "moveItem");
    assert_eq!(sent[0].1["x1"], to.x);
    assert_eq!(sent[0].1["y1"], to.y);
    pump(&host, &mut app, 16);
    step(&mut app, 0.1);
    let next = &app.world().resource::<Menu>().pages;
    assert!(at(next, from).is_none());
    assert_eq!(at(next, to).unwrap().id, item.id);
}
#[test]
fn accepted_magazine_round_advances_once_and_closing_pauses_its_next_request() {
    let (host, mut app, _) = setup();
    let view = app.world().resource::<Menu>().view.clone();
    let row = view
        .items
        .iter()
        .find(|r| r.magazine && view.tile(r.address.cell()).is_some_and(|t| t.amount > 1))
        .unwrap()
        .clone();
    let before = view.tile(row.address.cell()).unwrap().amount;
    assert!(
        app.world_mut()
            .resource_mut::<Menu>()
            .magazines
            .unload(&view, row.address.cell())
    );
    step(&mut app, 0.6);
    assert_eq!(
        app.world()
            .resource::<Menu>()
            .view
            .tile(row.address.cell())
            .unwrap()
            .amount,
        before
    );
    assert_eq!(
        flush(&mut app)
            .iter()
            .filter(|(name, _)| name == "magazineTransfer")
            .count(),
        1
    );
    pump(&host, &mut app, 18);
    step(&mut app, 0.1);
    let menu = app.world().resource::<Menu>();
    assert_eq!(
        menu.view.tile(row.address.cell()).unwrap().amount,
        before - 1
    );
    assert_eq!(menu.magazines.operations[0].done, 1);
    app.world_mut().resource_mut::<UiState>().screen = Screen::Playing;
    step(&mut app, 4.);
    assert!(flush(&mut app).is_empty());
    app.world_mut().resource_mut::<UiState>().screen = Screen::Inventory;
    step(&mut app, 0.2);
    assert!(flush(&mut app).is_empty());
    step(&mut app, 0.2);
    assert_eq!(flush(&mut app).len(), 1);
}

#[test]
fn production_dashboard_uses_frost_and_preserves_split_editor_through_accepted_refresh() {
    use crate::{
        character::nameplate::OriginalFont,
        ui::{backdrop::Backdrop, swoop::Swoop},
    };
    use bevy::text::EditableText;
    let (_host, mut app, _) = setup();
    app.init_resource::<Assets<Image>>()
        .init_resource::<Assets<feedback::WheelMaterial>>()
        .init_resource::<render::Art>()
        .init_resource::<badge::Badge>()
        .insert_resource(WorldConfig::default())
        .insert_resource(OriginalFont(default()));
    let cell = app
        .world()
        .resource::<Menu>()
        .view
        .items
        .iter()
        .find(|r| r.magazine)
        .unwrap()
        .address
        .cell();
    {
        let mut menu = app.world_mut().resource_mut::<Menu>();
        menu.gesture.selected = Some(cell);
        menu.gesture.split_amount = 15;
    }
    app.world_mut().run_system_once(render::build).unwrap();
    let world = app.world_mut();
    let root = world
        .query_filtered::<Entity, With<render::Root>>()
        .single(world)
        .unwrap();
    assert!(world.get::<Backdrop>(root).is_some());
    assert_eq!(world.get::<Swoop>(root).unwrap().0, Screen::Inventory);
    assert!(
        world
            .get::<BackgroundColor>(root)
            .is_none_or(|color| color.0.alpha() == 0.),
        "dashboard must not add an opaque global scrim"
    );
    let editor = world
        .query_filtered::<Entity, With<context::Number>>()
        .single(world)
        .unwrap();
    world
        .get_mut::<EditableText>(editor)
        .unwrap()
        .editor
        .set_text("7");
    world.resource_mut::<Menu>().dirty();
    app.world_mut().run_system_once(render::build).unwrap();
    let world = app.world_mut();
    assert_eq!(
        world
            .query_filtered::<Entity, With<context::Number>>()
            .single(world)
            .unwrap(),
        editor
    );
    assert_eq!(
        world
            .get::<EditableText>(editor)
            .unwrap()
            .value()
            .to_string(),
        "7"
    );
    assert!(
        world.get::<ChildOf>(editor).is_some(),
        "retained editor must be reparented before old panel teardown"
    );
    let caption = world
        .query::<&Text>()
        .iter(world)
        .map(|t| t.0.as_str())
        .collect::<Vec<_>>();
    assert!(
        caption.contains(&"PRIMARY")
            && caption.contains(&"SECONDARY")
            && caption.contains(&"Split")
    );
    app.init_resource::<bevy::input_focus::InputFocus>();
    app.world_mut()
        .resource_mut::<bevy::input_focus::InputFocus>()
        .set(editor, bevy::input_focus::FocusCause::Pressed);
    app.world_mut()
        .run_system_once(context::synchronize)
        .unwrap();
    assert_eq!(
        app.world().resource::<Menu>().gesture.split_amount,
        15,
        "source SpinBox does not update while typing"
    );
    app.world_mut().resource_mut::<Menu>().dirty();
    app.world_mut().run_system_once(render::build).unwrap();
    app.world_mut()
        .run_system_once(context::synchronize)
        .unwrap();
    assert_eq!(
        app.world()
            .get::<EditableText>(editor)
            .unwrap()
            .value()
            .to_string(),
        "7",
        "accepted refresh must retain draft text and focus"
    );
    app.world_mut()
        .resource_mut::<bevy::input_focus::InputFocus>()
        .clear();
    app.world_mut()
        .run_system_once(context::synchronize)
        .unwrap();
    assert_eq!(
        app.world().resource::<Menu>().gesture.split_amount,
        7,
        "source commits typed amount on focus loss"
    );
    assert!(
        flush(&mut app).is_empty(),
        "editing a preview amount sends no gameplay action"
    );
}

#[test]
fn accepted_inventory_rebuild_keeps_swoop_clock_and_close_retires_all_live_buttons() {
    use crate::{
        character::nameplate::OriginalFont,
        ui::{
            backdrop::Backdrop,
            swoop::{Swoop, SwoopPlugin},
        },
    };
    let (host, mut app, _) = setup();
    app.init_resource::<Assets<Image>>()
        .init_resource::<Assets<feedback::WheelMaterial>>()
        .init_resource::<render::Art>()
        .init_resource::<badge::Badge>()
        .insert_resource(WorldConfig::default())
        .insert_resource(OriginalFont(default()))
        .add_plugins(SwoopPlugin)
        .add_systems(Update, render::build.before(crate::ui::rebuild_overlay));
    let advance = |app: &mut App, dt: f32| {
        app.world_mut()
            .resource_mut::<Time>()
            .advance_by(std::time::Duration::from_secs_f32(dt));
        app.update();
    };
    let root = |app: &mut App| {
        app.world_mut()
            .query_filtered::<Entity, With<render::Root>>()
            .single(app.world())
            .unwrap()
    };
    advance(&mut app, 0.02125);
    let first = root(&mut app);
    assert!((app.world().get::<Backdrop>(first).unwrap().fade - 0.578125).abs() < 0.00001);
    app.world()
        .resource::<CoreBridge>()
        .request(
            "action",
            json!({"name":"console","args":{"text":"give 95"}}),
        )
        .unwrap();
    pump(&host, &mut app, 16);
    step(&mut app, 0.1);
    advance(&mut app, 0.02125);
    let next = root(&mut app);
    assert_ne!(first, next);
    assert!(app.world().get_entity(first).is_err());
    assert!((app.world().get::<Backdrop>(next).unwrap().fade - 0.875).abs() < 0.00001);
    app.world_mut().resource_mut::<UiState>().screen = Screen::Playing;
    step(&mut app, 0.);
    advance(&mut app, 0.);
    assert!(app.world().get::<render::Root>(next).is_none());
    assert_eq!(app.world().get::<Swoop>(next).unwrap().0, Screen::Inventory);
    assert_eq!(
        app.world_mut()
            .query_filtered::<Entity, With<bevy::ui_widgets::Button>>()
            .iter(app.world())
            .count(),
        0
    );
    assert!(
        app.world().resource::<paperdoll::Portrait>().active,
        "source isolated viewport remains visible until Closed"
    );
    advance(&mut app, 0.0425);
    assert!(app.world().get_entity(next).is_err());
    step(&mut app, 0.);
    assert!(!app.world().resource::<paperdoll::Portrait>().active);
}

#[test]
fn closing_cancels_pending_slot_equip_before_a_delayed_udp_move_echo() {
    let (host, mut app, _) = setup();
    app.world()
        .resource::<CoreBridge>()
        .request("action", json!({"name":"console","args":{"text":"give 4"}}))
        .unwrap();
    pump(&host, &mut app, 16);
    step(&mut app, 0.1);
    let view = app.world().resource::<Menu>().view.clone();
    let gun = view.items.iter().find(|row| row.address.id == 4).unwrap();
    let out = commands::dispatch(
        model::Intent::Equip(gun.address.cell()),
        &view,
        &app.world().resource::<Menu>().pages,
    );
    let pending = out
        .pending
        .expect("real gun starts in bag and must move into original primary slot");
    let slot = pending.cell;
    app.world_mut().resource_mut::<Menu>().pending_equip = Some(pending);
    for intent in out.commands {
        app.world_mut()
            .resource_mut::<Messages<UiIntent>>()
            .write(intent);
    }
    assert!(flush(&mut app).iter().any(|(name, _)| name == "moveItem"));
    app.world_mut().resource_mut::<UiState>().screen = Screen::Playing;
    step(&mut app, 0.);
    assert!(app.world().resource::<Menu>().pending_equip.is_none());
    assert!(app.world().resource::<Menu>().pending_selected.is_none());
    app.world_mut().resource_mut::<UiState>().screen = Screen::Inventory;
    step(&mut app, 0.);
    pump(&host, &mut app, 18);
    step(&mut app, 0.1);
    assert_eq!(
        app.world()
            .resource::<Menu>()
            .view
            .at(slot)
            .unwrap()
            .address
            .id,
        4,
        "the already-sent move still completes authoritatively"
    );
    assert!(
        flush(&mut app).is_empty(),
        "late move cannot select a gun after the source Close canceled that operation"
    );
    assert_eq!(app.world().resource::<UiState>().screen, Screen::Inventory);
}

#[test]
fn accepted_currency_table_matches_all_source_wallets_and_live_preview_rebuilds_only_its_tile() {
    use crate::character::nameplate::OriginalFont;
    let (host, mut app, _) = setup();
    for id in [1055, 1054] {
        app.world()
            .resource::<CoreBridge>()
            .request(
                "action",
                json!({"name":"console","args":{"text":format!("give {id}")}}),
            )
            .unwrap();
    }
    pump(&host, &mut app, 16);
    step(&mut app, 0.1);
    let view = app.world().resource::<Menu>().view.clone();
    let expected: Vec<Vec<u16>> =
        serde_json::from_str(include_str!("money/source-reference.json")).unwrap();
    assert_eq!(expected.len(), 501);
    for (amount, ids) in expected.into_iter().enumerate() {
        assert_eq!(
            money::breakdown(amount as u16, &view.tiles.currency)
                .iter()
                .map(|d| d.id)
                .collect::<Vec<_>>(),
            ids,
            "original Currency.Breakdown({amount})"
        );
    }
    let row = view.tiles.cells.iter().find(|c| c.tile.money).unwrap();
    assert_eq!(row.tile.amount, 150);
    let cell = model::Cell {
        page: row.page,
        x: row.x,
        y: row.y,
    };
    app.init_resource::<Assets<Image>>()
        .init_resource::<Assets<feedback::WheelMaterial>>()
        .init_resource::<render::Art>()
        .init_resource::<badge::Badge>()
        .insert_resource(WorldConfig::default())
        .insert_resource(OriginalFont(default()));
    {
        let mut menu = app.world_mut().resource_mut::<Menu>();
        menu.gesture.selected = Some(cell);
        menu.gesture.split_amount = 51;
    }
    app.world_mut().run_system_once(render::build).unwrap();
    app.world_mut().run_system_once(context::preview).unwrap();
    let editor = {
        let world = app.world_mut();
        world
            .query_filtered::<Entity, With<context::Number>>()
            .single(world)
            .unwrap()
    };
    let picture = |app: &mut App| {
        let world = app.world_mut();
        let root = world
            .query_filtered::<Entity, With<context::SplitPreview>>()
            .single(world)
            .unwrap();
        let descendants: Vec<_> = world
            .query::<&Children>()
            .query(world)
            .iter_descendants(root)
            .collect();
        let texts: Vec<_> = descendants
            .iter()
            .filter_map(|e| world.get::<Text>(*e).map(|t| t.0.clone()))
            .collect();
        let images = descendants
            .iter()
            .filter(|e| world.get::<ImageNode>(**e).is_some())
            .count();
        (root, images, texts)
    };
    let first = picture(&mut app);
    assert_eq!(first.1, 2, "$51 is one original fifty and one loonie");
    assert!(first.2.contains(&"$51".to_owned()));
    app.world_mut().resource_mut::<Menu>().gesture.split_amount = 1;
    app.world_mut().run_system_once(context::preview).unwrap();
    let one = picture(&mut app);
    assert_eq!(one.0, first.0);
    assert_eq!(
        one.1, 1,
        "a live amount change must not retain the old $150 fan"
    );
    assert!(one.2.contains(&"$1".to_owned()));
    assert!(
        app.world().get_entity(editor).is_ok(),
        "only preview children rebuild; draft focus survives"
    );
    assert_eq!(
        app.world()
            .resource::<Menu>()
            .view
            .tile(cell)
            .unwrap()
            .amount,
        150
    );
    assert!(
        flush(&mut app).is_empty(),
        "changing preview art emits no gameplay action"
    );
}
