//! Original world-bit geometry is distinct from rendered bullet/player meshes.
//! Use the live physics shape (including completed convex bakes and scaling),
//! never the broad source BoxSize or a second approximate model bound.
use super::*;
use bevy_rapier3d::parry::shape::{Shape, TypedShape};

pub(super) fn retained(groups: &CollisionGroups) -> bool {
    groups.memberships.contains(Group::GROUP_4)
        && !groups.memberships.contains(crate::vehicle::coupling::GHOST_TRAILER)
        && groups.filters.contains(Group::GROUP_2 | Group::GROUP_4)
}

pub(super) fn append(
    output: &mut Vec<serde_json::Value>,
    collider: &Collider,
    pose: &GlobalTransform,
    net_id: u32,
) {
    // Collider.raw already contains Rapier's current scale. Applying the
    // GlobalTransform scale again would enlarge the authority's obstruction.
    let (_, rotation, translation) = pose.to_scale_rotation_translation();
    append_shape(
        output,
        &*collider.raw,
        Transform::from_translation(translation).with_rotation(rotation),
        net_id,
    );
}

fn append_shape(
    output: &mut Vec<serde_json::Value>,
    shape: &dyn Shape,
    pose: Transform,
    net_id: u32,
) {
    let (vertices, triangles) = match shape.as_typed_shape() {
        TypedShape::Compound(compound) => {
            for (local, shape) in compound.shapes() {
                append_shape(
                    output,
                    &**shape,
                    pose.mul_transform(
                        Transform::from_translation(local.translation)
                            .with_rotation(local.rotation),
                    ),
                    net_id,
                );
            }
            return;
        }
        TypedShape::Cuboid(shape) => shape.to_trimesh(),
        TypedShape::ConvexPolyhedron(shape) => shape.to_trimesh(),
        TypedShape::TriMesh(shape) => (shape.vertices().to_vec(), shape.indices().to_vec()),
        // Every original native vehicle hull currently uses these exact shapes.
        // An unsupported future shape must not quietly become a broad AABB.
        _ => {
            warn!(
                "Unsupported source-world vehicle collider #{net_id}: {:?}",
                shape.as_typed_shape()
            );
            return;
        }
    };
    let vertices: Vec<_> = vertices
        .into_iter()
        .map(|p| pose.transform_point(p).to_array())
        .collect();
    let indices: Vec<_> = triangles.into_iter().flatten().collect();
    if indices.is_empty() {
        return;
    }
    output.push(
        json!({"vertices":vertices,"indices":indices,"vehicleNetId":net_id,
        "bodyPart":"hull", "sourceWorld":true,"combat":false}),
    );
}
