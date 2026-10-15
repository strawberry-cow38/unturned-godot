//! Original Vehicle.BuildBiFoldDoor mesh edits, independent of accepted phase.
use crate::content::{
    ObjData,
    cut::{Set, Vertex},
};
use crate::vehicle::data::VehicleSpec;
use bevy::prelude::*;

pub(super) struct Parts {
    pub body: ObjData,
    pub leaf_a: ObjData,
    pub leaf_b: Option<ObjData>,
}
pub(super) fn applies(spec: &VehicleSpec) -> bool {
    // Source Vehicle.Build is one exclusive chain; never peel a bus door over
    // an aircraft, landing-leg or baked-headlight split selected before it.
    !spec.heli
        && !spec.plane
        && spec.landing_leg_zone_min == spec.landing_leg_zone_max
        && spec.headlight_zone_min == spec.headlight_zone_max
        && spec.door_zone_min != spec.door_zone_max
}
pub(super) fn cut(source: &ObjData, spec: &VehicleSpec) -> Option<Parts> {
    if !applies(spec) {
        return None;
    }
    let (body, panel) =
        source.split_zones(&[(spec.door_zone_min.into(), spec.door_zone_max.into())]);
    if panel.positions.is_empty() {
        return None;
    }
    let mut door = Set::read(&panel);
    let trim = spec.door_trim_z0 != spec.door_trim_z1;
    let (mut front, mut rear) = (Vec::new(), Vec::new());
    if trim {
        (_, door, front) = door.split(2, spec.door_trim_z0);
        (door, _, rear) = door.split(2, spec.door_trim_z1);
    }
    let (mut a, mut b, middle) = door.split(2, spec.door_split_z);
    let two = !a.triangles.is_empty() && !b.triangles.is_empty();
    let mut inner = middle.first().or(front.first()).copied().unwrap_or(Vertex {
        position: Vec3::ZERO,
        normal: Vec3::ZERO,
        uv: Vec2::ZERO,
        color: Vec4::ZERO,
    });
    for &v in middle.iter().chain(&front) {
        if v.position.x < inner.position.x {
            inner = v;
        }
    }
    if two && middle.len() >= 3 {
        a.cap_hull(&middle, 2, Vec3::Z, inner.uv, inner.color);
        b.cap_hull(&middle, 2, -Vec3::Z, inner.uv, inner.color);
    }
    if trim {
        if two {
            a.cap_hull(&front, 2, -Vec3::Z, inner.uv, inner.color);
            b.cap_hull(&rear, 2, Vec3::Z, inner.uv, inner.color);
            bottom(&mut a, spec.door_trim_y, inner);
            bottom(&mut b, spec.door_trim_y, inner);
        } else {
            door.cap_hull(&front, 2, -Vec3::Z, inner.uv, inner.color);
            door.cap_hull(&rear, 2, Vec3::Z, inner.uv, inner.color);
            bottom(&mut door, spec.door_trim_y, inner);
        }
    }
    Some(Parts {
        body: pocket(body, spec),
        leaf_a: if two {
            a.commit()
        } else if trim {
            door.commit()
        } else {
            panel
        },
        leaf_b: two.then(|| b.commit()),
    })
}
fn bottom(leaf: &mut Set, y: f32, inner: Vertex) {
    let (_, kept, cut) = leaf.split(1, y);
    *leaf = kept;
    leaf.cap_hull(&cut, 1, -Vec3::Y, inner.uv, inner.color);
}
fn pocket(body: ObjData, spec: &VehicleSpec) -> ObjData {
    if spec.door_floor_cut_min == spec.door_floor_cut_max {
        return body;
    }
    let (mut kept, dropped) = Set::read(&body).subtract_box(
        spec.door_floor_cut_min.into(),
        spec.door_floor_cut_max.into(),
    );
    let riser = spec.door_riser_cut_min != spec.door_riser_cut_max;
    if riser {
        (kept, _) = kept.subtract_box(
            spec.door_riser_cut_min.into(),
            spec.door_riser_cut_max.into(),
        );
    }
    // Source commits neither subtraction unless the floor actually supplied a
    // face; its first dropped face supplies palette UV/color and floor height.
    let Some(f) = dropped.first() else {
        return body;
    };
    let [x0, _, z0] = spec.door_floor_cut_min;
    let [_, _, z1] = spec.door_floor_cut_max;
    let x1 = if riser {
        spec.door_riser_cut_max[0]
    } else {
        spec.door_floor_cut_max[0]
    };
    let yf = f.position.y;
    let yp = spec.door_pocket_y;
    kept.quad(
        [
            Vec3::new(x0, yp, z0),
            Vec3::new(x1, yp, z0),
            Vec3::new(x1, yp, z1),
            Vec3::new(x0, yp, z1),
        ],
        Vec3::Y,
        f.uv,
        f.color,
    );
    kept.quad(
        [
            Vec3::new(x0, yp, z0),
            Vec3::new(x0, yp, z1),
            Vec3::new(x0, yf, z1),
            Vec3::new(x0, yf, z0),
        ],
        Vec3::X,
        f.uv,
        f.color,
    );
    kept.quad(
        [
            Vec3::new(x0, yp, z1),
            Vec3::new(x1, yp, z1),
            Vec3::new(x1, yf, z1),
            Vec3::new(x0, yf, z1),
        ],
        -Vec3::Z,
        f.uv,
        f.color,
    );
    kept.quad(
        [
            Vec3::new(x0, yp, z0),
            Vec3::new(x1, yp, z0),
            Vec3::new(x1, yf, z0),
            Vec3::new(x0, yf, z0),
        ],
        Vec3::Z,
        f.uv,
        f.color,
    );
    kept.commit()
}
