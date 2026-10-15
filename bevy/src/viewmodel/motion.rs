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
        magnifying: bool,
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
        let sway = if magnifying { 0. } else { self.sway };
        let amount = tilt * (1. - sway * 0.5);
        let wiggle = (tilt * self.elapsed * 0.25).sin() * tilt;
        self.tilt.update(
            if moving {
                Vec3::new(
                    movement.y * amount * sway + wiggle * sway,
                    movement.x * amount + wiggle * sway,
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
/// The original Viewmodel oscillator. Holding breath changes its clock, never
/// its amplitude; centering on engagement would grant a free re-aim.
#[derive(Default)]
pub struct ScopeSway {
    elapsed: f32,
    pub degrees: Vec2,
}
impl ScopeSway {
    pub fn update(
        &mut self,
        dt: f32,
        aiming: bool,
        zoom: f32,
        stance: &str,
        scale: f32,
        rate: f32,
    ) -> Vec2 {
        let target = if aiming && zoom > 1. {
            let stance_scale = match stance {
                "crouch" => 0.85,
                "prone" => 0.7,
                _ => 1.,
            };
            // Source SteadyAccuracy is never assigned in this branch; its
            // passive multiplier is therefore1, rather than an invented skill.
            self.elapsed += dt * rate.clamp(0., 1.);
            Vec2::new((0.75 * self.elapsed).sin(), self.elapsed.sin())
                * (1. - 1. / zoom)
                * 1.25
                * scale
                * stance_scale
        } else {
            Vec2::ZERO
        };
        self.degrees = self.degrees.lerp(target, (dt * 4.).clamp(0., 1.));
        self.degrees
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn scope_breath_slows_phase_without_recentering_or_reducing_amplitude() {
        let mut sway = ScopeSway::default();
        for _ in 0..100 {
            sway.update(0.02, true, 4., "stand", 1., 1.);
        }
        let phase = sway.elapsed;
        let before = sway.degrees;
        let held = sway.update(0.02, true, 4., "stand", 1., 0.18);
        assert!((sway.elapsed - phase - 0.0036).abs() < 1e-6);
        assert!(held.length() > before.length() * 0.98);
        let target = Vec2::new((sway.elapsed * 0.75).sin(), sway.elapsed.sin()) * 0.9375;
        assert!((held - before.lerp(target, 0.08)).length() < 1e-6);
        let paused = sway.update(0., true, 4., "stand", 1., 0.18);
        assert_eq!(paused, held);
        for _ in 0..150 {
            sway.update(0.02, false, 4., "stand", 1., 1.);
        }
        assert!(sway.degrees.length() < 1e-5);
    }
    #[test]
    fn source_scope_zoom_stance_and_per_gun_scale_multiply_one_oscillator() {
        let sample = |zoom, stance, scale| {
            let mut sway = ScopeSway::default();
            for _ in 0..100 {
                sway.update(0.02, true, zoom, stance, scale, 1.);
            }
            sway.degrees
        };
        let normal = sample(4., "stand", 1.);
        assert_eq!(sample(1., "stand", 1.), Vec2::ZERO);
        assert!((sample(4., "crouch", 1.) - normal * 0.85).length() < 1e-6);
        assert!((sample(4., "prone", 0.3) - normal * 0.21).length() < 1e-6);
    }
    #[test]
    fn magnifying_scope_zeroes_forward_tilt_but_preserves_source_strafe_quirk_and_bob() {
        let mut forward = Motion::default();
        let mut strafe = Motion::default();
        let mut forward_pose = (Vec3::ZERO, Vec3::ZERO);
        let mut strafe_pose = forward_pose;
        for _ in 0..150 {
            forward_pose = forward.update(0.02, "stand", Vec2::Y, 1., Vec2::ZERO, true);
            strafe_pose = strafe.update(0.02, "stand", Vec2::X, 1., Vec2::ZERO, true);
        }
        assert_eq!(forward_pose.1, Vec3::ZERO);
        assert!(forward_pose.0.length() > 0.001);
        assert!((strafe_pose.1.z - 3.).abs() < 0.001);
    }
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
