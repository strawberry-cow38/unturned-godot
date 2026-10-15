//! Original native geometry → normal Fire → accepted UDP pane state → native
//! collision/visibility. No direct damage or renderer-owned repair mutation.
use super::*;
use crate::{
    bridge::CoreBridge,
    entities::CoreEvent,
    player::WorldSessionEpoch,
    settings::Settings,
    ui::{UiModel, UiState},
    world::{WorldGeometry, WorldState},
};
use serde_json::Value;
use std::{net::UdpSocket, path::Path};
mod visual;

struct Journey {
    peer: CoreBridge,
    id: u32,
    body: Entity,
    pane: Entity,
    center: Vec3,
    normal: Vec3,
    host_state: Value,
    peer_state: Value,
    sequence: u64,
}
impl Journey {
    fn install(app: &mut App) -> Self {
        let host = CoreBridge::launch(Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
        let peer = CoreBridge::launch(Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
        let socket = UdpSocket::bind("127.0.0.1:0").unwrap();
        let port = socket.local_addr().unwrap().port();
        drop(socket);
        let config = WorldConfig::default();
        host.request(
            "init",
            json!({"contentRoot":config.content_root,"listenPort":port,"seed":0,
            "allowCheats":true,"starterKit":true,"spawn":[0,0,-6]}),
        )
        .unwrap();
        let definition = crate::vehicle::data::named("police").unwrap();
        let pose = Transform::from_xyz(0., 1., 0.);
        let registration = crate::vehicle::registration(
            definition,
            "glazing-original-police".into(),
            pose.translation,
            0.,
            0,
        );
        let id = host
            .request("worldVehicles", json!({"vehicles":[registration]}))
            .unwrap()["result"]["vehicles"][0]["netId"]
            .as_u64()
            .unwrap() as u32;
        peer.request(
            "init",
            json!({"contentRoot":config.content_root,"connectHost":"127.0.0.1","connectPort":port}),
        )
        .unwrap();
        let mut host_state = Value::Null;
        let mut peer_state = Value::Null;
        for _ in 0..30 {
            host_state = host.request("tick", json!({})).unwrap()["state"].clone();
            peer_state = peer.request("tick", json!({})).unwrap()["state"].clone();
        }
        assert_eq!(peer_state["connected"], true);
        assert_eq!(peer_state["network"]["ownsWorld"], false);
        let mut model = UiModel::default();
        model.connected = true;
        model.network.owns_world = true;
        app.insert_resource(host)
            .insert_resource(model)
            .insert_resource(GameSnapshot(peer_state.clone()))
            .insert_resource(WorldState {
                ready: true,
                ..default()
            })
            .insert_resource(UiState {
                screen: crate::ui::Screen::Playing,
                networked: true,
                ..default()
            })
            .init_resource::<WorldSessionEpoch>()
            .init_resource::<Settings>()
            .init_resource::<WorldGeometry>()
            .init_resource::<crate::vehicle::geometry::GeometryUploads>()
            .init_asset::<AudioSource>()
            .add_message::<CoreEvent>()
            .add_plugins(crate::particles::ParticlePlugin)
            .add_systems(Update, super::super::effects::receive.after(adopt));
        let body = build_unstepped(app, "police", pose);
        app.world_mut().get_mut::<VehicleBody>(body).unwrap().net_id = id;
        app.world_mut()
            .run_system_once(bevy::transform::systems::propagate_transforms_for::<()>)
            .unwrap();
        let pane = app
            .world()
            .get::<Glazing>(body)
            .unwrap()
            .panes
            .iter()
            .copied()
            .find(|e| app.world().get::<Pane>(*e).unwrap().index == 0)
            .unwrap();
        let p = app.world().get::<Pane>(pane).unwrap();
        let transform = app.world().get::<GlobalTransform>(pane).unwrap();
        let center = transform.transform_point(p.center);
        let normal = outward(p, transform, pose.translation);
        app.world_mut()
            .run_system_once(super::super::upload::publish)
            .unwrap();
        app.world_mut()
            .run_system_once(crate::vehicle::geometry::upload)
            .unwrap();
        let mut journey = Self {
            peer,
            id,
            body,
            pane,
            center,
            normal,
            host_state,
            peer_state,
            sequence: 0,
        };
        journey.pump(app.world_mut(), 12);
        assert_eq!(
            journey.row()["glassMask"],
            63,
            "all six real panes must register in one complete build"
        );
        assert_eq!(
            journey.row()["tireMask"],
            15,
            "actual original split rubber is available before first registration"
        );
        journey
    }
    fn row(&self) -> &Value {
        self.peer_state["vehicles"]
            .as_array()
            .unwrap()
            .iter()
            .find(|r| r["netId"] == self.id)
            .unwrap()
    }
    fn pump(&mut self, world: &mut World, n: usize) {
        for _ in 0..n {
            self.host_state = world
                .resource::<CoreBridge>()
                .request("tick", json!({}))
                .unwrap()["state"]
                .clone();
            self.peer_state = self.peer.request("tick", json!({})).unwrap()["state"].clone();
            world.resource_mut::<GameSnapshot>().0 = self.peer_state.clone();
            if let Some(events) = self.peer_state["events"].as_array() {
                for event in events {
                    let seq = event["seq"].as_u64().unwrap();
                    if seq <= self.sequence {
                        continue;
                    }
                    world.write_message(CoreEvent {
                        sequence: seq,
                        kind: event["kind"].as_str().unwrap().into(),
                        data: event["data"].clone(),
                    });
                    self.sequence = seq;
                }
                self.peer.ack_events(self.sequence);
            }
        }
    }
    fn shot(&mut self, world: &mut World) {
        let bridge = world.resource::<CoreBridge>();
        let origin = self.center + self.normal * 2.;
        let feet = origin - Vec3::Y * 1.6;
        bridge.request("action",json!({"name":"console","args":{"text":format!("teleport {} {} {}",feet.x,feet.y,feet.z)}})).unwrap();
        bridge
            .request(
                "action",
                json!({"name":"holdItem","args":{"page":0,"x":0,"y":0}}),
            )
            .unwrap();
        self.pump(world, 20);
        let before = self.host_state["weapon"]["ammo"].as_u64().unwrap();
        let health = self.row()["health"].clone();
        let hit = world
            .resource::<CoreBridge>()
            .request(
                "worldRay",
                json!({"from":origin.to_array(),"to":(self.center-self.normal*0.4).to_array()}),
            )
            .unwrap();
        assert_eq!(
            hit["result"]["bodyPart"], "glass:0",
            "actual original windshield triangle must receive the normal shot: {hit}"
        );
        let result=world.resource::<CoreBridge>().request("action",json!({"name":"fire","args":{"origin":origin.to_array(),"dir":(-self.normal).to_array(),"visual":{"aimAlpha":1}}})).unwrap();
        assert_eq!(result["result"]["queued"], true);
        self.pump(world, 12);
        assert_eq!(self.host_state["weapon"]["ammo"], before - 1);
        assert_eq!(
            self.row()["health"],
            health,
            "glass consumes the shot before hull damage"
        );
        assert_eq!(self.row()["brokenGlassMask"], 1);
    }
    fn repair(&mut self, world: &mut World) {
        let result=self.peer.request("action",json!({"name":"vehicleMaintenance","args":{"netId":self.id,"action":"RepairGlass","index":0}})).unwrap();
        assert_eq!(result["result"]["queued"], true);
        self.pump(world, 12);
        assert_eq!(self.row()["brokenGlassMask"], 0);
    }
}

#[test]
fn original_police_geometry_normal_fire_and_remote_repair_drive_native_aperture() {
    let mut app = app();
    let mut journey = Journey::install(&mut app);
    for _ in 0..3 {
        app.update();
    }
    let origin = journey.center + journey.normal * 0.35;
    assert_eq!(
        ray(&mut app, origin, -journey.normal, 0.7, Group::GROUP_2).map(|h| h.0),
        Some(journey.pane)
    );
    journey.shot(app.world_mut());
    // Do not refresh native geometry yet: managed accepted filtering must already
    // remove the just-broken original triangle from every authority ray.
    let hit = app
        .world()
        .resource::<CoreBridge>()
        .request(
            "worldRay",
            json!({"from":origin.to_array(),"to":(origin-journey.normal*0.7).to_array()}),
        )
        .unwrap();
    assert_ne!(hit["result"]["bodyPart"], "glass:0");
    for _ in 0..2 {
        app.update();
    }
    assert_eq!(
        *app.world().get::<Visibility>(journey.pane).unwrap(),
        Visibility::Hidden
    );
    assert!(ray(&mut app, origin, -journey.normal, 0.7, Group::GROUP_2).is_none());
    let particles = app
        .world_mut()
        .query_filtered::<Entity, With<crate::particles::Emitter>>()
        .iter(app.world())
        .count();
    assert_eq!(
        particles, 1,
        "one accepted remote break emits one original six-chip burst"
    );
    // Same geometry registration cannot locally repair accepted damage.
    app.world_mut()
        .get_mut::<Glazing>(journey.body)
        .unwrap()
        .uploaded = None;
    app.world_mut()
        .run_system_once(super::super::upload::publish)
        .unwrap();
    journey.pump(app.world_mut(), 8);
    assert_eq!(journey.row()["brokenGlassMask"], 1);
    journey.repair(app.world_mut());
    for _ in 0..2 {
        app.update();
    }
    assert_eq!(
        *app.world().get::<Visibility>(journey.pane).unwrap(),
        Visibility::Inherited
    );
    assert_eq!(
        ray(&mut app, origin, -journey.normal, 0.7, Group::GROUP_2).map(|h| h.0),
        Some(journey.pane)
    );
    assert_eq!(
        app.world_mut()
            .query_filtered::<Entity, With<crate::particles::Emitter>>()
            .iter(app.world())
            .count(),
        particles,
        "repair is not another break burst"
    );
}
