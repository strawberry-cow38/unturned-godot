//! Production dashboard with a separate host and received Nearby owner facts.
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
    ground: Vec<u64>,
    vehicle: u64,
    acted: bool,
}
#[test]
#[ignore = "requires Vulkan/display; actual UDP Nearby receiver Take and Store"]
fn gpu_nearby_received_context_take_and_store() {
    let prefix = std::env::var("UG_INVENTORY_SHOT").unwrap_or_else(|_| "/tmp/bevy-nearby".into());
    let config = WorldConfig::default();
    let launch = || CoreBridge::launch(std::path::Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
    let host = launch();
    let socket = std::net::UdpSocket::bind("127.0.0.1:0").unwrap();
    let port = socket.local_addr().unwrap().port();
    drop(socket);
    host.request("init",json!({"contentRoot":config.content_root,"listenPort":port,"allowCheats":true,"spawn":[0,0,0]})).unwrap();
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
    let reg = crate::vehicle::registration(
        crate::vehicle::data::named("police").unwrap(),
        "nearby-visual".into(),
        Vec3::new(0., 0., -1.),
        0.,
        0,
    );
    let vehicle = host
        .request("worldVehicles", json!({"vehicles":[reg]}))
        .unwrap()["result"]["vehicles"][0]["netId"]
        .as_u64()
        .unwrap();
    let spawned=host.request("worldSpawn",json!({"items":[{"itemId":6,"amount":23,"quality":67,"position":[0,1.75,-1]},{"itemId":4,"quality":43,"position":[1,1.75,-1]}]})).unwrap();
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
    model.map_name = "Nearby owner / actual UDP".into();
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
                    title: "Nearby accepted world inventory".into(),
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
        ground,
        vehicle,
        acted: false,
    })
    .add_systems(Startup, scene)
    .add_systems(Update, adopt.before(controller::update))
    .add_systems(Last, record);
    app.run();
    let facts: Vec<Value> =
        serde_json::from_slice(&std::fs::read(format!("{prefix}.json")).unwrap()).unwrap();
    assert_eq!(facts.len(), 4);
    assert_eq!(facts[0]["area"].as_array().unwrap().len(), 2);
    assert_eq!(facts[1]["actions"], json!(["Take", "Close"]));
    assert_eq!(facts[2]["area"].as_array().unwrap().len(), 1);
    assert!(
        facts[2]["own"]
            .as_array()
            .unwrap()
            .iter()
            .any(|r| r["id"] == 6 && r["amount"] == 23)
    );
    assert_eq!(facts[3]["area"], json!([]));
    assert!(
        facts[3]["storage"]
            .as_array()
            .unwrap()
            .iter()
            .any(|r| r["id"] == 4 && r["quality"] == 43)
    );
    assert_eq!(facts[3]["worldRemaining"], 0);
    for stage in 0..4 {
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
    for intent in intents {
        if let UiIntent::Action { name, mut args } = intent {
            if name == "nearbyInventory" {
                args["eye"] = json!([0, 1.75, 0]);
            }
            action(world.resource::<CoreBridge>(), &name, args);
        }
    }
    tick(&world.resource::<Host>().0, 1);
    let state = tick(world.resource::<CoreBridge>(), 1);
    world.resource_mut::<UiModel>().adopt_snapshot(&state);
    world.resource_mut::<GameSnapshot>().0 = state;
}
fn area(world: &World, id: u16) -> Cell {
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
        c.acted = false;
        match c.stage {
            1 => {
                let cell = area(world, 6);
                let p = point(world, cell);
                pointer(world, p, MouseButton::Right, ButtonState::Pressed);
                pointer(world, p, MouseButton::Right, ButtonState::Released);
            }
            2 => world
                .resource_mut::<Menu>()
                .pending_actions
                .push(controller::Action::Context("transfer".into())),
            3 => action(
                world.resource::<CoreBridge>(),
                "openVehicleStorage",
                json!({"vehicleNetId":c.vehicle,"kind":0}),
            ),
            _ => {
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
    }
    c.frame += 1;
    if c.stage == 3 && c.frame == 20 {
        let cell = area(world, 4);
        let mut menu = world.resource_mut::<Menu>();
        assert_ne!(menu.view.open_crate_id, 0);
        menu.gesture.selected = Some(cell);
        menu.pending_actions
            .push(controller::Action::Context("transfer".into()));
        c.acted = true;
    }
    if c.frame == 60 {
        let menu = world.resource::<Menu>();
        let rows = |predicate: fn(u8) -> bool| {
            menu.view
                .tiles
                .cells
                .iter()
                .filter(|r| predicate(r.page))
                .map(|r| json!({"id":r.tile.id,"amount":r.tile.amount,"quality":r.tile.quality}))
                .collect::<Vec<_>>()
        };
        let actions = menu
            .gesture
            .selected
            .and_then(|cell| menu.view.at(cell))
            .map(|r| {
                r.actions
                    .iter()
                    .map(|b| b.label.clone())
                    .collect::<Vec<_>>()
            })
            .unwrap_or_default();
        let remaining = world.resource::<GameSnapshot>().0["worldItems"]
            .as_array()
            .unwrap()
            .iter()
            .filter(|e| c.ground.contains(&e["netId"].as_u64().unwrap()))
            .count();
        c.facts.push(json!({"stage":c.stage,"area":rows(|p|p==AREA),"own":rows(|p|p<OWN_PAGES),"storage":rows(|p|p==STORAGE),"actions":actions,"worldRemaining":remaining,"state":world.resource::<GameSnapshot>().0}));
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
