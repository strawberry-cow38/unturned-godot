//! Source TryRestraintUse/TickArrest/AimedPlayer, plus a native listen host
//! cuffing a real joined peer through both authorities.
use super::*;
use crate::{
    avatar::remotes::FootMotion,
    ui::{Screen, UiState},
    world::WorldConfig,
};
use bevy::input::mouse::MouseButtonInput;
use std::path::Path;

const CUFFS: u16 = 1195;
const KEY: u16 = 1196;
const TIE: u16 = 1197;
const G: Gestures = Gestures { surrender: 6, arrest: 11 };
fn held(id: u16, kind: Kind) -> Option<Held> {
    Some(Held { cell: Cell { id, page: 2, x: 0, y: 0 }, kind, use_seconds: if kind == Kind::Key { 1.633 } else { 1.3 } })
}
fn sends(outcomes: &[Outcome]) -> Vec<&'static str> {
    outcomes.iter().filter_map(|o| if let Outcome::Send(n, _) = o { Some(*n) } else { None }).collect()
}

#[test]
fn aimed_player_is_the_best_flattened_dot_within_reach_of_the_camera() {
    let eye = Vec3::new(0., 1.6, 0.);
    let fwd = Vec3::new(0., -0.3, -1.).normalize();
    // Feet 3 m ahead: 3.4 m from the eye, inside 3.5; pitch is flattened away.
    assert_eq!(aimed_player(eye, fwd, AIM_RANGE, [(2, Vec3::new(0., 0., -3.))]), 2);
    // 3.5 m along the ground is ~3.85 m from the eye: out of reach.
    assert_eq!(aimed_player(eye, fwd, AIM_RANGE, [(2, Vec3::new(0., 0., -3.5))]), 0);
    // 60 degrees off centre is the limit (dot > 0.5), and behind never counts.
    assert_eq!(aimed_player(eye, fwd, AIM_RANGE, [(3, Vec3::new(2., 0., -1.))]), 0);
    assert_eq!(aimed_player(eye, fwd, AIM_RANGE, [(3, Vec3::new(0., 0., 2.))]), 0);
    // The best dot wins, not the nearest; a body right under the eye is skipped.
    let many = [(4, Vec3::new(0.8, 0., -1.)), (5, Vec3::new(0.2, 0., -3.)), (6, Vec3::new(0., 0., 0.))];
    assert_eq!(aimed_player(eye, fwd, AIM_RANGE, many), 5);
}

#[test]
fn cuffs_refuse_with_the_source_reasons_then_land_when_cuff_use_ends() {
    let mut r = Restraints::default();
    let cuffs = held(CUFFS, Kind::Restraint);
    assert_eq!(r.step(0., cuffs, true, G, || 0, |_| 0), vec![Outcome::Notice("Aim at someone surrendering.")]);
    assert_eq!(r.step(0., cuffs, true, G, || 2, |_| 0), vec![Outcome::Notice("They have to surrender first.")]);
    assert!(!r.pending());
    assert_eq!(r.step(0., cuffs, true, G, || 2, |_| 6), vec![Outcome::Use(CUFFS)]);
    assert!(r.step(0.02, cuffs, true, G, || 3, |_| 6).is_empty(), "one attempt at a time");
    let mut out = Vec::new();
    for _ in 0..62 {
        out.extend(r.step(0.02, cuffs, false, G, || 0, |_| 0));
    }
    assert!(out.is_empty(), "nothing before Cuff_Use's 1.3 s");
    for _ in 0..3 {
        out.extend(r.step(0.02, cuffs, false, G, || 0, |_| 0));
    }
    assert_eq!(
        out,
        vec![Outcome::Send("arrestPlayer", json!({"targetPlayerId":2}))],
        "once, at the end of the clip, to the target chosen at the click; nothing re-checked locally"
    );
    assert!(!r.pending());
}

#[test]
fn the_key_unlocks_at_the_click_and_only_a_cuffed_target() {
    let mut r = Restraints::default();
    let key = held(KEY, Kind::Key);
    assert_eq!(r.step(0., key, true, G, || 0, |_| 0), vec![Outcome::Notice("Aim at someone who's cuffed.")]);
    assert_eq!(r.step(0., key, true, G, || 2, |_| 6), vec![Outcome::Notice("They aren't cuffed.")]);
    assert_eq!(
        r.step(0., key, true, G, || 2, |_| 11),
        vec![Outcome::Use(KEY), Outcome::Send("unlockArrest", json!({"targetPlayerId":2}))]
    );
    assert!(!r.pending(), "the source key path keeps no busy timer");
}

#[test]
fn tick_arrest_sends_only_while_a_restraint_is_still_in_the_hand() {
    // Switched away: `_heldRestraintItem` is null when the swing ends.
    let mut r = Restraints::default();
    r.step(0., held(TIE, Kind::Restraint), true, G, || 2, |_| 6);
    let mut out = Vec::new();
    for _ in 0..70 {
        out.extend(r.step(0.02, None, false, G, || 0, |_| 0));
    }
    assert!(sends(&out).is_empty());
    // The timer is not reset by an equip: a key picked up mid-swing still
    // sends the arrest, which the server refuses (it is not a restraint).
    r.step(0., held(TIE, Kind::Restraint), true, G, || 2, |_| 6);
    let mut out = Vec::new();
    for _ in 0..70 {
        out.extend(r.step(0.02, held(KEY, Kind::Key), false, G, || 0, |_| 0));
    }
    assert_eq!(sends(&out), vec!["arrestPlayer"]);
}

#[test]
fn held_reads_the_catalog_hand_tool_and_ignores_other_items() {
    let item = |tool: Value| -> ItemVisual {
        serde_json::from_value(json!({"id":CUFFS,"name":"Handcuffs","type":"arrest_start","asset":null,"mesh":null,"texture":null,"icon":null,"handTool":tool})).unwrap()
    };
    let cell = Cell { id: CUFFS, page: 2, x: 1, y: 0 };
    assert_eq!(
        Held::of(cell, &item(json!({"kind":"restraint","useSeconds":1.3}))),
        Some(Held { cell, kind: Kind::Restraint, use_seconds: 1.3 })
    );
    assert_eq!(Held::of(cell, &item(json!({"kind":"restraintKey","useSeconds":1.633}))).map(|h| h.kind), Some(Kind::Key));
    assert_eq!(Held::of(cell, &item(json!({"kind":"carjack","useSeconds":2.}))), None);
    assert_eq!(Held::of(cell, &item(Value::Null)), None);
}

// ---- A native listen host and a real joined peer -----------------------------------------------

fn request(bridge: &CoreBridge, op: &str, fields: Value) -> Value {
    bridge.request(op, fields).unwrap()
}
fn console(bridge: &CoreBridge, text: &str) {
    request(bridge, "action", json!({"name":"console","args":{"text":text}}));
}
fn cell(state: &Value, id: u16) -> (Value, Value, Value) {
    state["inventory"]["pages"].as_array().unwrap().iter().find_map(|p| {
        p["items"].as_array().unwrap().iter().find(|i| i["id"] == id).map(|i| (p["index"].clone(), i["x"].clone(), i["y"].clone()))
    }).unwrap()
}
fn count(state: &Value, id: u16) -> u64 {
    state["inventory"]["pages"].as_array().unwrap().iter()
        .flat_map(|p| p["items"].as_array().unwrap().iter())
        .filter(|i| i["id"] == id).map(|i| i["amount"].as_u64().unwrap()).sum()
}
struct Pair {
    app: App,
    peer: CoreBridge,
    window: Entity,
    peer_id: u16,
}
impl Pair {
    fn pump(&mut self, n: usize) -> Value {
        let mut state = Value::Null;
        for _ in 0..n {
            request(&self.peer, "tick", json!({}));
            state = request(self.app.world().resource::<CoreBridge>(), "tick", json!({}))["state"].clone();
            std::thread::sleep(std::time::Duration::from_millis(1));
        }
        self.app.world_mut().resource_mut::<UiModel>().adopt_snapshot(&state);
        self.app.world_mut().resource_mut::<GameSnapshot>().0 = state.clone();
        state
    }
    fn peer_state(&self) -> Value {
        request(&self.peer, "tick", json!({}))["state"].clone()
    }
    fn click(&mut self) {
        let window = self.window;
        self.app.world_mut().resource_mut::<FrameInput>().push(WindowEvent::MouseButtonInput(MouseButtonInput {
            button: MouseButton::Left,
            state: ButtonState::Pressed,
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
    fn sees_peer(&mut self) -> u64 {
        let id = self.peer_id;
        self.pump(12)["players"].as_array().unwrap().iter().find(|p| p["playerId"] == id).unwrap()["gesture"].as_u64().unwrap()
    }
}
fn pair() -> Pair {
    let manifest = Path::new(env!("CARGO_MANIFEST_DIR"));
    let root = WorldConfig::default().content_root;
    let port = std::net::UdpSocket::bind("127.0.0.1:0").unwrap().local_addr().unwrap().port();
    let host = CoreBridge::launch(manifest).unwrap();
    request(&host, "init", json!({"contentRoot":root,"name":"captor","spawn":[0,0,0],"allowCheats":true,"listenPort":port}));
    let peer = CoreBridge::launch(manifest).unwrap();
    request(&peer, "init", json!({"contentRoot":root,"name":"prisoner","connectHost":"127.0.0.1","connectPort":port}));
    let visuals: Vec<ItemVisual> = serde_json::from_value(request(&host, "catalog", json!({}))["result"]["items"].clone()).unwrap();
    let gestures = serde_json::from_value(request(&host, "catalog", json!({}))["result"]["gestures"].clone()).unwrap();
    let mut catalog = ItemCatalog::default();
    catalog.items = visuals.into_iter().map(|i| (i.id, i)).collect();
    catalog.gestures = gestures;
    let mut app = App::new();
    app.add_plugins((MinimalPlugins, TransformPlugin))
        .insert_resource(host)
        .insert_resource(catalog)
        .insert_resource(GameSnapshot(Value::Null))
        .init_resource::<UiModel>()
        .insert_resource(UiState { screen: Screen::Playing, ..default() })
        .init_resource::<Settings>()
        .init_resource::<FrameInput>()
        .init_resource::<WorldSessionEpoch>()
        .add_message::<HandEvent>()
        .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(std::time::Duration::from_millis(20)))
        .add_plugins(RestraintsPlugin);
    let window = app.world_mut().spawn((PrimaryWindow, Window { focused: true, ..default() },
        CursorOptions { visible: false, grab_mode: CursorGrabMode::Locked, ..default() })).id();
    let mut pair = Pair { app, peer, window, peer_id: 0 };
    let state = pair.pump(40);
    let peer = pair.peer_state();
    pair.peer_id = peer["playerId"].as_u64().unwrap() as u16;
    assert_ne!(pair.peer_id, state["playerId"].as_u64().unwrap() as u16);
    // The peer stands 2 m in front of the host's camera; its puppet is there too.
    console(&pair.peer, "teleport 0 0 -2");
    pair.pump(20);
    pair.app.world_mut().spawn((
        Transform::from_xyz(0., 1.6, 0.).looking_to(Vec3::new(0., -0.2, -1.), Vec3::Y),
        PlayerCamera,
    ));
    let mut puppet = RemotePlayer::for_footstep_test(FootMotion::default());
    puppet.id = pair.peer_id;
    pair.app.world_mut().spawn((Transform::from_xyz(0., 0., -2.), puppet));
    pair
}
fn hold(bridge: &CoreBridge, state: &Value, id: u16) -> Value {
    let c = cell(state, id);
    request(bridge, "action", json!({"name":"holdItem","args":{"page":c.0,"x":c.1,"y":c.2}}))
}

#[test]
fn a_native_captor_cuffs_a_surrendering_peer_and_the_key_frees_them() {
    let mut pair = pair();
    console(pair.app.world().resource::<CoreBridge>(), "give 1195");
    console(pair.app.world().resource::<CoreBridge>(), "give 1196");
    let state = pair.pump(10);
    hold(pair.app.world().resource::<CoreBridge>(), &state, CUFFS);
    pair.pump(10);
    pair.click();
    assert_eq!(
        pair.app.world().resource::<UiModel>().notices.last().map(String::as_str),
        Some("They have to surrender first."),
        "the replicated gesture gives the source reason; nothing is sent"
    );
    let asked = request(&pair.peer, "action", json!({"name":"requestGesture","args":{"gesture":6}}));
    assert_eq!(pair.sees_peer(), 6, "the host's replica shows hands up ({asked})");
    pair.click();
    assert!(pair.app.world().resource::<Restraints>().pending(), "Cuff_Use plays; the cuffs land at its end");
    pair.step(60);
    assert_eq!(pair.sees_peer(), 6, "no optimistic cuffing before the clip ends");
    pair.step(10);
    assert_eq!(
        pair.app.world().resource::<Restraints>().sent.last().map(|s| s.0.as_str()),
        Some("arrestPlayer")
    );
    assert_eq!(pair.sees_peer(), 11, "ARREST_START accepted by the server and replicated");
    assert_eq!(pair.peer_state()["player"]["arrested"], true, "and on the prisoner's own entity");
    let state = pair.pump(4);
    assert_eq!(count(&state, CUFFS), 0, "the server spent the cuffs");

    // The prisoner's hands are bound: equipping anything is refused.
    console(&pair.peer, "give 13");
    pair.pump(10);
    let peer = pair.peer_state();
    let refused = hold(&pair.peer, &peer, 13);
    assert_eq!(refused["result"]["queued"], false);

    let state = pair.pump(4);
    hold(pair.app.world().resource::<CoreBridge>(), &state, KEY);
    pair.pump(10);
    pair.click();
    assert_eq!(pair.sees_peer(), 0, "the key frees at the click");
    assert_eq!(count(&pair.pump(4), CUFFS), 1, "and the cuffs come back");
}
