//! PowerLineField's measured insulator anchors, paired pylon ends and sagging tubes.
use super::{
    ObjData, Placement,
    procedural::{ProceduralRegion, f, rows, value},
    roads::triangle,
};
use bevy::prelude::*;
use std::{collections::HashSet, sync::Arc};

const POLE: [Vec3; 4] = [
    Vec3::new(1.658, -0.331, 7.335),
    Vec3::new(0.880, -0.331, 7.335),
    Vec3::new(-0.880, -0.331, 6.612),
    Vec3::new(-1.658, -0.331, 6.612),
];
const PYLON: [Vec3; 6] = [
    Vec3::new(4.708, 0., 13.473),
    Vec3::new(5.952, 0., 19.209),
    Vec3::new(4.227, 0., 24.941),
    Vec3::new(-4.227, 0., 24.941),
    Vec3::new(-5.952, 0., 19.209),
    Vec3::new(-4.708, 0., 13.473),
];
const SPLIT: [f32; 6] = [1.178, 1.131, 1.139, 1.139, 1.131, 1.178];
const SAMPLES: usize = 14;
fn anchors(transform: Transform, toward: Vec3, pylon: bool) -> Vec<Vec3> {
    let anchors = if pylon { &PYLON[..] } else { &POLE[..] };
    anchors
        .iter()
        .enumerate()
        .map(|(i, a)| {
            if !pylon {
                return transform.transform_point(*a);
            }
            let near = transform.transform_point(Vec3::new(a.x, -SPLIT[i], a.z));
            let far = transform.transform_point(Vec3::new(a.x, SPLIT[i], a.z));
            if near.distance_squared(toward) <= far.distance_squared(toward) {
                near
            } else {
                far
            }
        })
        .collect()
}
fn span_point(a: Vec3, b: Vec3, t: f32) -> Vec3 {
    a.lerp(b, t) - Vec3::Y * (a.distance(b) * 0.035 * 4. * t * (1. - t))
}
fn wire(mesh: &mut ObjData, a: Vec3, b: Vec3) {
    let mut previous = None;
    for i in 0..SAMPLES {
        let t = i as f32 / (SAMPLES - 1) as f32;
        let p = span_point(a, b, t);
        let tangent = if i == 0 {
            span_point(a, b, 1. / (SAMPLES - 1) as f32) - p
        } else {
            p - span_point(a, b, (i - 1) as f32 / (SAMPLES - 1) as f32)
        }
        .normalize_or_zero();
        let up = if tangent.dot(Vec3::Y).abs() > 0.95 {
            Vec3::X
        } else {
            Vec3::Y
        };
        let n1 = tangent.cross(up).normalize_or_zero() * 0.045;
        let n2 = tangent.cross(n1).normalize_or_zero() * 0.045;
        let offsets = [n1, n2, -n1, -n2];
        if let Some((q, old, t0)) = previous {
            let old: [Vec3; 4] = old;
            for k in 0..4 {
                let next = (k + 1) % 4;
                let points = [q + old[k], q + old[next], p + offsets[next], p + offsets[k]];
                let normal = (old[k] + old[next] + offsets[k] + offsets[next]).normalize_or_zero();
                triangle(
                    mesh,
                    [points[0], points[1], points[2]],
                    [[t0, 0.], [t0, 1.], [t, 1.]],
                    [normal; 3],
                );
                triangle(
                    mesh,
                    [points[0], points[2], points[3]],
                    [[t0, 0.], [t, 1.], [t, 0.]],
                    [normal; 3],
                );
            }
        }
        previous = Some((p, offsets, t));
    }
}
#[derive(Default)]
struct Field {
    mesh: ObjData,
    edges: HashSet<([i32; 3], [i32; 3])>,
}
impl Field {
    fn span(&mut self, a: Transform, b: Transform, pylon: bool, center: Vec3, radius: f32) {
        if a.translation
            .xz()
            .distance(center.xz())
            .min(b.translation.xz().distance(center.xz()))
            > radius
        {
            return;
        }
        let distance = a.translation.distance(b.translation);
        if distance < 0.1 || distance > if pylon { 400. } else { 140. } {
            return;
        }
        let key = |p: Vec3| {
            [
                (p.x * 10.).round() as i32,
                (p.y * 10.).round() as i32,
                (p.z * 10.).round() as i32,
            ]
        };
        let (ka, kb) = (key(a.translation), key(b.translation));
        if !self.edges.insert(if ka < kb { (ka, kb) } else { (kb, ka) }) {
            return;
        }
        let an = anchors(a, b.translation, pylon);
        let bn = anchors(b, a.translation, pylon);
        for (a, b) in an.into_iter().zip(bn) {
            wire(&mut self.mesh, a, b);
        }
    }
}
fn transform(v: &serde_json::Value, next: bool, pylon: bool) -> Transform {
    let (x, z, h, dx, dz) = if next {
        ("nx", "nz", "nh", "nDirX", "nDirZ")
    } else {
        ("x", "z", "h", "dirX", "dirZ")
    };
    Transform {
        translation: Vec3::new(f(v, x), f(v, h) - if pylon { 0. } else { 0.3 }, f(v, z)),
        rotation: super::procedural::pole_rotation(f(v, dx), f(v, dz)),
        scale: Vec3::splat(if pylon { 1.6 } else { 1. }),
    }
}
pub fn generated(regions: &[Arc<ProceduralRegion>], center: Vec3, radius: f32) -> ObjData {
    let mut field = Field::default();
    for region in regions {
        for p in rows(region, "poles") {
            if value(p, "hasNext").as_bool() == Some(true) {
                field.span(
                    transform(p, false, false),
                    transform(p, true, false),
                    false,
                    center,
                    radius,
                );
            }
        }
        for p in rows(region, "pylons") {
            for far in value(p, "wired").as_array().into_iter().flatten() {
                field.span(
                    transform(p, false, true),
                    transform(far, false, true),
                    true,
                    center,
                    radius,
                );
            }
        }
    }
    field.mesh
}
/// Editor save endpoints are world-space pole origins. Resolve before culling,
/// so a visible pole retains its outgoing span even when the far end is unloaded.
pub fn saved(source: &str, placements: &[Placement], center: Vec3, radius: f32) -> ObjData {
    let poles: Vec<_> = placements
        .iter()
        .filter(|p| {
            matches!(
                p.name.as_str(),
                "Power_Line_0" | "Power_Line_0_Transformer" | "Power_Line_1"
            )
        })
        .collect();
    let nearest = |v: Vec3| {
        poles
            .iter()
            .filter(|p| p.transform.translation.distance_squared(v) < 4.)
            .min_by(|a, b| {
                a.transform
                    .translation
                    .distance_squared(v)
                    .total_cmp(&b.transform.translation.distance_squared(v))
            })
            .copied()
    };
    let mut field = Field::default();
    for line in source.lines().filter(|l| !l.trim_start().starts_with('#')) {
        let Ok(n) = line
            .split_whitespace()
            .map(str::parse::<f32>)
            .collect::<Result<Vec<_>, _>>()
        else {
            continue;
        };
        if n.len() < 6 || n.iter().any(|v| !v.is_finite()) {
            continue;
        }
        let (Some(a), Some(b)) = (
            nearest(Vec3::new(n[0], n[1], n[2])),
            nearest(Vec3::new(n[3], n[4], n[5])),
        ) else {
            continue;
        };
        let pylon = a.name == "Power_Line_1";
        if pylon == (b.name == "Power_Line_1") {
            field.span(a.transform, b.transform, pylon, center, radius);
        }
    }
    field.mesh
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn pylon_wire_ends_stay_on_near_insulator_with_measured_gap() {
        let transform = Transform {
            rotation: Quat::from_rotation_x(270_f32.to_radians()),
            scale: Vec3::splat(1.6),
            ..default()
        };
        let forward = anchors(transform, Vec3::Z * 200., true);
        let back = anchors(transform, Vec3::NEG_Z * 200., true);
        for i in 0..6 {
            assert!((forward[i].distance(back[i]) - SPLIT[i] * 3.2).abs() < 1e-5);
            assert!(forward[i].z > back[i].z);
            assert!((forward[i].y - PYLON[i].z * 1.6).abs() < 1e-5);
        }
    }
    #[test]
    fn span_has_source_sag_pinned_uvs_and_outward_faces() {
        let (a, b) = (Vec3::new(0., 20., 0.), Vec3::new(100., 20., 0.));
        assert_eq!(span_point(a, b, 0.5), Vec3::new(50., 16.5, 0.));
        let mut mesh = ObjData::default();
        wire(&mut mesh, a, b);
        assert_eq!(mesh.positions.len(), 13 * 4 * 6);
        assert!(mesh.uvs.iter().any(|uv| uv[0] == 0.));
        assert!(mesh.uvs.iter().any(|uv| uv[0] == 1.));
        for i in (0..mesh.positions.len()).step_by(3) {
            let p: Vec<_> = mesh.positions[i..i + 3]
                .iter()
                .map(|p| Vec3::from_array(*p))
                .collect();
            assert!(
                (p[1] - p[0])
                    .cross(p[2] - p[0])
                    .dot(Vec3::from_array(mesh.normals[i]))
                    > 0.
            );
        }
    }
}
