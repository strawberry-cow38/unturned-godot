use super::*;
use crate::{
    bridge::CoreBridge,
    entities::{CoreEvent, InteractionFocus},
    settings::{Binding, GameAction, Settings},
    ui::{Screen, UiIntent, UiState},
};
use serde_json::{Value, json};
use std::{net::UdpSocket, path::Path};

pub(super) struct Journey {
    pub host: CoreBridge,
    pub peer: CoreBridge,
    pub id: u32,
    pub sequence: u64,
}
impl Journey {
    pub fn new() -> Self {
        let host = CoreBridge::launch(Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
        let peer = CoreBridge::launch(Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
        let port = UdpSocket::bind("127.0.0.1:0")
            .unwrap()
            .local_addr()
            .unwrap()
            .port();
        let config = WorldConfig::default();
        host.request(
            "init",
            json!({"contentRoot":config.content_root,"listenPort":port,"spawn":[0,0,0]}),
        )
        .unwrap();
        let def = data::named("bus").unwrap();
        let id=host.request("worldVehicles",json!({"vehicles":[crate::vehicle::registration(def,"accepted bus".into(),Vec3::ZERO,0.,0)]})).unwrap()["result"]["vehicles"][0]["netId"].as_u64().unwrap()as u32;
        peer.request(
            "init",
            json!({"contentRoot":config.content_root,"connectHost":"127.0.0.1","connectPort":port}),
        )
        .unwrap();
        let j = Self {
            host,
            peer,
            id,
            sequence: 0,
        };
        j.pump(30);
        j.peer
            .request(
                "action",
                json!({"name":"enterVehicle","args":{"netId":id,"seat":0}}),
            )
            .unwrap();
        j.pump(180);
        j
    }
    pub fn pump(&self, n: u32) -> Value {
        let mut s = Value::Null;
        for _ in 0..n {
            self.host.request("tick", json!({})).unwrap();
            s = self.peer.request("tick", json!({})).unwrap()["state"].clone();
        }
        s
    }
    pub fn forward(&mut self, app: &mut App, state: &Value) {
        for e in state["events"].as_array().into_iter().flatten() {
            let sequence = e["seq"].as_u64().unwrap();
            if sequence <= self.sequence {
                continue;
            }
            self.sequence = sequence;
            app.world_mut()
                .resource_mut::<Messages<CoreEvent>>()
                .write(CoreEvent {
                    sequence,
                    kind: e["kind"].as_str().unwrap().into(),
                    data: e["data"].clone(),
                });
        }
        self.peer.ack_events(self.sequence);
    }
    pub fn send(&self, app: &mut App) -> usize {
        let intents = app
            .world_mut()
            .resource_mut::<Messages<UiIntent>>()
            .drain()
            .collect::<Vec<_>>();
        let n = intents.len();
        for intent in intents {
            if let UiIntent::Action { name, args } = intent {
                self.peer
                    .request("action", json!({"name":name,"args":args}))
                    .unwrap();
            } else {
                panic!("Unexpected non-door intent");
            }
        }
        n
    }
}
#[test]
fn actual_udp_driver_binding_waits_for_phase_and_sound_and_passenger_or_modal_does_not_toggle() {
    let mut journey = Journey::new();
    let mut app = app();
    let config = WorldConfig::default();
    app.insert_resource(config)
        .insert_resource(UiState {
            screen: Screen::Playing,
            networked: true,
            ..default()
        })
        .init_resource::<Settings>()
        .init_resource::<ButtonInput<KeyCode>>()
        .init_resource::<ButtonInput<MouseButton>>()
        .init_asset::<AudioSource>()
        .add_message::<UiIntent>()
        .add_message::<CoreEvent>();
    let body = bus(&mut app, Transform::IDENTITY);
    app.world_mut().get_mut::<VehicleBody>(body).unwrap().net_id = journey.id;
    let state = journey.pump(1);
    journey.sequence = state["eventSequence"].as_u64().unwrap_or_else(|| {
        state["events"]
            .as_array()
            .unwrap()
            .iter()
            .map(|e| e["seq"].as_u64().unwrap())
            .max()
            .unwrap_or(0)
    });
    app.insert_resource(GameSnapshot(state));
    app.update();
    assert_eq!(app.world().get::<Built>(body).unwrap().phase, 0.);
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .press(KeyCode::ControlLeft);
    app.world_mut().run_system_once(input::input).unwrap();
    assert_eq!(journey.send(&mut app), 1);
    app.world_mut().run_system_cached(audio::events).unwrap();
    assert_eq!(
        app.world().get::<Built>(body).unwrap().phase,
        0.,
        "request cannot animate an optimistic door"
    );
    assert_eq!(
        app.world_mut()
            .query::<&AudioPlayer>()
            .iter(app.world())
            .count(),
        0
    );
    let state = journey.pump(16);
    journey.forward(&mut app, &state);
    app.insert_resource(GameSnapshot(state));
    app.update();
    app.world_mut().run_system_cached(audio::events).unwrap();
    let built = app.world().get::<Built>(body).unwrap();
    assert!(built.open && built.phase > 0.2 && built.phase < 0.6);
    let a = built.a;
    assert!(
        app.world()
            .get::<Transform>(a)
            .unwrap()
            .rotation
            .angle_between(Quat::IDENTITY)
            > 0.1
    );
    let mut voices = app.world_mut().query::<(
        &AudioPlayer,
        &PlaybackSettings,
        &crate::vehicle::audio::VehicleSound,
        &Transform,
        &ChildOf,
    )>();
    let (clip, playback, sound, pose, parent) = voices.single(app.world()).unwrap();
    assert_eq!(parent.parent(), body);
    assert_eq!(sound.max_distance, 40.);
    assert!((sound.gain - 10_f32.powf(-2. / 20.)).abs() < 1e-6);
    assert_eq!(playback.speed, 1.);
    assert!(
        pose.translation
            .abs_diff_eq(Vec3::new(1.355, 0.87, -2.75), 1e-4)
    );
    assert!(
        app.world()
            .resource::<AssetServer>()
            .get_path(clip.0.id())
            .unwrap()
            .path()
            .ends_with("busdoor_open.wav")
    );
    app.world_mut().run_system_cached(audio::events).unwrap();
    assert_eq!(
        app.world_mut()
            .query::<&AudioPlayer>()
            .iter(app.world())
            .count(),
        1
    );
    // Rebinding is honored; moving edges are suppressed before transmission.
    app.world_mut()
        .resource_mut::<Settings>()
        .bindings
        .insert(GameAction::VehicleDoor, Binding::Key("KeyG".into()));
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .press(KeyCode::KeyG);
    app.world_mut().run_system_once(input::input).unwrap();
    assert_eq!(journey.send(&mut app), 0);
    let state = journey.pump(45);
    app.insert_resource(GameSnapshot(state));
    app.update();
    app.world_mut().resource_mut::<UiState>().screen = Screen::Inventory;
    app.world_mut().run_system_once(input::input).unwrap();
    assert_eq!(journey.send(&mut app), 0);
    app.world_mut().resource_mut::<UiState>().screen = Screen::Playing;
    app.world_mut().resource_mut::<UiState>().map_closing = true;
    assert!(app.world().resource::<UiState>().captures_input());
    app.world_mut().run_system_once(input::input).unwrap();
    assert_eq!(
        journey.send(&mut app),
        1,
        "discrete rebound key closes settled door during outgoing Map swoop"
    );
    journey.pump(45);
    journey
        .peer
        .request(
            "action",
            json!({"name":"enterVehicle","args":{"netId":journey.id,"seat":1}}),
        )
        .unwrap();
    let state = journey.pump(45);
    app.insert_resource(GameSnapshot(state));
    app.update();
    app.world_mut().run_system_once(input::input).unwrap();
    assert_eq!(journey.send(&mut app), 0, "passenger never owns the key");
    app.world_mut().despawn(body);
    assert_eq!(
        app.world_mut()
            .query::<&AudioPlayer>()
            .iter(app.world())
            .count(),
        0,
        "parent-owned voice retires with the vehicle"
    );
}
#[test]
fn source_focus_offers_busy_prompt_without_action_and_same_frame_edges_keep_one_original_voice() {
    let mut app = app();
    app.init_resource::<Settings>()
        .insert_resource(WorldConfig::default())
        .init_asset::<AudioSource>()
        .add_message::<CoreEvent>();
    let body = bus(&mut app, Transform::IDENTITY);
    app.update();
    let state =
        json!({"vehicles":[{"netId":1,"hasBiFoldDoor":true,"doorOpen":true,"doorFold":0.5}]});
    let mut focus = InteractionFocus::default();
    input::offer(&mut focus, &state, 1, 1., Vec3::ZERO, -Vec3::X);
    assert!(focus.selected("vehicleDoorMoving", 1));
    assert!(!focus.selected("vehicleDoor", 1));
    let state =
        json!({"vehicles":[{"netId":1,"hasBiFoldDoor":true,"doorOpen":true,"doorFold":1.}]});
    let mut focus = InteractionFocus::default();
    input::offer(&mut focus, &state, 1, 1., Vec3::ZERO, -Vec3::X);
    assert!(focus.selected("vehicleDoor", 1));
    for sequence in 1..=2 {
        app.world_mut()
            .resource_mut::<Messages<CoreEvent>>()
            .write(CoreEvent {
                sequence,
                kind: "vehicleDoor".into(),
                data: json!({"netId":1,"open":sequence==1}),
            });
    }
    app.world_mut().run_system_cached(audio::events).unwrap();
    assert_eq!(
        app.world_mut()
            .query::<&AudioPlayer>()
            .iter(app.world())
            .count(),
        1
    );
    app.world_mut()
        .resource_mut::<Messages<CoreEvent>>()
        .write(CoreEvent {
            sequence: 3,
            kind: "vehicleDoor".into(),
            data: json!({"netId":1,"open":true}),
        });
    app.world_mut().run_system_cached(audio::events).unwrap();
    assert_eq!(
        app.world_mut()
            .query::<&AudioPlayer>()
            .iter(app.world())
            .count(),
        1
    );
    app.world_mut().despawn(body);
}
