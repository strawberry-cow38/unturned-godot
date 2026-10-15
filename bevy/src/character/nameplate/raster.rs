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
pub(super) struct OriginalFont(Handle<Font>);
impl FromWorld for OriginalFont {
    fn from_world(world: &mut World) -> Self {
        Self(world.resource_mut::<Assets<Font>>().add(Font::from_bytes(
            include_bytes!("../../../assets/fonts/OpenSans_SemiBold.ttf").to_vec(),
        )))
    }
}

#[derive(SystemParam)]
pub(super) struct Raster<'w> {
    fonts: Res<'w, Assets<Font>>,
    font: Res<'w, OriginalFont>,
    pipeline: ResMut<'w, TextPipeline>,
    font_cx: ResMut<'w, FontCx>,
    layout_cx: ResMut<'w, LayoutCx>,
    scale_cx: ResMut<'w, ScaleCx>,
    atlases: ResMut<'w, FontAtlasSet>,
    pub images: ResMut<'w, Assets<Image>>,
}
pub(super) struct Bitmap {
    pub image: Image,
    pub offset: Vec2,
}
impl Raster<'_> {
    pub fn render(&mut self, text: &str) -> Option<Bitmap> {
        let font = TextFont {
            font: self.font.0.clone().into(),
            font_size: FontSize::Px(64.),
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
        bake(&layout, &self.images)
    }
}

fn bake(layout: &TextLayoutInfo, images: &Assets<Image>) -> Option<Bitmap> {
    const OUTLINE: i32 = 12;
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
    min = min.floor() - Vec2::splat(OUTLINE as f32);
    max = max.ceil() + Vec2::splat(OUTLINE as f32);
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
            for oy in -OUTLINE..=OUTLINE {
                let spread = ((OUTLINE * OUTLINE - oy * oy) as f32).sqrt().floor() as i32;
                let dy = y as i32 + oy;
                if dy < 0 || dy as usize >= height {
                    continue;
                }
                for ox in -spread..=spread {
                    let dx = x as i32 + ox;
                    if dx >= 0 && (dx as usize) < width {
                        let dst = &mut outline[dy as usize * width + dx as usize];
                        *dst = (*dst).max(alpha);
                    }
                }
            }
        }
    }
    let mut rgba = Vec::with_capacity(width * height * 4);
    for (pixel, edge) in pixels.into_iter().zip(outline) {
        let fg = pixel[3] as f32 / 255.;
        let alpha = fg + edge as f32 / 255. * 0.85 * (1. - fg);
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
    })
}
