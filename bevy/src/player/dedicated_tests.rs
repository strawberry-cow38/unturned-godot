//! The native dedicated driver against the actual bridge over real UDP: the
//! host has no local body, opens its session only after the map services
//! register, admits a real native client, and writes the final save and
//! releases its port on AppExit (the SIGTERM path).
use super::network_tests::{app as client_app, bridge, free_port};
use super::*;
use crate::{entities::ServiceManifest, network::Target, world::WorldConfig};

fn dedicated_app(port: u16, map: &str) -> App {
    let mut app = App::new();
    app.add_plugins(MinimalPlugins)
        .insert_resource(bridge())
        .insert_resource(LaunchOptions(crate::cli::Options {
            network: Target::Dedicated {
                port,
                name: Some("Native dedicated".into()),
                max_players: Some(4),
            },
            play: true,
            map: map.into(),
            unturned: None,
            ..default()
        }))
        .insert_resource(WorldState {
            ready: true,
            spawn: Vec3::new(4., 2., -6.),
            ..default()
        })
        .insert_resource(WorldConfig {
            map_name: map.into(),
            ..default()
        })
        .init_resource::<crate::world::origin::WorldOrigin>()
        .init_resource::<Session>()
        .init_resource::<GameSnapshot>()
        .init_resource::<PlayerInput>()
        .init_resource::<UiState>()
        .init_resource::<UiModel>()
        .init_resource::<WorldSessionEpoch>()
        .init_resource::<ServiceManifest>()
        .add_message::<AppExit>()
        .add_systems(Update, initialize_session.in_set(PlayerUpdate))
        .add_plugins(crate::dedicated::DedicatedPlugin);
    app
}

fn state(app: &App) -> &Value {
    &app.world().resource::<GameSnapshot>().0
}

#[test]
fn dedicated_host_has_no_body_opens_after_services_admits_a_client_and_saves_on_exit() {
    let port = free_port();
    let map = format!("DedicatedNative{}", std::process::id());
    let save = persistence::save_path(&map);
    let _ = std::fs::remove_file(&save);
    let mut host = dedicated_app(port, &map);
    host.update();
    {
        let model = host.world().resource::<UiModel>();
        assert!(model.connected, "the authority session exists");
        assert_eq!(model.network.mode, "dedicated");
        assert!(model.network.networked());
    }
    assert_eq!(state(&host)["playerId"], json!(0), "no local player");
    assert!(state(&host)["player"].is_null());
    assert_eq!(state(&host)["players"], json!([]));
    assert_eq!(
        host.world_mut()
            .query::<&PlayerBody>()
            .iter(host.world())
            .count(),
        0
    );
    assert!(std::net::UdpSocket::bind(("127.0.0.1", port)).is_err());

    // Services for this map have not registered: the save is not restored
    // and nobody is admitted yet, however long the host runs.
    for _ in 0..60 {
        host.update();
        host.world_mut().run_schedule(FixedUpdate);
    }
    assert_eq!(state(&host)["network"]["state"], json!("Loading"));
    host.world_mut().resource_mut::<ServiceManifest>().map = map.clone();
    for _ in 0..40 {
        host.update();
        if state(&host)["network"]["state"] == json!("Serving") {
            break;
        }
    }
    assert_eq!(state(&host)["network"]["state"], json!("Serving"));
    assert!(
        !host
            .world()
            .resource::<bevy::ecs::message::Messages<crate::vehicle::WorldRestored>>()
            .is_empty(),
        "the admission restore re-poses host vehicle bodies like a Load"
    );
    let before = state(&host)["tick"].as_u64().unwrap();

    let mut client = client_app(
        Target::Client {
            host: "127.0.0.1".into(),
            port,
        },
        true,
    );
    for _ in 0..300 {
        host.world_mut().run_schedule(FixedUpdate);
        std::thread::sleep(std::time::Duration::from_millis(2));
        client.update();
        if client.world().resource::<UiModel>().connected {
            break;
        }
    }
    assert!(client.world().resource::<UiModel>().connected);
    let owner = client.world().resource::<GameSnapshot>().0["playerId"].clone();
    assert_ne!(owner, json!(0));
    host.world_mut().run_schedule(FixedUpdate);
    let players = state(&host)["players"].as_array().unwrap().clone();
    assert_eq!(players.len(), 1, "only the remote peer is a player");
    assert_eq!(players[0]["playerId"], owner);
    assert!(state(&host)["tick"].as_u64().unwrap() > before);
    assert_eq!(state(&host)["network"]["peers"], json!(1));

    host.world_mut().write_message(AppExit::Success);
    host.update();
    assert!(save.is_file(), "AppExit writes the final save");
    assert!(
        std::net::UdpSocket::bind(("127.0.0.1", port)).is_ok(),
        "the stopped host released UDP {port}"
    );
    let _ = std::fs::remove_file(&save);
}

#[test]
fn dedicated_host_that_cannot_bind_exits_with_an_error_instead_of_idling() {
    let port = free_port();
    let _occupied = std::net::UdpSocket::bind(("0.0.0.0", port)).unwrap();
    let map = format!("DedicatedOccupied{}", std::process::id());
    let mut host = dedicated_app(port, &map);
    for _ in 0..3 {
        host.update();
    }
    assert!(!host.world().resource::<UiModel>().connected);
    assert!(
        host.should_exit().is_some_and(|exit| exit.is_error()),
        "systemd must see a failed start"
    );
    assert!(!persistence::save_path(&map).exists(), "nothing was served, nothing saved");
}
