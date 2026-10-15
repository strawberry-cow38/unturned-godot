use super::*;
use bevy::ecs::system::RunSystemOnce;

#[test]
fn exact_source_fruit_box_uses_radial_scale_and_tilt_without_physical_collider() {
    let pose = Transform::from_xyz(7., 3., -9.)
        .with_rotation(Quat::from_euler(EulerRot::YXZ, 0.4, -0.7, 0.2))
        .with_scale(Vec3::new(2., 3., 4.));
    let fruit = Focus::source("Bush_Jade", pose.scale);
    let center = pose.translation + pose.rotation * Vec3::new(-0.114 * 4., 1.141 * 3., 0.064 * 4.);
    let direction = pose.rotation * Vec3::NEG_Z;
    let from = center - direction * 10.;
    assert!(
        (fruit.ray(&pose, from, direction, 20.).unwrap() - (10. - 2.30 * 4. * 0.5)).abs() < 0.00001
    );
    assert!(
        fruit.ray(&pose, from, direction, 5.).is_none(),
        "nearer wall occludes fruit"
    );
    assert!(
        fruit
            .ray(&pose, from + pose.rotation * Vec3::X * 5., direction, 20.)
            .is_none(),
        "large leaf cards are not a forage target"
    );
    let mushroom = Focus::source("Mushroom_Brown_0", Vec3::ONE);
    assert!(
        (mushroom
            .ray(
                &Transform::IDENTITY,
                Vec3::new(0., 0.11, 2.),
                Vec3::NEG_Z,
                3.
            )
            .unwrap()
            - 1.6)
            .abs()
            < 0.00001
    );
}

#[test]
fn accepted_dead_bitmap_retires_only_fruit_and_focus_then_regrows_without_duplicate_shapes() {
    let mut app = App::new();
    app.add_plugins(MinimalPlugins)
        .init_resource::<ServiceManifest>()
        .init_resource::<GameSnapshot>()
        .init_resource::<ItemCatalog>()
        .init_resource::<InteractionFocus>()
        .add_systems(Update, (super::super::sync_static_life, prepare).chain());
    for (name, index, reward) in [
        ("Bush_Jade", 9, 272),
        ("Mushroom_Brown_0", 10, 1932),
        ("Bush_0", 11, 0),
    ] {
        app.world_mut()
            .resource_mut::<ServiceManifest>()
            .resources
            .insert(
                (name.into(), 0),
                json!({"index":index,"rewardItemId":reward}),
            );
    }
    let spawn = |app: &mut App, name: &str, last: bool| {
        app.world_mut()
            .spawn((
                crate::world::WorldResource {
                    name: name.into(),
                    instance_index: 0,
                },
                Part { last },
                Transform::IDENTITY,
                Visibility::Inherited,
            ))
            .id()
    };
    let leaves = spawn(&mut app, "Bush_Jade", false);
    let fruit = spawn(&mut app, "Bush_Jade", true);
    let cap = spawn(&mut app, "Mushroom_Brown_0", true);
    let plain = spawn(&mut app, "Bush_0", true);
    app.update();
    assert!(app.world().get::<Focus>(fruit).is_some());
    assert!(app.world().get::<Focus>(cap).is_some());
    assert!(app.world().get::<Focus>(leaves).is_none());
    assert!(app.world().get::<Focus>(plain).is_none());
    assert_eq!(
        app.world_mut()
            .query::<&Collider>()
            .iter(app.world())
            .count(),
        0,
        "forage is look-only, never a walking or bullet obstacle"
    );
    app.world_mut()
        .run_system_once(
            |plants: Plants,
             services: Res<ServiceManifest>,
             catalog: Res<ItemCatalog>,
             mut focus: ResMut<InteractionFocus>| {
                offer(
                    &plants,
                    &services,
                    &catalog,
                    Vec3::new(-0.114, 1.141, 3.),
                    Vec3::NEG_Z,
                    6.,
                    &mut focus,
                );
            },
        )
        .unwrap();
    assert_eq!(
        app.world()
            .resource::<InteractionFocus>()
            .candidate
            .as_ref()
            .unwrap()
            .args["index"],
        9
    );
    app.world_mut().resource_mut::<GameSnapshot>().0 = json!({"resources":{"dead":[9,10]}});
    app.update();
    assert_eq!(
        app.world().get::<Visibility>(leaves),
        Some(&Visibility::Inherited)
    );
    for entity in [fruit, cap] {
        assert_eq!(
            app.world().get::<Visibility>(entity),
            Some(&Visibility::Hidden)
        );
        assert!(app.world().get::<ColliderDisabled>(entity).is_some());
    }
    assert_eq!(
        app.world_mut()
            .query_filtered::<Entity, (With<Focus>, Without<ColliderDisabled>)>()
            .iter(app.world())
            .count(),
        0
    );
    app.world_mut().resource_mut::<GameSnapshot>().0 = json!({"resources":{"dead":[]}});
    app.update();
    assert_eq!(
        app.world().get::<Visibility>(fruit),
        Some(&Visibility::Inherited)
    );
    assert_eq!(
        app.world().get::<Visibility>(cap),
        Some(&Visibility::Inherited)
    );
    assert_eq!(
        app.world_mut()
            .query_filtered::<Entity, With<Focus>>()
            .iter(app.world())
            .count(),
        2
    );
}
