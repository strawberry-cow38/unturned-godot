use super::*;
use crate::{content::ContentCache, vehicle::data, world::WorldConfig};
use bevy::ecs::system::RunSystemOnce;
use bevy::{ecs::world::CommandQueue, prelude::*};
use bevy_rapier3d::prelude::*;

#[test]
fn source_landing_leg_transform_and_separate_sleeper_keep_the_authored_frames() {
    let mut world = World::new();
    let owner = world.spawn(Transform::IDENTITY).id();
    let mut queue = CommandQueue::default();
    let mut commands = Commands::new(&mut queue, &world);
    let semi = data::named("semi").unwrap();
    geometry::build(&mut commands, owner, semi, None, Handle::default());
    queue.apply(&mut world);
    let (entity, sleeper, pose, collider, groups) = world
        .query::<(
            Entity,
            &geometry::SleeperHull,
            &Transform,
            &Collider,
            &CollisionGroups,
        )>()
        .single(&world)
        .unwrap();
    assert_eq!(sleeper.0, owner);
    assert_eq!(world.get::<RigidBody>(entity), Some(&RigidBody::Fixed));
    let (size, center) = semi.roof.unwrap();
    assert_eq!(pose.translation, Vec3::from_array(center));
    assert_eq!(
        collider.as_cuboid().unwrap().half_extents(),
        Vec3::from_array(size) * 0.5
    );
    assert_eq!(groups.memberships, Group::GROUP_4);
    assert_eq!(groups.filters, Group::GROUP_2 | geometry::GHOST_TRAILER);
    let trailer = &data::named("trailer").unwrap().spec;
    let pose = geometry::leg_transform(trailer);
    assert_eq!(pose.scale, Vec3::new(1., 1.44, 1.));
    let pivot = Vec3::Y * trailer.landing_leg_pivot_y;
    assert!(pose.transform_point(pivot).abs_diff_eq(pivot, 1e-6));
    assert!(pose.transform_point(Vec3::ZERO).y < -0.49);
    assert_eq!(
        geometry::leg_transform(&data::named("small_trailer").unwrap().spec),
        Transform::IDENTITY
    );
    world.despawn(owner);
    assert!(world.get_entity(entity).is_err());
}

#[test]
fn all_original_trailers_split_legs_once_before_lenses_and_keep_source_attributes() {
    for definition in data::FLEET.iter().filter(|d| d.spec.kingpin != [0.; 3]) {
        let mut world = World::new();
        let owner = world.spawn(Transform::IDENTITY).id();
        let mut queue = CommandQueue::default();
        let mut cache = ContentCache::default();
        let mut meshes = Assets::<Mesh>::default();
        let mut materials = Assets::<StandardMaterial>::default();
        let root = WorldConfig::default().content_root;
        let (split, lights) = crate::vehicle::lighting::geometry::build(
            &mut Commands::new(&mut queue, &world),
            owner,
            definition,
            &root,
            &mut cache,
            &mut meshes,
            &mut materials,
        );
        let split = split.expect("source trailer body is partitioned");
        let legs = split
            .legs
            .as_ref()
            .expect("each original trailer has landing legs");
        geometry::build(
            &mut Commands::new(&mut queue, &world),
            owner,
            definition,
            Some(legs.clone()),
            Handle::default(),
        );
        queue.apply(&mut world);
        let original = cache
            .geometry
            .get(&root.join(definition.spec.body.as_ref().unwrap()))
            .unwrap();
        let body = meshes.get(&split.mesh).unwrap();
        let leg = meshes.get(legs).unwrap();
        let mut count = body.count_vertices() + leg.count_vertices();
        // Separate original part files are not triangles from the body. Only
        // the semi trailer has baked lens zones in its body partition.
        for lens in lights
            .lenses
            .iter()
            .filter(|_| definition.spec.taillight_zone_min != definition.spec.taillight_zone_max)
        {
            count += meshes
                .get(&world.get::<Mesh3d>(lens.entity).unwrap().0)
                .unwrap()
                .count_vertices();
        }
        assert_eq!(
            count,
            original.positions.len(),
            "{} no lost/duplicate triangles",
            definition.key
        );
        let lo = Vec3::from_array(definition.spec.landing_leg_zone_min);
        let hi = Vec3::from_array(definition.spec.landing_leg_zone_max);
        let positions = leg
            .attribute(Mesh::ATTRIBUTE_POSITION)
            .unwrap()
            .as_float3()
            .unwrap();
        let normals = leg
            .attribute(Mesh::ATTRIBUTE_NORMAL)
            .unwrap()
            .as_float3()
            .unwrap();
        let bevy::mesh::VertexAttributeValues::Float32x2(uvs) =
            leg.attribute(Mesh::ATTRIBUTE_UV_0).unwrap()
        else {
            panic!("source UVs");
        };
        assert!(!positions.is_empty());
        assert!(positions.iter().all(|p| {
            let p = Vec3::from_array(*p);
            p.cmpge(lo).all() && p.cmple(hi).all()
        }));
        for (i, position) in positions.iter().enumerate() {
            assert!(
                original
                    .positions
                    .iter()
                    .enumerate()
                    .any(|(j, p)| p == position
                        && original.normals[j] == normals[i]
                        && original.uvs[j] == uvs[i])
            );
        }
        assert_eq!(split.data.positions.len(), body.count_vertices());
        let leg_entity = world
            .query_filtered::<Entity, With<geometry::LandingLegs>>()
            .single(&world)
            .unwrap();
        assert!(
            world.get::<Collider>(leg_entity).is_none(),
            "source leg visual is not another physics body"
        );
        assert_eq!(
            world.get::<Transform>(leg_entity),
            Some(&geometry::leg_transform(&definition.spec))
        );
        world.despawn(owner);
        assert!(world.get_entity(leg_entity).is_err());
    }
}

#[test]
fn separate_sleeper_contacts_players_and_ghost_trailers_but_not_ordinary_chassis() {
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
    });
    let semi = data::named("semi").unwrap();
    let (size, center) = semi.roof.unwrap();
    let top = center[1] + size[1] * 0.5;
    let mut visitors = Vec::new();
    for (index, membership) in [
        Group::GROUP_2,
        Group::GROUP_4 | geometry::GHOST_TRAILER,
        Group::GROUP_4,
    ]
    .into_iter()
    .enumerate()
    {
        let x = index as f32 * 10.;
        let owner = app.world_mut().spawn(Transform::from_xyz(x, 0., 0.)).id();
        app.world_mut()
            .run_system_once(move |mut commands: Commands| {
                geometry::build(
                    &mut commands,
                    owner,
                    data::named("semi").unwrap(),
                    None,
                    Handle::default(),
                );
            })
            .unwrap();
        visitors.push(
            app.world_mut()
                .spawn((
                    Transform::from_xyz(x, top + 0.5, center[2]),
                    RigidBody::Dynamic,
                    Collider::ball(0.1),
                    CollisionGroups::new(membership, Group::GROUP_4),
                    Velocity::zero(),
                    Sleeping::disabled(),
                ))
                .id(),
        );
    }
    for _ in 0..60 {
        app.update();
    }
    let y = |id| app.world().get::<Transform>(id).unwrap().translation.y;
    assert!(
        y(visitors[0]) > top,
        "player sees the original sleeper surface"
    );
    assert!(
        y(visitors[1]) > top,
        "ghosted trailer cannot fold into the sleeper"
    );
    assert!(
        y(visitors[2]) < top - 1.,
        "ordinary chassis ignores the independent sleeper"
    );
}

#[test]
fn production_trailer_builder_decomposes_body_without_the_retractable_leg_triangles() {
    let mut app = App::new();
    app.add_plugins((MinimalPlugins, AssetPlugin::default()))
        .init_asset::<Mesh>()
        .init_asset::<Image>()
        .init_asset::<StandardMaterial>()
        .init_resource::<ContentCache>();
    let definition = data::named("trailer").unwrap();
    let owner = app.world_mut().spawn(Transform::IDENTITY).id();
    app.world_mut()
        .run_system_once(
            move |mut commands: Commands,
                  mut cache: ResMut<ContentCache>,
                  mut meshes: ResMut<Assets<Mesh>>,
                  mut materials: ResMut<Assets<StandardMaterial>>,
                  mut images: ResMut<Assets<Image>>,
                  assets: Res<AssetServer>| {
                crate::vehicle::render::build(
                    &mut commands,
                    owner,
                    definition,
                    0,
                    &WorldConfig::default().content_root,
                    &mut cache,
                    &mut meshes,
                    &mut materials,
                    &mut images,
                    &assets,
                );
            },
        )
        .unwrap();
    assert!(
        app.world()
            .get::<crate::vehicle::collision::PendingHull>(owner)
            .is_some()
    );
    let source = &app
        .world()
        .get::<crate::vehicle::collision::SourceMesh>(owner)
        .expect("production decomposer must receive the post-split source body")
        .0;
    let lo = Vec3::from_array(definition.spec.landing_leg_zone_min);
    let hi = Vec3::from_array(definition.spec.landing_leg_zone_max);
    let selected = |data: &crate::content::ObjData| {
        data.positions
            .chunks_exact(3)
            .filter(|face| {
                face.iter().all(|p| {
                    let p = Vec3::from_array(*p);
                    p.cmpge(lo).all() && p.cmple(hi).all()
                })
            })
            .count()
    };
    let original = &app.world().resource::<ContentCache>().geometry[&WorldConfig::default()
        .content_root
        .join(definition.spec.body.as_ref().unwrap())];
    assert!(
        selected(original) > 0,
        "negative control: original body contains supports"
    );
    assert_eq!(
        selected(source),
        0,
        "decomposition must not create invisible extended legs"
    );
    let leg = app
        .world_mut()
        .query_filtered::<Entity, With<geometry::LandingLegs>>()
        .single(app.world())
        .unwrap();
    assert_eq!(app.world().get::<ChildOf>(leg).unwrap().parent(), owner);
}

mod visual;
mod physics;
mod contact;
mod accepted;
mod coupled_visual;

#[test]
fn real_async_decomposer_consumes_the_post_partition_source_before_cache_fallback() {
    use std::sync::Arc;
    let mut app = App::new();
    app.add_plugins(MinimalPlugins)
        .init_resource::<ContentCache>()
        .init_resource::<crate::vehicle::collision::Hulls>();
    let root = std::env::temp_dir().join("unturned-no-trailer-bake");
    app.insert_resource(WorldConfig {
        content_root: root.clone(),
        ..default()
    });
    // A translated tetrahedron is an unambiguous data-flow witness. The real
    // builder/partition test above supplies the original-asset contract; this
    // test verifies the asynchronous consumer cannot silently use its cache.
    let tetra = |x: f32| {
        crate::content::parse_obj(&format!(
            "v {} 0 0\nv {} 0 0\nv {} 1 0\nv {} 0 1\nf 1 3 2\nf 1 2 4\nf 1 4 3\nf 2 3 4\n",
            x,
            x + 1.,
            x,
            x
        ))
        .unwrap()
    };
    let definition = data::named("trailer").unwrap();
    app.world_mut()
        .resource_mut::<ContentCache>()
        .geometry
        .insert(
            root.join(definition.spec.body.as_ref().unwrap()),
            Arc::new(tetra(0.)),
        );
    let body = crate::vehicle::tests::body(&mut app, "trailer", Vec3::ZERO);
    app.world_mut().entity_mut(body).insert((
        crate::vehicle::collision::PendingHull,
        crate::vehicle::collision::SourceMesh(Arc::new(tetra(40.))),
    ));
    app.add_systems(Update, crate::vehicle::collision::update);
    for _ in 0..1000 {
        app.update();
        if app
            .world()
            .get::<crate::vehicle::collision::PendingHull>(body)
            .is_none()
        {
            break;
        }
        std::thread::sleep(std::time::Duration::from_millis(1));
    }
    assert!(
        app.world()
            .get::<crate::vehicle::collision::PendingHull>(body)
            .is_none(),
        "real decomposition completed"
    );
    let collider = app.world().get::<Collider>(body).unwrap();
    assert!(
        collider.raw.compute_local_aabb().maxs.x > 40.,
        "post-partition source reaches the completed hull; original cached body remains near x0"
    );
}
