use super::*;
use crate::entities::{ItemCatalog, ItemVisual, Replicas, WorldItemReplica, sync_world_items};
use crate::{player::GameSnapshot, world::WorldConfig};
use serde_json::json;
fn app() -> App {
    let mut app = App::new();
    app.add_plugins((MinimalPlugins, AssetPlugin::default()))
        .init_asset::<Mesh>()
        .init_asset::<Image>()
        .init_asset::<StandardMaterial>()
        .init_resource::<Models>()
        .init_resource::<ContentCache>()
        .init_resource::<WorldConfig>()
        .init_resource::<Replicas>()
        .init_resource::<ItemCatalog>()
        .insert_resource(GameSnapshot(json!({"worldItems":[]})))
        .add_systems(Update, sync_world_items);
    for (id, kind, rarity, stack) in [
        (24, "shirt", "common", 1),
        (5004, "supply", "common", 128),
        (60000, "generic", "legendary", 1),
    ] {
        let item:ItemVisual=serde_json::from_value(json!({"id":id,"name":format!("item{id}"),"type":kind,"rarity":rarity,"stackSize":stack})).unwrap();
        app.world_mut()
            .resource_mut::<ItemCatalog>()
            .items
            .insert(id, item);
    }
    app
}
#[test]
fn real_dropped_shirt_has_its_manifest_mesh_material_mips_and_rotated_focus_box() {
    let mut app = app();
    app.world_mut().resource_mut::<GameSnapshot>().0 =
        json!({"worldItems":[{"netId":10,"itemId":24,"position":[4,5,6],"amount":1}]});
    app.update();
    let entity = app.world().resource::<Replicas>().entities[&10];
    let focus = app.world().get::<Focus>(entity).unwrap();
    assert!((focus.size - Vec3::new(0.9093, 1.7134, 0.1094) * 1.15).length() < 1e-5);
    assert_eq!(focus.center, Vec3::new(-0.0129, 0., 0.));
    let pose = app.world().get::<Transform>(entity).unwrap();
    let edge = pose.transform_point(focus.center + Vec3::Y * 0.8);
    assert!(
        focus
            .ray(pose, edge + Vec3::Y * 2., Vec3::NEG_Y, 6.)
            .is_some(),
        "shirt edge beyond old .6m origin radius is actually selectable"
    );
    assert!(
        focus
            .ray(pose, edge + Vec3::X * 3. + Vec3::Y * 2., Vec3::NEG_Y, 6.)
            .is_none()
    );
    let material = app
        .world()
        .resource::<Assets<StandardMaterial>>()
        .get(
            &app.world()
                .get::<MeshMaterial3d<StandardMaterial>>(entity)
                .unwrap()
                .0,
        )
        .unwrap();
    assert_eq!(material.perceptual_roughness, 0.8);
    assert!(material.cull_mode.is_none());
    let image = app
        .world()
        .resource::<Assets<Image>>()
        .get(material.base_color_texture.as_ref().unwrap())
        .unwrap();
    assert!(image.texture_descriptor.mip_level_count > 1);
    let ImageSampler::Descriptor(sampler) = &image.sampler else {
        panic!("source explicit nearest sampler")
    };
    assert_eq!(sampler.mag_filter, ImageFilterMode::Nearest);
    assert_eq!(sampler.mipmap_filter, ImageFilterMode::Nearest);
    let mesh = app
        .world()
        .resource::<Assets<Mesh>>()
        .get(&app.world().get::<Mesh3d>(entity).unwrap().0)
        .unwrap();
    assert!(
        mesh.count_vertices() > 36,
        "the real folded shirt, not the fallback box"
    );
}
#[test]
fn accepted_stack_amount_changes_triangle_prefix_without_replacing_entity_and_removal_cleans_up() {
    let mut app = app();
    let put = |app: &mut App, amount| {
        app.world_mut().resource_mut::<GameSnapshot>().0 =
            json!({"worldItems":[{"netId":9,"itemId":5004,"position":[0,0,0],"amount":amount}]});
        app.update();
    };
    put(&mut app, 1);
    let entity = app.world().resource::<Replicas>().entities[&9];
    let first = app.world().get::<Mesh3d>(entity).unwrap().0.clone();
    assert_eq!(
        app.world()
            .resource::<Assets<Mesh>>()
            .get(&first)
            .unwrap()
            .count_vertices(),
        22 * 3
    );
    put(&mut app, 43);
    assert_eq!(app.world().resource::<Replicas>().entities[&9], entity);
    let second = app.world().get::<Mesh3d>(entity).unwrap().0.clone();
    assert_eq!(
        app.world()
            .resource::<Assets<Mesh>>()
            .get(&second)
            .unwrap()
            .count_vertices(),
        44 * 3
    );
    put(&mut app, 128);
    assert_eq!(
        app.world()
            .resource::<Assets<Mesh>>()
            .get(&app.world().get::<Mesh3d>(entity).unwrap().0)
            .unwrap()
            .count_vertices(),
        110 * 3
    );
    put(&mut app, 1);
    assert_eq!(
        app.world().get::<Mesh3d>(entity).unwrap().0,
        first,
        "prefix mesh is reused"
    );
    app.world_mut().resource_mut::<GameSnapshot>().0 = json!({"worldItems":[]});
    app.update();
    assert!(app.world().get_entity(entity).is_err());
    assert!(app.world().resource::<Replicas>().entities.is_empty());
}
#[test]
fn missing_manifest_and_missing_catalog_items_remain_visible_and_focusable_source_boxes() {
    let mut app = app();
    app.world_mut().resource_mut::<GameSnapshot>().0 = json!({"worldItems":[{"netId":1,"itemId":60000,"position":[0,0,0],"amount":1},{"netId":2,"itemId":60001,"position":[1,0,0],"amount":1}]});
    app.update();
    assert_eq!(app.world().resource::<Replicas>().entities.len(), 2);
    for (&net_id, &entity) in &app.world().resource::<Replicas>().entities {
        assert!(app.world().get::<WorldItemReplica>(entity).is_some());
        assert_eq!(
            app.world().get::<Focus>(entity).unwrap().size,
            Vec3::splat(0.24 * 1.15)
        );
        let material = app
            .world()
            .resource::<Assets<StandardMaterial>>()
            .get(
                &app.world()
                    .get::<MeshMaterial3d<StandardMaterial>>(entity)
                    .unwrap()
                    .0,
            )
            .unwrap();
        assert_eq!(material.perceptual_roughness, 0.55);
        assert_eq!(
            material.base_color,
            if net_id == 1 {
                rarity_color("legendary")
            } else {
                Color::WHITE
            }
        );
    }
}
#[test]
fn original_amount_bands_include_lower_boundary_and_zero_means_full_single_model() {
    assert_eq!(visible_rounds(0, 32, 3), 3);
    assert_eq!(visible_rounds(1, 32, 3), 1);
    assert_eq!(visible_rounds(15, 32, 3), 1);
    assert_eq!(visible_rounds(16, 32, 3), 2);
    assert_eq!(visible_rounds(24, 32, 3), 3);
}
