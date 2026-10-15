use super::model::CELL;
use bevy::prelude::*;

pub(super) const HEADER_HEIGHT: f32 = 76.0;
pub(super) const HEADER_GAP: f32 = 86.0;
pub(super) const GRID_PAD: f32 = 30.0;
pub(super) const PAGE_ADVANCE: f32 = 96.0;
pub(super) const CHARACTER_WIDTH: f32 = 616.0;
const MARGIN: f32 = 12.0;
const NAV_HEIGHT: f32 = 60.0;
const BOX_X: f32 = MARGIN + CHARACTER_WIDTH + 8.0;
const BOX_INSET: f32 = 590.0;
const SPLIT_AT: f32 = 1350.0;

#[derive(Debug)]
pub(super) struct Dashboard {
    pub character: Rect,
    pub paperdoll: Rect,
    pub primary: Rect,
    pub secondary: Rect,
    pub cosmetics_y: f32,
    pub storage: Rect,
    pub split: bool,
    pub column_width: f32,
    pub container_x: f32,
}
impl Dashboard {
    /// InventoryUI.Refresh + LayoutDash, using shared HUD.ContentBottom.
    /// BOXINSET is intentionally the active source590, not its stale comment.
    pub fn new(viewport: Vec2) -> Self {
        let top = NAV_HEIGHT + MARGIN;
        let bottom = super::super::vitals::content_bottom(
            viewport,
            MARGIN,
            MARGIN + CHARACTER_WIDTH,
            viewport.y - MARGIN,
        );
        let height = (bottom - top).max(600.0);
        let character = Rect::from_corners(
            Vec2::new(MARGIN, top),
            Vec2::new(MARGIN + CHARACTER_WIDTH, top + height),
        );
        let weapon_y = height - CELL - 24.0 - MARGIN;
        let width = (CHARACTER_WIDTH - 2.0 * MARGIN - 16.0) / 2.0;
        let primary = Rect::from_corners(
            character.min + Vec2::new(12.0, weapon_y + 24.0),
            character.min + Vec2::new(12.0 + width, weapon_y + 24.0 + CELL),
        );
        let secondary = Rect::from_corners(
            primary.min + Vec2::new(width + 16.0, 0.0),
            primary.max + Vec2::new(width + 16.0, 0.0),
        );
        let paperdoll = Rect::from_corners(
            character.min + Vec2::new(0.0, 82.0),
            character.min + Vec2::new(CHARACTER_WIDTH, 82.0 + (weapon_y - 82.0).max(120.0)),
        );
        let box_width = (viewport.x - BOX_INSET).max(0.0);
        let storage_bottom = super::super::vitals::content_bottom(
            viewport,
            BOX_X,
            BOX_X + box_width,
            viewport.y - MARGIN,
        );
        let split = viewport.x >= SPLIT_AT;
        Self {
            character,
            paperdoll,
            primary,
            secondary,
            cosmetics_y: character.min.y + weapon_y - 44.0,
            storage: Rect::from_corners(
                Vec2::new(BOX_X, top),
                Vec2::new(BOX_X + box_width, storage_bottom),
            ),
            split,
            column_width: if split {
                box_width * 0.5 - 5.0
            } else {
                box_width
            },
            container_x: BOX_X + box_width * 0.5 + 5.0,
        }
    }
    pub fn column(&self, pointer: Vec2) -> usize {
        usize::from(self.split && pointer.x >= self.storage.min.x + self.storage.width() * 0.5)
    }
}

pub(super) fn page_advance(rows: u8, folded: bool) -> f32 {
    if folded || rows == 0 {
        HEADER_HEIGHT + 10.0
    } else {
        f32::from(rows) * CELL + GRID_PAD + PAGE_ADVANCE
    }
}

#[derive(Debug, Default)]
pub(super) struct Scroll {
    pub offset: [f32; 2],
    pub content: [f32; 2],
}
impl Scroll {
    pub fn clamp(&mut self, dashboard: &Dashboard) {
        for col in 0..2 {
            let maximum = if col == 1 && !dashboard.split {
                0.0
            } else {
                (self.content[col] - dashboard.storage.height()).max(0.0)
            };
            self.offset[col] = self.offset[col].clamp(0.0, maximum);
        }
    }
    /// Wheel events are handled over the whole panel, including its buttons.
    pub fn wheel(&mut self, dashboard: &Dashboard, pointer: Vec2, notches: f32) -> bool {
        if !dashboard.storage.contains(pointer) {
            return false;
        }
        let col = dashboard.column(pointer);
        if self.content[col] <= dashboard.storage.height() {
            return false;
        }
        self.offset[col] -= notches * 60.0;
        self.clamp(dashboard);
        true
    }
}

pub(super) fn split_marks(total: u16) -> Vec<(u16, &'static str)> {
    let mut result = Vec::new();
    for (value, label) in [(total / 4, "1/4"), (total / 3, "1/3"), (total / 2, "1/2")] {
        if value > 0 && value < total && !result.iter().any(|(v, _)| *v == value) {
            result.push((value, label));
        }
    }
    result
}
