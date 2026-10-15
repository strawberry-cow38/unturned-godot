//! Native force integration of Vehicle.StepHeli/StepPlane and the source virtual cyclic.
use super::{
    data::VehicleSpec,
    dynamics::{Controls, approach},
};
use bevy::{
    audio::{Decodable, Source},
    prelude::*,
};
use bevy_rapier3d::prelude::*;
use std::path::Path;

#[derive(Clone, Debug)]
pub struct Flight {
    pub spool: f32,
    pub collective: f32,
    pub main_phase: f32,
    pub tail_phase: f32,
    pub prop_phase: f32,
    pub stick: Vec2, // pitch, roll, before cross-axis deadzone
    pub main_health: f32,
    pub tail_health: f32,
    pub gear_down: bool,
    pub gear_deploy: f32,
    pub lift_acceleration: Vec3,
    ignition_length: f32,
    ignition_remaining: f32,
    ignition_fired: bool,
    ge_applied: f32,
    drag_forward: f32,
    lift_cap: f32,
    grace: f32,
    wheel_support: Vec<(f32, f32)>,
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::vehicle::data;

    #[test]
    fn original_ignition_audio_gates_lift_while_rotor_already_turns() {
        let spec = &data::named("huey").unwrap().spec;
        let duration = ignition_duration(
            spec,
            &Path::new(env!("CARGO_MANIFEST_DIR")).join("../game/content"),
        );
        assert!(
            duration > 5. && duration < 15.,
            "decode actual original ignition, got {duration}"
        );
        let mut flight = Flight::new(spec, duration);
        let mut wheels = spec.controller();
        let input = Controls {
            engine: true,
            throttle: 1.,
            ..default()
        };
        for _ in 0..(duration * 0.74 / 0.02).floor() as usize {
            flight.step(
                spec,
                &Transform::IDENTITY,
                &Velocity::default(),
                input,
                AirContact::default(),
                &mut wheels,
                true,
                false,
                0.02,
            );
            assert_eq!(
                flight.lift_acceleration,
                Vec3::ZERO,
                "starter sound must finish before thrust begins"
            );
        }
        assert!(
            flight.spool > 0.95 && flight.main_phase != 0.,
            "blades visibly wind up during the ignition gate"
        );
        for _ in 0..3 {
            flight.step(
                spec,
                &Transform::IDENTITY,
                &Velocity::default(),
                input,
                AirContact::default(),
                &mut wheels,
                true,
                false,
                0.02,
            );
        }
        assert!(flight.lift_acceleration.y > 10.);
    }
    #[test]
    fn ground_cushion_trim_does_not_lift_a_hands_off_helicopter() {
        for key in ["minicopter", "hind"] {
            let spec = &data::named(key).unwrap().spec;
            let mut flight = Flight::new(spec, 0.);
            flight.spool = 1.;
            let mut wheels = spec.controller();
            for _ in 0..300 {
                flight.step(
                    spec,
                    &Transform::IDENTITY,
                    &Velocity::default(),
                    Controls {
                        engine: true,
                        ..default()
                    },
                    AirContact {
                        ground_effect: ground_effect(spec.rotor_radius, Some(0.1)),
                        ..default()
                    },
                    &mut wheels,
                    true,
                    false,
                    0.02,
                );
            }
            assert!(
                flight.lift_acceleration.y > 8.9 && flight.lift_acceleration.y < 9.1,
                "{key} must retain source 92% hover trim inside ground effect: {:?}",
                flight.lift_acceleration
            );
        }
    }
    #[test]
    fn virtual_stick_keeps_cross_axis_deadzone_invert_and_self_centre() {
        let spec = &data::named("huey").unwrap().spec;
        let mut flight = Flight::new(spec, 0.);
        let input = flight.input(Vec2::new(10., -1.), Vec2::ZERO, false, 1., 0.02);
        assert_eq!(
            input.x, 0.,
            "tiny vertical jitter must not pitch during a roll"
        );
        assert!(input.y > 0.15);
        for _ in 0..100 {
            flight.input(Vec2::ZERO, Vec2::ZERO, false, 1., 0.02);
        }
        assert!(flight.stick.length() < 0.00001);
        assert!(
            flight
                .input(Vec2::new(0., -10.), Vec2::ZERO, true, 1., 0.02)
                .x
                > 0.1
        );
    }
    #[test]
    fn plane_stall_and_dive_lift_use_airflow_not_forward_speed_alone() {
        let spec = &data::named("otter").unwrap().spec;
        let mut flight = Flight::new(spec, 0.);
        let mut wheels = spec.controller();
        assert!(lift_coefficient(15.) > lift_coefficient(25.) * 3.);
        assert_eq!(lift_coefficient(-10.), 0.);
        let pose = Transform::from_rotation(Quat::from_rotation_x((-35f32).to_radians()));
        let forward = pose.rotation * Vec3::NEG_Z;
        let up = pose.rotation * Vec3::Y;
        // Same angle of attack at two dive speeds; the old 1.3x cap made these almost identical.
        let mut acceleration = Vec::new();
        for speed in [20., 40.] {
            let velocity = Velocity {
                linear: (forward * 0.99 - up * 0.14) * speed,
                ..default()
            };
            flight.step(
                spec,
                &pose,
                &velocity,
                Controls::default(),
                AirContact::default(),
                &mut wheels,
                true,
                false,
                0.02,
            );
            acceleration.push(flight.lift_acceleration.length());
        }
        assert!(
            acceleration[1] > acceleration[0] * 3.8,
            "altitude spent must buy recovery authority: {acceleration:?}"
        );
    }
}
#[derive(Clone, Copy)]
pub struct AirContact {
    pub grounded: bool,
    pub afloat: bool,
    pub ground_effect: f32,
}
impl Default for AirContact {
    fn default() -> Self {
        Self {
            grounded: false,
            afloat: false,
            ground_effect: 1.,
        }
    }
}
pub struct FlightStep {
    pub force: ExternalForce,
    pub damping: Damping,
    pub park: bool,
}

pub fn ignition_duration(spec: &VehicleSpec, root: &Path) -> f32 {
    if !spec.heli {
        return 0.;
    }
    let Some(path) = spec.ignition_sound.as_deref() else {
        return 0.;
    };
    let Ok(bytes) = std::fs::read(root.join(path)) else {
        return 0.;
    };
    // Use the shipping audio decoder, so clip timing remains correct for replacement content.
    std::panic::catch_unwind(|| {
        let source = AudioSource {
            bytes: bytes.into(),
        };
        let decoder = source.decoder();
        let seconds = if let Some(duration) = decoder.total_duration() {
            duration.as_secs_f32()
        } else {
            let rate = decoder.sample_rate().get() as f32 * decoder.channels().get() as f32;
            decoder.count() as f32 / rate
        };
        seconds / size_pitch(spec).max(0.01)
    })
    .unwrap_or(0.)
}
pub fn size_pitch(spec: &VehicleSpec) -> f32 {
    if !spec.heli {
        return 1.;
    }
    let volume = spec.box_size.iter().product::<f32>();
    if volume < 0.01 {
        1.
    } else {
        (2.970 / volume.cbrt()).sqrt().clamp(0.78, 1.5)
    }
}
pub fn ground_effect(radius: f32, rotor_height: Option<f32>) -> f32 {
    if radius < 0.01 {
        return 1.;
    }
    let Some(height) = rotor_height else {
        return 1.;
    };
    let ratio = radius / (4. * height.max(radius * 0.5));
    1. / (1. - ratio * ratio).max(0.1)
}
fn level_flight_acceleration(thrust: f32) -> f32 {
    let mut best: f32 = 0.;
    for step in 1..=900 {
        let tilt = (step as f32 * 0.1).to_radians();
        let lift = thrust * (1. - 0.55 * (1. - tilt.cos()));
        if lift * tilt.cos() < 9.8 {
            break;
        }
        best = best.max(lift * tilt.sin());
    }
    best
}
pub fn lift_coefficient(angle_degrees: f32) -> f32 {
    if angle_degrees <= 15. {
        0.34 + angle_degrees * 0.09
    } else {
        (0.34 + 15. * 0.09) - (angle_degrees - 15.) * 0.09 * 2.2
    }
    .clamp(0., 1.7)
}
impl Flight {
    pub fn new(spec: &VehicleSpec, ignition_length: f32) -> Self {
        Self {
            spool: 0.,
            collective: 0.,
            main_phase: 0.,
            tail_phase: 0.,
            prop_phase: 0.,
            stick: Vec2::ZERO,
            main_health: 1.,
            tail_health: 1.,
            gear_down: true,
            gear_deploy: 1.,
            lift_acceleration: Vec3::ZERO,
            ignition_length,
            ignition_remaining: 0.,
            ignition_fired: false,
            ge_applied: 1.,
            drag_forward: if spec.heli && spec.speed_max > 0.01 {
                level_flight_acceleration(spec.heli_thrust * 1.05) / spec.speed_max.powi(2)
            } else {
                0.
            },
            lift_cap: if spec.heli && spec.heli_thrust > 0.01 {
                ((9.8 + 0.45 * spec.heli_climb_max * 0.9) / spec.heli_thrust).max(1.)
            } else {
                1.
            },
            grace: if spec.plane && !spec.wheels().is_empty() {
                0.4
            } else {
                2.
            },
            wheel_support: spec
                .controller()
                .wheels
                .iter()
                .map(|w| (w.tuning.max_suspension_force, w.tuning.friction_slip))
                .collect(),
        }
    }
    pub fn input(
        &mut self,
        delta: Vec2,
        arrows: Vec2,
        invert: bool,
        sensitivity: f32,
        dt: f32,
    ) -> Vec2 {
        let sign = if invert { -1. } else { 1. };
        self.stick += Vec2::new(delta.y * sign, delta.x) * (0.020 * sensitivity);
        self.stick = self.stick.clamp(Vec2::NEG_ONE, Vec2::ONE);
        for (axis, target) in [(0, arrows.x * sign), (1, arrows.y)] {
            if target != 0. {
                self.stick[axis] = approach(self.stick[axis], target, 2.2 * dt);
            }
        }
        let filtered = Vec2::new(
            (self.stick.x.abs() - 0.4 * self.stick.y.abs()).max(0.) * self.stick.x.signum(),
            (self.stick.y.abs() - 0.4 * self.stick.x.abs()).max(0.) * self.stick.y.signum(),
        );
        self.stick *= (-8.5 * dt).exp();
        filtered
    }
    pub fn step(
        &mut self,
        spec: &VehicleSpec,
        pose: &Transform,
        velocity: &Velocity,
        input: Controls,
        contact: AirContact,
        wheels: &mut RayCastVehicleController,
        driven: bool,
        exploded: bool,
        dt: f32,
    ) -> FlightStep {
        self.grace = (self.grace - dt).max(0.);
        let running = input.engine && !exploded;
        let want = if running { 1. } else { 0. };
        let spool_up = if spec.heli && self.ignition_length > 0.1 {
            self.ignition_length * 0.74
        } else {
            3.2
        };
        self.spool = approach(
            self.spool,
            want,
            dt / if want > self.spool { spool_up } else { 5.5 },
        );
        self.ignition_remaining = (self.ignition_remaining - dt).max(0.);
        if running && self.spool < 0.05 && !self.ignition_fired {
            self.ignition_fired = true;
            self.ignition_remaining = self.ignition_length * 0.74;
        } else if !running && self.spool < 0.01 {
            self.ignition_fired = false;
        }
        if driven && !exploded {
            if spec.heli {
                let idle = if spec.heli_thrust > 0.01 {
                    (9.8 / (spec.heli_thrust * self.ge_applied.max(0.01))).clamp(0., 1.) * 0.92
                } else {
                    0.
                };
                let target = if input.throttle > 0.05 {
                    1.
                } else if input.throttle < -0.05 {
                    0.
                } else {
                    idle
                };
                self.collective = approach(
                    self.collective,
                    target,
                    dt * if input.throttle.abs() > 0.05 {
                        0.55
                    } else {
                        0.40
                    },
                );
            } else {
                let target = if input.throttle > 0.05 {
                    1.
                } else if input.throttle < -0.05 {
                    0.
                } else {
                    self.collective
                };
                self.collective = approach(
                    self.collective,
                    target,
                    dt * if input.throttle > 0.05 {
                        0.6
                    } else if input.throttle < -0.05 {
                        0.35
                    } else {
                        0.
                    },
                );
            }
        } else {
            self.collective = 0.;
        }
        let phase_scale = if exploded { 0. } else { self.spool };
        self.main_phase = (self.main_phase
            + dt * phase_scale * (0.30 + 0.70 * self.main_health) * 46.)
            % std::f32::consts::TAU;
        self.tail_phase = (self.tail_phase
            + dt * phase_scale * (0.30 + 0.70 * self.tail_health) * 120.)
            % std::f32::consts::TAU;
        self.prop_phase = (self.prop_phase + dt * phase_scale * 150.) % std::f32::consts::TAU;
        if spec.heli {
            self.helicopter(spec, pose, velocity, input, contact, exploded)
        } else {
            self.plane(spec, pose, velocity, input, contact, wheels, exploded, dt)
        }
    }
    fn helicopter(
        &mut self,
        s: &VehicleSpec,
        pose: &Transform,
        velocity: &Velocity,
        input: Controls,
        contact: AirContact,
        exploded: bool,
    ) -> FlightStep {
        let mut force = ExternalForce::default();
        let mass = s.mass();
        let vel = velocity.linear;
        let up = pose.rotation * Vec3::Y;
        let right = pose.rotation * Vec3::X;
        let back = pose.rotation * Vec3::Z;
        let damping = Damping {
            linear_damping: 0.,
            angular_damping: 0.35,
        };
        if exploded {
            force.force = -vel * 0.45 * mass;
            return FlightStep {
                force,
                damping,
                park: false,
            };
        }
        let spool = self.spool.powi(2);
        let flat = Vec3::new(vel.x, 0., vel.z);
        let speed = flat.length();
        let etl = 1. + 0.05 * ((speed - 4.) / 7.).clamp(0., 1.);
        let lift_mul = (etl * contact.ground_effect).min(self.lift_cap);
        self.ge_applied = lift_mul / etl;
        let main_eff = (self.main_health / 0.70).clamp(0., 1.);
        let mut lift = s.heli_thrust * spool * self.collective * (0.20 + 0.80 * main_eff);
        if self.ignition_remaining > 0. {
            lift = 0.;
        }
        lift *= lift_mul;
        if self.tail_health <= 0. && self.main_health > 0. {
            lift = lift.min(9.8 * 0.95);
        }
        lift *= 1. - 0.55 * (1. - up.y.clamp(0., 1.));
        self.lift_acceleration = up * lift;
        force.force += self.lift_acceleration * mass;
        let mut heave = 0.45;
        if vel.y < 0. {
            heave *= up.y.powi(2);
            if s.heli_fall_max > 0.01 {
                heave = heave.max((9.8 + (-lift * up.y).max(0.)) / (s.heli_fall_max * 0.9));
            }
            force.force += Vec3::new(up.x, 0., up.z) * (heave * -vel.y * mass);
        }
        force.force -= Vec3::Y * heave * vel.y * mass;
        if speed > 0.01 {
            let forward = Vec3::new(-back.x, 0., -back.z).normalize_or_zero();
            let along = forward * flat.dot(forward);
            force.force -= (along + (flat - along) * 2.5) * self.drag_forward * speed * mass;
            if s.speed_max > 0. && speed > s.speed_max * 1.15 {
                force.force -= flat / speed * (speed - s.speed_max * 1.15) * mass * 3.;
            }
        }
        let tail_eff = 0.04 + 0.96 * (self.tail_health / 0.70).clamp(0., 1.).powi(2);
        let agility = if s.sling_hook { 1.30 } else { 1. };
        let mut command = right * (input.pitch * 2.30 * s.heli_pitch_torque * agility / 2.6)
            + back * (-input.roll * 2.90 * s.heli_roll_torque * agility / 3.0 * tail_eff)
            + up * (-input.yaw * 2.10 * s.heli_yaw_torque * agility / 2.2 * tail_eff);
        if self.tail_health <= 0. {
            command += up * 1.35 * spool * self.collective.max(0.15);
        }
        let manual = input.pitch.abs().max(input.roll.abs());
        if s.heli_level > 0. && manual < 0.95 {
            let axis = up.cross(Vec3::Y);
            if axis.length_squared() > 1e-6 {
                command += axis.normalize()
                    * up.angle_between(Vec3::Y)
                    * s.heli_level
                    * spool
                    * (1. - manual);
            }
        }
        force.torque = command * mass * 0.9;
        let park = contact.grounded
            && self.collective < 0.02
            && vel.length_squared() < 0.05
            && velocity.angular.length_squared() < 0.05
            && self.grace <= 0.;
        FlightStep {
            force,
            damping,
            park,
        }
    }
    fn plane(
        &mut self,
        s: &VehicleSpec,
        pose: &Transform,
        velocity: &Velocity,
        input: Controls,
        contact: AirContact,
        wheels: &mut RayCastVehicleController,
        exploded: bool,
        dt: f32,
    ) -> FlightStep {
        let mut force = ExternalForce::default();
        let up = pose.rotation * Vec3::Y;
        let right = pose.rotation * Vec3::X;
        let back = pose.rotation * Vec3::Z;
        let forward = -back;
        let mass = s.mass();
        let vel = velocity.linear;
        let airspeed = vel.dot(forward);
        let target = s.plane_target_speed.max(1.);
        let on_ground = (contact.grounded || input.ground_mode) && !contact.afloat;
        // Preserve Godot's actual default COMBINE damping: .05+.1 linear, and .1 angular in flight.
        let damping = Damping {
            linear_damping: 0.15,
            angular_damping: if on_ground { 12.1 } else { 0.1 },
        };
        self.gear_deploy = approach(
            self.gear_deploy,
            if self.gear_down { 1. } else { 0. },
            dt / 1.5,
        );
        for (i, wheel) in wheels.wheels.iter_mut().enumerate() {
            wheel.engine_force = 0.;
            wheel.brake = if on_ground && self.collective < 0.08 && input.throttle >= -0.05 {
                s.brake * mass / 900. * 13.
            } else {
                0.
            };
            wheel.steering = if s.wheels()[i].3 && on_ground {
                (-input.yaw * s.steer_max).to_radians()
            } else {
                0.
            };
            if s.retract_gear {
                let (support, grip) = self.wheel_support[i];
                wheel.tuning.max_suspension_force =
                    if self.gear_deploy > 0.5 { support } else { 0. };
                wheel.tuning.friction_slip = if self.gear_deploy > 0.5 { grip } else { 0. };
            }
        }
        if exploded {
            return FlightStep {
                force,
                damping,
                park: false,
            };
        }
        force.force += forward * s.plane_thrust * self.collective * self.spool * mass;
        let aoa = if airspeed > 0.5 {
            (-vel.dot(up)).atan2(airspeed).to_degrees()
        } else {
            0.
        };
        self.lift_acceleration = Vec3::ZERO;
        if !input.ground_mode {
            let bank = if up.y > 0.2 {
                1. + (1. / up.y - 1.) * 0.75
            } else {
                1.
            };
            self.lift_acceleration = up
                * (vel.length() / target).min(2.5).powi(2)
                * lift_coefficient(aoa)
                * s.plane_lift
                * bank;
            force.force += self.lift_acceleration * mass;
        }
        force.force -= vel * 0.03 * mass;
        let flat = Vec3::new(vel.x, 0., vel.z);
        if s.speed_max > 0. && flat.length() > s.speed_max {
            force.force -= flat.normalize() * (flat.length() - s.speed_max) * mass * 2.;
        }
        if on_ground {
            if input.throttle < -0.05 && self.collective < 0.08 && vel.dot(back) < 6. {
                force.force += back * s.plane_thrust * 0.35 * mass;
            }
        } else {
            let auth = (airspeed / (target * 0.35)).clamp(0., 1.);
            let fade = if s.plane_steer_fade > 0. {
                s.plane_steer_fade
            } else {
                1.
            };
            let mut command = right * input.pitch * s.plane_pitch_torque * auth
                + back * -input.roll * s.plane_roll_torque * auth
                + up * -input.yaw * s.plane_yaw_torque * auth * fade;
            if vel.length_squared() > 4. {
                command +=
                    forward.cross(vel.normalize()) * (airspeed / target).clamp(0., 1.5) * 1.6;
            }
            force.torque += command * mass * 0.9;
        }
        if contact.grounded
            && !contact.afloat
            && input.pitch > 0.02
            && airspeed > target * 0.45
            && self.collective > 0.4
        {
            force.torque += right * input.pitch * 2.6 * airspeed * mass;
        }
        let park = (contact.grounded || contact.afloat)
            && self.collective < 0.1
            && input.throttle >= -0.05
            && vel.length_squared() < 4.
            && self.grace <= 0.;
        FlightStep {
            force,
            damping,
            park,
        }
    }
}
