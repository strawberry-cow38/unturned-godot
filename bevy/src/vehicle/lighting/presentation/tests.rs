//! Real host/UDP driver switches consumed by original native lamp geometry.
use super::*;
use bevy::ecs::system::RunSystemOnce;
use serde_json::Value;
use std::{net::UdpSocket, path::Path};
mod visual;

#[derive(Resource)]
struct Journey {
    peer: CoreBridge,
    id: u32,
    body: Entity,
    host_state: Value,
    peer_state: Value,
    frame: u32,
    last_event: u64,
    shots: u32,
}
fn bridge() -> CoreBridge {
    CoreBridge::launch(Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap()
}
fn install(app: &mut App) -> Journey {
    let socket = UdpSocket::bind("127.0.0.1:0").unwrap();
    let port = socket.local_addr().unwrap().port();
    drop(socket);
    let host = bridge();
    let peer = bridge();
    let config = WorldConfig::default();
    host.request(
        "init",
        json!({"contentRoot":config.content_root,"listenPort":port,
        "seed":0,"allowCheats":true,"starterKit":true,"spawn":[0,0,0]}),
    )
    .unwrap();
    let metadata = host.request("catalog", json!({})).unwrap();
    let mut catalog = ItemCatalog::default();
    catalog.vehicle_lighting =
        Some(serde_json::from_value(metadata["result"]["vehicleLighting"].clone()).unwrap());
    let definition = data::named("police").unwrap();
    let request = crate::vehicle::registration(
        definition,
        "native-lighting-fixture".into(),
        Vec3::ZERO,
        0.,
        0,
    );
    let id = host
        .request("worldVehicles", json!({"vehicles":[request]}))
        .unwrap()["result"]["vehicles"][0]["netId"]
        .as_u64()
        .unwrap() as u32;
    peer.request(
        "init",
        json!({"contentRoot":config.content_root,"connectHost":"127.0.0.1","connectPort":port}),
    )
    .unwrap();
    for _ in 0..30 {
        host.request("tick", json!({})).unwrap();
        peer.request("tick", json!({})).unwrap();
    }
    peer.request(
        "action",
        json!({"name":"enterVehicle","args":{"netId":id,"seat":0}}),
    )
    .unwrap();
    let mut model = UiModel::default();
    model.connected = true;
    model.network.owns_world = true;
    app.insert_resource(host)
        .insert_resource(catalog)
        .insert_resource(config)
        .insert_resource(model)
        .init_resource::<Settings>()
        .init_resource::<crate::content::ContentCache>()
        .init_resource::<WorldSessionEpoch>()
        .init_resource::<crate::world::WorldGeometry>()
        .init_resource::<crate::vehicle::geometry::GeometryUploads>()
        .add_message::<crate::entities::CoreEvent>()
        .add_plugins(crate::particles::ParticlePlugin)
        .insert_resource(GameSnapshot(json!({})))
        .insert_resource(WorldState {
            ready: true,
            ..default()
        })
        .insert_resource(UiState {
            screen: crate::ui::Screen::Playing,
            networked: true,
            ..default()
        });
    let body = crate::vehicle::tests::body(app, "police", Vec3::ZERO);
    app.world_mut().get_mut::<VehicleBody>(body).unwrap().net_id = id;
    app.world_mut()
        .entity_mut(body)
        .insert(Visibility::Inherited);
    app.world_mut()
        .run_system_once(
            move |mut commands: Commands,
                  mut cache: ResMut<crate::content::ContentCache>,
                  mut meshes: ResMut<Assets<Mesh>>,
                  mut materials: ResMut<Assets<StandardMaterial>>,
                  mut images: ResMut<Assets<Image>>,
                  assets: Res<AssetServer>,
                  config: Res<WorldConfig>| {
                crate::vehicle::render::build(
                    &mut commands,
                    body,
                    data::named("police").unwrap(),
                    0,
                    &config.content_root,
                    &mut cache,
                    &mut meshes,
                    &mut materials,
                    &mut images,
                    &assets,
                );
            },
        )
        .unwrap();
    app.world_mut().run_system_once(build).unwrap();
    app.world_mut().run_system_once(upload).unwrap();
    app.world_mut()
        .run_system_once(bevy::transform::systems::propagate_transforms_for::<()>)
        .unwrap();
    app.world_mut()
        .run_system_once(crate::vehicle::geometry::upload)
        .unwrap();
    let mut journey = Journey {
        peer,
        id,
        body,
        host_state: Value::Null,
        peer_state: Value::Null,
        frame: 0,
        last_event: 0,
        shots: 0,
    };
    journey.pump(app, 20);
    assert_eq!(journey.peer_state["network"]["ownsWorld"], false);
    assert_eq!(
        journey.row()["driverPlayerId"],
        journey.peer_state["playerId"]
    );
    assert_eq!(journey.row()["lampMask"], 127);
    assert_eq!(
        journey.row()["flags"].as_u64().unwrap() & 128,
        0,
        "fixture seed must not start an alarm"
    );
    journey
}
impl Journey {
    fn row(&self) -> &Value {
        self.peer_state["vehicles"]
            .as_array()
            .unwrap()
            .iter()
            .find(|r| r["netId"] == self.id)
            .unwrap()
    }
    fn command(&self, action: &str, value: i32) {
        let result=self.peer.request("action",json!({"name":"vehicleLighting","args":{"netId":self.id,"action":action,"value":value}})).unwrap();
        assert_eq!(result["result"]["queued"], true);
    }
    fn damage(&mut self, world: &mut World, slot: usize) {
        let health = self.row()["health"].clone();
        let label = &world
            .resource::<ItemCatalog>()
            .vehicle_lighting
            .as_ref()
            .unwrap()
            .lamp_labels[slot];
        let built = world.get::<BuiltLighting>(self.body).unwrap();
        let lamp = built.lamps.iter().find(|l| l.label == label).unwrap();
        let pose = world.get::<Transform>(self.body).unwrap();
        let center = pose.transform_point(lamp.center);
        let outward = pose.rotation
            * if slot < 2 {
                Vec3::NEG_Z
            } else if slot < 4 {
                Vec3::Z
            } else {
                Vec3::Y
            };
        let origin = center + outward * 4.;
        let hit = world
            .resource::<CoreBridge>()
            .request(
                "worldRay",
                json!({"from":origin.to_array(),"to":(center-outward).to_array()}),
            )
            .unwrap();
        assert_eq!(
            hit["result"]["vehicleNetId"], self.id,
            "ordinary shot must intersect uploaded original vehicle geometry: {hit}"
        );
        let point = crate::player::vector(&hit["result"]["point"]).unwrap();
        assert!(
            point.distance(center) < 0.42,
            "lamp {label} must be within original contact tolerance: {point:?} vs {center:?}"
        );
        let feet = origin - Vec3::Y * 1.6;
        world
            .resource::<CoreBridge>()
            .request(
                "action",
                json!({"name":"console","args":{
            "text":format!("teleport {} {} {}", feet.x, feet.y, feet.z)}}),
            )
            .unwrap();
        self.pump_world(world, 12);
        let before = self.host_state["weapon"]["ammo"].as_u64().unwrap();
        let reply = world
            .resource::<CoreBridge>()
            .request(
                "action",
                json!({"name":"fire","args":{
            "origin":origin.to_array(),"dir":(-outward).to_array(),"visual":{"aimAlpha":1}}}),
            )
            .unwrap();
        assert_eq!(reply["result"]["queued"], true);
        self.shots += 1;
        self.pump_world(world, 12);
        assert_eq!(
            self.host_state["weapon"]["ammo"],
            before - 1,
            "one normal Fire consumes one round"
        );
        assert_eq!(
            self.row()["health"],
            health,
            "a lamp contact consumes the shot before hull damage"
        );
        assert_ne!(
            self.row()["brokenLampMask"].as_u64().unwrap() & (1 << slot),
            0,
            "actual accepted shot must break lamp {slot}; host={}",
            self.host_state
        );
    }
    fn pump(&mut self, app: &mut App, n: usize) {
        self.pump_world(app.world_mut(), n);
    }
    fn pump_world(&mut self, world: &mut World, n: usize) {
        for _ in 0..n {
            self.host_state = world
                .resource::<CoreBridge>()
                .request("tick", json!({}))
                .unwrap()["state"]
                .clone();
            self.peer_state = self.peer.request("tick", json!({})).unwrap()["state"].clone();
            world.resource_mut::<GameSnapshot>().0 = self.peer_state.clone();
            if world.contains_resource::<crate::particles::ParticleAssets>() {
                if let Some(events) = self.peer_state["events"].as_array() {
                    for event in events {
                        let sequence = event["seq"].as_u64().unwrap_or(0);
                        if sequence <= self.last_event {
                            continue;
                        }
                        world.write_message(crate::entities::CoreEvent {
                            sequence,
                            kind: event["kind"].as_str().unwrap_or("").into(),
                            data: event["data"].clone(),
                        });
                        self.last_event = sequence;
                    }
                    self.peer.ack_events(self.last_event);
                }
                world
                    .run_system_cached(super::super::breaks::receive)
                    .unwrap();
                world
                    .resource_mut::<Messages<crate::entities::CoreEvent>>()
                    .clear();
            }
            world.run_system_once(advance).unwrap();
            world.run_system_once(update).unwrap();
        }
    }
}
fn app() -> App {
    let mut app = App::new();
    app.add_plugins((
        MinimalPlugins,
        AssetPlugin {
            file_path: WorldConfig::default()
                .content_root
                .to_string_lossy()
                .into_owned(),
            ..default()
        },
        TransformPlugin,
    ))
    .register_required_components::<Mesh3d, Visibility>()
    .init_asset::<Mesh>()
    .init_asset::<Image>()
    .init_asset::<StandardMaterial>()
    .init_asset::<AudioSource>();
    app
}
fn heads(app: &App, body: Entity) -> Vec<(Entity, SpotLight, Visibility)> {
    let runtime = app.world().get::<Runtime>(body).unwrap();
    runtime
        .lights
        .iter()
        .filter(|l| matches!(l.role, Role::Head))
        .map(|l| {
            (
                l.entity,
                app.world().get::<SpotLight>(l.entity).unwrap().clone(),
                *app.world().get::<Visibility>(l.entity).unwrap(),
            )
        })
        .collect()
}
#[test]
fn actual_udp_switches_reach_native_beams_and_break_repair_keeps_the_other_lamp() {
    let mut app = app();
    let mut journey = install(&mut app);
    app.update();
    assert!(
        heads(&app, journey.body)
            .iter()
            .all(|(_, s, v)| s.intensity == 0. && *v == Visibility::Hidden)
    );
    journey.command("CycleHeadlights", 0);
    journey.pump(&mut app, 12);
    let head = heads(&app, journey.body);
    assert_eq!(head.len(), 2);
    for (_, light, visible) in &head {
        assert_eq!(*visible, Visibility::Inherited);
        assert_eq!(light.range, 45.);
        assert_eq!(light.intensity, crate::hdr::point_lumens(9.));
        assert!(light.shadow_maps_enabled);
    }
    journey.command("CycleHeadlights", 0);
    journey.pump(&mut app, 12);
    assert_eq!(journey.row()["highBeam"], true);
    for (_, light, _) in heads(&app, journey.body) {
        assert_eq!(light.range, 98.);
        assert_eq!(light.outer_angle, 21_f32.to_radians());
    }
    let original_health = journey.row()["health"].clone();
    journey.damage(app.world_mut(), 0);
    journey.pump(&mut app, 12);
    assert_eq!(journey.row()["health"], original_health);
    assert_eq!(journey.row()["brokenLampMask"], 1);
    let head = heads(&app, journey.body);
    assert_eq!(head.iter().filter(|(_, s, _)| s.intensity > 0.).count(), 1);
    let built = app.world().get::<BuiltLighting>(journey.body).unwrap();
    let left = built
        .lamps
        .iter()
        .find(|l| l.label == "headlight_l")
        .unwrap();
    let right = built
        .lamps
        .iter()
        .find(|l| l.label == "headlight_r")
        .unwrap();
    let materials = app.world().resource::<Assets<StandardMaterial>>();
    assert_eq!(
        materials
            .get(left.material.as_ref().unwrap())
            .unwrap()
            .emissive
            .red,
        0.
    );
    assert!(
        materials
            .get(right.material.as_ref().unwrap())
            .unwrap()
            .emissive
            .red
            > 0.
    );
    // A metadata refresh must not heal the authority or native lamp.
    app.world_mut()
        .get_mut::<Runtime>(journey.body)
        .unwrap()
        .uploaded = None;
    app.world_mut().run_system_once(upload).unwrap();
    journey.pump(&mut app, 10);
    assert_eq!(journey.row()["brokenLampMask"], 1);
    journey.command("RepairLamp", 0);
    journey.pump(&mut app, 12);
    assert_eq!(journey.row()["brokenLampMask"], 0);
    assert!(
        heads(&app, journey.body)
            .iter()
            .all(|(_, s, _)| s.intensity > 0.)
    );
    journey.command("SetPattern", 2);
    journey.pump(&mut app, 12);
    let runtime = app.world().get::<Runtime>(journey.body).unwrap();
    let voice = runtime.voice.expect("accepted siren starts original loop");
    assert_eq!(
        app.world().get::<PlaybackSettings>(voice).unwrap().speed,
        0.88
    );
    for slot in 4..=6 {
        journey.damage(app.world_mut(), slot);
    }
    journey.pump(&mut app, 12);
    assert!(
        app.world()
            .get::<Runtime>(journey.body)
            .unwrap()
            .voice
            .is_none()
    );
    assert!(app.world().get_entity(voice).is_err());
    let children = app
        .world()
        .get::<Runtime>(journey.body)
        .unwrap()
        .lights
        .iter()
        .map(|l| l.entity)
        .collect::<Vec<_>>();
    app.world_mut().despawn(journey.body);
    assert!(children.iter().all(|e| app.world().get_entity(*e).is_err()));
}

#[test]
fn native_siren_clock_holds_on_singleplayer_pause_and_keeps_phase_on_switch_off() {
    let mut app = app();
    let mut journey = install(&mut app);
    app.update();
    journey.command("ToggleSiren", 0);
    journey.pump(&mut app, 12);
    let time = app
        .world()
        .get::<Runtime>(journey.body)
        .unwrap()
        .clock
        .siren_time;
    {
        let mut ui = app.world_mut().resource_mut::<UiState>();
        ui.screen = crate::ui::Screen::Paused;
        ui.networked = false;
    }
    for _ in 0..30 {
        app.world_mut().run_system_once(advance).unwrap();
    }
    assert_eq!(
        app.world()
            .get::<Runtime>(journey.body)
            .unwrap()
            .clock
            .siren_time,
        time
    );
    app.world_mut().resource_mut::<UiState>().screen = crate::ui::Screen::Playing;
    journey.command("ToggleSiren", 0);
    journey.pump(&mut app, 12);
    let time = app
        .world()
        .get::<Runtime>(journey.body)
        .unwrap()
        .clock
        .siren_time;
    journey.pump(&mut app, 30);
    assert_eq!(
        app.world()
            .get::<Runtime>(journey.body)
            .unwrap()
            .clock
            .siren_time,
        time
    );
}
