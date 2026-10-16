use super::super::{controller::Menu, gesture::Edge, input};
use super::*;
use crate::ui::{Screen, UiState, input_order::FrameInput};
use bevy::{
    input::{
        ButtonState,
        mouse::{MouseButtonInput, MouseMotion},
    },
    window::{CursorGrabMode, CursorMoved, CursorOptions, PrimaryWindow, WindowEvent},
};
fn left(down: bool) -> Edge {
    Edge::Left {
        down,
        control: false,
    }
}
#[test]
fn original_godot_accelerated_drag_uses_total_relative_distance_and_restores_initial_value() {
    let field = Rect::new(100., 200., 200., 231.);
    let anchor = Vec2::new(192., 207.);
    let mut state = State::default();
    let mut value = 50;
    let mut values = Vec::new();
    state.input(
        &Input::Gesture(left(true), anchor),
        field,
        Vec2::ZERO,
        false,
        &mut value,
        499,
    );
    values.push(value);
    assert!(!state.input(
        &Input::RelativeMotion(Vec2::new(0., -3.)),
        field,
        Vec2::ZERO,
        false,
        &mut value,
        499
    ));
    state.input(
        &Input::Gesture(
            Edge::Motion {
                delta: Vec2::new(0., -3.),
            },
            anchor + Vec2::new(0., -3.),
        ),
        field,
        Vec2::ZERO,
        false,
        &mut value,
        499,
    );
    values.push(value);
    assert!(state.dragging);
    state.raw_mode = true;
    for delta in [-10., -20., 10., 30.] {
        state.input(
            &Input::RelativeMotion(Vec2::new(0., delta)),
            field,
            Vec2::ZERO,
            false,
            &mut value,
            499,
        );
        let expected = value;
        // Locked-cursor warps and the old arrow-repeat clock cannot add another change.
        state.input(
            &Input::Gesture(
                Edge::Motion {
                    delta: Vec2::new(0., -900.),
                },
                Vec2::ZERO,
            ),
            field,
            Vec2::ZERO,
            false,
            &mut value,
            499,
        );
        state.step(3., field, &mut value, 499);
        assert_eq!(value, expected);
        values.push(value);
    }
    state.input(
        &Input::Gesture(left(false), anchor),
        field,
        Vec2::ZERO,
        false,
        &mut value,
        499,
    );
    values.push(value);
    assert!(!state.dragging && !state.raw_mode && !state.held);
    let source: Vec<u16> = serde_json::from_str(include_str!("drag-source.json")).unwrap();
    assert_eq!(values, source);
}
#[test]
fn coalesced_cursor_drag_before_capture_applies_once_and_global_raw_motion_stays_independent() {
    let mut world = World::new();
    let window = world.spawn_empty().id();
    let field = Rect::new(100., 200., 200., 231.);
    let anchor = Vec2::new(192., 207.);
    let mut stream = FrameInput::default();
    let mut state = State::default();
    let mut value = 50;
    for event in [
        WindowEvent::CursorMoved(CursorMoved {
            window,
            position: anchor,
            delta: None,
        }),
        WindowEvent::MouseButtonInput(MouseButtonInput {
            window,
            button: MouseButton::Left,
            state: ButtonState::Pressed,
        }),
        WindowEvent::MouseMotion(MouseMotion {
            delta: Vec2::new(0., -3.),
        }),
        WindowEvent::CursorMoved(CursorMoved {
            window,
            position: anchor + Vec2::new(0., -3.),
            delta: None,
        }),
        WindowEvent::MouseMotion(MouseMotion {
            delta: Vec2::new(0., -10.),
        }),
        WindowEvent::CursorMoved(CursorMoved {
            window,
            position: anchor + Vec2::new(0., -13.),
            delta: None,
        }),
        WindowEvent::MouseButtonInput(MouseButtonInput {
            window,
            button: MouseButton::Left,
            state: ButtonState::Released,
        }),
    ] {
        stream.push(event);
        if let Some(input) = input::map(stream.events.last().unwrap(), window, false, &default()) {
            state.input(&input, field, Vec2::ZERO, false, &mut value, 499);
        }
    }
    assert_eq!(
        value, 52,
        "raw/cursor pair must not count twice before actual capture"
    );
    assert!(!state.dragging);
    assert_eq!(stream.pointer(window), Some(anchor + Vec2::new(0., -13.)));
    let motion = stream
        .events
        .iter()
        .find(|e| matches!(e.event, WindowEvent::MouseMotion(_)))
        .unwrap();
    assert_eq!(motion.window, None);
    assert_eq!(motion.pointer, Vec2::ZERO);
    assert_eq!(motion.delta, Vec2::new(0., -3.));
}
#[test]
fn production_cursor_lease_releases_on_close_and_focus_loss_and_does_not_override_play_capture() {
    let mut app = App::new();
    app.init_resource::<Menu>()
        .init_resource::<capture::Lease>()
        .insert_resource(UiState {
            screen: Screen::Inventory,
            ..default()
        })
        .add_systems(PostUpdate, capture::apply);
    let window = app
        .world_mut()
        .spawn((
            PrimaryWindow,
            Window {
                focused: true,
                ..default()
            },
            CursorOptions::default(),
        ))
        .id();
    let anchor = Vec2::new(192., 207.);
    {
        let mut menu = app.world_mut().resource_mut::<Menu>();
        menu.open = true;
        menu.spin = State {
            dragging: true,
            held: true,
            anchor,
            ..default()
        };
    }
    app.update();
    assert_eq!(
        app.world().get::<CursorOptions>(window).unwrap().grab_mode,
        CursorGrabMode::Locked
    );
    assert!(!app.world().get::<CursorOptions>(window).unwrap().visible);
    assert!(app.world().resource::<Menu>().spin.raw_mode);
    app.world_mut().resource_mut::<Menu>().spin.cancel();
    app.update();
    assert_eq!(
        app.world().get::<CursorOptions>(window).unwrap().grab_mode,
        CursorGrabMode::None
    );
    assert_eq!(
        app.world().get::<Window>(window).unwrap().cursor_position(),
        Some(anchor)
    );
    {
        let mut menu = app.world_mut().resource_mut::<Menu>();
        menu.spin = State {
            dragging: true,
            held: true,
            anchor,
            ..default()
        };
    }
    app.update();
    app.world_mut().resource_mut::<Menu>().open = false;
    app.world_mut().resource_mut::<UiState>().screen = Screen::Playing;
    app.update();
    assert!(!app.world().resource::<Menu>().spin.dragging);
    assert_eq!(
        app.world().get::<CursorOptions>(window).unwrap().grab_mode,
        CursorGrabMode::Locked
    );
    {
        let mut menu = app.world_mut().resource_mut::<Menu>();
        menu.open = true;
        menu.spin = State {
            dragging: true,
            held: true,
            anchor,
            ..default()
        };
    }
    app.world_mut().resource_mut::<UiState>().screen = Screen::Inventory;
    app.update();
    app.world_mut().get_mut::<Window>(window).unwrap().focused = false;
    app.update();
    assert!(!app.world().resource::<Menu>().spin.dragging);
    assert!(app.world().get::<CursorOptions>(window).unwrap().visible);
}
