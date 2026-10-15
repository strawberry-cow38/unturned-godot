//! Original LensHull, ClassifyLamp, StreetLight.KelvinToColor and BeaconLensMesh.
use crate::content::ObjData;
use bevy::prelude::*;

pub(super) fn halves(source: &ObjData) -> (ObjData, ObjData) {
    let (right, left) = source.split_where(|p| (p[0].x + p[1].x + p[2].x) / 3. < 0.);
    (left, right)
}
pub(super) fn mirrored_zones(min: [f32; 3], max: [f32; 3]) -> [(Vec3, Vec3); 2] {
    let (min, max) = (Vec3::from_array(min), Vec3::from_array(max));
    [
        (min, max),
        (
            Vec3::new(-max.x, min.y, min.z),
            Vec3::new(-min.x, max.y, max.z),
        ),
    ]
}
fn equal(a: f32, b: f32) -> bool {
    a == b || (a - b).abs() < (0.00001 * a.abs()).max(0.00001)
}
pub(super) fn hull(mut points: Vec<Vec2>) -> Vec<Vec2> {
    points.sort_by(|a, b| a.x.total_cmp(&b.x).then(a.y.total_cmp(&b.y)));
    for i in (1..points.len()).rev() {
        if equal(points[i].x, points[i - 1].x) && equal(points[i].y, points[i - 1].y) {
            points.remove(i);
        }
    }
    if points.len() < 3 {
        return points;
    }
    let cross = |o: Vec2, a: Vec2, b: Vec2| (a - o).perp_dot(b - o);
    let mut lo: Vec<Vec2> = Vec::new();
    let mut hi: Vec<Vec2> = Vec::new();
    for q in &points {
        while lo.len() >= 2 && cross(lo[lo.len() - 2], lo[lo.len() - 1], *q) <= 0. {
            lo.pop();
        }
        lo.push(*q);
    }
    for q in points.iter().rev() {
        while hi.len() >= 2 && cross(hi[hi.len() - 2], hi[hi.len() - 1], *q) <= 0. {
            hi.pop();
        }
        hi.push(*q);
    }
    lo.pop();
    hi.pop();
    lo.extend(hi);
    for i in (0..lo.len()).rev() {
        if lo.len() <= 3 {
            break;
        }
        let e1 = lo[i] - lo[(i + lo.len() - 1) % lo.len()];
        let e2 = lo[(i + 1) % lo.len()] - lo[i];
        let (a, b) = (e1.length(), e2.length());
        if a < 0.00001 || b < 0.00001 || e1.perp_dot(e2).abs() / (a * b) < 0.08 {
            lo.remove(i);
        }
    }
    lo
}
pub(super) fn classify(source: &ObjData) -> Option<(bool, Color)> {
    if source.positions.len() < 3 {
        return None;
    }
    let mid = (source.min.x + source.max.x) * 0.5;
    let points: Vec<_> = source
        .positions
        .iter()
        .filter(|p| p[0] < mid)
        .map(|p| Vec2::new(p[0], p[1]))
        .collect();
    if points.len() < 3 {
        return None;
    }
    let round = hull(points).len() >= 5;
    Some((round, kelvin(if round { 3000. } else { 4300. })))
}
pub(super) fn kelvin(k: f32) -> Color {
    let t = k.clamp(1000., 12000.) / 100.;
    let (r, g) = if t <= 66. {
        (255., 99.4708 * t.ln() - 161.11957)
    } else {
        (
            329.69873 * (t - 60.).powf(-0.13320476),
            288.12216 * (t - 60.).powf(-0.07551485),
        )
    };
    let b = if t >= 66. {
        255.
    } else if t <= 19. {
        0.
    } else {
        138.51773 * (t - 10.).ln() - 305.0448
    };
    Color::srgb(
        r.clamp(0., 255.) / 255.,
        g.clamp(0., 255.) / 255.,
        b.clamp(0., 255.) / 255.,
    )
}
pub(super) fn beacon(source: &ObjData) -> Option<ObjData> {
    let centroids: Vec<_> = source
        .positions
        .chunks_exact(3)
        .map(|p| (p[0][0] + p[1][0] + p[2][0]) / 3.)
        .collect();
    if centroids.is_empty() {
        return None;
    }
    let mut sorted = centroids.clone();
    sorted.sort_by(f32::total_cmp);
    let gap = 0.08_f32.max((sorted[sorted.len() - 1] - sorted[0]) * 0.25);
    let (mut low, mut previous, mut count, mut best) = (sorted[0], sorted[0], 1, 0);
    let (mut best_low, mut best_high) = (sorted[0], sorted[sorted.len() - 1]);
    for i in 1..=sorted.len() {
        if i < sorted.len() && sorted[i] - previous <= gap {
            count += 1;
            previous = sorted[i];
            continue;
        }
        if count > best {
            best = count;
            best_low = low;
            best_high = previous;
        }
        if i == sorted.len() {
            break;
        }
        low = sorted[i];
        previous = sorted[i];
        count = 1;
    }
    let (_, selected) = source.split_where(|p| {
        let x = (p[0].x + p[1].x + p[2].x) / 3.;
        x >= best_low - 0.001 && x <= best_high + 0.001
    });
    if selected.positions.len() < 3 {
        return None;
    }
    let angle = if (best_low + best_high) * 0.5 >= 0. {
        -90_f32
    } else {
        90.
    };
    let turn = Quat::from_rotation_z(angle.to_radians());
    let center = (selected.min + selected.max) * 0.5;
    let mut out = ObjData::default();
    out.append_transformed(
        &selected,
        Transform::from_rotation(turn).with_translation(-(turn * center)),
    );
    Some(out)
}
