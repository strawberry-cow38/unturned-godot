//! Player-only source model collision. The dynamic driving hull keeps terrain
//! and vehicle contacts; these zero-mass child shapes cannot push their carrier.
use super::*;
use std::collections::HashMap;

#[derive(Resource, Default)]
pub(super) struct Shapes(HashMap<AssetId<Mesh>, Collider>);

pub(super) fn player_groups(intact: bool) -> CollisionGroups {
    CollisionGroups::new(
        if intact { Group::GROUP_4 } else { Group::NONE },
        Group::GROUP_2,
    )
}

pub(super) fn install(
    mut commands: Commands,
    mut shapes: ResMut<Shapes>,
    meshes: Res<Assets<Mesh>>,
    mut bodies: Query<(&VehicleBody, &mut CollisionGroups)>,
    parts: Query<(Entity, &Name, &Mesh3d, &ChildOf, Option<&Pane>), Changed<Mesh3d>>,
) {
    for (entity, name, mesh, parent, pane) in &parts {
        let Ok((body, mut groups)) = bodies.get_mut(parent.parent()) else {
            continue;
        };
        let Some(definition) = super::super::data::definition(body.type_id) else {
            continue;
        };
        let body_mesh = pane.is_none()
            && definition.spec.body.as_deref() == Some(name.as_str())
            && body_hitbox(&definition.spec);
        let pane_mesh = pane.is_some_and(|p| p.has_triangles()) && mesh_hitbox();
        if !body_mesh && !pane_mesh {
            continue;
        }
        let shape = if let Some(shape) = shapes.0.get(&mesh.0.id()) {
            shape.clone()
        } else {
            let Some(mesh_data) = meshes.get(&mesh.0) else {
                continue;
            };
            let Some(shape) = triangles(mesh_data) else {
                continue;
            };
            shapes.0.insert(mesh.0.id(), shape.clone());
            shape
        };
        commands.entity(entity).insert((
            shape,
            player_groups(true),
            ColliderMassProperties::Mass(0.),
        ));
        // Only a successfully installed source body mesh takes the player's job
        // from the convex chassis. Ship bands/boxes retain their existing contact.
        if body_mesh {
            groups.filters.remove(Group::GROUP_2);
        }
    }
}

fn triangles(mesh: &Mesh) -> Option<Collider> {
    let vertices = mesh.attribute(Mesh::ATTRIBUTE_POSITION)?.as_float3()?;
    let indices: Vec<u32> = mesh.indices().map_or_else(
        || (0..vertices.len() as u32).collect(),
        |indices| indices.iter().map(|i| i as u32).collect(),
    );
    if !indices.len().is_multiple_of(3) {
        return None;
    }
    Collider::trimesh(
        vertices.iter().map(|p| Vec3::from_array(*p)).collect(),
        indices
            .chunks_exact(3)
            .map(|t| [t[0], t[1], t[2]])
            .collect(),
    )
    .ok()
}
