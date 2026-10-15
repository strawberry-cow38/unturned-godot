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
        .add_plugins(material::AlphaDepthPlugin)
        .configure_sets(Update, material::AlphaDepthUpdate.after(sync_world_items))
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
fn recovered_arrow_nose_follows_contact_without_changing_saved_root_or_ordinary_art() {
    use bevy::mesh::VertexAttributeValues;
    let mut app = app();
    let direction = Vec3::new(0.3, -0.1, -1.).normalize();
    for id in [347, 348, 351, 352] {
        let state = json!({"worldItems":[
            {"netId":10,"itemId":id,"amount":1,"position":[4,2,1]},
            {"netId":11,"itemId":id,"amount":1,"position":[4,2,1],"stuckDirection":direction.to_array()}
        ]});
        app.world_mut().resource_mut::<GameSnapshot>().0 = state.clone();
        app.update();
        let entities = &app.world().resource::<Replicas>().entities;
        let ordinary = entities[&10];
        let recovered = entities[&11];
        let ordinary_mesh = &app.world().get::<Mesh3d>(ordinary).unwrap().0;
        let recovered_mesh = &app.world().get::<Mesh3d>(recovered).unwrap().0;
        assert_ne!(ordinary_mesh, recovered_mesh);
        let meshes = app.world().resource::<Assets<Mesh>>();
        let VertexAttributeValues::Float32x3(original) = meshes
            .get(ordinary_mesh)
            .unwrap()
            .attribute(Mesh::ATTRIBUTE_POSITION)
            .unwrap()
        else {
            panic!("source arrow vertices");
        };
        let VertexAttributeValues::Float32x3(aligned) = meshes
            .get(recovered_mesh)
            .unwrap()
            .attribute(Mesh::ATTRIBUTE_POSITION)
            .unwrap()
        else {
            panic!("aligned arrow vertices");
        };
        let tip = original
            .iter()
            .enumerate()
            .max_by(|(_, a), (_, b)| a[1].total_cmp(&b[1]))
            .unwrap()
            .0;
        // Real content has a centered pointed nose at +Y .341786, fins below it.
        assert!((original[tip][1] - 0.341786).abs() < 1e-5);
        assert!(original[tip][0].abs() < 1e-5 && original[tip][2].abs() < 1e-5);
        let pose = app.world().get::<Transform>(recovered).unwrap();
        assert!((pose.rotation * Vec3::NEG_Z).distance(direction) < 1e-5);
        let nose = pose.rotation * Vec3::from_array(aligned[tip]).normalize();
        assert!(
            nose.distance(direction) < 1e-5,
            "actual mesh nose must follow flight for {id}"
        );
        let focus = app.world().get::<Focus>(recovered).unwrap();
        assert!(focus.size.z > 0.7 && focus.size.y < 0.04);
        let center = pose.transform_point(focus.center);
        assert!(
            focus
                .ray(pose, center - direction * 2., direction, 4.)
                .is_some()
        );
        assert_eq!(
            app.world().resource::<GameSnapshot>().0,
            state,
            "authority/save metadata is read-only"
        );
    }
}
#[test]
fn late_stuck_metadata_changes_only_known_arrow_art_and_removal_restores_cached_drop() {
    let mut app = app();
    let ordinary = json!({"worldItems":[{"netId":10,"itemId":347,"amount":1,"position":[1,2,3]}]});
    app.world_mut().resource_mut::<GameSnapshot>().0 = ordinary.clone();
    app.update();
    let entity = app.world().resource::<Replicas>().entities[&10];
    let original_mesh = app.world().get::<Mesh3d>(entity).unwrap().0.clone();
    app.world_mut().resource_mut::<GameSnapshot>().0["worldItems"][0]["stuckDirection"] =
        json!([0, 0, -1]);
    app.update();
    let aligned_mesh = app.world().get::<Mesh3d>(entity).unwrap().0.clone();
    assert_ne!(aligned_mesh, original_mesh);
    app.update();
    assert_eq!(
        app.world().get::<Mesh3d>(entity).unwrap().0,
        aligned_mesh,
        "cached geometry reused"
    );
    app.world_mut().resource_mut::<GameSnapshot>().0 = ordinary;
    app.update();
    assert_eq!(app.world().get::<Mesh3d>(entity).unwrap().0, original_mesh);
    for value in [
        json!({}),
        json!({"stuckEuler":[1.57,0.3,0]}),
        json!({"stuckDirection":[0,0,0]}),
    ] {
        assert!(!aligned_arrow(&value, 347));
    }
    assert!(
        !aligned_arrow(&json!({"stuckDirection":[0,0,-1]}), 1166),
        "no guessed magazine or mod axis"
    );
}
#[test]
fn accepted_stuck_arrow_keeps_look_at_across_updates_new_ids_and_origin_shifts() {
    let mut app = app();
    // Source LookAt begins 5cm outside the target and aims at hit + direction.
    let direction = Vec3::new(0.2, -0.1, -1.05).normalize();
    let put = |app: &mut App, id, position: [f32; 3]| {
        app.world_mut().resource_mut::<GameSnapshot>().0 = json!({"worldItems":[{
            "netId":id,"itemId":347,"amount":1,"quality":100,"position":position,
            "settled":true,"stuckDirection":direction.to_array(),"stuckNormal":[0,0,1]
        }]});
        app.update();
    };
    put(&mut app, 10, [4., 1., -2.95]);
    let entity = app.world().resource::<Replicas>().entities[&10];
    let pose = *app.world().get::<Transform>(entity).unwrap();
    assert!((pose.rotation * Vec3::NEG_Z).distance(direction) < 1e-5);
    let focus = app.world().get::<Focus>(entity).unwrap();
    let center = pose.transform_point(focus.center);
    assert!(
        focus
            .ray(&pose, center - direction * 2., direction, 4.)
            .is_some(),
        "frozen recovered ammo remains an ordinary focusable pickup"
    );
    // Update a live unchanged item: that early return must still adopt rotation.
    app.world_mut()
        .get_mut::<Transform>(entity)
        .unwrap()
        .rotation = Quat::IDENTITY;
    put(&mut app, 10, [-1020., 1., -2.95]);
    assert_eq!(app.world().resource::<Replicas>().entities[&10], entity);
    let shifted = app.world().get::<Transform>(entity).unwrap();
    assert!((shifted.rotation * Vec3::NEG_Z).distance(direction) < 1e-5);
    assert_eq!(shifted.translation, Vec3::new(-1020., 1., -2.95));
    put(&mut app, 29, [-1020., 1., -2.95]);
    assert!(app.world().get_entity(entity).is_err());
    let loaded = app.world().resource::<Replicas>().entities[&29];
    assert!(
        app.world()
            .get::<Transform>(loaded)
            .unwrap()
            .rotation
            .abs_diff_eq(pose.rotation, 1e-5)
    );
    app.world_mut().resource_mut::<GameSnapshot>().0 = json!({"worldItems":[]});
    app.update();
    assert!(app.world().get_entity(loaded).is_err());
}
#[test]
fn vertical_stuck_arrow_uses_captured_yxz_euler_while_unmarked_drops_keep_existing_pose() {
    let mut app = app();
    let euler = Vec3::new(1.62, 0.73, -0.2);
    app.world_mut().resource_mut::<GameSnapshot>().0 = json!({"worldItems":[
        {"netId":10,"itemId":347,"amount":1,"position":[1,2,3],"settled":true,"stuckDirection":null,"stuckEuler":euler.to_array()},
        {"netId":11,"itemId":347,"amount":1,"position":[4,5,6],"settled":true}
    ]});
    app.update();
    let entities = &app.world().resource::<Replicas>().entities;
    let vertical = app.world().get::<Transform>(entities[&10]).unwrap();
    let expected = Quat::from_rotation_y(euler.y)
        * Quat::from_rotation_x(euler.x)
        * Quat::from_rotation_z(euler.z);
    assert!(vertical.rotation.abs_diff_eq(expected, 1e-5));
    let ordinary = app.world().get::<Transform>(entities[&11]).unwrap();
    let expected = Quat::from_euler(
        EulerRot::YXZ,
        (11 * 137 % 360) as f32 * std::f32::consts::PI / 180.,
        std::f32::consts::FRAC_PI_2,
        0.,
    );
    assert!(ordinary.rotation.abs_diff_eq(expected, 1e-5));
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

#[test]
fn accepted_translucent_item_uses_real_texture_thresholds_and_replacing_with_clothing_retires_both_companions()
 {
    let mut app = app();
    let ids = [9301u16, 9317, 9318, 9319, 9343, 9347];
    app.world_mut().resource_mut::<GameSnapshot>().0 = json!({"worldItems":ids.into_iter().enumerate()
        .map(|(i,id)| json!({"netId":i+1,"itemId":id,"position":[i,0,0],"amount":1})).collect::<Vec<_>>()});
    app.update();
    for (index, id) in ids.into_iter().enumerate() {
        let entity = app.world().resource::<Replicas>().entities[&((index + 1) as u32)];
        assert!(
            app.world().get::<material::AlphaDepth>(entity).is_some(),
            "manifest {id}"
        );
        assert_eq!(app.world().get::<Children>(entity).unwrap().len(), 2);
        let source = app
            .world()
            .resource::<Assets<StandardMaterial>>()
            .get(
                &app.world()
                    .get::<MeshMaterial3d<StandardMaterial>>(entity)
                    .unwrap()
                    .0,
            )
            .unwrap();
        let image = app
            .world()
            .resource::<Assets<Image>>()
            .get(source.base_color_texture.as_ref().unwrap())
            .unwrap();
        let pixels = &image.data.as_ref().unwrap()[..(image.width() * image.height() * 4) as usize];
        let opaque = pixels.chunks_exact(4).filter(|p| p[3] >= 253).count();
        assert_eq!(opaque > 0, id != 9301 && id != 9347);
        assert!(
            pixels.chunks_exact(4).all(|p| p[3] >= 26),
            "all original {id} texels cast a source shadow"
        );
    }
    let entity = app.world().resource::<Replicas>().entities[&1];
    let children: Vec<_> = app
        .world()
        .get::<Children>(entity)
        .unwrap()
        .iter()
        .collect();
    app.world_mut().resource_mut::<GameSnapshot>().0 =
        json!({"worldItems":[{"netId":1,"itemId":24,"position":[2,3,4],"amount":1}]});
    app.update();
    assert_eq!(app.world().resource::<Replicas>().entities[&1], entity);
    assert!(app.world().get::<material::AlphaDepth>(entity).is_none());
    for child in children {
        assert!(app.world().get_entity(child).is_err());
    }
    assert_eq!(
        app.world_mut()
            .query::<&MeshMaterial3d<material::DepthMaterial>>()
            .iter(app.world())
            .count(),
        0
    );
    assert_eq!(
        app.world_mut()
            .query::<&MeshMaterial3d<material::ShadowMaterial>>()
            .iter(app.world())
            .count(),
        0
    );
}
