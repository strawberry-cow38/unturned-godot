//! Plain text through Bevy's font shaper, then one outlined world-space texture.
//! A display name never becomes markup or a URI; the original engine font is embedded.
use bevy::{
    ecs::system::SystemParam,
    prelude::*,
    text::{
        ComputedTextBlock, FontAtlasSet, FontCx, LayoutCx, LetterSpacing, LineHeight, RemSize,
        ScaleCx, TextBounds, TextElement, TextLayoutInfo, TextPipeline,
    },
};

#[derive(Resource)]
pub(crate) struct OriginalFont(pub(crate) Handle<Font>);
impl FromWorld for OriginalFont {
    fn from_world(world: &mut World) -> Self {
        Self(world.resource_mut::<Assets<Font>>().add(Font::from_bytes(
            include_bytes!("../../../assets/fonts/OpenSans_SemiBold.ttf").to_vec(),
        )))
    }
}

#[derive(SystemParam)]
pub(crate) struct Raster<'w> {
    fonts: Res<'w, Assets<Font>>,
    font: Res<'w, OriginalFont>,
    pipeline: ResMut<'w, TextPipeline>,
    font_cx: ResMut<'w, FontCx>,
    layout_cx: ResMut<'w, LayoutCx>,
    scale_cx: ResMut<'w, ScaleCx>,
    atlases: ResMut<'w, FontAtlasSet>,
    pub images: ResMut<'w, Assets<Image>>,
}
pub(crate) struct Bitmap {
    pub image: Image,
    pub offset: Vec2,
    pub origin: Vec2,
    pub layout: Vec2,
}
impl Raster<'_> {
    pub fn render(&mut self, text: &str) -> Option<Bitmap> {
        let layout = self.layout(text, 64., false)?;
        bake(&layout, &self.images, 12., 0.85)
    }
    /// Original InfoBillboard.FitLabel: actual font advance width, base size
    /// reduced one pixel at a time, with a 15px minimum and an 8px side margin.
    pub fn render_fitted(
        &mut self,
        text: &str,
        base: f32,
        max_width: f32,
        outline: f32,
        outline_alpha: f32,
    ) -> Option<Bitmap> {
        let mut size = base;
        loop {
            let layout = self.layout(text, size, true)?;
            if size <= 15. || layout.size.x <= max_width {
                return bake(&layout, &self.images, outline, outline_alpha);
            }
            size -= 1.;
        }
    }
    fn layout(&mut self, text: &str, size: f32, source_label: bool) -> Option<TextLayoutInfo> {
        let font = TextFont {
            font: self.font.0.clone().into(),
            font_size: FontSize::Px(size),
            ..default()
        };
        let span = TextElement::Text {
            text,
            font: &font,
            color: Color::WHITE,
            line_height: LineHeight::default(),
            letter_spacing: LetterSpacing::default(),
        };
        let mut computed = ComputedTextBlock::default();
        self.pipeline
            .update_buffer(
                &self.fonts,
                std::iter::once((Entity::PLACEHOLDER, 0, span)),
                LineBreak::NoWrap,
                Justify::Center,
                TextBounds::UNBOUNDED,
                1.,
                &mut computed,
                &mut self.font_cx,
                &mut self.layout_cx,
                Vec2::new(1280., 720.),
                RemSize::default(),
            )
            .ok()?;
        let mut layout = TextLayoutInfo::default();
        self.pipeline
            .update_text_layout_info(
                &mut layout,
                &mut self.atlases,
                &mut self.images,
                &mut computed,
                &mut self.scale_cx,
                TextBounds::UNBOUNDED,
                Justify::Center,
                FontHinting::default(),
            )
            .ok()?;
        if source_label {
            // Godot's non-MSDF FontFile takes ceil(FreeType ascender), rather
            // than centering the font inside Bevy's default 1.2em line height.
            // Read the same embedded font's shaped metrics, not an ink offset
            // calibrated for one string or one font size.
            if let Some(line) = computed.buffer().lines().next()
                && let Some(run) = line.runs().next()
            {
                let shift = run.metrics().ascent.ceil() - line.metrics().baseline;
                for glyph in &mut layout.glyphs {
                    glyph.position.y += shift;
                }
            }
        }
        Some(layout)
    }
}

fn bake(
    layout: &TextLayoutInfo,
    images: &Assets<Image>,
    radius: f32,
    outline_alpha: f32,
) -> Option<Bitmap> {
    if layout.glyphs.is_empty() {
        return None;
    }
    let mut min = Vec2::ZERO;
    let mut max = layout.size;
    for glyph in &layout.glyphs {
        let half = glyph.atlas_info.rect.size() * 0.5;
        min = min.min(glyph.position - half);
        max = max.max(glyph.position + half);
    }
    let pad = radius.ceil() as i32;
    min = min.floor() - Vec2::splat(pad as f32);
    max = max.ceil() + Vec2::splat(pad as f32);
    let size = (max - min).as_uvec2();
    if size.x == 0 || size.y == 0 || size.x > 4096 || size.y > 1024 {
        return None;
    }
    let (width, height) = (size.x as usize, size.y as usize);
    let mut pixels = vec![[0_u8; 4]; width * height];
    for glyph in &layout.glyphs {
        let atlas = images.get(glyph.atlas_info.texture)?;
        let data = atlas.data.as_ref()?;
        let rect = glyph.atlas_info.rect;
        let dst = (glyph.position - rect.size() * 0.5 - min)
            .round()
            .as_ivec2();
        for y in 0..rect.height() as usize {
            for x in 0..rect.width() as usize {
                let (dx, dy) = (dst.x + x as i32, dst.y + y as i32);
                if dx < 0 || dy < 0 || dx as usize >= width || dy as usize >= height {
                    continue;
                }
                let src =
                    ((rect.min.y as usize + y) * atlas.width() as usize + rect.min.x as usize + x)
                        * 4;
                let p = &mut pixels[dy as usize * width + dx as usize];
                if data[src + 3] > p[3] {
                    p.copy_from_slice(&data[src..src + 4]);
                }
            }
        }
    }
    let mut outline = vec![0_u8; width * height];
    for y in 0..height {
        for x in 0..width {
            let alpha = pixels[y * width + x][3];
            if alpha == 0 {
                continue;
            }
            for oy in -pad..=pad {
                let dy = y as i32 + oy;
                if dy < 0 || dy as usize >= height {
                    continue;
                }
                for ox in -pad..=pad {
                    let distance = ((ox * ox + oy * oy) as f32).sqrt();
                    // Keep the existing integer-radius nameplate dilation
                    // byte-identical; fractional strokes cover the edge pixel.
                    let coverage = if radius.fract() == 0. {
                        if distance <= radius { 1. } else { 0. }
                    } else {
                        (radius + 0.5 - distance).clamp(0., 1.)
                    };
                    let dx = x as i32 + ox;
                    if dx >= 0 && (dx as usize) < width {
                        let dst = &mut outline[dy as usize * width + dx as usize];
                        *dst = (*dst).max((alpha as f32 * coverage).round() as u8);
                    }
                }
            }
        }
    }
    let mut rgba = Vec::with_capacity(width * height * 4);
    for (pixel, edge) in pixels.into_iter().zip(outline) {
        let fg = pixel[3] as f32 / 255.;
        let alpha = fg + edge as f32 / 255. * outline_alpha * (1. - fg);
        let weight = if alpha > 0. { fg / alpha } else { 0. };
        rgba.extend([
            (pixel[0] as f32 * weight).round() as u8,
            (pixel[1] as f32 * weight).round() as u8,
            (pixel[2] as f32 * weight).round() as u8,
            (alpha * 255.).round() as u8,
        ]);
    }
    Some(Bitmap {
        image: super::rgba_image(rgba, size.x, size.y),
        // Preserve Label3D's centred layout rather than centring ink bearings.
        offset: ((min + max) * 0.5 - layout.size * 0.5) * Vec2::new(1., -1.) * (0.16 / 64.),
        origin: min,
        layout: layout.size,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use bevy::text::{GlyphAtlasInfo, PositionedGlyph};
    #[test]
    fn existing_nameplate_integer_outline_keeps_its_original_disk_and_alpha() {
        let mut images = Assets::<Image>::default();
        let atlas = images.add(super::super::rgba_image(vec![255; 4], 1, 1));
        let mut layout = TextLayoutInfo::default();
        layout.size = Vec2::ONE;
        layout.glyphs.push(PositionedGlyph {
            position: Vec2::splat(0.5),
            atlas_info: GlyphAtlasInfo {
                texture: atlas.id(),
                rect: Rect::new(0., 0., 1., 1.),
                offset: Vec2::ZERO,
                is_alpha_mask: true,
            },
            section_index: 0,
            line_index: 0,
        });
        let bitmap = bake(&layout, &images, 12., 0.85).unwrap();
        assert_eq!(bitmap.image.size(), UVec2::splat(25));
        assert_eq!(bitmap.offset, Vec2::ZERO);
        let pixels = bitmap.image.data.unwrap();
        for y in 0..25_i32 {
            for x in 0..25_i32 {
                let p = &pixels[((y * 25 + x) * 4) as usize..][..4];
                let expected = if (x, y) == (12, 12) {
                    [255; 4]
                } else if (x - 12).pow(2) + (y - 12).pow(2) <= 144 {
                    [0, 0, 0, 217]
                } else {
                    [0; 4]
                };
                assert_eq!(p, expected, "pixel ({x},{y})");
            }
        }
    }
}
