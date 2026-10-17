//! A joined native client (separate host and client bridges over real UDP)
//! stands beside a host vehicle, looks away, presses F and is seated by the
//! server's CanEnter + VehicleEntered fact: no local seat before the echo.
use super::*;
use crate::{
    bridge::CoreBridge,
    entities::{ApplyInteractions, apply_interaction, reset_focus},
    settings::Settings,
    ui::{Screen, UiModel},
    vehicle::data,
    world::WorldConfig,
};
use serde_json::Value;

fn bridge() -> CoreBridge {
    CoreBridge::launch(std::path::Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap()
}
fn vec3(v: &Value) -> Vec3 {
    Vec3::new(v[0].as_f64().unwrap() as f32, v[1].as_f64().unwrap() as f32, v[2].as_f64().unwrap() as f32)
}
fn pump(host: &CoreBridge, app: &mut App, n: usize) -> Value {
    for _ in 0..n {
        host.request("tick", json!({})).unwrap();
        let state = app.world().resource::<CoreBridge>().request("tick", json!({})).unwrap()["state"].clone();
        app.world_mut().resource_mut::<GameSnapshot>().0 = state;
        std::thread::sleep(std::time::Duration::from_millis(1));
    }
    app.world().resource::<GameSnapshot>().0.clone()
}

#[test]
fn actual_udp_joined_client_presses_f_beside_a_host_vehicle_and_is_seated() {
    let config = WorldConfig::default();
    let port = std::net::UdpSocket::bind("127.0.0.1:0").unwrap().local_addr().unwrap().port();
    let host = bridge();
    host.request(
        "init",
        json!({"contentRoot":config.content_root,"listenPort":port,"seed":0,"spawn":[0,0,0]}),
    )
    .unwrap();
    for _ in 0..10 {
        host.request("tick", json!({})).unwrap();
    }
    let peer = bridge();
    peer.request(
        "init",
        json!({"contentRoot":config.content_root,"connectHost":"127.0.0.1","connectPort":port}),
    )
    .unwrap();
    let mut app = App::new();
    app.add_plugins(MinimalPlugins)
        .insert_resource(peer)
        .init_resource::<Settings>()
        .init_resource::<ButtonInput<KeyCode>>()
        .init_resource::<ButtonInput<MouseButton>>()
        .init_resource::<UiModel>()
        .init_resource::<InteractionFocus>()
        .insert_resource(GameSnapshot(Value::Null))
        .insert_resource(UiState {
            screen: Screen::Playing,
            ..default()
        })
        .configure_sets(Update, ApplyInteractions.after(CollectInteractions))
        .add_systems(Update, reset_focus.before(CollectInteractions))
        .add_systems(Update, apply_interaction.in_set(ApplyInteractions))
        .add_plugins(ProximityEnterPlugin);
    let mut state = pump(&host, &mut app, 40);
    for _ in 0..20 {
        if state["player"]["alive"] == true && state["network"]["ownsWorld"] == false {
            break;
        }
        state = pump(&host, &mut app, 10);
    }
    assert_eq!(state["network"]["ownsWorld"], false, "joined client: {}", state["network"]);
    let feet = vec3(&state["player"]["position"]);
    // A host Roadster 3 m beside the joined player's accepted position.
    let id = host
        .request(
            "worldVehicles",
            json!({"vehicles":[{"typeId":data::named("roadster").unwrap().type_id,
                "position":(feet + Vec3::X * 3.).to_array(),"health":1000,"fuel":100,"battery":10000,"seatCount":2}]}),
        )
        .unwrap()["result"]["vehicles"][0]["netId"]
        .as_u64()
        .unwrap() as u32;
    state = pump(&host, &mut app, 30);
    let row = state["vehicles"].as_array().unwrap().iter().find(|v| v["netId"] == id).expect("replicated").clone();
    // The replica's body and the player's body, where the production views put them.
    let body = crate::vehicle::tests::body(&mut app, "roadster", vec3(&row["position"]));
    app.world_mut().get_mut::<VehicleBody>(body).unwrap().net_id = id;
    app.world_mut().spawn((PlayerBody::for_motion_test(Vec3::ZERO, true, "stand"), Transform::from_translation(feet)));
    app.update();
    let focus = app.world().resource::<InteractionFocus>();
    assert_eq!(
        focus.dispatch().map(|(a, v, _)| (a.to_owned(), v.clone())),
        Some(("enterVehicle".into(), json!({"netId":id,"seat":255})))
    );
    assert!(app.world().resource::<UiModel>().interaction.is_empty(), "the source shows no prompt");
    app.world_mut().resource_mut::<ButtonInput<KeyCode>>().press(KeyCode::KeyF);
    app.update();
    app.world_mut().resource_mut::<ButtonInput<KeyCode>>().clear();
    assert_eq!(
        app.world().resource::<GameSnapshot>().0["player"]["vehicleNetId"].as_u64().unwrap_or(0),
        0,
        "the client never seats itself"
    );
    state = pump(&host, &mut app, 30);
    assert_eq!(state["player"]["vehicleNetId"], id, "seated by the host's VehicleEntered fact");
    let row = state["vehicles"].as_array().unwrap().iter().find(|v| v["netId"] == id).unwrap();
    assert_eq!(row["driverPlayerId"], state["playerId"], "AnySeat walks the seats driver first");
}

