use super::*;
use crate::{content::ContentCache, ui::Screen};
use std::time::Duration;

#[test]
fn source_seed_seven_anchor_matches_actual_godot_462_and_missing_map_fallback() {
    // Filled from the original Godot executable, not values derived from this port.
    let pairs = [
        (1, 0),
        (2, 1),
        (3, 2),
        (10, 3),
        (31, 8),
        (61, 1),
        (100, 3),
        (256, 155),
        (1000, 803),
        (2147483647, 1352667803),
    ];
    for (count, expected) in pairs {
        assert_eq!(source_pick(count), Some(expected));
    }
    assert_eq!(source_pick(0), None);
    assert_eq!(source_anchor(&[]), Vec3::new(0., 0., -350.));
    assert_eq!(
        source_anchor(&[json!({"x":17.25,"z":-82.5})]),
        Vec3::new(17.25, 0., -82.5)
    );
}
fn app(enabled: bool) -> App {
    let config = WorldConfig {
        map_name: "Fixture".into(),
        map_root: None,
        ..default()
    };
    let bridge = CoreBridge::launch(std::path::Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
    let reply = bridge
        .request(
            "init",
            json!({"contentRoot":config.content_root,"spawn":[-5,0,-346]}),
        )
        .unwrap();
    let mut model = UiModel::default();
    model.adopt_snapshot(&reply["state"]);
    let mut options = crate::cli::Options::default();
    options.demo_furniture = enabled;
    let mut app = App::new();
    app.add_plugins((
        MinimalPlugins,
        TransformPlugin,
        AssetPlugin::default(),
        RapierPhysicsPlugin::<NoUserData>::default(),
    ))
    .insert_resource(config)
    .insert_resource(crate::LaunchOptions(options))
    .insert_resource(bridge)
    .insert_resource(model)
    .insert_resource(GameSnapshot(reply["state"].clone()))
    .insert_resource(ServiceManifest {
        map: "Fixture".into(),
        ..default()
    })
    .insert_resource(WorldState {
        ready: true,
        ..default()
    })
    .insert_resource(UiState {
        screen: Screen::Playing,
        ..default()
    })
    .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
        Duration::from_millis(20),
    ))
    .init_resource::<WorldSessionEpoch>()
    .init_resource::<WorldGeometry>()
    .init_resource::<WorldOrigin>()
    .init_resource::<ContentCache>()
    .init_resource::<crate::settings::Settings>()
    .init_resource::<crate::vehicle::Occupancy>()
    .init_resource::<InteractionFocus>()
    .init_asset::<Mesh>()
    .init_asset::<StandardMaterial>()
    .add_plugins((BedPlugin, super::super::interactables::InteractablePlugin));
    app
}
fn tick(app: &mut App) {
    let mut state = Value::Null;
    for _ in 0..8 {
        state = app
            .world()
            .resource::<CoreBridge>()
            .request("tick", json!({}))
            .unwrap()["state"]
            .clone();
    }
    app.world_mut().resource_mut::<GameSnapshot>().0 = state;
    app.update();
}
fn action(app: &mut App, name: &str, id: u32) {
    app.world()
        .resource::<CoreBridge>()
        .request("action", json!({"name":name,"args":{"netId":id}}))
        .unwrap();
    tick(app);
}
#[test]
fn real_authority_claim_then_recline_and_stand_use_original_bed_and_one_seat() {
    let mut app = app(true);
    app.update();
    tick(&mut app);
    let entity = app
        .world()
        .resource::<Demo>()
        .entity
        .expect("accepted bed rendered");
    let pose = *app.world().get::<Transform>(entity).unwrap();
    assert_eq!(pose.translation, Vec3::new(-5., 0.225, -348.));
    let collider = app.world().get::<Collider>(entity).unwrap();
    assert_eq!(collider.as_cuboid().unwrap().half_extents(), SIZE * 0.5);
    assert_eq!(
        app.world()
            .get::<CollisionGroups>(entity)
            .unwrap()
            .memberships,
        Group::GROUP_1
    );
    let bed = app.world().get::<Bed>(entity).unwrap();
    let material = app
        .world()
        .resource::<Assets<StandardMaterial>>()
        .get(&bed.material)
        .unwrap();
    assert_eq!(material.base_color, Color::srgb(0.55, 0.52, 0.60));
    assert_eq!(
        intent(&app.world().resource::<GameSnapshot>().0, 1),
        ("Claim bed", "claimBed", 1)
    );
    action(&mut app, "claimBed", 1);
    let snapshot = &app.world().resource::<GameSnapshot>().0;
    assert_eq!(
        snapshot["interactables"]["beds"][0]["owner"],
        snapshot["playerId"]
    );
    assert_eq!(intent(snapshot, 1), ("Lie down", "sitSeat", 1));
    action(&mut app, "sitSeat", 1);
    let seat = app
        .world()
        .resource::<super::super::interactables::PropOccupancy>()
        .seat
        .unwrap();
    assert!(seat.recline);
    assert_eq!(seat.net_id, 1);
    assert!((seat.anchor - Vec3::new(-4., 0.45, -348.)).length() < 1e-5);
    assert!((seat.eye_pose.translation - Vec3::new(-5.5, 0.70, -348.)).length() < 1e-5);
    assert!((*seat.avatar_pose().up() - Vec3::NEG_X).length() < 1e-5);
    action(&mut app, "sitSeat", 0);
    assert!(
        app.world()
            .resource::<super::super::interactables::PropOccupancy>()
            .seat
            .is_none()
    );
    assert_eq!(
        intent(&app.world().resource::<GameSnapshot>().0, 1),
        ("Lie down", "sitSeat", 1)
    );
    assert_eq!(app.world().resource::<ServiceManifest>().seats.len(), 1);
    let heading = app.world().resource::<GameSnapshot>().0["player"]["yaw"].as_f64();
    app.world()
        .resource::<CoreBridge>()
        .request("action", json!({"name":"console","args":{"text":"kill"}}))
        .unwrap();
    tick(&mut app);
    assert_eq!(
        app.world().resource::<GameSnapshot>().0["player"]["alive"],
        false
    );
    action(&mut app, "respawn", 0);
    let state = &app.world().resource::<GameSnapshot>().0;
    assert_eq!(state["player"]["alive"], true);
    // PlayerController.Respawn lifts the claimed base by 0.5m.
    assert_eq!(
        crate::player::vector(&state["player"]["position"]),
        Some(Vec3::new(-5., 0.5, -348.))
    );
    // NetWorldHost.SpawnOverride deliberately ignores the bed's stored yaw,
    // matching PlayerController.Respawn's TryGetSpawn(..., out _).
    assert_eq!(state["player"]["yaw"].as_f64(), heading);
}

#[test]
fn retail_default_never_registers_demo_furniture_and_disconnect_retires_metadata() {
    let mut app = app(false);
    app.update();
    tick(&mut app);
    assert!(app.world().resource::<Demo>().definition.is_none());
    assert!(!accepted(
        &app.world().resource::<GameSnapshot>().0,
        "beds",
        1
    ));
    app.world_mut()
        .resource_mut::<crate::LaunchOptions>()
        .0
        .demo_furniture = true;
    app.update();
    tick(&mut app);
    let entity = app.world().resource::<Demo>().entity.unwrap();
    app.world_mut().resource_mut::<UiModel>().connected = false;
    app.world_mut().resource_mut::<GameSnapshot>().0 = Value::Null;
    app.update();
    assert!(app.world().get_entity(entity).is_err());
    assert!(app.world().resource::<ServiceManifest>().seats.is_empty());
}
#[test]
fn client_uses_accepted_source_ids_without_host_registration_or_geometry_publication() {
    let mut app = app(true);
    // A real client-only bridge guard is covered by the production network suite.
    // This local core deliberately stays empty: any leaked worldServices call
    // would create a bed and be visible in the final authoritative state query.
    app.world_mut().resource_mut::<GameSnapshot>().0["network"] =
        json!({"mode":"client","ownsWorld":false});
    app.update();
    assert!(app.world().resource::<Demo>().entity.is_none());
    {
        let mut snapshot = app.world_mut().resource_mut::<GameSnapshot>();
        snapshot.0["interactables"] =
            json!({"beds":[{"netId":1,"owner":0}],"seats":[{"netId":1,"occupant":0}]});
    }
    app.update();
    assert!(app.world().resource::<Demo>().entity.is_some());
    assert!(!app.world().resource::<Demo>().registered);
    assert!(!app.world().resource::<Demo>().exported);
    let state = app
        .world()
        .resource::<CoreBridge>()
        .request("tick", json!({}))
        .unwrap()["state"]
        .clone();
    assert!(
        !accepted(&state, "beds", 1),
        "client presentation must not register host fixtures"
    );
    let shift = Vec3::new(1024., 0., -256.);
    let before = app
        .world()
        .resource::<Demo>()
        .definition
        .as_ref()
        .unwrap()
        .base
        .translation;
    shift_origin(app.world_mut(), shift);
    assert_eq!(
        app.world()
            .resource::<Demo>()
            .definition
            .as_ref()
            .unwrap()
            .base
            .translation,
        before - shift
    );
}

#[test]
fn actual_udp_peer_adopts_demo_bed_claim_recline_and_stand_with_source_seat_prefix() {
    let port = std::net::UdpSocket::bind("127.0.0.1:0")
        .unwrap()
        .local_addr()
        .unwrap()
        .port();
    let mut host = app(true);
    let content = host.world().resource::<WorldConfig>().content_root.clone();
    let reply = host
        .world()
        .resource::<CoreBridge>()
        .request(
            "init",
            json!({"contentRoot":content,"mapId":"Fixture","listenPort":port,"spawn":[-5,0,-346]}),
        )
        .unwrap();
    host.world_mut().resource_mut::<GameSnapshot>().0 = reply["state"].clone();
    // A chair already authored before the diagnostic bed owns seat1.
    let chair = json!({"netId":1,"sourceIndex":4,"name":"Chair","position":[20,0,0],"face":[0,0,-1],"groundY":0});
    host.world()
        .resource::<CoreBridge>()
        .request("worldServices", json!({"seats":[chair]}))
        .unwrap();
    host.world_mut()
        .resource_mut::<ServiceManifest>()
        .seats
        .push(chair.clone());
    host.update();
    tick(&mut host);
    assert_eq!(
        host.world()
            .resource::<Demo>()
            .definition
            .as_ref()
            .unwrap()
            .seat_id,
        2
    );
    let mut remote = app(true);
    let reply=remote.world().resource::<CoreBridge>().request("init",json!({"contentRoot":content,"mapId":"Fixture","connectHost":"127.0.0.1","connectPort":port})).unwrap();
    remote.world_mut().resource_mut::<GameSnapshot>().0 = reply["state"].clone();
    remote
        .world_mut()
        .resource_mut::<UiModel>()
        .adopt_snapshot(&reply["state"]);
    remote
        .world_mut()
        .resource_mut::<ServiceManifest>()
        .seats
        .push(chair);
    let pump = |host: &mut App, remote: &mut App, count| {
        for _ in 0..count {
            for app in [&mut *host, &mut *remote] {
                let state = app
                    .world()
                    .resource::<CoreBridge>()
                    .request("tick", json!({}))
                    .unwrap()["state"]
                    .clone();
                app.world_mut()
                    .resource_mut::<UiModel>()
                    .adopt_snapshot(&state);
                app.world_mut().resource_mut::<GameSnapshot>().0 = state;
                app.update();
            }
            std::thread::sleep(Duration::from_millis(2));
        }
    };
    pump(&mut host, &mut remote, 30);
    assert!(remote.world().resource::<UiModel>().connected);
    assert!(!crate::network::owns_world(
        &remote.world().resource::<GameSnapshot>().0
    ));
    let remote_bed = remote
        .world()
        .resource::<Demo>()
        .entity
        .expect("real accepted snapshot creates remote bed");
    assert_eq!(remote.world().get::<Bed>(remote_bed).unwrap().seat_id, 2);
    assert!(!remote.world().resource::<Demo>().registered);
    assert!(!remote.world().resource::<Demo>().exported);
    action(&mut host, "claimBed", 1);
    pump(&mut host, &mut remote, 20);
    let owner = host.world().resource::<GameSnapshot>().0["playerId"].clone();
    assert_eq!(
        remote.world().resource::<GameSnapshot>().0["interactables"]["beds"][0]["owner"],
        owner
    );
    assert_eq!(
        intent(&remote.world().resource::<GameSnapshot>().0, 2).0,
        "Someone else's bed"
    );
    remote
        .world()
        .resource::<CoreBridge>()
        .request("action", json!({"name":"claimBed","args":{"netId":1}}))
        .unwrap();
    pump(&mut host, &mut remote, 20);
    assert_eq!(
        host.world().resource::<GameSnapshot>().0["interactables"]["beds"][0]["owner"],
        owner,
        "server rejects stealing accepted claim"
    );
    action(&mut host, "sitSeat", 2);
    pump(&mut host, &mut remote, 20);
    assert!(
        host.world()
            .resource::<super::super::interactables::PropOccupancy>()
            .seat
            .unwrap()
            .recline
    );
    let state = &remote.world().resource::<GameSnapshot>().0;
    assert!(
        state["interactables"]["seats"]
            .as_array()
            .unwrap()
            .iter()
            .any(|s| s["netId"] == 2 && s["occupant"] == owner)
    );
    assert!(
        remote
            .world()
            .resource::<ServiceManifest>()
            .seats
            .iter()
            .any(|s| s["netId"] == 2 && s["recline"] == true)
    );
    action(&mut host, "sitSeat", 0);
    pump(&mut host, &mut remote, 20);
    assert!(
        remote.world().resource::<GameSnapshot>().0["interactables"]["seats"]
            .as_array()
            .unwrap()
            .iter()
            .any(|s| s["netId"] == 2 && s["occupant"] == 0)
    );
}
#[test]
fn real_bed_focus_is_occluded_and_highlight_follows_only_the_visible_box() {
    let mut app = app(true);
    app.update();
    tick(&mut app);
    let entity = app.world().resource::<Demo>().entity.unwrap();
    let material = app.world().get::<Bed>(entity).unwrap().material.clone();
    app.world_mut().spawn((
        PlayerBody::for_motion_test(Vec3::ZERO, true, "stand"),
        Transform::from_xyz(-5., 0., -346.),
    ));
    let camera = app
        .world_mut()
        .spawn((
            PlayerCamera,
            Transform::from_xyz(-5., 1., -346.).looking_at(Vec3::new(-5., 0.2, -348.), Vec3::Y),
        ))
        .id();
    for _ in 0..3 {
        app.update();
    }
    assert!(app.world().resource::<InteractionFocus>().has_candidate());
    assert_eq!(
        app.world()
            .resource::<Assets<StandardMaterial>>()
            .get(&material)
            .unwrap()
            .emissive,
        Color::srgb(0.2, 0.24, 0.35).to_linear()
    );
    app.world_mut().spawn((
        Collider::cuboid(2., 2., 0.1),
        RigidBody::Fixed,
        Transform::from_xyz(-5., 0., -347.),
    ));
    *app.world_mut().resource_mut::<InteractionFocus>() = default();
    for _ in 0..3 {
        *app.world_mut().resource_mut::<InteractionFocus>() = default();
        app.update();
    }
    assert!(
        !app.world().resource::<InteractionFocus>().has_candidate(),
        "opaque foreground blocks bed focus"
    );
    assert_eq!(
        app.world()
            .resource::<Assets<StandardMaterial>>()
            .get(&material)
            .unwrap()
            .emissive,
        LinearRgba::BLACK
    );
    app.world_mut()
        .get_mut::<Transform>(camera)
        .unwrap()
        .rotation = Quat::IDENTITY;
}
#[test]
fn diagnostic_cli_is_explicit_and_default_off() {
    assert!(
        !crate::cli::Options::parse(Vec::<String>::new())
            .unwrap()
            .demo_furniture
    );
    assert!(
        crate::cli::Options::parse(["--demo-furniture".into()])
            .unwrap()
            .demo_furniture
    );
}
