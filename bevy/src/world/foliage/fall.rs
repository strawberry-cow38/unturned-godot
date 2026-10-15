//! TreeTrunk's original torque integration, base-centre pivot and two springs.
use super::reaction::Reaction;
use bevy::prelude::*;
const STEP: f32 = 1. / 60.;
const ACCEL: f32 = 0.669;
const END: f32 = 90.;
const KICK: f32 = 1.5;

pub(super) fn predict() -> (f32, f32) {
    let (mut angle, mut velocity, mut time) = (KICK, 0., 0.);
    for _ in 0..4000 {
        if angle >= END {
            break;
        }
        velocity += ACCEL * angle.to_radians().sin() * STEP;
        angle += velocity.to_degrees() * STEP;
        time += STEP;
    }
    (time, velocity)
}

#[derive(Component)]
pub(super) struct Fall {
    pub base: Transform,
    pub direction: Vec3,
    pub age: f32,
    pub lifetime: f32,
    pub leaf: Option<Entity>,
    pub trunk: Option<Entity>,
    pub sound_played: bool,
    length: f32,
    radius: f32,
    angle: f32,
    velocity: f32,
    settling: Option<f32>,
    landed: bool,
    last_angle: f32,
    leaf_offset: Vec3,
    leaf_velocity: Vec3,
    trunk_offset: Vec3,
    trunk_velocity: Vec3,
    leaf_enabled: bool,
}
impl Fall {
    pub fn new(base: Transform, direction: Vec3, length: f32, radius: f32, lifetime: f32) -> Self {
        let horizontal = Vec3::new(direction.x, 0., direction.z);
        Self {
            base,
            direction: if horizontal.length_squared() > 0.01 {
                horizontal.normalize()
            } else {
                Vec3::NEG_Z
            },
            age: 0.,
            lifetime,
            leaf: None,
            trunk: None,
            sound_played: false,
            length,
            radius,
            angle: KICK,
            velocity: 0.,
            settling: None,
            landed: false,
            last_angle: 0.,
            leaf_offset: Vec3::ZERO,
            leaf_velocity: Vec3::ZERO,
            trunk_offset: Vec3::ZERO,
            trunk_velocity: Vec3::ZERO,
            leaf_enabled: std::env::var("UG_TREELEAF").as_deref() != Ok("0"),
        }
    }
    pub fn step(&mut self, dt: f32) -> (Transform, Reaction, Reaction) {
        self.age += dt;
        if self.leaf_enabled && dt > 0.0001 {
            let w = std::f32::consts::TAU * 3.;
            self.trunk_velocity +=
                (-w * w * self.trunk_offset - 2. * 0.14 * w * self.trunk_velocity) * dt;
            self.trunk_offset += self.trunk_velocity * dt;
        }
        let angle = if !self.landed {
            let mut remaining = dt;
            while remaining > 0. {
                let h = remaining.min(STEP);
                remaining -= h;
                self.velocity += ACCEL * self.angle.to_radians().sin() * h;
                self.angle += self.velocity.to_degrees() * h;
                if self.angle >= END {
                    self.angle = END;
                    break;
                }
            }
            if self.angle >= END {
                self.landed = true;
                self.settling = Some(0.);
                self.trunk_velocity = Vec3::Y
                    * (0.016 * std::f32::consts::TAU * 3. * (self.velocity / 1.094).min(1.));
            }
            self.angle
        } else if let Some(t) = self.settling.as_mut() {
            *t += dt;
            let k = *t / 0.40;
            if k >= 1. {
                self.settling = None;
                END
            } else {
                END - 3.5 * (-4. * k).exp() * (std::f32::consts::PI * 2. * k).sin().abs()
            }
        } else {
            END
        };
        if self.leaf_enabled {
            if dt > 0.0001 {
                let radians = angle.to_radians();
                let omega = (angle - self.last_angle).to_radians() / dt;
                let target = -(self.direction * radians.cos() - Vec3::Y * radians.sin())
                    * (omega * 0.75 * self.length * 0.0014);
                let w = std::f32::consts::TAU * 2.5;
                self.leaf_velocity +=
                    (w * w * (target - self.leaf_offset) - 2. * 0.12 * w * self.leaf_velocity) * dt;
                self.leaf_offset =
                    (self.leaf_offset + self.leaf_velocity * dt).clamp_length_max(0.03);
            }
            self.last_angle = angle;
        }
        let rotation = Quat::from_axis_angle(
            Vec3::Y.cross(self.direction).normalize(),
            angle.to_radians(),
        );
        let sn = angle.to_radians().sin();
        let lift = (0.5 * self.base.scale.y.abs().max(0.01)
            + self.radius * self.base.scale.x.abs().max(0.01))
            * sn
            * sn
            * sn;
        let pose = Transform {
            translation: self.base.translation + Vec3::Y * lift,
            rotation: rotation * self.base.rotation,
            scale: self.base.scale,
        };
        (
            pose,
            Reaction {
                gust: self.leaf_offset,
                shake: (self.leaf_velocity.length() * 3.).min(1.),
                ..default()
            },
            Reaction {
                sway: 0.,
                gust: self.trunk_offset,
                base: self.trunk_offset * 5.,
                ..default()
            },
        )
    }
}
