//! RegionStreamer tunnel sweeps, twin-arch headwalls, replacement mouth terrain
//! and bed floors. The bore profile is measured from the original prism mesh.
use super::{
    ObjData,
    procedural::{ProceduralRegion, f, rows, value},
    roads::triangle,
};
use bevy::prelude::*;
use serde_json::Value;
use std::{
    collections::{BTreeMap, HashMap, HashSet},
    sync::Arc,
};
fn list<'a>(v: &'a Value, key: &str) -> &'a [Value] {
    value(v, key).as_array().map(Vec::as_slice).unwrap_or(&[])
}
fn number(values: &[Value], i: usize) -> f32 {
    values.get(i).and_then(Value::as_f64).unwrap_or(0.) as f32
}
fn xyz(v: &Value, x: &str, y: &str, z: &str, i: usize) -> Vec3 {
    Vec3::new(
        number(list(v, x), i),
        number(list(v, y), i),
        number(list(v, z), i),
    )
}
/// TunnelMesh.ProfileFrom welds to millimetres and walks each open end-plane chain.
fn profiles(mesh: &ObjData) -> Vec<Vec<Vec2>> {
    let mut ids = HashMap::new();
    let mut positions = Vec::new();
    let mut rep = Vec::new();
    for p in &mesh.positions {
        let key = p.map(|v| (v * 1000.).round() as i32);
        let next = positions.len();
        let id = *ids.entry(key).or_insert_with(|| {
            positions.push(Vec3::new(key[0] as f32, key[1] as f32, key[2] as f32) / 1000.);
            next
        });
        rep.push(id);
    }
    let low = mesh
        .positions
        .iter()
        .map(|p| p[1])
        .fold(f32::INFINITY, f32::min);
    let mut adjacent: BTreeMap<usize, Vec<usize>> = BTreeMap::new();
    for tri in rep.chunks_exact(3) {
        for i in 0..3 {
            let (a, b) = (tri[i], tri[(i + 1) % 3]);
            if a == b
                || (positions[a].y - low).abs() >= 0.01
                || (positions[b].y - low).abs() >= 0.01
            {
                continue;
            }
            for (a, b) in [(a, b), (b, a)] {
                let e = adjacent.entry(a).or_default();
                if !e.contains(&b) {
                    e.push(b);
                }
            }
        }
    }
    let mut starts: Vec<_> = adjacent
        .iter()
        .map(|(&id, neighbors)| (neighbors.len() != 1, id))
        .collect();
    starts.sort();
    let mut used = HashSet::new();
    let mut out = Vec::new();
    for (_, start) in starts {
        if !used.insert(start) {
            continue;
        }
        let mut chain = vec![positions[start].xz()];
        let mut current = start;
        while let Some(&next) = adjacent[&current].iter().find(|v| !used.contains(*v)) {
            used.insert(next);
            chain.push(positions[next].xz());
            current = next;
        }
        if chain.len() >= 2 {
            out.push(chain);
        }
    }
    out
}
fn sweep(chain: &[Vec2], centers: &[Vec3], lateral: f32) -> ObjData {
    let mut mesh = ObjData::default();
    if centers.len() < 2 {
        return mesh;
    }
    let mut frames = Vec::new();
    let mut along = vec![0.; centers.len()];
    for i in 0..centers.len() {
        let dir = if i == 0 {
            centers[1] - centers[0]
        } else if i == centers.len() - 1 {
            centers[i] - centers[i - 1]
        } else {
            centers[i + 1] - centers[i - 1]
        };
        let right = Vec3::Y.cross(dir).try_normalize().unwrap_or(Vec3::X);
        frames.push(right);
        if i > 0 {
            along[i] = along[i - 1] + centers[i].distance(centers[i - 1]);
        }
    }
    let mut across = vec![0.; chain.len()];
    for i in 1..chain.len() {
        across[i] = across[i - 1] + chain[i].distance(chain[i - 1]);
    }
    let width = across.last().copied().unwrap_or(1.).max(0.0001);
    let at =
        |i: usize, k: usize| centers[i] + frames[i] * (chain[k].x * lateral) + Vec3::Y * chain[k].y;
    for i in 0..centers.len() - 1 {
        for k in 0..chain.len() - 1 {
            let [a, b, c, d] = [at(i, k), at(i, k + 1), at(i + 1, k + 1), at(i + 1, k)];
            let mut normal = (b - a).cross(d - a).try_normalize().unwrap_or(Vec3::Y);
            if normal.dot((a + b + c + d) * 0.25 - (centers[i] + centers[i + 1]) * 0.5) < 0. {
                normal = -normal;
            }
            let uv = [
                [across[k] / width, along[i]],
                [across[k + 1] / width, along[i]],
                [across[k + 1] / width, along[i + 1]],
                [across[k] / width, along[i + 1]],
            ];
            triangle(&mut mesh, [a, b, c], [uv[0], uv[1], uv[2]], [normal; 3]);
            triangle(&mut mesh, [a, c, d], [uv[0], uv[2], uv[3]], [normal; 3]);
        }
    }
    mesh
}
fn interpolate(points: &[(f32, f32)], x: f32) -> f32 {
    if points.is_empty() {
        return 0.;
    }
    if x <= points[0].0 {
        return points[0].1;
    }
    for p in points.windows(2) {
        if x <= p[1].0 {
            return p[0].1 + (p[1].1 - p[0].1) * (x - p[0].0) / (p[1].0 - p[0].0).max(1e-6);
        }
    }
    points.last().unwrap().1
}
fn sorted(points: &mut Vec<(f32, f32)>) {
    points.sort_by(|a, b| a.0.total_cmp(&b.0));
    points.dedup_by(|a, b| (a.0 - b.0).abs() < 1e-5 && (a.1 - b.1).abs() < 1e-5);
}
/// Both headwall contours are X-monotone. Split them at every original contour
/// vertex and stitch vertical strips: exactly the source polygon, without an
/// engine-specific polygon triangulator or triangles spanning a bore opening.
fn wall(
    mesh: &mut ObjData,
    upper: &[(f32, f32)],
    lower: &[(f32, f32)],
    origin: Vec3,
    side: Vec3,
    normal: Vec3,
) {
    let mut xs: Vec<_> = upper.iter().chain(lower).map(|p| p.0).collect();
    xs.sort_by(f32::total_cmp);
    xs.dedup_by(|a, b| (*a - *b).abs() < 1e-5);
    for x in xs.windows(2) {
        // A bore has vertical feet: identical X with different heights. Pick the
        // contour segment containing this strip's interior, preserving both sides
        // of that discontinuity rather than dropping one of the measured points.
        let mid = (x[0] + x[1]) * 0.5;
        let segment = lower
            .windows(2)
            .find(|p| p[0].0 <= mid && p[1].0 >= mid && p[1].0 > p[0].0);
        let low = |u: f32| {
            segment
                .map(|p| p[0].1 + (p[1].1 - p[0].1) * (u - p[0].0) / (p[1].0 - p[0].0))
                .unwrap_or_else(|| interpolate(lower, u))
        };
        let a = origin + side * x[0] + Vec3::Y * low(x[0]);
        let b = origin + side * x[1] + Vec3::Y * low(x[1]);
        let c = origin + side * x[0] + Vec3::Y * interpolate(upper, x[0]);
        let d = origin + side * x[1] + Vec3::Y * interpolate(upper, x[1]);
        triangle(mesh, [a, b, c], [[0.; 2]; 3], [normal; 3]);
        triangle(mesh, [b, d, c], [[0.; 2]; 3], [normal; 3]);
    }
}
fn headwall(mesh: &mut ObjData, t: &Value, md: &Value, style: &Value, bore: &[Vec2], end: usize) {
    let count = list(t, "x").len();
    let (a, b) = if end == 0 {
        (0, 1)
    } else {
        (count - 1, count - 2)
    };
    let origin = xyz(t, "x", "y", "z", a);
    let toward = xyz(t, "x", "y", "z", b) - origin;
    let fwd = Vec3::new(toward.x, 0., toward.z).normalize_or_zero();
    let side = Vec3::new(-fwd.z, 0., fwd.x) * if end == 0 { 1. } else { -1. };
    let normal = if end == 0 { -fwd } else { fwd };
    let (reach, feet, lateral) = (f(style, "reach"), f(style, "feet"), f(style, "lateral"));
    let shell: Vec<_> = list(style, "shellProfile")
        .iter()
        .filter_map(|p| {
            value(p, "top")
                .as_f64()
                .map(|h| (f(p, "lateral"), h as f32))
        })
        .collect();
    let mut high = Vec::new();
    let mut low = Vec::new();
    for (i, u) in list(md, "faceU").iter().enumerate() {
        let u = u.as_f64().unwrap_or(0.) as f32;
        let (front, back) = (
            number(list(md, "faceFront"), i) - origin.y,
            number(list(md, "faceBehind"), i) - origin.y,
        );
        high.push((u, front.max(back)));
        low.push((u, front.min(back)));
    }
    if high.len() < 2 {
        let wing = f(style, "boreReach") + 10.5;
        let top = interpolate(&shell, reach) + 0.35;
        high = vec![(-wing, top), (wing, top)];
        low = vec![(-wing, -feet), (wing, -feet)];
    }
    let bottom = |u: f32| (interpolate(&low, u) - 1.).min(-feet);
    let (left, right) = (high[0].0.min(-reach), high.last().unwrap().0.max(reach));
    let mut us: Vec<_> = high.iter().map(|p| p.0).chain([left, right]).collect();
    let mut u = -reach;
    while u <= reach + 0.001 {
        us.push(u.clamp(-reach, reach));
        u += 0.25;
    }
    us.sort_by(f32::total_cmp);
    us.dedup_by(|a, b| (*a - *b).abs() < 1e-5);
    let upper: Vec<_> = us
        .iter()
        .map(|&u| {
            (
                u,
                interpolate(&high, u).max(if u.abs() <= reach {
                    interpolate(&shell, u)
                } else {
                    f32::NEG_INFINITY
                }),
            )
        })
        .collect();
    let half = bore.iter().map(|p| p.x.abs() * lateral).fold(0., f32::max);
    let offsets: Vec<f32> = list(style, "tubeOffsets")
        .iter()
        .filter_map(|v| v.as_f64().map(|v| v as f32))
        .collect();
    let mut lower: Vec<_> = us
        .into_iter()
        .filter(|u| {
            !offsets
                .iter()
                .any(|o| *u >= o - half - 0.01 && *u <= o + half + 0.01)
        })
        .map(|u| (u, bottom(u)))
        .collect();
    for offset in offsets {
        lower.push((offset - half - 0.01, bottom(offset - half - 0.01)));
        let mut chain = bore.to_vec();
        if chain.first().unwrap().x > chain.last().unwrap().x {
            chain.reverse();
        }
        lower.extend(chain.iter().map(|p| (offset + p.x * lateral, p.y)));
        lower.push((offset + half + 0.01, bottom(offset + half + 0.01)));
    }
    sorted(&mut lower);
    wall(mesh, &upper, &lower, origin, side, normal);
}
fn floor(mesh: &mut ObjData, t: &Value, style: &Value) {
    let centers: Vec<_> = (0..list(t, "x").len())
        .map(|i| xyz(t, "x", "y", "z", i))
        .collect();
    let mut along = vec![0.; centers.len()];
    for i in 1..centers.len() {
        along[i] = along[i - 1] + centers[i].xz().distance(centers[i - 1].xz());
    }
    let length = *along.last().unwrap();
    if length <= 0.001 {
        return;
    }
    let at = |s: f32| {
        let s = s.clamp(0., length);
        let i = along
            .windows(2)
            .position(|w| s <= w[1])
            .unwrap_or(centers.len() - 2);
        centers[i].lerp(
            centers[i + 1],
            ((s - along[i]) / (along[i + 1] - along[i]).max(1e-6)).clamp(0., 1.),
        )
    };
    let point = |s: f32| {
        if s < 0. {
            let d = at(2_f32.min(length)) - at(0.);
            at(0.) + d / d.xz().length().max(1e-6) * s
        } else if s > length {
            let d = at(length) - at((length - 2.).max(0.));
            at(length) + d / d.xz().length().max(1e-6) * (s - length)
        } else {
            at(s)
        }
    };
    let steps = ((length + 1.) / 2.).ceil().max(1.) as usize;
    for q in 0..steps {
        let pa = point(-0.5 + (length + 1.) * q as f32 / steps as f32);
        let pb = point(-0.5 + (length + 1.) * (q + 1) as f32 / steps as f32);
        let dir = (pb - pa).xz().normalize_or_zero();
        let across = Vec3::new(-dir.y, 0., dir.x) * f(style, "boreReach");
        let down = Vec3::NEG_Y * (f(style, "bedBelow") + 0.03);
        let [a, b, c, d] = [
            pa + across + down,
            pa - across + down,
            pb + across + down,
            pb - across + down,
        ];
        triangle(mesh, [a, b, c], [[0.; 2]; 3], [Vec3::Y; 3]);
        triangle(mesh, [b, d, c], [[0.; 2]; 3], [Vec3::Y; 3]);
    }
}
fn mouth(out: &mut BTreeMap<String, ObjData>, md: &Value) {
    let (w, h) = (f(md, "splatW") as usize, f(md, "splatH") as usize);
    if w == 0 || h == 0 {
        return;
    }
    for tri in list(md, "tris").chunks_exact(3) {
        let ids: [usize; 3] = std::array::from_fn(|i| tri[i].as_u64().unwrap_or(0) as usize);
        let uv = Vec2::new(
            ids.iter().map(|&i| number(list(md, "u"), i)).sum::<f32>(),
            ids.iter().map(|&i| number(list(md, "v"), i)).sum::<f32>(),
        ) / 3.;
        let (x, z) = (
            (uv.x * w as f32).max(0.) as usize,
            (uv.y * h as f32).max(0.) as usize,
        );
        let layer = number(list(md, "splat"), z.min(h - 1) * w + x.min(w - 1)) as usize;
        let p = ids.map(|i| xyz(md, "x", "y", "z", i));
        let n = ids.map(|i| xyz(md, "nx", "ny", "nz", i).normalize_or_zero());
        triangle(
            out.entry(format!("terrain:{}", layer.min(7))).or_default(),
            p,
            p.map(|p| [p.x / 16., p.z / 16.]),
            n,
        );
    }
}
pub fn build(
    regions: &[Arc<ProceduralRegion>],
    profile_mesh: &ObjData,
) -> BTreeMap<String, ObjData> {
    let mut out = BTreeMap::new();
    let Some(profile) = profiles(profile_mesh).into_iter().min_by(|a, b| {
        let width = |c: &Vec<Vec2>| c.iter().map(|p| p.x.abs()).fold(0., f32::max);
        width(a).total_cmp(&width(b))
    }) else {
        return out;
    };
    let mut seen = HashSet::new();
    for region in regions {
        let mouths: Vec<_> = rows(region, "tunnelMouths").collect();
        for (index, t) in rows(region, "tunnels").enumerate() {
            if list(t, "x").len() < 2 {
                continue;
            }
            let key = [
                number(list(t, "x"), 0).to_bits(),
                number(list(t, "z"), 0).to_bits(),
            ];
            if !seen.insert(key) {
                continue;
            }
            let kind = match value(t, "kind").as_str() {
                Some("Rail") => 4,
                Some("Highway") => 0,
                _ => value(t, "kind").as_u64().unwrap_or(0) as usize,
            };
            let Some(style) = rows(region, "tunnelStyles").find(|s| f(s, "kind") as usize == kind)
            else {
                continue;
            };
            let bore: Vec<_> = profile
                .iter()
                .map(|p| Vec2::new(p.x, p.y * f(style, "vertical")))
                .collect();
            for (x, z) in list(t, "sx").iter().zip(list(t, "sz")) {
                let mut centers = Vec::new();
                for i in 0..list(t, "y").len() {
                    let p = Vec3::new(
                        number(x.as_array().map(Vec::as_slice).unwrap_or(&[]), i),
                        number(list(t, "y"), i),
                        number(z.as_array().map(Vec::as_slice).unwrap_or(&[]), i),
                    );
                    if centers
                        .last()
                        .is_none_or(|old: &Vec3| old.distance_squared(p) > 1e-6)
                    {
                        centers.push(p);
                    }
                }
                out.entry("tunnel".into())
                    .or_insert_with(ObjData::default)
                    .append_transformed(
                        &sweep(&bore, &centers, f(style, "lateral")),
                        Transform::IDENTITY,
                    );
            }
            for end in 0..2 {
                let md = mouths
                    .get(index)
                    .and_then(|m| m.get(end))
                    .unwrap_or(&Value::Null);
                headwall(
                    out.entry("tunnel".into()).or_default(),
                    t,
                    md,
                    style,
                    &bore,
                    end,
                );
                mouth(&mut out, md);
            }
            floor(out.entry("floor".into()).or_default(), t, style);
        }
    }
    out
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn shipped_tunnel_profile_has_two_floorless_arches_and_exact_sweep_rings() {
        let mesh = ObjData::load(
            &std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
                .join("../game/content/objects/Tunnel_Line_0.obj"),
        )
        .unwrap();
        let profiles = profiles(&mesh);
        assert_eq!(profiles.len(), 2);
        let mut widths: Vec<_> = profiles
            .iter()
            .map(|c| c.iter().map(|p| p.x.abs()).fold(0., f32::max))
            .collect();
        widths.sort_by(f32::total_cmp);
        assert!((widths[0] - 8.).abs() < 0.001);
        assert!((widths[1] - 12.).abs() < 0.001);
        for chain in profiles {
            let centers = [Vec3::ZERO, Vec3::new(0., 1., 8.), Vec3::new(2., 2., 16.)];
            let swept = sweep(&chain, &centers, 1.05);
            let unique: HashSet<_> = swept
                .positions
                .iter()
                .map(|p| p.map(f32::to_bits))
                .collect();
            assert_eq!(unique.len(), chain.len() * centers.len());
            assert!(swept.colors.iter().all(|c| *c == [1.; 4]));
        }
    }
    #[test]
    fn headwall_strips_preserve_bore_opening_and_surface_area() {
        let mut mesh = ObjData::default();
        let upper = [(-3., 5.), (3., 5.)];
        let lower = [
            (-3., 0.),
            (-2., 0.),
            (-2., 2.),
            (0., 4.),
            (2., 2.),
            (2., 0.),
            (3., 0.),
        ];
        // Near-vertical source cut uses a 1cm offset instead of duplicate X.
        let lower: Vec<_> = lower
            .iter()
            .enumerate()
            .map(|(i, &(x, y))| {
                (
                    x + if i == 1 {
                        -0.01
                    } else if i == 5 {
                        0.01
                    } else {
                        0.
                    },
                    y,
                )
            })
            .collect();
        wall(&mut mesh, &upper, &lower, Vec3::ZERO, Vec3::X, Vec3::Z);
        let area: f32 = mesh
            .positions
            .chunks_exact(3)
            .map(|p| {
                let a = Vec3::from_array(p[0]);
                let b = Vec3::from_array(p[1]);
                let c = Vec3::from_array(p[2]);
                (b - a).cross(c - a).length() * 0.5
            })
            .sum();
        assert!((area - 17.98).abs() < 0.001, "area {area}");
        assert!(mesh.positions.chunks_exact(3).all(|p| {
            let c = p.iter().map(|p| Vec3::from_array(*p)).sum::<Vec3>() / 3.;
            c.y + 1e-4 >= interpolate(&lower, c.x)
        }));
    }
}
