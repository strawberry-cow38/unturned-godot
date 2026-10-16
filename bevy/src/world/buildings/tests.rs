use super::*;
use crate::bridge::CoreBridge;
use bevy::ecs::system::SystemState;
use serde_json::json;

mod visual;

fn source(request: serde_json::Value) -> View {
    let bridge = CoreBridge::launch(std::path::Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
    let mut request = request;
    request["contentRoot"] =
        json!(std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../game/content"));
    serde_json::from_value(bridge.request("buildingView", request).unwrap()["result"].clone())
        .unwrap()
}
fn app() -> App {
    let mut app = App::new();
    app.add_plugins((
        MinimalPlugins,
        TransformPlugin,
        AssetPlugin::default(),
        RapierPhysicsPlugin::<NoUserData>::default(),
    ))
    .init_asset::<Mesh>()
    .init_asset::<StandardMaterial>()
    .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(20),
    ));
    app
}
fn ray(app: &mut App, point: Vec3, groups: CollisionGroups) -> Option<Entity> {
    let mut state = SystemState::<ReadRapierContext>::new(app.world_mut());
    let context = state.get(app.world()).unwrap();
    context
        .single()
        .unwrap()
        .cast_ray(
            point,
            Vec3::NEG_Z,
            4.,
            true,
            QueryFilter::default().groups(groups),
        )
        .map(|x| x.0)
}

#[test]
fn original_wall_hole_and_trim_keep_real_query_layers_and_empty_replacement_retires_collision() {
    let view = source(json!({"text":"wall 0 0 0 0 6 .7 0 4 0 Wall\n  open 2 0 2 3 999 0\n"}));
    let mut app = app();
    let root = replace(app.world_mut(), &view).unwrap();
    app.update();
    app.update();
    let player = CollisionGroups::new(Group::GROUP_2, Group::GROUP_1 | Group::GROUP_4);
    assert!(
        ray(&mut app, Vec3::new(3., 1.5, 2.), player).is_none(),
        "actual opening is clear"
    );
    assert!(
        ray(&mut app, Vec3::new(1., 1.5, 2.), player).is_some(),
        "wall remains solid"
    );
    let actor = app
        .world_mut()
        .spawn((
            RigidBody::KinematicPositionBased,
            Collider::capsule_y(0.5, 0.35),
            Transform::from_xyz(3., 1.5, 2.),
            CollisionGroups::new(Group::GROUP_2, Group::GROUP_1 | Group::GROUP_4),
            KinematicCharacterController {
                translation: Some(Vec3::NEG_Z * 4.),
                filter_groups: Some(player),
                ..default()
            },
        ))
        .id();
    app.update();
    app.update();
    assert!(
        app.world().get::<Transform>(actor).unwrap().translation.z < -1.,
        "real capsule passes the source aperture"
    );
    app.world_mut().despawn(actor);
    let hit = ray(&mut app, Vec3::new(2.05, 1.5, 2.), player)
        .expect("source bit6 trim blocks actual player mask");
    assert!(app.world().get::<Trim>(hit).is_some());
    assert!(
        ray(
            &mut app,
            Vec3::new(2.05, 1.5, 2.),
            CollisionGroups::new(Group::GROUP_6, Group::GROUP_1)
        )
        .is_none(),
        "zombie world query ignores trim"
    );
    assert!(
        ray(
            &mut app,
            Vec3::new(2.05, 1.5, 2.),
            CollisionGroups::new(Group::GROUP_4, Group::GROUP_1 | Group::GROUP_4)
        )
        .is_none(),
        "vehicle wheel/body world query ignores trim"
    );
    let material = app.world().resource::<Assets<StandardMaterial>>();
    assert!(material.iter().any(|(_, m)| m.perceptual_roughness == 0.95));
    assert!(material.iter().any(|(_, m)| m.perceptual_roughness == 0.9));
    let mut invalid = view.clone();
    invalid.version = 99;
    assert!(replace(app.world_mut(), &invalid).is_err());
    assert!(
        app.world().get_entity(root).is_ok(),
        "invalid projection retains current scene"
    );
    let empty = source(json!({"text":"wall 0 0 0 0 6 .7 0 4 0 Floor\n  open 0 0 6 4 999 0\n"}));
    replace(app.world_mut(), &empty).unwrap();
    app.update();
    app.update();
    assert!(app.world().get_entity(root).is_err());
    assert!(
        ray(&mut app, Vec3::new(1., 1.5, 2.), player).is_none(),
        "all-holed replacement leaves no old collider"
    );
}

#[test]
fn source_flat_normals_and_native_front_winding_agree_on_every_projected_triangle() {
    let view = source(json!({"spec":{"kind":"Residential","seed":17,"minFloors":2,"maxFloors":2}}));
    let mut triangles = 0;
    for wall in &view.walls {
        for part in [&wall.front, &wall.back, &wall.trim] {
            let mesh = surface::mesh(part);
            let positions = mesh
                .attribute(Mesh::ATTRIBUTE_POSITION)
                .unwrap()
                .as_float3()
                .unwrap();
            let normals = mesh
                .attribute(Mesh::ATTRIBUTE_NORMAL)
                .unwrap()
                .as_float3()
                .unwrap();
            for (index, source) in part.triangles.chunks_exact(3).enumerate() {
                let [a, b, c] = [
                    Vec3::from(source[0]),
                    Vec3::from(source[1]),
                    Vec3::from(source[2]),
                ];
                let expected = (c - a).cross(b - a).normalize_or_zero();
                assert_eq!(positions[index * 3], source[0]);
                assert_eq!(positions[index * 3 + 1], source[2]);
                assert_eq!(positions[index * 3 + 2], source[1]);
                assert!((Vec3::from(normals[index * 3]) - expected).length() < 1e-6);
                triangles += 1;
            }
        }
    }
    assert!(triangles > 1000);
}

#[test]
fn actual_generated_ramp_body_keeps_source_basis_and_save_reopen_does_not_recreate_it() {
    let view = source(json!({"spec":{"kind":"Residential","seed":17,"minFloors":2,"maxFloors":2}}));
    let mut app = app();
    replace(app.world_mut(), &view).unwrap();
    app.update();
    app.update();
    let ramps: Vec<_> = app
        .world_mut()
        .query_filtered::<(Entity, &Transform, &Collider), With<Ramp>>()
        .iter(app.world())
        .map(|(e, t, c)| (e, *t, c.as_cuboid().unwrap().half_extents()))
        .collect();
    assert_eq!(ramps.len(), 2);
    let (entity, pose, half) = ramps[0];
    let mut state = SystemState::<ReadRapierContext>::new(app.world_mut());
    let context = state.get(app.world()).unwrap();
    let only = |e: Entity, _: &bevy_rapier3d::rapier::geometry::Collider| e == entity;
    let hit = context
        .single()
        .unwrap()
        .cast_ray_and_get_normal(
            pose.translation + Vec3::Y * 4.,
            Vec3::NEG_Y,
            8.,
            true,
            QueryFilter::default().predicate(&only),
        )
        .expect("invisible source ramp is a real physical surface");
    assert!(hit.1.normal.dot(Vec3::Y) > 0.5);
    assert!(half.z > half.x && half.y < 0.2);
    let saved = source(json!({"text":view.text}));
    replace(app.world_mut(), &saved).unwrap();
    app.update();
    assert_eq!(
        app.world_mut()
            .query_filtered::<Entity, With<Ramp>>()
            .iter(app.world())
            .count(),
        0
    );
}
