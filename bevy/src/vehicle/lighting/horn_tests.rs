//! Native input → real UDP authority → original horn audio, with no local echo.
use super::*;
use crate::ui::Screen;
use crate::vehicle::{self, Occupancy, OccupiedSeat};
use bevy::ecs::system::RunSystemOnce;
use bevy_rapier3d::prelude::*;
use std::{net::UdpSocket, path::Path};

#[test]
fn actual_driver_press_waits_for_accepted_udp_horn_and_uses_source_audio_once() {
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
        json!({"contentRoot":config.content_root,"listenPort":port,"seed":0,"spawn":[0,0,0]}),
    )
    .unwrap();
    let id=host.request("worldVehicles",json!({"vehicles":[{"typeId":data::named("police").unwrap().type_id,"position":[0,0,0],"health":6000,"fuel":60000,"battery":10000,"seatCount":4}]})).unwrap()["result"]["vehicles"][0]["netId"].as_u64().unwrap()as u32;
    peer.request(
        "init",
        json!({"contentRoot":config.content_root,"connectHost":"127.0.0.1","connectPort":port}),
    )
    .unwrap();
    let mut state = serde_json::Value::Null;
    for _ in 0..30 {
        host.request("tick", json!({})).unwrap();
        state = peer.request("tick", json!({})).unwrap()["state"].clone();
    }
    peer.request(
        "action",
        json!({"name":"enterVehicle","args":{"netId":id,"seat":0}}),
    )
    .unwrap();
    for _ in 0..20 {
        host.request("tick", json!({})).unwrap();
        state = peer.request("tick", json!({})).unwrap()["state"].clone();
    }
    assert_eq!(state["player"]["vehicleSeat"], 0);
    assert_eq!(state["network"]["ownsWorld"], false);
    let mut model = UiModel::default();
    model.connected = true;
    model.alive = true;
    let mut app = App::new();
    app.add_plugins((
        MinimalPlugins,
        AssetPlugin::default(),
        TransformPlugin,
        RapierPhysicsPlugin::<NoUserData>::default(),
    ))
    .insert_resource(peer)
    .insert_resource(config)
    .insert_resource(model)
    .insert_resource(UiState {
        screen: Screen::Playing,
        networked: true,
        ..default()
    })
    .init_resource::<Settings>()
    .init_resource::<ButtonInput<KeyCode>>()
    .init_resource::<ButtonInput<MouseButton>>()
    .init_resource::<crate::content::ContentCache>()
    .init_resource::<bevy::input::mouse::AccumulatedMouseMotion>()
    .init_resource::<vehicle::turrets::Operator>()
    .init_asset::<AudioSource>()
    .add_message::<CoreEvent>();
    let car = vehicle::tests::body(&mut app, "police", Vec3::ZERO);
    {
        let mut body = app.world_mut().get_mut::<VehicleBody>(car).unwrap();
        body.net_id = id;
        body.flags = 0;
    }
    app.insert_resource(Occupancy {
        seat: Some(OccupiedSeat {
            vehicle: car,
            net_id: id,
            seat: 0,
            eye_pose: Transform::IDENTITY,
            body_pose: Transform::IDENTITY,
            velocity: Vec3::ZERO,
            driver: true,
            aircraft: false,
        }),
    });
    // Both native edges arrive before authority progresses. Only the first is
    // accepted; a duplicate raw request cannot bypass the source cooldown.
    app.world_mut()
        .resource_mut::<ButtonInput<MouseButton>>()
        .press(MouseButton::Left);
    app.world_mut()
        .run_system_once(vehicle::read_controls)
        .unwrap();
    app.world_mut()
        .run_system_once(vehicle::read_controls)
        .unwrap();
    app.world_mut().run_system_cached(events).unwrap();
    assert_eq!(
        app.world_mut()
            .query::<&AudioPlayer>()
            .iter(app.world())
            .count(),
        0,
        "input cannot play an optimistic horn"
    );
    for _ in 0..10 {
        host.request("tick", json!({})).unwrap();
        state = app
            .world()
            .resource::<CoreBridge>()
            .request("tick", json!({}))
            .unwrap()["state"]
            .clone();
    }
    let horns: Vec<_> = state["events"]
        .as_array()
        .unwrap()
        .iter()
        .filter(|e| e["kind"] == "vehicleHorn")
        .collect();
    assert_eq!(
        horns.len(),
        1,
        "authority cooldown rejects the duplicate edge"
    );
    app.world_mut()
        .resource_mut::<Messages<CoreEvent>>()
        .write(CoreEvent {
            sequence: horns[0]["seq"].as_u64().unwrap(),
            kind: "vehicleHorn".into(),
            data: horns[0]["data"].clone(),
        });
    app.world_mut().run_system_cached(events).unwrap();
    app.world_mut().run_system_cached(events).unwrap();
    let mut query = app.world_mut().query::<(
        &AudioPlayer,
        &PlaybackSettings,
        &audio::VehicleSound,
        &ChildOf,
    )>();
    let (clip, playback, sound, parent) = query.single(app.world()).unwrap();
    assert_eq!(parent.parent(), car);
    assert_eq!(playback.speed, 1.);
    assert!(playback.spatial);
    assert!((sound.gain - 10_f32.powf(4. / 20.)).abs() < 1e-6);
    assert_eq!(sound.max_distance, 90.);
    assert!(
        app.world()
            .resource::<AssetServer>()
            .get_path(clip.0.id())
            .unwrap()
            .path()
            .ends_with(data::named("police").unwrap().spec.horn.as_ref().unwrap())
    );
}
