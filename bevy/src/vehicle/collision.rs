//! Reuses the original shipped convex bakes; new meshes decompose off the render thread.
pub(super) mod hull_mesh;
#[cfg(test)]
mod hull_mesh_tests;
use super::{
    VehicleBody,
    data::{VehicleDefinition, definition},
};
use crate::{content::ContentCache, world::WorldConfig};
use bevy::{
    prelude::*,
    tasks::{AsyncComputeTaskPool, Task, futures_lite::future},
};
use bevy_rapier3d::{geometry::VHACDParameters, prelude::*};
use std::{
    collections::HashMap,
    io::{Cursor, Read},
    path::Path,
};

#[derive(Resource, Default)]
pub struct Hulls {
    ready: HashMap<u8, Collider>,
    pending: HashMap<u8, Task<Collider>>,
}
#[derive(Component)]
pub struct PendingHull;
fn hash_name(key: &str) -> String {
    let mut h = 14695981039346656037u64;
    for c in key.encode_utf16() {
        h = (h ^ c as u64).wrapping_mul(1099511628211);
    }
    format!("{h:016x}.hulls")
}
fn read_bake(bytes: &[u8]) -> Option<Vec<Collider>> {
    let mut r = Cursor::new(bytes);
    fn u32(r: &mut Cursor<&[u8]>) -> Option<u32> {
        let mut b = [0; 4];
        r.read_exact(&mut b).ok()?;
        Some(u32::from_le_bytes(b))
    }
    let count = u32(&mut r)?;
    if !(1..=256).contains(&count) {
        return None;
    }
    let mut shapes = Vec::new();
    for _ in 0..count {
        let n = u32(&mut r)?;
        if !(4..=4096).contains(&n) {
            return None;
        }
        let mut points = Vec::new();
        for _ in 0..n {
            let p = Vec3::new(
                f32::from_bits(u32(&mut r)?),
                f32::from_bits(u32(&mut r)?),
                f32::from_bits(u32(&mut r)?),
            );
            if !p.is_finite() {
                return None;
            }
            points.push(p);
        }
        shapes.push(Collider::convex_hull(&points)?);
    }
    Some(shapes)
}
fn bake(d: &VehicleDefinition, root: &Path) -> Option<Vec<Collider>> {
    let body = d.spec.body.as_ref()?;
    let size = std::fs::metadata(root.join(body)).ok()?.len();
    let hulls = std::env::var("UG_CARHULLS").unwrap_or_default();
    let concavity = std::env::var("UG_CARCONCAVITY").unwrap_or_default();
    let key = format!("body|{body}|{}|{hulls}|{concavity}|{size}", d.spec.name);
    read_bake(&std::fs::read(root.join("vehicle_hulls").join(hash_name(&key))).ok()?)
}
fn with_extras(d: &VehicleDefinition, base: Vec<Collider>) -> Collider {
    let mut shapes = Vec::new();
    for collider in base {
        // VHACD returns a compound, whereas shipped bakes return convex leaves.
        // Parry forbids nested compounds: flatten the decomposition before adding
        // authored trailer/landing shapes to the same rigid body.
        if let Some(compound) = collider.as_compound() {
            for (pose, shape) in compound.raw.shapes() {
                shapes.push((
                    pose.translation,
                    pose.rotation,
                    Collider::from(shape.clone()),
                ));
            }
        } else {
            shapes.push((Vec3::ZERO, Quat::IDENTITY, collider));
        }
    }
    for (size, center) in d.spec.extra_boxes.iter().flatten() {
        shapes.push((
            Vec3::from_array(*center),
            Quat::IDENTITY,
            Collider::cuboid(size[0] * 0.5, size[1] * 0.5, size[2] * 0.5),
        ));
    }
    Collider::compound(shapes)
}
pub fn update(
    mut commands: Commands,
    config: Res<WorldConfig>,
    cache: Res<ContentCache>,
    mut hulls: ResMut<Hulls>,
    query: Query<(Entity, &VehicleBody), With<PendingHull>>,
) {
    let mut completed = Vec::new();
    for (id, task) in &mut hulls.pending {
        if let Some(shape) = future::block_on(future::poll_once(task)) {
            completed.push((*id, shape));
        }
    }
    for (id, shape) in completed {
        hulls.pending.remove(&id);
        hulls.ready.insert(id, shape);
    }
    for (entity, body) in &query {
        if let Some(shape) = hulls.ready.get(&body.type_id) {
            commands
                .entity(entity)
                .insert(shape.clone())
                .remove::<PendingHull>();
            continue;
        }
        if hulls.pending.contains_key(&body.type_id) {
            continue;
        }
        let d = definition(body.type_id).unwrap();
        if let Some(baked) = bake(d, &config.content_root) {
            hulls.ready.insert(body.type_id, with_extras(d, baked));
            continue;
        }
        let Some(name) = &d.spec.body else {
            commands.entity(entity).remove::<PendingHull>();
            continue;
        };
        let Some(mesh) = cache.geometry.get(&config.content_root.join(name)) else {
            continue;
        };
        let mesh = mesh.clone();
        let id = body.type_id;
        hulls.pending.insert(
            id,
            AsyncComputeTaskPool::get().spawn(async move {
                let vertices: Vec<_> = mesh
                    .positions
                    .iter()
                    .map(|p| Vec3::from_array(*p))
                    .collect();
                let indices: Vec<_> = (0..vertices.len() as u32)
                    .collect::<Vec<_>>()
                    .chunks_exact(3)
                    .map(|t| [t[0], t[1], t[2]])
                    .collect();
                let params = VHACDParameters {
                    concavity: 0.08,
                    max_convex_hulls: 12,
                    resolution: 22,
                    ..default()
                };
                // Parry resolution is voxels per axis; source Godot uses a total voxel budget (10,000).
                let shape =
                    Collider::convex_decomposition_with_params(&vertices, &indices, &params);
                with_extras(definition(id).unwrap(), vec![shape])
            }),
        );
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn original_jeep_bake_is_read_without_running_a_new_decomposer() {
        let d = super::super::data::named("jeep").unwrap();
        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join("../game/content");
        let shapes = bake(d, &root).unwrap();
        assert!(!shapes.is_empty());
        assert!(shapes.len() <= 12);
        assert!(read_bake(&[255, 255, 255, 255]).is_none());
        assert!(read_bake(&[1, 0, 0, 0, 4, 0, 0, 0]).is_none());
    }

    #[test]
    fn decomposed_compound_and_authored_extras_are_flattened_for_rapier() {
        let d = super::super::data::named("semi").unwrap();
        let a = Collider::compound(vec![
            (Vec3::X, Quat::IDENTITY, Collider::ball(1.)),
            (Vec3::NEG_X, Quat::IDENTITY, Collider::ball(1.)),
        ]);
        let result = with_extras(d, vec![a]);
        let parts = result.as_compound().unwrap();
        assert_eq!(
            parts.raw.shapes().len(),
            2 + d.spec.extra_boxes.as_ref().map_or(0, Vec::len)
        );
        assert!(
            parts
                .raw
                .shapes()
                .iter()
                .all(|(_, s)| s.as_compound().is_none())
        );
    }
}
