use super::*;
use bevy::ecs::system::RunSystemOnce;
use bevy::ui_widgets::TextInputPlugin;

mod affordances;
mod dispatch;

fn editor() -> (App, Entity, Entity) {
    let mut app = App::new();
    app.add_plugins(TextInputPlugin)
        .init_resource::<InputFocus>()
        .init_resource::<ButtonInput<Key>>()
        .init_resource::<FontCx>()
        .init_resource::<LayoutCx>()
        .init_resource::<Clipboard>()
        .init_resource::<primary::Primary>()
        .init_resource::<popup::Popup>()
        .init_resource::<Assets<Font>>()
        .init_resource::<bevy::text::RemSize>()
        .init_resource::<crate::character::nameplate::OriginalFont>()
        .add_observer(shortcuts);
    let window = app.world_mut().spawn(Window::default()).id();
    let text = EditableText::default();
    let entity = app
        .world_mut()
        .spawn((
            TextInput,
            History::new(&text),
            text,
            Field {
                placeholder: window,
                requested: String::new(),
            },
        ))
        .id();
    let font = app
        .world()
        .resource::<crate::character::nameplate::OriginalFont>()
        .0
        .clone();
    app.world_mut().entity_mut(entity).insert((
        TextFont {
            font: font.into(),
            font_size: FontSize::Px(12.),
            ..default()
        },
        bevy::ui::ComputedUiRenderTargetInfo::default(),
    ));
    app.world_mut()
        .run_system_once(bevy::text::load_font_assets_into_font_collection)
        .unwrap();
    app.world_mut()
        .run_system_once(bevy::ui::widget::update_editable_text_styles)
        .unwrap();
    app.insert_resource(InputFocus::from_entity(entity));
    (app, entity, window)
}
fn key(app: &mut App, entity: Entity, window: Entity, key: Key, text: Option<&str>) {
    app.world_mut().commands().trigger(FocusedInput::new(
        entity,
        KeyboardInput {
            key_code: KeyCode::Unidentified(bevy::input::keyboard::NativeKeyCode::Unidentified),
            logical_key: key,
            state: bevy::input::ButtonState::Pressed,
            text: text.map(Into::into),
            repeat: false,
            window,
        },
        window,
    ));
    app.world_mut().flush();
}
fn edits(app: &mut App) {
    app.world_mut().run_system_once(edit_history).unwrap();
}
fn value(app: &App, e: Entity) -> String {
    app.world()
        .get::<EditableText>(e)
        .unwrap()
        .value()
        .to_string()
}
fn undo(app: &mut App, e: Entity, w: Entity, redo: bool) {
    app.world_mut()
        .resource_mut::<ButtonInput<Key>>()
        .press(Key::Control);
    if redo {
        app.world_mut()
            .resource_mut::<ButtonInput<Key>>()
            .press(Key::Shift);
    }
    key(app, e, w, Key::Character("z".into()), None);
    app.world_mut()
        .resource_mut::<ButtonInput<Key>>()
        .reset_all();
    edits(app);
}
#[test]
fn actual_keyboard_caret_unicode_selection_and_source_deferred_undo_redo() {
    let (mut app, e, w) = editor();
    for letter in ["a", "b", "c"] {
        key(&mut app, e, w, Key::Character(letter.into()), Some(letter));
    }
    edits(&mut app);
    assert_eq!(value(&app, e), "abc");
    undo(&mut app, e, w, false);
    assert_eq!(
        value(&app, e),
        "",
        "same-frame source typing forms one deferred undo state"
    );
    undo(&mut app, e, w, true);
    assert_eq!(value(&app, e), "abc");
    key(&mut app, e, w, Key::ArrowLeft, None);
    key(&mut app, e, w, Key::Character("é".into()), Some("é"));
    edits(&mut app);
    assert_eq!(value(&app, e), "abéc");
    undo(&mut app, e, w, false);
    assert_eq!(value(&app, e), "abc");
    key(&mut app, e, w, Key::Character("x".into()), Some("x"));
    edits(&mut app);
    assert_eq!(
        value(&app, e),
        "abcx",
        "undo restores prior edit caret, not the later navigation position"
    );
    undo(&mut app, e, w, true);
    assert_eq!(value(&app, e), "abcx", "new edit retires redo");
    app.world_mut()
        .resource_mut::<ButtonInput<Key>>()
        .press(Key::Control);
    key(&mut app, e, w, Key::Character("a".into()), None);
    app.world_mut()
        .resource_mut::<ButtonInput<Key>>()
        .reset_all();
    key(&mut app, e, w, Key::Character("🦀".into()), Some("🦀"));
    edits(&mut app);
    assert_eq!(
        value(&app, e),
        "🦀",
        "engine select-all replaces the full Unicode buffer"
    );
    key(&mut app, e, w, Key::Backspace, None);
    edits(&mut app);
    assert_eq!(value(&app, e), "");
}
#[test]
fn repaint_preserves_exact_field_selection_ime_focus_and_reparents_before_old_root_despawn() {
    let mut app = render::tests::app();
    app.add_observer(changed);
    app.world_mut().resource_mut::<Menu>().browse.search = "maple".into();
    app.update();
    let e = app
        .world_mut()
        .query_filtered::<Entity, With<Field>>()
        .single(app.world())
        .unwrap();
    let old = app.world().get::<ChildOf>(e).unwrap().parent();
    let style = app.world().get::<TextCursorStyle>(e).unwrap();
    assert_eq!(
        style.unfocused_selection_color, style.selection_color,
        "source retained selection remains painted while the edit popup owns focus"
    );
    app.init_resource::<FontCx>()
        .init_resource::<LayoutCx>()
        .init_resource::<Clipboard>()
        .init_resource::<primary::Primary>()
        .init_resource::<popup::Popup>()
        .insert_resource(InputFocus::from_entity(e))
        .init_resource::<bevy::text::RemSize>();
    app.world_mut()
        .entity_mut(e)
        .insert(bevy::ui::ComputedUiRenderTargetInfo::default());
    app.world_mut()
        .run_system_once(bevy::text::load_font_assets_into_font_collection)
        .unwrap();
    app.world_mut()
        .run_system_once(bevy::ui::widget::update_editable_text_styles)
        .unwrap();
    {
        let mut t = app.world_mut().get_mut::<EditableText>(e).unwrap();
        t.queue_edit(TextEdit::TextStart(false));
        t.queue_edit(TextEdit::Right(true));
    }
    edits(&mut app);
    let selection = *app
        .world()
        .get::<EditableText>(e)
        .unwrap()
        .editor
        .raw_selection();
    app.world_mut().resource_mut::<Menu>().options["tick"] = json!(101);
    app.update();
    assert!(app.world().get_entity(old).is_err());
    assert_ne!(app.world().get::<ChildOf>(e).unwrap().parent(), old);
    assert_eq!(
        *app.world()
            .get::<EditableText>(e)
            .unwrap()
            .editor
            .raw_selection(),
        selection
    );
    assert_eq!(app.world().resource::<InputFocus>().get(), Some(e));
    // Active composition survives a quantity/accepted-count repaint too.
    app.world_mut()
        .get_mut::<EditableText>(e)
        .unwrap()
        .queue_edit(TextEdit::ImeSetCompose {
            value: "木".into(),
            cursor: None,
        });
    edits(&mut app);
    app.world_mut()
        .run_system_once(bevy::text::apply_text_edits)
        .unwrap();
    assert_eq!(app.world().resource::<Menu>().browse.search, "maple");
    assert_ne!(
        value(&app, e),
        "maple",
        "preedit hides the selected span from committed value"
    );
    app.world_mut().resource_mut::<Menu>().options["tick"] = json!(102);
    app.update();
    assert!(app.world().get::<EditableText>(e).unwrap().is_composing());
    assert_eq!(app.world().resource::<InputFocus>().get(), Some(e));
}
#[test]
fn first_escape_latches_dashboard_guard_then_second_escape_is_available_to_close() {
    let mut app = render::tests::app();
    app.update();
    let e = app
        .world_mut()
        .query_filtered::<Entity, With<Field>>()
        .single(app.world())
        .unwrap();
    app.world_mut().resource_mut::<Menu>().open = true;
    app.insert_resource(InputFocus::from_entity(e))
        .init_resource::<FocusFrame>()
        .init_resource::<popup::Popup>()
        .init_resource::<ButtonInput<KeyCode>>();
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .press(KeyCode::Escape);
    app.world_mut().run_system_once(remember_focus).unwrap();
    // Real Bevy TextInput clears focus in PreUpdate before keyboard_ui runs.
    app.world_mut().resource_mut::<InputFocus>().clear();
    app.world_mut().run_system_once(synchronize).unwrap();
    assert!(app.world().resource::<Menu>().typing());
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .reset_all();
    app.world_mut().run_system_once(remember_focus).unwrap();
    app.world_mut().run_system_once(synchronize).unwrap();
    assert!(!app.world().resource::<Menu>().typing());
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .press(KeyCode::Escape);
    app.world_mut().run_system_once(remember_focus).unwrap();
    app.world_mut().run_system_once(synchronize).unwrap();
    assert!(
        !app.world().resource::<Menu>().typing(),
        "second Escape reaches existing dashboard close"
    );
}
#[test]
#[ignore = "requires an X11/Wayland system clipboard; run under xvfb, no GPU required"]
fn actual_system_clipboard_cut_paste_and_control_character_filter() {
    let (mut app, e, w) = editor();
    app.world_mut()
        .resource_mut::<Clipboard>()
        .set_text("maple\nwood\t🦀")
        .unwrap();
    app.world_mut()
        .resource_mut::<ButtonInput<Key>>()
        .press(Key::Control);
    key(&mut app, e, w, Key::Character("v".into()), None);
    edits(&mut app);
    assert_eq!(value(&app, e), "maplewood🦀");
    assert_eq!(
        app.world_mut()
            .resource_mut::<Clipboard>()
            .fetch_text()
            .poll_result()
            .unwrap()
            .unwrap(),
        "maple\nwood\t🦀",
        "paste must not rewrite OS clipboard"
    );
    key(&mut app, e, w, Key::Character("a".into()), None);
    edits(&mut app);
    key(&mut app, e, w, Key::Character("x".into()), None);
    edits(&mut app);
    assert_eq!(value(&app, e), "");
    key(&mut app, e, w, Key::Character("v".into()), None);
    edits(&mut app);
    assert_eq!(value(&app, e), "maplewood🦀");
}
#[test]
fn undo_after_font_resize_retains_current_editor_style() {
    let (mut app, e, w) = editor();
    app.init_resource::<Assets<Font>>()
        .init_resource::<bevy::text::RemSize>();
    app.world_mut().get_mut::<TextFont>(e).unwrap().font_size = FontSize::Px(12.);
    app.world_mut()
        .run_system_once(bevy::ui::widget::update_editable_text_styles)
        .unwrap();
    key(&mut app, e, w, Key::Character("a".into()), Some("a"));
    edits(&mut app);
    key(&mut app, e, w, Key::Character("b".into()), Some("b"));
    edits(&mut app);
    assert_eq!(font_size(&mut app, e), 12.);
    app.world_mut().get_mut::<TextFont>(e).unwrap().font_size = FontSize::Px(24.);
    app.world_mut()
        .run_system_once(bevy::ui::widget::update_editable_text_styles)
        .unwrap();
    undo(&mut app, e, w, false);
    assert_eq!(value(&app, e), "a");
    assert_eq!(
        font_size(&mut app, e),
        24.,
        "undo restores text/caret, never an obsolete layout/font snapshot"
    );
}
fn font_size(app: &mut App, entity: Entity) -> f32 {
    app.world_mut()
        .run_system_once(
            move |mut fields: Query<&mut EditableText>,
                  mut fonts: ResMut<FontCx>,
                  mut layouts: ResMut<LayoutCx>| {
                let mut text = fields.get_mut(entity).unwrap();
                text.editor
                    .layout(&mut fonts, &mut layouts.0)
                    .lines()
                    .flat_map(|line| line.runs())
                    .next()
                    .unwrap()
                    .font_size()
            },
        )
        .unwrap()
}
#[test]
fn asynchronous_paste_preserves_sanitized_order_and_source_empty_paste_selection() {
    let (mut app, e, w) = editor();
    let pending = std::sync::Arc::new(std::sync::Mutex::new(None));
    {
        let mut history = app.world_mut().get_mut::<History>(e).unwrap();
        history
            .pending
            .push_back(Edit::Paste(ClipboardRead::Pending(pending.clone())));
        history
            .pending
            .push_back(Edit::Native(TextEdit::Insert("x".into())));
    }
    edits(&mut app);
    assert_eq!(
        value(&app, e),
        "",
        "later typed input cannot pass a pending paste"
    );
    *pending.lock().unwrap() = Some(Ok("a\nb\t\u{7f}\u{85}".into()));
    edits(&mut app);
    assert_eq!(
        value(&app, e),
        "ab\u{7f}\u{85}x",
        "strip only source ASCII escapes"
    );
    app.world_mut()
        .resource_mut::<ButtonInput<Key>>()
        .press(Key::Control);
    key(&mut app, e, w, Key::Character("a".into()), None);
    edits(&mut app);
    app.world_mut()
        .get_mut::<History>(e)
        .unwrap()
        .pending
        .push_back(Edit::Paste(ClipboardRead::Ready(Ok("\n\t".into()))));
    edits(&mut app);
    assert_eq!(
        value(&app, e),
        "ab\u{7f}\u{85}x",
        "empty sanitized paste does not delete selection"
    );
    assert!(
        app.world()
            .get::<EditableText>(e)
            .unwrap()
            .editor
            .selected_text()
            .is_some()
    );
}
