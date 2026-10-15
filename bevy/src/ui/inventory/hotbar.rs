//! Source 3..10 binds retain cell+item ID and expire when that item moves.
use super::{api, commands, controller::Menu, model::*};
use crate::{
    bridge::CoreBridge,
    settings::{GameAction, Settings},
    ui::{Screen, UiIntent, UiModel, UiState},
};
use bevy::prelude::*;
use serde_json::json;
pub(super) fn input(
    ui: Res<UiState>,
    mut model: ResMut<UiModel>,
    settings: Res<Settings>,
    keys: Res<ButtonInput<KeyCode>>,
    mouse: Res<ButtonInput<MouseButton>>,
    bridge: Res<CoreBridge>,
    mut menu: ResMut<Menu>,
    mut intents: MessageWriter<UiIntent>,
) {
    if ui.screen != Screen::Playing || !model.alive || !model.connected {
        return;
    }
    use GameAction::*;
    for (index, key) in [
        Hotbar3, Hotbar4, Hotbar5, Hotbar6, Hotbar7, Hotbar8, Hotbar9, Hotbar10,
    ]
    .into_iter()
    .enumerate()
    {
        if !settings.just_pressed(key, &keys, &mouse) {
            continue;
        }
        let Some((cell, id)) = menu.bindings[index] else {
            continue;
        };
        let Ok(reply) = bridge.request("inventoryView", json!({})) else {
            continue;
        };
        let Ok(view) = serde_json::from_value::<api::View>(reply["result"].clone()) else {
            continue;
        };
        let Some(row) = view.at(cell).filter(|row| row.address.id == id) else {
            menu.bindings[index] = None;
            continue;
        };
        if let Some(intent) = selection(row, cell) {
            intents.write(intent);
        } else if row.equip_slot >= 0 && cell.page >= 2 {
            model.notice("Holster it first");
        }
    }
}
fn selection(row: &api::Item, cell: Cell) -> Option<UiIntent> {
    if row.hand_action == "Dequip" {
        Some(commands::dequip())
    } else if row.hand_action.is_empty()
        || row.address.token == 0
        || cell.page >= OWN_PAGES
        || (row.equip_slot >= 0 && cell.page >= 2)
    {
        None
    } else {
        Some(commands::select(row.address))
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn binding_a_bag_gun_does_not_create_a_third_holster_but_same_held_item_dequips() {
        let cell = Cell::at(2, 1, 1);
        let mut row = api::Item {
            address: api::Address {
                page: 2,
                x: 1,
                y: 1,
                id: 4,
                token: 9,
            },
            hand_action: "Equip".into(),
            equip_slot: 0,
            ..default()
        };
        assert!(selection(&row, cell).is_none());
        row.equip_slot = -1;
        let Some(UiIntent::Action { name, args }) = selection(&row, cell) else {
            panic!()
        };
        assert_eq!(name, "selectHeld");
        assert_eq!(args["token"], 9);
        row.hand_action = "Dequip".into();
        let Some(UiIntent::Action { args, .. }) = selection(&row, cell) else {
            panic!()
        };
        assert_eq!(args["token"], 0);
    }
}
