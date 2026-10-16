//! Original WorldBuilder.SpawnPeiVehicles River format and weighted selection.
use super::data;
use anyhow::{Result, bail};
use bevy::prelude::*;
use std::io::{Cursor, Read};

/// Source SpawnVehicles already chose the variant, ground lift and road yaw.
/// Wait for resident collision without rerolling or applying the retail +1.2m
/// placement rule. The host saves even unvisited rows; stable keys dedupe load.
pub(super) fn generated_requests(
    snapshot: &serde_json::Value,
    geometry: &crate::world::WorldGeometry,
    registered: &std::collections::HashSet<usize>,
) -> Vec<(usize, serde_json::Value)> {
    snapshot["finiteVehicles"]
        .as_array()
        .into_iter()
        .flatten()
        .enumerate()
        .filter_map(|(i, row)| {
            let index = usize::MAX - 1 - i; // MAX belongs to the explicit vehicle harness.
            if registered.contains(&index) {
                return None;
            }
            let position = crate::player::vector(&row["position"])?;
            if position.xz().distance(geometry.loaded_center.xz()) > geometry.loaded_radius
                || geometry.terrain_height(position.x, position.z).is_none()
            {
                return None;
            }
            let definition = data::named(row["kind"].as_str()?)?;
            let variant = u8::try_from(row["variant"].as_u64()?).ok()?;
            let yaw = row["yaw"].as_f64()? as f32;
            Some((
                index,
                super::registration(
                    definition,
                    row["key"].as_str()?.to_owned(),
                    position,
                    yaw,
                    variant,
                ),
            ))
        })
        .collect()
}

#[derive(Clone, Debug)]
pub struct SpawnPoint {
    pub index: usize,
    pub type_id: u8,
    pub position: Vec3,
    pub yaw: f32,
    pub water: bool,
}
struct River<'a>(Cursor<&'a [u8]>);
impl River<'_> {
    fn bytes<const N: usize>(&mut self) -> Result<[u8; N]> {
        let mut b = [0; N];
        self.0.read_exact(&mut b)?;
        Ok(b)
    }
    fn u8(&mut self) -> Result<u8> {
        Ok(self.bytes::<1>()?[0])
    }
    fn u16(&mut self) -> Result<u16> {
        Ok(u16::from_le_bytes(self.bytes()?))
    }
    fn f32(&mut self) -> Result<f32> {
        let f = f32::from_le_bytes(self.bytes()?);
        if !f.is_finite() {
            bail!("non-finite vehicle spawn data");
        }
        Ok(f)
    }
    fn string(&mut self) -> Result<String> {
        let n = self.u8()? as usize;
        let mut s = vec![0; n];
        self.0.read_exact(&mut s)?;
        Ok(String::from_utf8(s)?)
    }
}
fn tier_for(tiers: &[(f32, String)], point: usize) -> Option<&str> {
    if tiers.len() < 2 {
        return None;
    }
    let total: f32 = tiers.iter().map(|v| v.0).sum();
    if total <= 0. {
        return Some(&tiers[point % tiers.len()].1);
    }
    let h = (point as u32).wrapping_mul(2654435761);
    let mut chance = (h >> 16) as f32 / 65536. * total;
    for (weight, name) in tiers {
        chance -= weight;
        if chance <= 0. {
            return Some(name);
        }
    }
    tiers.last().map(|v| v.1.as_str())
}
pub fn decode(bytes: &[u8]) -> Result<Vec<SpawnPoint>> {
    let mut r = River(Cursor::new(bytes));
    let version = r.u8()?;
    if version > 1 && version < 3 {
        r.bytes::<8>()?;
    }
    let tables = r.u8()?;
    let mut tiers = Vec::new();
    for _ in 0..tables {
        r.bytes::<3>()?;
        r.string()?;
        if version > 3 {
            r.u16()?;
        }
        let count = r.u8()?;
        let mut table = Vec::new();
        for _ in 0..count {
            let name = r.string()?;
            let chance = r.f32()?;
            let ids = r.u8()?;
            for _ in 0..ids {
                r.u16()?;
            }
            table.push((chance, name));
        }
        tiers.push(table);
    }
    let count = r.u16()?;
    let mut points = Vec::new();
    for index in 0..count as usize {
        let table = r.u8()?;
        let position = Vec3::new(r.f32()?, r.f32()?, -r.f32()?);
        let yaw = -(r.u8()? as f32) * 2.;
        if table > 5 && table != 8 {
            continue;
        }
        let preferred = tiers
            .get(table as usize)
            .and_then(|t| tier_for(t, index))
            .map(|s| s.to_lowercase().replace(' ', "_"));
        let key = if table == 8 {
            "runabout"
        } else if let Some(name) = preferred.as_deref().filter(|s| data::named(s).is_some()) {
            name
        } else {
            match table {
                0 => [
                    "sedan",
                    "hatchback",
                    "roadster",
                    "offroader",
                    "truck",
                    "van",
                ][index % 6],
                1 => "police",
                2 => "firetruck",
                3 => ["humvee", "jeep", "ural"][index % 3],
                4 => "ambulance",
                _ => "tractor",
            }
        };
        let definition = data::named(key).expect("original vehicle spawn name");
        points.push(SpawnPoint {
            index,
            type_id: definition.type_id,
            position,
            yaw,
            water: table == 8,
        });
    }
    Ok(points)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn generated_plan_keeps_source_pose_and_variant_and_waits_for_loaded_terrain() {
        let mut geometry = crate::world::WorldGeometry::default();
        geometry.loaded_center = Vec3::new(20., 0., 20.);
        geometry.loaded_radius = 50.;
        geometry.terrain.push(crate::world::TerrainPatch {
            origin: Vec2::ZERO,
            cells: 1,
            spacing: 1024.,
            heights: vec![9.; 4],
            layers: vec![0; 4],
            normals: vec![[0., 1., 0.]; 4],
            holes: vec![false],
            splat: None,
        });
        let snapshot = serde_json::json!({"finiteVehicles":[
            {"key":"Island_T:generated-vehicle:0","kind":"sedan","variant":3,"position":[20.,77.1,20.],"yaw":143.},
            {"key":"Island_T:generated-vehicle:1","kind":"jeep","variant":2,"position":[900.,44.1,20.],"yaw":-28.}
        ]});
        let mut registered = std::collections::HashSet::new();
        let requests = generated_requests(&snapshot, &geometry, &registered);
        assert_eq!(requests.len(), 1);
        assert_eq!(
            requests[0].1["position"],
            serde_json::json!([20., 77.1_f32, 20.])
        );
        assert_eq!(requests[0].1["yaw"], 143.);
        assert_eq!(requests[0].1["variant"], 3);
        assert_eq!(requests[0].1["key"], "Island_T:generated-vehicle:0");
        registered.insert(requests[0].0);
        assert!(generated_requests(&snapshot, &geometry, &registered).is_empty());
        geometry.loaded_center.x = 900.;
        let far = generated_requests(&snapshot, &geometry, &registered);
        assert_eq!(far.len(), 1);
        assert_eq!(far[0].1["position"][1], serde_json::json!(44.1_f32));
        assert_eq!(far[0].1["variant"], 2);
        geometry.terrain.clear();
        assert!(generated_requests(&snapshot, &geometry, &registered).is_empty());
    }
    #[test]
    fn single_fire_truck_label_does_not_select_civilian_pickup() {
        assert!(tier_for(&[(1., "Truck".into())], 14).is_none());
        let farm = [(0.667, "Quad".into()), (0.333, "Tractor".into())];
        let quads = (0..1000)
            .filter(|i| tier_for(&farm, *i) == Some("Quad"))
            .count();
        assert!((665..=669).contains(&quads), "{quads}");
    }
    #[test]
    fn truncated_spawn_data_is_rejected() {
        assert!(decode(&[4, 1, 0, 0, 0, 8, b'C']).is_err());
    }
}
