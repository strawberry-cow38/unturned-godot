use super::*;
use bevy::ecs::world::CommandQueue;
mod visual;
fn scene(key: &str) -> (World, Entity, Option<Handle<Mesh>>) {
    let mut world = World::new();
    let parent = world
        .spawn((Transform::IDENTITY, Visibility::Inherited))
        .id();
    let mut queue = CommandQueue::default();
    let mut cache = ContentCache::default();
    let mut meshes = Assets::<Mesh>::default();
    let mut materials = Assets::<StandardMaterial>::default();
    let root = crate::world::WorldConfig::default().content_root;
    let (body, lights) = build(
        &mut Commands::new(&mut queue, &world),
        parent,
        crate::vehicle::data::named(key).unwrap(),
        &root,
        &mut cache,
        &mut meshes,
        &mut materials,
    );
    queue.apply(&mut world);
    world.entity_mut(parent).insert(lights);
    world.insert_resource(meshes);
    world.insert_resource(materials);
    world.insert_resource(cache);
    (world, parent, body)
}
#[test]
fn actual_lens_halves_keep_source_attributes_and_independent_materials() {
    let (mut world, parent, body) = scene("sedan");
    assert!(body.is_none());
    let lights = world.get::<BuiltLighting>(parent).unwrap();
    let labels: Vec<_> = lights.lamps.iter().map(|l| l.label).collect();
    assert_eq!(
        labels,
        ["headlight_l", "headlight_r", "taillight_l", "taillight_r"]
    );
    assert_eq!(
        lights.head_primary, lights.lamps[1].material,
        "source part alias is right"
    );
    assert_eq!(lights.tail_primary, lights.lamps[3].material);
    for pair in lights.lamps.chunks_exact(2) {
        assert!(pair[0].center.x < 0. && pair[1].center.x > 0.);
        assert_ne!(
            pair[0].material, pair[1].material,
            "one broken lens must not mutate the other"
        );
    }
    assert_eq!(lights.head_spots.len(), 2);
    assert_eq!(lights.tail_spots.len(), 2);
    assert!(!lights.round);
    let entities: Vec<_> = lights.lenses.iter().map(|l| l.entity).collect();
    world.despawn(parent);
    assert!(entities.iter().all(|e| world.get_entity(*e).is_err()));
    let root = crate::world::WorldConfig::default().content_root;
    let original = ObjData::load(&root.join("sedan_headlights.txt")).unwrap();
    let (left, right) = math::halves(&original);
    assert_eq!(
        left.positions.len() + right.positions.len(),
        original.positions.len()
    );
    for output in [&left, &right] {
        for (i, position) in output.positions.iter().enumerate() {
            assert!(
                original
                    .positions
                    .iter()
                    .enumerate()
                    .any(|(j, p)| p == position
                        && original.normals[j] == output.normals[i]
                        && original.uvs[j] == output.uvs[i]
                        && original.colors[j] == output.colors[i])
            );
        }
    }
}
#[test]
fn baked_body_zones_preserve_every_triangle_and_source_default_tint() {
    for (key, kind) in [("semi", Kind::Head), ("trailer", Kind::Tail)] {
        let (world, parent, body) = scene(key);
        let body = body.unwrap();
        let lights = world.get::<BuiltLighting>(parent).unwrap();
        let meshes = world.resource::<Assets<Mesh>>();
        let count = meshes.get(&body).unwrap().count_vertices()
            + lights
                .lenses
                .iter()
                .filter(|l| l.kind == kind)
                .map(|l| {
                    meshes
                        .get(&world.get::<Mesh3d>(l.entity).unwrap().0)
                        .unwrap()
                        .count_vertices()
                })
                .sum::<usize>();
        let d = crate::vehicle::data::named(key).unwrap();
        let original = ObjData::load(
            &crate::world::WorldConfig::default()
                .content_root
                .join(d.spec.body.as_ref().unwrap()),
        )
        .unwrap();
        assert_eq!(
            count,
            original.positions.len(),
            "{key} triangles must be neither duplicated nor lost"
        );
        assert_eq!(lights.lamps.len(), 2);
        if key == "semi" {
            assert_eq!(
                lights.head_primary, lights.lamps[0].material,
                "baked source alias is left"
            );
        }
        assert_eq!(
            lights.head_tint,
            Color::srgb(0.97, 0.96, 0.83),
            "source never classifies baked zone lenses"
        );
    }
}
#[test]
fn source_shape_kelvin_auto_emitters_and_hidden_lightbar_center() {
    let (world, parent, _) = scene("jeep");
    let lights = world.get::<BuiltLighting>(parent).unwrap();
    assert!(lights.round);
    assert_eq!(lights.head_tint, math::kelvin(3000.));
    for key in ["quad", "bus"] {
        let (world, parent, _) = scene(key);
        let lights = world.get::<BuiltLighting>(parent).unwrap();
        assert_eq!(lights.head_spots.len(), 2, "source auto lights on {key}");
        for lamp in lights
            .lamps
            .iter()
            .filter(|l| l.label.starts_with("headlight"))
        {
            let mesh = world.get::<Mesh3d>(lamp.entity).unwrap();
            let data = world
                .resource::<ContentCache>()
                .geometry
                .iter()
                .find(|(p, _)| world.resource::<ContentCache>().meshes.get(*p) == Some(&mesh.0))
                .unwrap()
                .1;
            let expected = Vec3::new(lamp.center.x, lamp.center.y, data.min.z - 0.06);
            assert!(lights.head_spots.contains(&expected));
        }
    }
    let (world, parent, _) = scene("police");
    let lights = world.get::<BuiltLighting>(parent).unwrap();
    let last = &lights.lamps[lights.lamps.len() - 3..];
    assert_eq!(
        last.iter().map(|l| l.label).collect::<Vec<_>>(),
        ["lightbar_l", "lightbar_r", "lightbar_c"]
    );
    assert_eq!(last[2].center, (last[0].center + last[1].center) * 0.5);
    assert!(last[2].material.is_none());
    for lens in lights
        .lenses
        .iter()
        .filter(|l| matches!(l.kind, Kind::SirenLeft | Kind::SirenRight))
    {
        assert_eq!(lens.emitter.as_ref().unwrap().energy, 5.);
    }
    assert_eq!(
        world.get::<Visibility>(last[2].entity),
        Some(&Visibility::Hidden)
    );
}
#[test]
fn original_nav_sides_and_single_downward_belly_lens_follow_geometry() {
    for key in ["huey", "orca", "hind", "minicopter"] {
        let (world, parent, _) = scene(key);
        let lights = world.get::<BuiltLighting>(parent).unwrap();
        assert!(
            lights.lamps.is_empty(),
            "aircraft nav/beacon are not source shootable lamps"
        );
        let beacon = lights
            .lenses
            .iter()
            .find(|l| l.kind == Kind::Beacon)
            .unwrap();
        let def = &crate::vehicle::data::named(key).unwrap().spec;
        assert_eq!(
            beacon.center,
            Vec3::new(
                0.,
                def.box_center[1] - def.box_size[1] * 0.5 - 0.08,
                def.box_center[2]
            )
        );
        assert_eq!(beacon.emitter.as_ref().unwrap().range, 6.);
        assert_eq!(beacon.emitter.as_ref().unwrap().energy, 3.2);
        if key == "minicopter" {
            assert_eq!(lights.lenses.len(), 1);
            continue;
        }
        for (kind, sign, color) in [
            (Kind::NavPort, -1., Color::srgb(0.95, 0.05, 0.05)),
            (Kind::NavStarboard, 1., Color::srgb(0.05, 0.95, 0.15)),
        ] {
            let lens = lights.lenses.iter().find(|l| l.kind == kind).unwrap();
            assert!(lens.center.x * sign > 0.);
            assert_eq!(lens.color, color);
            let emitter = lens.emitter.as_ref().unwrap();
            assert_eq!(emitter.position, lens.center);
            assert_eq!((emitter.energy, emitter.range), (1.4, 2.2));
            let material = world
                .resource::<Assets<StandardMaterial>>()
                .get(&lens.material)
                .unwrap();
            assert!(
                material.cull_mode.is_none(),
                "mirrored lens winding must remain visible"
            );
        }
        let mesh = world.get::<Mesh3d>(beacon.entity).unwrap();
        let positions = world
            .resource::<Assets<Mesh>>()
            .get(&mesh.0)
            .unwrap()
            .attribute(Mesh::ATTRIBUTE_POSITION)
            .unwrap()
            .as_float3()
            .unwrap();
        let (mut min, mut max) = (Vec3::splat(f32::INFINITY), Vec3::splat(f32::NEG_INFINITY));
        for p in positions {
            min = min.min(Vec3::from_array(*p));
            max = max.max(Vec3::from_array(*p));
        }
        assert!(((min + max) * 0.5).length() < 0.0001);
        let size = max - min;
        assert!(
            size.y < size.x && size.y < size.z,
            "{key} slab points down: {size:?}"
        );
        // Actual original BeaconLensMesh centroid clusters: Orca keeps the first
        // of two equal ten-triangle clusters; Hind borrows the larger Skycrane
        // lens. A generic <0.5m limit wrongly rejects that exact source mesh.
        let (triangles, expected) = match key {
            "huey" => (20, Vec3::new(0.368619, 0.160731, 0.370285)),
            "orca" => (10, Vec3::new(0.368370, 0.160730, 0.368369)),
            "hind" => (20, Vec3::new(0.5, 0.174106, 0.5)),
            _ => unreachable!(),
        };
        assert_eq!(
            positions.len() / 3,
            triangles,
            "{key} single source cluster"
        );
        assert!(
            (size - expected).abs().max_element() < 0.00001,
            "{key}: {size:?}"
        );
    }
}
