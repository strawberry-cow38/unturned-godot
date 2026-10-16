//! PlayerController.LookTrace/UpdateLookFocus and Vehicle.ResolveAccess.
//! Select the vehicle first; only then choose among all its original zones.
use super::{
    VehicleBody,
    data::{self, AccessZone, VehicleDefinition},
    ray_box,
};
use crate::{
    entities::InteractionFocus,
    player::{GameSnapshot, PlayerBody, PlayerCamera},
    ui::UiState,
};
use bevy::prelude::*;
use bevy_rapier3d::prelude::*;
use serde_json::json;

#[derive(SystemSet, Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub(crate) struct VehicleAccess;
const REACH: f32 = 2.6;
const SPHERE: f32 = 0.16;

fn segment_box(from: Vec3, to: Vec3, center: Vec3, size: Vec3) -> bool {
    ray_box(
        from,
        to - from,
        center - size * 0.5,
        center + size * 0.5,
        1.,
    )
    .is_some()
}
/// Source ties retain manifest order. Intersected zones rank by center distance
/// from the eye; otherwise use point-to-box distance from the clipped endpoint.
fn resolve(zones: &[AccessZone], from: Vec3, to: Vec3) -> Option<&AccessZone> {
    let mut hit = None;
    let mut near = None;
    let mut best_hit = f32::INFINITY;
    let mut best_near = f32::INFINITY;
    for zone in zones {
        let c = Vec3::from_array(zone.center);
        let s = Vec3::from_array(zone.size);
        if segment_box(from, to, c, s) {
            let distance = from.distance_squared(c);
            if distance < best_hit {
                best_hit = distance;
                hit = Some(zone);
            }
        } else {
            let delta = (c - s * 0.5 - to).max(Vec3::ZERO).max(to - (c + s * 0.5));
            let distance = delta.length_squared();
            if distance < best_near {
                best_near = distance;
                near = Some(zone);
            }
        }
    }
    hit.or_else(|| (best_near <= 1.5 * 1.5).then_some(near).flatten())
}
/// Original direct box children, including disabled BellyBox/RoofBox. Convex
/// hull bands are deliberately not substituted with oversized bounding boxes.
fn hits_hull(d: &VehicleDefinition, from: Vec3, to: Vec3) -> bool {
    let s = &d.spec;
    if s.hull_boxes.is_none()
        && s.hull_bands.is_none()
        && segment_box(from, to, s.box_center.into(), s.box_size.into())
    {
        return true;
    }
    for (size, center, yaw) in s.hull_boxes.iter().flatten() {
        let inv = Quat::from_rotation_y(yaw.to_radians()).inverse();
        let c = Vec3::from_array(*center);
        if segment_box(inv * (from - c), inv * (to - c), Vec3::ZERO, (*size).into()) {
            return true;
        }
    }
    for (size, center) in d.roof.iter().chain(s.extra_boxes.iter().flatten()) {
        if segment_box(from, to, (*center).into(), (*size).into()) {
            return true;
        }
    }
    s.landing_gear_size != [0.; 3]
        && segment_box(
            from,
            to,
            s.landing_gear_center.into(),
            s.landing_gear_size.into(),
        )
}

pub(crate) fn focus(
    snapshot: Res<GameSnapshot>,
    ui: Res<UiState>,
    cameras: Query<&Transform, With<PlayerCamera>>,
    bodies: Query<(Entity, &VehicleBody, &Transform)>,
    parents: Query<&ChildOf>,
    players: Query<(Entity, &Transform), With<PlayerBody>>,
    context: ReadRapierContext,
    mut focus: ResMut<InteractionFocus>,
    mut looked: Option<ResMut<super::hand_tools::LookedVehicle>>,
) {
    // Source _focusVehicle, also read by the held vehicle tools.
    if let Some(looked) = looked.as_mut() {
        looked.0 = None;
    }
    if ui.captures_input()
        || snapshot.0["player"]["alive"] != true
        || snapshot.0["player"]["vehicleNetId"].as_u64().unwrap_or(0) != 0
    {
        return;
    }
    let (Ok(eye), Ok(context)) = (cameras.single(), context.single()) else {
        return;
    };
    // Current source LookTrace explicitly selects the active camera in BOTH
    // first and third person (2026-09-04 override of older shoulder comments).
    let from = eye.translation;
    let direction = *eye.forward();
    let own = players.single().ok().map(|(entity,_)| entity);
    let exclude_owner = |entity, _: &bevy_rapier3d::rapier::geometry::Collider| {
        !own.is_some_and(|p| entity == p || parents.iter_ancestors(entity).any(|a| a == p))
    };
    // Source FinaliseHitboxLayers removes the convex chassis from player/look
    // queries after the visible mesh arrives. ALL membership bypasses that gate.
    let filter = QueryFilter::default()
        .exclude_sensors()
        .groups(CollisionGroups::new(
            Group::GROUP_2,
            Group::GROUP_1 | Group::GROUP_4,
        ))
        .predicate(&exclude_owner);
    let ray = context.cast_ray(from, direction, REACH, true, filter);
    let distance = ray.map_or(REACH, |(_, d)| d);
    let end = from + direction * distance;
    let owner = |entity| {
        std::iter::once(entity)
            .chain(parents.iter_ancestors(entity))
            .find(|e| bodies.contains(*e))
    };
    let mut chosen = None;
    let mut best = f32::INFINITY;
    context.intersect_shape(
        end,
        Quat::IDENTITY,
        &Collider::ball(SPHERE),
        QueryFilter::default()
            .exclude_sensors()
            .groups(CollisionGroups::new(Group::GROUP_2, Group::GROUP_4))
            .predicate(&exclude_owner),
        |entity, _| {
            if let Some(root) = owner(entity)
                && let Ok((_, _, pose)) = bodies.get(root)
            {
                let d = pose.translation.distance_squared(end);
                if d < best {
                    best = d;
                    chosen = Some(root);
                }
            }
            true
        },
    );
    // The original sphere finds the owning child. A ray hit is also a reliable
    // witness at numerical triangle boundaries where a zero-depth overlap is lost.
    if chosen.is_none() {
        chosen = ray.and_then(|(entity, _)| owner(entity));
    }
    if chosen.is_none() && !focus.has_candidate() {
        let mut best = (REACH + 6.).powi(2);
        for (entity, body, pose) in &bodies {
            let d = from.distance_squared(pose.translation);
            let Some(def) = data::definition(body.type_id) else {
                continue;
            };
            let inv = pose.compute_affine().inverse();
            if d < best && hits_hull(def, inv.transform_point3(from), inv.transform_point3(end)) {
                best = d;
                chosen = Some(entity);
            }
        }
    }
    let Some(entity) = chosen else {
        return;
    };
    if let Some(looked) = looked.as_mut() {
        looked.0 = Some(entity);
    }
    let Ok((_, body, pose)) = bodies.get(entity) else {
        return;
    };
    let Some(def) = data::definition(body.type_id) else {
        return;
    };
    if let Ok((_, player)) = players.single() {
        if super::coupling::input::offer(&mut focus, body, pose,
            bodies.iter().map(|(_,body,pose)|(body,pose)), player.translation,
            distance, from, direction) { return; }
    }
    let inv = pose.compute_affine().inverse();
    let zone = resolve(
        &def.access,
        inv.transform_point3(from),
        inv.transform_point3(end),
    );
    match zone.map(|z| z.kind.as_str()) {
        Some("Hood") if body.flags & 32 == 0 => focus.offer_manual(
            distance,
            "Open hood",
            "openVehicleHood",
            json!({"netId":body.net_id}),
        ),
        Some("Trunk") => focus.offer_manual(
            distance,
            "Open trunk",
            "openVehicleTrunk",
            json!({"netId":body.net_id}),
        ),
        Some("BiFold") => super::bifold::input::offer(
            &mut focus,
            &snapshot.0,
            body.net_id,
            distance,
            from,
            direction,
        ),
        Some("Door") => focus.offer(
            distance,
            format!("Enter {}", def.spec.name),
            "enterVehicle",
            json!({"netId":body.net_id,"seat":zone.unwrap().seat.max(0)}),
            false,
        ),
        None if !def.spec.access_required => focus.offer(
            distance,
            format!("Enter {}", def.spec.name),
            "enterVehicle",
            json!({"netId":body.net_id,"seat":0}),
            false,
        ),
        // Unimplemented access kinds never silently become a driver door.
        _ => {}
    }
}

#[cfg(test)]
mod tests;
