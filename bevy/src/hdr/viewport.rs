//! Viewmodel.cs OversizeFov/VpToScreen and the screen-centred TextureRect roll.
use bevy::prelude::*;

pub(crate) const SOURCE_FOV: f32 = 60_f32.to_radians();
const OVERSIZE: Vec2 = Vec2::new(1.15, 1.45);

#[derive(Resource, Clone, Copy, Debug)]
pub(crate) struct ViewmodelViewport {
    pub screen: UVec2,
    pub target: UVec2,
    scale: f32,
}
impl Default for ViewmodelViewport {
    fn default() -> Self {
        Self::new(UVec2::new(1280, 720), 1.)
    }
}
impl ViewmodelViewport {
    pub fn new(screen: UVec2, scale: f32) -> Self {
        let screen = screen.max(UVec2::ONE);
        Self {
            screen,
            // Source Mathf.RoundToInt uses nearest-even, including odd windows.
            target: UVec2::new(
                (screen.x as f32 * OVERSIZE.x).round_ties_even() as u32,
                (screen.y as f32 * OVERSIZE.y).round_ties_even() as u32,
            )
            .max(UVec2::ONE),
            scale: scale.max(f32::EPSILON),
        }
    }
    /// Godot KeepWidth uses the source screen aspect and constant horizontal
    /// oversize, then the actual integer target aspect supplies vertical FOV.
    pub fn vertical_fov(self, source_vertical: f32) -> f32 {
        let screen_aspect = self.screen.x as f32 / self.screen.y as f32;
        let target_aspect = self.target.x as f32 / self.target.y as f32;
        2. * ((source_vertical * 0.5).tan() * screen_aspect * OVERSIZE.x / target_aspect).atan()
    }
    /// UI anchors are logical pixels. Source VpToScreen only subtracts margins:
    /// the tracer/attachment overlay deliberately does not inherit image roll.
    pub fn viewport_to_screen(self, pixel: Vec2) -> Vec2 {
        pixel - (self.target.as_vec2() - self.screen.as_vec2()) * (0.5 / self.scale)
    }
    pub fn cropped_projection(self, projection: Mat4) -> Mat4 {
        let scale = self.target.as_vec2() / self.screen.as_vec2();
        Mat4::from_scale(Vec3::new(scale.x, scale.y, 1.)) * projection
    }
    #[cfg(test)]
    fn sample_uv(self, screen_uv: Vec2, lean: f32) -> Vec2 {
        let p = (screen_uv - Vec2::splat(0.5)) * self.screen.as_vec2();
        let (s, c) = lean.sin_cos();
        Vec2::splat(0.5) + Vec2::new(c * p.x - s * p.y, s * p.x + c * p.y) / self.target.as_vec2()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn source_expansion_and_crop_preserve_unrolled_projection() {
        for screen in [
            UVec2::new(1280, 720),
            UVec2::new(2560, 1440),
            UVec2::new(1200, 900),
            UVec2::new(800, 1200),
        ] {
            let viewport = ViewmodelViewport::new(screen, 1.);
            let original = Mat4::perspective_infinite_reverse_rh(
                SOURCE_FOV,
                screen.x as f32 / screen.y as f32,
                0.01,
            );
            let expanded = Mat4::perspective_infinite_reverse_rh(
                viewport.vertical_fov(SOURCE_FOV),
                viewport.target.x as f32 / viewport.target.y as f32,
                0.01,
            );
            let cropped = viewport.cropped_projection(expanded);
            for point in [
                Vec3::new(0., 0., -1.),
                Vec3::new(0.12, -0.17, -0.6),
                Vec3::new(-0.8, 0.4, -2.),
            ] {
                let a = original.project_point3(point);
                let b = cropped.project_point3(point);
                assert!((a - b).length() < 1e-5, "{screen:?}: {a:?} vs {b:?}");
            }
        }
    }
    #[test]
    fn integer_rounding_and_dpi_keep_source_pixel_margin() {
        let viewport = ViewmodelViewport::new(UVec2::new(1279, 719), 2.);
        assert_eq!(viewport.target, UVec2::new(1471, 1043));
        assert_eq!(
            ViewmodelViewport::new(UVec2::splat(10), 1.).target,
            UVec2::new(12, 14)
        );
        assert_eq!(
            viewport.viewport_to_screen(viewport.target.as_vec2() / 4.),
            viewport.screen.as_vec2() / 4.
        );
        assert_eq!(ViewmodelViewport::new(UVec2::ZERO, 1.).screen, UVec2::ONE);
    }
    #[test]
    fn image_roll_is_inverse_screen_space_and_keeps_ads_at_centre() {
        let viewport = ViewmodelViewport::default();
        for lean in [-20_f32.to_radians(), 0., 20_f32.to_radians()] {
            assert_eq!(viewport.sample_uv(Vec2::splat(0.5), lean), Vec2::splat(0.5));
            // Rotate a source image landmark by -lean, then sample: it must
            // recover that original landmark independent of aspect/padding.
            let point = Vec2::new(210., -100.);
            let (s, c) = (-lean).sin_cos();
            let screen = Vec2::splat(0.5)
                + Vec2::new(c * point.x - s * point.y, s * point.x + c * point.y)
                    / viewport.screen.as_vec2();
            let expected = Vec2::splat(0.5) + point / viewport.target.as_vec2();
            assert!((viewport.sample_uv(screen, lean) - expected).length() < 1e-6);
        }
    }
}
