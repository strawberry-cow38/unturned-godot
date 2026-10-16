//! Actual Bevy text editing/dispatch into the production number commit observer.
use super::super::{context, controller::Menu, model::Cell};
use bevy::ecs::system::RunSystemOnce;
use bevy::{
    input::{
        ButtonState, InputPlugin,
        keyboard::{Key, KeyboardInput},
    },
    input_focus::{InputDispatchPlugin, InputFocus},
    prelude::*,
    text::{EditableText, FontCx, LayoutCx},
    ui_widgets::{TextInput, TextInputPlugin},
    window::{Ime, PrimaryWindow},
};
fn setup() -> (App, Entity, Entity) {
    let mut app = App::new();
    app.add_plugins((InputPlugin, InputDispatchPlugin, TextInputPlugin))
        .init_resource::<Menu>()
        .init_resource::<FontCx>()
        .init_resource::<LayoutCx>()
        .init_resource::<bevy::clipboard::Clipboard>()
        .init_resource::<Assets<Font>>()
        .init_resource::<bevy::text::RemSize>()
        .init_resource::<crate::character::nameplate::OriginalFont>()
        .insert_resource(crate::ui::UiState {
            screen: crate::ui::Screen::Inventory,
            ..default()
        })
        .add_message::<Ime>()
        .add_observer(context::number_key);
    let window = app
        .world_mut()
        .spawn((Window::default(), PrimaryWindow))
        .id();
    let field = app
        .world_mut()
        .spawn((
            TextInput,
            context::number_text(75),
            context::Number {
                cell: Cell::at(2, 0, 0),
                maximum: 149,
                requested: 75,
                editing: true,
            },
        ))
        .id();
    let font = app
        .world()
        .resource::<crate::character::nameplate::OriginalFont>()
        .0
        .clone();
    app.world_mut().entity_mut(field).insert((
        TextFont {
            font: font.into(),
            font_size: FontSize::Px(16.),
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
    app.insert_resource(InputFocus::from_entity(field));
    app.world_mut().resource_mut::<Menu>().gesture.split_amount = 75;
    (app, field, window)
}
fn edge(app: &mut App, window: Entity, code: KeyCode, key: Key, down: bool, text: Option<&str>) {
    app.world_mut().write_message(KeyboardInput {
        key_code: code,
        logical_key: key,
        state: if down {
            ButtonState::Pressed
        } else {
            ButtonState::Released
        },
        text: text.map(Into::into),
        repeat: false,
        window,
    });
}
#[test]
fn batched_select_all_type_and_enter_applies_queued_edits_before_numeric_commit() {
    let (mut app, field, window) = setup();
    for (code, key, down, text) in [
        (KeyCode::ControlLeft, Key::Control, true, None),
        (KeyCode::KeyA, Key::Character("a".into()), true, Some("a")),
        (KeyCode::KeyA, Key::Character("a".into()), false, None),
        (KeyCode::ControlLeft, Key::Control, false, None),
        (
            KeyCode::Digit1,
            Key::Character("100/2+1".into()),
            true,
            Some("100/2+1"),
        ),
        (
            KeyCode::Digit1,
            Key::Character("100/2+1".into()),
            false,
            None,
        ),
        (KeyCode::Enter, Key::Enter, true, Some("\r")),
        (KeyCode::Enter, Key::Enter, false, None),
    ] {
        edge(&mut app, window, code, key, down, text);
    }
    app.world_mut().run_schedule(PreUpdate);
    assert_eq!(app.world().resource::<Menu>().gesture.split_amount, 51);
    assert_eq!(
        app.world()
            .get::<EditableText>(field)
            .unwrap()
            .value()
            .to_string(),
        "51"
    );
    // EditableText::clear queues cursor-to-start. Applying that deferred work
    // must preserve the submitted value rather than replay the earlier draft.
    app.world_mut()
        .run_system_once(
            |mut fields: Query<&mut EditableText>,
             mut fonts: ResMut<FontCx>,
             mut layouts: ResMut<LayoutCx>,
             mut clipboard: ResMut<bevy::clipboard::Clipboard>| {
                for mut text in &mut fields {
                    text.apply_pending_edits(&mut fonts, &mut layouts.0, &mut clipboard, |_| true);
                }
            },
        )
        .unwrap();
    assert_eq!(
        app.world()
            .get::<EditableText>(field)
            .unwrap()
            .value()
            .to_string(),
        "51"
    );
}
#[test]
fn escape_discards_same_batch_draft_without_committing_it() {
    let (mut app, field, window) = setup();
    edge(
        &mut app,
        window,
        KeyCode::Digit1,
        Key::Character("4".into()),
        true,
        Some("4"),
    );
    edge(&mut app, window, KeyCode::Escape, Key::Escape, true, None);
    app.world_mut().run_schedule(PreUpdate);
    assert_eq!(app.world().resource::<Menu>().gesture.split_amount, 75);
    assert_eq!(
        app.world()
            .get::<EditableText>(field)
            .unwrap()
            .value()
            .to_string(),
        "75"
    );
}
