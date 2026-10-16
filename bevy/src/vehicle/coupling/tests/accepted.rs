//! Actual UDP intent -> accepted relationship -> native joint/paired publisher
//! -> rejected pair -> reliable native recovery. No trusted relationship writes.
use super::super::{physics, recovery, state, support};
use super::*;
use crate::{
    bridge::CoreBridge,
    player::GameSnapshot,
    ui::{Screen, UiIntent, UiModel, UiState},
    vehicle::{self, VehicleBody, Vehicles},
};
use serde_json::{Value, json};
use std::{net::UdpSocket, path::Path};

pub(super) struct Journey {
    host: CoreBridge,
    pub(super) cab: u32,
    pub(super) trailer: u32,
    sequence: u64,
}
impl Journey {
    pub(super) fn new(app: &mut App) -> Self {
        let host = CoreBridge::launch(Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
        let peer = CoreBridge::launch(Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
        let port = UdpSocket::bind("127.0.0.1:0")
            .unwrap()
            .local_addr()
            .unwrap()
            .port();
        host.request("init",json!({"contentRoot":WorldConfig::default().content_root,"listenPort":port,"allowCheats":true})).unwrap();
        let mut cab = vehicle::registration(
            data::named("semi").unwrap(),
            "hitch cab".into(),
            Vec3::ZERO,
            0.,
            0,
        );
        let mut trailer = vehicle::registration(
            data::named("trailer").unwrap(),
            "hitch trailer".into(),
            Vec3::Z * 9.9,
            0.,
            0,
        );
        // This authority fixture tests hitch ownership independently of source
        // physical door clearance, which has its own production Rapier tests.
        cab["hasDoors"] = json!(false);
        trailer["hasDoors"] = json!(false);
        let fleet = host
            .request("worldVehicles", json!({"vehicles":[cab,trailer]}))
            .unwrap();
        let cab = fleet["result"]["vehicles"][0]["netId"].as_u64().unwrap() as u32;
        let trailer = fleet["result"]["vehicles"][1]["netId"].as_u64().unwrap() as u32;
        peer.request("init",json!({"contentRoot":WorldConfig::default().content_root,"connectHost":"127.0.0.1","connectPort":port})).unwrap();
        app.insert_resource(peer);
        let mut j = Self {
            host,
            cab,
            trailer,
            sequence: 0,
        };
        j.pump(app, 30);
        j
    }
    fn action(&self, app: &App, name: &str, args: Value) {
        self.action_world(app.world(), name, args);
    }
    pub(super) fn action_world(&self, world: &World, name: &str, args: Value) {
        world
            .resource::<CoreBridge>()
            .request("action", json!({"name":name,"args":args}))
            .unwrap();
    }
    fn pump(&mut self, app: &mut App, n: usize) -> Value {
        self.pump_world(app.world_mut(), n)
    }
    pub(super) fn pump_world(&mut self, world: &mut World, n: usize) -> Value {
        let mut state = Value::Null;
        for _ in 0..n {
            self.host.request("tick", json!({})).unwrap();
            state = world
                .resource::<CoreBridge>()
                .request("tick", json!({}))
                .unwrap()["state"]
                .clone();
        }
        for event in state["events"].as_array().into_iter().flatten() {
            let seq = event["seq"].as_u64().unwrap();
            if seq <= self.sequence {
                continue;
            }
            self.sequence = seq;
            world
                .resource_mut::<Messages<crate::entities::CoreEvent>>()
                .write(crate::entities::CoreEvent {
                    sequence: seq,
                    kind: event["kind"].as_str().unwrap().into(),
                    data: event["data"].clone(),
                });
        }
        world.resource::<CoreBridge>().ack_events(self.sequence);
        world.resource_mut::<UiModel>().adopt_snapshot(&state);
        world.resource_mut::<GameSnapshot>().0 = state.clone();
        state
    }
}
fn row(state: &Value, id: u32) -> &Value {
    state["vehicles"]
        .as_array()
        .unwrap()
        .iter()
        .find(|v| v["netId"].as_u64() == Some(id.into()))
        .unwrap()
}
#[test]
fn actual_udp_link_drives_native_joint_pair_publication_recovery_and_exit_handoff() {
    let mut app = super::physics::app();
    app.init_resource::<UiModel>()
        .init_resource::<Vehicles>()
        .init_resource::<vehicle::Occupancy>()
        .add_message::<UiIntent>()
        .insert_resource(UiState {
            screen: Screen::Playing,
            networked: true,
            ..default()
        });
    let mut journey = Journey::new(&mut app);
    let cab = super::physics::body(&mut app, "semi", journey.cab, Vec3::ZERO);
    let trailer = super::physics::body(&mut app, "trailer", journey.trailer, Vec3::Z * 9.9);
    app.world_mut()
        .resource_mut::<Vehicles>()
        .entities
        .extend([(journey.cab, cab), (journey.trailer, trailer)]);
    app.world_mut()
        .get_mut::<VehicleBody>(cab)
        .unwrap()
        .driver_player_id = 0;
    app.world_mut()
        .get_mut::<VehicleBody>(trailer)
        .unwrap()
        .driver_player_id = 0;
    journey.action(&app, "console", json!({"text":"teleport 2 0 5.7"}));
    journey.pump(&mut app, 12);
    // This is the production hitch action generated by the original focus path.
    journey.action(
        &app,
        "vehicleHitch",
        json!({"trailerNetId":journey.trailer,"origin":[2,0.7,5.7],"direction":[-1,0,0]}),
    );
    assert!(
        app.world().get::<ImpulseJoint>(trailer).is_none(),
        "intent cannot create an optimistic joint"
    );
    let accepted = journey.pump(&mut app, 16);
    assert_eq!(
        row(&accepted, journey.cab)["coupledTrailerNetId"],
        journey.trailer
    );
    app.world_mut()
        .run_system_once(vehicle::sync_vehicles)
        .unwrap();
    recovery::apply_adoptions(app.world_mut());
    physics::sync(app.world_mut());
    assert!(
        app.world().get::<ImpulseJoint>(trailer).is_none(),
        "remote observer has no duplicate physics joint"
    );
    assert!(
        app.world()
            .get::<support::Supports>(trailer)
            .unwrap()
            .raised
    );
    assert!((app.world().get::<Transform>(trailer).unwrap().translation.z - 9.6).abs() < 0.01);
    journey.action(&app, "console", json!({"text":"teleport 0 0 1"}));
    journey.pump(&mut app, 12);
    journey.action(&app, "enterVehicle", json!({"netId":journey.cab,"seat":0}));
    let entered = journey.pump(&mut app, 16);
    assert_eq!(entered["player"]["vehicleNetId"], journey.cab);
    app.world_mut()
        .run_system_once(vehicle::sync_vehicles)
        .unwrap();
    recovery::apply_adoptions(app.world_mut());
    physics::sync(app.world_mut());
    app.world_mut()
        .run_system_once(vehicle::adopt_occupancy)
        .unwrap();
    assert!(app.world().get::<ImpulseJoint>(trailer).is_some());
    assert_eq!(
        state::driver(app.world().get::<VehicleBody>(trailer).unwrap()),
        entered["playerId"].as_u64().unwrap() as u32
    );
    // Native publisher must include the exact tilted trailer Euler fields.
    let rotation = Quat::from_euler(EulerRot::YXZ, 0.11, 0.03, -0.04);
    let cab_pose = Transform::from_xyz(0.2, 0., 0.);
    let trailer_pose = Transform::from_translation(
        cab_pose.transform_point(data::named("semi").unwrap().spec.fifth_wheel.into())
            - rotation * Vec3::from_array(data::named("trailer").unwrap().spec.kingpin),
    )
    .with_rotation(rotation);
    for (entity, pose) in [(cab, cab_pose), (trailer, trailer_pose)] {
        *app.world_mut().get_mut::<Transform>(entity).unwrap() = pose;
        *app.world_mut().get_mut::<Velocity>(entity).unwrap() = Velocity::linear(Vec3::X);
    }
    app.world_mut()
        .run_system_once(vehicle::publish_bodies)
        .unwrap();
    let moved = journey.pump(&mut app, 12);
    assert!((row(&moved, journey.cab)["position"][0].as_f64().unwrap() - 0.2).abs() < 0.01);
    assert!(
        (row(&moved, journey.trailer)["eulerDegrees"][1]
            .as_f64()
            .unwrap()
            - 0.11_f64.to_degrees())
        .abs()
            < 360. / 2048.
    );
    assert!(
        (row(&moved, journey.trailer)["position"][0]
            .as_f64()
            .unwrap()
            - trailer_pose.translation.x as f64)
            .abs()
            < 0.01
    );
    // Reject both through the real wire; the event consumer applies exact poses,
    // even though the current local bodies are arbitrarily far away.
    for entity in [cab, trailer] {
        app.world_mut()
            .get_mut::<Transform>(entity)
            .unwrap()
            .translation
            .x += 200.;
    }
    app.world_mut()
        .run_system_once(vehicle::publish_bodies)
        .unwrap();
    let rejected = journey.pump(&mut app, 12);
    assert!(
        rejected["events"]
            .as_array()
            .unwrap()
            .iter()
            .any(|e| e["kind"] == "vehicleRecov")
    );
    app.world_mut()
        .run_system_cached(recovery::receive)
        .unwrap();
    recovery::apply(app.world_mut());
    assert!((app.world().get::<Transform>(cab).unwrap().translation.x - 0.2).abs() < 0.01);
    assert!(
        app.world()
            .get::<Transform>(trailer)
            .unwrap()
            .translation
            .distance(trailer_pose.translation)
            < 0.02
    );
    assert!(app.world().get::<VehicleBody>(cab).unwrap().recov_counter > 0);
    app.world_mut()
        .run_system_once(vehicle::publish_bodies)
        .unwrap();
    journey.pump(&mut app, 12);
    journey.action(&app, "exitVehicle", json!({}));
    let exited = journey.pump(&mut app, 16);
    assert_eq!(row(&exited, journey.cab)["driverPlayerId"], 0);
    app.world_mut()
        .run_system_once(vehicle::sync_vehicles)
        .unwrap();
    recovery::apply_adoptions(app.world_mut());
    physics::sync(app.world_mut());
    assert!(
        app.world().get::<ImpulseJoint>(trailer).is_none(),
        "remote exit returns pair to host, keeping accepted relationship"
    );
    assert!(
        app.world()
            .get::<support::Supports>(trailer)
            .unwrap()
            .raised
    );
    // A new drive can produce recovery counter1 again. Event identity, not a
    // changed byte, must trigger the second physical correction.
    journey.action(&app, "console", json!({"text":"teleport 0 0 1"}));
    journey.pump(&mut app, 12);
    journey.action(&app, "enterVehicle", json!({"netId":journey.cab,"seat":0}));
    journey.pump(&mut app, 16);
    app.world_mut()
        .run_system_once(vehicle::sync_vehicles)
        .unwrap();
    recovery::apply_adoptions(app.world_mut());
    physics::sync(app.world_mut());
    app.world_mut()
        .run_system_once(vehicle::adopt_occupancy)
        .unwrap();
    let before = app.world().get::<Transform>(cab).unwrap().translation;
    let counter = app.world().get::<VehicleBody>(cab).unwrap().recov_counter;
    for entity in [cab, trailer] {
        app.world_mut()
            .get_mut::<Transform>(entity)
            .unwrap()
            .translation
            .x += 300.;
    }
    app.world_mut()
        .run_system_once(vehicle::publish_bodies)
        .unwrap();
    journey.pump(&mut app, 12);
    app.world_mut()
        .run_system_cached(recovery::receive)
        .unwrap();
    recovery::apply(app.world_mut());
    assert_eq!(
        app.world().get::<VehicleBody>(cab).unwrap().recov_counter,
        counter,
        "source per-drive counter repeats"
    );
    assert!(
        app.world()
            .get::<Transform>(cab)
            .unwrap()
            .translation
            .distance(before)
            < 0.02,
        "repeated counter still applies new reliable fact"
    );
    app.world_mut()
        .run_system_once(vehicle::publish_bodies)
        .unwrap();
    journey.pump(&mut app, 12);
    journey.action(&app, "exitVehicle", json!({}));
    journey.pump(&mut app, 12);
    journey.action(&app, "console", json!({"text":"teleport 2 0 5.7"}));
    journey.pump(&mut app, 12);
    journey.action(
        &app,
        "vehicleHitch",
        json!({"trailerNetId":journey.trailer,"origin":[2,0.7,5.7],"direction":[-1,0,0]}),
    );
    let detached = journey.pump(&mut app, 16);
    assert_eq!(row(&detached, journey.cab)["coupledTrailerNetId"], 0);
    app.world_mut()
        .run_system_once(vehicle::sync_vehicles)
        .unwrap();
    recovery::apply_adoptions(app.world_mut());
    physics::sync(app.world_mut());
    assert!(
        !app.world()
            .get::<support::Supports>(trailer)
            .unwrap()
            .raised,
        "accepted detach restores landing shape"
    );
}
