use super::*;
use crate::world::{WorldConfig, WorldGeometry, WorldSourceCache, WorldState, WorldTerrain};
use bevy::{asset::AssetPlugin, ecs::system::SystemState, image::ImagePlugin};
use bevy_rapier3d::prelude::*;

#[derive(Resource)]
struct Rebuild(bool);

fn terrain_hit(app: &mut App, at: Vec2) -> Option<Entity> {
    let mut state: SystemState<ReadRapierContext> = SystemState::new(app.world_mut());
    state
        .get(app.world())
        .unwrap()
        .single()
        .unwrap()
        .cast_ray(
            Vec3::new(at.x, 250., at.y),
            Vec3::NEG_Y,
            1000.,
            true,
            QueryFilter::default(),
        )
        .map(|(e, _)| e)
}

fn rebuild(app: &mut App, reopen: bool) {
    if reopen {
        app.world_mut().resource_mut::<WorldState>().ready = false;
    }
    app.world_mut().resource_mut::<Rebuild>().0 = true;
    app.update();
    app.world_mut().resource_mut::<Rebuild>().0 = false;
    for _ in 0..3 {
        app.update();
    }
}

#[test]
fn source_empty_chunk_retires_native_mesh_and_rapier_collision_then_refill_and_stream_restore() {
    let project = Path::new(env!("CARGO_MANIFEST_DIR"));
    let temp = Directory(std::env::temp_dir().join(
        format!("bevy-finite-lifecycle-{}-{}",std::process::id(),
        std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos()),
    ));
    fs::create_dir_all(&temp.0).unwrap();
    fs::copy(
        project.join("../tests/UnturnedBevy.Tests/fixtures/pei-road-materials.dat"),
        temp.0.join("Roads.dat"),
    )
    .unwrap();
    let root = temp.0.join("map");
    let bridge = CoreBridge::launch(project).unwrap();
    bridge.request("finiteMap",json!({"directory":root,"contentRoot":project.join("../game/content"),"environmentRoot":temp.0,
        "name":"Lifecycle","seed":1234,"tilesX":1,"tilesZ":1,"create":true})).unwrap();
    // Keep the real generated terrain, but author a deliberately empty scene at
    // the first chunk so no road/prop can hide a surviving terrain collider.
    fs::write(root.join("objects/editor_Lifecycle.txt"), "").unwrap();
    fs::remove_file(root.join("roads/editor_Lifecycle_Paths.dat")).unwrap();
    fs::write(
        root.join("spawns/editor_Lifecycle_player.txt"),
        "96 30 -96 0 0 0\n",
    )
    .unwrap();
    let reopen = |bridge: &CoreBridge| {
        bridge
            .request(
                "finiteMap",
                json!({"directory":root,"contentRoot":project.join("../game/content")}),
            )
            .unwrap();
    };
    reopen(&bridge);
    let mut app = App::new();
    app.add_plugins((
        MinimalPlugins,
        AssetPlugin::default(),
        ImagePlugin::default(),
        TransformPlugin,
        RapierPhysicsPlugin::<NoUserData>::default(),
    ))
    .init_asset::<Mesh>()
    .init_asset::<StandardMaterial>()
    .insert_resource(bridge)
    .insert_resource(crate::LaunchOptions(crate::cli::Options::default()))
    .insert_resource(WorldConfig {
        map_root: None,
        finite_path: Some(root.clone()),
        map_name: "Island_Lifecycle".into(),
        fixed_camera: Some((bevy::math::DVec3::new(96., 120., -96.), 0., 0.)),
        spawn_radius: 16.,
        foliage: false,
        ..default()
    })
    .init_resource::<WorldState>()
    .init_resource::<WorldGeometry>()
    .init_resource::<WorldSourceCache>()
    .init_resource::<crate::content::ContentCache>()
    .init_resource::<crate::world::origin::WorldOrigin>()
    .insert_resource(Rebuild(false))
    .add_systems(
        Update,
        crate::world::build_world.run_if(|r: Res<Rebuild>| r.0),
    );
    rebuild(&mut app, true);
    let original =
        terrain_hit(&mut app, Vec2::new(96., -96.)).expect("actual source chunk must collide");
    assert!(app.world().get::<WorldTerrain>(original).is_some());
    let original_chunks = app.world().resource::<WorldGeometry>().exact_terrain.len();
    assert!(original_chunks > 0);
    assert!(app.world().get::<Mesh3d>(original).is_some());
    fs::write(
        root.join("objects/editor_Lifecycle.txt"),
        concat!(
            "cb0d8bf87fca47e3b73f634959a9f523 120 30 96 0 0 0 1 1 1\n",
            "bd1554d0c8564890a852dd34932d16d3 124 30 96 0 0 0 1 1 1\n",
            "abf653b7838a4b47aa83c5119ed5a0e7 128 30 96 0 0 0 1 1 1\n",
            "01145113ecff42dbb9eb5b3b0c00b69d 132 30 96 0 0 0 1 1 1\n"
        ),
    )
    .unwrap();
    // Original x-major, bit-packed quad mask. Only chunk 0,0 is fully dug.
    let mut holes = Vec::new();
    u32_(&mut holes, 256);
    u32_(&mut holes, 256);
    let mut bits = vec![0_u8; 256 * 256 / 8];
    for x in 0..48 {
        for z in 0..48 {
            let i = x * 256 + z;
            bits[i / 8] |= 1 << (i % 8);
        }
    }
    holes.extend(bits);
    let path = root.join("terrain/editor_Lifecycle_heightmap.bin.holes");
    fs::write(&path, holes).unwrap();
    reopen(app.world().resource::<CoreBridge>());
    rebuild(&mut app, true);
    assert!(
        app.world().get_entity(original).is_err(),
        "old visible/physical chunk retired"
    );
    assert_eq!(
        app.world().resource::<WorldGeometry>().exact_terrain.len(),
        original_chunks - 1,
        "empty source chunk is omitted while adjacent chunks remain"
    );
    assert!(
        terrain_hit(&mut app, Vec2::new(96., -96.)).is_none(),
        "old Rapier collider must disappear"
    );
    assert_eq!(
        app.world_mut()
            .query::<&crate::world::WorldObject>()
            .iter(app.world())
            .filter(|p| p.asset_name == "Crate_0")
            .count(),
        1,
        "reopen reads edited prop file instead of cached empty placements"
    );
    assert_eq!(
        app.world().resource::<WorldState>().object_count,
        4,
        "editor authored props are not hidden by retail holiday filtering"
    );
    let geometry = app.world().resource::<WorldGeometry>();
    let ladder = geometry
        .objects
        .iter()
        .find(|p| p.source_index == Some(1))
        .unwrap();
    let original_ladder =
        ObjData::load(&project.join("../game/content/objects/Ladder_Wood_0.obj")).unwrap();
    assert_eq!(
        ladder.geometry.positions, original_ladder.positions,
        "editor ladder uses original triangles, not retail rung-filling box"
    );
    assert!(
        geometry.objects.iter().any(|p| p.source_index == Some(2)),
        "editor hedge remains collidable"
    );
    fs::write(root.join("objects/editor_Lifecycle.txt"), "").unwrap();
    fs::remove_file(path).unwrap();
    reopen(app.world().resource::<CoreBridge>());
    rebuild(&mut app, true);
    assert!(
        terrain_hit(&mut app, Vec2::new(96., -96.)).is_some(),
        "source refill must restore collision"
    );
    assert_eq!(
        app.world_mut()
            .query::<&crate::world::WorldObject>()
            .iter(app.world())
            .count(),
        0
    );
    app.world_mut()
        .resource_mut::<WorldConfig>()
        .fixed_camera
        .as_mut()
        .unwrap()
        .0
        .x = 700.;
    rebuild(&mut app, false);
    assert!(
        terrain_hit(&mut app, Vec2::new(96., -96.)).is_none(),
        "streaming retires old chunk"
    );
    assert!(
        terrain_hit(&mut app, Vec2::new(700., -96.)).is_some(),
        "streamed source chunk collides"
    );
    assert_eq!(
        app.world().resource::<WorldState>().spawn.x,
        96.,
        "streaming does not reset spawn"
    );
}

#[test]
fn source_chart_pixels_survive_png_encoding_and_reopen_does_not_rebake() {
    use bevy::{
        asset::RenderAssetUsages,
        image::{CompressedImageFormats, ImageSampler, ImageType},
    };
    let temp =
        Directory(std::env::temp_dir().join(format!("bevy-finite-chart-{}", std::process::id())));
    fs::create_dir_all(&temp.0).unwrap();
    let rgb: Vec<u8> = (0..1024 * 1024)
        .flat_map(|i| [(i % 256) as u8, ((i / 256) % 256) as u8, 17])
        .collect();
    let raw = temp.0.join("island_5_map.rgb");
    let png = temp.0.join("island_5_map.png");
    fs::write(&raw, &rgb).unwrap();
    bootstrap::persist_chart(&temp.0, 5).unwrap();
    let encoded = fs::read(&png).unwrap();
    let decoded = Image::from_buffer(
        &encoded,
        ImageType::Extension("png"),
        CompressedImageFormats::NONE,
        true,
        ImageSampler::Default,
        RenderAssetUsages::MAIN_WORLD,
    )
    .unwrap();
    assert_eq!(
        decoded.data.unwrap(),
        rgb.chunks_exact(3)
            .flat_map(|p| [p[0], p[1], p[2], 255])
            .collect::<Vec<_>>()
    );
    fs::write(&raw, [0]).unwrap();
    bootstrap::persist_chart(&temp.0, 5).unwrap();
    assert_eq!(
        fs::read(&png).unwrap(),
        encoded,
        "reopen preserves original saved chart"
    );
    fs::remove_file(&png).unwrap();
    assert!(bootstrap::persist_chart(&temp.0, 5).is_err());
    assert!(
        !png.exists(),
        "invalid raw image cannot create a partial PNG"
    );
}
