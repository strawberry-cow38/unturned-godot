use super::*;
use crate::bridge::CoreBridge;
use bevy::asset::AssetPlugin;

pub(super) fn bridge() -> CoreBridge {
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR"));
    let bridge = CoreBridge::launch(root).expect("build the protocol70 Skills bridge and set UG_BRIDGE_DLL before running Skills integration tests");
    bridge
        .request("init", json!({"contentRoot":root.join("../game/content")}))
        .unwrap();
    bridge
}
pub(super) fn snapshot(bridge: &CoreBridge) -> Value {
    bridge.request("snapshot", json!({})).unwrap()["state"].clone()
}
pub(super) fn source_tree(bridge: &CoreBridge) -> Tree {
    let tree: Tree = serde_json::from_value(snapshot(bridge)["skills"]["tree"].clone())
        .expect("protocol70 Skills read model missing; use built Skills bridge");
    assert_eq!(tree.nodes.len(), 15);
    tree
}
pub(super) fn pump(bridge: &CoreBridge) {
    for _ in 0..12 {
        bridge.request("tick", json!({})).unwrap();
    }
}

#[test]
fn original_graph_bands_cross_pillar_edges_and_pan_bounds_come_from_source_read_model() {
    let bridge = bridge();
    let tree = source_tree(&bridge);
    let graph = geometry::Graph::new(&tree);
    assert_eq!(graph.heads["might"], Vec2::ZERO);
    assert_eq!(graph.heads["engineering"], Vec2::new(584., 586.));
    assert_eq!(graph.nodes["field_surgery"], Vec2::new(292., 824.));
    assert_eq!(graph.size, Vec2::new(828., 906.));
    let target = graph.nodes["improvised_armour"] + Vec2::new(122., 0.);
    assert_eq!(
        graph
            .edges(&tree)
            .iter()
            .filter(|(_, to, _)| *to == target)
            .count(),
        2
    );
    assert_eq!(
        graph.clamp(Vec2::new(-9999., 9999.), Vec2::new(400., 350.)),
        Vec2::new(-428., 0.)
    );
    assert_eq!(
        graph.center_if_small(Vec2::ZERO, Vec2::new(1000., 1000.)),
        Vec2::new(86., 0.)
    );
    let layout = geometry::Layout::new(Vec2::new(1280., 720.));
    assert!(
        layout.clip.max.y + layout.panel.min.y < 720. - 200.,
        "tree stops above source vitals band"
    );
}

fn app(tree: Tree) -> App {
    let mut app = App::new();
    let mut model = UiModel::default();
    model.connected = true;
    model.health = 100.;
    model.map_name = "PEI".into();
    app.add_plugins((MinimalPlugins, AssetPlugin::default()))
        .init_asset::<Font>()
        .insert_resource(model)
        .insert_resource(UiState {
            screen: Screen::Skills,
            ..default()
        })
        .init_resource::<Settings>()
        .init_resource::<WorldSessionEpoch>()
        .insert_resource(Menu {
            tree,
            viewport: Vec2::new(1280., 720.),
            ..default()
        })
        .init_resource::<crate::character::nameplate::OriginalFont>()
        .init_resource::<Symbols>()
        .add_message::<UiIntent>()
        .add_observer(activate)
        .add_systems(Update, render::build);
    app
}

#[test]
fn learn_button_waits_for_accepted_graph_and_preserves_selection_across_replica_rebuild() {
    let bridge = bridge();
    bridge
        .request("action", json!({"name":"console","args":{"text":"xp 100"}}))
        .unwrap();
    pump(&bridge);
    let mut app = app(source_tree(&bridge));
    app.world_mut().resource_mut::<Menu>().selected = Some("heavy_lifter".into());
    app.update();
    let button = app
        .world_mut()
        .query::<(Entity, &Action)>()
        .iter(app.world())
        .find_map(|(e, a)| matches!(a, Action::Learn).then_some(e))
        .unwrap();
    app.world_mut().trigger(Activate { entity: button });
    let before = app.world().resource::<Menu>().tree.clone();
    assert_eq!(before.experience, 100);
    assert!(
        !before
            .nodes
            .iter()
            .find(|n| n.id == "heavy_lifter")
            .unwrap()
            .learned
    );
    let intents: Vec<_> = app
        .world_mut()
        .resource_mut::<Messages<UiIntent>>()
        .drain()
        .collect();
    let UiIntent::Action { name, args } = &intents[0] else {
        panic!("expected learn intent")
    };
    assert_eq!(name, "learnSkillNode");
    assert_eq!(args["nodeId"], "heavy_lifter");
    bridge
        .request("action", json!({"name":name,"args":args}))
        .unwrap();
    pump(&bridge);
    app.world_mut().resource_mut::<Menu>().tree = source_tree(&bridge);
    app.update();
    let menu = app.world().resource::<Menu>();
    assert_eq!(menu.selected.as_deref(), Some("heavy_lifter"));
    assert_eq!(menu.tree.experience, 80);
    assert!(
        menu.tree
            .nodes
            .iter()
            .find(|n| n.id == "heavy_lifter")
            .unwrap()
            .learned
    );
    let button = app
        .world_mut()
        .query::<(Entity, &Action)>()
        .iter(app.world())
        .find_map(|(e, a)| matches!(a, Action::Learn).then_some(e))
        .unwrap();
    assert!(
        app.world().get::<Button>(button).is_none(),
        "accepted learned node disables repeat purchase"
    );
    app.world_mut().trigger(Activate { entity: button });
    assert_eq!(
        app.world_mut()
            .resource_mut::<Messages<UiIntent>>()
            .drain()
            .count(),
        0
    );
}

#[test]
fn closing_or_dead_graph_cannot_dispatch_a_stale_learn_button() {
    let bridge = bridge();
    let mut tree = source_tree(&bridge);
    tree.nodes[0].can_take = true;
    let mut app = app(tree);
    app.world_mut().resource_mut::<Menu>().selected = Some("heavy_lifter".into());
    app.update();
    let button = app
        .world_mut()
        .query::<(Entity, &Action)>()
        .iter(app.world())
        .find_map(|(e, a)| matches!(a, Action::Learn).then_some(e))
        .unwrap();
    app.world_mut().resource_mut::<UiState>().screen = Screen::Playing;
    app.world_mut().trigger(Activate { entity: button });
    app.world_mut().resource_mut::<UiState>().screen = Screen::Skills;
    app.world_mut().resource_mut::<UiModel>().alive = false;
    app.world_mut().trigger(Activate { entity: button });
    assert_eq!(
        app.world_mut()
            .resource_mut::<Messages<UiIntent>>()
            .drain()
            .count(),
        0
    );
    app.update();
    assert_eq!(
        app.world_mut()
            .query_filtered::<Entity, With<render::Root>>()
            .iter(app.world())
            .count(),
        0
    );
}

#[test]
fn original_skills_key_and_dashboard_tabs_toggle_without_recapturing_information_close() {
    let mut app = App::new();
    app.init_resource::<UiState>()
        .init_resource::<Settings>()
        .init_resource::<ButtonInput<KeyCode>>()
        .init_resource::<ButtonInput<MouseButton>>()
        .add_message::<KeyboardInput>()
        .add_message::<UiIntent>()
        .add_systems(Update, super::super::keyboard_ui);
    app.world_mut().resource_mut::<UiState>().screen = Screen::Playing;
    for (key, expected) in [
        (KeyCode::KeyJ, Screen::Skills),
        (KeyCode::KeyY, Screen::Crafting),
        (KeyCode::KeyJ, Screen::Skills),
        (KeyCode::Tab, Screen::Inventory),
        (KeyCode::KeyJ, Screen::Skills),
        (KeyCode::KeyJ, Screen::Playing),
    ] {
        app.world_mut()
            .resource_mut::<ButtonInput<KeyCode>>()
            .reset_all();
        app.world_mut()
            .resource_mut::<ButtonInput<KeyCode>>()
            .press(key);
        app.update();
        assert_eq!(app.world().resource::<UiState>().screen, expected);
        assert!(!app.world().resource::<UiState>().map_closing);
    }
    app.world_mut()
        .resource_mut::<Settings>()
        .bindings
        .insert(GameAction::Skills, Binding::Key("KeyK".into()));
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .reset_all();
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .press(KeyCode::KeyJ);
    app.update();
    assert_eq!(app.world().resource::<UiState>().screen, Screen::Playing);
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .reset_all();
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .press(KeyCode::KeyK);
    app.update();
    assert_eq!(app.world().resource::<UiState>().screen, Screen::Skills);
}

#[test]
fn real_canvas_drag_skips_node_buttons_and_session_change_resets_selection_and_pan() {
    let bridge = bridge();
    let mut app = app(source_tree(&bridge));
    app.init_resource::<ButtonInput<MouseButton>>()
        .insert_resource(GameSnapshot(snapshot(&bridge)))
        .add_systems(Update, (sync, input).chain().before(render::build));
    let window = app
        .world_mut()
        .spawn((
            Window {
                resolution: (1280, 720).into(),
                ..default()
            },
            PrimaryWindow,
        ))
        .id();
    app.update();
    // The exposed inter-column gutter starts the source canvas drag.
    app.world_mut()
        .get_mut::<Window>(window)
        .unwrap()
        .set_cursor_position(Some(Vec2::new(312., 300.)));
    app.world_mut()
        .resource_mut::<ButtonInput<MouseButton>>()
        .press(MouseButton::Left);
    app.update();
    assert!(app.world().resource::<Menu>().dragging);
    app.world_mut()
        .resource_mut::<ButtonInput<MouseButton>>()
        .clear();
    app.world_mut()
        .get_mut::<Window>(window)
        .unwrap()
        .set_cursor_position(Some(Vec2::new(272., 170.)));
    app.update();
    assert_eq!(app.world().resource::<Menu>().pan, Vec2::new(-8., -130.));
    app.world_mut()
        .resource_mut::<ButtonInput<MouseButton>>()
        .reset_all();
    app.world_mut().resource_mut::<Menu>().pan = Vec2::ZERO;
    app.update();
    app.world_mut()
        .get_mut::<Window>(window)
        .unwrap()
        .set_cursor_position(Some(Vec2::new(64., 250.)));
    app.world_mut()
        .resource_mut::<ButtonInput<MouseButton>>()
        .press(MouseButton::Left);
    app.update();
    assert!(
        !app.world().resource::<Menu>().dragging,
        "node Button consumes the click"
    );
    app.world_mut()
        .resource_mut::<ButtonInput<MouseButton>>()
        .reset_all();
    app.update();
    let root = app
        .world_mut()
        .query_filtered::<Entity, With<render::Root>>()
        .single(app.world())
        .unwrap();
    app.world_mut()
        .get_mut::<UiTransform>(root)
        .unwrap()
        .translation
        .y = px(54);
    app.world_mut()
        .get_mut::<Window>(window)
        .unwrap()
        .set_cursor_position(Some(Vec2::new(64., 330.)));
    app.world_mut()
        .resource_mut::<ButtonInput<MouseButton>>()
        .press(MouseButton::Left);
    app.update();
    assert!(
        !app.world().resource::<Menu>().dragging,
        "arriving node consumes at its actual Swoop position, not its resting rectangle"
    );
    app.world_mut()
        .resource_mut::<ButtonInput<MouseButton>>()
        .reset_all();
    app.world_mut().resource_mut::<Menu>().selected = Some("heavy_lifter".into());
    app.world_mut().resource_mut::<Menu>().pan.y = -130.;
    app.world_mut().resource_mut::<UiState>().screen = Screen::Playing;
    app.update();
    app.world_mut().resource_mut::<UiState>().screen = Screen::Skills;
    app.update();
    assert_eq!(
        app.world().resource::<Menu>().selected.as_deref(),
        Some("heavy_lifter")
    );
    assert_eq!(app.world().resource::<Menu>().pan.y, -130.);
    app.world_mut()
        .get_mut::<Window>(window)
        .unwrap()
        .resolution
        .set(1800., 1000.);
    app.update();
    app.world_mut().resource_mut::<Menu>().pan.x = 35.;
    app.world_mut().resource_mut::<GameSnapshot>().0["skills"]["tree"]["experience"] = json!(1000);
    app.update();
    assert_eq!(
        app.world().resource::<Menu>().pan.x,
        35.,
        "accepted XP repaint must not recenter a manually panned narrow graph"
    );
    app.world_mut()
        .get_mut::<Window>(window)
        .unwrap()
        .resolution
        .set(1280., 720.);
    app.update();
    app.world_mut().resource_mut::<WorldSessionEpoch>().0 += 1;
    app.update();
    assert!(app.world().resource::<Menu>().selected.is_none());
    assert_eq!(app.world().resource::<Menu>().pan, Vec2::ZERO);
    app.world_mut().resource_mut::<UiModel>().connected = false;
    app.update();
    assert!(app.world().resource::<Menu>().tree.nodes.is_empty());
    assert_eq!(
        app.world_mut()
            .query_filtered::<Entity, With<render::Root>>()
            .iter(app.world())
            .count(),
        0
    );
}
