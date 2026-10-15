//! Persistent original CraftingMenu LineEdit backed by Bevy's Unicode editor.
//! Root repainting must never replace its caret, selection, pending paste or IME.
use super::*;
use bevy::{
    clipboard::{Clipboard, ClipboardRead},
    input::InputSystems,
    input_focus::{
        FocusCause, FocusedInput, InputFocus, InputFocusSystems, tab_navigation::TabIndex,
    },
    text::{
        EditableText, EditableTextSystems, FontCx, LayoutCx, TextCursorStyle, TextEdit,
        TextEditChange,
    },
    ui_widgets::TextInput,
};
use std::collections::VecDeque;

#[derive(Component)]
pub(super) struct Field {
    placeholder: Entity,
    requested: String,
}
#[derive(Resource, Default)]
struct FocusFrame {
    before: Option<Entity>,
    passive: Option<Entity>,
    reactivated: bool,
}
#[derive(Component)]
pub(super) struct History {
    states: Vec<EditState>,
    at: usize,
    actions: Vec<(usize, bool)>,
    pending: VecDeque<Edit>,
}
struct EditState {
    value: String,
    caret: usize,
    scroll: Vec2,
}
enum Edit {
    Native(TextEdit),
    Paste(ClipboardRead),
    History(bool),
}
impl History {
    fn new(text: &EditableText) -> Self {
        Self {
            states: vec![Self::snapshot(text)],
            at: 0,
            actions: vec![],
            pending: VecDeque::new(),
        }
    }
    fn snapshot(text: &EditableText) -> EditState {
        EditState {
            value: text.value().to_string(),
            caret: text.editor.raw_selection().focus().index(),
            scroll: text.viewport.offset,
        }
    }
    fn remember(&mut self, text: &EditableText) {
        if text.is_composing() {
            return;
        }
        if self.states[self.at].value != text.value().to_string() {
            self.states.truncate(self.at + 1);
            self.states.push(Self::snapshot(text));
            self.at += 1;
        }
    }
    fn restore(
        &mut self,
        text: &mut EditableText,
        redo: bool,
        fonts: &mut FontCx,
        layouts: &mut LayoutCx,
    ) {
        let next = if redo {
            (self.at + 1).min(self.states.len() - 1)
        } else {
            self.at.saturating_sub(1)
        };
        if next == self.at {
            return;
        }
        self.at = next;
        text.editor.set_text(&self.states[next].value);
        text.editor
            .driver(fonts, &mut layouts.0)
            .move_to_byte(self.states[next].caret);
        text.viewport.offset = self.states[next].scroll;
        text.pending_edits.clear();
        text.pending_paste = None;
        text.queue_edit(TextEdit::CollapseSelection);
    }
}
pub(super) struct SearchPlugin;
impl Plugin for SearchPlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<FocusFrame>()
            .add_observer(changed)
            .add_observer(shortcuts)
            .add_observer(pointer_focus)
            .add_systems(
                PreUpdate,
                remember_focus
                    .after(InputSystems)
                    .before(InputFocusSystems::Dispatch),
            )
            .add_systems(Update, synchronize.in_set(UiUpdate).before(keyboard_ui))
            .add_systems(PostUpdate, edit_history.before(EditableTextSystems));
    }
}
fn pointer_focus(
    event: On<PointerPress>,
    fields: Query<(), With<Field>>,
    mut state: ResMut<FocusFrame>,
) {
    if event.entity == event.original_event_target() && !fields.contains(event.entity) {
        state.passive = None;
    }
}
fn remember_focus(
    mut frame: ResMut<FocusFrame>,
    mut focus: ResMut<InputFocus>,
    fields: Query<(), With<Field>>,
    keys: Res<ButtonInput<KeyCode>>,
    ui: Res<UiState>,
) {
    frame.reactivated = false;
    if ui.screen != Screen::Crafting {
        frame.before = None;
        frame.passive = None;
        return;
    }
    if focus.get().is_none()
        && keys.just_pressed(KeyCode::Enter)
        && let Some(entity) = frame.passive.filter(|e| fields.contains(*e))
    {
        // Godot's first Enter resumes editing a still-focused, unedited field.
        focus.set(entity, FocusCause::Navigated);
        frame.reactivated = true;
    }
    frame.before = focus.get().filter(|e| fields.contains(*e));
}
fn synchronize(
    ui: Res<UiState>,
    mut menu: ResMut<Menu>,
    keys: Res<ButtonInput<KeyCode>>,
    mut focus: ResMut<InputFocus>,
    mut frame: ResMut<FocusFrame>,
    fields: Query<(Entity, &Field, &EditableText)>,
    mut placeholders: Query<&mut Visibility>,
    mut borders: Query<&mut BorderColor>,
) {
    let current = focus.get().filter(|e| fields.get(*e).is_ok());
    if ui.screen != Screen::Crafting {
        if current.is_some() {
            focus.clear();
        }
        menu.typing = false;
        frame.passive = None;
        return;
    }
    let stop = frame.before.is_some()
        && (keys.just_pressed(KeyCode::Escape) || keys.just_pressed(KeyCode::Enter))
        && !frame.reactivated
        && frame
            .before
            .and_then(|e| fields.get(e).ok())
            .is_some_and(|(_, _, t)| !t.is_composing());
    if stop {
        frame.passive = frame.before;
        focus.clear();
    }
    menu.typing = current.is_some() || stop;
    for (entity, field, text) in &fields {
        if let Ok(mut visible) = placeholders.get_mut(field.placeholder) {
            *visible = if text.value().to_string().is_empty() && !text.is_composing() {
                Visibility::Inherited
            } else {
                Visibility::Hidden
            };
        }
        if let Ok(mut border) = borders.get_mut(entity) {
            *border = BorderColor::all(
                if current == Some(entity) || frame.passive == Some(entity) {
                    Color::srgb(0.62, 0.78, 0.94)
                } else {
                    Color::NONE
                },
            );
        }
    }
}
fn changed(
    event: On<TextEditChange>,
    fields: Query<&EditableText, With<Field>>,
    mut menu: ResMut<Menu>,
    ui: Res<UiState>,
) {
    if ui.screen != Screen::Crafting {
        return;
    }
    if let Ok(text) = fields.get(event.event_target()) {
        if text.is_composing() {
            return;
        }
        let value = text.value().to_string();
        if menu.browse.search != value {
            menu.browse.search = value;
            menu.browse.lookup.clear();
            menu.browse.selected = -1;
            menu.scroll[1] = 0.;
            menu.poll = 0.;
        }
    }
}
fn shortcuts(
    mut event: On<FocusedInput<KeyboardInput>>,
    keys: Res<ButtonInput<Key>>,
    mut fields: Query<(&EditableText, &mut History), With<Field>>,
) {
    let Ok((text, mut history)) = fields.get_mut(event.original_event_target()) else {
        return;
    };
    if !event.input.state.is_pressed() || text.is_composing() {
        return;
    }
    let command = if cfg!(target_os = "macos") {
        Key::Super
    } else {
        Key::Control
    };
    if !keys.pressed(command) {
        return;
    }
    let letter = match &event.input.logical_key {
        Key::Character(s) => s.to_ascii_lowercase(),
        _ => String::new(),
    };
    let redo = match letter.as_str() {
        "z" => keys.pressed(Key::Shift),
        "y" if !cfg!(target_os = "macos") => true,
        _ => return,
    };
    history.actions.push((text.pending_edits.len(), redo));
    event.propagate(false);
}
fn edit_history(
    mut fields: Query<(&mut EditableText, &mut History), With<Field>>,
    mut fonts: ResMut<FontCx>,
    mut layouts: ResMut<LayoutCx>,
    mut clipboard: ResMut<Clipboard>,
) {
    for (mut text, mut history) in &mut fields {
        let edits = std::mem::take(&mut text.pending_edits);
        let mut actions = std::mem::take(&mut history.actions).into_iter().peekable();
        for (index, edit) in edits.into_iter().map(Some).chain([None]).enumerate() {
            while actions.peek().is_some_and(|(at, _)| *at == index) {
                history
                    .pending
                    .push_back(Edit::History(actions.next().unwrap().1));
            }
            if let Some(edit) = edit {
                history.pending.push_back(Edit::Native(edit));
            }
        }
        if text.pending_paste.is_some() {
            text.apply_pending_edits(&mut fonts, &mut layouts.0, &mut clipboard, |c| c >= ' ');
            history.remember(&text);
            if text.pending_paste.is_some() {
                continue;
            }
        }
        while let Some(edit) = history.pending.pop_front() {
            // Godot LineEdit defers insertion/delete/paste history until the
            // frame ends, but _backspace commits immediately. No time grouping.
            let immediate = matches!(
                &edit,
                Edit::Native(TextEdit::Backspace | TextEdit::BackspaceWord)
            );
            match edit {
                Edit::Native(TextEdit::Paste) => {
                    history
                        .pending
                        .push_front(Edit::Paste(clipboard.fetch_text()));
                    continue;
                }
                Edit::Paste(mut read) => match read.poll_result() {
                    Some(Ok(value)) => {
                        // Godot String.strip_escapes removes only ASCII <32,
                        // retaining DEL/C1 and every Unicode character. Do not
                        // rewrite the user's system clipboard while sanitizing.
                        let value: String = value.chars().filter(|c| *c >= ' ').collect();
                        if value.is_empty() {
                            continue;
                        }
                        text.queue_edit(TextEdit::Insert(value.into()));
                    }
                    Some(Err(_)) => continue,
                    None => {
                        history.pending.push_front(Edit::Paste(read));
                        break;
                    }
                },
                Edit::Native(edit) => text.queue_edit(edit),
                Edit::History(redo) => history.restore(&mut text, redo, &mut fonts, &mut layouts),
            }
            text.apply_pending_edits(&mut fonts, &mut layouts.0, &mut clipboard, |c| c >= ' ');
            if immediate {
                history.remember(&text);
            }
            if text.pending_paste.is_some() {
                break;
            }
        }
        history.remember(&text);
    }
}
/// Detach before despawning the old dashboard root, then reparent this exact
/// editor to its replacement. Explicit category/clear changes reset like SetText.
pub(super) fn prepare(
    commands: &mut Commands,
    fields: &mut Query<(Entity, &mut EditableText, &mut History, &mut Field)>,
    value: &str,
    bounds: Rect,
    font: &Handle<Font>,
    scale: f32,
) -> Entity {
    let font = TextFont {
        font: font.clone().into(),
        font_size: FontSize::Px(23. * scale),
        ..default()
    };
    let node = Node {
        position_type: PositionType::Absolute,
        left: px(bounds.min.x),
        top: px(bounds.min.y),
        width: px(bounds.width()),
        height: px(bounds.height()),
        padding: UiRect {
            left: px(6.),
            right: px(6.),
            top: px(((bounds.height() - 23. * scale * 1.2) * 0.5).max(3.)),
            bottom: px(3.),
        },
        border: UiRect::all(px(1.)),
        border_radius: BorderRadius::all(px(4.)),
        overflow: Overflow::clip(),
        ..default()
    };
    if let Some((entity, mut text, mut history, mut field)) = fields.iter_mut().next() {
        if field.requested != value && (text.value().to_string() != value || text.is_composing()) {
            text.clear();
            text.editor.set_text(value);
            text.queue_edit(TextEdit::TextStart(false));
            *history = History::new(&text);
        }
        field.requested = value.into();
        text.cursor_width = 1. / (23. * scale);
        commands.entity(entity).remove::<ChildOf>().insert((
            node,
            font.clone(),
            Pickable::default(),
            Visibility::Inherited,
        ));
        commands.entity(field.placeholder).insert(font);
        return entity;
    }
    let mut text = EditableText::new(value);
    text.cursor_blink_period = std::time::Duration::MAX;
    text.cursor_width = 1. / (23. * scale);
    let history = History::new(&text);
    let entity = commands
        .spawn((
            node,
            TextInput,
            text,
            history,
            font.clone(),
            TextColor(TEXT),
            TextLayout::no_wrap(),
            TabIndex(0),
            BackgroundColor(Color::srgba(0.09, 0.09, 0.10, 0.98)),
            BorderColor::all(Color::NONE),
            TextCursorStyle {
                color: Color::srgb(0.62, 0.78, 0.94),
                selection_color: Color::srgba(0.5, 0.5, 0.5, 1.),
                selected_text_color: Some(Color::WHITE),
                unfocused_selection_color: Color::NONE,
                ..default()
            },
        ))
        .id();
    let placeholder = commands
        .spawn((
            Text::new("search recipes..."),
            font,
            TextColor(DIM),
            TextLayout::no_wrap(),
            Pickable::IGNORE,
            Node {
                position_type: PositionType::Absolute,
                left: px(0.),
                top: px(0.),
                ..default()
            },
            if value.is_empty() {
                Visibility::Inherited
            } else {
                Visibility::Hidden
            },
            ChildOf(entity),
        ))
        .id();
    commands.entity(entity).insert(Field {
        placeholder,
        requested: value.into(),
    });
    entity
}
#[cfg(test)]
mod tests;
