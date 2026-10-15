//! CraftingMenu.PadlockGlyph: the source twenty-point shackle, body and keyhole.
//! Supersampling only supplies canvas antialiasing; geometry/colors remain authored.
use bevy::{
    asset::RenderAssetUsages,
    image::ImageSampler,
    prelude::*,
    render::render_resource::{Extent3d, TextureDimension, TextureFormat},
};

#[derive(Default)]
pub(in crate::ui::crafting) struct Cache(Option<(Vec2, Handle<Image>)>);
impl Cache {
    pub fn get(&mut self, size: Vec2, images: &mut Assets<Image>) -> Handle<Image> {
        if let Some((old, image)) = &self.0
            && *old == size
        {
            return image.clone();
        }
        let image = images.add(draw(size));
        self.0 = Some((size, image.clone()));
        image
    }
}
const INK: Vec4 = Vec4::new(0.92, 0.86, 0.62, 0.95);
const SHADE: Vec4 = Vec4::new(0., 0., 0., 0.55);
const HOLE: Vec4 = Vec4::new(0.18, 0.16, 0.12, 1.);
fn over(dst: Vec4, source: Vec4) -> Vec4 {
    let alpha = source.w + dst.w * (1. - source.w);
    if alpha == 0. {
        return Vec4::ZERO;
    }
    ((source.truncate() * source.w + dst.truncate() * dst.w * (1. - source.w)) / alpha)
        .extend(alpha)
}
fn segment_distance(point: Vec2, a: Vec2, b: Vec2) -> f32 {
    let v = b - a;
    point
        .distance(a + v * ((point - a).dot(v) / v.length_squared().max(f32::EPSILON)).clamp(0., 1.))
}
fn sample(point: Vec2, size: Vec2) -> Vec4 {
    let width = size.x;
    let height = size.y * 0.58;
    let y = size.y - height;
    let radius = width * 0.30;
    let center = Vec2::new(width * 0.5, y + 1.);
    let points: [Vec2; 20] = std::array::from_fn(|i| {
        center
            + Vec2::from_angle(std::f32::consts::PI + std::f32::consts::PI * i as f32 / 19.)
                * radius
    });
    let distance = points
        .windows(2)
        .map(|p| segment_distance(point, p[0], p[1]))
        .fold(f32::INFINITY, f32::min);
    let mut color = Vec4::ZERO;
    if distance <= width * 0.11 {
        color = over(color, SHADE);
    }
    if distance <= width * 0.07 {
        color = over(color, INK);
    }
    for x in [center.x - radius, center.x + radius] {
        if segment_distance(point, Vec2::new(x, center.y), Vec2::new(x, y + 2.)) <= width * 0.07 {
            color = over(color, INK);
        }
    }
    if Rect::new(-1., y - 1., width + 1., y + height + 1.).contains(point) {
        color = over(color, SHADE);
    }
    if Rect::new(0., y, width, y + height).contains(point) {
        color = over(color, INK);
    }
    if point.distance(Vec2::new(center.x, y + height * 0.40)) <= width * 0.10 {
        color = over(color, HOLE);
    }
    if Rect::new(
        center.x - width * 0.04,
        y + height * 0.40,
        center.x + width * 0.04,
        y + height * 0.72,
    )
    .contains(point)
    {
        color = over(color, HOLE);
    }
    color
}
fn draw(size: Vec2) -> Image {
    // The original outline extends one pixel beyond the Control bounds.
    let scale = size.x / (116. * 0.36);
    let authored = size / scale;
    let extent = (size + Vec2::splat(2. * scale)).ceil().as_uvec2();
    let mut data = vec![0; (extent.x * extent.y * 4) as usize];
    const AA: u32 = 4;
    for y in 0..extent.y {
        for x in 0..extent.x {
            let mut premultiplied = Vec4::ZERO;
            for sy in 0..AA {
                for sx in 0..AA {
                    let point = Vec2::new(
                        x as f32 + (sx as f32 + 0.5) / AA as f32,
                        y as f32 + (sy as f32 + 0.5) / AA as f32,
                    ) / scale
                        - Vec2::ONE;
                    let color = sample(point, authored);
                    premultiplied +=
                        (color.truncate() * color.w).extend(color.w) / (AA * AA) as f32;
                }
            }
            let color = if premultiplied.w > 0. {
                (premultiplied.truncate() / premultiplied.w).extend(premultiplied.w)
            } else {
                Vec4::ZERO
            };
            let index = ((y * extent.x + x) * 4) as usize;
            data[index..index + 4].copy_from_slice(
                &color
                    .to_array()
                    .map(|c| (c.clamp(0., 1.) * 255.).round() as u8),
            );
        }
    }
    let mut image = Image::new(
        Extent3d {
            width: extent.x,
            height: extent.y,
            depth_or_array_layers: 1,
        },
        TextureDimension::D2,
        data,
        TextureFormat::Rgba8UnormSrgb,
        RenderAssetUsages::default(),
    );
    image.sampler = ImageSampler::linear();
    image
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn source_lock_preserves_transparent_shackle_hole_brass_body_and_dark_keyhole() {
        let size = Vec2::new(41.76, 49.88);
        assert_eq!(
            sample(Vec2::new(size.x * 0.5, 17.), size),
            Vec4::ZERO,
            "the shackle must be open"
        );
        let body = sample(Vec2::new(6., 35.), size);
        assert!(
            body.x > body.y && body.y > body.z && body.w > 0.95,
            "source brass, not a gray block"
        );
        assert_eq!(sample(Vec2::new(size.x * 0.5, 34.), size), HOLE);
        let image = draw(size);
        assert!(
            image
                .data
                .as_ref()
                .unwrap()
                .chunks_exact(4)
                .any(|pixel| pixel[3] > 0 && pixel[3] < 140),
            "source antialiased outline stays translucent"
        );
        // Optional cosmetic inspection of the actual renderer output. This is
        // not a persisted golden or a shipping recipe/unlock journey.
        if let Some(path) = std::env::var_os("UG_CRAFT_PADLOCK_ARTIFACT") {
            image.try_into_dynamic().unwrap().save(path).unwrap();
        }
    }
}
