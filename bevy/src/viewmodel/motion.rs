//! Source Rk4Spring and PlayerAnimator motion, with the original viewmodel tuning.
use bevy::prelude::*;

pub struct Spring {
    pub position: Vec3,
    velocity: Vec3,
    stiffness: f32,
    damping: f32,
}
impl Spring {
    fn new(stiffness: f32, damping: f32) -> Self {
        Self {
            position: Vec3::ZERO,
            velocity: Vec3::ZERO,
            stiffness,
            damping,
        }
    }
    fn step(&mut self, target: Vec3, dt: f32) {
        let eval = |t: f32, v: Vec3, a: Vec3| {
            let velocity = self.velocity + a * t;
            (
                velocity,
                self.stiffness * (target - self.position - v * t) - self.damping * velocity,
            )
        };
        let (v0, a0) = eval(0., Vec3::ZERO, Vec3::ZERO);
        let (v1, a1) = eval(dt * 0.5, v0, a0);
        let (v2, a2) = eval(dt * 0.5, v1, a1);
        let (v3, a3) = eval(dt, v2, a2);
        self.position += (v0 + 2. * (v1 + v2) + v3) * (dt / 6.);
        self.velocity += (a0 + 2. * (a1 + a2) + a3) * (dt / 6.);
    }
    fn update(&mut self, target: Vec3, mut dt: f32) {
        while dt > 0.05 {
            self.step(target, 0.05);
            dt -= 0.05;
        }
        if dt > 0. {
            self.step(target, dt);
        }
    }
}
pub struct Motion {
    bob: Spring,
    tilt: Spring,
    pub shake: Spring,
    pub recoil: Spring,
    inertia: Spring,
    elapsed: f32,
    sway: f32,
}
impl Default for Motion {
    fn default() -> Self {
        Self {
            bob: Spring::new(900., 60.),
            tilt: Spring::new(120., 22.),
            shake: Spring::new(550., 40.),
            recoil: Spring::new(550., 40.),
            inertia: Spring::new(140., 18.),
            elapsed: 0.,
            sway: 1.,
        }
    }
}
impl Motion {
    pub fn update(
        &mut self,
        dt: f32,
        stance: &str,
        movement: Vec2,
        aim: f32,
        look: Vec2,
    ) -> (Vec3, Vec3) {
        self.elapsed += dt;
        self.sway += (1. - 0.9 * aim - self.sway) * (16. * dt).clamp(0., 1.);
        let (speed, amp, tilt) = match stance {
            "sprint" => (10., 0.075, 5.),
            "crouch" => (6., 0.025, 2.),
            "prone" => (4., 0.0125, 1.),
            "swim" => (6., 0.025, 10.),
            _ => (8., 0.05, 3.),
        };
        let moving = movement.length_squared() > 0.001;
        let s = (speed * self.elapsed).sin() * amp * self.sway;
        self.bob.update(
            if moving {
                Vec3::new(s, s.abs(), 0.)
            } else {
                Vec3::ZERO
            },
            dt,
        );
        self.shake.update(Vec3::ZERO, dt);
        self.recoil.update(Vec3::ZERO, dt);
        // Source AddLookDelta: view-space pitch/yaw inertia; positive yaw also rolls.
        self.inertia.position += Vec3::new(
            -look.x * 0.03 * self.sway,
            -look.y * 0.015 * self.sway,
            -look.y * 0.05,
        );
        self.inertia.position = self
            .inertia
            .position
            .clamp(Vec3::splat(-10.), Vec3::splat(10.));
        self.inertia.update(Vec3::ZERO, dt);
        let amount = tilt * (1. - self.sway * 0.5);
        let wiggle = (tilt * self.elapsed * 0.25).sin() * tilt;
        self.tilt.update(
            if moving {
                Vec3::new(
                    movement.y * amount * self.sway + wiggle * self.sway,
                    movement.x * amount + wiggle * self.sway,
                    0.,
                )
            } else {
                Vec3::ZERO
            },
            dt,
        );
        let position = Vec3::new(
            -self.bob.position.x + self.shake.position.x,
            self.bob.position.y - self.shake.position.y,
            -self.shake.position.z,
        );
        let rotation =
            self.inertia.position + Vec3::new(-self.tilt.position.x, 0., self.tilt.position.y);
        (position, rotation)
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn spring_large_frames_use_source_substeps_and_settle_without_drift() {
        let mut large = Spring::new(550., 40.);
        let mut split = Spring::new(550., 40.);
        large.position = Vec3::new(0.2, -0.1, 0.3);
        split.position = large.position;
        large.update(Vec3::ZERO, 0.2);
        for _ in 0..4 {
            split.update(Vec3::ZERO, 0.05);
        }
        assert!((large.position - split.position).length() < 0.00001);
        for _ in 0..100 {
            large.update(Vec3::ZERO, 0.02);
        }
        assert!(large.position.length() < 0.00001);
    }
}
