//! Fixed InfoBillboard canvas coordinates and source alpha blending. The font
//! shaper/outline raster is shared with original avatar/NPC nameplates.
use super::*;
use bevy::{
    asset::RenderAssetUsages,
    image::{CompressedImageFormats, ImageType},
    render::render_resource::{Extent3d, TextureDimension, TextureFormat},
};
use std::{collections::HashMap, path::Path};

struct Bitmap {
    pixels: Vec<u8>,
    width: usize,
    height: usize,
    origin: Vec2,
    layout: Vec2,
}
impl Bitmap {
    fn sample(&self, uv: Vec2, linear: bool) -> [f32; 4] {
        let point = uv * Vec2::new(self.width as f32, self.height as f32);
        let texel = |x: i32, y: i32| {
            let x = x.clamp(0, self.width as i32 - 1) as usize;
            let y = y.clamp(0, self.height as i32 - 1) as usize;
            std::array::from_fn::<_, 4, _>(|i| {
                self.pixels[(y * self.width + x) * 4 + i] as f32 / 255.
            })
        };
        if !linear {
            return texel(point.x.floor() as i32, point.y.floor() as i32);
        }
        // Inner TextureRects inherit the original SubViewport's linear
        // filtering. Only the finished Sprite3D canvas uses nearest filtering.
        let p = point - Vec2::splat(0.5);
        let base = p.floor();
        let t = p - base;
        let (x, y) = (base.x as i32, base.y as i32);
        let a = texel(x, y);
        let b = texel(x + 1, y);
        let c = texel(x, y + 1);
        let d = texel(x + 1, y + 1);
        std::array::from_fn(|i| {
            (a[i] * (1. - t.x) + b[i] * t.x) * (1. - t.y) + (c[i] * (1. - t.x) + d[i] * t.x) * t.y
        })
    }
}
#[derive(Resource, Default)]
pub(super) struct Cache {
    labels: HashMap<(String, u8), Bitmap>,
    icons: HashMap<String, Option<Bitmap>>,
}
impl Cache {
    fn label(&mut self, raster: &mut Raster, text: &str, size: u8) -> Option<&Bitmap> {
        let key = (text.to_string(), size);
        if !self.labels.contains_key(&key) {
            // Godot 4.6 TextServerAdvanced's non-MSDF stroker multiplies
            // outline_size by 16 in FreeType's 26.6 units: 6 means 1.5px.
            let bitmap = raster.render_fitted(text, size as f32, 312., 6. / 4., 0.9)?;
            let width = bitmap.image.width() as usize;
            let height = bitmap.image.height() as usize;
            let value = Bitmap {
                pixels: bitmap.image.data.as_ref()?[..width * height * 4].to_vec(),
                width,
                height,
                origin: bitmap.origin,
                layout: bitmap.layout,
            };
            if self.labels.len() >= 64 {
                self.labels.clear();
            }
            self.labels.insert(key.clone(), value);
        }
        self.labels.get(&key)
    }
    fn icon(&mut self, root: &Path, name: &str) -> Option<&Bitmap> {
        self.icons
            .entry(name.into())
            .or_insert_with(|| {
                let bytes = std::fs::read(root.join("ui").join(name)).ok()?;
                let image = Image::from_buffer(
                    &bytes,
                    ImageType::Extension("png"),
                    CompressedImageFormats::NONE,
                    true,
                    ImageSampler::nearest(),
                    RenderAssetUsages::default(),
                )
                .ok()?
                .try_into_dynamic()
                .ok()?
                .into_rgba8();
                Some(Bitmap {
                    width: image.width() as usize,
                    height: image.height() as usize,
                    pixels: image.into_raw(),
                    origin: Vec2::ZERO,
                    layout: Vec2::ZERO,
                })
            })
            .as_ref()
    }
}
struct Canvas(Vec<u8>);
impl Canvas {
    fn pixel(&mut self, x: i32, y: i32, color: [f32; 4]) {
        if x < 0 || y < 0 || x as usize >= WIDTH || y as usize >= HEIGHT {
            return;
        }
        let at = (y as usize * WIDTH + x as usize) * 4;
        let old = &mut self.0[at..at + 4];
        let back = old[3] as f32 / 255.;
        let alpha = color[3] + back * (1. - color[3]);
        if alpha <= 0. {
            return;
        }
        for i in 0..3 {
            old[i] = ((color[i] * color[3] + old[i] as f32 / 255. * back * (1. - color[3])) / alpha
                * 255.)
                .round() as u8;
        }
        old[3] = (alpha * 255.).round() as u8;
    }
    fn rect(&mut self, x: f32, y: f32, w: f32, h: f32, color: [f32; 4]) {
        for py in y.floor() as i32..(y + h).ceil() as i32 {
            for px in x.floor() as i32..(x + w).ceil() as i32 {
                if px as f32 + 0.5 < x + w && py as f32 + 0.5 < y + h {
                    self.pixel(px, py, color);
                }
            }
        }
    }
    fn bitmap(&mut self, source: &Bitmap, origin: Vec2, size: Vec2, color: Color, linear: bool) {
        let tint = color.to_srgba().to_f32_array();
        for py in 0..size.y.ceil() as usize {
            for px in 0..size.x.ceil() as usize {
                let uv = Vec2::new((px as f32 + 0.5) / size.x, (py as f32 + 0.5) / size.y);
                let sample = source.sample(uv, linear);
                let rgba = std::array::from_fn(|i| sample[i] * tint[i]);
                self.pixel(
                    origin.x.round() as i32 + px as i32,
                    origin.y.round() as i32 + py as i32,
                    rgba,
                );
            }
        }
    }
    fn label(&mut self, source: &Bitmap, y: f32, color: Color) {
        self.bitmap(
            source,
            Vec2::new((WIDTH as f32 - source.layout.x) * 0.5, y) + source.origin,
            Vec2::new(source.width as f32, source.height as f32),
            color,
            false,
        );
    }
}
pub(super) fn compose(
    content: &Content,
    root: &Path,
    raster: &mut Raster,
    cache: &mut Cache,
) -> Option<Image> {
    let mut canvas = Canvas(vec![0; WIDTH * HEIGHT * 4]);
    if !content.name.is_empty() {
        canvas.label(
            cache.label(raster, &content.name, 30)?,
            4.,
            content.name_color,
        );
    }
    if !content.prompt.is_empty() {
        canvas.label(
            cache.label(raster, &content.prompt, 26)?,
            258.,
            content.prompt_color,
        );
    }
    let icons = [
        "ui_health.png",
        "ui_fuel.png",
        "ui_stamina.png",
        "ui_health.png",
        "ui_health.png",
    ];
    for (i, bar) in content.bars.iter().enumerate() {
        let Some(bar) = bar else { continue };
        let y = 44. + i as f32 * 32.;
        canvas.rect(48., y, 210., 22., [0., 0., 0., 0.55]);
        let value = if bar.value.is_finite() {
            bar.value.clamp(0., 1.)
        } else {
            0.
        };
        canvas.rect(
            48.,
            y,
            210. * value,
            22.,
            bar.color.to_srgba().to_f32_array(),
        );
        if let Some(icon) = cache.icon(root, icons[i]) {
            let size = Vec2::new(icon.width as f32, icon.height as f32);
            let display = size * (26. / size.max_element());
            canvas.bitmap(
                icon,
                Vec2::new(14., y - 2.) + (Vec2::splat(26.) - display) * 0.5,
                display,
                Color::WHITE,
                true,
            );
        }
    }
    let mut image = Image::new_uninit(
        Extent3d {
            width: WIDTH as u32,
            height: HEIGHT as u32,
            depth_or_array_layers: 1,
        },
        TextureDimension::D2,
        TextureFormat::Rgba8UnormSrgb,
        RenderAssetUsages::default(),
    );
    image.data = Some(canvas.0);
    Some(image)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn transparent_canvas_keeps_straight_alpha_for_the_world_quad() {
        let mut canvas = Canvas(vec![0; WIDTH * HEIGHT * 4]);
        canvas.pixel(1, 1, [1., 0., 0., 0.5]);
        let at = (WIDTH + 1) * 4;
        assert_eq!(&canvas.0[at..at + 4], &[255, 0, 0, 128]);
        canvas.pixel(1, 1, [0., 0., 1., 0.5]);
        assert_eq!(&canvas.0[at..at + 4], &[85, 0, 170, 192]);
        canvas.pixel(-1, -1, [1.; 4]);
        assert_eq!(&canvas.0[..4], &[0; 4]);
    }
    #[test]
    fn inner_icon_filter_is_linear_and_finished_text_filter_is_nearest() {
        let bitmap = Bitmap {
            pixels: vec![255, 0, 0, 255, 0, 0, 255, 0],
            width: 2,
            height: 1,
            origin: Vec2::ZERO,
            layout: Vec2::ZERO,
        };
        assert_eq!(bitmap.sample(Vec2::splat(0.5), true), [0.5, 0., 0.5, 0.5]);
        assert_eq!(bitmap.sample(Vec2::splat(0.5), false), [0., 0., 1., 0.]);
        assert_eq!(bitmap.sample(Vec2::ZERO, true), [1., 0., 0., 1.]);
    }
}
