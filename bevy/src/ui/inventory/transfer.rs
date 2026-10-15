use super::super::InventoryPage;
use super::model::*;
use std::collections::VecDeque;

pub(super) const TRANSFER_SECONDS: f32 = 0.35;

#[derive(Default, Debug)]
pub(super) struct Transfers {
    pub queue: VecDeque<(Cell, u16)>,
    head: Option<Cell>,
    elapsed: f32,
}
impl Transfers {
    pub fn clear(&mut self) {
        *self = Self::default();
    }
    pub fn progress(&self) -> f32 {
        (self.elapsed / TRANSFER_SECONDS).clamp(0.0, 1.0)
    }
    /// Original TickQuickTransfer: H collects; queued work survives key release.
    /// A manual drag removes just that source, and at most one transfer drains
    /// per rendered frame even if the frame takes longer than the whole queue.
    pub fn step(
        &mut self,
        pages: &[InventoryPage],
        dt: f32,
        context_open: bool,
        held: bool,
        hover: Option<(Cell, bool)>,
        carry: Option<Source>,
    ) -> Option<Intent> {
        if !open_storage(pages) || context_open {
            self.clear();
            return None;
        }
        if let Some(Source::Grid(cell)) = carry {
            if self.queue.front().is_some_and(|q| q.0 == cell) {
                self.head = None;
                self.elapsed = 0.0;
            }
            self.queue.retain(|q| q.0 != cell);
        }
        if held
            && carry.is_none()
            && let Some((cell, false)) = hover
            && cell.page != AREA
            && (cell.page < OWN_PAGES || container(cell.page))
            && let Some(item) = at(pages, cell)
        {
            let origin = Cell::item(cell.page, item);
            if !self.queue.iter().any(|q| q.0 == origin) {
                self.queue.push_back((origin, item.id));
            }
        }
        while let Some(&(cell, id)) = self.queue.front() {
            if at(pages, cell).is_none_or(|item| item.id != id) {
                self.queue.pop_front();
                self.elapsed = 0.0;
                continue;
            }
            if self.head != Some(cell) {
                self.head = Some(cell);
                self.elapsed = 0.0;
            }
            self.elapsed += dt.max(0.0);
            if self.elapsed < TRANSFER_SECONDS {
                return None;
            }
            self.queue.pop_front();
            self.head = None;
            self.elapsed = 0.0;
            return Some(Intent::QuickTransfer(cell));
        }
        self.head = None;
        self.elapsed = 0.0;
        None
    }
}
