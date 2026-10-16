//! Production dashboard driving command 87 over actual UDP: a ground split
//! strip drag, a rotated whole-row drag and context Empty. Pointer/key edges
//! are scripted into the production ordered-input controller, not an OS mouse.
use super::*;
#[derive(Resource)]
struct Host(CoreBridge);
#[derive(Resource)]
struct Journey {
    prefix: String,
    stage: usize,
    frame: u32,
    pending: bool,
    done: Arc<Mutex<bool>>,
    facts: Vec<Value>,
    sent: Vec<Value>,
    ground: Vec<u64>,
}
const STAGES: usize = 9;
#[test]
#[ignore = "requires Vulkan/display; actual UDP Nearby split, rotated place and Empty"]
fn gpu_nearby_split_rotated_place_and_empty() {
    let prefix =
        std::env::var("UG_INVENTORY_SHOT").unwrap_or_else(|_| "/tmp/bevy-nearby-ops".into());
    let config = WorldConfig::default();
    let launch = || CoreBridge::launch(std::path::Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
    let host = launch();
    let socket = std::net::UdpSocket::bind("127.0.0.1:0").unwrap();
    let port = socket.local_addr().unwrap().port();
    drop(socket);
    host.request(
        "init",
        json!({"contentRoot":config.content_root,"listenPort":port,"allowCheats":true,"spawn":[0,0,0]}),
    )
    .unwrap();
    let peer = launch();
    peer.request("init",json!({"contentRoot":config.content_root,"connectHost":"127.0.0.1","connectPort":port,"name":"Nearby owner"})).unwrap();
    let pump = |n| {
        let mut state = Value::Null;
        for _ in 0..n {
            tick(&host, 1);
            state = tick(&peer, 1);
            std::thread::sleep(std::time::Duration::from_millis(1));
        }
        state
    };
    pump(40);
    let spawned = host
        .request(
            "worldSpawn",
            json!({"items":[
                {"itemId":6,"amount":23,"quality":67,"position":[0,1.75,-1]},
                {"itemId":28,"quality":81,"fuelLevel":12000.0,"position":[0.6,1.75,-1]},
                {"itemId":17,"amount":12,"quality":64,"position":[-0.6,1.75,-1]}]}),
        )
        .unwrap();
    let ground = spawned["result"]["entities"]
        .as_array()
        .unwrap()
        .iter()
        .map(|e| e["netId"].as_u64().unwrap())
        .collect::<Vec<_>>();
    action(
        &peer,
        "nearbyInventory",
        json!({"action":"Open","eye":[0,1.75,0]}),
    );
    let snapshot = pump(20);
    let mut model = UiModel::default();
    model.adopt_snapshot(&snapshot);
    model.map_name = "Nearby operations / actual UDP".into();
    let mut app = App::new();
    app.add_plugins(
        DefaultPlugins
            .set(AssetPlugin {
                file_path: config
                    .content_root
                    .canonicalize()
                    .unwrap()
                    .to_string_lossy()
                    .into(),
                ..default()
            })
            .set(WindowPlugin {
                primary_window: Some(Window {
                    title: "Nearby accepted ground operations".into(),
                    resolution: (1600, 1000).into(),
                    ..default()
                }),
                ..default()
            })
            .set(WinitPlugin {
                run_on_any_thread: true,
                ..default()
            }),
    )
    .insert_resource(config)
    .insert_resource(peer)
    .insert_resource(Host(host))
    .insert_resource(crate::avatar::remotes::tests::source_catalog())
    .insert_resource(GameSnapshot(snapshot))
    .insert_resource(model)
    .insert_resource(UiState {
        screen: Screen::Inventory,
        ..default()
    })
    .init_resource::<WorldSessionEpoch>()
    .init_resource::<Settings>()
    .init_resource::<FrameInput>()
    .init_resource::<crate::ui::crafting::Menu>()
    .init_resource::<crate::viewmodel::lighting::NearbyLight>()
    .add_message::<UiIntent>()
    .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(20),
    ))
    .add_plugins((
        crate::content::ContentPlugin,
        crate::rig::RigPlugin,
        crate::character::CharacterPlugin,
    ))
    .add_plugins((
        crate::ui::backdrop::BackdropPlugin,
        crate::ui::swoop::SwoopPlugin,
        crate::ui::navbar::NavbarPlugin,
        crate::ui::vitals::VitalsPlugin,
        InventoryPlugin,
    ))
    .insert_resource(Journey {
        prefix: prefix.clone(),
        stage: 0,
        frame: 0,
        pending: false,
        done: default(),
        facts: vec![],
        sent: vec![],
        ground,
    })
    .add_systems(Startup, scene)
    .add_systems(Update, adopt.before(controller::update))
    .add_systems(Last, record);
    app.run();
    let facts: Vec<Value> =
        serde_json::from_slice(&std::fs::read(format!("{prefix}.json")).unwrap()).unwrap();
    assert_eq!(facts.len(), STAGES);
    let ground_amount = |stage: usize, id: u16| {
        facts[stage]["area"]
            .as_array()
            .unwrap()
            .iter()
            .find(|r| r["id"] == id)
            .map(|r| r["amount"].clone())
    };
    assert_eq!(ground_amount(0, 6), Some(json!(23)));
    assert!(!facts[0]["own"].as_array().unwrap().iter().any(|r| r["id"] == 6));
    assert_eq!(facts[1]["split"], json!(5), "source strip on a ground stack");
    assert_eq!(ground_amount(2, 6), Some(json!(18)));
    assert!(facts[2]["own"].as_array().unwrap().iter().any(|r| r["id"] == 6 && r["amount"] == 5 && r["rot"] == 0));
    assert_eq!(facts[3]["carry"], json!({"id":6,"rotation":1}));
    assert_eq!(ground_amount(4, 6), None);
    assert!(facts[4]["own"].as_array().unwrap().iter().any(|r| r["id"] == 6 && r["amount"] == 18 && r["rot"] == 1));
    assert_eq!(facts[5]["actions"], json!(["Empty", "Take", "Close"]));
    assert_eq!(facts[5]["fuel"], json!(12000.0));
    assert_eq!(facts[6]["fuel"], json!(0.0));
    assert_eq!(facts[6]["worldRemaining"], 2, "Empty never takes the can");
    assert_eq!(facts[7]["actions"], json!(["Take", "Unload", "Close"]));
    let drum = ground_amount(8, 17).unwrap().as_u64().unwrap();
    let loose = facts[8]["own"]
        .as_array()
        .unwrap()
        .iter()
        .filter(|r| r["id"] != 6)
        .map(|r| r["amount"].as_u64().unwrap())
        .sum::<u64>();
    assert!((1..12).contains(&drum), "accepted ground unload in progress: {drum}");
    assert_eq!(drum + loose, 12, "every round is either in the drum or the bag");
    assert_eq!(facts[8]["wheel"], json!(true), "source wheel on the ground drum");
    let journey: Vec<Value> = facts[8]["sent"].as_array().unwrap().clone();
    let operations = journey
        .iter()
        .filter(|s| s["name"] == "nearbyOperation")
        .map(|s| s["args"]["operation"].clone())
        .collect::<Vec<_>>();
    assert_eq!(operations, [json!("Split"), json!("Place"), json!("EmptyFuel")]);
    let unloads = journey
        .iter()
        .filter(|s| s["name"] == "nearbyMagazineTransfer")
        .count() as u64;
    assert!(unloads >= 12 - drum, "one accepted request per unloaded round");
    assert!(
        journey.iter().all(|s| matches!(
            s["name"].as_str(),
            Some("nearbyOperation" | "nearbyInventory" | "nearbyMagazineTransfer")
        )),
        "no legacy grid command touched AREA: {journey:?}"
    );
    for stage in 0..STAGES {
        assert!(
            std::fs::metadata(format!("{prefix}-{stage}.png"))
                .unwrap()
                .len()
                > 20_000
        );
    }
}
fn adopt(world: &mut World) {
    let intents = world
        .resource_mut::<Messages<UiIntent>>()
        .drain()
        .collect::<Vec<_>>();
    let mut sent = vec![];
    for intent in intents {
        if let UiIntent::Action { name, mut args } = intent {
            // The production forwarder's camera rule (player::ui_intents).
            if crate::player::needs_eye(&name) {
                args["eye"] = json!([0, 1.75, 0]);
            }
            action(world.resource::<CoreBridge>(), &name, args.clone());
            sent.push(json!({"name":name,"args":args}));
        }
    }
    world.resource_mut::<Journey>().sent.extend(sent);
    tick(&world.resource::<Host>().0, 1);
    let state = tick(world.resource::<CoreBridge>(), 1);
    world.resource_mut::<UiModel>().adopt_snapshot(&state);
    world.resource_mut::<GameSnapshot>().0 = state;
}
fn ground(world: &World, id: u16) -> Cell {
    world
        .resource::<Menu>()
        .view
        .items
        .iter()
        .find(|r| r.address.page == AREA && r.address.id == id)
        .unwrap()
        .address
        .cell()
}
fn cell_point(world: &World, page: u8, x: u8, y: u8, inset: f32) -> Vec2 {
    let menu = world.resource::<Menu>();
    let grid = menu.hits.grids.iter().find(|g| g.page == page).unwrap();
    grid.rect.min + Vec2::new(x as f32, y as f32) * CELL + Vec2::splat(inset)
}
fn rotate(world: &mut World) {
    let window = world
        .query_filtered::<Entity, With<PrimaryWindow>>()
        .single(world)
        .unwrap();
    world
        .resource_mut::<FrameInput>()
        .push(WindowEvent::KeyboardInput(bevy::input::keyboard::KeyboardInput {
            key_code: KeyCode::KeyR,
            logical_key: bevy::input::keyboard::Key::Character("r".into()),
            state: ButtonState::Pressed,
            text: None,
            repeat: false,
            window,
        }));
}
fn hover(world: &mut World, point: Vec2) {
    let window = world
        .query_filtered::<Entity, With<PrimaryWindow>>()
        .single(world)
        .unwrap();
    world
        .resource_mut::<FrameInput>()
        .push(WindowEvent::CursorMoved(CursorMoved {
            window,
            position: point,
            delta: None,
        }));
}
fn record(world: &mut World) {
    world.resource_mut::<FrameInput>().begin();
    let mut c = world.remove_resource::<Journey>().unwrap();
    if c.pending {
        if !*c.done.lock().unwrap() {
            world.insert_resource(c);
            return;
        }
        *c.done.lock().unwrap() = false;
        c.pending = false;
        c.frame = 0;
        c.stage += 1;
        if c.stage == STAGES {
            std::fs::write(
                format!("{}.json", c.prefix),
                serde_json::to_vec_pretty(&c.facts).unwrap(),
            )
            .unwrap();
            world.write_message(AppExit::Success);
            world.insert_resource(c);
            return;
        }
    }
    c.frame += 1;
    match (c.stage, c.frame) {
        // 1: source RMB click on the ground stack opens its context strip.
        (1, 10) => {
            let p = point(world, ground(world, 6));
            pointer(world, p, MouseButton::Right, ButtonState::Pressed);
            pointer(world, p, MouseButton::Right, ButtonState::Released);
        }
        (1, 20) => world.resource_mut::<Menu>().gesture.split_amount = 5,
        // 2: drag the preview slot out onto an empty pocket cell.
        (2, 10) => {
            let preview = world.resource::<Menu>().hits.split_preview.unwrap().center();
            pointer(world, preview, MouseButton::Left, ButtonState::Pressed);
            let to = cell_point(world, 2, 0, 0, 30.);
            pointer(world, to, MouseButton::Left, ButtonState::Released);
        }
        // 3: pick the remaining ground row up, press R, carry it over pocket.
        (3, 10) => {
            let p = cell_point(world, AREA, ground(world, 6).x, ground(world, 6).y, 18.);
            pointer(world, p, MouseButton::Left, ButtonState::Pressed);
            rotate(world);
            let over = cell_point(world, 2, 4, 0, 18.);
            hover(world, over);
        }
        // 4: release; the accepted rotated cell arrives from the host.
        (4, 10) => {
            let over = cell_point(world, 2, 4, 0, 18.);
            pointer(world, over, MouseButton::Left, ButtonState::Released);
        }
        // 5: the ground can's source context offers Empty.
        (5, 10) => {
            let p = point(world, ground(world, 28));
            pointer(world, p, MouseButton::Right, ButtonState::Pressed);
            pointer(world, p, MouseButton::Right, ButtonState::Released);
        }
        (6, 10) => {
            let cell = ground(world, 28);
            let mut menu = world.resource_mut::<Menu>();
            menu.gesture.selected = Some(cell);
            menu.pending_actions
                .push(controller::Action::Context("emptyFuel".into()));
        }
        // 7: the ground drum's source context offers Unload.
        (7, 10) => {
            let p = point(world, ground(world, 17));
            pointer(world, p, MouseButton::Right, ButtonState::Pressed);
            pointer(world, p, MouseButton::Right, ButtonState::Released);
        }
        // 8: timed source unload; each round waits for its accepted reply.
        (8, 10) => world
            .resource_mut::<Menu>()
            .pending_actions
            .push(controller::Action::Context("unload".into())),
        _ => {}
    }
    if c.frame == 60 {
        let menu = world.resource::<Menu>();
        let rows = |predicate: fn(u8) -> bool| {
            menu.pages
                .iter()
                .filter(|p| predicate(p.index))
                .flat_map(|p| p.items.iter())
                .map(|i| json!({"id":i.id,"amount":i.amount,"x":i.x,"y":i.y,"rot":i.rot}))
                .collect::<Vec<_>>()
        };
        let actions = menu
            .gesture
            .selected
            .and_then(|cell| menu.view.at(cell))
            .map(|r| r.actions.iter().map(|b| b.label.clone()).collect::<Vec<_>>())
            .unwrap_or_default();
        let fuel = menu
            .view
            .items
            .iter()
            .find(|r| r.address.page == AREA && r.address.id == 28)
            .map(|r| r.fuel);
        let carry = menu
            .gesture
            .carry
            .as_ref()
            .map(|carry| json!({"id":carry.item.id,"rotation":carry.rotation}));
        let split = menu.gesture.selected.map(|_| menu.gesture.split_amount);
        let remaining = world.resource::<GameSnapshot>().0["worldItems"]
            .as_array()
            .unwrap()
            .iter()
            .filter(|e| c.ground.contains(&e["netId"].as_u64().unwrap()))
            .count();
        let wheel = menu
            .magazines
            .operations
            .iter()
            .any(|op| op.magazine.page == AREA && op.unloading);
        c.facts.push(json!({"stage":c.stage,"area":rows(|p|p==AREA),"own":rows(|p|p<OWN_PAGES),
            "actions":actions,"fuel":fuel,"carry":carry,"split":split,"notice":menu.notice,"wheel":wheel,
            "worldRemaining":remaining,"sent":c.sent,"nearby":world.resource::<GameSnapshot>().0["nearby"]}));
        let done = c.done.clone();
        world
            .spawn(Screenshot::primary_window())
            .observe(save_to_disk(format!("{}-{}.png", c.prefix, c.stage)))
            .observe(move |_: On<ScreenshotCaptured>| *done.lock().unwrap() = true);
        c.pending = true;
    }
    assert!(c.frame < 300);
    world.insert_resource(c);
}
