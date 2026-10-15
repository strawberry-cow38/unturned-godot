use super::*;
use crate::{content::ObjData, ui::Screen};
use bevy::ecs::system::RunSystemOnce;

fn zone(kind: &str, center: Vec3, size: Vec3) -> AccessZone {
    AccessZone {
        kind: kind.into(),
        seat: 0,
        center: center.to_array(),
        size: size.to_array(),
    }
}
#[test]
fn source_access_prioritizes_all_intersections_then_nearest_endpoint_with_stable_ties() {
    let zones = [
        zone("Door", Vec3::new(0., 0., -2.), Vec3::ONE),
        zone("Hood", Vec3::new(0.6, 0., -0.5), Vec3::splat(0.2)),
    ];
    assert_eq!(
        resolve(&zones, Vec3::ZERO, Vec3::new(0., 0., -2.5))
            .unwrap()
            .kind,
        "Door"
    );
    assert_eq!(
        resolve(&zones, Vec3::ZERO, Vec3::new(0., 0., -0.4))
            .unwrap()
            .kind,
        "Hood"
    );
    assert!(resolve(&zones, Vec3::new(8., 0., 0.), Vec3::new(8., 0., -0.4)).is_none());
    let ties = [
        zone("Door", Vec3::X, Vec3::ONE),
        zone("Hood", -Vec3::X, Vec3::ONE),
    ];
    assert_eq!(resolve(&ties, Vec3::Z, Vec3::ZERO).unwrap().kind, "Door");
}
fn fixture() -> (App, Entity, Entity) {
    let mut app = App::new();
    app.add_plugins((
        MinimalPlugins,
        TransformPlugin,
        RapierPhysicsPlugin::<NoUserData>::default(),
    ))
    .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(20),
    ))
    .insert_resource(TimestepMode::Fixed {
        dt: 0.02,
        substeps: 1,
    })
    .init_resource::<InteractionFocus>()
    .insert_resource(UiState {
        screen: Screen::Playing,
        ..default()
    })
    .insert_resource(GameSnapshot(
        json!({"player":{"alive":true,"vehicleNetId":0}}),
    ));
    let root =
        crate::vehicle::tests::body(&mut app, "police", Vec3::new(-522.40625, 31.5664, 54.44922));
    let rotation = Quat::from_euler(
        EulerRot::YXZ,
        2.63672f32.to_radians(),
        0.17578f32.to_radians(),
        346.64062f32.to_radians(),
    );
    let d = data::named("police").unwrap();
    let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../game/content")
        .join(d.spec.body.as_ref().unwrap());
    let obj = ObjData::load(&path).unwrap();
    let vertices: Vec<_> = obj.positions.iter().map(|p| Vec3::from_array(*p)).collect();
    let indices: Vec<_> = (0..vertices.len() as u32)
        .collect::<Vec<_>>()
        .chunks_exact(3)
        .map(|t| [t[0], t[1], t[2]])
        .collect();
    app.world_mut().get_mut::<VehicleBody>(root).unwrap().net_id = 10;
    app.world_mut().get_mut::<Transform>(root).unwrap().rotation = rotation;
    app.world_mut().entity_mut(root).insert((
        RigidBody::Fixed,
        Collider::convex_hull(&vertices).unwrap(),
        CollisionGroups::new(Group::GROUP_4, Group::ALL & !Group::GROUP_2),
    ));
    let child = app
        .world_mut()
        .spawn((
            ChildOf(root),
            Transform::IDENTITY,
            Collider::trimesh(vertices, indices).unwrap(),
            CollisionGroups::new(Group::GROUP_4, Group::GROUP_2),
        ))
        .id();
    // Actual failed full-game capture. The active camera is also the source TP
    // trace selector; no reconstructed shoulder or speculative reach extension.
    app.world_mut().spawn((
        PlayerCamera,
        Transform::from_xyz(-522.60156, 32.78056, 50.5).with_rotation(Quat::from_euler(
            EulerRot::YXZ,
            180f32.to_radians(),
            -11.04f32.to_radians(),
            0.,
        )),
    ));
    app.update();
    app.update();
    (app, root, child)
}
fn scan(app: &mut App) {
    *app.world_mut().resource_mut::<InteractionFocus>() = default();
    app.world_mut().run_system_once(focus).unwrap();
}
#[test]
fn actual_tilted_police_mesh_hood_resolves_with_stripped_chassis_and_wall_occlusion() {
    let (mut app, root, _) = fixture();
    scan(&mut app);
    assert!(
        app.world()
            .resource::<InteractionFocus>()
            .selected("openVehicleHood", 10),
        "actual production pose must select its bonnet"
    );
    // Inspect the same native physics query, proving its result belongs to the
    // original visible child rather than the stripped convex chassis.
    let ray = app
        .world_mut()
        .run_system_once(
            |ctx: ReadRapierContext, camera: Query<&Transform, With<PlayerCamera>>| {
                let c = camera.single().unwrap();
                let p = ctx.single().unwrap();
                let q = |member| {
                    p.cast_ray(
                        c.translation,
                        *c.forward(),
                        REACH,
                        true,
                        QueryFilter::default().groups(CollisionGroups::new(
                            member,
                            Group::GROUP_1 | Group::GROUP_4,
                        )),
                    )
                };
                (q(Group::GROUP_2), q(Group::ALL))
            },
        )
        .unwrap();
    assert!(ray.0.is_some());
    assert_ne!(ray.0.unwrap().0, root);
    assert_eq!(
        ray.1.unwrap().0,
        root,
        "negative control: ALL erroneously admits the convex chassis"
    );
    let wall = app
        .world_mut()
        .spawn((
            RigidBody::Fixed,
            Collider::cuboid(2., 2., 0.04),
            Transform::from_xyz(-522.6, 32.5, 51.),
            CollisionGroups::new(Group::GROUP_1, Group::ALL),
        ))
        .id();
    app.update();
    app.update();
    scan(&mut app);
    assert!(
        !app.world().resource::<InteractionFocus>().has_candidate(),
        "a nearby hood may not win before the vehicle itself is visible"
    );
    app.world_mut().despawn(wall);
    app.update();
    app.update();
    scan(&mut app);
    assert!(
        app.world()
            .resource::<InteractionFocus>()
            .selected("openVehicleHood", 10)
    );
}
#[test]
fn vehicle_winner_precedes_zone_fallback_and_claimed_targets_block_hull_fallback() {
    let (mut app, _, child) = fixture();
    app.world_mut().despawn(child);
    app.update();
    app.update();
    scan(&mut app);
    assert!(
        app.world()
            .resource::<InteractionFocus>()
            .selected("openVehicleHood", 10),
        "source disabled box still supplies visible-footprint fallback"
    );
    let focus = app.world_mut().resource_mut::<InteractionFocus>();
    drop(focus);
    *app.world_mut().resource_mut::<InteractionFocus>() = default();
    app.world_mut().resource_mut::<InteractionFocus>().offer(
        2.5,
        "Item",
        "pickupItem",
        json!({"netId":99}),
        false,
    );
    app.world_mut().run_system_once(super::focus).unwrap();
    assert!(
        app.world()
            .resource::<InteractionFocus>()
            .selected("pickupItem", 99),
        "source hull fallback runs only when nothing else owns focus"
    );
}
