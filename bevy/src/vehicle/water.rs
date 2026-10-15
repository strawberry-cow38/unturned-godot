//! Vehicle.ApplySwampedPhysics: trapped air in a land vehicle, then irreversible drowning.
use super::data::VehicleSpec;
use bevy::prelude::*;
use bevy_rapier3d::prelude::*;

#[derive(Clone, Debug, Default)]
pub struct Swamp {
    pub active: bool,
    pub elapsed: f32,
    voxels: Vec<Vec3>,
    half_height: f32,
}
impl Swamp {
    pub fn new(spec: &VehicleSpec) -> Self {
        let size = Vec3::from_array(spec.box_size);
        if spec.heli || spec.plane || spec.water == "Boat" || size == Vec3::ZERO {
            return Self::default();
        }
        let cell = size * 0.5;
        let min = Vec3::from_array(spec.box_center) - size * 0.5;
        let mut voxels = Vec::with_capacity(8);
        for x in 0..2 {
            for y in 0..2 {
                for z in 0..2 {
                    voxels.push(
                        min + cell * (Vec3::new(x as f32, y as f32, z as f32) + Vec3::splat(0.5)),
                    );
                }
            }
        }
        Self {
            voxels,
            half_height: cell.min_element() * 0.5,
            ..default()
        }
    }
    /// The boolean is the host's trusted water-contact fact, sent to the authoritative bridge.
    pub fn step(
        &mut self,
        spec: &VehicleSpec,
        pose: &Transform,
        velocity: &Velocity,
        sea: Option<f32>,
        exploded: bool,
        dt: f32,
    ) -> (ExternalForce, bool) {
        let mut force = ExternalForce::default();
        let Some(sea) = sea.filter(|_| !exploded && !self.voxels.is_empty()) else {
            return (force, false);
        };
        let submerged = self
            .voxels
            .iter()
            .filter(|p| pose.transform_point(**p).y < sea)
            .count();
        if submerged < (self.voxels.len() as f32 * 0.25).ceil() as usize {
            self.active = false;
            self.elapsed = 0.;
            return (force, false);
        }
        self.active = true;
        self.elapsed += dt;
        let lift = 1. - ((self.elapsed - 5.) / 4.).clamp(0., 1.);
        let com = pose.transform_point(spec.center_of_mass().0);
        let arch = Vec3::Y * (1000. * 9.8 * (spec.mass() / 800.) * lift / self.voxels.len() as f32);
        for local in &self.voxels {
            let point = pose.transform_point(*local);
            if point.y >= sea {
                continue;
            }
            let arm = point - com;
            let point_velocity = velocity.linear + velocity.angular.cross(arm);
            let damping = -point_velocity * (0.1 * spec.mass());
            let depth = ((sea - point.y) / (2. * self.half_height) + 0.5)
                .clamp(0., 1.)
                .sqrt();
            let f = damping + arch * depth;
            force.force += f;
            force.torque += arm.cross(f);
        }
        (force, true)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn land_vehicle_floats_briefly_then_loses_lift_but_keeps_water_drag() {
        let spec = &super::super::data::named("jeep").unwrap().spec;
        let mut water = Swamp::new(spec);
        let pose = Transform::from_xyz(0., -3., 0.);
        let (initial, drowned) =
            water.step(spec, &pose, &Velocity::default(), Some(0.), false, 0.02);
        assert!(drowned && initial.force.y > spec.mass() * 9.8);
        for _ in 0..500 {
            water.step(spec, &pose, &Velocity::default(), Some(0.), false, 0.02);
        }
        let (empty, _) = water.step(spec, &pose, &Velocity::default(), Some(0.), false, 0.02);
        assert_eq!(
            empty.force,
            Vec3::ZERO,
            "all trapped air has escaped after nine seconds"
        );
        let (drag, _) = water.step(
            spec,
            &pose,
            &Velocity {
                linear: Vec3::NEG_Y,
                ..default()
            },
            Some(0.),
            false,
            0.02,
        );
        assert!(
            drag.force.y > spec.mass() * 0.79,
            "water keeps resisting sinking after buoyancy is gone"
        );
        assert!(
            !water
                .step(
                    spec,
                    &Transform::from_xyz(0., 4., 0.),
                    &Velocity::default(),
                    Some(0.),
                    false,
                    0.02
                )
                .1
        );
        assert_eq!(water.elapsed, 0.);
    }
    #[test]
    fn boats_and_aircraft_never_use_car_swamping() {
        for key in ["runabout", "otter", "huey"] {
            let spec = &super::super::data::named(key).unwrap().spec;
            assert!(
                !Swamp::new(spec)
                    .step(
                        spec,
                        &Transform::from_xyz(0., -10., 0.),
                        &Velocity::default(),
                        Some(0.),
                        false,
                        1.
                    )
                    .1
            );
        }
    }
}
