use super::*;
use crate::{
    content::ObjData,
    world::{ObjectCollider, WorldState, finite::FiniteWorld},
};
use bevy_rapier3d::prelude::ColliderDisabled;
use std::{fs, path::Path, sync::Arc};

struct Directory(std::path::PathBuf);
impl Drop for Directory {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}
fn tick(bridge: &CoreBridge) -> Value {
    let mut state = Value::Null;
    for _ in 0..12 {
        state = bridge.request("tick", json!({})).unwrap()["state"].clone();
    }
    state
}

#[test]
fn native_population_registration_waits_for_authority_then_consumes_once_across_geometry_refresh() {
    let project = Path::new(env!("CARGO_MANIFEST_DIR"));
    let temp = Directory(std::env::temp_dir().join(format!(
            "bevy-finite-population-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        )));
    let tables = temp.0.join("Maps/PEI/Spawns");
    fs::create_dir_all(&tables).unwrap();
    let mut bytes = vec![4, 1, 255, 255, 255, 4];
    bytes.extend(b"Food");
    bytes.extend([0, 0, 1, 4]);
    bytes.extend(b"Food");
    bytes.extend(1_f32.to_le_bytes());
    bytes.push(1);
    bytes.extend(13_u16.to_le_bytes());
    fs::write(tables.join("Items.dat"), bytes).unwrap();
    let config = WorldConfig {
        map_name: "Island_Test".into(),
        map_root: None,
        finite_population: Some(json!({"zombies":[],"animals":[],"vehicles":[
            {"kind":"sedan","variant":3,"position":[30.,2.1,20.],"yaw":143.}],
            "loot":[{"position":[20.,2.,18.],"table":0}]})),
        ..default()
    };
    let bridge = CoreBridge::launch(project).unwrap();
    bridge
        .request(
            "init",
            json!({"contentRoot":config.content_root,"mapId":config.map_name,"spawn":[20,2,20]}),
        )
        .unwrap();
    let mut options = crate::cli::Options::default();
    options.unturned = Some(temp.0.clone());
    let mut model = UiModel::default();
    model.connected = true;
    model.network.owns_world = false;
    let mut geometry = WorldGeometry::default();
    geometry.revision = 1;
    geometry.loaded_center = Vec3::new(20., 0., 20.);
    geometry.loaded_radius = 50.;
    geometry.terrain.push(crate::world::TerrainPatch {
        origin: Vec2::ZERO,
        cells: 1,
        spacing: 64.,
        heights: vec![1.; 4],
        layers: vec![0; 4],
        normals: vec![[0., 1., 0.]; 4],
        holes: vec![false],
        splat: None,
    });
    let services = ServiceManifest {
        map: config.map_name.clone(),
        holiday: Some(config.holiday.clone()),
        ..default()
    };
    let mut app = App::new();
    app.add_plugins(MinimalPlugins)
        .insert_resource(bridge)
        .insert_resource(crate::LaunchOptions(options))
        .insert_resource(config)
        .insert_resource(geometry)
        .insert_resource(model)
        .insert_resource(services)
        .insert_resource(WorldState {
            ready: true,
            ..default()
        })
        .init_resource::<Replicas>()
        .add_systems(Update, register_world_geometry);
    app.update();
    assert!(
        app.world()
            .resource::<WorldConfig>()
            .finite_population
            .is_some(),
        "client cannot consume/publish generated plans"
    );
    assert!(tick(app.world().resource::<CoreBridge>())["finiteVehicles"].is_null());
    app.world_mut().resource_mut::<UiModel>().network.owns_world = true;
    app.update();
    assert!(
        app.world()
            .resource::<WorldConfig>()
            .finite_population
            .is_none()
    );
    let state = tick(app.world().resource::<CoreBridge>());
    assert_eq!(state["worldItems"].as_array().unwrap().len(), 1);
    assert_eq!(
        crate::player::vector(&state["finiteVehicles"][0]["position"]).unwrap(),
        Vec3::new(30., 2.1, 20.)
    );
    let item = state["worldItems"][0]["netId"].clone();
    app.world()
        .resource::<CoreBridge>()
        .request("action", json!({"name":"pickupItem","args":{"netId":item}}))
        .unwrap();
    assert!(
        tick(app.world().resource::<CoreBridge>())["worldItems"]
            .as_array()
            .unwrap()
            .is_empty()
    );
    app.world_mut().resource_mut::<WorldGeometry>().revision += 1;
    app.update();
    let state = tick(app.world().resource::<CoreBridge>());
    assert!(
        state["worldItems"].as_array().unwrap().is_empty(),
        "stream refresh cannot replenish taken generated loot"
    );
    assert_eq!(state["finiteVehicles"].as_array().unwrap().len(), 1);
}

#[test]
fn finite_native_registration_uploads_source_ids_and_hides_accepted_forage_across_stream_refresh() {
    let project = Path::new(env!("CARGO_MANIFEST_DIR"));
    let temp = Directory(std::env::temp_dir().join(
        format!("bevy-finite-services-{}-{}",std::process::id(),
        std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos()),
    ));
    fs::create_dir_all(&temp.0).unwrap();
    fs::copy(
        project.join("../tests/UnturnedBevy.Tests/fixtures/pei-road-materials.dat"),
        temp.0.join("Roads.dat"),
    )
    .unwrap();
    let root = temp.0.join("map");
    let config = WorldConfig {
        finite_path: Some(root.clone()),
        map_name: "Island_Native Services".into(),
        map_root: None,
        ..default()
    };
    let bridge = CoreBridge::launch(project).unwrap();
    bridge
        .request(
            "finiteMap",
            json!({"directory":root,"contentRoot":config.content_root,"environmentRoot":temp.0,
        "name":"Native Services","seed":1234,"tilesX":1,"tilesZ":1,"create":true}),
        )
        .unwrap();
    fs::write(
        root.join("objects/editor_Native Services.txt"),
        concat!(
            "91dbbf923c8c401bb6b2d56084783f73 20 50 -20 270 180 0 1 1 1\n",
            "aa7fd5bc5e634a0c846770a28ff6cd65 30 50 -30 270 180 0 1 1 1\n"
        ),
    )
    .unwrap();
    bridge
        .request(
            "finiteMap",
            json!({"directory":root,"contentRoot":config.content_root}),
        )
        .unwrap();
    let finite = Arc::new(FiniteWorld::load(&root).unwrap());
    bridge
        .request(
            "init",
            json!({"contentRoot":config.content_root,"mapId":config.map_name,
        "spawn":[20,50,20],"allowCheats":true}),
        )
        .unwrap();
    let mut model = UiModel::default();
    model.connected = true;
    let mut geometry = WorldGeometry::default();
    geometry.finite = Some(finite.clone());
    geometry
        .exact_terrain
        .push(Arc::new(finite.chunk(0, 0).unwrap()));
    geometry.revision = 1;
    let mut app = App::new();
    app.add_plugins(MinimalPlugins)
        .insert_resource(bridge)
        .insert_resource(crate::LaunchOptions(crate::cli::Options::default()))
        .insert_resource(config)
        .insert_resource(geometry)
        .insert_resource(model)
        .insert_resource(WorldState {
            ready: true,
            sea_level: Some(25.6),
            ..default()
        })
        .init_resource::<ServiceManifest>()
        .init_resource::<Replicas>()
        .init_resource::<GameSnapshot>()
        .add_systems(Update, (register_world_geometry, sync_static_life).chain());
    app.update();
    assert!(app.world().resource::<UiModel>().connected);
    let manifest = app.world().resource::<ServiceManifest>();
    assert_eq!(manifest.objects.len(), 2);
    assert_eq!(manifest.object_doors.len(), 2);
    assert!(manifest.container_doors.is_empty());
    assert!(manifest.resources.len() > 400);
    let revision = manifest.revision;
    let (key, row) = manifest
        .resources
        .iter()
        .find(|(_, r)| r["rewardItemId"].as_u64().unwrap_or(0) > 0)
        .unwrap();
    let key = key.clone();
    let row = row.clone();
    let index = row["index"].as_u64().unwrap();
    let position: [f32; 3] = serde_json::from_value(row["position"].clone()).unwrap();
    let at = Vec3::from_array(position);
    let resource = app
        .world_mut()
        .spawn((
            crate::world::WorldResource {
                name: key.0.clone(),
                instance_index: key.1,
            },
            Visibility::Inherited,
        ))
        .id();
    // A small isolated probe surface carries a real source resource identity;
    // the production uploader must resolve it through the newly adopted map.
    let data = Arc::new(ObjData {
        positions: vec![[-1., 1., -1.], [0., 1., 1.], [1., 1., -1.]],
        ..default()
    });
    let matrix = Mat4::from_translation(at);
    {
        let mut geometry = app.world_mut().resource_mut::<WorldGeometry>();
        geometry.objects.push(ObjectCollider {
            source_index: None,
            resource_key: Some(key),
            min: at - Vec3::ONE,
            max: at + Vec3::ONE,
            transform: matrix,
            inverse: matrix.inverse(),
            geometry: data,
        });
        geometry.revision += 1;
    }
    app.update();
    let bridge = app.world().resource::<CoreBridge>();
    let ray = bridge
        .request(
            "worldRay",
            json!({"from":(at+Vec3::Y*2.).to_array(),"to":at.to_array()}),
        )
        .unwrap();
    assert_eq!(ray["result"]["resourceIndex"], index);
    let terrain = bridge
        .request("worldRay", json!({"from":[96,250,-96],"to":[96,-50,-96]}))
        .unwrap();
    assert_eq!(
        terrain["result"]["hit"], true,
        "exact source chunk reaches authority"
    );
    bridge
        .request(
            "action",
            json!({"name":"console","args":{"text":format!("teleport {} {} {}",at.x,at.y,at.z)}}),
        )
        .unwrap();
    tick(bridge);
    bridge
        .request(
            "action",
            json!({"name":"forageResource","args":{"index":index}}),
        )
        .unwrap();
    let snapshot = tick(bridge);
    assert!(
        snapshot["resources"]["dead"]
            .as_array()
            .unwrap()
            .contains(&json!(index))
    );
    app.world_mut().resource_mut::<GameSnapshot>().0 = snapshot;
    app.update();
    assert_eq!(
        app.world().get::<Visibility>(resource),
        Some(&Visibility::Hidden)
    );
    assert!(app.world().get::<ColliderDisabled>(resource).is_some());
    app.world_mut().resource_mut::<WorldGeometry>().revision += 1;
    app.update();
    assert_eq!(
        app.world().resource::<ServiceManifest>().revision,
        revision,
        "stream upload does not register/restock source fixtures again"
    );
    assert_eq!(
        app.world().get::<Visibility>(resource),
        Some(&Visibility::Hidden)
    );
}
