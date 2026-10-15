//! Inventory clothing transactions use the original EItemType slot numbers.
//! Selection emits a request; only the next accepted replica changes worn gear.
use super::*;

pub(super) const SLOTS: [&str; 7] = [
    "hat", "pants", "shirt", "mask", "backpack", "vest", "glasses",
];

pub(super) fn slot(kind: &str) -> Option<u8> {
    SLOTS.iter().position(|name| *name == kind).map(|i| i as u8)
}

pub(super) fn worn_buttons(panel: &mut ChildSpawnerCommands, model: &UiModel) {
    if model.worn.iter().all(|id| *id == 0) {
        return;
    }
    panel.spawn(row()).with_children(|p| {
        p.spawn(text("Wearing", 14., DIM));
        for (index, id) in model.worn.iter().enumerate() {
            if *id != 0 {
                button(
                    p,
                    format!("Remove {}", SLOTS[index]),
                    UiButton::Unwear(index as u8),
                );
            }
        }
    });
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn wear_and_unwear_wait_for_the_accepted_inventory_replica() {
        let mut app = App::new();
        app.init_resource::<Settings>()
            .init_resource::<UiModel>()
            .init_resource::<UiState>()
            .add_message::<UiIntent>()
            .add_observer(activate_button);
        let initial = json!({"inventory":{"worn":{"hat":27},"pages":[
            {"index":2,"width":5,"height":3,"items":[{"id":334,"x":3,"y":1}]}]}});
        app.world_mut()
            .resource_mut::<UiModel>()
            .adopt_snapshot(&initial);
        app.world_mut().resource_mut::<UiState>().selected_item = Some((2, 3, 1));
        let wear = app
            .world_mut()
            .spawn(UiButton::Wear(slot("glasses").unwrap()))
            .id();
        app.world_mut().trigger(Activate { entity: wear });
        let messages: Vec<_> = app
            .world_mut()
            .resource_mut::<Messages<UiIntent>>()
            .drain()
            .collect();
        assert!(matches!(&messages[..], [UiIntent::Action {name,args}]
            if name == "wearClothing" && args == &json!({"page":2,"x":3,"y":1,"slot":6})));
        assert_eq!(
            app.world().resource::<UiModel>().worn,
            [27, 0, 0, 0, 0, 0, 0]
        );
        assert_eq!(app.world().resource::<UiModel>().pages[0].items[0].id, 334);
        assert!(app.world().resource::<UiState>().selected_item.is_none());

        app.world_mut()
            .resource_mut::<UiModel>()
            .adopt_snapshot(&json!({
            "inventory":{"worn":{"hat":27,"glasses":334},"pages":[]}}));
        let remove = app.world_mut().spawn(UiButton::Unwear(6)).id();
        app.world_mut().trigger(Activate { entity: remove });
        let messages: Vec<_> = app
            .world_mut()
            .resource_mut::<Messages<UiIntent>>()
            .drain()
            .collect();
        assert!(matches!(&messages[..], [UiIntent::Action {name,args}]
            if name == "unwearClothing" && args == &json!({"slot":6})));
        assert_eq!(app.world().resource::<UiModel>().worn[6], 334);
        app.world_mut()
            .resource_mut::<UiModel>()
            .adopt_snapshot(&initial);
        assert_eq!(app.world().resource::<UiModel>().worn[6], 0);
        app.world_mut().resource_mut::<UiModel>().reset_session();
        assert_eq!(app.world().resource::<UiModel>().worn, [0; 7]);
    }
}
