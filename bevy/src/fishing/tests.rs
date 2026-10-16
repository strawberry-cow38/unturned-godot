//! Source FisherPrimary/FisherRelease/TickFishing on the client, and a native listen host
//! fishing through its own server (v80) over the real bridge.
use super::*;
use crate::{
    ui::{Screen, UiState},
    world::WorldConfig,
};
use bevy::input::mouse::MouseButtonInput;
use std::path::Path;

const ROD: u16 = 503;
fn rod() -> Option<Cell> {
    Some(Cell { id: ROD, page: 2, x: 0, y: 0 })
}
fn frame(press: bool, release: bool, held: Option<Cell>, fishable: &dyn Fn(Vec3) -> bool) -> Frame<'_> {
    Frame {
        dt: 0.02,
        held,
        skill: 0,
        press,
        release,
        eye: (Vec3::new(0., 1.6, 0.), Vec3::new(0., -0.2, -1.).normalize()),
        sea: Some(0.),
        fishable,
    }
}
const DEEP: &dyn Fn(Vec3) -> bool = &|_| true;
const SHALLOW: &dyn Fn(Vec3) -> bool = &|_| false;

#[test]
fn the_gauge_is_the_source_tock_and_slows_with_fishing_skill() {
    // m = 1 - |sin(((t + P/2) % P) / P * pi)|, squared: full at t = P/2, empty at t = 0 and P.
    assert!((gauge(50, 0) - 1.).abs() < 1e-6);
    assert!(gauge(0, 0) < 1e-6 && gauge(100, 0) < 1e-6);
    assert!((gauge(70, 2) - 1.).abs() < 1e-6, "skill 2: a 140-tock period");
}

#[test]
fn hold_charges_release_flings_and_only_deep_water_sends_the_cast() {
    let mut f = Fishing::default();
    assert!(f.step(frame(true, false, rod(), DEEP)).is_empty());
    assert_eq!(f.phase, Phase::Charging);
    for _ in 0..24 {
        f.step(frame(false, false, rod(), DEEP));
    }
    assert!((f.strength - gauge(25, 0)).abs() < 1e-6, "one tock per 20 ms, the press frame included: {}", f.strength);
    for _ in 0..25 {
        f.step(frame(false, false, rod(), DEEP));
    }
    assert!(f.strength > 0.99, "half a period: full strength ({})", f.strength);
    f.step(frame(false, true, rod(), DEEP));
    assert!(matches!(f.phase, Phase::Flying { .. }));
    let mut sent = Vec::new();
    for _ in 0..200 {
        sent.extend(f.step(frame(false, false, rod(), DEEP)));
    }
    let [Send::Cast { bobber, strength }] = sent.as_slice() else { panic!("{sent:?}") };
    assert_eq!(bobber.y, 0., "snapped to the surface it hit");
    assert!(bobber.z < -8. && bobber.x.abs() < 1e-4, "flung out along the aim, 1.6 m above the water and pitched down: {bobber}");
    assert_eq!(*strength, f.strength);
    assert_eq!(f.phase, Phase::Cast);
    // A shallow landing is no cast at all (retail minimumDepth).
    let mut f = Fishing::default();
    f.step(frame(true, false, rod(), SHALLOW));
    f.step(frame(false, true, rod(), SHALLOW));
    let mut sent = Vec::new();
    for _ in 0..200 {
        sent.extend(f.step(frame(false, false, rod(), SHALLOW)));
    }
    assert!(sent.is_empty());
    assert_eq!(f.phase, Phase::Idle);
}

#[test]
fn the_server_line_drives_the_bobber_and_every_press_and_release_on_it_is_sent() {
    let mut f = Fishing::default();
    f.step(frame(false, false, rod(), DEEP));
    f.phase = Phase::Cast;
    f.adopt(LINE);
    assert_eq!(f.phase, Phase::Line { bite: None });
    assert!(f.bobber_height(0.).abs() <= 0.06, "a gentle bob on the swell");
    f.adopt(BITE);
    for _ in 0..40 {
        f.step(frame(false, false, rod(), DEEP));
    }
    assert!(!f.bite_window_open(), "the window opens 1 s after the bite");
    for _ in 0..20 {
        f.step(frame(false, false, rod(), DEEP));
    }
    assert!(f.bite_window_open());
    assert_eq!(f.bobber_height(0.), -0.35, "tugged under in the catch window");
    assert_eq!(f.step(frame(true, false, rod(), DEEP)), vec![Send::Input(0)], "the press is the server's to judge");
    f.adopt(CHALLENGE);
    assert_eq!(f.bobber_height(0.), -0.5);
    assert_eq!(f.step(frame(false, true, rod(), DEEP)), vec![Send::Input(1)]);
    assert_eq!(f.step(frame(true, false, rod(), DEEP)), vec![Send::Input(0)]);
    f.adopt(IDLE);
    assert!(!f.bobber_visible(), "caught or reeled in: the bobber and line go");
    // ClearFisher: putting the rod away with a line out reels it in on the server.
    f.phase = Phase::Line { bite: None };
    assert_eq!(f.step(frame(false, false, None, DEEP)), vec![Send::Input(2)]);
    assert_eq!(f.phase, Phase::Idle);
    f.adopt(LINE);
    assert_eq!(f.phase, Phase::Idle, "no rod, no line");
}

// ---- A native host fishing through its own server -------------------------------------------

fn request(bridge: &CoreBridge, op: &str, fields: Value) -> Value {
    bridge.request(op, fields).unwrap()
}
struct Rig {
    app: App,
    window: Entity,
}
impl Rig {
    fn tick(&mut self, n: usize) -> Value {
        let mut state = Value::Null;
        for _ in 0..n {
            state = request(self.app.world().resource::<CoreBridge>(), "tick", json!({}))["state"].clone();
        }
        self.app.world_mut().resource_mut::<UiModel>().adopt_snapshot(&state);
        self.app.world_mut().resource_mut::<GameSnapshot>().0 = state.clone();
        // Production order: the snapshot's events become CoreEvents.
        for e in state["events"].as_array().unwrap() {
            self.app.world_mut().write_message(CoreEvent {
                sequence: e["seq"].as_u64().unwrap(),
                kind: e["kind"].as_str().unwrap().into(),
                data: e["data"].clone(),
            });
        }
        state
    }
    fn mouse(&mut self, state: ButtonState) {
        let window = self.window;
        self.app.world_mut().resource_mut::<FrameInput>().push(WindowEvent::MouseButtonInput(MouseButtonInput {
            button: MouseButton::Left,
            state,
            window,
        }));
        self.step(1);
    }
    fn step(&mut self, n: usize) {
        for _ in 0..n {
            self.app.update();
            self.app.world_mut().resource_mut::<FrameInput>().begin();
        }
    }
    fn phase(&self) -> Phase {
        self.app.world().resource::<Fishing>().phase
    }
}
fn count(state: &Value, ids: &[u64]) -> u64 {
    state["inventory"]["pages"].as_array().unwrap().iter()
        .flat_map(|p| p["items"].as_array().unwrap().iter())
        .filter(|i| ids.contains(&i["id"].as_u64().unwrap())).count() as u64
}

#[test]
fn a_native_cast_lands_a_line_on_the_server_and_only_the_server_grants_a_fish() {
    let bridge = CoreBridge::launch(Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
    request(&bridge, "init", json!({"contentRoot":WorldConfig::default().content_root,"name":"angler","spawn":[0,2,0],"allowCheats":true}));
    request(&bridge, "worldServices", json!({"hasWater":true,"seaLevel":0.}));
    request(&bridge, "action", json!({"name":"console","args":{"text":"give 503"}}));
    let items: Vec<crate::entities::ItemVisual> =
        serde_json::from_value(request(&bridge, "catalog", json!({}))["result"]["items"].clone()).unwrap();
    let mut catalog = ItemCatalog::default();
    catalog.items = items.into_iter().map(|i| (i.id, i)).collect();
    let mut app = App::new();
    app.add_plugins((MinimalPlugins, TransformPlugin, AssetPlugin::default()))
        .init_asset::<Mesh>()
        .init_asset::<StandardMaterial>()
        .insert_resource(bridge)
        .insert_resource(catalog)
        .insert_resource(GameSnapshot(Value::Null))
        .init_resource::<UiModel>()
        .insert_resource(UiState { screen: Screen::Playing, ..default() })
        .init_resource::<Settings>()
        .init_resource::<FrameInput>()
        .init_resource::<WorldSessionEpoch>()
        .insert_resource(WorldState { sea_level: Some(0.), ..default() })
        .add_message::<CoreEvent>()
        .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(std::time::Duration::from_millis(20)))
        .add_plugins(FishingPlugin);
    let window = app.world_mut().spawn((PrimaryWindow, Window { focused: true, ..default() },
        CursorOptions { visible: false, grab_mode: CursorGrabMode::Locked, ..default() })).id();
    app.world_mut().spawn((
        PlayerCamera,
        Transform::from_xyz(0., 3.6, 0.).looking_to(Vec3::new(0., 0.3, -1.), Vec3::Y),
    ));
    let mut rig = Rig { app, window };
    let state = rig.tick(10);
    let cell = state["inventory"]["pages"].as_array().unwrap().iter().find_map(|p| {
        p["items"].as_array().unwrap().iter().find(|i| i["id"] == ROD).map(|i| (p["index"].clone(), i["x"].clone(), i["y"].clone()))
    }).unwrap();
    request(rig.app.world().resource::<CoreBridge>(), "action", json!({"name":"holdItem","args":{"page":cell.0,"x":cell.1,"y":cell.2}}));
    rig.tick(10);
    rig.step(1);
    rig.mouse(ButtonState::Pressed);
    rig.step(25);
    rig.mouse(ButtonState::Released);
    for _ in 0..200 {
        if rig.phase() == Phase::Cast {
            break;
        }
        rig.step(1);
    }
    assert_eq!(rig.phase(), Phase::Cast, "the bobber flew out and splashed into open water");
    assert_eq!(rig.app.world().resource::<Fishing>().sent.last().map(|s| s.0.as_str()), Some("fishingCast"));
    rig.tick(4);
    rig.step(1);
    assert_eq!(rig.phase(), Phase::Line { bite: None }, "the server's line is in the water");
    let fish = [1349, 504, 505, 1351, 1959];
    // Reel in before any bite: an empty line, and no fish anywhere.
    rig.mouse(ButtonState::Pressed);
    let state = rig.tick(4);
    rig.step(1);
    assert_eq!(rig.phase(), Phase::Idle);
    assert_eq!(count(&state, &fish), 0, "nothing is granted locally or for an early press");
}

mod visual;
