//! Exact original fleet metadata, evaluated by tools/export_vehicles.py.
use bevy::prelude::*;
use bevy_rapier3d::prelude::*;
use serde::Deserialize;
use std::sync::LazyLock;

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct VehicleDefinition {
    pub type_id: u8,
    pub key: String,
    pub spec: VehicleSpec,
    pub seats: Vec<[f32; 3]>,
    pub body_seats: Vec<[f32; 3]>,
    pub access: Vec<AccessZone>,
    pub paints: Vec<[f32; 4]>,
    pub roof: Option<([f32; 3], [f32; 3])>,
}
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct AccessZone {
    pub kind: String,
    pub seat: i32,
    pub center: [f32; 3],
    pub size: [f32; 3],
}
#[derive(Debug, Clone, Deserialize, Default)]
#[serde(rename_all = "PascalCase", default)]
pub struct VehicleSpec {
    pub name: String,
    pub body: Option<String>,
    pub wheel: Option<String>,
    pub wheel_tex: Option<String>,
    pub palette: Option<String>,
    pub parts: Option<Vec<(String, [f32; 4])>>,
    pub turrets: Option<Vec<TurretDef>>,
    pub treads: Option<String>,
    pub turret_meshes: Option<Vec<String>>,
    pub gun_mesh: Option<String>,
    pub glass_mesh: Option<String>,
    pub glass_tint: Option<[f32; 4]>,
    pub frame: String,
    pub heli_body_meshes: Option<Vec<String>>,
    pub heli_rotor_mesh_prefix: Option<String>,
    pub prop_mesh_prefix: Option<String>,
    pub prop_hub: [f32; 3],
    pub missile_mesh: Option<String>,
    pub steer_mesh: Option<String>,
    pub steer_model: Option<String>,
    pub steer_pivot: [f32; 3],
    pub steer_axis: [f32; 3],
    pub seat_model_file: Option<String>,
    pub seat_model: [f32; 3],
    pub exhaust_pos: Option<[f32; 3]>,
    pub retract_gear: bool,
    pub rotor_hub: [f32; 3],
    pub tail_rotor_hub: [f32; 3],
    pub rotor_radius: f32,
    pub tail_rotor_radius: f32,
    pub main_hub_box: [f32; 3],
    pub tail_hub_box: [f32; 3],
    pub main_rotor_hp: f32,
    pub tail_rotor_hp: f32,
    pub rear_engine: bool,
    pub seat_body_rise: f32,
    pub driver_eye: [f32; 3],
    pub access_required: bool,
    pub box_size: [f32; 3],
    pub box_center: [f32; 3],
    pub extra_boxes: Option<Vec<([f32; 3], [f32; 3])>>,
    pub hull_boxes: Option<Vec<([f32; 3], [f32; 3], f32)>>,
    pub hull_bands: Option<Vec<([f32; 3], [f32; 3])>>,
    pub hull_decompose: Option<([f32; 3], [f32; 3])>,
    pub hull_trimesh: Option<([f32; 3], [f32; 3])>,
    pub mass: f32,
    pub wheel_radius: f32,
    pub wheel_radii: Option<Vec<f32>>,
    pub wheels: Option<Vec<(f32, f32, f32, bool)>>,
    pub engine: f32,
    pub brake: f32,
    pub steer_max: f32,
    pub steer_min: f32,
    pub speed_max: f32,
    pub speed_min: f32,
    pub health: f32,
    pub fuel: f32,
    pub sound: Option<String>,
    pub ignition_sound: Option<String>,
    pub horn: Option<String>,
    pub horn_cooldown: f32,
    pub horn_range: f32,
    pub idle_pitch: f32,
    pub max_pitch: f32,
    pub idle_volume: f32,
    pub max_volume: f32,
    pub kingpin: [f32; 3],
    pub off_road: bool,
    pub tracked: bool,
    pub heli: bool,
    pub plane: bool,
    pub water: String,
    pub buoys: Option<Vec<[f32; 3]>>,
    pub buoy_slices: i32,
    pub buoy_lift: f32,
    pub buoy_damp: f32,
    pub buoy_reserve: f32,
    pub turn_scale: f32,
    pub steady_hull: bool,
    pub heli_thrust: f32,
    pub heli_pitch_torque: f32,
    pub heli_roll_torque: f32,
    pub heli_yaw_torque: f32,
    pub heli_level: f32,
    pub sling_hook: bool,
    pub heli_climb_max: f32,
    pub heli_fall_max: f32,
    pub plane_thrust: f32,
    pub plane_lift: f32,
    pub plane_target_speed: f32,
    pub plane_pitch_torque: f32,
    pub plane_roll_torque: f32,
    pub plane_yaw_torque: f32,
    pub plane_steer_fade: f32,
}
#[derive(Deserialize)]
struct Export {
    vehicles: Vec<VehicleDefinition>,
}
/// Exact source mounted-weapon metadata, including parent frames and seat priority.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct TurretDef {
    pub seat: u8,
    pub yaw_mesh: Option<String>,
    pub pitch_mesh: Option<String>,
    pub pivot: [f32; 3],
    pub muzzle: [f32; 3],
    pub yaw_min: f32,
    pub yaw_max: f32,
    pub pitch_min: f32,
    pub pitch_max: f32,
    pub gunner_at: [f32; 3],
    pub mesh_rotation_deg: [f32; 3],
    pub gun_id: String,
    pub display_name: String,
    pub belt: u16,
    pub colour: [f32; 4],
    pub cycle: f32,
    pub yaw_free: bool,
    pub driver_fallback: bool,
    pub crosshair_aim: bool,
    pub hold_to_aim: bool,
    pub parent_mount: i32,
    pub yaw_mesh_rotation_deg: [f32; 3],
    pub yaw_rate: f32,
    pub pitch_rate: f32,
    pub pitch_offset: [f32; 3],
}
pub static FLEET: LazyLock<Vec<VehicleDefinition>> = LazyLock::new(|| {
    serde_json::from_str::<Export>(include_str!("../../data/vehicles.json"))
        .expect("original vehicle metadata must deserialize")
        .vehicles
});
pub fn definition(type_id: u8) -> Option<&'static VehicleDefinition> {
    FLEET.get(type_id as usize).filter(|d| d.type_id == type_id)
}
pub fn named(key: &str) -> Option<&'static VehicleDefinition> {
    FLEET.iter().find(|d| d.key == key)
}
pub fn speed_buff() -> f32 {
    std::env::var("UG_BUFF")
        .ok()
        .and_then(|v| v.parse().ok())
        .filter(|v: &f32| v.is_finite() && *v > 0.)
        .unwrap_or(2.5)
}
impl VehicleSpec {
    pub fn rotor_health_max(&self, main: bool) -> f32 {
        let explicit = if main {
            self.main_rotor_hp
        } else {
            self.tail_rotor_hp
        };
        if explicit > 0. {
            explicit
        } else {
            self.health * if main { 0.45 } else { 0.28 }
        }
    }
    pub fn mass(&self) -> f32 {
        if self.mass > 0. { self.mass } else { 900. }
    }
    pub fn ground_clearance(&self) -> f32 {
        let mut lowest = (self.box_center[1] - self.box_size[1] * 0.5).min(0.);
        for (size, center) in self.extra_boxes.iter().flatten() {
            lowest = lowest.min(center[1] - size[1] * 0.5);
        }
        if self.plane {
            for wheel in self.wheels() {
                lowest = lowest.min(wheel.1 - self.wheel_radius);
            }
        }
        -lowest
    }
    pub fn wheels(&self) -> &[(f32, f32, f32, bool)] {
        self.wheels.as_deref().unwrap_or(&[])
    }
    pub fn wheel_radius(&self, i: usize) -> f32 {
        self.wheel_radii
            .as_ref()
            .and_then(|r| r.get(i))
            .copied()
            .unwrap_or(self.wheel_radius)
    }
    pub fn drives_wheel(&self, index: usize) -> bool {
        if self.kingpin != [0.; 3] {
            return false;
        }
        if self.off_road || self.tracked || self.plane || self.heli {
            return true;
        }
        !self.wheels().iter().any(|w| !w.3) || !self.wheels()[index].3
    }
    pub fn drivetrain(&self) -> bool {
        !self.heli
            && !self.plane
            && self.engine > 0.
            && self.speed_max > 0.
            && self.wheel_radius > 0.
            && self
                .wheels()
                .iter()
                .enumerate()
                .any(|(i, _)| self.drives_wheel(i))
    }
    pub fn speed_max(&self) -> f32 {
        self.speed_max * if self.drivetrain() { speed_buff() } else { 1. }
    }
    pub fn center_of_mass(&self) -> (Vec3, f32) {
        let wheels = self.wheels();
        let rest = if self.tracked { 0.15 } else { 0.25 };
        // Preserve the actual source branches, including the car box-based CoM.
        let mut y = if self.tracked {
            0.1
        } else if self.plane {
            if wheels.is_empty() {
                self.box_center[1] - self.box_size[1] * 0.60
            } else {
                wheels.iter().map(|w| w.1).sum::<f32>() / wheels.len() as f32 - 0.2
            }
        } else {
            self.box_center[1] - self.box_size[1] * 0.25
        };
        let mut ssf = 0.;
        if !self.tracked && !self.plane && !self.heli && !wheels.is_empty() {
            let track = wheels.iter().map(|w| w.0.abs()).fold(0., f32::max);
            let ground = wheels
                .iter()
                .enumerate()
                .map(|(i, w)| w.1 - rest - self.wheel_radius(i))
                .fold(f32::INFINITY, f32::min);
            if track > 0.01 && y - ground > 0.05 {
                ssf = track / (y - ground);
                if ssf < 1. {
                    y = ground + track;
                    ssf = 1.;
                }
            }
        }
        (Vec3::new(0., y, 0.), ssf)
    }
    pub fn controller(&self) -> RayCastVehicleController {
        let count = self.wheels().len().max(1) as f32;
        let load = (self.mass() / count) / (900. / 4.);
        let headroom = if load > 3. && !self.tracked { 1.8 } else { 3. };
        let wheels = self
            .wheels()
            .iter()
            .enumerate()
            .map(|(i, w)| {
                let tuning = WheelTuning {
                    suspension_stiffness: (if self.plane { 30. } else { 55. }) * load,
                    suspension_compression: if self.plane { 7. } else { 3.5 },
                    suspension_damping: if self.plane { 8. } else { 4.2 },
                    max_suspension_travel: if self.tracked { 0.2 } else { 0.25 },
                    max_suspension_force: headroom * self.mass() * 9.8 / count,
                    friction_slip: if self.tracked {
                        1.
                    } else if self.kingpin != [0.; 3] {
                        1.5
                    } else if self.plane {
                        2.
                    } else {
                        6.
                    },
                    ..default()
                };
                VehicleWheel::new(
                    Vec3::new(w.0, w.1, w.2),
                    Vec3::NEG_Y,
                    Vec3::X,
                    if self.tracked { 0.15 } else { 0.25 },
                    self.wheel_radius(i),
                    tuning,
                )
            })
            .collect();
        RayCastVehicleController {
            filter_groups: Some(CollisionGroups::new(
                Group::GROUP_4,
                Group::ALL & !Group::GROUP_3,
            )),
            index_forward_axis: 2,
            index_up_axis: 1,
            ..RayCastVehicleController::new(wheels)
        }
    }
}

impl VehicleDefinition {
    /// Vehicle.SeatLabels: geometric rows and sides, including source duplicate suffix ordering.
    pub fn seat_labels(&self) -> Vec<String> {
        let seats = &self.seats[..self.seats.len().min(12)];
        let mut rows: Vec<f32> = Vec::new();
        for s in seats {
            if !rows.iter().any(|r| (*r - s[2]).abs() < 0.4) {
                rows.push(s[2]);
            }
        }
        rows.sort_by(|a, b| a.total_cmp(b));
        let mut labels: Vec<String> = seats
            .iter()
            .enumerate()
            .map(|(i, s)| {
                let row = rows
                    .iter()
                    .position(|r| (*r - s[2]).abs() < 0.4)
                    .unwrap_or(0);
                let side = if s[0] < -0.15 {
                    "left"
                } else if s[0] > 0.15 {
                    "right"
                } else {
                    "middle"
                };
                if i == 0 {
                    "Driver's seat".into()
                } else if row == 0 {
                    "Front passenger".into()
                } else if rows.len() == 2 {
                    format!("Rear {side}")
                } else {
                    format!("Row {} {side}", row + 1)
                }
            })
            .collect();
        for i in 0..labels.len() {
            if labels.iter().filter(|s| **s == labels[i]).count() > 1 {
                labels[i] = format!("{} ({})", labels[i], i + 1);
            }
        }
        labels
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn original_fleet_keeps_replicated_ids_and_aircraft_shapes() {
        assert_eq!(FLEET.len(), 39);
        assert_eq!(definition(0).unwrap().key, "jeep");
        assert_eq!(definition(29).unwrap().key, "otter");
        assert_eq!(definition(38).unwrap().key, "animal_trailer");
        for key in ["otter", "fighterjet", "jet"] {
            assert!(named(key).unwrap().spec.plane, "{key}");
        }
        assert!(named("skycrane").unwrap().spec.heli);
        for d in FLEET.iter() {
            assert!(!d.seats.is_empty());
            assert_eq!(d.paints.len(), 256);
        }
    }
    #[test]
    fn retail_axles_and_authored_suspension_are_preserved() {
        let sedan = &named("sedan").unwrap().spec;
        assert_eq!(
            (0..sedan.wheels().len())
                .filter(|i| sedan.drives_wheel(*i))
                .count(),
            2
        );
        let jeep = &named("jeep").unwrap().spec;
        assert_eq!(jeep.mass(), 1700.);
        let controller = jeep.controller();
        assert_eq!(controller.wheels.len(), 4);
        assert!((controller.wheels[0].tuning.max_suspension_force - 12495.).abs() < 0.1);
        assert_eq!(
            controller.wheels[0].chassis_connection_point,
            Vec3::new(-1.3, 0.25, -1.4)
        );
        assert_eq!(controller.wheels[0].axle, Vec3::X);
        assert_eq!(named("quad").unwrap().spec.center_of_mass().1, 1.);
    }
}
