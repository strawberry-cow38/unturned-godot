use super::super::{InventoryItem, InventoryPage, UiIntent};
use super::{api, commands::*, model::*};
use serde_json::json;
fn commands(dispatch: Dispatch) -> Vec<(String, serde_json::Value)> {
    dispatch
        .commands
        .into_iter()
        .map(|i| match i {
            UiIntent::Action { name, args } => (name, args),
            _ => panic!("not a gameplay command"),
        })
        .collect()
}
fn row(page: u8, id: u16, token: u64) -> api::Item {
    api::Item {
        address: api::Address {
            page,
            x: 2,
            y: 1,
            id,
            token,
        },
        hand_action: "Equip".into(),
        ..Default::default()
    }
}
#[test]
fn container_paperdoll_weapon_moves_displaced_slot_then_waits_for_owner_token_before_equipping() {
    let mut incoming = row(7, 4, 0);
    incoming.equip = Some(api::EquipPlan {
        slot: 0,
        displaced: Some(api::Address {
            page: 0,
            x: 0,
            y: 0,
            id: 6,
            token: 111,
        }),
        displaced_to: Some(api::Destination {
            page: 2,
            x: 4,
            y: 2,
            rotation: 1,
        }),
        drop_displaced: false,
    });
    let view = api::View {
        items: vec![incoming.clone()],
        ..Default::default()
    };
    let out = dispatch(Intent::Equip(incoming.address.cell()), &view, &[]);
    let pending = out.pending.clone().unwrap();
    assert_eq!(
        commands(out),
        vec![
            (
                "moveItem".into(),
                json!({"page0":0,"x0":0,"y0":0,"page1":2,"x1":4,"y1":2,"rot1":1})
            ),
            (
                "moveItem".into(),
                json!({"page0":7,"x0":2,"y0":1,"page1":0,"x1":0,"y1":0,"rot1":0})
            )
        ]
    );
    assert!(pending.accepted(&view).is_none());
    let mut accepted = incoming.clone();
    accepted.address = api::Address {
        page: 0,
        x: 0,
        y: 0,
        id: 4,
        token: 222,
    };
    let next = api::View {
        items: vec![accepted],
        ..Default::default()
    };
    let Some(UiIntent::Action { name, args }) = pending.accepted(&next) else {
        panic!("missing accepted selection")
    };
    assert_eq!(name, "selectHeld");
    assert_eq!(args["token"], 222);
    assert_eq!(args["equip"], false);
    assert_eq!(
        view.items[0], incoming,
        "planning never rewrites accepted pages"
    );
}
#[test]
fn paperdoll_take_full_bag_does_not_fall_through_to_drop_and_clothing_maps_existing_wire_slots() {
    let selected = row(7, 44, 0);
    let view = api::View {
        items: vec![selected.clone()],
        ..Default::default()
    };
    assert!(
        dispatch(Intent::TakeToPaperdoll(selected.address.cell()), &view, &[])
            .commands
            .is_empty()
    );
    assert_eq!(
        (0..7).filter_map(worn_wire).collect::<Vec<_>>(),
        vec![0, 6, 3, 2, 5, 4, 1]
    );
    assert_eq!(
        commands(dispatch(
            Intent::Wear {
                from: Cell::at(2, 3, 4),
                slot: 1
            },
            &view,
            &[]
        )),
        vec![(
            "wearClothing".into(),
            json!({"page":2,"x":3,"y":4,"slot":6})
        )]
    );
}
#[test]
fn source_context_salvage_and_empty_use_exact_tokens_and_do_not_claim_authority() {
    let selected = row(2, 27, 912);
    let view = api::View {
        items: vec![selected.clone()],
        ..Default::default()
    };
    for (button, expected) in [("salvage", "salvageItem"), ("emptyFuel", "emptyFuel")] {
        assert_eq!(
            commands(context(button, &selected, &view, &[])),
            vec![(
                expected.into(),
                json!({"page":2,"x":2,"y":1,"id":27,"token":912})
            )]
        );
    }
    assert_eq!(view.items[0], selected);
    for page in [7, 8, 9, 10, 21] {
        let external = row(page, 28, 0);
        assert_eq!(
            context_refusal("emptyFuel", &external),
            Some("Take this item into your inventory first.")
        );
        assert!(
            context("emptyFuel", &external, &view, &[])
                .commands
                .is_empty()
        );
    }
}
#[test]
fn own_to_container_uses_source_compartment_plan_and_nearby_always_takes_to_owner() {
    let mut selected = row(2, 44, 31);
    selected.store_page = 12;
    let view = api::View {
        items: vec![selected.clone()],
        ..Default::default()
    };
    let pages = vec![InventoryPage {
        index: 7,
        width: 3,
        height: 3,
        items: vec![InventoryItem::default()],
    }];
    let result = commands(dispatch(
        Intent::QuickTransfer(selected.address.cell()),
        &view,
        &pages,
    ));
    assert_eq!(result[0].1["toPage"], 12);
    let result = commands(dispatch(
        Intent::TakeToOwn(Cell::at(8, 0, 0)),
        &view,
        &pages,
    ));
    assert_eq!(result[0].1["toPage"], 255);
}
