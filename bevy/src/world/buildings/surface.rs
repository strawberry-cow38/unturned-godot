use super::data::{Part, Shape};
use bevy::{asset::RenderAssetUsages, mesh::PrimitiveTopology, prelude::*};
use bevy_rapier3d::prelude::*;

pub(super) fn mesh(part: &Part) -> Mesh {
    let mut positions = part.triangles.clone();
    let mut normals = Vec::with_capacity(positions.len());
    for triangle in positions.chunks_exact_mut(3) {
        // Source Godot front faces are clockwise. Reverse once, keeping Y-up
        // coordinates and the authored basis. Flat normals precede indexing.
        triangle.swap(1, 2);
        let [a, b, c] = [
            Vec3::from(triangle[0]),
            Vec3::from(triangle[1]),
            Vec3::from(triangle[2]),
        ];
        let n = (b - a).cross(c - a).normalize_or_zero().to_array();
        normals.extend([n; 3]);
    }
    Mesh::new(
        PrimitiveTopology::TriangleList,
        RenderAssetUsages::default(),
    )
    .with_inserted_attribute(Mesh::ATTRIBUTE_POSITION, positions)
    .with_inserted_attribute(Mesh::ATTRIBUTE_NORMAL, normals)
}
pub(super) fn compound(shapes: &[Shape]) -> anyhow::Result<Option<Collider>> {
    let mut parts = Vec::new();
    for shape in shapes {
        let collider = if let Some([x, y, z]) = shape.cuboid {
            Collider::cuboid(x * 0.5, y * 0.5, z * 0.5)
        } else if let Some(points) = &shape.hull {
            Collider::convex_hull(&points.iter().copied().map(Vec3::from).collect::<Vec<_>>())
                .ok_or_else(|| {
                    anyhow::anyhow!("original building convex points could not form a hull")
                })?
        } else {
            continue;
        };
        parts.push((Vec3::from(shape.position), Quat::IDENTITY, collider));
    }
    Ok((!parts.is_empty()).then(|| Collider::compound(parts)))
}
