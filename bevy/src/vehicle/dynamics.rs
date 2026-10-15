//! Source Vehicle.cs drivetrain and buoyancy, independent of render entities.
use super::data::VehicleSpec;
use bevy::prelude::*;
use bevy_rapier3d::prelude::*;
use std::f32::consts::{PI, TAU};

#[derive(Clone, Copy, Default, Debug)]
pub struct Controls {
    pub throttle: f32,
    pub steer: f32,
    pub handbrake: bool,
    pub engine: bool,
    pub collective: f32,
    pub pitch: f32,
    pub roll: f32,
    pub yaw: f32,
    pub ground_mode: bool,
}
#[derive(Clone, Debug)]
pub struct Drivetrain {
    pub rpm: f32,
    pub gear: usize,
    pub steering: f32,
    pub wheel_slip: f32,
    pub surface_grip: f32,
    pub surface_roll: f32,
    pub bush_drag: f32,
    gears: Vec<f32>,
    reverse: f32,
    torque: f32,
    radius: f32,
    drag: f32,
    roll: f32,
    shift_cooldown: f32,
    clutch: f32,
    rotations: Vec<f32>,
    roll_grip: f32,
}
fn torque_fraction(n: f32) -> f32 {
    let d = n.clamp(0., 1.) - 0.6;
    (1. - if d < 0. { 1.25 } else { 1.875 } * d * d).max(0.35)
}
fn rev_limit(rpm: f32) -> f32 {
    if rpm <= 5400. {
        1.
    } else {
        (1. - (rpm - 5400.) / 600.).max(0.)
    }
}
pub(super) fn approach(current: f32, target: f32, step: f32) -> f32 {
    current + (target - current).clamp(-step, step)
}
impl Drivetrain {
    pub fn new(spec: &VehicleSpec) -> Self {
        let mut state = Self {
            rpm: 1000.,
            gear: 0,
            steering: 0.,
            wheel_slip: 0.,
            surface_grip: 1.,
            surface_roll: 1.,
            bush_drag: 1.,
            gears: vec![],
            reverse: 0.,
            torque: 0.,
            radius: spec.wheel_radius,
            drag: 0.,
            roll: 0.,
            shift_cooldown: 0.,
            clutch: 0.,
            rotations: vec![0.; spec.wheels().len()],
            roll_grip: 1.,
        };
        if !spec.drivetrain() {
            return state;
        }
        if let Some(radii) = &spec.wheel_radii {
            if !radii.is_empty() {
                state.radius = radii.iter().sum::<f32>() / radii.len() as f32;
            }
        }
        let speed = spec.speed_max();
        let wheel_rpm = speed / (TAU * state.radius) * 60.;
        let top_ratio = 5400. / wheel_rpm;
        let spread = ((3. + spec.mass() / 4000.) * 1.366).clamp(3. * 1.366, 18.);
        let count = ((spread.ln() / 1.35_f32.ln()).ceil() as usize + 1).clamp(2, 11);
        let first = top_ratio * spread;
        state.gears = (0..count)
            .map(|i| first * (top_ratio / first).powf(i as f32 / (count - 1) as f32))
            .collect();
        state.reverse = first * 0.9;
        state.torque =
            spec.engine * (spec.mass() / 900.) * spec.wheels().len() as f32 * 4. * state.radius
                / first;
        let top_force = state.torque
            * torque_fraction((wheel_rpm * top_ratio - 1000.) / 5000.)
            * rev_limit(wheel_rpm * top_ratio)
            * top_ratio
            / state.radius;
        state.roll = 0.015 * spec.mass() * 9.8;
        state.drag = (top_force - state.roll).max(0.) / (speed * speed);
        state
    }
    pub fn rpm_fraction(&self) -> f32 {
        ((self.rpm - 1000.) / 5000.).clamp(0., 1.)
    }
    fn force(&self, spec: &VehicleSpec, throttle: f32) -> f32 {
        let count = spec
            .wheels()
            .iter()
            .enumerate()
            .filter(|(i, _)| spec.drives_wheel(*i))
            .count();
        if self.torque <= 0. || count == 0 || self.radius <= 0. {
            return throttle * spec.engine * spec.mass() / 900.;
        }
        let ratio = if throttle < 0. {
            self.reverse
        } else {
            self.gears[self.gear]
        };
        throttle * self.torque * torque_fraction(self.rpm_fraction()) * rev_limit(self.rpm) * ratio
            / self.radius
            / count as f32
    }
    pub fn step(
        &mut self,
        spec: &VehicleSpec,
        wheels: &mut RayCastVehicleController,
        pose: &Transform,
        velocity: &Velocity,
        input: Controls,
        dt: f32,
    ) -> (ExternalForce, ExternalImpulse) {
        let forward = pose.rotation * Vec3::NEG_Z;
        let signed = velocity.linear.dot(forward);
        let speed = velocity.linear.length();
        let throttle = if input.engine { input.throttle } else { 0. };
        let foot = (throttle < 0. && signed > 0.6) || (throttle > 0. && signed < -0.6);
        let neutral = input.handbrake && speed < 0.5;
        let mut force = if foot || neutral {
            0.
        } else {
            self.force(spec, throttle)
        };
        let max = spec.speed_max();
        let reverse_max = spec.speed_min.abs()
            * if spec.drivetrain() {
                super::data::speed_buff()
            } else {
                1.
            };
        if (throttle > 0. && speed >= max * 1.15) || (throttle < 0. && speed >= reverse_max) {
            force = 0.;
        }
        if self.clutch > 0. {
            force *= 1. - (self.clutch / 0.20 * PI).clamp(0., PI).sin();
        }
        let contacts = wheels
            .wheels
            .iter()
            .filter(|w| w.state.is_in_contact)
            .count();
        self.wheel_slip = 0.;
        if contacts > 0 && self.torque > 0. {
            let grip =
                (if spec.tracked { 1.6 } else { 1. }) * self.surface_grip * spec.mass() * 9.8
                    / contacts as f32;
            if force.abs() > grip {
                self.wheel_slip = (force.abs() - grip) / force.abs();
                force = force.signum() * grip;
            }
        }
        let t = if spec.speed_max > 0. {
            (speed / spec.speed_max).clamp(0., 1.)
        } else {
            0.
        };
        let mut angle = spec.steer_max + (spec.steer_min - spec.steer_max) * t;
        let (com, ssf) = spec.center_of_mass();
        let base = spec
            .wheels()
            .iter()
            .map(|w| w.2)
            .fold(f32::NEG_INFINITY, f32::max)
            - spec
                .wheels()
                .iter()
                .map(|w| w.2)
                .fold(f32::INFINITY, f32::min);
        if ssf > 0.01 && speed > 1. && base > 0.1 {
            let cap = (ssf * 0.82 * 9.8 * base / (speed * speed))
                .atan()
                .to_degrees();
            angle = angle.min(cap.max(2.));
        }
        self.steering = approach(
            self.steering,
            -input.steer * angle,
            spec.steer_max * 2. * (1. - 0.65 * t) * dt,
        );
        let brake = spec.brake * spec.mass() / 900.;
        let coasting = throttle.abs() < 0.05 && !foot;
        let foot_brake = if foot {
            brake * 1.5
        } else if coasting {
            brake * 1.5 * 0.12 * self.rpm_fraction()
        } else {
            0.
        };
        let lateral_g = velocity.angular.y.abs() * signed.abs() / 9.8;
        let limit = ssf * 0.62;
        let wanted = if ssf > 0.01 && lateral_g > limit {
            (limit / lateral_g.max(0.001)).clamp(0.35, 1.)
        } else {
            1.
        };
        self.roll_grip += (wanted - self.roll_grip)
            * (1. - (-if wanted < self.roll_grip { 12. } else { 3. } * dt).exp());
        for (i, wheel) in wheels.wheels.iter_mut().enumerate() {
            wheel.engine_force = if spec.drives_wheel(i) { force } else { 0. };
            wheel.steering = if spec.wheels()[i].3 && !spec.tracked {
                self.steering.to_radians()
            } else {
                0.
            };
            wheel.brake = if input.handbrake && spec.wheels()[i].2 > 0. {
                brake * 13.
            } else {
                foot_brake
            };
            if !spec.tracked && !spec.plane && !spec.heli {
                let grip = if spec.wheels()[i].3 {
                    self.roll_grip + (1. - self.roll_grip) * 0.55
                } else {
                    self.roll_grip
                };
                wheel.tuning.friction_slip =
                    (if spec.kingpin == [0.; 3] { 6. } else { 1.5 }) * self.surface_grip * grip;
            }
            if spec.tracked {
                let side = if spec.wheels()[i].0 < 0. { 1. } else { -1. };
                wheel.engine_force = if foot || neutral {
                    0.
                } else {
                    self.force(spec, (throttle + input.steer * 0.3 * side).clamp(-1., 1.))
                };
                wheel.brake = if input.handbrake {
                    brake * 13.
                } else {
                    foot_brake
                };
            }
        }
        let h = Vec3::new(velocity.linear.x, 0., velocity.linear.z);
        let hs = h.length();
        let mut result = ExternalForce::default();
        // Original anti-roll bar is a PURE couple, even with one wheel airborne.
        // Applying two individually contact-gated forces launches the inside corner.
        if !spec.heli && !spec.plane && std::env::var("UG_ANTIROLL").as_deref() != Ok("0") {
            let rate = std::env::var("UG_ANTIROLLK")
                .ok()
                .and_then(|s| s.parse::<f32>().ok())
                .filter(|n| *n >= 0.)
                .unwrap_or(0.55);
            for left in &wheels.wheels {
                if left.chassis_connection_point.x >= 0. {
                    continue;
                }
                if let Some(right) = wheels.wheels.iter().find(|right| {
                    right.chassis_connection_point.x > 0.
                        && (right.chassis_connection_point.z - left.chassis_connection_point.z)
                            .abs()
                            <= 0.35
                }) {
                    if !left.state.is_in_contact && !right.state.is_in_contact {
                        continue;
                    }
                    let extension = |w: &VehicleWheel| {
                        if w.state.is_in_contact {
                            (w.state.suspension_length / w.suspension_rest_length.max(0.01))
                                .clamp(0., 1.)
                        } else {
                            1.
                        }
                    };
                    let force =
                        (extension(left) - extension(right)) * rate * spec.mass() * 9.8 * 0.5;
                    result.torque += pose.rotation
                        * Vec3::Z
                        * (force
                            * (right.chassis_connection_point.x - left.chassis_connection_point.x)
                                .abs());
                }
            }
        }
        if hs > 0.15 && self.drag > 0. {
            result.force -= h / hs
                * (self.drag * (1. + 0.37 * (self.surface_roll - 1.)) * hs * hs
                    + self.roll * self.surface_roll * self.bush_drag);
        }
        if spec.tracked {
            let target =
                -input.steer * 0.6 * (1. - 0.7 * (signed.abs() / max.max(0.1)).clamp(0., 1.));
            let inertia = spec.mass() / 12. * (spec.box_size[0].powi(2) + spec.box_size[2].powi(2));
            let gain = (60000. * spec.mass() / 900.).min(1.2 * inertia / dt.max(0.0001));
            result.torque.y += (target - velocity.angular.y) * gain;
        } else if ssf > 0.01 && lateral_g > limit {
            let over = lateral_g / limit.max(0.001) - 1.;
            let tipped = (pose.rotation * Vec3::Y).dot(Vec3::Y);
            let assist = over.clamp(0., 2.) * (1. - tipped).clamp(0., 1.);
            result.torque -= forward * (velocity.angular.dot(forward) * 2.6 * spec.mass() * assist);
            result.torque += (pose.rotation * Vec3::Y).cross(Vec3::Y) * 9. * spec.mass() * assist;
        }
        let mut impulse = ExternalImpulse::default();
        if !self.gears.is_empty() {
            let mut rpms = Vec::new();
            for (i, w) in wheels.wheels.iter().enumerate() {
                let rpm = ((w.state.rotation - self.rotations[i]) / dt / TAU * 60.).abs();
                self.rotations[i] = w.state.rotation;
                if spec.drives_wheel(i) {
                    rpms.push((rpm, w.state.is_in_contact));
                }
            }
            let grounded = rpms.iter().filter(|p| p.1).count();
            let n = if grounded > 0 { grounded } else { rpms.len() };
            let avg = if n > 0 {
                rpms.iter()
                    .filter(|p| grounded == 0 || p.1)
                    .map(|p| p.0)
                    .sum::<f32>()
                    / n as f32
            } else {
                0.
            };
            let driven = avg * self.gears[self.gear];
            let engage = (driven / 2600.).clamp(0., 1.);
            let launch = 1000. + throttle.abs() * 1600.;
            let target = (launch + (driven - launch) * engage).clamp(1000., 6000.);
            self.rpm += (target - self.rpm) * (8. * dt).min(1.);
            self.shift_cooldown = (self.shift_cooldown - dt).max(0.);
            self.clutch = (self.clutch - dt).max(0.);
            if signed.abs() < 1. {
                self.gear = 0;
            } else if self.shift_cooldown <= 0. {
                let down = if self.gear > 0 {
                    5400. * self.gears[self.gear] / self.gears[self.gear - 1] * 0.85
                } else {
                    0.
                };
                if self.rpm >= 5400. && self.gear + 1 < self.gears.len() {
                    self.gear += 1;
                    self.shift_cooldown = 0.5;
                    self.clutch = 0.2;
                    impulse.impulse = Vec3::Y * spec.mass() * 0.22;
                    impulse.torque_impulse = pose.rotation * Vec3::X * spec.mass() * 0.5;
                } else if self.gear > 0 && self.rpm <= down {
                    self.gear -= 1;
                    self.shift_cooldown = 0.5;
                    self.clutch = 0.2;
                }
            }
        }
        let _ = com;
        (result, impulse)
    }
}

#[derive(Clone, Debug)]
pub struct Buoyancy {
    pub points: Vec<Vec3>,
    half_height: f32,
}
impl Buoyancy {
    pub fn new(spec: &VehicleSpec) -> Self {
        let slices = spec.buoy_slices.max(2) as usize;
        let voxel = Vec3::from_array(spec.box_size) / slices as f32;
        let minimum = Vec3::from_array(spec.box_center) - Vec3::from_array(spec.box_size) * 0.5;
        let mut points = Vec::new();
        for x in 0..slices {
            for y in 0..slices {
                for z in 0..slices {
                    points.push(
                        minimum
                            + voxel * Vec3::new(x as f32 + 0.5, y as f32 + 0.5, z as f32 + 0.5)
                            + Vec3::Y * spec.buoy_lift,
                    );
                }
            }
        }
        Self {
            points,
            half_height: voxel.min_element() * 0.5,
        }
    }
    pub fn step(
        &self,
        spec: &VehicleSpec,
        pose: &Transform,
        velocity: &mut Velocity,
        input: Controls,
        sea: f32,
        dt: f32,
    ) -> (ExternalForce, bool) {
        let mut result = ExternalForce::default();
        let com = pose.transform_point(spec.center_of_mass().0);
        let reserve = if spec.buoy_reserve > 0. {
            spec.buoy_reserve
        } else {
            1.
        };
        let arch = Vec3::Y * (2. * spec.mass() * 9.8 * reserve / self.points.len() as f32);
        let damp = (if spec.plane { 0.035 } else { 0.1 }) * 8. / self.points.len() as f32 * reserve;
        let damp_y = if spec.buoy_damp > 0. {
            spec.buoy_damp
        } else {
            1.
        };
        let pitch_free = if spec.plane {
            1. - 0.96 * ((velocity.linear.xz().length() - 4.) / 9.).clamp(0., 1.)
        } else {
            1.
        };
        let mut submerged = 0;
        for point in &self.points {
            let world = pose.transform_point(*point);
            if world.y >= sea {
                continue;
            }
            submerged += 1;
            let pv = velocity.linear + velocity.angular.cross(world - com);
            let mut damping = -pv * damp * spec.mass();
            damping.y += -pv.y * damp * spec.mass() * (damp_y - 1.);
            let factor = ((sea - world.y) / (2. * self.half_height) + 0.5)
                .clamp(0., 1.)
                .sqrt();
            let f = damping + arch * factor;
            let mut arm = world - pose.translation;
            let forward = pose.rotation * Vec3::Z;
            arm -= forward * arm.dot(forward) * (1. - pitch_free);
            result.force += f;
            result.torque += arm.cross(f);
        }
        if submerged == 0 {
            return (result, false);
        }
        if spec.steady_hull {
            let sailing = (input.steer.abs() + input.throttle.abs()).clamp(0., 1.);
            velocity.linear.y *= 1. - (10. * dt).min(1.);
            let damping = (25. + (3. - 25.) * sailing) * dt;
            velocity.angular.x *= 1. - damping.min(1.);
            velocity.angular.z *= 1. - damping.min(1.);
        }
        let forward = pose.rotation * Vec3::NEG_Z;
        if spec.plane {
            result.torque -= forward * velocity.angular.dot(forward) * 3.2 * spec.mass();
        } else {
            result.force +=
                forward * (if input.engine { input.throttle } else { 0. }) * 15. * spec.mass();
            let rudder = (velocity.linear.dot(forward).abs() * 0.25 + 0.25).clamp(0.25, 1.);
            let turn = if spec.turn_scale > 0. {
                spec.turn_scale
            } else {
                1.
            };
            result.torque.y += -input.steer * 2.2 * spec.mass() * rudder * turn;
            result.force -= Vec3::new(velocity.linear.x, 0., velocity.linear.z) * 0.5 * spec.mass();
        }
        (result, true)
    }
}
