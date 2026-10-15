use super::*;
use bevy::{
    input::{keyboard::KeyboardInput, mouse::MouseButtonInput},
    window::CursorMoved,
};
fn key(window: Entity, code: KeyCode, logical: Key, down: bool) -> WindowEvent {
    WindowEvent::KeyboardInput(KeyboardInput {
        window,
        key_code: code,
        logical_key: logical,
        state: if down {
            ButtonState::Pressed
        } else {
            ButtonState::Released
        },
        repeat: false,
        text: None,
    })
}
#[test]
fn one_batch_retains_independent_event_time_modifiers_positions_and_window_state() {
    let mut world = World::new();
    let first = world.spawn_empty().id();
    let second = world.spawn_empty().id();
    let mut frame = FrameInput::default();
    for event in [
        key(first, KeyCode::ControlLeft, Key::Control, true),
        WindowEvent::CursorMoved(CursorMoved {
            window: first,
            position: Vec2::new(12., 34.),
            delta: None,
        }),
        WindowEvent::MouseButtonInput(MouseButtonInput {
            window: first,
            button: MouseButton::Left,
            state: ButtonState::Released,
        }),
        key(first, KeyCode::ControlLeft, Key::Control, false),
        WindowEvent::CursorMoved(CursorMoved {
            window: second,
            position: Vec2::new(900., 800.),
            delta: None,
        }),
    ] {
        frame.push(event);
    }
    assert!(frame.events[2].buttons.logical.pressed(Key::Control));
    assert!(
        frame.events[2]
            .buttons
            .physical
            .pressed(KeyCode::ControlLeft)
    );
    assert_eq!(frame.events[2].pointer, Vec2::new(12., 34.));
    assert!(!frame.current(first).unwrap().logical.pressed(Key::Control));
    assert_eq!(frame.pointer(first), Some(Vec2::new(12., 34.)));
    assert!(!frame.current(second).unwrap().logical.pressed(Key::Control));
    frame.begin();
    assert!(frame.events.is_empty());
    assert_eq!(frame.pointer(first), Some(Vec2::new(12., 34.)));
}
#[test]
fn releasing_one_physical_modifier_preserves_the_other_and_focus_loss_clears_both() {
    let mut world = World::new();
    let window = world.spawn_empty().id();
    let mut frame = FrameInput::default();
    for event in [
        key(window, KeyCode::ShiftLeft, Key::Shift, true),
        key(window, KeyCode::ShiftRight, Key::Shift, true),
        key(window, KeyCode::ShiftLeft, Key::Shift, false),
    ] {
        frame.push(event);
    }
    assert!(frame.current(window).unwrap().logical.pressed(Key::Shift));
    frame.push(WindowEvent::KeyboardFocusLost(
        bevy::input::keyboard::KeyboardFocusLost,
    ));
    assert!(!frame.current(window).unwrap().logical.pressed(Key::Shift));
    assert!(
        !frame
            .current(window)
            .unwrap()
            .physical
            .pressed(KeyCode::ShiftRight)
    );
}
