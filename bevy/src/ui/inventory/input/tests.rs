use super::*;
use crate::ui::input_order::FrameInput;
use crate::ui::inventory::{gesture::Gesture, model::*};
use crate::ui::{InventoryItem, InventoryPage};
use bevy::input::{
    keyboard::{Key, KeyboardInput},
    mouse::MouseButtonInput,
};
use bevy::window::{CursorMoved, WindowFocused};
fn key(window: Entity, code: KeyCode, state: ButtonState, repeat: bool) -> WindowEvent {
    WindowEvent::KeyboardInput(KeyboardInput {
        window,
        key_code: code,
        logical_key: Key::Unidentified(bevy::input::keyboard::NativeKey::Unidentified),
        state,
        text: None,
        repeat,
    })
}
fn mouse(window: Entity, button: MouseButton, state: ButtonState) -> WindowEvent {
    WindowEvent::MouseButtonInput(MouseButtonInput {
        window,
        button,
        state,
    })
}
fn motion(window: Entity, x: f32, y: f32) -> WindowEvent {
    WindowEvent::CursorMoved(CursorMoved {
        window,
        position: Vec2::new(x, y),
        delta: None,
    })
}
fn fixture() -> (Vec<InventoryPage>, HitLayout) {
    (
        vec![InventoryPage {
            index: 2,
            width: 6,
            height: 2,
            items: vec![InventoryItem {
                id: 42,
                width: 2,
                height: 1,
                amount: 4,
                ..default()
            }],
        }],
        HitLayout {
            grids: vec![Grid {
                page: 2,
                rect: Rect::new(0., 0., 432., 144.),
                clip: Rect::new(0., 0., 432., 144.),
                slot: false,
            }],
            ..default()
        },
    )
}
#[test]
fn same_frame_ctrl_click_uses_its_event_modifier_not_final_released_state() {
    let mut world = World::new();
    let window = world.spawn_empty().id();
    let mut stream = FrameInput::default();
    let mut gesture = Gesture::default();
    let settings = Settings::default();
    let (pages, layout) = fixture();
    let mut intents = Vec::new();
    for event in [
        key(window, KeyCode::ControlLeft, ButtonState::Pressed, false),
        motion(window, 20., 20.),
        mouse(window, MouseButton::Left, ButtonState::Pressed),
        mouse(window, MouseButton::Left, ButtonState::Released),
        key(window, KeyCode::ControlLeft, ButtonState::Released, false),
    ] {
        stream.push(event);
        if let Some(Input::Gesture(edge, p)) = map(
            stream.events.last().unwrap(),
            window,
            gesture.carry.is_some(),
            &settings,
        ) {
            intents.extend(gesture.input(edge, p, &layout, &pages, &default(), |_| default()));
        }
    }
    assert_eq!(intents, vec![Intent::Drop(Cell::at(2, 0, 0))]);
    assert!(gesture.carry.is_none());
    assert!(
        !stream
            .current(window)
            .unwrap()
            .physical
            .pressed(KeyCode::ControlLeft)
    );
    assert_eq!(pages[0].items[0].amount, 4);
}
#[test]
fn same_frame_drag_retains_press_position_and_all_four_rotations_before_release() {
    let mut world = World::new();
    let window = world.spawn_empty().id();
    let mut stream = FrameInput::default();
    let mut gesture = Gesture::default();
    let settings = Settings::default();
    let (pages, layout) = fixture();
    let mut intents = Vec::new();
    for event in [
        motion(window, 20., 20.),
        mouse(window, MouseButton::Left, ButtonState::Pressed),
        key(window, KeyCode::KeyR, ButtonState::Pressed, false),
        key(window, KeyCode::Space, ButtonState::Pressed, false),
        key(window, KeyCode::Space, ButtonState::Pressed, true),
        motion(window, 236., 20.),
        mouse(window, MouseButton::Left, ButtonState::Released),
    ] {
        stream.push(event);
        if let Some(Input::Gesture(edge, p)) = map(
            stream.events.last().unwrap(),
            window,
            gesture.carry.is_some(),
            &settings,
        ) {
            intents.extend(gesture.input(edge, p, &layout, &pages, &default(), |_| default()));
        }
    }
    assert_eq!(
        intents,
        vec![Intent::LoadOrMove {
            from: Cell::at(2, 0, 0),
            to: Cell::at(2, 3, 0),
            rotation: 3
        }]
    );
    assert_eq!(pages[0].items[0].rot, 0);
}
#[test]
fn other_windows_and_focus_loss_cannot_operate_the_inventory() {
    let mut world = World::new();
    let window = world.spawn_empty().id();
    let other = world.spawn_empty().id();
    let mut stream = FrameInput::default();
    let settings = Settings::default();
    stream.push(mouse(other, MouseButton::Left, ButtonState::Pressed));
    assert!(map(stream.events.last().unwrap(), window, false, &settings).is_none());
    stream.push(WindowEvent::WindowFocused(WindowFocused {
        window,
        focused: false,
    }));
    assert!(matches!(
        map(stream.events.last().unwrap(), window, false, &settings),
        Some(Input::Cancel)
    ));
}
