//! Original EditorBuildings stage values. Retail finite prefab towns never use
//! this path. Doors/glass use original opening identities in this preview. Furniture and
//! gameplay registration follow in separate consumers.
mod data;
mod openings;
mod surface;
pub(crate) use openings::BuildingOpeningPlugin;

use bevy::prelude::*;
use bevy_rapier3d::prelude::*;
pub(crate) use data::View;

/// Native tag for original Godot bit6 props, distinct from vehicle HitMesh.
/// GROUP4 keeps existing player/look/combat filters compatible; GROUP17 tells
/// source-bit6 queries apart from source-bit15 vehicle presentation geometry.
pub(crate) const SOURCE_PROP: Group = Group::GROUP_17;

#[derive(Component)]
pub(crate) struct Stage;
#[derive(Component)]
pub(crate) struct Wall(pub usize);
#[derive(Component)]
pub(crate) struct Trim;
#[derive(Component)]
pub(crate) struct Ramp;

/// Replace the one authored preview atomically after decoding/validating the
/// complete value view. Despawning old colliders precedes installing new ones;
/// an empty source layout therefore cannot retain invisible old walls.
pub(crate) fn replace(world: &mut World, view: &View) -> anyhow::Result<Entity> {
    view.validate()?;
    let collision = view
        .walls
        .iter()
        .map(|w| {
            Ok((
                surface::compound(&w.solids.shapes)?,
                surface::compound(&w.trim_solids.shapes)?,
            ))
        })
        .collect::<anyhow::Result<Vec<_>>>()?;
    let opening_art = view
        .walls
        .iter()
        .map(|w| openings::prepare(w, &view.content_root))
        .collect::<anyhow::Result<Vec<_>>>()?;
    let roots: Vec<_> = world
        .query_filtered::<Entity, With<Stage>>()
        .iter(world)
        .collect();
    for root in roots {
        world.despawn(root);
    }
    let root = world
        .spawn((
            Stage,
            Name::new("Original building stage"),
            Transform::IDENTITY,
            Visibility::Inherited,
        ))
        .id();
    for ((wall, (solids, trim)), art) in view.walls.iter().zip(collision).zip(opening_art) {
        let entity = world
            .spawn((
                Wall(wall.index),
                Name::new(format!("{} {}", wall.kind, wall.index)),
                data::pose(wall.position, wall.basis),
                Visibility::Inherited,
                ChildOf(root),
            ))
            .id();
        for (name, part, roughness) in [
            ("Front", &wall.front, 0.95),
            ("Back", &wall.back, 0.95),
            ("Trim", &wall.trim, 0.9),
        ] {
            if part.triangles.is_empty() {
                continue;
            }
            let mesh = world
                .resource_mut::<Assets<Mesh>>()
                .add(surface::mesh(part));
            let [r, g, b, a] = part.color;
            let material = world
                .resource_mut::<Assets<StandardMaterial>>()
                .add(StandardMaterial {
                    base_color: Color::srgba(r, g, b, a),
                    perceptual_roughness: roughness,
                    ..default()
                });
            world.spawn((
                Name::new(name),
                Mesh3d(mesh),
                MeshMaterial3d(material),
                Transform::IDENTITY,
                ChildOf(entity),
            ));
        }
        if let Some(collider) = solids {
            world.entity_mut(entity).insert((
                RigidBody::Fixed,
                collider,
                CollisionGroups::new(Group::GROUP_1, Group::ALL),
            ));
        }
        openings::spawn(world, entity, wall, art);
        if let Some(collider) = trim {
            world.spawn((
                Trim,
                Name::new("Original bit6 trim"),
                RigidBody::Fixed,
                collider,
                CollisionGroups::new(Group::GROUP_4 | SOURCE_PROP, Group::GROUP_2),
                Transform::IDENTITY,
                ChildOf(entity),
            ));
        }
    }
    for ramp in &view.ramps {
        world.spawn((
            Ramp,
            Name::new("Original invisible stair ramp"),
            RigidBody::Fixed,
            Collider::cuboid(ramp.size[0] * 0.5, ramp.size[1] * 0.5, ramp.size[2] * 0.5),
            CollisionGroups::new(Group::GROUP_1, Group::ALL),
            data::pose(ramp.position, ramp.basis),
            ChildOf(root),
        ));
    }
    Ok(root)
}

#[cfg(test)]
mod tests;
