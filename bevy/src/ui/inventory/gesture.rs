use super::super::{InventoryItem, InventoryPage};
use super::model::*;
use bevy::prelude::*;

const RMB_SLOP: f32 = 6.0;

#[derive(Clone, Debug)]
pub(super) struct Carry {
    pub source: Source,
    pub item: InventoryItem,
    pub rotation: u8,
    pub grab: Vec2,
    pub split: Option<(u16, bool)>,
}
#[derive(Debug)]
pub(super) struct Gesture {
    pub carry: Option<Carry>,
    pub selected: Option<Cell>,
    armed: Option<(Cell, Vec2)>,
    pub split_amount: u16,
    pub spinning: bool,
    pub paperdoll_yaw: f32,
    pub pointer: Vec2,
    /// Source inventory foley occurs on grab, rotate, cancel and ordinary drop.
    pub foley: Vec<(u8, u8)>,
}
impl Default for Gesture {
    fn default() -> Self {
        Self {
            carry: None,
            selected: None,
            armed: None,
            split_amount: 0,
            spinning: false,
            paperdoll_yaw: 0.5,
            pointer: Vec2::ZERO,
            foley: vec![],
        }
    }
}
#[derive(Clone, Copy, Debug)]
pub(super) enum Edge {
    Left { down: bool, control: bool },
    Right { down: bool, control: bool },
    Motion { delta: Vec2 },
    Rotate,
    Lookup { uses: bool },
    Bind(u8),
}
impl Gesture {
    pub fn close(&mut self) {
        self.carry = None;
        self.selected = None;
        self.armed = None;
        self.spinning = false;
    }
    fn sound(&mut self, item: &InventoryItem) {
        self.foley.push((item.width, item.height));
    }
    fn select(&mut self, pages: &[InventoryPage], cell: Cell) {
        self.selected = at(pages, cell).map(|item| Cell::item(cell.page, item));
        self.split_amount = at(pages, cell).map_or(0, |item| item.amount / 2);
    }
    fn split(&mut self, pages: &[InventoryPage], cell: Cell, count: u16, right: bool) {
        let Some(item) = at(pages, cell).filter(|i| i.amount > 1) else {
            return;
        };
        let mut ghost = item.clone();
        ghost.amount = count.clamp(1, item.amount - 1);
        self.sound(item);
        self.carry = Some(Carry {
            source: Source::Grid(Cell::item(cell.page, item)),
            rotation: 0,
            grab: Vec2::splat(CELL / 2.0),
            split: Some((ghost.amount, right)),
            item: ghost,
        });
        self.selected = None;
    }
    /// Process ordered input edges. The UI adapter supplies final visible control
    /// rectangles; buttons remain owned by ButtonPlugin, never stolen by drag.
    pub fn input(
        &mut self,
        edge: Edge,
        point: Vec2,
        layout: &HitLayout,
        pages: &[InventoryPage],
        worn: &[Option<InventoryItem>; 7],
        rules: impl Fn(u16) -> ItemRules,
    ) -> Vec<Intent> {
        self.pointer = point;
        if matches!(edge, Edge::Left { .. } | Edge::Right { .. })
            && layout.buttons.iter().any(|r| r.contains(point))
        {
            return vec![];
        }
        match edge {
            Edge::Left {
                down: true,
                control,
            } => {
                if self.carry.is_none()
                    && layout.split_preview.is_some_and(|r| r.contains(point))
                    && let Some(cell) = self.selected
                {
                    self.split(pages, cell, self.split_amount, false);
                    return vec![];
                }
                if self.selected.is_some() {
                    if layout.context.is_some_and(|r| r.contains(point)) {
                        return vec![];
                    }
                    self.selected = None;
                }
                if let Some((index, _)) = layout.quick_craft.iter().find(|(_, r)| r.contains(point))
                {
                    return vec![Intent::Craft {
                        index: *index,
                        quantity: 1,
                    }];
                }
                if self.carry.is_some() {
                    return vec![];
                }
                if control
                    && let Some((cell, _)) = layout.cell(point)
                    && let Some(item) = at(pages, cell)
                {
                    let from = Cell::item(cell.page, item);
                    return vec![if from.page == AREA {
                        Intent::TakeToOwn(from)
                    } else {
                        Intent::Drop(from)
                    }];
                }
                if layout.paperdoll(point) {
                    self.spinning = true;
                    return vec![];
                }
                if let Some((slot, rect)) = layout.worn(point)
                    && let Some(item) = worn.get(usize::from(slot)).and_then(Option::as_ref)
                {
                    self.carry = Some(Carry {
                        source: Source::Worn(slot),
                        item: item.clone(),
                        rotation: 0,
                        grab: point - rect.min,
                        split: None,
                    });
                    return vec![];
                }
                if let Some((cell, grid)) = layout.cell(point)
                    && let Some(item) = at(pages, cell)
                {
                    let size = footprint(item, item.rot).as_vec2() * CELL;
                    let grab = if grid.slot {
                        (point - (grid.rect.min + (grid.rect.size() - size) / 2.0))
                            .clamp(Vec2::splat(CELL * 0.25), size - Vec2::splat(CELL * 0.25))
                    } else {
                        point - grid.rect.min - Vec2::new(item.x.into(), item.y.into()) * CELL
                    };
                    self.sound(item);
                    self.carry = Some(Carry {
                        source: Source::Grid(Cell::item(cell.page, item)),
                        item: item.clone(),
                        rotation: item.rot,
                        grab,
                        split: None,
                    });
                }
            }
            Edge::Left { down: false, .. } => {
                if self.spinning {
                    self.spinning = false;
                } else {
                    return self.drop(point, layout, rules);
                }
            }
            Edge::Right {
                down: true,
                control,
            } => {
                if let Some(carry) = self.carry.take() {
                    self.sound(&carry.item);
                    return vec![];
                }
                if let Some((index, _)) = layout.quick_craft.iter().find(|(_, r)| r.contains(point))
                {
                    return vec![Intent::Craft {
                        index: *index,
                        quantity: 5,
                    }];
                }
                self.selected = None;
                if let Some((cell, _)) = layout.cell(point)
                    && let Some(item) = at(pages, cell)
                {
                    let origin = Cell::item(cell.page, item);
                    if control
                        && (open_storage(pages)
                            || cell.page == AREA
                            || rules(item.id).clothing.is_some())
                    {
                        return vec![Intent::QuickTransfer(origin)];
                    }
                    if item.amount > 1 {
                        self.armed = Some((origin, point));
                    } else {
                        self.select(pages, origin);
                    }
                }
            }
            Edge::Right { down: false, .. } => {
                if self
                    .carry
                    .as_ref()
                    .is_some_and(|c| c.split.is_some_and(|(_, right)| right))
                {
                    return self.drop(point, layout, rules);
                }
                if let Some((cell, _)) = self.armed.take() {
                    self.select(pages, cell);
                }
            }
            Edge::Motion { delta } => {
                if let Some((cell, start)) = self.armed
                    && point.distance(start) > RMB_SLOP
                {
                    self.armed = None;
                    self.split(pages, cell, 1, true);
                } else if self.spinning {
                    self.paperdoll_yaw += delta.x * 0.012;
                }
            }
            Edge::Rotate => {
                if let Some(carry) = &mut self.carry {
                    carry.rotation = (carry.rotation + 1) % 4;
                    self.foley.push((carry.item.width, carry.item.height));
                }
            }
            Edge::Lookup { uses } if self.carry.is_none() => {
                let item = layout
                    .cell(point)
                    .and_then(|(cell, _)| at(pages, cell))
                    .or_else(|| {
                        layout
                            .worn(point)
                            .and_then(|(slot, _)| worn[usize::from(slot)].as_ref())
                    });
                if let Some(item) = item {
                    return vec![Intent::Lookup { id: item.id, uses }];
                }
            }
            Edge::Bind(hotbar) if (3..=10).contains(&hotbar) => {
                if let Some(from) = self.selected.take() {
                    return vec![Intent::Bind { from, hotbar }];
                }
            }
            _ => {}
        }
        vec![]
    }
    fn drop(
        &mut self,
        point: Vec2,
        layout: &HitLayout,
        rules: impl Fn(u16) -> ItemRules,
    ) -> Vec<Intent> {
        let Some(carry) = self.carry.take() else {
            return vec![];
        };
        if let Some((amount, _)) = carry.split {
            if let Source::Grid(from) = carry.source
                && let Some((to, _)) = layout.cell(point)
            {
                self.sound(&carry.item);
                // Original DoSplitTo always requests rot0, even if its transient
                // preview was rotated. Ordinary whole-item drops retain all4.
                return vec![Intent::Split {
                    from,
                    amount,
                    to,
                    rotation: 0,
                }];
            }
            return vec![];
        }
        self.sound(&carry.item);
        let rule = rules(carry.item.id);
        let top_left = point - carry.grab + Vec2::splat(CELL / 2.0);
        let clothing = layout
            .worn(point)
            .map(|(slot, _)| slot)
            .or_else(|| layout.paperdoll(point).then_some(rule.clothing).flatten());
        if let Some(slot) = clothing {
            return if let Source::Grid(from) = carry.source
                && rule.clothing == Some(slot)
            {
                vec![Intent::Wear { from, slot }]
            } else {
                vec![]
            };
        }
        if let Source::Worn(slot) = carry.source {
            let to = layout
                .cell(top_left)
                .map_or(Cell::at(ANY_PAGE, 0, 0), |(cell, _)| cell);
            return vec![Intent::Unwear { slot, to }];
        }
        let Source::Grid(from) = carry.source else {
            unreachable!()
        };
        if layout.paperdoll(point) && from.page >= 2 && from.page != AREA {
            if rule.weapon {
                return vec![Intent::Equip(from)];
            }
            if container(from.page) {
                return vec![Intent::TakeToPaperdoll(from)];
            }
            if rule.hand_action {
                return vec![Intent::Equip(from)];
            }
        }
        if let Some((to, _)) = layout.cell(top_left) {
            if to != from {
                return vec![Intent::LoadOrMove {
                    from,
                    to,
                    rotation: carry.rotation,
                }];
            }
        } else if point.x >= layout.viewport.x / 2.0 && from.page != AREA {
            return vec![Intent::Drop(from)];
        }
        vec![]
    }
}
