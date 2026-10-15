//! InventoryUI.UprightIcon and BuildMoneyFan: source artwork determines its own
//! tight frame; a fixed five-note reference keeps denomination size consistent.
use super::api::Denomination;
use bevy::{
    asset::RenderAssetUsages,
    image::{CompressedImageFormats, ImageSampler, ImageType},
    prelude::*,
    render::render_resource::{Extent3d, TextureDimension, TextureFormat},
};
use std::{collections::HashMap, path::Path};
/// Original Currency.Breakdown, using the source-provided largest-first table.
/// This changes only a local preview; repetitions are essential for $249/$500.
pub(super) fn breakdown(mut amount: u16, currency: &[Denomination]) -> Vec<Denomination> {
    let mut result = Vec::new();
    for denomination in currency {
        let Ok(value) = u16::try_from(denomination.value) else {
            continue;
        };
        if value == 0 {
            continue;
        }
        result.extend(std::iter::repeat_n(
            denomination.clone(),
            usize::from(amount / value),
        ));
        amount %= value;
    }
    result
}
pub(super) fn preview(
    tile: &super::api::Tile,
    amount: u16,
    currency: &[Denomination],
) -> super::api::Tile {
    let mut result = tile.clone();
    if result.money && amount != result.amount {
        result.denominations = breakdown(amount, currency);
    }
    result.amount = amount;
    result
}
#[derive(Default)]
pub(super) struct Art(HashMap<u16, Option<(Handle<Image>, Vec2)>>);
impl Art {
    pub fn get(
        &mut self,
        denomination: &Denomination,
        root: &Path,
        images: &mut Assets<Image>,
    ) -> Option<(Handle<Image>, Vec2)> {
        self.0
            .entry(denomination.id)
            .or_insert_with(|| {
                let bytes = std::fs::read(root.join(&denomination.icon)).ok()?;
                let image = Image::from_buffer(
                    &bytes,
                    ImageType::Extension("png"),
                    CompressedImageFormats::NONE,
                    true,
                    ImageSampler::nearest(),
                    RenderAssetUsages::default(),
                )
                .ok()?;
                let image = image.convert(TextureFormat::Rgba8UnormSrgb)?;
                let (w, h, data) = upright(image.width(), image.height(), image.data.as_deref()?);
                let image = rgba(w, h, data);
                Some((images.add(image), Vec2::new(w as f32, h as f32)))
            })
            .clone()
    }
}
pub(super) fn rgba(w: u32, h: u32, data: Vec<u8>) -> Image {
    let mut image = Image::new(
        Extent3d {
            width: w,
            height: h,
            depth_or_array_layers: 1,
        },
        TextureDimension::D2,
        data,
        TextureFormat::Rgba8UnormSrgb,
        RenderAssetUsages::default(),
    );
    image.sampler = ImageSampler::nearest();
    image
}
fn upright(w: u32, h: u32, src: &[u8]) -> (u32, u32, Vec<u8>) {
    let mut top = None;
    let mut left = Vec2::ZERO;
    let mut right = Vec2::ZERO;
    let mut best_left = u32::MAX;
    let mut best_right = 0;
    for y in 0..h {
        for x in 0..w {
            if src[((y * w + x) * 4 + 3) as usize] <= 5 {
                continue;
            }
            top.get_or_insert(Vec2::new(x as f32, y as f32));
            if x < best_left {
                best_left = x;
                left = Vec2::new(x as f32, y as f32);
            }
            if x > best_right {
                best_right = x;
                right = Vec2::new(x as f32, y as f32);
            }
        }
    }
    let Some(top) = top else {
        return (w, h, src.to_vec());
    };
    let a = top - left;
    let b = right - top;
    let mut axis = if a.length_squared() >= b.length_squared() {
        a
    } else {
        b
    };
    if axis.length_squared() < 1. {
        axis = Vec2::X;
    }
    if axis.x < 0. {
        axis = -axis;
    }
    axis = axis.normalize();
    let across = Vec2::new(-axis.y, axis.x);
    let center = Vec2::new(w as f32, h as f32) * 0.5;
    let mut low = Vec2::splat(f32::INFINITY);
    let mut high = Vec2::splat(f32::NEG_INFINITY);
    for y in 0..h {
        for x in 0..w {
            if src[((y * w + x) * 4 + 3) as usize] <= 5 {
                continue;
            }
            let d = Vec2::new(x as f32, y as f32) - center;
            let uv = Vec2::new(d.dot(axis), d.dot(across));
            low = low.min(uv);
            high = high.max(uv);
        }
    }
    let extent = high - low;
    let size = UVec2::new(
        extent.x.round_ties_even() as u32,
        extent.y.round_ties_even() as u32,
    ) + UVec2::ONE;
    let size = size.clamp(UVec2::ONE, UVec2::splat(1024));
    let mut out = vec![0; (size.x * size.y * 4) as usize];
    for y in 0..size.y {
        for x in 0..size.x {
            let p = center + axis * (low.x + x as f32) + across * (low.y + y as f32);
            let p = IVec2::new(p.x.round_ties_even() as i32, p.y.round_ties_even() as i32);
            if p.x < 0 || p.y < 0 || p.x >= w as i32 || p.y >= h as i32 {
                continue;
            }
            let source = ((p.y as u32 * w + p.x as u32) * 4) as usize;
            let dest = ((y * size.x + x) * 4) as usize;
            out[dest..dest + 4].copy_from_slice(&src[source..source + 4]);
        }
    }
    (size.x, size.y, out)
}
#[derive(Clone, Debug)]
pub(super) struct Piece {
    pub id: u16,
    pub image: Handle<Image>,
    pub size: Vec2,
    pub center: Vec2,
    pub radians: f32,
}
/// Pixel centers are transformed explicitly because Bevy UiTransform rotates
/// around a node's center; source TextureRect uses an outboard short-edge pivot.
pub(super) fn fan(
    denominations: &[Denomination],
    size: Vec2,
    art: &mut Art,
    root: &Path,
    images: &mut Assets<Image>,
) -> Vec<Piece> {
    let notes: Vec<_> = denominations.iter().filter(|d| d.value >= 5).collect();
    let same = notes.windows(2).filter(|p| p[0].id == p[1].id).count();
    let diff = notes.len().saturating_sub(1) - same;
    let same_step = if (same + diff) as f32 * 17. > 68. && same > 0 {
        ((68. - diff as f32 * 17.) / same as f32).clamp(5., 17.)
    } else {
        17.
    };
    let mut angles = vec![0.; notes.len()];
    let mut spread = 0.;
    for i in 1..notes.len() {
        spread += if notes[i].id == notes[i - 1].id {
            same_step
        } else {
            17.
        };
        angles[i] = spread;
    }
    let squeeze = if spread > 68. { 68. / spread } else { 1. };
    let mut pending = Vec::new();
    let mut note_size = Vec2::new(1., 0.5);
    for (i, note) in notes.iter().enumerate() {
        let Some((image, dimensions)) = art.get(note, root, images) else {
            continue;
        };
        note_size = Vec2::new(1., dimensions.y / dimensions.x.max(1.));
        pending.push((
            note.id,
            image,
            note_size,
            (-90. + (angles[i] - spread * 0.5) * squeeze).to_radians(),
        ));
    }
    let mut result = Vec::new();
    if !pending.is_empty() {
        let pivot = Vec2::new(-0.12, note_size.y * 0.5);
        let mut lo = Vec2::splat(f32::INFINITY);
        let mut hi = Vec2::splat(f32::NEG_INFINITY);
        for i in 0..5 {
            let rot = Rot2::degrees(-124. + i as f32 * 17.);
            for corner in [
                Vec2::ZERO,
                Vec2::new(note_size.x, 0.),
                note_size,
                Vec2::new(0., note_size.y),
            ] {
                let q = rot * (corner - pivot);
                lo = lo.min(q);
                hi = hi.max(q);
            }
        }
        let scale = (size / (hi - lo).max(Vec2::splat(0.001))).min_element() * 0.88;
        let hinge = size * 0.5 - (lo + hi) * 0.5 * scale;
        for (id, image, note, radians) in pending {
            let pivot = Vec2::new(-0.12, note.y * 0.5);
            result.push(Piece {
                id,
                image,
                size: note * scale,
                center: hinge + Rot2::radians(radians) * (note * 0.5 - pivot) * scale,
                radians,
            });
        }
    }
    let cs = size.min_element() * 0.20;
    for (i, coin) in denominations.iter().filter(|d| d.value < 5).enumerate() {
        if let Some((image, dimensions)) = art.get(coin, root, images) {
            result.push(Piece {
                id: coin.id,
                image,
                size: Vec2::new(cs, cs * dimensions.y / dimensions.x.max(1.)),
                center: Vec2::new(size.x * 0.17 + i as f32 * cs * 1.05, size.y * 0.82),
                radians: 0.,
            });
        }
    }
    result
}
#[cfg(test)]
mod tests;
