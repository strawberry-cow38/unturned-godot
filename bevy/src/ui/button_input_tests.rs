//! Regression input is queued exactly like bevy_picking::pointer_events, rather
//! than triggering Activate directly and bypassing the button state machine.
use super::*;
use bevy::{
    camera::NormalizedRenderTarget,
    picking::{
        backend::HitData,
        events::{Pointer, PointerClick, PointerPress, PointerRelease},
        pointer::{Location, PointerButton, PointerId},
    },
    ui_widgets::ButtonPlugin,
};

#[derive(Resource, Default)]
struct Activations(usize);
fn setup() -> (App, Entity) {
    let mut app = App::new();
    app.add_plugins((MinimalPlugins, ButtonPlugin))
        .init_resource::<Activations>()
        .add_observer(|_: On<Activate>, mut count: ResMut<Activations>| count.0 += 1);
    let button = app.world_mut().spawn(Button).id();
    (app, button)
}
fn pointer() -> Pointer {
    Pointer::new(
        PointerId::Mouse,
        Location {
            target: NormalizedRenderTarget::None {
                width: 100,
                height: 100,
            },
            position: Vec2::splat(20.),
        },
    )
}
fn hit(entity: Entity) -> HitData {
    HitData {
        camera: entity,
        depth: 0.,
        position: None,
        normal: None,
        extra: None,
    }
}
fn press(app: &mut App, entity: Entity) {
    app.world_mut().commands().trigger(PointerPress {
        entity,
        pointer: pointer(),
        button: PointerButton::Primary,
        hit: hit(entity),
        count: 1,
    });
}
fn release(app: &mut App, entity: Entity) {
    app.world_mut().commands().trigger(PointerClick {
        entity,
        pointer: pointer(),
        button: PointerButton::Primary,
        hit: hit(entity),
        duration: std::time::Duration::from_millis(12),
        count: 1,
    });
    app.world_mut().commands().trigger(PointerRelease {
        entity,
        pointer: pointer(),
        button: PointerButton::Primary,
        hit: hit(entity),
    });
}

#[test]
fn coalesced_pointer_press_click_release_activates_once_and_releases() {
    let (mut app, entity) = setup();
    press(&mut app, entity);
    release(&mut app, entity);
    app.world_mut().flush();
    assert_eq!(app.world().resource::<Activations>().0, 1);
    assert!(!app.world().entity(entity).contains::<Pressed>());
}

#[test]
fn separate_frame_pointer_press_click_release_activates_once_and_releases() {
    let (mut app, entity) = setup();
    press(&mut app, entity);
    app.world_mut().flush();
    assert!(app.world().entity(entity).contains::<Pressed>());
    release(&mut app, entity);
    app.world_mut().flush();
    assert_eq!(app.world().resource::<Activations>().0, 1);
    assert!(!app.world().entity(entity).contains::<Pressed>());
}

fn picked_click(previously_hovered: bool) -> (App, Entity) {
    use bevy::{
        ecs::system::RunSystemOnce,
        picking::{
            InteractionPlugin, PickingPlugin,
            events::pointer_events,
            hover::{HoverMap, PreviousHoverMap},
            pointer::{PointerAction, PointerInput},
        },
    };
    let (mut app, entity) = setup();
    app.add_plugins((PickingPlugin, InteractionPlugin));
    let hits = [(entity, hit(entity))].into_iter().collect();
    app.world_mut()
        .resource_mut::<HoverMap>()
        .0
        .insert(PointerId::Mouse, hits);
    if previously_hovered {
        let hits = [(entity, hit(entity))].into_iter().collect();
        app.world_mut()
            .resource_mut::<PreviousHoverMap>()
            .0
            .insert(PointerId::Mouse, hits);
    }
    for action in [
        PointerAction::Press(PointerButton::Primary),
        PointerAction::Release(PointerButton::Primary),
    ] {
        app.world_mut().write_message(PointerInput::new(
            PointerId::Mouse,
            pointer().location(),
            action,
        ));
    }
    app.world_mut().run_system_once(pointer_events).unwrap();
    (app, entity)
}

#[test]
fn picker_same_frame_mouse_enter_press_release_activates_once_and_releases() {
    let (app, entity) = picked_click(false);
    assert_eq!(app.world().resource::<Activations>().0, 1);
    assert!(!app.world().entity(entity).contains::<Pressed>());
}

#[test]
fn picker_already_hovered_same_frame_mouse_click_activates_once_and_releases() {
    let (app, entity) = picked_click(true);
    assert_eq!(app.world().resource::<Activations>().0, 1);
    assert!(!app.world().entity(entity).contains::<Pressed>());
}

#[test]
fn picker_mouse_leaves_pressed_button_and_releases_over_another_without_clicking_either() {
    use bevy::{
        ecs::system::RunSystemOnce,
        picking::{
            InteractionPlugin, PickingPlugin,
            events::pointer_events,
            hover::{HoverMap, PreviousHoverMap},
            pointer::{PointerAction, PointerInput},
        },
    };
    let (mut app, first) = setup();
    let second = app.world_mut().spawn(Button).id();
    app.add_plugins((PickingPlugin, InteractionPlugin));
    for previous in [false, true] {
        let hits = [(first, hit(first))].into_iter().collect();
        if previous {
            app.world_mut()
                .resource_mut::<PreviousHoverMap>()
                .0
                .insert(PointerId::Mouse, hits);
        } else {
            app.world_mut()
                .resource_mut::<HoverMap>()
                .0
                .insert(PointerId::Mouse, hits);
        }
    }
    app.world_mut().write_message(PointerInput::new(
        PointerId::Mouse,
        pointer().location(),
        PointerAction::Press(PointerButton::Primary),
    ));
    app.world_mut().run_system_once(pointer_events).unwrap();
    app.world_mut()
        .resource_mut::<Messages<PointerInput>>()
        .clear();
    app.world_mut().resource_mut::<HoverMap>().0.insert(
        PointerId::Mouse,
        [(second, hit(second))].into_iter().collect(),
    );
    for action in [
        PointerAction::Move {
            delta: Vec2::new(30., 0.),
        },
        PointerAction::Release(PointerButton::Primary),
    ] {
        app.world_mut().write_message(PointerInput::new(
            PointerId::Mouse,
            pointer().location(),
            action,
        ));
    }
    app.world_mut().run_system_once(pointer_events).unwrap();
    assert_eq!(
        app.world().resource::<Activations>().0,
        0,
        "release on another target cancels the pressed button"
    );
    assert!(!app.world().entity(first).contains::<Pressed>());
    assert!(!app.world().entity(second).contains::<Pressed>());
}

#[test]
fn picker_touch_release_keeps_prior_hit_after_the_pointer_disappears() {
    use bevy::{
        ecs::system::RunSystemOnce,
        picking::{
            InteractionPlugin, PickingPlugin,
            events::pointer_events,
            hover::{HoverMap, PreviousHoverMap},
            pointer::{PointerAction, PointerInput},
        },
    };
    let (mut app, entity) = setup();
    app.add_plugins((PickingPlugin, InteractionPlugin));
    let id = PointerId::Touch(4);
    app.world_mut()
        .resource_mut::<HoverMap>()
        .0
        .insert(id, [(entity, hit(entity))].into_iter().collect());
    app.world_mut().write_message(PointerInput::new(
        id,
        pointer().location(),
        PointerAction::Press(PointerButton::Primary),
    ));
    app.world_mut().run_system_once(pointer_events).unwrap();
    app.world_mut()
        .resource_mut::<Messages<PointerInput>>()
        .clear();
    app.world_mut().resource_mut::<HoverMap>().0.clear();
    app.world_mut()
        .resource_mut::<PreviousHoverMap>()
        .0
        .insert(id, [(entity, hit(entity))].into_iter().collect());
    app.world_mut().write_message(PointerInput::new(
        id,
        pointer().location(),
        PointerAction::Release(PointerButton::Primary),
    ));
    app.world_mut().run_system_once(pointer_events).unwrap();
    assert_eq!(app.world().resource::<Activations>().0, 1);
    assert!(!app.world().entity(entity).contains::<Pressed>());
}

fn mouse_frame(
    app: &mut App,
    current: &[Entity],
    previous: &[Entity],
    location: Location,
    actions: &[bevy::picking::pointer::PointerAction],
) {
    use bevy::{
        ecs::system::RunSystemOnce,
        picking::{
            events::pointer_events,
            hover::{HoverMap, PreviousHoverMap},
            pointer::{PointerInput, PointerLocation, PointerMap},
        },
    };
    app.world_mut()
        .resource_mut::<Messages<PointerInput>>()
        .clear();
    app.world_mut().resource_mut::<HoverMap>().0.insert(
        PointerId::Mouse,
        current.iter().map(|e| (*e, hit(*e))).collect(),
    );
    app.world_mut().resource_mut::<PreviousHoverMap>().0.insert(
        PointerId::Mouse,
        previous.iter().map(|e| (*e, hit(*e))).collect(),
    );
    let entity = app
        .world()
        .resource::<PointerMap>()
        .get_entity(PointerId::Mouse);
    if let Some(entity) = entity {
        app.world_mut().entity_mut(entity).insert(PointerLocation {
            location: Some(location.clone()),
        });
    } else {
        app.world_mut().spawn((
            PointerId::Mouse,
            PointerLocation {
                location: Some(location.clone()),
            },
        ));
    }
    for action in actions {
        app.world_mut().write_message(PointerInput::new(
            PointerId::Mouse,
            location.clone(),
            action.clone(),
        ));
    }
    app.world_mut().run_system_once(pointer_events).unwrap();
}

#[test]
fn real_inventory_selection_equip_and_respawn_receive_fast_pointer_clicks_once() {
    use bevy::picking::{InteractionPlugin, PickingPlugin, pointer::PointerAction};
    let (mut app, select) = setup();
    app.add_plugins((PickingPlugin, InteractionPlugin))
        .init_resource::<Settings>()
        .init_resource::<UiModel>()
        .init_resource::<UiState>()
        .add_message::<UiIntent>()
        .insert_resource(crate::LaunchOptions(crate::cli::Options::default()))
        .add_observer(activate_button);
    app.world_mut()
        .entity_mut(select)
        .insert(UiButton::Select(2, 3, 4));
    let equip = app.world_mut().spawn((Button, UiButton::Equip)).id();
    let respawn = app.world_mut().spawn((Button, UiButton::Respawn)).id();
    let click = [
        PointerAction::Press(PointerButton::Primary),
        PointerAction::Release(PointerButton::Primary),
    ];
    mouse_frame(&mut app, &[select], &[], pointer().location(), &click);
    assert_eq!(
        app.world().resource::<UiState>().selected_item,
        Some((2, 3, 4))
    );
    mouse_frame(&mut app, &[equip], &[select], pointer().location(), &click);
    mouse_frame(&mut app, &[respawn], &[equip], pointer().location(), &click);
    let messages: Vec<_> = app
        .world_mut()
        .resource_mut::<Messages<UiIntent>>()
        .drain()
        .collect();
    assert_eq!(messages.len(), 2);
    assert!(
        matches!(&messages[0], UiIntent::Action {name,args} if name=="equipAndHold" && args==&json!({"page":2,"x":3,"y":4}))
    );
    assert!(
        matches!(&messages[1], UiIntent::Action {name,args} if name=="respawn" && args==&json!({}))
    );
    assert_eq!(app.world().resource::<Activations>().0, 3);
}

#[test]
fn disabled_button_and_cancelled_press_never_activate() {
    use bevy::picking::{InteractionPlugin, PickingPlugin, pointer::PointerAction};
    let (mut app, entity) = setup();
    app.add_plugins((PickingPlugin, InteractionPlugin));
    app.world_mut()
        .entity_mut(entity)
        .insert(bevy::ui::InteractionDisabled);
    let click = [
        PointerAction::Press(PointerButton::Primary),
        PointerAction::Release(PointerButton::Primary),
    ];
    mouse_frame(&mut app, &[entity], &[], pointer().location(), &click);
    assert_eq!(app.world().resource::<Activations>().0, 0);
    app.world_mut()
        .entity_mut(entity)
        .remove::<bevy::ui::InteractionDisabled>();
    mouse_frame(
        &mut app,
        &[entity],
        &[entity],
        pointer().location(),
        &[PointerAction::Press(PointerButton::Primary)],
    );
    mouse_frame(
        &mut app,
        &[entity],
        &[entity],
        pointer().location(),
        &[PointerAction::Cancel],
    );
    mouse_frame(
        &mut app,
        &[entity],
        &[entity],
        pointer().location(),
        &[PointerAction::Release(PointerButton::Primary)],
    );
    assert_eq!(app.world().resource::<Activations>().0, 0);
    assert!(!app.world().entity(entity).contains::<Pressed>());
}

#[test]
fn switching_windows_preserves_click_target_and_does_not_activate_the_old_button() {
    use bevy::picking::{InteractionPlugin, PickingPlugin, pointer::PointerAction};
    let (mut app, first) = setup();
    app.add_plugins((PickingPlugin, InteractionPlugin));
    let second = app.world_mut().spawn(Button).id();
    let windows = [
        app.world_mut().spawn(Window::default()).id(),
        app.world_mut().spawn(Window::default()).id(),
    ];
    let location = |i: usize| Location {
        target: NormalizedRenderTarget::Window(
            bevy::window::WindowRef::Entity(windows[i])
                .normalize(None)
                .unwrap(),
        ),
        position: Vec2::splat(20.),
    };
    mouse_frame(
        &mut app,
        &[first],
        &[first],
        location(0),
        &[PointerAction::Press(PointerButton::Primary)],
    );
    mouse_frame(
        &mut app,
        &[second],
        &[first],
        location(1),
        &[
            PointerAction::Move {
                delta: Vec2::splat(30.),
            },
            PointerAction::Release(PointerButton::Primary),
        ],
    );
    assert_eq!(app.world().resource::<Activations>().0, 0);
    app.world_mut()
        .entity_mut(second)
        .observe(move |event: On<PointerClick>| {
            assert_eq!(
                event.pointer.target,
                NormalizedRenderTarget::Window(
                    bevy::window::WindowRef::Entity(windows[1])
                        .normalize(None)
                        .unwrap()
                )
            );
        });
    mouse_frame(
        &mut app,
        &[second],
        &[first],
        location(1),
        &[
            PointerAction::Press(PointerButton::Primary),
            PointerAction::Release(PointerButton::Primary),
        ],
    );
    assert_eq!(app.world().resource::<Activations>().0, 1);
}

/// Exercise capture cleanup in the real hover generator before event dispatch.
fn backend_frame(app: &mut App, target: Entity, actions: &[bevy::picking::pointer::PointerAction]) {
    use bevy::{
        ecs::system::RunSystemOnce,
        picking::{
            backend::PointerHits,
            events::pointer_events,
            hover::generate_hovermap,
            pointer::{PointerInput, PointerLocation, PointerMap},
        },
    };
    app.world_mut()
        .resource_mut::<Messages<PointerInput>>()
        .clear();
    app.world_mut()
        .resource_mut::<Messages<PointerHits>>()
        .clear();
    if app
        .world()
        .resource::<PointerMap>()
        .get_entity(PointerId::Mouse)
        .is_none()
    {
        app.world_mut().spawn((
            PointerId::Mouse,
            PointerLocation {
                location: Some(pointer().location()),
            },
        ));
    }
    app.world_mut().write_message(PointerHits::new(
        PointerId::Mouse,
        vec![(target, hit(target))],
        0.,
    ));
    for action in actions {
        app.world_mut()
            .write_message(bevy::picking::pointer::PointerInput::new(
                PointerId::Mouse,
                pointer().location(),
                action.clone(),
            ));
    }
    app.world_mut()
        .run_system_cached(generate_hovermap)
        .unwrap();
    app.world_mut().run_system_cached(pointer_events).unwrap();
}

#[test]
fn released_mouse_capture_retains_its_target_once_then_returns_to_physical_hits() {
    use bevy::picking::{
        InteractionPlugin, PickingPlugin, hover::PointerCaptureMap, pointer::PointerAction,
    };
    let (mut app, first) = setup();
    app.add_plugins((PickingPlugin, InteractionPlugin));
    let second = app.world_mut().spawn(Button).id();
    backend_frame(
        &mut app,
        first,
        &[PointerAction::Press(PointerButton::Primary)],
    );
    app.world_mut().resource_mut::<PointerCaptureMap>().capture(
        PointerId::Mouse,
        first,
        hit(first),
    );
    backend_frame(
        &mut app,
        second,
        &[PointerAction::Release(PointerButton::Primary)],
    );
    assert_eq!(
        app.world().resource::<Activations>().0,
        1,
        "the original captured release target still receives its click"
    );
    assert!(
        !app.world()
            .resource::<PointerCaptureMap>()
            .is_captured(&PointerId::Mouse)
    );
    assert!(!app.world().entity(first).contains::<Pressed>());
    let released: Vec<_> = app
        .world_mut()
        .resource_mut::<Messages<PointerRelease>>()
        .drain()
        .collect();
    assert_eq!(released.len(), 1);
    assert_eq!(released[0].entity, first);
    backend_frame(
        &mut app,
        second,
        &[
            PointerAction::Press(PointerButton::Primary),
            PointerAction::Release(PointerButton::Primary),
        ],
    );
    let released: Vec<_> = app
        .world_mut()
        .resource_mut::<Messages<PointerRelease>>()
        .drain()
        .collect();
    assert_eq!(released.len(), 1);
    assert_eq!(released[0].entity, second);
    assert_eq!(app.world().resource::<Activations>().0, 2);
}

#[test]
fn cancelled_mouse_capture_never_becomes_a_release_click() {
    use bevy::picking::{
        InteractionPlugin, PickingPlugin, hover::PointerCaptureMap, pointer::PointerAction,
    };
    let (mut app, first) = setup();
    app.add_plugins((PickingPlugin, InteractionPlugin));
    backend_frame(
        &mut app,
        first,
        &[PointerAction::Press(PointerButton::Primary)],
    );
    app.world_mut().resource_mut::<PointerCaptureMap>().capture(
        PointerId::Mouse,
        first,
        hit(first),
    );
    backend_frame(&mut app, first, &[PointerAction::Cancel]);
    backend_frame(
        &mut app,
        first,
        &[PointerAction::Release(PointerButton::Primary)],
    );
    assert_eq!(app.world().resource::<Activations>().0, 0);
    assert!(
        !app.world()
            .resource::<PointerCaptureMap>()
            .is_captured(&PointerId::Mouse)
    );
    assert!(!app.world().entity(first).contains::<Pressed>());
}

#[test]
fn actual_slider_drag_capture_releases_without_stranded_drag_or_pressed_state() {
    use bevy::{
        picking::{
            InteractionPlugin, PickingPlugin, hover::PointerCaptureMap, pointer::PointerAction,
        },
        ui_widgets::{Slider, SliderDragState, SliderPlugin},
    };
    let (mut app, second) = setup();
    app.add_plugins((PickingPlugin, InteractionPlugin, SliderPlugin))
        .init_resource::<UiScale>();
    let slider = app
        .world_mut()
        .spawn((Node::default(), Slider::default()))
        .id();
    backend_frame(
        &mut app,
        slider,
        &[PointerAction::Press(PointerButton::Primary)],
    );
    backend_frame(
        &mut app,
        slider,
        &[PointerAction::Move {
            delta: Vec2::new(30., 0.),
        }],
    );
    assert!(app.world().get::<SliderDragState>(slider).unwrap().dragging);
    assert!(
        app.world()
            .resource::<PointerCaptureMap>()
            .is_captured(&PointerId::Mouse)
    );
    backend_frame(
        &mut app,
        second,
        &[PointerAction::Release(PointerButton::Primary)],
    );
    assert!(!app.world().get::<SliderDragState>(slider).unwrap().dragging);
    assert!(
        !app.world()
            .resource::<PointerCaptureMap>()
            .is_captured(&PointerId::Mouse)
    );
    assert!(!app.world().entity(slider).contains::<Pressed>());
    assert_eq!(app.world().resource::<Activations>().0, 0);
}

mod visual;
