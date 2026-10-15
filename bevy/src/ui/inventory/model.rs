use super::super::{InventoryItem, InventoryPage};
use bevy::prelude::*;

pub(super) const CELL: f32 = 72.0;
pub(super) const OWN_PAGES: u8 = 7;
pub(super) const STORAGE: u8 = 7;
pub(super) const AREA: u8 = 8;
pub(super) const ANY_PAGE: u8 = 255;

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq, Hash)]
pub(super) struct Cell {
    pub page: u8,
    pub x: u8,
    pub y: u8,
}
impl Cell {
    pub fn at(page: u8, x: u8, y: u8) -> Self {
        Self { page, x, y }
    }
    pub fn item(page: u8, item: &InventoryItem) -> Self {
        Self::at(page, item.x, item.y)
    }
}

pub(super) fn container(page: u8) -> bool {
    page == STORAGE || page == 9 || (10..22).contains(&page)
}

/// Presentation hit testing uses the accepted footprint. Admission and merges
/// remain PlayerInventory.TryDrag/TryQuickTransfer on the server.
pub(super) fn footprint(item: &InventoryItem, rotation: u8) -> UVec2 {
    let size = UVec2::new(item.width.max(1).into(), item.height.max(1).into());
    if rotation % 2 == 0 {
        size
    } else {
        UVec2::new(size.y, size.x)
    }
}
pub(super) fn at(pages: &[InventoryPage], cell: Cell) -> Option<&InventoryItem> {
    pages
        .iter()
        .find(|p| p.index == cell.page)?
        .items
        .iter()
        .find(|item| {
            let size = footprint(item, item.rot);
            u32::from(cell.x) >= u32::from(item.x)
                && u32::from(cell.y) >= u32::from(item.y)
                && u32::from(cell.x) < u32::from(item.x) + size.x
                && u32::from(cell.y) < u32::from(item.y) + size.y
        })
}
pub(super) fn open_storage(pages: &[InventoryPage]) -> bool {
    pages
        .iter()
        .any(|p| p.index == STORAGE && p.width > 0 && p.height > 0)
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) enum Source {
    Grid(Cell),
    Worn(u8),
}

#[derive(Clone, Debug, PartialEq)]
pub(super) enum Intent {
    Move {
        from: Cell,
        to: Cell,
        rotation: u8,
    },
    Split {
        from: Cell,
        amount: u16,
        to: Cell,
        rotation: u8,
    },
    Drop(Cell),
    Wear {
        from: Cell,
        slot: u8,
    },
    Unwear {
        slot: u8,
        to: Cell,
    },
    /// Resolve the original hand/slot dispatch against accepted item metadata.
    Equip(Cell),
    /// Source QuickAction chooses StoreTarget or first-own-page server merge.
    QuickTransfer(Cell),
    /// Ctrl+LMB on Nearby always takes to the owner, even with a crate open.
    TakeToOwn(Cell),
    /// Paperdoll take uses first unrotated free own cell, not stack merging.
    TakeToPaperdoll(Cell),
    /// The adapter resolves MagRules before issuing any round commands.
    LoadOrMove {
        from: Cell,
        to: Cell,
        rotation: u8,
    },
    Craft {
        index: u16,
        quantity: u8,
    },
    Lookup {
        id: u16,
        uses: bool,
    },
    Bind {
        from: Cell,
        hotbar: u8,
    },
}

#[derive(Clone, Copy, Debug)]
pub(super) struct Grid {
    pub page: u8,
    pub rect: Rect,
    pub clip: Rect,
    pub slot: bool,
}
impl Grid {
    pub fn cell(&self, point: Vec2) -> Option<Cell> {
        // Godot Rect2.HasPoint excludes the trailing edge.
        let contains = |r: Rect| point.cmpge(r.min).all() && point.cmplt(r.max).all();
        if !contains(self.rect) || !contains(self.clip) {
            return None;
        }
        let xy = if self.slot {
            Vec2::ZERO
        } else {
            (point - self.rect.min) / CELL
        };
        Some(Cell::at(self.page, xy.x.floor() as u8, xy.y.floor() as u8))
    }
}

#[derive(Clone, Copy, Debug, Default)]
pub(super) struct ItemRules {
    pub clothing: Option<u8>,
    pub weapon: bool,
    pub hand_action: bool,
}

#[derive(Default)]
pub(super) struct HitLayout {
    pub grids: Vec<Grid>,
    pub worn: Vec<(u8, Rect)>,
    pub header_icons: Vec<(u8, Rect)>,
    pub buttons: Vec<Rect>,
    pub paperdoll: Option<Rect>,
    pub context: Option<Rect>,
    pub split_preview: Option<Rect>,
    pub number: Option<Rect>,
    pub quick_craft: Vec<(u16, Rect)>,
    pub tooltips: Vec<(Rect, String)>,
    pub viewport: Vec2,
}
impl HitLayout {
    pub fn cell(&self, point: Vec2) -> Option<(Cell, Grid)> {
        self.grids
            .iter()
            .find_map(|grid| grid.cell(point).map(|cell| (cell, *grid)))
    }
    pub fn paperdoll(&self, point: Vec2) -> bool {
        self.paperdoll.is_some_and(|r| r.contains(point))
    }
    pub fn worn(&self, point: Vec2) -> Option<(u8, Rect)> {
        self.header_icons
            .iter()
            .chain(&self.worn)
            .copied()
            .find(|(_, rect)| rect.contains(point))
    }
}
