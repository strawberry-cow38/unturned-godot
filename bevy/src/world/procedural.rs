//! Original InfiniteTerrain region data and renderer metadata. Coordinates are
//! already in the runtime's right-handed space; only retail files need Z reflection.
use super::{ContentError, TerrainPatch};
use crate::bridge::CoreBridge;
use bevy::prelude::*;
use serde::Deserialize;
use serde_json::Value;
use std::{collections::HashMap, path::Path, sync::Arc};
#[derive(Clone, Deserialize)]
pub struct ProceduralRegion {
    #[serde(alias = "Cells")]
    pub cells: usize,
    #[serde(alias = "Spacing")]
    pub spacing: f32,
    #[serde(alias = "Heights")]
    pub heights: Vec<f32>,
    #[serde(default)]
    pub normals: Vec<f32>,
    #[serde(default, alias = "Layers")]
    pub layers: Vec<u8>,
    #[serde(default, alias = "Holes")]
    pub holes: Option<Vec<bool>>,
    #[serde(default)]
    pub origin_x: f32,
    #[serde(default)]
    pub origin_z: f32,
    #[serde(default, alias = "Trees")]
    pub trees: Vec<ProceduralTree>,
    #[serde(default)]
    pub foliage: Option<Vec<Foliage>>,
    #[serde(default)]
    pub boulders: Option<Vec<Boulder>>,
    #[serde(default, rename = "tunnelRocks")]
    pub tunnel_rocks: Option<Vec<Boulder>>,
    #[serde(flatten)]
    pub structures: HashMap<String, Value>,
}
#[derive(Clone, Deserialize)]
pub struct ProceduralTree {
    #[serde(alias = "X")]
    pub x: f32,
    #[serde(alias = "Y")]
    pub y: f32,
    #[serde(alias = "Z")]
    pub z: f32,
    #[serde(alias = "Yaw")]
    pub yaw: f32,
    #[serde(alias = "Scale")]
    pub scale: f32,
    #[serde(alias = "Kind")]
    pub kind: u8,
    #[serde(alias = "Variant")]
    pub variant: u8,
}
pub fn procedural_tree_name(kind: u8, variant: u8) -> Option<String> {
    const CACTUS: [&str; 14] = [
        "Cactus_Column_0",
        "Cactus_Column_1",
        "Cactus_Column_2",
        "Cactus_Branched_0",
        "Cactus_Branched_1",
        "Cactus_Branched_2",
        "Cactus_Barrel_0",
        "Cactus_Barrel_2",
        "Cactus_Cluster_0",
        "Cactus_Cluster_1",
        "Cactus_Cluster_2",
        "Cactus_Paddles_0",
        "Cactus_Paddles_1",
        "Cactus_Paddles_2",
    ];
    match kind {
        0..=2 if variant < 2 => Some(format!(
            "{}_{}",
            ["Pine", "Birch", "Maple"][kind as usize],
            variant
        )),
        3 if variant < 3 => Some(format!("Redwood_{variant}")),
        4 => CACTUS.get(variant as usize).map(|n| n.to_string()),
        _ => None,
    }
}
pub fn procedural_tree_transform(tree: &ProceduralTree, origin: Vec2) -> Transform {
    // RegionStreamer.BuildMesh: generated trees are already in runtime coordinates;
    // retail placement's Euler/Z conversion would turn these upright assets sideways.
    let sink = if tree.kind == 4 { 0.05 } else { 0.5 };
    Transform {
        translation: Vec3::new(
            origin.x + tree.x,
            tree.y - sink * tree.scale,
            origin.y + tree.z,
        ),
        rotation: Quat::from_rotation_y(tree.yaw.to_radians()),
        scale: Vec3::splat(tree.scale),
    }
}

#[derive(Clone, Deserialize)]
pub struct Foliage {
    pub x: f32,
    pub y: f32,
    pub z: f32,
    pub yaw: f32,
    pub scale: f32,
    pub kind: u8,
}
#[derive(Clone, Deserialize)]
pub struct Boulder {
    pub x: f32,
    pub y: f32,
    pub z: f32,
    pub yaw: f32,
    pub radius: f32,
    pub bury: f32,
    pub nx: f32,
    pub ny: f32,
    pub nz: f32,
    pub kind: u8,
}
impl ProceduralRegion {
    pub fn patch(&self) -> Result<TerrainPatch, ContentError> {
        let count = (self.cells + 1)
            .checked_mul(self.cells + 1)
            .ok_or_else(|| ContentError("terrain size overflow".into()))?;
        if self.cells == 0
            || self.cells > 1024
            || self.heights.len() != count
            || self.spacing <= 0.
            || !self.spacing.is_finite()
            || self.heights.iter().any(|h| !h.is_finite())
        {
            return Err(ContentError("invalid procedural height grid".into()));
        }
        Ok(TerrainPatch {
            origin: Vec2::new(self.origin_x, self.origin_z),
            cells: self.cells,
            spacing: self.spacing,
            heights: self.heights.clone(),
            normals: self
                .normals
                .chunks_exact(3)
                .map(|p| [p[0], p[1], p[2]])
                .collect(),
            layers: self.layers.clone(),
            holes: self.holes.clone().unwrap_or_default(),
            splat: None,
        })
    }
}
pub fn region_coord(p: Vec3) -> (i32, i32) {
    ((p.x / 256.).floor() as i32, (p.z / 256.).floor() as i32)
}
pub fn regions(
    bridge: &CoreBridge,
    seed: i32,
    center: Vec3,
    radius: f32,
    initial: &Path,
    cache: &mut HashMap<(i32, i32, i32), Arc<ProceduralRegion>>,
) -> Result<Vec<Arc<ProceduralRegion>>, ContentError> {
    let (cx, cz) = region_coord(center);
    let ring = (radius / 256.).ceil() as i32;
    let mut cells = Vec::new();
    for z in cz - ring..=cz + ring {
        for x in cx - ring..=cx + ring {
            cells.push((x, z));
        }
    }
    cells.sort_by_key(|(x, z)| ((x - cx).abs().max((z - cz).abs()), *z, *x));
    let mut result = Vec::new();
    for (x, z) in cells {
        let key = (seed, x, z);
        if !cache.contains_key(&key) {
            let parsed = if x == 0 && z == 0 {
                std::fs::read(initial)
                    .ok()
                    .and_then(|b| serde_json::from_slice::<ProceduralRegion>(&b).ok())
            } else {
                None
            };
            let region = if let Some(r) = parsed {
                r
            } else {
                let response = bridge
                    .request(
                        "terrain",
                        serde_json::json!({"seed":seed,"x":x,"z":z,"lod":0}),
                    )
                    .map_err(|e| ContentError(e.to_string()))?;
                serde_json::from_value(response["result"].clone())
                    .map_err(|e| ContentError(e.to_string()))?
            };
            region.patch()?;
            cache.insert(key, Arc::new(region));
        }
        result.push(cache[&key].clone());
    }
    // Preserve a small warm band, not an unbounded history of every visited region.
    cache.retain(|&(s, x, z), _| s == seed && (x - cx).abs().max((z - cz).abs()) <= ring + 1);
    Ok(result)
}

pub struct AssetInstance {
    pub mesh: String,
    pub transform: Transform,
    pub solid: bool,
    pub foliage: bool,
    pub tint: Color,
}
fn object(name: &str, transform: Transform, solid: bool) -> AssetInstance {
    AssetInstance {
        mesh: format!("objects/{name}"),
        transform,
        solid,
        foliage: false,
        tint: Color::WHITE,
    }
}
pub(super) fn value<'a>(v: &'a Value, key: &str) -> &'a Value {
    v.get(key)
        .or_else(|| {
            v.as_object().and_then(|o| {
                o.iter()
                    .find(|(k, _)| k.eq_ignore_ascii_case(key))
                    .map(|(_, v)| v)
            })
        })
        .unwrap_or(&Value::Null)
}
pub(super) fn f(v: &Value, key: &str) -> f32 {
    value(v, key).as_f64().unwrap_or(0.) as f32
}
pub fn vec(v: &Value, x: &str, y: &str, z: &str) -> Vec3 {
    Vec3::new(f(v, x), f(v, y), f(v, z))
}
pub(super) fn rows<'a>(r: &'a ProceduralRegion, key: &str) -> impl Iterator<Item = &'a Value> {
    r.structures
        .get(key)
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
}
pub(super) fn pole_rotation(dx: f32, dz: f32) -> Quat {
    Quat::from_rotation_y((-dx).atan2(-dz)) * Quat::from_rotation_x(270_f32.to_radians())
}
fn bridge_rotation(dir: Vec3, pier: bool) -> Quat {
    let stand = pole_rotation(dir.x, dir.z);
    let flat = Vec3::new(dir.x, 0., dir.z).normalize_or_zero();
    if pier || flat == Vec3::ZERO {
        return stand;
    }
    let grade = dir.normalize_or_zero().y.clamp(-1., 1.).asin();
    Quat::from_axis_angle(Vec3::Y.cross(flat).normalize(), -grade) * stand
}
/// InfiniteRoads.DeckScale: the same width dresses the kit and its road surface.
pub fn deck_scale(road: usize) -> f32 {
    match road {
        0 => 1.,
        1 => 9.2 / 8.,
        4 => 6.5 / 8.,
        _ => 4.6 / 8.,
    }
}
/// Model selection and placement follow RegionStreamer.Build and its kit bases.
/// Wire spans and tunnel shell geometry are separate mesh consumers.
pub fn instances(region: &ProceduralRegion) -> Vec<AssetInstance> {
    let origin = Vec3::new(region.origin_x, 0., region.origin_z);
    let mut out = Vec::new();
    const FOLIAGE: [&str; 9] = [
        "foliage/grass_00",
        "foliage/flowers_00",
        "foliage/flowers_01",
        "foliage/flowers_02",
        "foliage/flowers_03",
        "foliage/pebble_00",
        "foliage/pebble_sand_00",
        "resources/Bush_0_0",
        "resources/Bush_1_0",
    ];
    for a in region.foliage.iter().flatten() {
        let Some(name) = FOLIAGE.get(a.kind as usize) else {
            continue;
        };
        let scale = a.scale * if matches!(a.kind, 5 | 6) { 0.75 } else { 1. };
        out.push(AssetInstance {
            mesh: (*name).into(),
            transform: Transform {
                translation: origin + Vec3::new(a.x, a.y, a.z),
                rotation: Quat::from_rotation_y(a.yaw.to_radians()),
                scale: Vec3::splat(scale),
            },
            solid: false,
            foliage: true,
            tint: if a.kind == 6 {
                Color::srgb(0.506, 0.506, 0.506)
            } else if a.kind >= 1 && a.kind <= 5 {
                Color::srgb(0.456, 0.456, 0.456)
            } else {
                Color::WHITE
            },
        });
    }
    // Prefer metadata serialized from InfiniteTerrain.BoulderKit; no inferred mesh bounds.
    let kit = region
        .structures
        .get("boulderModels")
        .and_then(Value::as_array);
    for b in region
        .boulders
        .iter()
        .flatten()
        .chain(region.tunnel_rocks.iter().flatten())
    {
        let Some(meta) = kit.and_then(|k| k.get(b.kind as usize)) else {
            continue;
        };
        let Some(name) = meta["name"].as_str() else {
            continue;
        };
        let scale = b.radius / f(meta, "radius");
        if !scale.is_finite() || scale <= 0. {
            continue;
        }
        let normal = Vec3::new(b.nx, b.ny, b.nz).normalize_or_zero();
        let stand =
            super::placement_transform(Vec3::ZERO, Vec3::new(270., b.yaw, 0.), Vec3::ONE).rotation;
        let axis = Vec3::Y.cross(normal);
        let rotation = if axis.length_squared() < 1e-8 {
            stand
        } else {
            Quat::from_axis_angle(axis.normalize(), Vec3::Y.angle_between(normal)) * stand
        };
        let position = origin
            + Vec3::new(
                b.x,
                b.y - f(meta, "bottom") * scale - f(meta, "height") * scale * b.bury,
                b.z,
            );
        out.push(object(
            name,
            Transform {
                translation: position,
                rotation,
                scale: Vec3::splat(scale),
            },
            true,
        ));
    }
    for p in rows(region, "poles") {
        out.push(object(
            "Power_Line_0",
            Transform {
                translation: vec(p, "x", "h", "z") - Vec3::Y * 0.3,
                rotation: pole_rotation(f(p, "dirX"), f(p, "dirZ")),
                ..default()
            },
            true,
        ));
    }
    for p in rows(region, "pylons") {
        out.push(object(
            "Power_Line_1",
            Transform {
                translation: vec(p, "x", "h", "z"),
                rotation: pole_rotation(f(p, "dirX"), f(p, "dirZ")),
                scale: Vec3::splat(1.6),
            },
            true,
        ));
    }
    for p in rows(region, "rails") {
        let z = vec(p, "dx", "dy", "dz").normalize_or_zero();
        let x = Vec3::Y.cross(z).normalize_or_zero();
        let y = z.cross(x);
        let matrix = Mat4::from_cols(
            x.extend(0.),
            y.extend(0.),
            (z * f(p, "k")).extend(0.),
            vec(p, "x", "y", "z").extend(1.),
        );
        let name = if p["kind"] == 0 {
            "New_Rail_Unit"
        } else {
            "New_Rail_Sleeper"
        };
        out.push(object(name, Transform::from_matrix(matrix), true));
    }
    for (key, name) in [("crossingSigns", "Crossing_0"), ("stopSigns", "Stop_0")] {
        for p in rows(region, key) {
            out.push(object(
                name,
                Transform {
                    translation: vec(p, "x", "y", "z") - Vec3::Y * 0.1,
                    rotation: pole_rotation(f(p, "dx"), f(p, "dz")),
                    ..default()
                },
                true,
            ));
        }
    }
    for p in rows(region, "bridges") {
        let kind = p["kind"].as_u64().unwrap_or(0);
        let road = p["road"].as_u64().unwrap_or(0);
        let width = deck_scale(road as usize);
        let name = match kind {
            0 => "Bridge_Line_1_Deck_Unit",
            1 => "Bridge_Line_1_Pier",
            _ => "Bridge_Line_1_Deck_Cap",
        };
        out.push(object(
            name,
            Transform {
                translation: vec(p, "x", "y", "z"),
                rotation: bridge_rotation(vec(p, "dx", "dy", "dz"), kind == 1),
                scale: Vec3::new(width, 1., if kind == 1 { f(p, "k") } else { 1. }),
            },
            true,
        ));
    }
    out
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn region_lookup_floors_negative_coordinates() {
        assert_eq!(region_coord(Vec3::new(-1., 50., 256.)), (-1, 1));
        assert_eq!(region_coord(Vec3::new(-256., 0., -0.5)), (-1, -1));
    }
    #[test]
    fn bridge_basis_keeps_deck_vertical_axis_up_and_length_on_grade() {
        let direction = Vec3::new(0.4, 0.1, 1.).normalize();
        let rotation = bridge_rotation(direction, false);
        assert!((rotation * Vec3::Y - direction).length() < 1e-5);
        assert!((rotation * Vec3::X).y.abs() < 1e-5);
        assert!((bridge_rotation(direction, true) * Vec3::Z - Vec3::Y).length() < 1e-5);
    }
}
