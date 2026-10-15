//! Original LineEdit edit popup. Godot4.6 default PopupMenu theme is Open Sans
//! SemiBold16, panel(.25), hover(.4), 4px margins,3px corners. Direction/control
//! rendering submenus remain a separate Parley shaping task.
//! Original layout: godotengine/godot 4.6.2-stable scene/gui/popup_menu.cpp.
use super::*;
use bevy::input_focus::AcquireFocus;
use bevy::ui_widgets::Activate;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) enum Action {
    Cut,
    Copy,
    Paste,
    SelectAll,
    Clear,
    Undo,
    Redo,
}
const ACTIONS: [Action; 7] = [
    Action::Cut,
    Action::Copy,
    Action::Paste,
    Action::SelectAll,
    Action::Clear,
    Action::Undo,
    Action::Redo,
];
impl Action {
    fn label(self) -> &'static str {
        match self {
            Self::Cut => "Cut",
            Self::Copy => "Copy",
            Self::Paste => "Paste",
            Self::SelectAll => "Select All",
            Self::Clear => "Clear",
            Self::Undo => "Undo",
            Self::Redo => "Redo",
        }
    }
    fn shortcut(self) -> &'static str {
        match self {
            Self::Cut => "Ctrl+X",
            Self::Copy => "Ctrl+C",
            Self::Paste => "Ctrl+V",
            Self::SelectAll => "Ctrl+A",
            Self::Undo => "Ctrl+Z",
            Self::Redo => "Ctrl+Shift+Z",
            Self::Clear => "",
        }
    }
}
#[derive(Resource, Default)]
pub(super) struct Popup {
    pub request: Option<(Entity, Vec2)>,
    pub drag: Option<(Vec2, String)>,
    field: Option<Entity>,
    root: Option<Entity>,
    selected: Option<usize>,
    enabled: [bool; 7],
    action: Option<Action>,
    close: bool,
    consumed: bool,
    prefix: String,
    prefix_at: f64,
}
impl Popup {
    pub fn owns_field(&self, entity: Entity) -> bool {
        self.field == Some(entity) || self.request.is_some_and(|(field, _)| field == entity)
    }
    pub fn is_open(&self) -> bool {
        self.field.is_some() || self.request.is_some()
    }
    pub fn typing(&self) -> bool {
        self.is_open() || self.consumed
    }
}
#[derive(Component)]
struct Root;
#[derive(Component)]
pub(super) struct Row(usize);
#[derive(Component)]
pub(super) struct Preview;
pub(super) fn install(app: &mut App) {
    app.init_resource::<Popup>()
        .add_observer(open_key)
        .add_observer(menu_key)
        .add_observer(activate)
        .add_observer(hover)
        .add_observer(outside)
        .add_observer(focus)
        .add_systems(PreUpdate, reset.before(InputFocusSystems::Dispatch))
        .add_systems(
            PostUpdate,
            render
                .after(edit_history)
                .before(super::super::super::backdrop::BackdropRouting)
                .before(bevy::ui::UiSystems::Layout),
        );
}
fn reset(mut popup: ResMut<Popup>) {
    popup.consumed = false;
}
fn open_key(
    mut event: On<FocusedInput<KeyboardInput>>,
    fields: Query<(&EditableText, &ComputedNode, &UiGlobalTransform), With<Field>>,
    mut popup: ResMut<Popup>,
) {
    if event.event_target() != event.original_event_target() {
        return;
    }
    if !event.input.state.is_pressed() || event.input.logical_key != Key::ContextMenu {
        return;
    }
    let Ok((text, node, transform)) = fields.get(event.original_event_target()) else {
        return;
    };
    if text.is_composing() {
        return;
    }
    let caret = text
        .editor
        .cursor_geometry(1.)
        .map_or(Vec2::ZERO, |b| Vec2::new(b.x0 as f32, b.y1 as f32));
    let at = transform
        .affine()
        .transform_point2(caret + node.content_box().min - text.viewport.offset);
    popup.request = Some((event.original_event_target(), at));
    event.propagate(false);
}
fn menu_key(
    mut event: On<FocusedInput<KeyboardInput>>,
    roots: Query<(), With<Root>>,
    keys: Res<ButtonInput<Key>>,
    time: Res<Time<Real>>,
    mut popup: ResMut<Popup>,
) {
    if event.event_target() != event.original_event_target() {
        return;
    }
    if !roots.contains(event.original_event_target()) {
        return;
    }
    event.propagate(false);
    if !event.input.state.is_pressed() {
        return;
    }
    let command = keys.pressed(if cfg!(target_os = "macos") {
        Key::Super
    } else {
        Key::Control
    });
    match &event.input.logical_key {
        Key::Escape => {
            popup.close = true;
            popup.consumed = true;
        }
        Key::ArrowDown | Key::ArrowUp => {
            let down = event.input.logical_key == Key::ArrowDown;
            let mut index = popup.selected.unwrap_or(if down { 6 } else { 0 });
            for _ in 0..7 {
                index = if down {
                    (index + 1) % 7
                } else {
                    (index + 6) % 7
                };
                if popup.enabled[index] {
                    break;
                }
            }
            popup.selected = Some(index);
        }
        Key::Home => popup.selected = (0..7).find(|i| popup.enabled[*i]),
        Key::End => popup.selected = (0..7).rev().find(|i| popup.enabled[*i]),
        Key::Enter | Key::Space => {
            if let Some(index) = popup.selected.filter(|i| popup.enabled[*i]) {
                popup.action = Some(ACTIONS[index]);
            }
        }
        Key::Character(letter) => {
            if command {
                popup.action = match letter.to_ascii_lowercase().as_str() {
                    "x" => Some(Action::Cut),
                    "c" => Some(Action::Copy),
                    "v" => Some(Action::Paste),
                    "a" => Some(Action::SelectAll),
                    "z" => Some(if keys.pressed(Key::Shift) {
                        Action::Redo
                    } else {
                        Action::Undo
                    }),
                    _ => None,
                };
            } else {
                if time.elapsed_secs_f64() - popup.prefix_at > 1. {
                    popup.prefix.clear();
                }
                popup.prefix.push_str(&letter.to_lowercase());
                popup.prefix_at = time.elapsed_secs_f64();
                popup.selected = (0..7).find(|i| {
                    popup.enabled[*i]
                        && ACTIONS[*i]
                            .label()
                            .to_lowercase()
                            .starts_with(&popup.prefix)
                });
            }
        }
        _ => {}
    }
}
fn activate(event: On<Activate>, rows: Query<&Row>, mut popup: ResMut<Popup>) {
    if let Ok(row) = rows.get(event.entity)
        && popup.enabled[row.0]
    {
        popup.action = Some(ACTIONS[row.0]);
    }
}
fn hover(
    event: On<bevy::picking::events::PointerOver>,
    rows: Query<&Row>,
    mut popup: ResMut<Popup>,
) {
    if let Ok(row) = rows.get(event.entity) {
        popup.selected = popup.enabled[row.0].then_some(row.0);
    }
}
fn outside(event: On<PointerPress>, roots: Query<(), With<Root>>, mut popup: ResMut<Popup>) {
    if event.entity == event.original_event_target() && roots.contains(event.entity) {
        popup.close = true;
        popup.consumed = true;
    }
}
fn focus(mut event: On<AcquireFocus>, roots: Query<(), With<Root>>, mut focus: ResMut<InputFocus>) {
    if roots.contains(event.event_target()) {
        focus.set(event.event_target(), FocusCause::Pressed);
        event.propagate(false);
    }
}
fn action(action: Action, text: &mut EditableText, history: &mut History) {
    match action {
        Action::Undo | Action::Redo => history.enqueue(text, Edit::History(action == Action::Redo)),
        Action::Clear => {
            text.queue_edit(TextEdit::SelectAll);
            text.queue_edit(TextEdit::Delete);
        }
        _ => text.queue_edit(match action {
            Action::Cut => TextEdit::Cut,
            Action::Copy => TextEdit::Copy,
            Action::Paste => TextEdit::Paste,
            Action::SelectAll => TextEdit::SelectAll,
            _ => unreachable!(),
        }),
    }
}
pub(super) fn render(
    mut commands: Commands,
    mut popup: ResMut<Popup>,
    ui: Res<UiState>,
    mut fields: Query<(&mut EditableText, &mut History), With<Field>>,
    windows: Query<&Window, With<PrimaryWindow>>,
    mut focus: ResMut<InputFocus>,
    font: Res<crate::character::nameplate::OriginalFont>,
    mut rows: Query<(&Row, &mut BackgroundColor)>,
    previews: Query<Entity, With<Preview>>,
) {
    if ui.screen != Screen::Crafting || popup.field.is_some_and(|e| fields.get(e).is_err()) {
        popup.close = true;
        popup.request = None;
    }
    if let Some(action_id) = popup.action.take()
        && let Some(field) = popup.field
        && let Ok((mut text, mut history)) = fields.get_mut(field)
    {
        if popup.enabled[ACTIONS.iter().position(|a| *a == action_id).unwrap()] {
            action(action_id, &mut text, &mut history);
        }
        popup.close = true;
        popup.consumed = true;
    }
    if popup.close || popup.request.is_some() {
        if let Some(root) = popup.root.take() {
            commands.entity(root).try_despawn();
        }
        if let Some(field) = popup.field.take()
            && fields.contains(field)
        {
            commands.entity(field).insert(TextInput);
            if ui.screen == Screen::Crafting {
                focus.set(field, FocusCause::Navigated);
            } else {
                focus.clear();
            }
        }
        popup.close = false;
    }
    if let Some((field, at)) = popup.request.take()
        && let Ok((text, mut history)) = fields.get_mut(field)
    {
        // The popup takes releases away from the editor, including custom/touch.
        history.enqueue(&text, Edit::Pointer(pointer::Operation::Cancel));
        popup.drag = None;
        popup.enabled = [
            true,
            true,
            true,
            true,
            true,
            history.at > 0,
            history.at + 1 < history.states.len(),
        ];
        popup.selected = None;
        popup.field = Some(field);
        popup.prefix.clear();
        let size = windows
            .single()
            .map_or(Vec2::new(1280., 720.), |w| Vec2::new(w.width(), w.height()));
        // Godot 4.6.2 original seven edit rows, Open Sans16: 209×205.
        // Text height23 + vertical separation4; empty separators consume4.
        let panel_size = Vec2::new(209., 205.);
        let at = at.clamp(Vec2::ZERO, (size - panel_size).max(Vec2::ZERO));
        let root = commands
            .spawn((
                Root,
                super::super::super::backdrop::AboveDashboard,
                Node {
                    width: percent(100.),
                    height: percent(100.),
                    ..default()
                },
                GlobalZIndex(10000),
            ))
            .id();
        let panel = commands
            .spawn((
                Node {
                    position_type: PositionType::Absolute,
                    left: px(at.x),
                    top: px(at.y),
                    width: px(panel_size.x),
                    height: px(panel_size.y),
                    border_radius: BorderRadius::all(px(3.)),
                    ..default()
                },
                BackgroundColor(Color::srgb(0.25, 0.25, 0.25)),
                ChildOf(root),
            ))
            .id();
        let mut y = 4.;
        for (index, a) in ACTIONS.iter().enumerate() {
            if index == 3 || index == 5 {
                commands.spawn((
                    Node {
                        position_type: PositionType::Absolute,
                        left: px(4.),
                        top: px(y + 2.),
                        width: px(201.),
                        height: px(1.),
                        ..default()
                    },
                    BackgroundColor(Color::srgba(0.875, 0.875, 0.875, 0.2)),
                    Pickable::IGNORE,
                    ChildOf(panel),
                ));
                y += 4.;
            }
            let row = commands
                .spawn((
                    Row(index),
                    Button,
                    Node {
                        position_type: PositionType::Absolute,
                        left: px(4.),
                        top: px(y),
                        width: px(201.),
                        height: px(27.),
                        border_radius: BorderRadius::all(px(3.)),
                        ..default()
                    },
                    BackgroundColor(Color::NONE),
                    ChildOf(panel),
                ))
                .id();
            for (label, right) in [(a.label(), false), (a.shortcut(), true)] {
                commands.spawn((
                    Text::new(label),
                    TextFont {
                        font: font.0.clone().into(),
                        font_size: FontSize::Px(16.),
                        ..default()
                    },
                    TextLayout::no_wrap(),
                    TextColor(if !popup.enabled[index] {
                        Color::srgba(0.4, 0.4, 0.4, 0.8)
                    } else if right {
                        Color::srgba(0.7, 0.7, 0.7, 0.8)
                    } else {
                        Color::srgb(0.875, 0.875, 0.875)
                    }),
                    Node {
                        position_type: PositionType::Absolute,
                        left: if right { Val::Auto } else { px(4.) },
                        right: if right { px(4.) } else { Val::Auto },
                        top: px(4.),
                        ..default()
                    },
                    Pickable::IGNORE,
                    ChildOf(row),
                ));
            }
            y += 27.;
        }
        // PopupWindow focus in Godot does not collapse its parent LineEdit.
        // Keep EditableText intact while the popup temporarily owns key focus.
        commands.entity(field).remove::<TextInput>();
        focus.set(root, FocusCause::Navigated);
        popup.root = Some(root);
    }
    for (row, mut color) in &mut rows {
        *color = BackgroundColor(if popup.selected == Some(row.0) {
            Color::srgb(0.4, 0.4, 0.4)
        } else {
            Color::NONE
        });
    }
    for entity in &previews {
        commands.entity(entity).despawn();
    }
    if let Some((at, text)) = &popup.drag {
        commands.spawn((
            Preview,
            super::super::super::backdrop::AboveDashboard,
            GlobalZIndex(10001),
            Node {
                position_type: PositionType::Absolute,
                left: px(at.x + 8.),
                top: px(at.y + 8.),
                ..default()
            },
            Text::new(text),
            TextFont {
                font: font.0.clone().into(),
                font_size: FontSize::Px(16.),
                ..default()
            },
            TextLayout::no_wrap(),
            TextColor(Color::WHITE),
            Pickable::IGNORE,
        ));
    }
}
