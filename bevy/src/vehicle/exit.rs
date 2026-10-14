//! VehicleExit.cs: own-door path and room probes, with rolled-hull and centreline alternatives.
use super::data::VehicleDefinition;
use crate::content::ContentCache;
use bevy::prelude::*;
use bevy_rapier3d::prelude::*;
use std::path::Path;

fn outboard(d: &VehicleDefinition, at: Vec3, side: f32, cache: &ContentCache, root: &Path) -> Vec3 {
    let s = &d.spec;
    let mut edge = at.x.abs().max(side * s.box_center[0] + s.box_size[0] * 0.5);
    for name in s.body.iter().chain(s.heli_body_meshes.iter().flatten()) {
        if let Some(mesh) = cache.geometry.get(&root.join(name)) {
            for triangle in mesh.positions.chunks_exact(3) {
                let p = [
                    Vec3::from_array(triangle[0]),
                    Vec3::from_array(triangle[1]),
                    Vec3::from_array(triangle[2]),
                ];
                let lo = p.iter().fold(Vec3::splat(f32::INFINITY), |a, b| a.min(*b));
                let hi = p
                    .iter()
                    .fold(Vec3::splat(f32::NEG_INFINITY), |a, b| a.max(*b));
                if hi.y < at.y - 0.2 || lo.y > at.y + 2. || hi.z < at.z - 0.5 || lo.z > at.z + 0.5 {
                    continue;
                }
                for p in p {
                    edge = edge.max(side * p.x);
                }
            }
        }
    }
    Vec3::new(side * (edge + 0.5), at.y, at.z)
}
pub fn doors(
    d: &VehicleDefinition,
    seat: usize,
    pose: &Transform,
    cache: &ContentCache,
    root: &Path,
) -> Vec<Vec3> {
    let at = Vec3::from_array(d.seats[seat.min(d.seats.len() - 1)]);
    if d.spec.access_required {
        return vec![at];
    }
    if let Some(door) = d.access.iter().find(|z| z.kind == "BiFold") {
        return vec![outboard(
            d,
            Vec3::new(door.center[0], at.y, door.center[2]),
            door.center[0].signum(),
            cache,
            root,
        )];
    }
    let side = if at.x.abs() > 0.15 {
        at.x.signum()
    } else if seat % 2 == 0 {
        -1.
    } else {
        1.
    };
    let mut result = vec![outboard(d, at, side, cache, root)];
    if at.x.abs() <= 0.15 || (pose.rotation * Vec3::Y).y < 0.5 {
        result.push(outboard(d, at, -side, cache, root));
    }
    result
}
pub fn probe(
    context: &RapierContext,
    vehicle: Entity,
    player: Entity,
    pose: &Transform,
    seat: Vec3,
    doors: &[Vec3],
) -> (bool, Vec3) {
    let from = pose.transform_point(seat) + Vec3::Y * 0.9;
    let exclude = |e, _: &bevy_rapier3d::rapier::geometry::Collider| e != vehicle && e != player;
    let filter = QueryFilter::default().predicate(&exclude);
    let mut first = pose.transform_point(doors[0]);
    for (i, local) in doors.iter().enumerate() {
        let at = pose.transform_point(*local);
        let delta = at + Vec3::Y * 0.9 - from;
        let length = delta.length();
        if length > 1e-5
            && context
                .cast_ray(from, delta / length, length, true, filter)
                .is_some()
        {
            continue;
        }
        let mut stand = at;
        if let Some((_, hit)) = context.cast_ray(
            at + Vec3::Y,
            Vec3::NEG_Y,
            2.2,
            true,
            QueryFilter::default().exclude_collider(player),
        ) {
            stand.y = at.y + 1. - hit + 0.05;
        }
        if i == 0 {
            first = stand;
        }
        let capsule = Collider::capsule(Vec3::Y * (0.25 + 0.35), Vec3::Y * (2. - 0.35), 0.35);
        let mut blocked = false;
        context.intersect_shape(stand, Quat::IDENTITY, &capsule, filter, |_, _| {
            blocked = true;
            false
        });
        if !blocked {
            return (true, stand);
        }
    }
    (false, first)
}
