//! Source gesture dispatch. This module returns commands and pending-selection
//! state; it never edits the accepted inventory used to draw the dashboard.
use super::super::{InventoryPage, UiIntent};
use super::{api, model::*};
use serde_json::{Value, json};

pub(super) fn action(name: &str, args: Value) -> UiIntent {
    UiIntent::Action {
        name: name.into(),
        args,
    }
}
pub(super) fn cell_args(cell: Cell) -> Value {
    json!({"page":cell.page,"x":cell.x,"y":cell.y})
}
pub(super) fn move_item(from: Cell, to: Cell, rotation: u8) -> UiIntent {
    action(
        "moveItem",
        json!({"page0":from.page,"x0":from.x,"y0":from.y,"page1":to.page,"x1":to.x,"y1":to.y,"rot1":rotation}),
    )
}
pub(super) fn select(address: api::Address) -> UiIntent {
    action(
        "selectHeld",
        json!({"page":address.page,"x":address.x,"y":address.y,"itemId":address.id,"token":address.token,"equip":false}),
    )
}
pub(super) fn dequip() -> UiIntent {
    select(api::Address::default())
}
pub(super) fn exact(action_name: &str, address: api::Address) -> UiIntent {
    action(
        action_name,
        json!({"page":address.page,"x":address.x,"y":address.y,"id":address.id,"token":address.token}),
    )
}
// InventoryUI's authored worn-slot order differs from WearClothingCommand's
// existing wire order. Ask the shared clothing mapping, rather than casting it.
pub(super) fn worn_wire(slot: u8) -> Option<u8> {
    const NAMES: [&str; 7] = [
        "hat", "glasses", "mask", "shirt", "vest", "backpack", "pants",
    ];
    NAMES
        .get(usize::from(slot))
        .and_then(|name| super::super::clothing::slot(name))
}
#[derive(Clone, Debug)]
pub(super) struct PendingEquip {
    pub cell: Cell,
    pub id: u16,
    pub token: u64,
}
impl PendingEquip {
    pub fn accepted(&self, view: &api::View) -> Option<UiIntent> {
        let row = view.at(self.cell)?;
        // Container pages have no owner token until the item enters the bag.
        // Source ArmSlotEquip waits for the accepted id at the requested slot.
        if row.address.id != self.id
            || row.address.token == 0
            || (self.token != 0 && self.token != row.address.token)
        {
            return None;
        }
        Some(select(row.address))
    }
}
#[derive(Default)]
pub(super) struct Dispatch {
    pub commands: Vec<UiIntent>,
    pub pending: Option<PendingEquip>,
    pub select_token: Option<u64>,
}
fn equip(from: Cell, row: &api::Item) -> Dispatch {
    let mut out = Dispatch::default();
    if row.hand_action == "Dequip" {
        out.commands.push(dequip());
        return out;
    }
    if row.hand_action.is_empty() {
        return out;
    }
    if let Some(plan) = &row.equip {
        let to = Cell::at(plan.slot, 0, 0);
        if from == to {
            out.commands.push(select(row.address));
            out.select_token = Some(row.address.token);
            return out;
        }
        if let Some(old) = plan.displaced {
            if let Some(dest) = plan.displaced_to {
                out.commands
                    .push(move_item(old.cell(), dest.cell(), dest.rotation));
            } else if plan.drop_displaced {
                out.commands.push(action("dropItem", cell_args(old.cell())));
            } else {
                return out;
            }
        }
        out.commands.push(move_item(from, to, 0));
        out.pending = Some(PendingEquip {
            cell: to,
            id: row.address.id,
            token: row.address.token,
        });
    } else if from.page < OWN_PAGES {
        out.commands.push(select(row.address));
        out.select_token = Some(row.address.token);
    } else if let Some(to) = row.paperdoll_take {
        out.commands.push(move_item(from, to.cell(), to.rotation));
        out.pending = Some(PendingEquip {
            cell: to.cell(),
            id: row.address.id,
            token: row.address.token,
        });
    }
    out
}
pub(super) fn dispatch(intent: Intent, view: &api::View, pages: &[InventoryPage]) -> Dispatch {
    let mut out = Dispatch::default();
    match intent {
        Intent::Move{from,to,rotation} => out.commands.push(move_item(from,to,rotation)),
        Intent::Split{from,amount,to,rotation} => out.commands.push(action("splitItem",json!({"page":from.page,"x":from.x,"y":from.y,"amount":amount,"toPage":to.page,"toX":to.x,"toY":to.y,"toRot":rotation}))),
        Intent::Drop(from) => out.commands.push(action("dropItem",cell_args(from))),
        Intent::Wear{from,slot} => if let Some(slot)=worn_wire(slot) {out.commands.push(action("wearClothing",json!({"page":from.page,"x":from.x,"y":from.y,"slot":slot})));},
        Intent::Unwear{slot,to} => if let Some(slot)=worn_wire(slot) {out.commands.push(action("unwearClothing",json!({"slot":slot,"page":to.page,"x":to.x,"y":to.y})));},
        Intent::Equip(from) => if let Some(row)=view.at(from) {return equip(from,row);},
        Intent::QuickTransfer(from) => if let Some(row)=view.at(from) {
            if open_storage(pages) || from.page==AREA {
                let to_page=if container(from.page)||from.page==AREA {ANY_PAGE} else {row.store_page};
                out.commands.push(action("quickTransfer",json!({"page":from.page,"x":from.x,"y":from.y,"toPage":to_page})));
            } else if let Some(slot)=row.clothing_slot.and_then(worn_wire) {
                out.commands.push(action("wearClothing",json!({"page":from.page,"x":from.x,"y":from.y,"slot":slot})));
            }
        },
        Intent::TakeToOwn(from) => out.commands.push(action("quickTransfer",json!({"page":from.page,"x":from.x,"y":from.y,"toPage":ANY_PAGE}))),
        Intent::TakeToPaperdoll(from) => if let Some(to)=view.at(from).and_then(|r|r.paperdoll_take) {out.commands.push(move_item(from,to.cell(),to.rotation));},
        Intent::Craft{index,quantity} => for _ in 0..quantity {out.commands.push(action("craft",json!({"index":index})));},
        // MagRules, lookup and local key bindings have their own lifecycle.
        Intent::LoadOrMove{..}|Intent::Lookup{..}|Intent::Bind{..} => {}
    }
    out
}
pub(super) fn context_refusal(name: &str, row: &api::Item) -> Option<&'static str> {
    if matches!(name, "emptyFuel" | "salvage")
        && (row.address.page >= OWN_PAGES || row.address.token == 0)
    {
        Some("Take this item into your inventory first.")
    } else {
        None
    }
}
pub(super) fn context(
    name: &str,
    row: &api::Item,
    view: &api::View,
    pages: &[InventoryPage],
) -> Dispatch {
    if context_refusal(name, row).is_some() {
        return Dispatch::default();
    }
    let from = row.address.cell();
    match name {
        "equip" | "hold" | "dequip" => equip(from, row),
        "transfer" => dispatch(Intent::QuickTransfer(from), view, pages),
        "drop" => dispatch(Intent::Drop(from), view, pages),
        "emptyFuel" | "salvage" => Dispatch {
            commands: vec![exact(
                if name == "salvage" {
                    "salvageItem"
                } else {
                    name
                },
                row.address,
            )],
            ..Default::default()
        },
        "learn" => Dispatch {
            commands: vec![action("consume", cell_args(from))],
            ..Default::default()
        },
        "autoDrink" => Dispatch {
            commands: vec![action(
                "setAutoDrink",
                json!({"page":from.page,"x":from.x,"y":from.y,"id":row.address.id,"autoDrink":!row.fluid.as_ref().is_some_and(|f|f.auto_drink)}),
            )],
            ..Default::default()
        },
        _ => Dispatch::default(),
    }
}
