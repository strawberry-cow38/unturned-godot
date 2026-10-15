//! Read-only source UI facts from the accepted owner bag. Placement options are
//! suggestions; every move, split and selection still crosses server admission.
use super::model::*;
use serde::Deserialize;

#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(default, rename_all = "camelCase")]
pub(super) struct View {
    pub accepted_tick: u32,
    pub items: Vec<Item>,
    pub quick_crafts: Vec<QuickCraft>,
    pub cooker: Option<Cooker>,
    pub load: Option<Load>,
    pub tiles: Tiles,
}

#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(default)]
pub(super) struct Tiles {
    pub cells: Vec<TileCell>,
    pub worn: Vec<Worn>,
}
#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(default)]
pub(super) struct TileCell {
    pub page: u8,
    pub x: u8,
    pub y: u8,
    pub tile: Tile,
}
#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(default)]
pub(super) struct Worn {
    pub slot: u8,
    pub tile: Option<Tile>,
}
#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(default, rename_all = "camelCase")]
pub(super) struct Tile {
    pub id: u16,
    pub name: String,
    #[serde(deserialize_with = "super::super::null_string")]
    pub icon: String,
    pub width: u8,
    pub height: u8,
    pub amount: u16,
    pub quality: u8,
    pub rarity_color: Vec<f32>,
    pub condition: bool,
    pub food: bool,
    pub broken: bool,
    pub quality_color: Vec<f32>,
    pub frozen: u8,
    pub preserved: bool,
    pub fill: Option<Fill>,
    pub active_auto_drink: bool,
    pub money: bool,
    pub denominations: Vec<Denomination>,
}
#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(default)]
pub(super) struct Fill {
    pub amount: f32,
    pub capacity: f32,
    pub color: Vec<f32>,
    pub fuel: bool,
}
#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(default)]
pub(super) struct Denomination {
    pub id: u16,
    pub value: i32,
    #[serde(deserialize_with = "super::super::null_string")]
    pub icon: String,
}
#[derive(Clone, Copy, Debug, Default, Deserialize, PartialEq, Eq)]
#[serde(default)]
pub(super) struct Address {
    pub page: u8,
    pub x: u8,
    pub y: u8,
    pub id: u16,
    pub token: u64,
}
impl Address {
    pub fn cell(self) -> Cell {
        Cell::at(self.page, self.x, self.y)
    }
}
#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(default, rename_all = "camelCase")]
pub(super) struct Item {
    pub address: Address,
    pub name: String,
    pub description: String,
    pub info: String,
    pub rarity_color: Vec<f32>,
    pub condition: String,
    pub condition_color: Vec<f32>,
    pub clothing_slot: Option<u8>,
    pub hand_action: String,
    pub equip_slot: i32,
    pub loose_ammo: bool,
    pub magazine: bool,
    pub magazine_capacity: i32,
    pub round_id: u16,
    pub loaded_round: String,
    pub fuel: f32,
    pub fluid: Option<Fluid>,
    pub actions: Vec<Button>,
    pub store_page: u8,
    pub paperdoll_take: Option<Destination>,
    pub equip: Option<EquipPlan>,
}
impl Item {
    pub fn rules(&self) -> ItemRules {
        ItemRules {
            clothing: self.clothing_slot,
            weapon: self.equip.is_some(),
            hand_action: !self.hand_action.is_empty(),
        }
    }
}
#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(default)]
pub(super) struct Button {
    pub action: String,
    pub label: String,
    pub tooltip: String,
    pub recipe: i32,
}
#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(default, rename_all = "camelCase")]
pub(super) struct Fluid {
    #[serde(rename = "type")]
    pub kind: u8,
    pub quality: u8,
    pub amount: f32,
    pub capacity: f32,
    pub auto_drink: bool,
    pub contents: String,
    pub color: Vec<f32>,
}
#[derive(Clone, Copy, Debug, Default, Deserialize, PartialEq, Eq)]
#[serde(default)]
pub(super) struct Destination {
    pub page: u8,
    pub x: u8,
    pub y: u8,
    pub rotation: u8,
}
impl Destination {
    pub fn cell(self) -> Cell {
        Cell::at(self.page, self.x, self.y)
    }
}
#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(default, rename_all = "camelCase")]
pub(super) struct EquipPlan {
    pub slot: u8,
    pub displaced: Option<Address>,
    pub displaced_to: Option<Destination>,
    pub drop_displaced: bool,
}
#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(default, rename_all = "camelCase")]
pub(super) struct QuickCraft {
    pub index: u16,
    pub name: String,
    pub item_id: u16,
    #[serde(deserialize_with = "super::super::null_string")]
    pub icon: String,
}
#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(default, rename_all = "camelCase")]
pub(super) struct Cooker {
    pub net_id: u32,
    pub kind: String,
    pub on: bool,
    pub fuel: f32,
    pub needs_fuel: bool,
}
#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(default, rename_all = "camelCase")]
pub(super) struct Load {
    pub consumed: bool,
    pub allowed: bool,
    pub message: String,
    pub batch: u16,
    pub round_id: u16,
}
impl View {
    pub fn tile(&self, cell: Cell) -> Option<&Tile> {
        self.tiles
            .cells
            .iter()
            .find(|t| Cell::at(t.page, t.x, t.y) == cell)
            .map(|t| &t.tile)
    }
    pub fn at(&self, cell: Cell) -> Option<&Item> {
        self.items.iter().find(|i| i.address.cell() == cell)
    }
    pub fn rules(&self, id: u16) -> ItemRules {
        self.items
            .iter()
            .find(|i| i.address.id == id)
            .map_or(Default::default(), Item::rules)
    }
}
