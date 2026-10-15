//! WindField.cs, using the same upstream FastNoiseLite algorithm as Godot.
use bevy::prelude::*;
use fastnoise_lite::{FastNoiseLite, FractalType, NoiseType};

pub struct WindField {
    noise: FastNoiseLite,
    forced: Option<f32>,
    phase: f32,
}
impl Default for WindField {
    fn default() -> Self {
        let mut noise = FastNoiseLite::with_seed(1337);
        noise.set_noise_type(Some(NoiseType::OpenSimplex2S));
        noise.set_fractal_type(Some(FractalType::FBm));
        noise.set_frequency(Some(super::env("UG_WINDFREQ").unwrap_or(0.0025)));
        noise.set_fractal_octaves(Some(super::env("UG_WINDOCT").unwrap_or(2.) as i32));
        Self {
            noise,
            forced: super::env("UG_WIND"),
            phase: 0.,
        }
    }
}
impl WindField {
    pub fn sample(&self, at: Vec3, elapsed: f32, weather: f32) -> (Vec2, f32) {
        let n = self
            .noise
            .get_noise_2d(at.x + elapsed * 2.5, at.z + elapsed * 1.2);
        let ambient = (0.5 + 0.65 * n).clamp(0., 0.8);
        let strength = self
            .forced
            .unwrap_or(ambient + weather.clamp(0., 1.) * (1. - ambient));
        let region = self
            .noise
            .get_noise_2d(at.x * 0.5 + 5000., at.z * 0.5 + 5000.);
        let angle = 1.2_f32.atan2(2.5) + region * 0.8 + 0.3 * (elapsed * 0.06).sin();
        (Vec2::new(angle.cos(), angle.sin()), strength)
    }
    pub fn advance(&mut self, at: Vec3, elapsed: f32, weather: f32, dt: f32) -> Vec4 {
        let (direction, strength) = self.sample(at, elapsed, weather);
        self.phase =
            (self.phase + dt * (0.55 + 0.9 * strength)).rem_euclid(std::f32::consts::TAU * 10.);
        Vec4::new(direction.x, direction.y, strength, self.phase)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn wind_noise_matches_actual_godot_462_samples() {
        // Measured with a FastNoiseLite Resource in the original Godot runtime.
        let field = WindField::default();
        for (point, expected) in [
            (Vec2::new(5000., 5000.), 0.329330981),
            (Vec2::new(-450., 50.), -0.142558709),
            (Vec2::new(4775., 5025.), -0.329647064),
        ] {
            assert!((field.noise.get_noise_2d(point.x, point.y) - expected).abs() < 0.000001);
        }
        let (direction, strength) = field.sample(Vec3::new(-450., 0., 50.), 0., 1.);
        assert!((strength - 1.).abs() < 1e-6);
        assert!((direction.length() - 1.).abs() < 1e-6);
    }
}
