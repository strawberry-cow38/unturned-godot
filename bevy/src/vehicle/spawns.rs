//! Original WorldBuilder.SpawnPeiVehicles River format and weighted selection.
use super::data;
use anyhow::{Result, bail};
use bevy::prelude::*;
use std::io::{Cursor, Read};

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
