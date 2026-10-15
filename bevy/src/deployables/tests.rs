use super::*;
use crate::content::ObjData;
use geometry::{Bounds, seat};
use std::path::Path;

fn catalog(bridge: &CoreBridge) -> Vec<Definition> {
    serde_json::from_value(
        bridge.request("catalog", json!({})).unwrap()["result"]["deployables"].clone(),
    )
    .unwrap()
}
fn initialize() -> CoreBridge {
    let bridge = CoreBridge::launch(Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
    bridge
        .request(
            "init",
            json!({"contentRoot":WorldConfig::default().content_root,
        "name":"deployable-replica-proof","spawn":[0,5,0],"allowCheats":true}),
        )
        .unwrap();
    bridge
}
fn tick(bridge: &CoreBridge) -> Value {
    let mut state = Value::Null;
    for _ in 0..12 {
        state = bridge.request("tick", json!({})).unwrap()["state"].clone();
    }
    state
}
fn fixture(bridge: CoreBridge, state: Value) -> App {
    let mut defs = ItemCatalog::default();
    defs.deployables = catalog(&bridge);
    let mut model = UiModel::default();
    model.adopt_snapshot(&state);
    let mut app = App::new();
    app.add_plugins((MinimalPlugins, AssetPlugin::default(), TransformPlugin))
        .init_asset::<Mesh>()
        .init_asset::<Image>()
        .init_asset::<StandardMaterial>()
        .init_resource::<ContentCache>()
        .init_resource::<WorldConfig>()
        .init_resource::<WorldGeometry>()
        .init_resource::<WorldSessionEpoch>()
        .insert_resource(bridge)
        .insert_resource(GameSnapshot(state))
        .insert_resource(defs)
        .insert_resource(model)
        .add_plugins(DeployablePlugin);
    app
}

#[test]
fn original_catalog_routes_specialized_bodies_and_materializes_every_ordinary_mesh() {
    let bridge = initialize();
    let defs = catalog(&bridge);
    assert_eq!(defs.len(), 61);
    for id in [9200, 9201, 9106, 9130, 9110, 9112, 9114] {
        assert!(
            !defs.iter().find(|d| d.id == id).unwrap().ordinary(),
            "special body #{id}"
        );
    }
    let mut app = fixture(bridge, json!({"deployables":[]}));
    let world = app.world_mut();
    let asset_server = world.resource::<AssetServer>().clone();
    let mut cache = ContentCache::default();
    let mut meshes = Assets::<Mesh>::default();
    let mut materials = Assets::<StandardMaterial>::default();
    for def in defs.iter().filter(|d| d.ordinary()) {
        let art = presentation::load(
            def,
            &WorldConfig::default().content_root,
            &mut cache,
            &asset_server,
            &mut meshes,
            &mut materials,
        )
        .unwrap_or_else(|e| panic!("{}: {e}", def.name));
        assert!(art.bounds.min.is_finite());
        let material = materials.get(&art.material_for_test()).unwrap();
        if !def.proc_box {
            assert!(material.double_sided);
            assert!(material.cull_mode.is_none());
        }
    }
}

#[test]
fn replicated_ceiling_wall_and_top_adapter_use_the_original_surface_seat() {
    let bridge = initialize();
    let defs = catalog(&bridge);
    let bounds = Bounds {
        min: Vec3::new(-1., -2., -3.),
        max: Vec3::new(1., 2., 3.),
    };
    let point = Vec3::new(10., 20., 30.);
    let pose = Pose {
        point,
        yaw: 90.,
        mount_up: false,
    };
    let ceiling = defs.iter().find(|d| d.id == 9210).unwrap();
    let p = seat(ceiling, bounds, pose);
    assert!(
        (p.translation - (point - Vec3::Y * 0.005)).length() < 0.00001,
        "ceiling must not receive the floor GroundLift"
    );
    let wall = defs.iter().find(|d| d.id == 1222).unwrap();
    let p = seat(wall, bounds, pose);
    assert!((p.translation - (point + Vec3::X * wall.offset.min(0.1))).length() < 0.00001);
    let adapter = defs.iter().find(|d| d.id == 9216).unwrap();
    let p = seat(
        adapter,
        bounds,
        Pose {
            mount_up: true,
            ..pose
        },
    );
    assert!((p.translation - (point + Vec3::Y * (adapter.size.y * 0.5 + 0.01))).length() < 0.00001);
    assert!(
        (p.rotation * Vec3::Y - Vec3::Y).length() < 0.00001,
        "flat model's front must face up on a container top"
    );
    let bench = defs.iter().find(|d| d.id == 1916).unwrap();
    let p = seat(bench, bounds, pose);
    let floor = bounds
        .corners()
        .into_iter()
        .map(|v| p.transform_point(v).y)
        .fold(f32::INFINITY, f32::min);
    assert!((floor - point.y).abs() < 0.00001);
}

#[test]
fn all_ordinary_bounds_and_seats_match_141_godot_math_reference_cases() {
    let bridge = initialize();
    let defs = catalog(&bridge);
    let rows: Vec<Value> = serde_json::from_str(include_str!("source-reference.json")).unwrap();
    assert_eq!(rows.len(), 141);
    for row in rows {
        let def = defs
            .iter()
            .find(|d| Some(d.id as u64) == row["id"].as_u64())
            .unwrap();
        let size = Vec3::from(def.size);
        let raw = if def.proc_box {
            Bounds {
                min: size * -0.5,
                max: size * 0.5,
            }
        } else {
            let data = ObjData::load(
                &WorldConfig::default()
                    .content_root
                    .join(format!("objects/{}.obj", def.model.as_ref().unwrap())),
            )
            .unwrap();
            Bounds {
                min: data.min,
                max: data.max,
            }
        };
        let bounds = raw.rotated(geometry::mesh_rotation(def));
        let expect = |key: &str| crate::player::vector(&row[key]).unwrap();
        assert!(
            bounds.min.distance(expect("min")) < 0.00005,
            "{} min",
            def.name
        );
        assert!(
            bounds.max.distance(expect("max")) < 0.00005,
            "{} max",
            def.name
        );
        let pose = Pose {
            point: Vec3::new(10., 20., 30.),
            yaw: row["yaw"].as_f64().unwrap() as f32,
            mount_up: row["mountUp"].as_bool().unwrap(),
        };
        let actual = seat(def, bounds, pose);
        assert!(
            actual.translation.distance(expect("position")) < 0.00005,
            "{} seat",
            def.name
        );
        let expected = &row["basis"];
        for (i, axis) in [Vec3::X, Vec3::Y, Vec3::Z].into_iter().enumerate() {
            assert!(
                (actual.rotation * axis).distance(crate::player::vector(&expected[i]).unwrap())
                    < 0.00001,
                "{} basis{} yaw{} up{}",
                def.name,
                i,
                pose.yaw,
                pose.mount_up
            );
        }
    }
}

#[test]
fn accepted_workbench_builds_one_body_uploads_exact_box_and_retires_on_remove_and_epoch() {
    let bridge = initialize();
    bridge
        .request(
            "action",
            json!({"name":"console","args":{"text":"give 1916"}}),
        )
        .unwrap();
    tick(&bridge);
    bridge
        .request(
            "action",
            json!({"name":"placeDeployable","args":{"defId":1916,"pos":[0,5,-2],"yawDegrees":37}}),
        )
        .unwrap();
    let state = tick(&bridge);
    let row = state["deployables"]
        .as_array()
        .unwrap()
        .iter()
        .find(|d| d["defId"] == 1916)
        .expect("accepted placement");
    let id = row["netId"].as_u64().unwrap() as u32;
    let mut app = fixture(bridge, state);
    app.update();
    app.update();
    let mut q = app
        .world_mut()
        .query::<(Entity, &Deployed, &Transform, &Collider)>();
    let rows: Vec<_> = q.iter(app.world()).collect();
    assert_eq!(rows.len(), 1);
    let (entity, deployed, transform, collider) = rows[0];
    assert_eq!((deployed.net_id, deployed.def_id), (id, 1916));
    let compound = collider.as_compound().expect("original offset box");
    assert_eq!(compound.raw.shapes().len(), 1);
    let bench = app
        .world()
        .resource::<ItemCatalog>()
        .deployables
        .iter()
        .find(|d| d.id == 1916)
        .unwrap();
    let source = ObjData::load(
        &WorldConfig::default()
            .content_root
            .join("objects/Workbench_0.obj"),
    )
    .unwrap();
    let bounds = Bounds {
        min: source.min,
        max: source.max,
    }
    .rotated(geometry::mesh_rotation(bench));
    let center = transform.transform_point((bounds.min + bounds.max) * 0.5);
    let ray = app
        .world()
        .resource::<CoreBridge>()
        .request(
            "worldRay",
            json!({
        "from":(center+Vec3::Y*10.).to_array(),"to":(center-Vec3::Y*10.).to_array()}),
        )
        .unwrap();
    assert_eq!(
        ray["result"]["deployableNetId"], id,
        "native authority must see the source box"
    );
    assert_eq!(app.world().resource::<Replicas>().published.len(), 1);
    let generation = app.world().resource::<WorldGeometry>().revision;
    app.world_mut().resource_mut::<WorldGeometry>().revision = generation + 1;
    app.update();
    assert_eq!(
        app.world().resource::<Replicas>().published[&id].2,
        generation + 1,
        "terrain replacement clears dynamic geometry and must force reupload"
    );
    app.world_mut().resource_mut::<GameSnapshot>().0["deployables"] = json!([]);
    app.update();
    assert!(app.world().get_entity(entity).is_err());
    assert!(app.world().resource::<Replicas>().published.is_empty());
    let ray = app
        .world()
        .resource::<CoreBridge>()
        .request(
            "worldRay",
            json!({
        "from":(center+Vec3::Y*10.).to_array(),"to":(center-Vec3::Y*10.).to_array()}),
        )
        .unwrap();
    assert_eq!(
        ray["result"]["hit"], false,
        "missing replica retires host collision too"
    );
    // A freshly accepted snapshot can reuse a net ID after a session reset.
    let state = tick(app.world().resource::<CoreBridge>());
    app.world_mut().resource_mut::<GameSnapshot>().0 = state;
    app.update();
    let previous = app.world().resource::<Replicas>().instances[&id].entity;
    app.world_mut().resource_mut::<WorldSessionEpoch>().0 += 1;
    app.update();
    assert!(app.world().get_entity(previous).is_err());
    assert_eq!(
        app.world_mut()
            .query::<&Deployed>()
            .iter(app.world())
            .count(),
        1
    );
}

#[test]
fn udp_owner_materializes_accepted_workbench_without_publishing_remote_authority() {
    let port = std::net::UdpSocket::bind("127.0.0.1:0")
        .unwrap()
        .local_addr()
        .unwrap()
        .port();
    let project = Path::new(env!("CARGO_MANIFEST_DIR"));
    let host = CoreBridge::launch(project).unwrap();
    host.request(
        "init",
        json!({"contentRoot":WorldConfig::default().content_root,
        "listenPort":port,"allowCheats":true,"spawn":[0,5,0]}),
    )
    .unwrap();
    let peer = CoreBridge::launch(project).unwrap();
    peer.request(
        "init",
        json!({"contentRoot":WorldConfig::default().content_root,
        "connectHost":"127.0.0.1","connectPort":port}),
    )
    .unwrap();
    let pump = |peer: &CoreBridge, count| {
        let mut state = Value::Null;
        for _ in 0..count {
            state = peer.request("tick", json!({})).unwrap()["state"].clone();
            host.request("tick", json!({})).unwrap();
            std::thread::sleep(std::time::Duration::from_millis(1));
        }
        state
    };
    pump(&peer, 30);
    peer.request(
        "action",
        json!({"name":"console","args":{"text":"give 1916"}}),
    )
    .unwrap();
    let state = pump(&peer, 20);
    let position = crate::player::vector(&state["player"]["position"]).unwrap() - Vec3::Z * 2.;
    peer.request(
        "action",
        json!({"name":"placeDeployable","args":{
        "defId":1916,"pos":position.to_array(),"yawDegrees":37}}),
    )
    .unwrap();
    let state = pump(&peer, 20);
    let row = state["deployables"]
        .as_array()
        .unwrap()
        .iter()
        .find(|r| r["defId"] == 1916)
        .unwrap();
    let id = row["netId"].as_u64().unwrap() as u32;
    assert!(!crate::network::owns_world(&state));
    let mut app = fixture(peer, state);
    app.update();
    app.update();
    let replicas = app.world().resource::<Replicas>();
    let entity = replicas.instances[&id].entity;
    assert!(
        replicas.published.is_empty(),
        "a UDP renderer must not call host world geometry APIs"
    );
    let original = app.world().get::<Transform>(entity).unwrap().translation;
    // The session origin barrier delivers a new local-frame snapshot. The same
    // accepted ID updates its existing body exactly once in that frame.
    app.world_mut().resource_mut::<GameSnapshot>().0["deployables"][0]["position"][0] =
        json!(position.x - 256.);
    app.update();
    let moved = app.world().get::<Transform>(entity).unwrap().translation;
    assert!((moved - (original - Vec3::X * 256.)).length() < 0.001);
    assert!(app.world().resource::<Replicas>().published.is_empty());
}
