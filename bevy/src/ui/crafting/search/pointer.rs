//! Source LineEdit pointer selection and selected-text move/copy (4.6 lines430–659,
//! 1086–1158). A child hit surface stops the stock TextInput pointer observers;
//! keyboard/IME/layout continue to use the same persistent TextInput entity.
use super::*;
use bevy::picking::events::PointerCancel;
pub(super) mod ordered;
use bevy::ui::{ComputedUiRenderTargetInfo, UiGlobalTransform};

#[derive(Component)]
pub(super) struct Surface(pub Entity);
#[derive(Component, Default)]
pub(super) struct State {
    gesture: Option<Gesture>,
    last_press: Option<(f64, Vec2)>,
    last_double: Option<(f64, Vec2)>,
}
struct Gesture {
    pointer: PointerId,
    start: Vec2,
    selection: Option<(std::ops::Range<usize>, String)>,
    dragging: bool,
}
pub(super) enum Operation {
    Press {
        point: Vec2,
        screen: Vec2,
        pointer: PointerId,
        button: PointerButton,
        time: f64,
        shift: bool,
    },
    Move {
        point: Vec2,
        screen: Vec2,
        pointer: PointerId,
    },
    Release {
        point: Vec2,
        screen: Vec2,
        pointer: PointerId,
        inside: bool,
        copy: bool,
    },
    Cancel,
}
pub(super) fn install(app: &mut App) {
    ordered::install(app);
    app.add_observer(press)
        .add_observer(motion)
        .add_observer(release)
        .add_observer(cancel);
}
type Shape<'a> = (
    &'a ComputedNode,
    &'a UiGlobalTransform,
    &'a ComputedUiRenderTargetInfo,
);
fn point(shape: Shape, screen: Vec2, scale: f32) -> Option<(Vec2, bool)> {
    let (node, transform, target) = shape;
    let local = transform
        .try_inverse()?
        .transform_point2(screen * target.scale_factor() / scale);
    Some((
        local - node.content_box().min,
        node.size().cmpgt(Vec2::ZERO).all()
            && Rect::from_center_size(Vec2::ZERO, node.size()).contains(local),
    ))
}
fn press(
    mut event: On<PointerPress>,
    surfaces: Query<&Surface>,
    shapes: Query<Shape>,
    mut fields: Query<(&EditableText, &mut History)>,
    keys: Res<ButtonInput<Key>>,
    scale: Res<UiScale>,
    mut captures: ResMut<PointerCaptureMap>,
    time: Res<Time<Real>>,
    replay: Res<ordered::Replay>,
) {
    let Ok(surface) = surfaces.get(event.entity) else {
        return;
    };
    event.propagate(false);
    if event.pointer.id == PointerId::Mouse && replay.active {
        return;
    }
    let Ok((text, mut history)) = fields.get_mut(surface.0) else {
        return;
    };
    let Some((point, _)) = shapes
        .get(surface.0)
        .ok()
        .and_then(|s| point(s, event.pointer.position, scale.0))
    else {
        return;
    };
    if event.button == PointerButton::Primary {
        captures.capture(event.pointer.id, event.entity, event.hit.clone());
    }
    history.enqueue(
        text,
        Edit::Pointer(Operation::Press {
            point,
            screen: event.pointer.position,
            pointer: event.pointer.id,
            button: event.button,
            time: time.elapsed_secs_f64(),
            shift: keys.pressed(Key::Shift),
        }),
    );
}
fn motion(
    mut event: On<PointerMove>,
    surfaces: Query<&Surface>,
    shapes: Query<Shape>,
    mut fields: Query<(&EditableText, &mut History)>,
    scale: Res<UiScale>,
    replay: Res<ordered::Replay>,
) {
    let Ok(surface) = surfaces.get(event.entity) else {
        return;
    };
    event.propagate(false);
    if event.pointer.id == PointerId::Mouse && replay.active {
        return;
    }
    let Ok((text, mut history)) = fields.get_mut(surface.0) else {
        return;
    };
    let Some((point, _)) = shapes
        .get(surface.0)
        .ok()
        .and_then(|s| point(s, event.pointer.position, scale.0))
    else {
        return;
    };
    history.enqueue(
        text,
        Edit::Pointer(Operation::Move {
            point,
            screen: event.pointer.position,
            pointer: event.pointer.id,
        }),
    );
}
fn release(
    mut event: On<PointerRelease>,
    surfaces: Query<&Surface>,
    shapes: Query<Shape>,
    mut fields: Query<(&EditableText, &mut History)>,
    keys: Res<ButtonInput<Key>>,
    scale: Res<UiScale>,
    replay: Res<ordered::Replay>,
) {
    let Ok(surface) = surfaces.get(event.entity) else {
        return;
    };
    event.propagate(false);
    if event.pointer.id == PointerId::Mouse && replay.active {
        return;
    }
    if event.button != PointerButton::Primary {
        return;
    }
    let Ok((text, mut history)) = fields.get_mut(surface.0) else {
        return;
    };
    let Some((point, inside)) = shapes
        .get(surface.0)
        .ok()
        .and_then(|s| point(s, event.pointer.position, scale.0))
    else {
        return;
    };
    history.enqueue(
        text,
        Edit::Pointer(Operation::Release {
            point,
            screen: event.pointer.position,
            pointer: event.pointer.id,
            inside,
            copy: keys.pressed(if cfg!(target_os = "macos") {
                Key::Super
            } else {
                Key::Control
            }),
        }),
    );
}
fn cancel(
    mut event: On<PointerCancel>,
    surfaces: Query<&Surface>,
    mut fields: Query<(&EditableText, &mut History)>,
) {
    let Ok(surface) = surfaces.get(event.entity) else {
        return;
    };
    event.propagate(false);
    if let Ok((text, mut history)) = fields.get_mut(surface.0) {
        history.enqueue(text, Edit::Pointer(Operation::Cancel));
    }
}
fn range(text: &EditableText) -> std::ops::Range<usize> {
    let selection = text.editor.raw_selection();
    let a = selection.anchor().index();
    let b = selection.focus().index();
    a.min(b)..a.max(b)
}
fn at(text: &EditableText, point: Vec2, fonts: &mut FontCx, layouts: &mut LayoutCx) -> usize {
    let mut editor = text.editor.clone();
    editor
        .driver(fonts, &mut layouts.0)
        .move_to_point(point.x, point.y);
    editor.raw_selection().focus().index()
}
pub(super) fn apply(
    operation: Operation,
    entity: Entity,
    text: &mut EditableText,
    state: &mut State,
    fonts: &mut FontCx,
    layouts: &mut LayoutCx,
    primary: &mut primary::Primary,
    popup: &mut popup::Popup,
) -> Option<Edit> {
    // Source apply_ime commits preedit before a new pointer operation. Layout
    // and selection remain in the same Unicode editor (never insert it twice).
    if text.is_composing() && matches!(operation, Operation::Press { .. }) {
        text.editor.driver(fonts, &mut layouts.0).finish_compose();
    }
    match operation {
        Operation::Press {
            point,
            screen,
            pointer,
            button,
            time,
            shift,
        } => {
            let point = point + text.viewport.offset;
            if button == PointerButton::Secondary {
                if range(text).is_empty() {
                    text.queue_edit(TextEdit::MoveToPoint(point));
                }
                popup.request = Some((entity, screen));
            } else if button == PointerButton::Middle {
                if primary.supported() {
                    return Some(Edit::Primary(primary.read(), point));
                }
            } else {
                let double = state
                    .last_press
                    .is_some_and(|(at, pos)| time - at < 0.4 && screen.distance(pos) < 5.);
                let triple = !double
                    && state
                        .last_double
                        .is_some_and(|(at, pos)| time - at < 0.6 && screen.distance(pos) < 5.);
                let count = if triple {
                    state.last_double = None;
                    3
                } else if double {
                    state.last_double = Some((time, screen));
                    2
                } else {
                    1
                };
                state.last_press = if double { None } else { Some((time, screen)) };
                let selected = range(text);
                let index = at(text, point, fonts, layouts);
                let selection = (!shift
                    && count == 1
                    && !selected.is_empty()
                    && index >= selected.start
                    && index <= selected.end)
                    .then(|| (selected, text.value().to_string()));
                if count >= 3 && !shift {
                    let end = text.value().to_string().len();
                    text.editor
                        .driver(fonts, &mut layouts.0)
                        .select_byte_range(end, 0);
                } else if selection.is_none() {
                    text.queue_edit(if shift {
                        TextEdit::ShiftClickExtension(point)
                    } else if count == 2 {
                        TextEdit::SelectWordAtPoint(point)
                    } else if count >= 3 {
                        TextEdit::SelectAll
                    } else {
                        TextEdit::MoveToPoint(point)
                    });
                }
                state.gesture = Some(Gesture {
                    pointer,
                    start: screen,
                    selection,
                    dragging: false,
                });
                if count >= 2 {
                    return Some(Edit::PublishPrimary);
                }
            }
        }
        Operation::Move {
            point,
            screen,
            pointer,
        } => {
            if let Some(gesture) = state.gesture.as_mut().filter(|g| g.pointer == pointer) {
                if gesture.selection.is_some() {
                    // Viewport GUI drag threshold, 10 physical UI pixels.
                    gesture.dragging |= screen.distance(gesture.start) > 10.;
                    if gesture.dragging {
                        popup.drag =
                            Some((screen, text.editor.selected_text().unwrap_or("").into()));
                    }
                } else {
                    text.queue_edit(TextEdit::ExtendSelectionToPoint(
                        point + text.viewport.offset,
                    ));
                }
            }
        }
        Operation::Release {
            point,
            screen,
            pointer,
            inside,
            copy,
        } => {
            if state.gesture.as_ref().is_none_or(|g| g.pointer != pointer) {
                return None;
            }
            let gesture = state.gesture.take().unwrap();
            popup.drag = None;
            // Source publishes the old selected text before its release/deselect.
            if let Some(selected) = text.editor.selected_text() {
                primary.write(selected);
            }
            if let Some((selection, original)) = gesture.selection {
                if original != text.value().to_string() {
                    return None;
                }
                let point = point + text.viewport.offset;
                if gesture.dragging {
                    if !inside {
                        return None;
                    }
                    let index = at(text, point, fonts, layouts);
                    let within = if copy {
                        index > selection.start && index < selection.end
                    } else {
                        index >= selection.start && index <= selection.end
                    };
                    if within {
                        return None;
                    }
                    let value = original[selection.clone()].to_string();
                    let mut index = index;
                    let mut driver = text.editor.driver(fonts, &mut layouts.0);
                    if !copy {
                        driver.select_byte_range(selection.start, selection.end);
                        driver.delete_selection();
                        if index > selection.end {
                            index -= selection.len();
                        }
                    }
                    driver.move_to_byte(index);
                    driver.insert_or_replace_selection(&value);
                    driver.select_byte_range(index, index + value.len());
                } else {
                    text.queue_edit(TextEdit::MoveToPoint(point));
                }
            }
        }
        Operation::Cancel => {
            state.gesture = None;
            popup.drag = None;
        }
    }
    None
}
