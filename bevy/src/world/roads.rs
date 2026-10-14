//! Retail cubic road splines, ported from game/RoadField.cs (including its
//! cross-slope banking, width/depth adjustments and sealed collision underside).
use super::{ContentError, ObjData, WorldGeometry};
use bevy::prelude::*;
use std::io::{Cursor, Read};

#[derive(Debug)]
pub struct RoadMaterial {
    pub width: f32,
    pub height: f32,
    pub depth: f32,
    pub offset: f32,
    pub concrete: bool,
}
#[derive(Debug, Default)]
pub struct Joint {
    pub vertex: Vec3,
    pub tan0: Vec3,
    pub tan1: Vec3,
    pub offset: f32,
    pub ignore_terrain: bool,
    pub mode: u8,
}
#[derive(Debug)]
pub struct Road {
    pub material: usize,
    pub looping: bool,
    pub guid: Vec<u8>,
    pub joints: Vec<Joint>,
}
struct Reader<'a>(Cursor<&'a [u8]>);
impl Reader<'_> {
    fn bytes<const N: usize>(&mut self) -> Result<[u8; N], ContentError> {
        let mut b = [0; N];
        self.0.read_exact(&mut b)?;
        Ok(b)
    }
    fn byte(&mut self) -> Result<u8, ContentError> {
        Ok(self.bytes::<1>()?[0])
    }
    fn short(&mut self) -> Result<u16, ContentError> {
        Ok(u16::from_le_bytes(self.bytes()?))
    }
    fn float(&mut self) -> Result<f32, ContentError> {
        let v = f32::from_le_bytes(self.bytes()?);
        if !v.is_finite() {
            return Err(ContentError("non-finite road coordinate".into()));
        }
        Ok(v)
    }
    fn vector(&mut self) -> Result<Vec3, ContentError> {
        Ok(Vec3::new(self.float()?, self.float()?, -self.float()?))
    }
}
pub fn decode_materials(bytes: &[u8]) -> Result<Vec<RoadMaterial>, ContentError> {
    let mut r = Reader(Cursor::new(bytes));
    let version = r.byte()?;
    if version > 2 {
        return Err(ContentError(format!(
            "unsupported Roads.dat version {version}"
        )));
    }
    let count = r.byte()?;
    (0..count)
        .map(|_| {
            let material = RoadMaterial {
                width: r.float()?,
                height: r.float()?,
                depth: r.float()?,
                offset: if version > 1 { r.float()? } else { 0. },
                concrete: r.byte()? != 0,
            };
            if material.width < 0. || material.depth < 0. {
                return Err(ContentError("negative road width/depth".into()));
            }
            Ok(material)
        })
        .collect()
}
pub fn decode_paths(bytes: &[u8]) -> Result<Vec<Road>, ContentError> {
    let mut r = Reader(Cursor::new(bytes));
    let version = r.byte()?;
    if version <= 1 {
        return Ok(Vec::new());
    }
    if version > 6 {
        return Err(ContentError(format!(
            "unsupported Paths.dat version {version}"
        )));
    }
    let count = r.short()?;
    let mut roads = Vec::with_capacity(count as usize);
    for _ in 0..count {
        let length = r.short()?;
        let material = r.byte()? as usize;
        let looping = version > 2 && r.byte()? != 0;
        let mut guid = vec![];
        if version >= 6 {
            guid.resize(r.short()? as usize, 0);
            r.0.read_exact(&mut guid)?;
        }
        let mut joints = Vec::with_capacity(length as usize);
        for _ in 0..length {
            let mut joint = Joint {
                vertex: r.vector()?,
                ..default()
            };
            if version > 2 {
                joint.tan0 = r.vector()?;
                joint.tan1 = r.vector()?;
                joint.mode = r.byte()?;
            }
            if version > 4 {
                joint.offset = r.float()?;
            }
            if version > 3 {
                joint.ignore_terrain = r.byte()? != 0;
            }
            joints.push(joint);
        }
        roads.push(Road {
            material,
            looping,
            guid,
            joints,
        });
    }
    Ok(roads)
}
impl Road {
    fn position(&self, index: usize, t: f32) -> Vec3 {
        let s = &self.joints[index];
        let e = &self.joints[(index + 1) % self.joints.len()];
        let u = 1. - t;
        u * u * u * s.vertex
            + 3. * u * u * t * (s.vertex + s.tan1)
            + 3. * u * t * t * (e.vertex + e.tan0)
            + t * t * t * e.vertex
    }
    fn sample_walk(&self) -> Vec<(usize, f32)> {
        if self.joints.len() < 2 {
            return Vec::new();
        }
        let mut samples = Vec::new();
        let mut carry = 0.;
        for index in 0..self.joints.len() - usize::from(!self.looping) {
            let mut previous = self.position(index, 0.);
            let mut length = 0_f32;
            for step in 1..=16 {
                let p = self.position(index, step as f32 / 16.);
                length += p.distance(previous);
                previous = p;
            }
            let length = length.max(0.001);
            let mut step = carry;
            while step < length {
                samples.push((index, step / length));
                step += 5.;
            }
            carry = step - length;
        }
        samples.push(if self.looping {
            (0, 0.)
        } else {
            (self.joints.len() - 2, 1.)
        });
        samples
    }
}
struct Frame {
    p: Vec3,
    direction: Vec3,
    normal: Vec3,
    side: Vec3,
    offset: f32,
}
fn frame(road: &Road, index: usize, t: f32, terrain: &WorldGeometry) -> Frame {
    let mut p = road.position(index, t);
    let ignore = road.joints[index].ignore_terrain;
    if !ignore {
        p.y = terrain.terrain_height(p.x, p.z).unwrap_or(p.y);
    }
    let d = road.position(index, (t + 0.02).min(1.)) - road.position(index, (t - 0.02).max(0.));
    let direction = if d.length_squared() > 1e-6 {
        d.normalize()
    } else {
        Vec3::NEG_Z
    };
    let normal = if ignore {
        Vec3::Y
    } else {
        let h = |x, z| terrain.terrain_height(x, z).unwrap_or(p.y);
        Vec3::new(
            h(p.x - 4., p.z) - h(p.x + 4., p.z),
            8.,
            h(p.x, p.z - 4.) - h(p.x, p.z + 4.),
        )
        .normalize()
    };
    let side = direction.cross(normal).normalize_or_zero();
    let a = road.joints[index].offset;
    let b = road.joints[(index + 1) % road.joints.len()].offset;
    let offset = a + (b - a) * t;
    p.y += offset;
    Frame {
        p,
        direction,
        normal,
        side,
        offset,
    }
}
struct Ring {
    vertices: [Vec3; 4],
    normal: Vec3,
    v: f32,
}
fn cap(f: &Frame, sign: f32, half_width: f32, depth: f32, offset: f32, v: f32) -> Ring {
    let low = f.normal * (offset - depth) + f.direction * (4. * depth * sign);
    Ring {
        vertices: [
            f.p + f.side * (half_width + 2. * depth) + low,
            f.p + f.side * half_width + low,
            f.p - f.side * half_width + low,
            f.p - f.side * (half_width + 2. * depth) + low,
        ],
        normal: f.normal,
        v,
    }
}
fn triangle(out: &mut ObjData, mut p: [Vec3; 3], mut uv: [[f32; 2]; 3], mut n: [Vec3; 3]) {
    let cross = (p[1] - p[0]).cross(p[2] - p[0]);
    if cross.length_squared() < 1e-12 {
        return;
    }
    if cross.dot(n[0] + n[1] + n[2]) < 0. {
        p.swap(1, 2);
        uv.swap(1, 2);
        n.swap(1, 2);
    }
    for i in 0..3 {
        out.positions.push(p[i].to_array());
        out.normals.push(n[i].to_array());
        out.uvs.push(uv[i]);
        out.colors.push([1.; 4]);
        out.min = out.min.min(p[i]);
        out.max = out.max.max(p[i]);
    }
}
/// Returns visible road shell and the closed collision shell, bounded to loaded
/// world area. UV distance still accumulates from the original road start.
pub fn build(
    road: &Road,
    mat: &RoadMaterial,
    terrain: &WorldGeometry,
    center: Vec3,
    radius: f32,
) -> (ObjData, ObjData) {
    let empty = || ObjData {
        min: Vec3::splat(f32::INFINITY),
        max: Vec3::splat(f32::NEG_INFINITY),
        ..default()
    };
    let (mut visible, mut solid) = (empty(), empty());
    let samples = road.sample_walk();
    if samples.len() < 2 {
        return (visible, solid);
    }
    let half_width = mat.width * 1.15;
    let depth = mat.depth * 1.1;
    let tex_height = [128., 128., 256., 2., 256., 64., 256., 256., 256., 256.]
        .get(road.material)
        .copied()
        .unwrap_or(256.);
    let repeat = if mat.height != 0. {
        mat.height / tex_height
    } else {
        1. / tex_height
    };
    let mut rings = Vec::new();
    let mut distance = 0.;
    let mut previous = Vec3::ZERO;
    let mut last_frame = None;
    for (i, (index, t)) in samples.iter().copied().enumerate() {
        let f = frame(road, index, t, terrain);
        if i > 0 {
            distance += f.p.distance(previous);
        }
        if i == 0 && !road.looping {
            rings.push(cap(&f, -1., half_width, depth, mat.offset, 0.));
        }
        let mut left = f.p + f.side * half_width;
        let mut right = f.p - f.side * half_width;
        if !road.joints[index].ignore_terrain {
            left.y = terrain
                .terrain_height(left.x, left.z)
                .unwrap_or(f.p.y - f.offset)
                + f.offset;
            right.y = terrain
                .terrain_height(right.x, right.z)
                .unwrap_or(f.p.y - f.offset)
                + f.offset;
        } else {
            left.y = f.p.y;
            right.y = f.p.y;
        }
        left += f.normal * (depth + mat.offset);
        right += f.normal * (depth + mat.offset);
        rings.push(Ring {
            vertices: [
                left + f.side * (2. * depth) - f.normal * (2. * depth),
                left,
                right,
                right - f.side * (2. * depth) - f.normal * (2. * depth),
            ],
            normal: f.normal,
            v: distance * repeat,
        });
        previous = f.p;
        last_frame = Some(f);
    }
    if !road.looping {
        rings.push(cap(
            &last_frame.unwrap(),
            1.,
            half_width,
            depth,
            mat.offset,
            distance * repeat,
        ));
    }
    for pair in rings.windows(2) {
        let (a, b) = (&pair[0], &pair[1]);
        let mid = (a.vertices[1] + a.vertices[2] + b.vertices[1] + b.vertices[2]) * 0.25;
        let segment_radius = a.vertices[1].xz().distance(b.vertices[1].xz()) * 0.5;
        if mid.xz().distance(center.xz()) > radius + half_width + segment_radius {
            continue;
        }
        let u = [0., 0., 1., 1.];
        for q in 0..3 {
            triangle(
                &mut visible,
                [a.vertices[q], a.vertices[q + 1], b.vertices[q + 1]],
                [[u[q], a.v], [u[q + 1], a.v], [u[q + 1], b.v]],
                [a.normal, a.normal, b.normal],
            );
            triangle(
                &mut visible,
                [a.vertices[q], b.vertices[q + 1], b.vertices[q]],
                [[u[q], a.v], [u[q + 1], b.v], [u[q], b.v]],
                [a.normal, b.normal, b.normal],
            );
        }
        triangle(
            &mut solid,
            [a.vertices[0], a.vertices[3], b.vertices[3]],
            [[0.; 2]; 3],
            [-a.normal, -a.normal, -b.normal],
        );
        triangle(
            &mut solid,
            [a.vertices[0], b.vertices[3], b.vertices[0]],
            [[0.; 2]; 3],
            [-a.normal, -b.normal, -b.normal],
        );
    }
    solid.positions.extend_from_slice(&visible.positions);
    solid.normals.extend_from_slice(&visible.normals);
    solid.uvs.extend_from_slice(&visible.uvs);
    solid.colors.extend_from_slice(&visible.colors);
    solid.min = solid.min.min(visible.min);
    solid.max = solid.max.max(visible.max);
    (visible, solid)
}
#[cfg(test)]
mod tests {
    use super::*;
    fn straight() -> Road {
        Road {
            material: 0,
            looping: false,
            guid: vec![],
            joints: vec![
                Joint {
                    vertex: Vec3::ZERO,
                    tan1: Vec3::new(0., 0., -10.),
                    ..default()
                },
                Joint {
                    vertex: Vec3::new(0., 0., -30.),
                    tan0: Vec3::new(0., 0., 10.),
                    ..default()
                },
            ],
        }
    }
    #[test]
    fn spline_keeps_source_spacing_half_width_depth_and_repeat() {
        let road = straight();
        let m = RoadMaterial {
            width: 8.,
            height: 16.,
            depth: 0.2,
            offset: 0.,
            concrete: true,
        };
        assert_eq!(road.sample_walk().len(), 7);
        let (mesh, solid) = build(&road, &m, &WorldGeometry::default(), Vec3::ZERO, 100.);
        assert!((mesh.max.x - 9.64).abs() < 1e-5);
        assert!((mesh.max.y - 0.22).abs() < 1e-5);
        assert!((mesh.min.y + 0.22).abs() < 1e-5);
        assert!(mesh.uvs.iter().any(|uv| (uv[1] - 3.75).abs() < 1e-5));
        assert!(solid.positions.len() > mesh.positions.len());
        for (p, n) in mesh
            .positions
            .chunks_exact(3)
            .zip(mesh.normals.chunks_exact(3))
        {
            let p: Vec<_> = p.iter().map(|x| Vec3::from_array(*x)).collect();
            assert!((p[1] - p[0]).cross(p[2] - p[0]).dot(Vec3::from_array(n[0])) >= 0.);
        }
    }
    #[test]
    fn retail_path_v6_keeps_guid_tangents_offsets_and_z_conversion() {
        let mut bytes = vec![6, 1, 0, 1, 0, 3, 1, 2, 0, 0xaa, 0xbb];
        for x in [2_f32, 3., 4., 1., 2., 3., 4., 5., 6.] {
            bytes.extend(x.to_le_bytes());
        }
        bytes.push(2);
        bytes.extend(0.75_f32.to_le_bytes());
        bytes.push(1);
        let roads = decode_paths(&bytes).unwrap();
        let r = &roads[0];
        assert_eq!(r.guid, vec![0xaa, 0xbb]);
        assert!(r.looping);
        assert_eq!(r.material, 3);
        assert_eq!(r.joints[0].vertex, Vec3::new(2., 3., -4.));
        assert_eq!(r.joints[0].tan1, Vec3::new(4., 5., -6.));
        assert_eq!(r.joints[0].offset, 0.75);
        assert!(r.joints[0].ignore_terrain);
        assert!(decode_paths(&bytes[..bytes.len() - 1]).is_err());
        assert!(decode_paths(&[7, 0, 0]).is_err());
    }
}
