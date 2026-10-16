use super::*;
#[test]
fn manual_source_hold_cannot_be_dispatched_by_generic_press() {
    let bridge = CoreBridge::launch(std::path::Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
    let config = WorldConfig::default();
    bridge
        .request(
            "init",
            json!({"contentRoot":config.content_root,"spawn":[0,0,1]}),
        )
        .unwrap();
    bridge
        .request(
            "worldServices",
            json!({"doors":[{"netId":1,"position":[0,0,0],"owner":1}]}),
        )
        .unwrap();
    let mut app = App::new();
    app.add_plugins(MinimalPlugins)
        .insert_resource(bridge)
        .init_resource::<Settings>()
        .init_resource::<ButtonInput<KeyCode>>()
        .init_resource::<ButtonInput<MouseButton>>()
        .init_resource::<UiModel>()
        .init_resource::<InteractionFocus>()
        .insert_resource(UiState {
            screen: crate::ui::Screen::Playing,
            ..default()
        })
        .add_systems(Update, apply_interaction);
    app.world_mut()
        .resource_mut::<InteractionFocus>()
        .offer_manual(1., "Door", "toggleDoor", json!({"netId":1}));
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .press(KeyCode::KeyF);
    app.update();
    let mut snapshot = Value::Null;
    for _ in 0..8 {
        snapshot = app
            .world()
            .resource::<CoreBridge>()
            .request("tick", json!({}))
            .unwrap();
    }
    assert_eq!(
        snapshot["state"]["interactables"]["doors"][0]["open"],
        false
    );
    assert!(
        app.world()
            .resource::<UiModel>()
            .interaction
            .contains("Door")
    );
    assert!(
        app.world()
            .resource::<UiModel>()
            .interaction
            .starts_with("[F]")
    );
    *app.world_mut().resource_mut::<InteractionFocus>() = default();
    app.world_mut()
        .resource_mut::<InteractionFocus>()
        .offer_status(1., "door opening", "toggleDoor", json!({"netId":1}));
    app.update();
    assert_eq!(
        app.world().resource::<UiModel>().interaction,
        "door opening"
    );
    for _ in 0..8 {
        snapshot = app
            .world()
            .resource::<CoreBridge>()
            .request("tick", json!({}))
            .unwrap();
    }
    assert_eq!(
        snapshot["state"]["interactables"]["doors"][0]["open"], false,
        "passive status must neither advertise nor dispatch F"
    );
    *app.world_mut().resource_mut::<InteractionFocus>() = default();
    app.world_mut().resource_mut::<InteractionFocus>().offer(
        1.,
        "Door",
        "toggleDoor",
        json!({"netId":1}),
        false,
    );
    app.update();
    let mut snapshot = Value::Null;
    for _ in 0..8 {
        snapshot = app
            .world()
            .resource::<CoreBridge>()
            .request("tick", json!({}))
            .unwrap();
    }
    assert_eq!(
        snapshot["state"]["interactables"]["doors"][0]["open"], true,
        "ordinary press actions remain dispatchable"
    );
}

/// OpenNearestCrate has no prompt in the source, yet F still opens the crate.
#[test]
fn hidden_open_storage_dispatches_without_a_prompt_and_opens_the_storage_screen() {
    let bridge = CoreBridge::launch(std::path::Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
    let config = WorldConfig::default();
    bridge
        .request(
            "init",
            json!({"contentRoot":config.content_root,"spawn":[0,0,0],"allowCheats":true}),
        )
        .unwrap();
    bridge
        .request("action", json!({"name":"console","args":{"text":"give 9130"}}))
        .unwrap();
    for _ in 0..8 {
        bridge.request("tick", json!({})).unwrap();
    }
    bridge
        .request(
            "action",
            json!({"name":"placeDeployable","args":{"defId":9130,"pos":[0,0,-1.5],"yawDegrees":0}}),
        )
        .unwrap();
    let mut state = Value::Null;
    for _ in 0..8 {
        state = bridge.request("tick", json!({})).unwrap()["state"].clone();
    }
    let id = state["deployables"][0]["netId"].as_u64().unwrap();
    let mut app = App::new();
    app.add_plugins(MinimalPlugins)
        .insert_resource(bridge)
        .init_resource::<Settings>()
        .init_resource::<ButtonInput<KeyCode>>()
        .init_resource::<ButtonInput<MouseButton>>()
        .init_resource::<UiModel>()
        .init_resource::<InteractionFocus>()
        .insert_resource(UiState {
            screen: crate::ui::Screen::Playing,
            ..default()
        })
        .add_systems(Update, apply_interaction);
    app.world_mut().resource_mut::<InteractionFocus>().offer_hidden(
        f32::MAX,
        "openStorage",
        json!({"netId":id}),
        true,
    );
    app.update();
    assert_eq!(app.world().resource::<UiModel>().interaction, "", "no prompt");
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .press(KeyCode::KeyF);
    app.update();
    assert_eq!(
        app.world().resource::<UiState>().screen,
        crate::ui::Screen::Inventory
    );
    for _ in 0..8 {
        state = app
            .world()
            .resource::<CoreBridge>()
            .request("tick", json!({}))
            .unwrap()["state"]
            .clone();
    }
    assert_eq!(state["storage"]["netId"], id, "the accepted fridge crate opened");
}
