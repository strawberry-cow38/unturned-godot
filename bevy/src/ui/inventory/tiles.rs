//! Original MakeTile, GridPanel and food badges. All displayed facts arrive in
//! InventoryTiles; this module only clamps drawing extents, never inventory data.
use super::{api, model::CELL, money};
use bevy::{
    asset::RenderAssetUsages,
    image::{CompressedImageFormats, ImageSampler, ImageType},
    picking::Pickable,
    prelude::*,
};
use std::{collections::HashMap, path::Path};

#[derive(Default)]
pub(super) struct Art {
    icons: HashMap<String, Option<(Handle<Image>, Vec2, bool)>>,
    money: money::Art,
    snow: Option<Handle<Image>>,
    pub(super) spin: Option<[Handle<Image>; 2]>,
    pub(super) slider: Option<[Handle<Image>; 2]>,
}
impl Art {
    fn icon(
        &mut self,
        path: &str,
        root: &Path,
        images: &mut Assets<Image>,
    ) -> Option<(Handle<Image>, Vec2, bool)> {
        self.icons
            .entry(path.into())
            .or_insert_with(|| {
                let bytes = std::fs::read(root.join(path)).ok()?;
                let image = Image::from_buffer(
                    &bytes,
                    ImageType::Extension("png"),
                    CompressedImageFormats::NONE,
                    true,
                    ImageSampler::nearest(),
                    RenderAssetUsages::default(),
                )
                .ok()?;
                let size = Vec2::new(image.width() as f32, image.height() as f32);
                let wide = super::super::attachments::drawn_wide(&image);
                Some((images.add(image), size, wide))
            })
            .clone()
    }
    fn snow(&mut self, images: &mut Assets<Image>) -> Handle<Image> {
        self.snow
            .get_or_insert_with(|| images.add(snowflake()))
            .clone()
    }
}
pub(super) fn rect(r: Rect) -> Node {
    Node {
        position_type: PositionType::Absolute,
        left: px(r.min.x),
        top: px(r.min.y),
        width: px(r.width().max(0.)),
        height: px(r.height().max(0.)),
        ..default()
    }
}
pub(super) fn at(x: f32, y: f32, w: f32, h: f32) -> Node {
    rect(Rect::from_corners(Vec2::new(x, y), Vec2::new(x + w, y + h)))
}
pub(super) fn color(rgb: &[f32], fallback: Color) -> Color {
    if rgb.len() >= 3 {
        Color::srgba(rgb[0], rgb[1], rgb[2], rgb.get(3).copied().unwrap_or(1.))
    } else {
        fallback
    }
}
pub(super) fn label(
    p: &mut ChildSpawnerCommands,
    value: impl Into<String>,
    bounds: Rect,
    size: f32,
    color: Color,
    font: &Handle<Font>,
    align: Justify,
) -> Entity {
    let content = value.into();
    let text = (
        Text::new(content),
        TextFont {
            font: font.clone().into(),
            font_size: FontSize::Px(size),
            ..default()
        },
        TextColor(color),
        TextLayout::no_wrap(),
        Pickable::IGNORE,
    );
    if align == Justify::Left {
        return p.spawn((rect(bounds), text)).id();
    }
    // Bevy's NoWrap text has unbounded layout width, so TextLayout::justify
    // cannot align it inside a source Label rectangle. Align its intrinsic
    // child within that rectangle, preserving original unwrapped glyph metrics.
    let mut node = rect(bounds);
    node.justify_content = if align == Justify::Right {
        JustifyContent::FlexEnd
    } else {
        JustifyContent::Center
    };
    node.align_items = AlignItems::FlexStart;
    let mut child = None;
    p.spawn((node, Pickable::IGNORE)).with_children(|p| {
        child = Some(
            p.spawn((
                Node {
                    flex_shrink: 0.,
                    ..default()
                },
                text,
            ))
            .id(),
        );
    });
    child.unwrap()
}

pub(super) fn image(
    p: &mut ChildSpawnerCommands,
    handle: Handle<Image>,
    size: Vec2,
    area: Rect,
    rotation: f32,
    flip: bool,
) {
    let dimensions = size * (area.size() / size.max(Vec2::ONE)).min_element();
    p.spawn((
        rect(Rect::from_center_size(area.center(), dimensions)),
        ImageNode::new(handle),
        UiTransform {
            rotation: Rot2::radians(rotation),
            scale: if flip { Vec2::new(-1., 1.) } else { Vec2::ONE },
            ..default()
        },
        Pickable::IGNORE,
    ));
}
pub(super) fn plain_icon(
    p: &mut ChildSpawnerCommands,
    path: &str,
    bounds: Rect,
    art: &mut Art,
    root: &Path,
    images: &mut Assets<Image>,
) -> bool {
    if let Some((handle, size, _)) = art.icon(path, root, images) {
        image(p, handle, size, bounds, 0., false);
        true
    } else {
        false
    }
}
#[derive(Component)]
pub(super) struct ItemTile(pub u16);
#[derive(Component)]
pub(super) struct FillBar(pub f32);
#[derive(Component)]
pub(super) struct ConditionChip;
#[derive(Component)]
pub(super) struct AutoDrink;
#[derive(Component)]
pub(super) struct Preservation;

fn chip(
    p: &mut ChildSpawnerCommands,
    value: String,
    fg: Color,
    bg: Color,
    border: Color,
    font: &Handle<Font>,
    bottom: bool,
) -> Entity {
    let node = Node {
        position_type: PositionType::Absolute,
        top: if bottom { Val::Auto } else { px(0) },
        left: if bottom { Val::Auto } else { px(0) },
        right: if bottom { px(2) } else { Val::Auto },
        bottom: if bottom { px(2) } else { Val::Auto },
        padding: UiRect::horizontal(px(4)),
        border: UiRect::all(px(1)),
        border_radius: BorderRadius::all(px(3)),
        ..default()
    };
    let snow = value.contains('❄');
    p.spawn((
        node,
        BackgroundColor(bg),
        BorderColor::all(border),
        Pickable::IGNORE,
    ))
    .with_children(|p| {
        let mut entity = p.spawn((
            Text::new(value),
            TextFont {
                font: font.clone().into(),
                font_size: FontSize::Px(11.),
                ..default()
            },
            TextColor(fg),
            TextLayout::no_wrap(),
            Pickable::IGNORE,
        ));
        if snow {
            entity.insert(super::symbols::SnowGlyph);
        }
    })
    .id()
}
#[allow(clippy::too_many_arguments)]
pub(super) fn tile(
    p: &mut ChildSpawnerCommands,
    facts: &api::Tile,
    bounds: Rect,
    rotation: u8,
    magazine: bool,
    condition_chip: bool,
    count: bool,
    art: &mut Art,
    root: &Path,
    images: &mut Assets<Image>,
    font: &Handle<Font>,
) -> Entity {
    let rarity = color(&facts.rarity_color, Color::WHITE);
    let rgb = rarity.to_srgba();
    let mut node = rect(bounds);
    node.overflow = Overflow::clip();
    let size = bounds.size();
    let w = size.x;
    let h = size.y;
    p.spawn((
        node,
        ItemTile(facts.id),
        BackgroundColor(Color::srgba(
            rgb.red * 0.22,
            rgb.green * 0.22,
            rgb.blue * 0.22,
            0.97,
        )),
        Pickable::IGNORE,
    ))
    .with_children(|p| {
        // Source Panel borders paint without insetting the child coordinate
        // space; keep this outline in its own paint node for the same contract.
        let mut edge = at(0., 0., w, h);
        edge.border = UiRect::all(px(2));
        p.spawn((edge, BorderColor::all(rarity), Pickable::IGNORE));
        if facts.money {
            for piece in money::fan(&facts.denominations, size, &mut art.money, root, images) {
                p.spawn((
                    rect(Rect::from_center_size(piece.center, piece.size)),
                    ImageNode::new(piece.image),
                    UiTransform {
                        rotation: Rot2::radians(piece.radians),
                        ..default()
                    },
                    Pickable::IGNORE,
                ));
            }
        } else if let Some((handle, dimensions, wide)) = art.icon(&facts.icon, root, images) {
            let rotated = rotation % 2 == 1;
            let upright = rotated && magazine;
            // A TextureRect's natural unrotated box swaps dimensions before
            // non-mag CW rotation. Magazines share the source guarded transform.
            let area = Rect::from_center_size(
                size * 0.5,
                if rotated && !upright {
                    Vec2::new(h - 16., w - 16.)
                } else {
                    size - Vec2::splat(16.)
                },
            );
            image(
                p,
                handle,
                dimensions,
                area,
                if rotated && (!upright || wide) {
                    std::f32::consts::FRAC_PI_2
                } else {
                    0.
                },
                upright && wide,
            );
        } else {
            label(
                p,
                &facts.name,
                Rect::from_corners(Vec2::ZERO, size),
                if w <= CELL { 9. } else { 12. },
                rarity.mix(&Color::WHITE, 0.35),
                font,
                Justify::Center,
            );
        }
        if count && (facts.money || facts.amount > 1 || magazine) {
            let value = if facts.money {
                format!("${}", facts.amount)
            } else {
                format!("x{}", facts.amount)
            };
            let area = Rect::new(0., h - 20., w - 4., h - 2.);
            // Source's black glyph outline, without changing the text metrics.
            for offset in [
                Vec2::new(-1., 0.),
                Vec2::new(1., 0.),
                Vec2::new(0., -1.),
                Vec2::new(0., 1.),
            ] {
                label(
                    p,
                    &value,
                    Rect::from_corners(area.min + offset, area.max + offset),
                    13.,
                    Color::BLACK,
                    font,
                    Justify::Right,
                );
            }
            label(p, value, area, 13., Color::WHITE, font, Justify::Right);
        }
        if let Some(fill) = &facts.fill {
            let fraction = if fill.capacity > 0. {
                (fill.amount.max(0.) / fill.capacity).clamp(0., 1.)
            } else {
                0.
            };
            p.spawn((
                at(2., h - 10., w - 4., 8.),
                BackgroundColor(Color::srgba(0., 0., 0., 0.5)),
                Pickable::IGNORE,
            ));
            p.spawn((
                at(3., h - 9., w - 6., 6.),
                BackgroundColor(if fill.fuel {
                    Color::srgb(0.32, 0.32, 0.35)
                } else {
                    Color::srgb(0.30, 0.31, 0.34)
                }),
                Pickable::IGNORE,
            ));
            if fraction > 0. {
                p.spawn((
                    at(3., h - 9., (w - 6.) * fraction, 6.),
                    FillBar(fraction),
                    BackgroundColor(color(&fill.color, Color::srgb(0.49, 0.49, 0.49))),
                    Pickable::IGNORE,
                ));
            }
        }
        if facts.active_auto_drink {
            let mut node = at(2., 2., 21., 21.);
            node.border = UiRect::all(px(2));
            node.border_radius = BorderRadius::all(px(7));
            p.spawn((
                node,
                AutoDrink,
                BackgroundColor(Color::srgb(0.25, 0.72, 0.95)),
                BorderColor::all(Color::srgba(0., 0., 0., 0.5)),
                Pickable::IGNORE,
            ))
            .with_child((
                at(2., 1., 4., 4.),
                BackgroundColor(Color::srgba(0.92, 0.98, 1., 0.95)),
                Pickable::IGNORE,
            ));
        }
        if facts.food || (facts.condition && condition_chip) {
            let q = color(&facts.quality_color, Color::WHITE);
            let entity = chip(
                p,
                if facts.broken {
                    "BROKEN".into()
                } else {
                    format!("{}%", facts.quality)
                },
                q.mix(&Color::WHITE, 0.45),
                Color::srgba(0., 0., 0., 0.72),
                q,
                font,
                true,
            );
            p.commands().entity(entity).insert(ConditionChip);
        }
        if facts.food && facts.frozen > 0 {
            chip(
                p,
                format!("❄{}%", facts.frozen),
                Color::srgb(0.55, 0.83, 1.),
                Color::srgba(0.06, 0.15, 0.28, 0.95),
                Color::srgb(0.55, 0.83, 1.),
                font,
                false,
            );
        } else if facts.food && facts.preserved {
            let mut node = at(2., 2., 21., 21.);
            node.border = UiRect::all(px(2));
            node.border_radius = BorderRadius::all(px(3));
            p.spawn((
                node,
                Preservation,
                BackgroundColor(Color::srgb(0.14, 0.44, 0.86)),
                BorderColor::all(Color::srgb(0.05, 0.16, 0.38)),
                Pickable::IGNORE,
            ))
            .with_child((
                at(-2., -2., 21., 21.),
                ImageNode::new(art.snow(images)),
                Pickable::IGNORE,
            ));
        }
    })
    .id()
}
pub(super) fn grid(p: &mut ChildSpawnerCommands, cells: UVec2) {
    for y in 0..cells.y {
        for x in 0..cells.x {
            let mut node = at(
                x as f32 * CELL + 1.,
                y as f32 * CELL + 1.,
                CELL - 2.,
                CELL - 2.,
            );
            node.border = UiRect::all(px(1));
            p.spawn((
                node,
                BackgroundColor(super::super::navbar::neutral(0.30, 0.34, 0.40, 0.34)),
                BorderColor::all(super::super::navbar::neutral(0.55, 0.60, 0.68, 0.45)),
                Pickable::IGNORE,
            ));
        }
    }
}
fn snowflake() -> Image {
    const N: i32 = 40;
    let mut bytes = vec![255; (N * N * 4) as usize];
    for alpha in bytes.iter_mut().skip(3).step_by(4) {
        *alpha = 0;
    }
    let mut dot = |p: Vec2| {
        let p = IVec2::new(p.x.round_ties_even() as i32, p.y.round_ties_even() as i32);
        for oy in 0..=1 {
            for ox in 0..=1 {
                let x = p.x + ox;
                let y = p.y + oy;
                if x >= 0 && y >= 0 && x < N && y < N {
                    let at = ((y * N + x) * 4) as usize;
                    bytes[at..at + 4].copy_from_slice(&[250, 250, 250, 255]);
                }
            }
        }
    };
    let center = Vec2::splat(19.5);
    for a in 0..6 {
        let angle = a as f32 * std::f32::consts::PI / 3.;
        let axis = Vec2::new(angle.cos(), angle.sin());
        for t in 0..=26 {
            dot(center + axis * t as f32 * 0.5);
        }
        for f in [0.5, 0.78] {
            for sign in [1., -1.] {
                let base = center + axis * 13. * f;
                let b = angle + sign * 0.85;
                let ray = Vec2::new(b.cos(), b.sin());
                for t in 0..=8 {
                    dot(base + ray * t as f32 * 0.5);
                }
            }
        }
    }
    money::rgba(N as u32, N as u32, bytes)
}

#[cfg(test)]
mod tests;
