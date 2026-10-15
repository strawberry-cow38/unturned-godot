use super::*;

#[test]
fn craft_quantity_and_cancel_are_only_accepted_state_intents() {
    let mut app = App::new();
    app.insert_resource(UiState { screen: Screen::Crafting, ..default() })
        .insert_resource(Menu { browse: Browse {selected: 17, quantity: 3,..default()},
            options: json!({"view":{"selected":{"index":17,"quantity":3,"maxQuantity":3,"craftable":true}}}),..default() })
        .add_message::<UiIntent>().add_observer(activate);
    let craft = app.world_mut().spawn(Action::Craft).id();
    app.world_mut().trigger(Activate { entity: craft });
    let sent: Vec<_> = app
        .world_mut()
        .resource_mut::<Messages<UiIntent>>()
        .drain()
        .collect();
    assert_eq!(sent.len(), 3);
    assert!(sent.iter().all(|intent|matches!(intent,UiIntent::Action{name,args} if name=="craft" && args==&json!({"index":17}))));
    assert_eq!(
        app.world().resource::<Menu>().options["view"]["selected"]["quantity"],
        3
    );
    let cancel = app.world_mut().spawn(Action::Cancel(2)).id();
    app.world_mut().trigger(Activate { entity: cancel });
    let sent: Vec<_> = app
        .world_mut()
        .resource_mut::<Messages<UiIntent>>()
        .drain()
        .collect();
    assert!(
        matches!(&sent[..],[UiIntent::Action{name,args}] if name=="craftCancel" && args==&json!({"slot":2}))
    );
    app.world_mut().resource_mut::<Menu>().options["view"]["selected"]["craftable"] = json!(false);
    app.world_mut().trigger(Activate { entity: craft });
    assert_eq!(
        app.world_mut()
            .resource_mut::<Messages<UiIntent>>()
            .drain()
            .count(),
        0
    );
    app.world_mut().resource_mut::<UiState>().screen = Screen::Playing;
    app.world_mut().trigger(Activate { entity: cancel });
    assert_eq!(
        app.world_mut()
            .resource_mut::<Messages<UiIntent>>()
            .drain()
            .count(),
        0
    );
}

#[test]
fn category_and_bag_lookup_leave_no_stale_query_or_selection() {
    let mut app = App::new();
    app.insert_resource(UiState {
        screen: Screen::Crafting,
        ..default()
    })
    .init_resource::<Menu>()
    .add_message::<UiIntent>()
    .add_observer(activate);
    {
        let mut menu = app.world_mut().resource_mut::<Menu>();
        menu.browse.search = "metal scrap".into();
        menu.browse.selected = 3;
        menu.lookup(true, 76);
        assert_eq!(menu.browse.lookup, "uses");
        assert_eq!(menu.browse.item_id, 76);
        assert!(menu.browse.search.is_empty());
        assert_eq!(menu.browse.selected, -1);
    }
    let category = app.world_mut().spawn(Action::Category("Dyes".into())).id();
    app.world_mut().trigger(Activate { entity: category });
    let menu = app.world().resource::<Menu>();
    assert!(menu.browse.lookup.is_empty());
    assert_eq!(menu.browse.category, "Dyes");
}

#[test]
fn queue_bar_uses_only_simulation_ticks_and_does_not_advance_at_pause() {
    let mut app = App::new();
    app.insert_resource(GameSnapshot(json!({"tick":100})))
        .add_systems(Update, progress);
    let bar = app
        .world_mut()
        .spawn((
            Progress {
                left: 2.,
                total: 4.,
                tick: 100,
            },
            Node::default(),
        ))
        .id();
    app.update();
    assert_eq!(app.world().get::<Node>(bar).unwrap().width, percent(50));
    app.update();
    assert_eq!(app.world().get::<Node>(bar).unwrap().width, percent(50));
    app.world_mut().resource_mut::<GameSnapshot>().0["tick"] = json!(150);
    app.update();
    assert_eq!(app.world().get::<Node>(bar).unwrap().width, percent(75));
}

#[test]
fn double_click_keeps_source_press_time_distance_and_pair_reset() {
    let mut click = DoubleClick::default();
    assert!(!click.press(1., Vec2::new(30., 30.)));
    // The tile entity may have been replaced by selection; its recipe is stable.
    assert!(click.press(1.39, Vec2::new(33., 30.)));
    assert!(
        !click.press(1.40, Vec2::new(33., 30.)),
        "a third click starts a fresh pair"
    );
    assert!(
        !click.press(1.42, Vec2::new(38., 30.)),
        "exact5px does not count"
    );
    assert!(
        !click.press(1.83, Vec2::new(38., 30.)),
        "outside400ms does not count"
    );
}

fn pointer_app() -> App {
    let mut app = App::new();
    app.add_plugins((MinimalPlugins,bevy::ui_widgets::ButtonPlugin))
        .init_resource::<PointerCaptureMap>()
        .insert_resource(UiState{screen:Screen::Crafting,..default()})
        .insert_resource(Menu{viewport:Vec2::new(1280.,720.),options:json!({"tick":100,"queue":[{"slot":0,"index":17}],"view":{"tiles":[{"index":17,"craftable":true}]}}),..default()})
        .add_message::<UiIntent>().add_observer(activate).add_observer(pointer_press).add_observer(pointer_move)
        .add_observer(pointer_release);
    app
}
fn pointer(x: f32) -> bevy::picking::events::Pointer {
    bevy::picking::events::Pointer::new(
        bevy::picking::pointer::PointerId::Mouse,
        bevy::picking::pointer::Location {
            target: bevy::camera::NormalizedRenderTarget::None {
                width: 1280,
                height: 720,
            },
            position: Vec2::new(x, 650.),
        },
    )
}
fn hit(entity: Entity) -> bevy::picking::backend::HitData {
    bevy::picking::backend::HitData {
        camera: entity,
        depth: 0.,
        position: None,
        normal: None,
        extra: None,
    }
}
fn at_time(app: &mut App, time: f64) {
    app.world_mut()
        .resource_mut::<Time<Real>>()
        .advance_to(std::time::Duration::from_secs_f64(time));
}
fn press_pointer(app: &mut App, entity: Entity, time: f64, x: f32) {
    at_time(app, time);
    app.world_mut().commands().trigger(PointerPress {
        entity,
        pointer: pointer(x),
        button: PointerButton::Primary,
        hit: hit(entity),
        count: 1,
    });
    app.world_mut().flush();
}
fn release_pointer(app: &mut App, entity: Entity, time: f64, x: f32) {
    use bevy::picking::events::{PointerClick, PointerRelease};
    at_time(app, time);
    app.world_mut().commands().trigger(PointerClick {
        entity,
        pointer: pointer(x),
        button: PointerButton::Primary,
        hit: hit(entity),
        duration: std::time::Duration::from_millis(10),
        count: 1,
    });
    app.world_mut().commands().trigger(PointerRelease {
        entity,
        pointer: pointer(x),
        button: PointerButton::Primary,
        hit: hit(entity),
    });
    app.world_mut().flush();
}
fn drain(app: &mut App) -> Vec<UiIntent> {
    app.world_mut()
        .resource_mut::<Messages<UiIntent>>()
        .drain()
        .collect()
}

#[test]
fn pointer_double_click_uses_presses_across_widget_replacement_and_long_holds() {
    let mut app = pointer_app();
    let first = app.world_mut().spawn((Button, Action::Select(17))).id();
    press_pointer(&mut app, first, 0., 50.);
    release_pointer(&mut app, first, 0.3, 50.);
    assert!(drain(&mut app).is_empty());
    app.world_mut().despawn(first);
    let next = app.world_mut().spawn((Button, Action::Select(17))).id();
    press_pointer(&mut app, next, 0.5, 50.);
    assert!(
        drain(&mut app).is_empty(),
        "500ms press gap must not use210ms release gap"
    );
    release_pointer(&mut app, next, 0.51, 50.);
    assert!(drain(&mut app).is_empty());
    press_pointer(&mut app, next, 0.7, 50.);
    let sent = drain(&mut app);
    assert!(
        matches!(&sent[..],[UiIntent::Action{name,args}] if name=="craft"&&args==&json!({"index":17}))
    );
    release_pointer(&mut app, next, 1.5, 50.);
    assert!(
        drain(&mut app).is_empty(),
        "holding the second press cannot lose or duplicate its craft"
    );
}

#[test]
fn queue_drag_and_changed_acceptance_suppress_cancel_but_plain_click_cancels() {
    let mut app = pointer_app();
    let tile = app.world_mut().spawn((Button, Action::Cancel(0))).id();
    press_pointer(&mut app, tile, 0., 50.);
    release_pointer(&mut app, tile, 0.01, 50.);
    assert!(
        matches!(&drain(&mut app)[..],[UiIntent::Action{name,args}] if name=="craftCancel"&&args==&json!({"slot":0}))
    );
    press_pointer(&mut app, tile, 0.5, 50.);
    app.world_mut().trigger(PointerMove {
        entity: tile,
        pointer: pointer(70.),
        hit: hit(tile),
        delta: Vec2::new(20., 0.),
    });
    assert_eq!(app.world().resource::<Menu>().scroll[2], 20.);
    release_pointer(&mut app, tile, 0.52, 70.);
    assert!(
        drain(&mut app).is_empty(),
        "scrolling must not cancel the held job"
    );
    press_pointer(&mut app, tile, 1., 50.);
    app.world_mut().resource_mut::<Menu>().options["tick"] = json!(110);
    release_pointer(&mut app, tile, 1.01, 50.);
    assert!(
        drain(&mut app).is_empty(),
        "an accepted replacement queue invalidates the old pressed slot"
    );
}

#[test]
fn rebound_lookup_hover_and_carried_rotation_use_distinct_bag_contexts() {
    let mut app = App::new();
    app.init_resource::<Menu>()
        .init_resource::<Settings>()
        .init_resource::<ButtonInput<KeyCode>>()
        .init_resource::<ButtonInput<MouseButton>>()
        .add_message::<KeyboardInput>()
        .add_message::<UiIntent>()
        .insert_resource(UiState {
            screen: Screen::Inventory,
            ..default()
        })
        .insert_resource(UiModel {
            pages: vec![InventoryPage {
                index: 2,
                width: 5,
                height: 3,
                items: vec![InventoryItem {
                    id: 76,
                    x: 1,
                    y: 1,
                    amount: 1,
                    ..default()
                }],
            }],
            ..default()
        })
        .add_systems(Update, (lookup_input, keyboard_ui).chain());
    app.world_mut()
        .spawn((UiButton::Select(2, 1, 1), Hovered(true)));
    app.world_mut()
        .resource_mut::<Settings>()
        .bindings
        .insert(GameAction::ItemUses, Binding::Key("KeyO".into()));
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .press(KeyCode::KeyO);
    app.update();
    assert_eq!(app.world().resource::<UiState>().screen, Screen::Crafting);
    assert_eq!(app.world().resource::<Menu>().browse.lookup, "uses");
    assert_eq!(app.world().resource::<Menu>().browse.item_id, 76);
    {
        let mut ui = app.world_mut().resource_mut::<UiState>();
        ui.screen = Screen::Inventory;
        ui.selected_item = Some((2, 1, 1));
    }
    for expected in [1, 2, 3, 0] {
        let mut keys = app.world_mut().resource_mut::<ButtonInput<KeyCode>>();
        keys.reset_all();
        keys.press(KeyCode::KeyR);
        app.update();
        assert_eq!(app.world().resource::<UiState>().screen, Screen::Inventory);
        assert_eq!(app.world().resource::<UiState>().rotation, expected);
    }
    app.world_mut().resource_mut::<UiState>().selected_item = None;
    let mut keys = app.world_mut().resource_mut::<ButtonInput<KeyCode>>();
    keys.reset_all();
    keys.press(KeyCode::KeyR);
    app.update();
    assert_eq!(app.world().resource::<Menu>().browse.lookup, "recipes");
    assert_eq!(app.world().resource::<UiState>().screen, Screen::Crafting);
    assert_eq!(
        app.world().resource::<UiModel>().pages[0].items[0].amount,
        1,
        "lookup never mutates a jar"
    );
}

#[test]
fn blank_queue_space_captures_drag_and_ignores_other_pointers() {
    let mut app = pointer_app();
    let area = app.world_mut().spawn(render::QueueArea).id();
    press_pointer(&mut app, area, 0., 50.);
    assert!(app.world().resource::<Menu>().retain_queue_widgets());
    let mut other = pointer(90.);
    other.id = PointerId::Touch(7);
    app.world_mut().trigger(PointerMove {
        entity: area,
        pointer: other,
        hit: hit(area),
        delta: Vec2::new(40., 0.),
    });
    assert_eq!(app.world().resource::<Menu>().scroll[2], 0.);
    app.world_mut().trigger(PointerMove {
        entity: area,
        pointer: pointer(70.),
        hit: hit(area),
        delta: Vec2::new(20., 0.),
    });
    assert_eq!(app.world().resource::<Menu>().scroll[2], 20.);
    assert!(drain(&mut app).is_empty());
}

#[test]
fn queue_row_uses_source_x_only_job_hit_even_above_or_below_the_icon() {
    let mut app = pointer_app();
    let area = app.world_mut().spawn(render::QueueArea).id();
    let layout = render::Layout::new(app.world().resource::<Menu>().viewport);
    let x = layout.queue_row.max.x - 25.;
    for (time, y) in [
        (0., layout.queue_row.min.y + 1.),
        (0.5, layout.queue_row.max.y - 1.),
    ] {
        at_time(&mut app, time);
        let mut point = pointer(x);
        point.position.y = y;
        app.world_mut().commands().trigger(PointerPress {
            entity: area,
            pointer: point,
            button: PointerButton::Primary,
            hit: hit(area),
            count: 1,
        });
        app.world_mut().flush();
        release_pointer(&mut app, area, time + 0.01, x);
        assert!(
            matches!(&drain(&mut app)[..],[UiIntent::Action{name,args}] if name=="craftCancel"&&args==&json!({"slot":0}))
        );
    }
    let gap = layout.queue_row.max.x - 48.;
    press_pointer(&mut app, area, 1., gap);
    release_pointer(&mut app, area, 1.01, gap);
    assert!(
        drain(&mut app).is_empty(),
        "the source8px inter-tile gap never names a job"
    );
}

#[test]
fn actual_picker_coalesced_move_press_release_cancels_the_visible_queue_slot_once() {
    use bevy::picking::{InteractionPlugin, PickingPlugin, pointer::PointerAction};
    let mut app = pointer_app();
    app.add_plugins((PickingPlugin, InteractionPlugin));
    let old = app.world_mut().spawn(Button).id();
    let tile = app.world_mut().spawn((Button, Action::Cancel(0))).id();
    super::super::button_input_tests::mouse_frame(
        &mut app,
        &[old],
        &[],
        pointer(1150.).location(),
        &[PointerAction::Move {
            delta: Vec2::new(300., 0.),
        }],
    );
    super::super::button_input_tests::mouse_frame(
        &mut app,
        &[tile],
        &[old],
        pointer(904.).location(),
        &[
            PointerAction::Move {
                delta: Vec2::new(-246., 0.),
            },
            PointerAction::Press(PointerButton::Primary),
            PointerAction::Release(PointerButton::Primary),
        ],
    );
    assert!(
        matches!(&drain(&mut app)[..],[UiIntent::Action{name,args}] if name=="craftCancel"&&args==&json!({"slot":0})),
        "raw picker dispatch must cancel exactly once even after fresh hover"
    );
}
