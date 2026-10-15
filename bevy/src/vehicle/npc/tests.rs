use super::*;
use bevy::ecs::system::RunSystemOnce;
use bevy_rapier3d::prelude::*;
use serde_json::Value;

fn ground() -> crate::world::TerrainPatch {
    crate::world::TerrainPatch {
        origin: Vec2::new(512., -128.),
        cells: 64,
        spacing: 4.,
        heights: vec![10.; 65 * 65],
        layers: vec![2; 65 * 65],
        normals: vec![],
        holes: vec![],
        splat: None,
    }
}
fn native() -> (App, Entity) {
    let mut app = super::super::tests::physics_app(None);
    let mut geometry = WorldGeometry::default();
    geometry.loaded_radius = 100.;
    geometry.terrain = vec![ground()];
    app.insert_resource(UiState {
        screen: crate::ui::Screen::Playing,
        ..default()
    })
    .init_resource::<WorldSessionEpoch>()
    .init_resource::<Autopilots>()
    .init_resource::<terrain::Support>()
    .insert_resource(geometry)
    .insert_resource(GameSnapshot(json!({"connected":true,"playerId":1,
            "network":{"ownsWorld":true},"npcHelis":[]})))
    .add_systems(PostUpdate, drive.before(super::super::step_bodies))
    .add_systems(Update, terrain::support);
    let e = super::super::tests::body(&mut app, "hind", Vec3::new(600., 60., 0.));
    app.world_mut()
        .get_mut::<VehicleBody>(e)
        .unwrap()
        .driver_player_id = 0;
    (app, e)
}
fn accepted(axes: [f32; 4]) -> Value {
    json!({"connected":true,"playerId":1,"network":{"ownsWorld":true},
        "npcHelis":[{"netId":1,"active":true,"rotorRunning":true,"controls":{
            "collective":axes[0],"yaw":axes[1],"pitch":axes[2],"roll":axes[3]}}]})
}

#[test]
fn original_brain_drives_the_one_physical_airframe_beyond_the_render_window() {
    let (mut app, entity) = native();
    let bridge = CoreBridge::launch(std::path::Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
    bridge
        .request(
            "init",
            json!({"contentRoot":WorldConfig::default().content_root,"spawn":[0,10,0]}),
        )
        .unwrap();
    let def = data::named("hind").unwrap();
    let registration = super::super::registration(
        def,
        "npc-flight-test".into(),
        Vec3::new(600., 60., 0.),
        90.,
        0,
    );
    let reply = bridge
        .request("worldVehicles", json!({"vehicles":[registration]}))
        .unwrap();
    let id = reply["result"]["vehicles"][0]["netId"].as_u64().unwrap();
    let patch = ground();
    bridge.request("worldNpcHelis",json!({"mapBounds":[512,768,-128,128],
        "terrainPatches":[{"origin":patch.origin.to_array(),"cells":patch.cells,"spacing":patch.spacing,"heights":patch.heights}],
        "vehicles":[{"netId":id,"target":[0,10,0],"targetName":"native flight fixture"}]})).unwrap();
    {
        let mut body = app.world_mut().get_mut::<VehicleBody>(entity).unwrap();
        body.net_id = id as u32;
    }
    app.world_mut()
        .get_mut::<Transform>(entity)
        .unwrap()
        .rotation = Quat::from_rotation_y(90_f32.to_radians());
    app.world_mut().get_mut::<Velocity>(entity).unwrap().linear =
        Vec3::NEG_X * def.spec.speed_max * 0.5;
    let start = app.world().get::<Transform>(entity).unwrap().translation;
    let mut phases = HashSet::new();
    let mut lowest = start.y;
    for _ in 0..2500 {
        let pose = *app.world().get::<Transform>(entity).unwrap();
        let velocity = *app.world().get::<Velocity>(entity).unwrap();
        let (yaw, pitch, roll) = pose.rotation.to_euler(EulerRot::YXZ);
        bridge.request("worldVehicleState",json!({"vehicles":[{"netId":id,"position":pose.translation.to_array(),
            "eulerDegrees":[pitch.to_degrees(),yaw.to_degrees(),roll.to_degrees()],"linVel":velocity.linear.to_array(),
            "angVel":velocity.angular.to_array(),"flags":1}]})).unwrap();
        let reply = bridge.request("tick", json!({})).unwrap();
        assert_eq!(reply["state"]["npcHelis"][0]["active"], true);
        phases.insert(
            reply["state"]["npcHelis"][0]["phase"]
                .as_str()
                .unwrap()
                .to_owned(),
        );
        assert!(
            (reply["state"]["npcHelis"][0]["wantHeight"]
                .as_f64()
                .unwrap()
                - 60.)
                .abs()
                < 0.001
        );
        app.world_mut().resource_mut::<GameSnapshot>().0 = reply["state"].clone();
        app.update();
        lowest = lowest.min(app.world().get::<Transform>(entity).unwrap().translation.y);
    }
    let body = app.world().get::<VehicleBody>(entity).unwrap();
    let pose = app.world().get::<Transform>(entity).unwrap();
    assert_eq!(
        *app.world().get::<RigidBody>(entity).unwrap(),
        RigidBody::Dynamic
    );
    assert!(
        start.distance(pose.translation) > 10.,
        "An inbound NPC must not freeze outside the player's100m window"
    );
    assert!(
        lowest > 30.,
        "The original warm rotor and AI collective must support the full route: lowest={lowest}, final={:?}",
        pose.translation,
    );
    assert!(
        phases.contains("arrive") && phases.contains("orbit"),
        "the physical airframe must reach the source arrival/orbit phases: {phases:?}"
    );
    assert!(body.flight.spool > 0.99 && body.flight.collective > 0.1);
    assert_eq!(
        app.world_mut()
            .query::<&VehicleBody>()
            .iter(app.world())
            .count(),
        1
    );
    assert!(app.world().resource::<Autopilots>().active(id as u32));
}

#[test]
fn accepted_driver_pause_remote_and_bad_controls_never_compete_with_human_or_restart_rotors() {
    let (mut app, e) = native();
    app.world_mut().resource_mut::<GameSnapshot>().0 = accepted([1., 0.2, -0.3, 0.4]);
    app.update();
    app.update();
    assert!(app.world().resource::<Autopilots>().active(1));
    assert_eq!(
        app.world().get::<VehicleBody>(e).unwrap().controls.pitch,
        -0.3
    );
    app.world_mut()
        .get_mut::<VehicleBody>(e)
        .unwrap()
        .flight
        .spool = 0.4;
    app.world_mut().run_system_once(drive).unwrap();
    assert_eq!(
        app.world().get::<VehicleBody>(e).unwrap().flight.spool,
        0.4,
        "source warm start is applied only once"
    );
    let before = app.world().get::<VehicleBody>(e).unwrap().controls.pitch;
    app.world_mut().resource_mut::<UiState>().screen = crate::ui::Screen::Paused;
    app.world_mut().run_system_once(drive).unwrap();
    assert_eq!(
        app.world().get::<VehicleBody>(e).unwrap().controls.pitch,
        before
    );
    app.world_mut().resource_mut::<UiState>().screen = crate::ui::Screen::Playing;
    {
        let mut body = app.world_mut().get_mut::<VehicleBody>(e).unwrap();
        body.driver_player_id = 1;
        body.controls.pitch = 0.8;
    }
    app.world_mut().resource_mut::<GameSnapshot>().0["npcHelis"][0]["active"] = json!(false);
    app.world_mut().run_system_once(drive).unwrap();
    assert!(!app.world().resource::<Autopilots>().active(1));
    assert_eq!(
        app.world().get::<VehicleBody>(e).unwrap().controls.pitch,
        0.8,
        "AI cannot overwrite a human driver"
    );
    app.world_mut()
        .get_mut::<VehicleBody>(e)
        .unwrap()
        .driver_player_id = 0;
    app.world_mut().resource_mut::<GameSnapshot>().0 = accepted([2., 0., 0., 0.]);
    app.world_mut().run_system_once(drive).unwrap();
    assert!(!app.world().resource::<Autopilots>().active(1));
    assert_eq!(
        app.world().get::<VehicleBody>(e).unwrap().controls.throttle,
        0.
    );
    app.world_mut().resource_mut::<GameSnapshot>().0 = accepted([1., 0., 0., 0.]);
    app.world_mut().resource_mut::<GameSnapshot>().0["network"]["ownsWorld"] = json!(false);
    app.update();
    assert!(!app.world().resource::<Autopilots>().managed(1));
    assert_eq!(
        *app.world().get::<RigidBody>(e).unwrap(),
        RigidBody::KinematicPositionBased
    );
}

#[test]
fn supplemental_original_terrain_catches_an_unpowered_npc_and_retires_on_removal() {
    let (mut app, e) = native();
    app.world_mut().resource_mut::<GameSnapshot>().0 = accepted([0., 0., 0., 0.]);
    app.world_mut().resource_mut::<GameSnapshot>().0["npcHelis"][0]["active"] = json!(false);
    app.world_mut().resource_mut::<GameSnapshot>().0["npcHelis"][0]["rotorRunning"] = json!(false);
    app.world_mut().get_mut::<VehicleBody>(e).unwrap().flags = 0;
    app.world_mut()
        .get_mut::<Transform>(e)
        .unwrap()
        .translation
        .y = 22.;
    for _ in 0..300 {
        app.update();
    }
    let pose = *app.world().get::<Transform>(e).unwrap();
    let spec = &data::named("hind").unwrap().spec;
    let center = Vec3::from_array(spec.box_center);
    let half = Vec3::from_array(spec.box_size) * 0.5;
    let mut bottom = f32::INFINITY;
    for x in [-1., 1.] {
        for y in [-1., 1.] {
            for z in [-1., 1.] {
                bottom = bottom.min(pose.transform_point(center + half * Vec3::new(x, y, z)).y);
            }
        }
    }
    // The original hull collider is offset above its root. A root below the
    // terrain is valid; the supported collider's lowest point must settle on it.
    assert!(
        (bottom - 10.).abs() < 0.15,
        "real Rapier terrain supports the original hull outside streamed collision: bottom={bottom}, pose={pose:?}"
    );
    let resting = pose.translation;
    for _ in 0..50 {
        app.update();
    }
    assert!(
        app.world()
            .get::<Transform>(e)
            .unwrap()
            .translation
            .distance(resting)
            < 0.1,
        "the hull must remain supported, not merely pass through the ground"
    );
    assert!(app.world().resource::<terrain::Support>().entity.is_some());
    app.world_mut().resource_mut::<GameSnapshot>().0["npcHelis"] = json!([]);
    app.update();
    app.update();
    assert!(app.world().resource::<terrain::Support>().entity.is_none());
}

#[test]
fn native_full_height_registration_drives_source_spawn_instead_of_nearby_mesh_zero() {
    let (mut app, _) = native();
    let bridge = CoreBridge::launch(std::path::Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
    bridge
        .request(
            "init",
            json!({"contentRoot":WorldConfig::default().content_root,"spawn":[600,10,0]}),
        )
        .unwrap();
    let reply = bridge.request("snapshot", json!({})).unwrap();
    let mut model = UiModel::default();
    model.adopt_snapshot(&reply["state"]);
    app.insert_resource(bridge)
        .insert_resource(model)
        .init_resource::<WorldConfig>()
        .init_resource::<TerrainRegistration>();
    app.world_mut().run_system_once(configure).unwrap();
    let core = app.world().resource::<CoreBridge>();
    core.request(
        "action",
        json!({"name":"console","args":{"text":"spawnheli hind"}}),
    )
    .unwrap();
    let mut state = Value::Null;
    for _ in 0..12 {
        state = core.request("tick", json!({})).unwrap()["state"].clone();
    }
    assert_eq!(state["npcHelis"].as_array().unwrap().len(), 1);
    assert_eq!(state["npcHelis"][0]["wantHeight"], 60.);
    assert!(
        app.world()
            .resource::<TerrainRegistration>()
            .signature
            .is_some()
    );
}
