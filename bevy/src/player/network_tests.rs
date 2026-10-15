//! Real UDP plus the native lifecycle systems. A join must adopt the server's
//! owner before sending positions, and closing a listen session releases its port.
use super::*;
use crate::{network::Target, world::WorldConfig};
fn bridge() -> CoreBridge {
    CoreBridge::launch(std::path::Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap()
}
fn free_port() -> u16 {
    std::net::UdpSocket::bind("127.0.0.1:0")
        .unwrap()
        .local_addr()
        .unwrap()
        .port()
}
fn app(target: Target, play: bool) -> App {
    let mut app = App::new();
    let spawn = Vec3::new(-99., 40., 55.);
    app.add_plugins(MinimalPlugins)
        .insert_resource(bridge())
        .insert_resource(LaunchOptions(crate::cli::Options {
            network: target,
            play,
            ..default()
        }))
        .insert_resource(WorldState {
            ready: true,
            spawn,
            ..default()
        })
        .init_resource::<WorldConfig>()
        .init_resource::<crate::world::origin::WorldOrigin>()
        .init_resource::<Session>()
        .init_resource::<GameSnapshot>()
        .init_resource::<PlayerInput>()
        .init_resource::<UiState>()
        .init_resource::<UiModel>()
        .init_resource::<WorldSessionEpoch>()
        .init_resource::<crate::use_items::HandUse>()
        .init_resource::<crate::weapons::Trigger>()
        .init_resource::<crate::entities::ItemCatalog>()
        .add_message::<UiIntent>()
        .add_message::<crate::use_items::HandEvent>()
        .add_message::<AppExit>()
        .add_systems(
            Update,
            (initialize_session, ui_intents, publish_body).chain(),
        );
    app.world_mut().spawn((
        Transform::from_translation(spawn),
        PlayerBody {
            velocity: Vec3::ZERO,
            grounded: false,
            height: 2.,
            stance: "stand".into(),
            eye_height: 1.75,
            locomotion: default(),
        },
        capsule(2.),
        KinematicCharacterController::default(),
    ));
    app
}
#[test]
fn menu_defers_authority_and_explicit_leave_releases_listen_port_and_presentation() {
    let port = free_port();
    let mut app = app(
        Target::Listen {
            port,
            name: "lifecycle".into(),
            max_players: 4,
        },
        false,
    );
    app.update();
    assert!(!app.world().resource::<Session>().initialized);
    assert!(std::net::UdpSocket::bind(("127.0.0.1", port)).is_ok());
    app.world_mut().resource_mut::<LaunchOptions>().0.play = true;
    app.update();
    assert!(app.world().resource::<Session>().initialized);
    assert!(std::net::UdpSocket::bind(("127.0.0.1", port)).is_err());
    let before = app.world().resource::<GameSnapshot>().0["tick"]
        .as_u64()
        .unwrap();
    app.world_mut().resource_mut::<UiState>().screen = Screen::Paused;
    for _ in 0..4 {
        app.update();
    }
    assert!(
        app.world().resource::<GameSnapshot>().0["tick"]
            .as_u64()
            .unwrap()
            > before,
        "listen server continues while owner opens pause"
    );
    app.world_mut().write_message(UiIntent::Disconnect);
    app.update();
    assert!(!app.world().resource::<Session>().initialized);
    assert!(!app.world().resource::<UiModel>().connected);
    assert!(app.world().resource::<GameSnapshot>().0.is_null());
    assert!(!app.world().resource::<UiState>().networked);
    assert!(
        std::net::UdpSocket::bind(("127.0.0.1", port)).is_ok(),
        "actual UDP listener disposed"
    );
    app.update();
    assert!(
        !app.world().resource::<Session>().initialized,
        "menu does not silently recreate host"
    );
}
#[test]
fn remote_join_adopts_owner_before_any_position_publication_and_pause_keeps_ticks() {
    let port = free_port();
    let host = bridge();
    host.request("init",json!({"contentRoot":WorldConfig::default().content_root,"listenPort":port,"spawn":[10,3,-8],"allowCheats":true})).unwrap();
    let mut client = app(
        Target::Client {
            host: "127.0.0.1".into(),
            port,
        },
        true,
    );
    client.update();
    assert!(!client.world().resource::<UiModel>().connected);
    assert_eq!(
        client.world().resource::<UiState>().screen,
        Screen::Connecting
    );
    for _ in 0..200 {
        host.request("tick", json!({})).unwrap();
        std::thread::sleep(std::time::Duration::from_millis(2));
        client.update();
        if client.world().resource::<UiModel>().connected {
            break;
        }
    }
    assert!(
        client.world().resource::<UiModel>().connected,
        "real owner replica must arrive"
    );
    let world = client.world_mut();
    let pose = world
        .query_filtered::<&Transform, With<PlayerBody>>()
        .single(world)
        .unwrap();
    assert!(
        pose.translation.distance(Vec3::new(10., 3., -8.)) < 0.1,
        "client map hint never overwrites accepted spawn: {:?}",
        pose.translation
    );
    assert!(!world.resource::<UiModel>().network.owns_world);
    assert_eq!(world.resource::<UiState>().screen, Screen::Playing);
    let before = world.resource::<GameSnapshot>().0["tick"].as_u64().unwrap();
    world.resource_mut::<UiState>().screen = Screen::Paused;
    for _ in 0..5 {
        host.request("tick", json!({})).unwrap();
        client.update();
    }
    assert!(
        client.world().resource::<GameSnapshot>().0["tick"]
            .as_u64()
            .unwrap()
            > before
    );
    client.world_mut().write_message(UiIntent::Disconnect);
    client.update();
    assert!(client.world().resource::<GameSnapshot>().0.is_null());
    // A fresh local session after a remote leave gets genuine authority again.
    client
        .world_mut()
        .write_message(UiIntent::Play { map: "PEI".into() });
    client.update();
    client.update();
    assert!(client.world().resource::<UiModel>().network.owns_world);
    assert!(client.world().resource::<UiModel>().connected);
}
