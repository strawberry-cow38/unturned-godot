//! Replay the shared OS mouse timeline, retaining event-time modifiers. Picking
//! observers still handle touch/custom pointers and synthetic picking fixtures.
use super::*;
use crate::ui::input_order::FrameInput;
use bevy::{input::ButtonState, window::WindowEvent};
#[derive(Resource, Default)]
pub(in super::super) struct Replay {
    pub active: bool,
    captured: Option<Entity>,
}
pub(super) fn install(app: &mut App) {
    app.init_resource::<Replay>().add_systems(
        PreUpdate,
        replay
            .after(InputSystems)
            .before(bevy::picking::PickingSystems::Hover)
            .before(InputFocusSystems::Dispatch),
    );
}
pub(in super::super) fn replay(
    frame: Option<Res<FrameInput>>,
    mut state: ResMut<Replay>,
    ui: Res<UiState>,
    popup: Res<popup::Popup>,
    windows: Query<Entity, With<PrimaryWindow>>,
    mut fields: Query<(Entity, &EditableText, &mut History, Shape), With<Field>>,
    scale: Res<UiScale>,
    time: Res<Time<Real>>,
    mut focus: ResMut<InputFocus>,
) {
    state.active = false;
    let (Some(frame), Ok(window)) = (frame, windows.single()) else {
        return;
    };
    if ui.screen != Screen::Crafting {
        state.captured = None;
        return;
    }
    // A popup owns subsequent release events. Retire the field's gesture now,
    // even when this frame has no mouse events, so dismissal cannot revive it.
    if popup.is_open()
        && let Some(entity) = state.captured.take()
        && let Ok((_, text, mut history, _)) = fields.get_mut(entity)
    {
        history.enqueue(text, Edit::Pointer(Operation::Cancel));
    }
    for event in &frame.events {
        if event.window.is_some_and(|w| w != window) {
            continue;
        }
        let cancel = matches!(&event.event, WindowEvent::KeyboardFocusLost(_))
            || matches!(&event.event, WindowEvent::WindowFocused(e) if !e.focused);
        if cancel {
            if let Some(entity) = state.captured.take()
                && let Ok((_, text, mut history, _)) = fields.get_mut(entity)
            {
                history.enqueue(text, Edit::Pointer(Operation::Cancel));
            }
            continue;
        }
        if !matches!(
            event.event,
            WindowEvent::MouseButtonInput(_) | WindowEvent::CursorMoved(_)
        ) {
            continue;
        }
        state.active = true;
        if popup.is_open() {
            continue;
        }
        let hit = fields.iter().find_map(|(entity, _, _, shape)| {
            point(shape, event.pointer, scale.0)
                .filter(|(_, inside)| *inside)
                .map(|_| entity)
        });
        let target = state.captured.or(hit);
        let Some(entity) = target else {
            continue;
        };
        let Ok((_, text, mut history, shape)) = fields.get_mut(entity) else {
            state.captured = None;
            continue;
        };
        let Some((point, inside)) = point(shape, event.pointer, scale.0) else {
            continue;
        };
        let operation = match &event.event {
            WindowEvent::MouseButtonInput(input) => {
                let button = match input.button {
                    MouseButton::Left => PointerButton::Primary,
                    MouseButton::Right => PointerButton::Secondary,
                    MouseButton::Middle => PointerButton::Middle,
                    _ => continue,
                };
                if input.state == ButtonState::Pressed {
                    if !inside {
                        continue;
                    }
                    if button == PointerButton::Primary {
                        state.captured = Some(entity);
                    }
                    focus.set(entity, FocusCause::Pressed);
                    Operation::Press {
                        point,
                        screen: event.pointer,
                        pointer: PointerId::Mouse,
                        button,
                        time: time.elapsed_secs_f64(),
                        shift: event.buttons.logical.pressed(Key::Shift),
                    }
                } else if button == PointerButton::Primary && state.captured.take() == Some(entity)
                {
                    Operation::Release {
                        point,
                        screen: event.pointer,
                        pointer: PointerId::Mouse,
                        inside,
                        copy: event.buttons.logical.pressed(if cfg!(target_os = "macos") {
                            Key::Super
                        } else {
                            Key::Control
                        }),
                    }
                } else {
                    continue;
                }
            }
            WindowEvent::CursorMoved(_) if state.captured == Some(entity) => Operation::Move {
                point,
                screen: event.pointer,
                pointer: PointerId::Mouse,
            },
            _ => continue,
        };
        history.enqueue(text, Edit::Pointer(operation));
    }
}
