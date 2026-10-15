//! Vehicle.Build's explicit hull branch and parked trailer supports.
use super::*;
use crate::content::{ObjData, cut::planar_hull};
use std::collections::BTreeMap;

pub(in crate::vehicle) type Shape = (Vec3, Quat, Collider);

pub(in crate::vehicle) fn enabled(spec: &super::super::data::VehicleSpec) -> bool {
    (spec.hull_bands.is_some() || spec.hull_boxes.is_some())
        && std::env::var("UG_SHIPBOX").as_deref() != Ok("1")
}

/// Original ConvexBand: inclusive source vertices, millimetre Y planes and
/// exact (not epsilon) XZ deduplication before the shared monotone chain.
pub(super) fn band_points(source: &ObjData, min: Vec3, max: Vec3) -> Option<Vec<Vec3>> {
    let mut planes: BTreeMap<i32, Vec<Vec3>> = BTreeMap::new();
    for p in &source.positions {
        let p = source_face_vertex(*p);
        if p.cmpge(min).all() && p.cmple(max).all() {
            let key = (p.y * 1000.).round_ties_even() as i32;
            planes
                .entry(key)
                .or_default()
                .push(Vec3::new(p.x, key as f32 / 1000., p.z));
        }
    }
    if planes.len() < 2 {
        return None;
    }
    let mut points = Vec::new();
    for mut plane in planes.into_values() {
        plane.sort_by(|a, b| a.x.total_cmp(&b.x).then(a.z.total_cmp(&b.z)));
        plane.dedup_by(|a, b| a.x == b.x && a.z == b.z);
        points.extend(planar_hull(plane, 0, 2));
    }
    (points.len() >= 4).then_some(points)
}

fn box_shape(shapes: &mut Vec<Shape>, size: [f32; 3], center: [f32; 3], yaw: f32) {
    shapes.push((
        Vec3::from_array(center),
        Quat::from_rotation_y(yaw.to_radians()),
        Collider::cuboid(size[0] * 0.5, size[1] * 0.5, size[2] * 0.5),
    ));
}

/// These survive ordinary car decomposition too. Landing legs remain deployed
/// until the separate source coupling/retraction authority is ported.
pub(super) fn extras(definition: &VehicleDefinition, shapes: &mut Vec<Shape>) {
    let s = &definition.spec;
    for (size, center) in s.extra_boxes.iter().flatten() {
        box_shape(shapes, *size, *center, 0.);
    }
    if s.landing_gear_size != [0.; 3] {
        box_shape(shapes, s.landing_gear_size, s.landing_gear_center, 0.);
    }
}

pub(in crate::vehicle) fn initial(
    definition: &VehicleDefinition,
    source: Option<&ObjData>,
    separate_hull: bool,
    authored: bool,
    box_hull: bool,
) -> (Vec<Shape>, bool) {
    let s = &definition.spec;
    let mut shapes = Vec::new();
    let mut pending = false;
    if authored {
        for (min, max) in s.hull_bands.iter().flatten() {
            if let Some(collider) = source
                .and_then(|mesh| band_points(mesh, (*min).into(), (*max).into()))
                .and_then(|points| Collider::convex_hull(&points))
            {
                shapes.push((Vec3::ZERO, Quat::IDENTITY, collider));
            }
        }
        for (size, center, yaw) in s.hull_boxes.iter().flatten() {
            box_shape(&mut shapes, *size, *center, *yaw);
        }
        // No shipping spec uses HullDecompose. Do not substitute the ordinary
        // whole-body car bake for that separate source region feature.
        if shapes.is_empty() && !separate_hull {
            box_shape(&mut shapes, s.box_size, s.box_center, 0.);
        }
    } else {
        pending = source.is_some() && !box_hull;
        box_shape(&mut shapes, s.box_size, s.box_center, 0.);
    }
    if let Some((size, center)) = definition.roof {
        box_shape(&mut shapes, size, center, 0.);
    }
    extras(definition, &mut shapes);
    (shapes, pending)
}

#[cfg(test)]
mod tests;
