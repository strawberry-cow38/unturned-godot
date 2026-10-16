//! Map the shared ordered OS batch to InventoryUI's original gesture branches.
use super::super::input_order::OrderedEvent;
use super::gesture::Edge;
use crate::settings::{GameAction, Settings};
use bevy::{input::ButtonState, prelude::*, window::WindowEvent};

#[derive(Debug)]
pub(super) enum Input {
    Gesture(Edge, Vec2),
    Wheel(Vec2, f32),
    RelativeMotion(Vec2),
    Cancel,
}
fn hotbar(matches: impl Fn(GameAction) -> bool) -> Option<u8> {
    use GameAction::*;
    [
        Hotbar3, Hotbar4, Hotbar5, Hotbar6, Hotbar7, Hotbar8, Hotbar9, Hotbar10,
    ]
    .into_iter()
    .position(matches)
    .map(|i| i as u8 + 3)
}
/// No state reducer lives here: search/drag and Inventory consume the same
/// event-time physical/logical keys, pointer coordinates and window isolation.
pub(super) fn map(
    input: &OrderedEvent,
    window: Entity,
    carrying: bool,
    settings: &Settings,
) -> Option<Input> {
    if input.window.is_some_and(|w| w != window) {
        return None;
    }
    let matches = |action| {
        settings
            .bindings
            .get(&action)
            .is_some_and(|binding| binding.matches_window_event(&input.event))
    };
    let control = input.buttons.physical.pressed(KeyCode::ControlLeft)
        || input.buttons.physical.pressed(KeyCode::ControlRight);
    let edge = match &input.event {
        WindowEvent::KeyboardFocusLost(_) => return Some(Input::Cancel),
        WindowEvent::WindowFocused(e) if !e.focused => return Some(Input::Cancel),
        WindowEvent::MouseMotion(e) => return Some(Input::RelativeMotion(e.delta)),
        WindowEvent::CursorMoved(_) => Some(Edge::Motion { delta: input.delta }),
        WindowEvent::MouseWheel(e) => {
            let notches = match e.unit {
                bevy::input::mouse::MouseScrollUnit::Line => e.y,
                bevy::input::mouse::MouseScrollUnit::Pixel => e.y / 60.,
            };
            return Some(Input::Wheel(input.pointer, notches));
        }
        WindowEvent::MouseButtonInput(e) => {
            let down = e.state == ButtonState::Pressed;
            match e.button {
                MouseButton::Left => Some(Edge::Left { down, control }),
                MouseButton::Right => Some(Edge::Right { down, control }),
                _ if down && !carrying && matches(GameAction::ItemUses) => {
                    Some(Edge::Lookup { uses: true })
                }
                _ if down && !carrying && matches(GameAction::ItemRecipes) => {
                    Some(Edge::Lookup { uses: false })
                }
                _ if down => hotbar(matches).map(Edge::Bind),
                _ => None,
            }
        }
        WindowEvent::KeyboardInput(e) if e.state == ButtonState::Pressed => {
            if carrying && matches!(e.key_code, KeyCode::KeyR | KeyCode::Space) {
                Some(Edge::Rotate)
            } else if !carrying && !e.repeat && matches(GameAction::ItemUses) {
                Some(Edge::Lookup { uses: true })
            } else if !carrying && !e.repeat && matches(GameAction::ItemRecipes) {
                Some(Edge::Lookup { uses: false })
            } else {
                hotbar(matches).map(Edge::Bind)
            }
        }
        _ => None,
    };
    edge.map(|e| Input::Gesture(e, input.pointer))
}
#[cfg(test)]
mod tests;
