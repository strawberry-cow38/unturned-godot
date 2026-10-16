use super::*;
use bevy::ecs::system::RunSystemOnce;

#[test]
fn source_wire_node_ray_ignores_vehicle_model_panes_but_keeps_world_fallback_ghost_and_authored_props()
 {
    let (mut app, _, _, _) = super::fixture();
    let mut camera = app
        .world_mut()
        .query_filtered::<&mut Transform, With<PlayerCamera>>();
    *camera.single_mut(app.world_mut()).unwrap() = Transform::from_xyz(20., 10., 20.);
    let spawn = |app: &mut App, z: f32, groups: CollisionGroups| {
        app.world_mut()
            .spawn((
                Collider::cuboid(0.5, 0.5, 0.05),
                Transform::from_xyz(20., 10., z),
                groups,
            ))
            .id()
    };
    let hull = spawn(
        &mut app,
        19.5,
        CollisionGroups::new(Group::GROUP_4, Group::GROUP_4),
    );
    let pane = spawn(
        &mut app,
        19.,
        CollisionGroups::new(Group::GROUP_4, Group::GROUP_2),
    );
    step(&mut app, 3);
    assert!(
        app.world()
            .resource::<Frame>()
            .free_end
            .distance(Vec3::new(20., 10., 14.))
            < 1e-5,
        "source bits15|5 meshes/panes never catch the free preview end"
    );
    app.world_mut()
        .entity_mut(pane)
        .insert(CollisionGroups::new(Group::NONE, Group::GROUP_2));
    step(&mut app, 2);
    assert!(
        app.world()
            .resource::<Frame>()
            .free_end
            .distance(Vec3::new(20., 10., 14.))
            < 1e-5,
        "broken glass does not change the independent source0|6 query"
    );
    for groups in [
        CollisionGroups::new(Group::GROUP_1, Group::ALL),
        CollisionGroups::new(Group::GROUP_4, Group::GROUP_2 | Group::GROUP_4),
        CollisionGroups::new(
            Group::GROUP_4 | crate::vehicle::coupling::GHOST_TRAILER,
            Group::GROUP_2,
        ),
        CollisionGroups::new(
            Group::GROUP_4 | crate::world::buildings::SOURCE_PROP,
            Group::GROUP_2,
        ),
    ] {
        let body = spawn(&mut app, 18., groups);
        step(&mut app, 2);
        assert!(
            (app.world().resource::<Frame>().free_end.z - 18.05).abs() < 1e-5,
            "source world/prop remains a route point"
        );
        app.world_mut().despawn(body);
        step(&mut app, 2);
    }
    app.world_mut().despawn(hull);
}

#[test]
fn sockets_are_port_layer_sensors_outside_contacts_and_the_ordinary_look_ray() {
    let (mut app, _, consumer, _) = super::fixture();
    super::aim(&mut app, consumer);
    let point = app.world().resource::<Frame>().endpoints[&consumer].position;
    let mut sockets = app
        .world_mut()
        .query_filtered::<(Entity, &Socket, &CollisionGroups), With<Sensor>>();
    let (socket, groups) = sockets
        .iter(app.world())
        .find(|(_, s, _)| s.address == consumer)
        .map(|(e, _, g)| (e, *g))
        .expect("source PortLayer cube is a sensor");
    assert_eq!(
        (groups.memberships, groups.filters),
        (LAYER, LAYER),
        "CollisionMask 0: no vehicle/body pair can include the cube"
    );
    let hit = app
        .world_mut()
        .run_system_once(move |context: ReadRapierContext| {
            let context = context.single().unwrap();
            let origin = point + Vec3::Z * 2.;
            // entities::focus's item ray: unfiltered groups, sensors excluded.
            let ordinary = context
                .cast_ray(origin, Vec3::NEG_Z, 2.5, true, QueryFilter::default().exclude_sensors())
                .map(|h| h.0);
            let port = context
                .cast_ray(
                    origin,
                    Vec3::NEG_Z,
                    2.5,
                    true,
                    QueryFilter::default().groups(CollisionGroups::new(Group::ALL, LAYER)),
                )
                .map(|h| h.0);
            (ordinary, port)
        })
        .unwrap();
    assert_ne!(hit.0, Some(socket), "ordinary look ray never stops at a port cube");
    assert_eq!(hit.1, Some(socket), "the port-only ray still selects it");
}
