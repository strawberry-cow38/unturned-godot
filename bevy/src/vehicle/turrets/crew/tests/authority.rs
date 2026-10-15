//! Actual normal owner Fire commands and a second JSONL process over UDP.
//! The fixture holds the helicopter still; it never writes crew life or damage.
use super::*;
use crate::bridge::CoreBridge;
use serde_json::Value;
use std::{net::UdpSocket, path::Path};

#[derive(Resource)]
pub(super) struct Journey {
    host: CoreBridge,
    peer: CoreBridge,
    port: u16,
    pub id: u32,
    pub metadata: Definition,
    pub host_state: Value,
    pub peer_state: Value,
    pub shots: u32,
    pub render_frame: u32,
}
impl Journey {
    pub fn new() -> Self {
        let socket = UdpSocket::bind("127.0.0.1:0").unwrap();
        let port = socket.local_addr().unwrap().port();
        drop(socket);
        let host = CoreBridge::launch(Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
        host.request(
            "init",
            json!({"contentRoot":WorldConfig::default().content_root,
            "starterKit":true,"allowCheats":true,"listenPort":port,
            "spawn":[-4.8,0.08,-0.26],"yaw":270}),
        )
        .unwrap();
        let catalog = host.request("catalog", json!({})).unwrap();
        let metadata = serde_json::from_value(catalog["result"]["npcCrew"].clone())
            .expect("crew authority bridge must expose original appearance metadata");
        let peer = CoreBridge::launch(Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
        peer.request(
            "init",
            json!({"contentRoot":WorldConfig::default().content_root,
            "connectHost":"127.0.0.1","connectPort":port,"name":"Crew observer"}),
        )
        .unwrap();
        let mut journey = Self {
            host,
            peer,
            port,
            id: 0,
            metadata,
            host_state: Value::Null,
            peer_state: Value::Null,
            shots: 0,
            render_frame: 0,
        };
        journey.pump(30);
        assert_eq!(journey.peer_state["connected"], true);
        assert_eq!(journey.peer_state["network"]["ownsWorld"], false);
        // Both players initially share the host spawn. Move the observer through
        // the normal accepted console command so it cannot intercept our shots.
        journey
            .peer
            .request(
                "action",
                json!({"name":"console","args":{"text":"teleport -8 0 8"}}),
            )
            .unwrap();
        journey.pump(24);
        let observer = crate::player::vector(&journey.peer_state["player"]["position"]).unwrap();
        assert!(observer.distance(Vec3::new(-8., 0., 8.)) < 0.02);
        let def = data::named("huey").unwrap();
        let registration =
            crate::vehicle::registration(def, "crew-authority-fixture".into(), Vec3::ZERO, 0., 0);
        let reply = journey
            .host
            .request("worldVehicles", json!({"vehicles":[registration]}))
            .unwrap();
        journey.id = reply["result"]["vehicles"][0]["netId"].as_u64().unwrap() as u32;
        journey
            .host
            .request(
                "worldNpcHelis",
                json!({"vehicles":[{"netId":journey.id,
            "target":[1000,50,0],"targetName":"native crew authority fixture"}]}),
            )
            .unwrap();
        journey.pump(12);
        assert_eq!(journey.ammo(), 30);
        assert_eq!(journey.health(0), 100.);
        assert_eq!(journey.health(1), 100.);
        assert!(journey.peer_alive(0) && journey.peer_alive(1));
        journey
    }
    pub fn pump(&mut self, n: usize) {
        for _ in 0..n {
            self.host_state = self.host.request("tick", json!({})).unwrap()["state"].clone();
            self.peer_state = self.peer.request("tick", json!({})).unwrap()["state"].clone();
        }
    }
    pub fn fire(&mut self) {
        let before = self.ammo();
        let response = self
            .host
            .request(
                "action",
                json!({"name":"fire","args":{
            "origin":[-4.8,1.68,-0.26],"dir":[1,0,0],"visual":{"aimAlpha":1}}}),
            )
            .unwrap();
        assert_eq!(response["result"]["queued"], true);
        self.shots += 1;
        self.pump(12);
        assert_eq!(
            self.ammo(),
            before - 1,
            "one ordinary Fire consumes one accepted round"
        );
    }
    pub fn ammo(&self) -> u64 {
        self.host_state["weapon"]["ammo"].as_u64().unwrap()
    }
    pub fn health(&self, index: usize) -> f64 {
        self.host_state["npcHelis"]
            .as_array()
            .unwrap()
            .iter()
            .find(|row| row["netId"] == self.id)
            .unwrap()["crews"][index]["health"]
            .as_f64()
            .unwrap()
    }
    pub fn peer_alive(&self, index: usize) -> bool {
        self.peer_state["vehicleMounts"]
            .as_array()
            .unwrap()
            .iter()
            .find(|row| row["vehicleNetId"] == self.id)
            .unwrap()["mounts"][index]["crewAlive"]
            .as_bool()
            .unwrap()
    }
    pub fn evidence(&self) -> Value {
        json!({"fixture":"actual JSONL owner Fire and UDP observer; stationary original Huey",
            "vehicleNetId":self.id,"shots":self.shots,"hostTick":self.host_state["tick"],
            "hostPlayer":self.host_state["player"],"hostCrew":self.host_state["npcHelis"],
            "hostHits":self.host_state["events"].as_array().unwrap().iter()
                .filter(|e|e["kind"]=="hit").collect::<Vec<_>>(),
            "remoteNetwork":self.peer_state["network"],"remoteTick":self.peer_state["tick"],
            "remoteMounts":self.peer_state["vehicleMounts"]})
    }
}

#[test]
fn actual_fire_and_udp_fact_remove_only_killed_native_crew_and_late_join_keeps_it_dead() {
    let mut journey = Journey::new();
    let mut app = app();
    app.world_mut().resource_mut::<ItemCatalog>().npc_crew = Some(journey.metadata.clone());
    build(&mut app, "huey", journey.id);
    app.world_mut().resource_mut::<GameSnapshot>().0 = journey.peer_state.clone();
    app.update();
    app.update();
    assert_eq!(crew(&mut app).len(), 2);
    let ids = app.world().resource::<Registry>().roots.clone();
    let near = *ids.iter().find(|((_, index), _)| *index == 0).unwrap().1;
    let far = *ids.iter().find(|((_, index), _)| *index == 1).unwrap().1;
    let hull_hp = journey.host_state["vehicles"][0]["health"].clone();
    for hp in [60., 20., 0.] {
        journey.fire();
        assert_eq!(journey.health(0), hp);
        assert_eq!(journey.health(1), 100.);
        assert_eq!(journey.host_state["vehicles"][0]["health"], hull_hp);
        app.world_mut().resource_mut::<GameSnapshot>().0 = journey.peer_state.clone();
        app.update();
        assert_eq!(crew(&mut app).len(), if hp == 0. { 1 } else { 2 });
        assert_eq!(app.world().get_entity(near).is_ok(), hp > 0.);
        assert!(app.world().get_entity(far).is_ok());
    }
    assert_eq!(journey.ammo(), 27);
    let hits: Vec<_> = journey.host_state["events"]
        .as_array()
        .unwrap()
        .iter()
        .filter(|e| e["kind"] == "hit")
        .collect();
    assert_eq!(hits.len(), 3);
    assert!(hits.iter().all(|e| e["data"]["headshot"] == true
        && e["data"]["targetKind"] == 3
        && e["data"]["targetId"] == journey.id));
    assert_eq!(hits.last().unwrap()["data"]["killed"], true);

    // A fresh remote replica must not replay a death or first spawn both crew.
    let late = CoreBridge::launch(Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
    late.request(
        "init",
        json!({"contentRoot":WorldConfig::default().content_root,
        "connectHost":"127.0.0.1","connectPort":journey.port,"name":"Late crew observer"}),
    )
    .unwrap();
    let mut state = Value::Null;
    for _ in 0..30 {
        journey.pump(1);
        state = late.request("tick", json!({})).unwrap()["state"].clone();
    }
    assert_eq!(state["connected"], true);
    app.world_mut().resource_mut::<WorldSessionEpoch>().0 += 1;
    app.world_mut().resource_mut::<GameSnapshot>().0 = state;
    app.update();
    assert_eq!(crew(&mut app).len(), 1);
    assert!(
        app.world().get_entity(far).is_err(),
        "session replacement gets a fresh remaining rig"
    );
}

pub(super) fn advance(mut journey: ResMut<Journey>, mut snapshot: ResMut<GameSnapshot>) {
    journey.render_frame += 1;
    if [80, 96, 112].contains(&journey.render_frame) {
        journey.fire();
    } else {
        journey.pump(1);
    }
    snapshot.0 = journey.peer_state.clone();
}

#[test]
fn actual_uploader_keeps_crew_art_out_of_authoritative_hull_triangles() {
    use crate::{
        ui::UiModel,
        vehicle::geometry::{GeometryUploads, upload},
        world::{WorldGeometry, WorldState},
    };
    let journey = Journey::new();
    let mut app = app();
    // DefaultPlugins registers this for the renderer. The minimal rig fixture
    // must retain it, because the production uploader queries visible meshes.
    app.register_required_components::<Mesh3d, Visibility>();
    let mut model = UiModel::default();
    model.adopt_snapshot(&journey.host_state);
    assert!(model.connected && model.network.owns_world);
    app.insert_resource(journey.host)
        .insert_resource(model)
        .insert_resource(GameSnapshot(journey.host_state))
        .insert_resource(WorldState {
            ready: true,
            ..default()
        })
        .init_resource::<WorldGeometry>()
        .init_resource::<GeometryUploads>();
    app.world_mut().resource_mut::<ItemCatalog>().npc_crew = Some(journey.metadata);
    let body = crate::vehicle::tests::body(&mut app, "huey", Vec3::ZERO);
    app.world_mut().get_mut::<VehicleBody>(body).unwrap().net_id = journey.id;
    app.world_mut().entity_mut(body).insert((
        Visibility::Inherited,
        Mounts {
            angles: vec![Vec2::ZERO; 2],
            crew_alive: vec![true; 2],
            head_out: false,
            gunner_rise: 0.,
            accepted_gunner_rise: None,
            frames: Vec::new(),
            hatches: None,
        },
    ));
    // A genuine vehicle mesh keeps this a nonempty upload. Place it below the
    // crew ray so only leaked character art can make the negative control hit.
    let mesh = app
        .world_mut()
        .resource_mut::<Assets<Mesh>>()
        .add(Cuboid::new(1., 1., 1.));
    app.world_mut().spawn((
        Mesh3d(mesh),
        Transform::from_xyz(0., -4., 0.),
        Visibility::Inherited,
        ChildOf(body),
    ));
    app.update();
    app.update();
    assert_eq!(crew(&mut app).len(), 2);
    let person = crew(&mut app)[0];
    let skin = app.world().get::<crate::rig::RigSkin>(person).unwrap().0;
    assert_eq!(
        app.world().get::<Visibility>(skin),
        Some(&Visibility::Inherited)
    );
    let mesh = app.world().get::<Mesh3d>(skin).unwrap();
    let pose = app.world().get::<GlobalTransform>(skin).unwrap();
    let mesh = app.world().resource::<Assets<Mesh>>().get(&mesh.0).unwrap();
    let positions = mesh
        .attribute(Mesh::ATTRIBUTE_POSITION)
        .unwrap()
        .as_float3()
        .unwrap();
    let indices: Vec<_> = mesh.indices().unwrap().iter().collect();
    let (center, normal) = indices
        .chunks_exact(3)
        .find_map(|triangle| {
            let p =
                [0, 1, 2].map(|i| pose.transform_point(Vec3::from_array(positions[triangle[i]])));
            let normal = (p[1] - p[0]).cross(p[2] - p[0]).normalize_or_zero();
            let center = (p[0] + p[1] + p[2]) / 3.;
            (normal.length_squared() > 0.9 && center.y > 0.3).then_some((center, normal))
        })
        .expect("original rig has nondegenerate skin triangles above the hull sentinel");
    let ray = |app: &App, y: f32| {
        app.world()
            .resource::<CoreBridge>()
            .request("worldRay", json!({"from":[-4.,y,-0.26],"to":[4.,y,-0.26]}))
            .unwrap()["result"]
            .clone()
    };
    let crew_ray = |app: &App| {
        app.world()
            .resource::<CoreBridge>()
            .request(
                "worldRay",
                json!({"from":(center-normal*0.1).to_array(),
            "to":(center+normal*0.1).to_array()}),
            )
            .unwrap()["result"]
            .clone()
    };
    app.world_mut().run_system_once(upload).unwrap();
    assert_eq!(ray(&app, -4.)["hit"], true, "ordinary hull still uploads");
    assert_eq!(
        crew_ray(&app)["hit"],
        false,
        "source crew boxes remain managed; skin must not become a hull obstacle"
    );

    // Removing only the ancestry marker reproduces the old uploader with the
    // same real rig/mesh/pose and authority BVH, so an empty fixture cannot pass.
    for person in crew(&mut app) {
        app.world_mut().entity_mut(person).remove::<Crew>();
    }
    app.insert_resource(GeometryUploads::default());
    app.world_mut().run_system_once(upload).unwrap();
    let leaked = crew_ray(&app);
    assert_eq!(
        leaked["hit"], true,
        "negative control must intersect actual crew art"
    );
    assert_eq!(
        leaked["vehicleNetId"], journey.id,
        "the old ancestry path incorrectly labels the person as vehicle hull"
    );
}
