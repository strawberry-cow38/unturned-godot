//! Original finite editor map, distinct from InfiniteTerrain. The managed
//! adapter owns generation/persistence; these files only transport source data.
use super::{ContentError, ObjData, Placement, TerrainPatch, terrain::SplatField};
use bevy::prelude::*;
use serde::Deserialize;
use std::{
    fs,
    path::{Path, PathBuf},
    sync::Arc,
};

pub(crate) mod bootstrap;
mod surface;
pub(super) use surface::Surface;
#[cfg(test)]
mod tests;

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Descriptor {
    pub version: u32,
    pub name: String,
    pub tiles_x: usize,
    pub tiles_z: usize,
    pub seed: i32,
    pub water: bool,
    pub sea_level: f32,
    pub foliage: Option<String>,
    pub resources: Option<String>,
}
#[derive(Debug, Clone)]
pub struct FiniteWorld {
    pub root: PathBuf,
    pub descriptor: Descriptor,
    pub(super) surface: Surface,
    pub(super) splat: Arc<SplatField>,
}
impl FiniteWorld {
    pub fn load(root: &Path) -> Result<Self, ContentError> {
        let descriptor: Descriptor =
            serde_json::from_slice(&fs::read(root.join("finite.json"))?)
                .map_err(|e| ContentError(format!("finite descriptor: {e}")))?;
        if descriptor.version != 1
            || !(1..=8).contains(&descriptor.tiles_x)
            || !(1..=8).contains(&descriptor.tiles_z)
            || descriptor.sea_level != 25.6
            || descriptor.name.is_empty()
            || descriptor.name.len() > 160
            || descriptor
                .name
                .chars()
                .any(|c| c.is_control() || "/\\".contains(c))
            || descriptor
                .foliage
                .as_ref()
                .is_some_and(|p| p != &format!("foliage_island_{}", descriptor.seed))
            || descriptor
                .resources
                .as_ref()
                .is_some_and(|p| p != &format!("resources_island_{}", descriptor.seed))
        {
            return Err(ContentError("invalid finite descriptor".into()));
        }
        let surface = Surface::decode(&fs::read(root.join(".native/surface.bin"))?)?;
        if surface.width != descriptor.tiles_x * 256 + 1
            || surface.height != descriptor.tiles_z * 256 + 1
        {
            return Err(ContentError(
                "finite surface dimensions disagree with descriptor".into(),
            ));
        }
        let splat = Arc::new(surface.splat());
        Ok(Self {
            root: root.to_owned(),
            descriptor,
            surface,
            splat,
        })
    }
    pub fn props_path(&self) -> PathBuf {
        self.root.join(".native/props.json")
    }
    pub fn placements(&self) -> Result<Vec<Placement>, ContentError> {
        #[derive(Deserialize)]
        #[serde(rename_all = "camelCase")]
        struct Record {
            source_index: usize,
            name: String,
            guid: String,
            material: String,
            position: [f32; 3],
            basis: [[f32; 3]; 3],
        }
        let records: Vec<Record> = serde_json::from_slice(&fs::read(self.props_path())?)
            .map_err(|e| ContentError(format!("finite placements: {e}")))?;
        records
            .into_iter()
            .map(|p| {
                let position = Vec3::from_array(p.position);
                let basis = p.basis.map(Vec3::from_array);
                if !position.is_finite()
                    || basis.iter().any(|v| !v.is_finite())
                    || [&p.name, &p.material]
                        .iter()
                        .any(|s| s.is_empty() || s.contains(['/', '\\']))
                {
                    return Err(ContentError("invalid finite placement".into()));
                }
                let matrix = Mat4::from_cols(
                    basis[0].extend(0.),
                    basis[1].extend(0.),
                    basis[2].extend(0.),
                    position.extend(1.),
                );
                Ok(Placement {
                    source_index: p.source_index,
                    name: p.name,
                    guid: p.guid,
                    material: p.material,
                    transform: Transform::from_matrix(matrix),
                    euler: None,
                })
            })
            .collect()
    }
    pub fn roads_path(&self) -> PathBuf {
        self.root
            .join(format!("roads/editor_{}_Paths.dat", self.descriptor.name))
    }
    pub fn players(&self) -> Result<Vec<(Vec3, f32)>, ContentError> {
        #[derive(Deserialize)]
        struct Spawn {
            position: [f32; 3],
            yaw: f32,
            alt: bool,
        }
        let points: Vec<Spawn> =
            serde_json::from_slice(&fs::read(self.root.join(".native/players.json"))?)
                .map_err(|e| ContentError(format!("finite player spawns: {e}")))?;
        // The original EditorSpawns translator already converted source text.
        Ok(points
            .into_iter()
            .filter(|p| !p.alt)
            .filter_map(|p| {
                let position = Vec3::from_array(p.position);
                (position.is_finite() && p.yaw.is_finite()).then_some((position, p.yaw))
            })
            .collect())
    }
    pub(super) fn patches(&self) -> Vec<TerrainPatch> {
        self.surface.patches(self.splat.clone())
    }
    pub(super) fn chunks_near(&self, center: Vec3, radius: f32) -> Vec<(usize, usize)> {
        let mut out = Vec::new();
        for x in 0..(self.surface.width - 2) / 48 + 1 {
            for z in 0..(self.surface.height - 2) / 48 + 1 {
                let min = Vec2::new(
                    (x * 48 * 4) as f32,
                    -(((z * 48 + 48).min(self.surface.height - 1) * 4) as f32),
                );
                let max = Vec2::new(
                    ((x * 48 + 48).min(self.surface.width - 1) * 4) as f32,
                    -((z * 48 * 4) as f32),
                );
                if center.xz().distance_squared(center.xz().clamp(min, max)) <= radius * radius {
                    out.push((x, z));
                }
            }
        }
        out
    }
    pub(crate) fn chunk(&self, x: usize, z: usize) -> Result<ObjData, ContentError> {
        decode_chunk(&fs::read(
            self.root.join(format!(".native/chunk_{x}_{z}.bin")),
        )?)
    }
}

/// Local fixed-width transport reader. Validate lengths before allocation and
/// reject non-finite geometry rather than feeding invalid bounds into physics.
struct Reader<'a>(&'a [u8]);
impl Reader<'_> {
    fn take<const N: usize>(&mut self) -> Result<[u8; N], ContentError> {
        let bytes = self
            .0
            .get(..N)
            .ok_or_else(|| ContentError("truncated finite data".into()))?;
        let bytes = bytes.try_into().unwrap();
        self.0 = &self.0[N..];
        Ok(bytes)
    }
    fn u32(&mut self) -> Result<u32, ContentError> {
        Ok(u32::from_le_bytes(self.take()?))
    }
    fn f32(&mut self) -> Result<f32, ContentError> {
        let value = f32::from_le_bytes(self.take()?);
        value
            .is_finite()
            .then_some(value)
            .ok_or_else(|| ContentError("non-finite map coordinate".into()))
    }
    fn floats<const N: usize>(&mut self) -> Result<[f32; N], ContentError> {
        let mut values = [0.; N];
        for value in &mut values {
            *value = self.f32()?;
        }
        Ok(values)
    }
    fn end(&self) -> Result<(), ContentError> {
        self.0
            .is_empty()
            .then_some(())
            .ok_or_else(|| ContentError("trailing finite data".into()))
    }
}
pub(super) fn decode_chunk(bytes: &[u8]) -> Result<ObjData, ContentError> {
    let mut r = Reader(bytes);
    if r.u32()? != 0x43464755 || r.u32()? != 1 {
        return Err(ContentError("invalid finite chunk header".into()));
    }
    let vertices = r.u32()? as usize;
    let indices = r.u32()? as usize;
    if vertices > 200_000
        || indices > 500_000
        || indices % 3 != 0
        || r.0.len() != vertices * 48 + indices * 4
    {
        return Err(ContentError("invalid finite chunk length".into()));
    }
    let p = (0..vertices)
        .map(|_| r.floats::<3>())
        .collect::<Result<Vec<_>, _>>()?;
    let n = (0..vertices)
        .map(|_| r.floats::<3>())
        .collect::<Result<Vec<_>, _>>()?;
    let uv = (0..vertices)
        .map(|_| r.floats::<2>())
        .collect::<Result<Vec<_>, _>>()?;
    let c = (0..vertices)
        .map(|_| r.floats::<4>())
        .collect::<Result<Vec<_>, _>>()?;
    let mut out = ObjData {
        min: Vec3::splat(f32::INFINITY),
        max: Vec3::splat(f32::NEG_INFINITY),
        ..default()
    };
    for _ in 0..indices / 3 {
        let tri = [r.u32()? as usize, r.u32()? as usize, r.u32()? as usize];
        // Godot's clockwise triangles become Bevy's counter-clockwise once.
        for i in [tri[0], tri[2], tri[1]] {
            if i >= vertices {
                return Err(ContentError("finite chunk index out of bounds".into()));
            }
            out.positions.push(p[i]);
            out.normals.push(n[i]);
            out.uvs.push(uv[i]);
            out.colors.push(c[i]);
            out.min = out.min.min(Vec3::from_array(p[i]));
            out.max = out.max.max(Vec3::from_array(p[i]));
        }
    }
    r.end()?;
    Ok(out)
}
