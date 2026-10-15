//! MapUI.cs geometry. Map nodes are already in engine coordinates: do not flip Z.
use bevy::prelude::*;

pub(super) const LEVEL_SIZE: f32 = 1920.;
pub(super) fn normalized(position: Vec3) -> Vec2 {
    position.xz() / LEVEL_SIZE + Vec2::splat(0.5)
}
#[derive(Clone, Copy, Debug, PartialEq)]
pub(super) struct Layout {
    pub map: Rect,
    pub quests: Rect,
    pub roster: Rect,
}
impl Layout {
    pub fn new(viewport: Vec2) -> Self {
        let bottom = viewport.y - 32.;
        let top = 116.;
        let mut side = (bottom - top).min((viewport.x - 64. - 540.).max(200.));
        if side < 64. {
            side = (bottom - top).max(64.);
        }
        let x = viewport.x - 32. - side;
        let left_width = (x - 48.).max(160.);
        let left_height = (bottom - top).max(120.);
        // Source58% split runs the entire roster behind the seven-row vitals
        // at720p. Preserve it when it fits; otherwise reserve the header and
        // two30px rows above the same HUD.ContentBottom used by every menu.
        let roster_bottom =
            super::super::vitals::content_bottom(viewport, 32., 32. + left_width, bottom);
        let quest_height = (left_height * 0.58)
            .round_ties_even()
            .min((roster_bottom - top - 114.).max(128.));
        Self {
            map: Rect::from_corners(Vec2::new(x, top), Vec2::new(x + side, top + side)),
            quests: Rect::from_corners(
                Vec2::new(32., top),
                Vec2::new(32. + left_width, top + (quest_height - 8.).max(120.)),
            ),
            roster: Rect::from_corners(
                Vec2::new(32., top + quest_height),
                Vec2::new(
                    32. + left_width,
                    top + quest_height + (left_height - quest_height).max(100.),
                ),
            ),
        }
    }
}
#[derive(Clone, Debug, Default, PartialEq)]
pub(super) struct Marker {
    pub norm: Vec2,
    pub serial: u32,
}
#[derive(Clone, Debug)]
pub(super) struct View {
    pub zoom: f32,
    pub pan: Vec2,
    pub markers: Vec<Marker>,
    serial: u32,
}
impl Default for View {
    fn default() -> Self {
        Self {
            zoom: 1.,
            pan: Vec2::ZERO,
            markers: vec![],
            serial: 0,
        }
    }
}
impl View {
    pub fn clamp(&mut self, side: f32) {
        self.zoom = self.zoom.clamp(1., 8.);
        self.pan = self
            .pan
            .clamp(Vec2::splat(side - side * self.zoom), Vec2::ZERO);
    }
    pub fn zoom_at(&mut self, local: Vec2, factor: f32, side: f32) {
        let norm = (local - self.pan) / (side * self.zoom);
        self.zoom = (self.zoom * factor).clamp(1., 8.);
        self.pan = local - norm * (side * self.zoom);
        self.clamp(side);
    }
    pub fn marker(&mut self, local: Vec2, side: f32) {
        let norm = (local - self.pan) / (side * self.zoom);
        if norm.cmplt(Vec2::ZERO).any() || norm.cmpgt(Vec2::ONE).any() {
            return;
        }
        if let Some(index) = self
            .markers
            .iter()
            .position(|m| ((m.norm - norm) * side * self.zoom).length() <= 14.)
        {
            self.markers.remove(index);
        } else {
            self.serial += 1;
            self.markers.push(Marker {
                norm,
                serial: self.serial,
            });
        }
    }
    pub fn tile_visible(&self, row: usize, col: usize, side: f32) -> bool {
        if self.zoom < 2. {
            return false;
        }
        let tile = side * self.zoom / 4.;
        let window = Rect::from_corners(
            -self.pan - Vec2::splat(tile * 0.25),
            -self.pan + Vec2::splat(side + tile * 0.25),
        );
        let at = Vec2::new(col as f32, row as f32) * tile;
        intersects(window, Rect::from_corners(at, at + Vec2::splat(tile)))
    }
}
pub(super) fn intersects(a: Rect, b: Rect) -> bool {
    a.min.x < b.max.x && a.max.x > b.min.x && a.min.y < b.max.y && a.max.y > b.min.y
}
/// Greedy source label pass: callers supply markers, peers, then towns.
pub(super) fn label(
    anchor: Vec2,
    size: Vec2,
    pip: f32,
    window: Rect,
    placed: &mut Vec<Rect>,
) -> Option<Vec2> {
    let gap = pip * 0.5 + 4.;
    let mut at = anchor + Vec2::new(gap, -size.y * 0.5);
    if at.x + size.x > window.max.x {
        at.x = anchor.x - gap - size.x;
    }
    let rect = Rect::from_corners(at, at + size);
    if !intersects(window, rect) || placed.iter().any(|r| intersects(*r, rect)) {
        return None;
    }
    placed.push(Rect::from_corners(
        rect.min - Vec2::splat(3.),
        rect.max + Vec2::splat(3.),
    ));
    Some(at)
}
pub(super) fn facing(rotation: Quat) -> f32 {
    let forward = rotation * Vec3::NEG_Z;
    forward.x.atan2(-forward.z)
}
