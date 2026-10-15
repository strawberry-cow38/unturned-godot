//! Production widgets and observer against two independent managed UDP processes.
use super::*;

fn bridge() -> CoreBridge {
    CoreBridge::launch(std::path::Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap()
}
fn action(remote: &CoreBridge, name: &str, args: Value) -> Value {
    remote
        .request("action", json!({"name":name,"args":args}))
        .unwrap()
}
fn pump(host: &CoreBridge, app: &mut App, count: usize) {
    for _ in 0..count {
        host.request("tick", json!({})).unwrap();
        let reply = app
            .world()
            .resource::<CoreBridge>()
            .request("tick", json!({}))
            .unwrap();
        app.world_mut()
            .resource_mut::<UiModel>()
            .adopt_snapshot(&reply["state"]);
        app.world_mut().resource_mut::<GameSnapshot>().0 = reply["state"].clone();
        app.update();
        std::thread::sleep(std::time::Duration::from_millis(1));
    }
}
fn click(app: &mut App, entity: Entity) -> Value {
    let world = app.world_mut();
    world.trigger(Activate { entity });
    let intents: Vec<_> = world.resource_mut::<Messages<UiIntent>>().drain().collect();
    assert_eq!(intents.len(), 1);
    let UiIntent::Action { name, args } = &intents[0] else {
        panic!("expected source attachment intent")
    };
    let reply = action(world.resource::<CoreBridge>(), name, args.clone());
    assert_eq!(reply["result"]["queued"], true);
    args.clone()
}
fn state(app: &App) -> &Value {
    &app.world().resource::<GameSnapshot>().0
}
fn bag_items(app: &App, id: u16) -> Vec<Value> {
    state(app)["inventory"]["pages"]
        .as_array()
        .unwrap()
        .iter()
        .flat_map(|page| page["items"].as_array().unwrap())
        .filter(|item| item["id"] == id)
        .cloned()
        .collect()
}

#[test]
fn actual_udp_menu_waits_for_reply_refreshes_busy_and_spends_the_clicked_magazine() {
    let port = std::net::UdpSocket::bind("127.0.0.1:0")
        .unwrap()
        .local_addr()
        .unwrap()
        .port();
    let config = WorldConfig::default();
    let host = bridge();
    host.request("init", json!({"contentRoot":config.content_root,"listenPort":port,"allowCheats":true,"spawn":[0,0,0]})).unwrap();
    let remote = bridge();
    remote
        .request(
            "init",
            json!({"contentRoot":config.content_root,"connectHost":"127.0.0.1","connectPort":port}),
        )
        .unwrap();
    let mut app = App::new();
    app.add_plugins((
        MinimalPlugins,
        AssetPlugin {
            file_path: config.content_root.to_string_lossy().into_owned(),
            ..default()
        },
    ))
    .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(20),
    ))
    .insert_resource(remote)
    .insert_resource(config)
    .init_asset::<Image>()
    .init_resource::<Menu>()
    .init_resource::<Settings>()
    .init_resource::<UiState>()
    .init_resource::<UiModel>()
    .init_resource::<ItemCatalog>()
    .init_resource::<GameSnapshot>()
    .add_message::<UiIntent>()
    .add_systems(Update, (refresh, build).chain())
    .add_observer(activate);
    app.world_mut().resource_mut::<UiState>().screen = Screen::Playing;
    pump(&host, &mut app, 30);
    assert_eq!(state(&app)["connected"], true);
    let remote = app.world().resource::<CoreBridge>();
    action(remote, "console", json!({"text":"give 4"}));
    action(remote, "console", json!({"text":"give 6"}));
    pump(&host, &mut app, 12);
    let mut gun_address = None;
    for page in state(&app)["inventory"]["pages"].as_array().unwrap() {
        for item in page["items"].as_array().unwrap() {
            if item["id"] == 4 {
                gun_address = Some(json!({"page":page["index"],"x":item["x"],"y":item["y"]}));
            }
        }
    }
    action(
        app.world().resource::<CoreBridge>(),
        "equipAndHold",
        gun_address.unwrap(),
    );
    pump(&host, &mut app, 20);
    assert_eq!(
        state(&app)["weapon"]["held"]["id"],
        4,
        "needs the protocol62 equipment adapter"
    );
    action(app.world().resource::<CoreBridge>(), "fire", json!({}));
    pump(&host, &mut app, 12);
    assert_eq!(state(&app)["weapon"]["ammo"], 29);
    let selection = state(&app)["weapon"]["selectionVersion"].clone();
    app.world_mut().resource_mut::<Menu>().selection = selection;
    app.world_mut().resource_mut::<UiState>().screen = Screen::Attachments;
    app.update(); // query sends, but authority has not received/published it
    assert!(app.world().resource::<Menu>().pending);
    let world = app.world_mut();
    assert_eq!(world.query::<&Action>().iter(world).count(), 0);
    assert!(
        world
            .query::<&Text>()
            .iter(world)
            .any(|label| label.0 == "Loading attachments…")
    );
    pump(&host, &mut app, 24);
    assert!(!app.world().resource::<Menu>().pending);
    let world = app.world_mut();
    let remove = world
        .query::<(Entity, &Action)>()
        .iter(world)
        .find(|(_, a)| a.name == "removeAttachment" && a.args["slot"] == "Magazine")
        .unwrap()
        .0;
    click(&mut app, remove);
    assert_eq!(
        state(&app)["weapon"]["ammo"],
        29,
        "UI request cannot spend local ammunition"
    );
    pump(&host, &mut app, 12);
    assert_eq!(state(&app)["weapon"]["ammo"], 1);
    assert_eq!(state(&app)["weapon"]["reloading"], true);
    let world = app.world_mut();
    assert_eq!(
        world.query::<&Action>().iter(world).count(),
        0,
        "accepted busy state disables the old buttons"
    );
    pump(&host, &mut app, 170);
    assert_eq!(state(&app)["weapon"]["reloading"], false);
    let world = app.world_mut();
    let magazines: Vec<_> = world
        .query::<(Entity, &Action, &Label)>()
        .iter(world)
        .filter(|(_, a, _)| a.name == "installAttachment" && a.args["slot"] == "Magazine")
        .map(|(entity, action, label)| (entity, action.args.clone(), label.0.clone()))
        .collect();
    assert_eq!(magazines.len(), 2);
    let used = magazines
        .iter()
        .find(|(_, _, label)| label.contains("28 rounds"))
        .unwrap();
    let full = magazines
        .iter()
        .find(|(_, _, label)| label.contains("30 rounds"))
        .unwrap();
    assert_ne!(used.1["instanceToken"], full.1["instanceToken"]);
    let full_token = full.1["instanceToken"].clone();
    let sent = click(&mut app, used.0);
    assert_eq!(sent["instanceToken"], used.1["instanceToken"]);
    assert_eq!(state(&app)["weapon"]["ammo"], 1);
    pump(&host, &mut app, 12);
    assert_eq!(state(&app)["weapon"]["ammo"], 29);
    let remaining = bag_items(&app, 6);
    assert_eq!(remaining.len(), 1);
    assert_eq!(remaining[0]["amount"], 30);
    pump(&host, &mut app, 170);
    let choices = app.world().resource::<Menu>().options["slots"]
        .as_array()
        .unwrap()
        .iter()
        .find(|slot| slot["name"] == "Magazine")
        .unwrap()["choices"]
        .as_array()
        .unwrap()
        .clone();
    assert_eq!(choices.len(), 1);
    assert_eq!(
        choices[0]["instanceToken"], full_token,
        "the unclicked identical magazine stays owned"
    );
    assert_eq!(
        app.world()
            .resource::<CoreBridge>()
            .request("diagnostics", json!({}))
            .unwrap()["result"]["host"],
        false
    );
}
