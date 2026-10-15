//! Original MeshCut.cs load-time triangle surgery. Source vertices/attributes
//! survive clipping; only newly generated cap winding adapts to Bevy's CCW.
use super::ObjData;
use bevy::prelude::*;

#[derive(Clone, Copy, Debug)]
pub(crate) struct Vertex {
    pub position: Vec3,
    pub normal: Vec3,
    pub uv: Vec2,
    pub color: Vec4,
}
impl Vertex {
    fn lerp(self, other: Self, fraction: f32) -> Self {
        Self {
            position: self.position.lerp(other.position, fraction),
            normal: self.normal.lerp(other.normal, fraction).normalize_or_zero(),
            uv: self.uv.lerp(other.uv, fraction),
            color: self.color.lerp(other.color, fraction),
        }
    }
}
#[derive(Clone, Default)]
pub(crate) struct Set {
    pub triangles: Vec<[Vertex; 3]>,
}
impl Set {
    pub fn read(source: &ObjData) -> Self {
        Self {
            triangles: (0..source.positions.len() / 3)
                .map(|t| {
                    [0, 1, 2].map(|corner| {
                        let i = t * 3 + corner;
                        Vertex {
                            position: source.positions[i].into(),
                            normal: source.normals[i].into(),
                            uv: source.uvs[i].into(),
                            color: source.colors[i].into(),
                        }
                    })
                })
                .collect(),
        }
    }
    pub fn commit(&self) -> ObjData {
        let mut result = ObjData {
            min: Vec3::splat(f32::INFINITY),
            max: Vec3::splat(f32::NEG_INFINITY),
            ..default()
        };
        for triangle in &self.triangles {
            for vertex in triangle {
                result.positions.push(vertex.position.to_array());
                result.normals.push(vertex.normal.to_array());
                result.uvs.push(vertex.uv.to_array());
                result.colors.push(vertex.color.to_array());
                result.min = result.min.min(vertex.position);
                result.max = result.max.max(vertex.position);
            }
        }
        result
    }
    /// Source SplitTri visits the low half first and records crossings once.
    pub fn split(&self, axis: usize, value: f32) -> (Self, Self, Vec<Vertex>) {
        let (mut below, mut above, mut cut) = (Self::default(), Self::default(), Vec::new());
        for &triangle in &self.triangles {
            split_triangle(
                triangle,
                axis,
                value,
                &mut below.triangles,
                &mut above.triangles,
                &mut cut,
            );
        }
        (below, above, cut)
    }
    /// Keep everything outside the box, clipping triangles that straddle it.
    /// The discarded vertices retain their source attributes for replacement art.
    pub fn subtract_box(&self, min: Vec3, max: Vec3) -> (Self, Vec<Vertex>) {
        let (mut kept, mut dropped) = (Self::default(), Vec::new());
        for &triangle in &self.triangles {
            if (0..3).any(|axis| {
                triangle.iter().all(|v| v.position[axis] <= min[axis])
                    || triangle.iter().all(|v| v.position[axis] >= max[axis])
            }) {
                kept.triangles.push(triangle);
                continue;
            }
            let mut inside = Self {
                triangles: vec![triangle],
            };
            for axis in 0..3 {
                let mut next = Self::default();
                for triangle in inside.triangles {
                    let one = Self {
                        triangles: vec![triangle],
                    };
                    let (below, above, _) = one.split(axis, min[axis]);
                    kept.triangles.extend(below.triangles);
                    for triangle in above.triangles {
                        let one = Self {
                            triangles: vec![triangle],
                        };
                        let (below, above, _) = one.split(axis, max[axis]);
                        kept.triangles.extend(above.triangles);
                        next.triangles.extend(below.triangles);
                    }
                }
                inside = next;
            }
            dropped.extend(inside.triangles.into_iter().flatten());
        }
        (kept, dropped)
    }
    /// Source Andrew monotone-chain cap, including 1e-6 squared duplicate radius.
    pub fn cap_hull(&mut self, cut: &[Vertex], axis: usize, normal: Vec3, uv: Vec2, color: Vec4) {
        if cut.len() < 3 {
            return;
        }
        let u = if axis == 0 { 1 } else { 0 };
        let w = if axis == 2 { 1 } else { 2 };
        let mut points: Vec<Vec3> = Vec::new();
        for vertex in cut {
            if !points
                .iter()
                .any(|p| p.distance_squared(vertex.position) < 1e-6)
            {
                points.push(vertex.position);
            }
        }
        if points.len() < 3 {
            return;
        }
        let hull = planar_hull(points, u, w);
        for i in 1..hull.len().saturating_sub(1) {
            self.face([hull[0], hull[i], hull[i + 1]], normal, uv, color);
        }
    }
    pub fn quad(&mut self, points: [Vec3; 4], normal: Vec3, uv: Vec2, color: Vec4) {
        self.face([points[0], points[1], points[2]], normal, uv, color);
        self.face([points[0], points[2], points[3]], normal, uv, color);
    }
    fn face(&mut self, mut points: [Vec3; 3], normal: Vec3, uv: Vec2, color: Vec4) {
        // MeshCut's explicit normal describes the visible side. Godot uses CW,
        // Bevy CCW; source caps have a constant UV/color so swapping corners is
        // the entire rendering adaptation (no geometry or normal changes).
        if (points[1] - points[0])
            .cross(points[2] - points[0])
            .dot(normal)
            < 0.
        {
            points.swap(1, 2);
        }
        self.triangles.push(points.map(|position| Vertex {
            position,
            normal,
            uv,
            color,
        }));
    }
}

/// Shared Andrew monotone chain. Callers retain their distinct source
/// duplicate rules (MeshCut epsilon vs Vehicle.ConvexBand exact equality).
pub(crate) fn planar_hull(mut points: Vec<Vec3>, u: usize, w: usize) -> Vec<Vec3> {
    if points.len() < 3 {
        return points;
    }
    points.sort_by(|a, b| a[u].total_cmp(&b[u]).then(a[w].total_cmp(&b[w])));
    let cross =
        |o: Vec3, a: Vec3, b: Vec3| (a[u] - o[u]) * (b[w] - o[w]) - (a[w] - o[w]) * (b[u] - o[u]);
    let mut hull: Vec<Vec3> = Vec::new();
    for &point in &points {
        while hull.len() >= 2 && cross(hull[hull.len() - 2], hull[hull.len() - 1], point) <= 0. {
            hull.pop();
        }
        hull.push(point);
    }
    let lower = hull.len() + 1;
    for &point in points[..points.len() - 1].iter().rev() {
        while hull.len() >= lower && cross(hull[hull.len() - 2], hull[hull.len() - 1], point) <= 0.
        {
            hull.pop();
        }
        hull.push(point);
    }
    hull.pop();
    hull
}

fn split_triangle(
    triangle: [Vertex; 3],
    axis: usize,
    value: f32,
    below: &mut Vec<[Vertex; 3]>,
    above: &mut Vec<[Vertex; 3]>,
    cut: &mut Vec<Vertex>,
) {
    for low in [true, false] {
        let mut polygon = Vec::with_capacity(5);
        for i in 0..3 {
            let a = triangle[i];
            let b = triangle[(i + 1) % 3];
            let da = a.position[axis] - value;
            let db = b.position[axis] - value;
            let inside_a = if low { da <= 0. } else { da >= 0. };
            let inside_b = if low { db <= 0. } else { db >= 0. };
            if inside_a {
                polygon.push(a);
            }
            if inside_a != inside_b {
                let middle = a.lerp(b, (da / (da - db)).clamp(0., 1.));
                polygon.push(middle);
                if low {
                    cut.push(middle);
                }
            }
        }
        let destination = if low { &mut *below } else { &mut *above };
        for i in 1..polygon.len().saturating_sub(1) {
            destination.push([polygon[0], polygon[i], polygon[i + 1]]);
        }
    }
}

#[cfg(test)]
#[path = "cut/tests.rs"]
mod tests;
