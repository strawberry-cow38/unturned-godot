//! Original outer radial-gap split: popping removes rubber while the real rim keeps turning.
use super::{VehicleBody, data::VehicleSpec};
use crate::content::{ContentCache, ObjData};
use bevy::prelude::*;
use bevy_rapier3d::prelude::*;
use std::{path::Path, sync::Arc};

#[derive(Component)]
pub struct TireVisual {
    pub body: Entity,
    pub index: usize,
}

fn split(source: &ObjData) -> Option<(ObjData, ObjData)> {
    if source.positions.len() < 6 {
        return None;
    }
    let centre = source
        .positions
        .iter()
        .map(|p| Vec3::from_array(*p))
        .sum::<Vec3>()
        / source.positions.len() as f32;
    let radial = |p: [f32; 3]| {
        let d = Vec3::from_array(p) - centre;
        Vec2::new(d.y, d.z).length()
    };
    let mut radii: Vec<_> = source.positions.iter().map(|p| radial(*p)).collect();
    radii.sort_by(f32::total_cmp);
    let max = *radii.last()?;
    let mut gap = 0.;
    let mut seam = 0.;
    for pair in radii.windows(2) {
        if pair[0] < 0.35 * max {
            continue;
        }
        let width = pair[1] - pair[0];
        if width > gap {
            gap = width;
            seam = (pair[0] + pair[1]) * 0.5;
        }
    }
    if gap < 0.02 {
        return None;
    }
    let mut tire = ObjData {
        min: Vec3::splat(f32::INFINITY),
        max: Vec3::splat(f32::NEG_INFINITY),
        ..default()
    };
    let mut rim = tire.clone();
    for i in (0..source.positions.len()).step_by(3) {
        let target = if source.positions[i..i + 3].iter().any(|p| radial(*p) > seam) {
            &mut tire
        } else {
            &mut rim
        };
        for n in i..i + 3 {
            target.positions.push(source.positions[n]);
            target.normals.push(source.normals[n]);
            target.uvs.push(source.uvs[n]);
            target.colors.push(source.colors[n]);
            let p = Vec3::from_array(source.positions[n]);
            target.min = target.min.min(p);
            target.max = target.max.max(p);
        }
    }
    if tire.positions.is_empty() || rim.positions.is_empty() {
        None
    } else {
        Some((tire, rim))
    }
}
pub fn load(
    path: &Path,
    cache: &mut ContentCache,
    meshes: &mut Assets<Mesh>,
) -> Option<(Handle<Mesh>, Handle<Mesh>)> {
    let tire_key = path.with_extension("tire-native");
    let rim_key = path.with_extension("rim-native");
    if let (Some(tire), Some(rim)) = (cache.meshes.get(&tire_key), cache.meshes.get(&rim_key)) {
        return Some((tire.clone(), rim.clone()));
    }
    let (_, source) = cache.load_mesh(path, meshes).ok()?;
    let (tire, rim) = split(&source)?;
    let tire_mesh = meshes.add(tire.mesh());
    let rim_mesh = meshes.add(rim.mesh());
    cache.meshes.insert(tire_key.clone(), tire_mesh.clone());
    cache.geometry.insert(tire_key, Arc::new(tire));
    cache.meshes.insert(rim_key.clone(), rim_mesh.clone());
    cache.geometry.insert(rim_key, Arc::new(rim));
    Some((tire_mesh, rim_mesh))
}
pub fn apply_physics(
    spec: &VehicleSpec,
    mask: u8,
    wheels: &mut RayCastVehicleController,
    surface_grip: f32,
) {
    if spec.plane || spec.tracked {
        return;
    }
    for (i, wheel) in wheels.wheels.iter_mut().enumerate() {
        let popped = i < 8 && mask & (1 << i) != 0;
        wheel.radius = spec.wheel_radius(i) * if popped { 0.78 } else { 1. };
        if popped {
            wheel.tuning.friction_slip =
                (if spec.kingpin == [0.; 3] { 6. } else { 1.5 }) * 0.35 * surface_grip;
        }
    }
}
pub fn update(bodies: Query<&VehicleBody>, mut tires: Query<(&TireVisual, &mut Visibility)>) {
    for (tire, mut visibility) in &mut tires {
        if let Ok(body) = bodies.get(tire.body) {
            *visibility = if tire.index < 8 && body.popped_tire_mask & (1 << tire.index) != 0 {
                Visibility::Hidden
            } else {
                Visibility::Inherited
            };
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn actual_sedan_tire_split_preserves_every_triangle_and_replacement_restores_own_radius() {
        let spec = &super::super::data::named("sedan").unwrap().spec;
        let path = Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("../game/content")
            .join(spec.wheel.as_ref().unwrap());
        let source = ObjData::load(&path).unwrap();
        let (tire, rim) = split(&source).expect("source wheel contains a real rim/tread gap");
        assert_eq!(
            tire.positions.len() + rim.positions.len(),
            source.positions.len()
        );
        assert!(tire.positions.len() > 30 && rim.positions.len() > 30);
        let mut wheels = spec.controller();
        let stock = wheels.wheels[0].radius;
        apply_physics(spec, 1, &mut wheels, 1.);
        assert_eq!(wheels.wheels[0].radius, stock * 0.78);
        assert!((wheels.wheels[0].tuning.friction_slip - 6. * 0.35).abs() < 1e-6);
        apply_physics(spec, 0, &mut wheels, 1.);
        assert_eq!(wheels.wheels[0].radius, stock);
    }
}
