//! StoreShelf tier layout, pure: `Profiles`/`SolidDefault`/`Prof`, `StoodAabb`,
//! `PoseOf`/`NearestAxis`, the stand vs lie-flat orientation, oversize scaling
//! and the front-biased slot position of `PlaceItem`, all in the shelf's frame
//! (the node at the container's position, yawed; the mesh stood up +270° X).
use bevy::prelude::*;
use std::collections::HashMap;

/// StoreShelf.Profile: tier surface heights (fractions of the stood AABB),
/// slots per tier, the used width fraction and the solid-container grid.
/// `FrontZ` and the loot counts are kept for parity; PlaceItem never reads
/// FrontZ and the counts belong to the server's roll.
#[derive(Debug, PartialEq)]
#[allow(dead_code)]
pub(crate) struct Profile {
    pub tier_y: &'static [f32],
    pub per_tier: u8,
    pub width_use: f32,
    pub front_z: f32,
    pub min: u8,
    pub max: u8,
    pub grid: (u8, u8),
}
const SHELF_1: Profile = Profile { tier_y: &[0.20, 0.50, 0.80], per_tier: 8, width_use: 0.90, front_z: 0.30, min: 12, max: 22, grid: (0, 0) };
const SHELF_0: Profile = Profile { tier_y: &[0.18, 0.48, 0.78], per_tier: 3, width_use: 0.78, front_z: 0.45, min: 5, max: 10, grid: (0, 0) };
const TOASTER_0: Profile = Profile { tier_y: &[0.5], per_tier: 2, width_use: 0.8, front_z: 0.3, min: 1, max: 2, grid: (2, 1) };
/// StoreShelf.SolidDefault: the fallback for a solid (non-display) container.
const SOLID_DEFAULT: Profile = Profile { tier_y: &[0.5], per_tier: 4, width_use: 0.8, front_z: 0.3, min: 2, max: 5, grid: (0, 0) };

/// StoreShelf.Prof: an unknown display shelf falls through to the gondola.
pub(crate) fn profile(mesh: &str, display: bool) -> &'static Profile {
    match mesh {
        "Shelf_1" => &SHELF_1,
        "Shelf_0" => &SHELF_0,
        "Toaster_0" => &TOASTER_0,
        _ if display => &SHELF_1,
        _ => &SOLID_DEFAULT,
    }
}
/// StoreShelf.GridDims: a display shelf's grid mirrors its tiers 1:1. The
/// server sends the dims; this pins the catalogue against the source table.
#[cfg_attr(not(test), allow(dead_code))]
pub(crate) fn grid_dims(mesh: &str, display: bool) -> (u8, u8) {
    let p = profile(mesh, display);
    if display {
        (p.per_tier, p.tier_y.len() as u8)
    } else {
        (if p.grid.0 != 0 { p.grid.0 } else { 8 }, if p.grid.1 != 0 { p.grid.1 } else { 6 })
    }
}

/// StoreShelf._upright: the shelf mesh is authored lying down.
pub(crate) fn upright() -> Quat {
    Quat::from_rotation_x(270f32.to_radians())
}
/// Godot `Transform3D(basis, 0) * Aabb`: the axis-aligned bounds of a box under a linear map.
pub(crate) fn transform_box(m: Mat3, min: Vec3, max: Vec3) -> (Vec3, Vec3) {
    let center = m * ((min + max) * 0.5);
    let half = (max - min) * 0.5;
    let abs = Mat3::from_cols(m.x_axis.abs(), m.y_axis.abs(), m.z_axis.abs());
    let extent = abs * half;
    (center - extent, center + extent)
}
/// StoreShelf.StoodAabb: the standing shelf's AABB in its root space.
pub(crate) fn stood_box(min: Vec3, max: Vec3) -> (Vec3, Vec3) {
    transform_box(Mat3::from_quat(upright()), min, max)
}

/// The icon pose ripped from each prefab's "Icon" transform (item_poses.json).
#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) struct Pose {
    pub front: Vec3,
    pub up: Vec3,
}
/// StoreShelf.PoseOf's loader: `{"id":{"f":[x,y,z],"u":[x,y,z]}}`; rows
/// without three components or with a non-integer key are skipped.
pub(crate) fn poses(source: &str) -> HashMap<u16, Pose> {
    let Ok(serde_json::Value::Object(rows)) = serde_json::from_str::<serde_json::Value>(source) else {
        return HashMap::new();
    };
    let v3 = |v: &serde_json::Value| {
        let a = v.as_array().filter(|a| a.len() == 3)?;
        Some(Vec3::new(a[0].as_f64()? as f32, a[1].as_f64()? as f32, a[2].as_f64()? as f32))
    };
    rows.iter()
        .filter_map(|(key, row)| Some((key.parse().ok()?, Pose { front: v3(&row["f"])?, up: v3(&row["u"])? })))
        .collect()
}
/// StoreShelf.NearestAxis: the dominant local axis (ties to the lower axis)
/// and its sign (`>= 0` is positive, so -0 is +1).
pub(crate) fn nearest_axis(v: Vec3) -> (usize, f32) {
    let (ax, ay, az) = (v.x.abs(), v.y.abs(), v.z.abs());
    let a = if ax >= ay && ax >= az { 0 } else if ay >= az { 1 } else { 2 };
    (a, if v[a] >= 0. { 1. } else { -1. })
}

/// StoreShelf._forceLie: produce, chocolate/candy/cheese and the carjack.
pub(crate) const FORCE_LIE: [u16; 8] = [329, 344, 335, 342, 83, 84, 464, 277];
/// What PlaceItem reads off the item asset (`null` when it has none).
#[derive(Clone, Copy, Default)]
pub(crate) struct Facts<'a> {
    /// EItemType, lower-case as the catalogue sends it.
    pub item_type: Option<&'a str>,
    pub name: Option<&'a str>,
}
const CLOTHING: [&str; 7] = ["hat", "pants", "shirt", "mask", "backpack", "vest", "glasses"];

/// The mesh AABB's axes, shortest first, exactly as .NET `Array.Sort` orders
/// three keys: ArraySortHelper's SwapIfGreater on (0,1), (0,2), (1,2). It is
/// NOT stable: equal dims can come out reversed (0.126 x 0.126 x 0.0525 sorts
/// to [2, 1, 0], not [2, 0, 1]), and the longest axis decides how a
/// diagonal-up item stands.
pub(crate) fn sorted_axes(d: [f32; 3]) -> [usize; 3] {
    let mut ax = [0, 1, 2];
    for (i, j) in [(0, 1), (0, 2), (1, 2)] {
        if d[ax[i]] > d[ax[j]] {
            ax.swap(i, j);
        }
    }
    ax
}
/// The stand vs lie-flat rule: drinks always stand; flat slabs, clothing,
/// pinned foods, magazines and sandwiches lie.
pub(crate) fn lies_flat(id: u16, size: Vec3, facts: Facts) -> bool {
    let d = size.to_array();
    let ax = sorted_axes(d);
    let round = d[ax[1]] >= d[ax[2]] * 0.85;
    let flat_slab = d[ax[0]] < d[ax[1]] * 0.45 && !round;
    let drink = facts.item_type == Some("water");
    let clothing = facts.item_type.is_some_and(|t| CLOTHING.contains(&t));
    !drink
        && (flat_slab
            || clothing
            || FORCE_LIE.contains(&id)
            || facts.item_type == Some("magazine")
            || facts.name.is_some_and(|n| n.contains("Sandwich")))
}
/// Columns c[0..3] as a basis; a negative determinant flips column `fix`.
fn proper(mut c: [Vec3; 3], fix: usize) -> Mat3 {
    if Mat3::from_cols(c[0], c[1], c[2]).determinant() < 0. {
        c[fix] = -c[fix];
    }
    Mat3::from_cols(c[0], c[1], c[2])
}
/// PlaceItem's ORIENT: stand as the icon poses it (up -> +Y, front -> +Z, the
/// aisle) snapped to axes, or lie flat with the icon front (else the
/// shortest axis) up and the longer remaining axis along the shelf width.
pub(crate) fn orient(id: u16, size: Vec3, pose: Option<Pose>, facts: Facts) -> Mat3 {
    let d = size.to_array();
    let ax = sorted_axes(d);
    let lie = lies_flat(id, size, facts);
    let mut c = [Vec3::ZERO; 3];
    if let Some(pose) = pose.filter(|_| !lie) {
        let (mut up, mut up_sign) = nearest_axis(pose.up);
        let abs = pose.up.abs();
        let top = abs.max_element();
        // A diagonal icon-up (the carrot at 45°) stands on its longest axis.
        if abs.x + abs.y + abs.z - top - abs.min_element() > top * 0.7 {
            up = ax[2];
            up_sign = if pose.up[ax[2]] >= 0. { 1. } else { -1. };
        }
        let (mut front, mut front_sign) = nearest_axis(pose.front);
        if front == up {
            front = (up + 1) % 3;
            front_sign = 1.;
        }
        let third = 3 - up - front;
        c[up] = Vec3::new(0., up_sign, 0.);
        c[front] = Vec3::new(0., 0., front_sign);
        c[third] = Vec3::X;
        proper(c, third)
    } else {
        let front = pose.map_or(Vec3::ZERO, |p| p.front);
        let (up, up_sign) = if front.length_squared() > 0.01 { nearest_axis(front) } else { (ax[0], 1.) };
        let (o1, o2) = ((up + 1) % 3, (up + 2) % 3);
        let (wide, deep) = if d[o1] >= d[o2] { (o1, o2) } else { (o2, o1) };
        c[up] = Vec3::new(0., up_sign, 0.);
        c[wide] = Vec3::X;
        c[deep] = Vec3::Z;
        proper(c, deep)
    }
}

/// One placed display model, in the shelf's root space.
#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) struct Placement {
    pub transform: Transform,
    /// The name tag's anchor: world-up above the model's origin by its height + 0.12.
    pub tag: Vec3,
}
/// PlaceItem's SCALE + POSITION for grid cell (gx, gy) spanning `eff_w`
/// columns. `None` when the cell is beyond the shelf's slots (not shown).
#[allow(clippy::too_many_arguments)]
pub(crate) fn place(
    profile: &Profile,
    shelf: (Vec3, Vec3),
    gx: u8,
    gy: u8,
    eff_w: u8,
    mesh: (Vec3, Vec3),
    oriented: Mat3,
) -> Option<Placement> {
    let tiers = profile.tier_y.len() as i32;
    let tier = (tiers - 1) - i32::from(gy); // the top grid row is the top tier
    if gx >= profile.per_tier || tier < 0 || tier >= tiers {
        return None;
    }
    let (box_min, box_size) = (shelf.0, shelf.1 - shelf.0);
    let per = f32::from(profile.per_tier);
    let eff_w = eff_w.clamp(1, profile.per_tier - gx); // wide items span, clamped to the end
    let center_col = f32::from(gx) + f32::from(eff_w - 1) * 0.5;
    let fx = if profile.per_tier > 1 { center_col / (per - 1.) } else { 0.5 };
    let x0 = box_min.x + box_size.x * (1. - profile.width_use) * 0.5;
    let xspan = box_size.x * profile.width_use;
    // SCALE oversized items down to the slot width and the tier gap.
    let (ob_min, ob_max) = transform_box(oriented, mesh.0, mesh.1);
    let ob = ob_max - ob_min;
    let slot_w = (xspan / per.max(1.)) * f32::from(eff_w) * 0.9;
    let tier_gap = box_size.y * 0.24;
    let foot = ob.x.max(ob.z);
    let mut sc = 1f32;
    if foot > slot_w && foot > 0.0001 {
        sc = sc.min(slot_w / foot);
    }
    if ob.y > tier_gap && ob.y > 0.0001 {
        sc = sc.min(tier_gap / ob.y);
    }
    let basis = oriented * sc;
    // POSITION: base on the tier (+ a lift), centred on the slot, front-biased
    // (front = high Z, the aisle) and kept off the back wall and the lip.
    let (rb_min, rb_max) = transform_box(basis, mesh.0, mesh.1);
    let rb = rb_max - rb_min;
    let tier_surface = box_min.y + box_size.y * profile.tier_y[tier as usize];
    let mut z = (box_min.z + box_size.z * 0.76) - (rb_min.z + rb.z * 0.5);
    let min_back = box_min.z + box_size.z * 0.34;
    if z + rb_min.z < min_back {
        z = min_back - rb_min.z;
    }
    let front_lip = box_min.z + box_size.z * 0.96;
    if z + rb_min.z + rb.z > front_lip {
        z = front_lip - (rb_min.z + rb.z);
    }
    let translation = Vec3::new(
        (x0 + xspan * fx) - (rb_min.x + rb.x * 0.5),
        tier_surface - rb_min.y + rb.y * 0.03 + 0.05,
        z,
    );
    Some(Placement {
        transform: Transform { translation, rotation: Quat::from_mat3(&oriented), scale: Vec3::splat(sc) },
        tag: translation + Vec3::Y * (rb.y + 0.12),
    })
}
