use super::*;
fn dialogue_view(revision: u64, id: u32) -> View {
    serde_json::from_value(json!({"revision":revision,"openDialogue":id,"dialogue":{"id":id,"pages":["First page","Second page"],"responses":[{"index":1,"text":"Supplies"},{"index":3,"text":"Goodbye","endsConversation":true}]}})).unwrap()
}
fn app() -> App {
    let mut app = App::new();
    app.add_plugins(MinimalPlugins)
        .init_resource::<Menu>()
        .init_resource::<UiState>()
        .init_resource::<ButtonInput<KeyCode>>()
        .init_resource::<ButtonInput<MouseButton>>()
        .add_message::<UiIntent>()
        .add_systems(Update, input)
        .add_observer(activate);
    app
}
fn intents(app: &mut App) -> Vec<UiIntent> {
    app.world_mut()
        .resource_mut::<Messages<UiIntent>>()
        .drain()
        .collect()
}
#[test]
fn keyboard_finishes_pages_before_numbered_original_response_and_waits_for_authority() {
    let mut app = app();
    app.world_mut().resource_mut::<UiState>().screen = Screen::Dialogue;
    app.world_mut().resource_mut::<Menu>().view = dialogue_view(1, 59);
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .press(KeyCode::Digit1);
    app.update();
    assert!(
        intents(&mut app).is_empty(),
        "Responses cannot skip the message pages"
    );
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .clear();
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .press(KeyCode::Space);
    app.update();
    assert_eq!(app.world().resource::<Menu>().page, 1);
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .clear();
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .press(KeyCode::Digit2);
    app.update();
    let events = intents(&mut app);
    let [UiIntent::Action { name, args }] = events.as_slice() else {
        panic!("one command")
    };
    assert_eq!(name, "npcChoose");
    assert_eq!(args, &json!({"dialogue":59,"response":3}));
    assert_eq!(
        app.world().resource::<Menu>().view.open_dialogue,
        59,
        "Choosing does not navigate optimistically"
    );
}
#[test]
fn accepted_vendor_opens_once_and_escape_returns_to_the_accepted_conversation() {
    let mut menu = Menu::default();
    let mut ui = UiState {
        screen: Screen::Playing,
        ..default()
    };
    let mut view = dialogue_view(1, 87);
    menu.adopt(view.clone(), &mut ui);
    assert_eq!(ui.screen, Screen::Dialogue);
    view.vendor_revision = 1;
    view.vendor_opened = "hangar".into();
    view.revision = 2;
    menu.adopt(view.clone(), &mut ui);
    assert_eq!(ui.screen, Screen::Trade);
    menu.close_trade(&mut ui);
    assert_eq!(ui.screen, Screen::Dialogue);
    view.revision = 3;
    menu.adopt(view, &mut ui);
    assert_eq!(
        ui.screen,
        Screen::Dialogue,
        "An unrelated state cannot replay the vendor fact"
    );
    menu.adopt(
        View {
            revision: 4,
            ..default()
        },
        &mut ui,
    );
    assert_eq!(ui.screen, Screen::Playing);
}
#[test]
fn actual_trade_buttons_send_original_vendor_index_and_cap_without_mutating_wallet() {
    let mut app = app();
    app.world_mut().resource_mut::<UiState>().screen = Screen::Trade;
    app.world_mut().resource_mut::<Menu>().view=serde_json::from_value(json!({"wallet":55,"trade":{"guid":"guid","buying":[{"index":7,"itemId":70,"held":123,"maxCount":100,"enabled":true}],"selling":[{"index":2,"vehicle":true,"enabled":false}]}})).unwrap();
    let select = app
        .world_mut()
        .spawn((Button, Action::Select(true, 7)))
        .id();
    let sell = app.world_mut().spawn((Button, Action::Trade(true))).id();
    app.world_mut().trigger(Activate { entity: select });
    app.world_mut().trigger(Activate { entity: sell });
    let events = intents(&mut app);
    let [UiIntent::Action { name, args }] = events.as_slice() else {
        panic!("one trade")
    };
    assert_eq!(name, "npcTrade");
    assert_eq!(
        args,
        &json!({"vendor":"guid","sell":true,"index":7,"count":100})
    );
    assert_eq!(app.world().resource::<Menu>().view.wallet, 55);
    app.world_mut().resource_mut::<UiState>().screen = Screen::Playing;
    app.world_mut().trigger(Activate { entity: sell });
    assert!(
        intents(&mut app).is_empty(),
        "Detached controls cannot trade after close"
    );
}
#[test]
fn menu_renderer_builds_only_available_choices_and_tracker_disappears_under_modals() {
    let mut app = App::new();
    app.add_plugins((MinimalPlugins, AssetPlugin::default()))
        .init_asset::<Image>();
    app.insert_resource(Menu {
        view: dialogue_view(1, 59),
        page: 1,
        ..default()
    })
    .insert_resource(UiState {
        screen: Screen::Dialogue,
        ..default()
    })
    .add_systems(Update, draw);
    app.update();
    let w = app.world_mut();
    let mut actions = w.query::<&Action>();
    let indices: Vec<_> = actions
        .iter(w)
        .filter_map(|a| {
            if let Action::Choose(i) = a {
                Some(*i)
            } else {
                None
            }
        })
        .collect();
    assert_eq!(indices, [1, 3]);
    w.resource_mut::<UiState>().screen = Screen::Playing;
    w.resource_mut::<Menu>().view.tracker = Some(Quest {
        name: "Nautical Nuisance".into(),
        status: "active".into(),
        objectives: vec![Objective {
            text: "Cleanup 3/9 Barnacles".into(),
            done: false,
        }],
        ..default()
    });
    app.update();
    assert_eq!(
        app.world_mut()
            .query_filtered::<Entity, With<Root>>()
            .iter(app.world())
            .count(),
        1
    );
    app.world_mut().resource_mut::<UiState>().screen = Screen::Inventory;
    app.update();
    assert_eq!(
        app.world_mut()
            .query_filtered::<Entity, With<Root>>()
            .iter(app.world())
            .count(),
        0
    );
}

#[test]
fn original_trade_console_reads_catalog_without_opening_or_mutating_a_conversation() {
    let bridge = CoreBridge::launch(std::path::Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
    let content = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap()
        .join("game/content");
    bridge
        .request("init", json!({"contentRoot":content}))
        .unwrap();
    let mut app = App::new();
    app.add_plugins(MinimalPlugins)
        .insert_resource(bridge)
        .init_resource::<Menu>()
        .insert_resource(UiState {
            screen: Screen::Console,
            ..default()
        })
        .init_resource::<UiModel>()
        .add_message::<UiIntent>()
        .add_systems(Update, console);
    app.world_mut().resource_mut::<UiModel>().connected = true;
    app.world_mut()
        .resource_mut::<Messages<UiIntent>>()
        .write(UiIntent::Action {
            name: "console".into(),
            args: json!({"text":"trade Chef_Fresh_Food_Market"}),
        });
    app.update();
    assert_eq!(app.world().resource::<UiState>().screen, Screen::Trade);
    let menu = app.world().resource::<Menu>();
    assert_eq!(menu.view.open_dialogue, 0);
    assert_eq!(menu.view.wallet, 0);
    assert_eq!(
        menu.view.trade.as_ref().unwrap().key,
        "Chef_Fresh_Food_Market"
    );
    assert!(
        menu.view
            .trade
            .as_ref()
            .unwrap()
            .selling
            .iter()
            .all(|r| !r.enabled)
    );
    let state = app
        .world()
        .resource::<CoreBridge>()
        .request("snapshot", json!({}))
        .unwrap();
    assert_eq!(
        state["state"]["npc"]["revision"], 0,
        "Opening a read-only vendor view sends no NPC command"
    );
}

#[test]
fn session_epoch_or_disconnect_clears_old_vendor_pages_tracker_and_pin() {
    let mut app = App::new();
    app.add_plugins(MinimalPlugins)
        .insert_resource(WorldSessionEpoch(8))
        .insert_resource(GameSnapshot(json!({"connected":true})))
        .insert_resource(UiState {
            screen: Screen::Trade,
            ..default()
        })
        .insert_resource(Menu {
            epoch: 7,
            vendor: "previous-world-vendor".into(),
            page: 1,
            pinned: 157,
            scroll: [50., 200., 10.],
            view: dialogue_view(9, 59),
            ..default()
        })
        .add_systems(Update, refresh);
    app.update();
    let menu = app.world().resource::<Menu>();
    assert_eq!(menu.epoch, 8);
    assert_eq!(menu.view, View::default());
    assert!(menu.vendor.is_empty());
    assert_eq!(menu.page, 0);
    assert_eq!(menu.pinned, 0);
    assert_eq!(menu.scroll, [0.; 3]);
    assert_eq!(app.world().resource::<UiState>().screen, Screen::Playing);
    app.world_mut().resource_mut::<UiState>().screen = Screen::Quests;
    app.world_mut().resource_mut::<Menu>().view = dialogue_view(2, 59);
    app.world_mut().resource_mut::<GameSnapshot>().0 = json!({"connected":false});
    app.update();
    assert_eq!(app.world().resource::<Menu>().view, View::default());
    assert_eq!(app.world().resource::<UiState>().screen, Screen::Playing);
}

#[test]
fn information_embeds_accepted_quests_and_pin_changes_no_authoritative_state() {
    let mut app = App::new();
    app.add_plugins(MinimalPlugins)
        .init_resource::<Menu>()
        .init_resource::<UiState>()
        .add_message::<UiIntent>()
        .add_observer(activate);
    let view:View=serde_json::from_value(json!({"revision":8,"quests":[{"id":124,"name":"Research Recovery","status":"active","objectives":[{"text":"Find 0/4 Research Capsules","done":false}]}]})).unwrap();
    app.world_mut().resource_mut::<Menu>().view = view.clone();
    app.world_mut().resource_mut::<UiState>().screen = Screen::Information;
    let pin = app.world_mut().spawn((Button, Action::Pin(124))).id();
    app.world_mut().trigger(Activate { entity: pin });
    assert_eq!(app.world().resource::<Menu>().pinned, 124);
    assert_eq!(app.world().resource::<Menu>().view, view);
    assert!(intents(&mut app).is_empty());
    app.world_mut().trigger(Activate { entity: pin });
    assert_eq!(app.world().resource::<Menu>().pinned, 0);
    app.world_mut().resource_mut::<UiState>().screen = Screen::Playing;
    app.world_mut().trigger(Activate { entity: pin });
    assert_eq!(
        app.world().resource::<Menu>().pinned,
        0,
        "Detached map controls cannot change the pin"
    );
}
