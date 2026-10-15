//! Ordered OS input for source widgets that use event-time modifiers and cursor
//! positions. Consumers read the same immutable batch independently; there is no
//! global cursor that one widget can consume before another sees its events.
use bevy::{
    input::{ButtonState, keyboard::Key},
    picking::PickingSystems,
    prelude::*,
    window::WindowEvent,
};
use std::collections::HashMap;

/// Held-state snapshots only. Derive edges from `OrderedEvent.event`; the
/// ButtonInput transition flags are not a second event stream.
#[derive(Clone, Default, Debug)]
pub(crate) struct Buttons {
    pub physical: ButtonInput<KeyCode>,
    pub logical: ButtonInput<Key>,
    pub mouse: ButtonInput<MouseButton>,
}
#[derive(Clone, Debug)]
pub(crate) struct OrderedEvent {
    pub event: WindowEvent,
    pub window: Option<Entity>,
    pub pointer: Vec2,
    pub delta: Vec2,
    pub buttons: Buttons,
}
#[derive(Default)]
struct WindowState {
    pointer: Vec2,
    buttons: Buttons,
    logical_by_code: HashMap<KeyCode, Key>,
}
impl WindowState {
    fn clear(&mut self) {
        self.buttons = default();
        self.logical_by_code.clear();
    }
}
#[derive(Resource, Default)]
pub(crate) struct FrameInput {
    pub events: Vec<OrderedEvent>,
    windows: HashMap<Entity, WindowState>,
}
impl FrameInput {
    pub fn current(&self, window: Entity) -> Option<&Buttons> {
        self.windows.get(&window).map(|w| &w.buttons)
    }
    pub fn pointer(&self, window: Entity) -> Option<Vec2> {
        self.windows.get(&window).map(|w| w.pointer)
    }
    pub fn begin(&mut self) {
        self.events.clear();
    }
    pub fn push(&mut self, event: WindowEvent) {
        let window = match &event {
            WindowEvent::CursorMoved(e) => Some(e.window),
            WindowEvent::MouseButtonInput(e) => Some(e.window),
            WindowEvent::MouseWheel(e) => Some(e.window),
            WindowEvent::KeyboardInput(e) => Some(e.window),
            WindowEvent::WindowFocused(e) => Some(e.window),
            WindowEvent::WindowDestroyed(e) => {
                self.windows.remove(&e.window);
                return;
            }
            WindowEvent::KeyboardFocusLost(_) => {
                for state in self.windows.values_mut() {
                    state.clear();
                }
                None
            }
            _ => return,
        };
        let mut delta = Vec2::ZERO;
        let (pointer, buttons) = if let Some(window) = window {
            let state = self.windows.entry(window).or_default();
            match &event {
                WindowEvent::CursorMoved(e) => {
                    delta = e.position - state.pointer;
                    state.pointer = e.position;
                }
                WindowEvent::KeyboardInput(e) => {
                    if e.state == ButtonState::Pressed {
                        state.buttons.physical.press(e.key_code);
                        state
                            .logical_by_code
                            .insert(e.key_code, e.logical_key.clone());
                    } else {
                        state.buttons.physical.release(e.key_code);
                        state.logical_by_code.remove(&e.key_code);
                    }
                    // Two physical Shift/Ctrl keys can share a logical key.
                    // Releasing either one must not release the other.
                    state.buttons.logical.reset_all();
                    for key in state.logical_by_code.values() {
                        state.buttons.logical.press(key.clone());
                    }
                }
                WindowEvent::MouseButtonInput(e) => {
                    if e.state == ButtonState::Pressed {
                        state.buttons.mouse.press(e.button);
                    } else {
                        state.buttons.mouse.release(e.button);
                    }
                }
                WindowEvent::WindowFocused(e) if !e.focused => state.clear(),
                _ => {}
            }
            (state.pointer, state.buttons.clone())
        } else {
            (Vec2::ZERO, Buttons::default())
        };
        self.events.push(OrderedEvent {
            event,
            window,
            pointer,
            delta,
            buttons,
        });
    }
}
pub(super) struct InputOrderPlugin;
impl Plugin for InputOrderPlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<FrameInput>().add_systems(
            First,
            collect
                .after(bevy::ecs::message::message_update_system)
                .before(PickingSystems::Input),
        );
    }
}
fn collect(mut events: MessageReader<WindowEvent>, mut frame: ResMut<FrameInput>) {
    frame.begin();
    for event in events.read() {
        frame.push(event.clone());
    }
}
#[cfg(test)]
mod tests;
