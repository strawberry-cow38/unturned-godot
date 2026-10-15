//! Actual input reduction and focused dispatch, including keys released before
//! the slow frame consumes their messages. Directly triggering FocusedInput
//! cannot reproduce this production ordering bug.
use super::*;
use bevy::{
    input::{ButtonState, InputPlugin},
    input_focus::InputDispatchPlugin,
    window::{Ime, PrimaryWindow},
};

mod visual;

fn dispatched_editor() -> (App, Entity, Entity) {
    let (mut app, entity, window) = editor();
    app.add_plugins((InputPlugin, InputDispatchPlugin))
        .add_message::<Ime>();
    app.world_mut().entity_mut(window).insert(PrimaryWindow);
    (app, entity, window)
}

fn message(
    app: &mut App,
    window: Entity,
    code: KeyCode,
    key: Key,
    pressed: bool,
    text: Option<&str>,
) {
    app.world_mut().write_message(KeyboardInput {
        key_code: code,
        logical_key: key,
        state: if pressed {
            ButtonState::Pressed
        } else {
            ButtonState::Released
        },
        text: text.map(Into::into),
        repeat: false,
        window,
    });
}

fn chord(
    app: &mut App,
    window: Entity,
    modifier: (KeyCode, Key),
    code: KeyCode,
    key: Key,
    text: Option<&str>,
) {
    message(app, window, modifier.0, modifier.1.clone(), true, None);
    message(app, window, code, key.clone(), true, text);
    message(app, window, code, key, false, None);
    message(app, window, modifier.0, modifier.1, false, None);
}

fn frame(app: &mut App) {
    app.world_mut().run_schedule(PreUpdate);
    edits(app);
    app.world_mut()
        .resource_mut::<Messages<KeyboardInput>>()
        .clear();
}

#[test]
fn batched_control_a_and_shift_caret_keep_event_modifiers_and_final_polled_state() {
    let (mut app, field, window) = dispatched_editor();
    key(
        &mut app,
        field,
        window,
        Key::Character("Birch".into()),
        Some("Birch"),
    );
    edits(&mut app);
    chord(
        &mut app,
        window,
        (KeyCode::ControlLeft, Key::Control),
        KeyCode::KeyA,
        Key::Character("a".into()),
        Some("a"),
    );
    frame(&mut app);
    assert_eq!(
        value(&app, field),
        "Birch",
        "Ctrl+A must not insert its printable A when Ctrl was released in this batch"
    );
    assert!(
        !app.world()
            .get::<EditableText>(field)
            .unwrap()
            .editor
            .raw_selection()
            .is_collapsed()
    );
    let logical = app.world().resource::<ButtonInput<Key>>();
    assert!(!logical.pressed(Key::Control));
    assert!(logical.just_pressed(Key::Control) && logical.just_released(Key::Control));
    let physical = app.world().resource::<ButtonInput<KeyCode>>();
    assert!(!physical.pressed(KeyCode::ControlLeft));
    assert!(
        physical.just_pressed(KeyCode::ControlLeft) && physical.just_released(KeyCode::ControlLeft)
    );
    message(
        &mut app,
        window,
        KeyCode::KeyM,
        Key::Character("Maple".into()),
        true,
        Some("Maple"),
    );
    frame(&mut app);
    assert_eq!(value(&app, field), "Maple");
    chord(
        &mut app,
        window,
        (KeyCode::ShiftLeft, Key::Shift),
        KeyCode::ArrowLeft,
        Key::ArrowLeft,
        None,
    );
    frame(&mut app);
    message(
        &mut app,
        window,
        KeyCode::KeyX,
        Key::Character("X".into()),
        true,
        Some("X"),
    );
    frame(&mut app);
    assert_eq!(
        value(&app, field),
        "MaplX",
        "Shift+Left must select the last character despite the batched Shift release"
    );
    chord(
        &mut app,
        window,
        (KeyCode::ControlLeft, Key::Control),
        KeyCode::KeyZ,
        Key::Character("z".into()),
        Some("z"),
    );
    frame(&mut app);
    assert_eq!(
        value(&app, field),
        "Maple",
        "source History must observe the same per-event modifiers as TextInput"
    );
}

#[test]
fn releasing_control_before_text_does_not_retroactively_apply_later_control_press() {
    let (mut app, field, window) = dispatched_editor();
    message(
        &mut app,
        window,
        KeyCode::ControlLeft,
        Key::Control,
        true,
        None,
    );
    frame(&mut app);
    message(
        &mut app,
        window,
        KeyCode::ControlLeft,
        Key::Control,
        false,
        None,
    );
    message(
        &mut app,
        window,
        KeyCode::KeyA,
        Key::Character("a".into()),
        true,
        Some("a"),
    );
    message(
        &mut app,
        window,
        KeyCode::ControlLeft,
        Key::Control,
        true,
        None,
    );
    frame(&mut app);
    assert_eq!(
        value(&app, field),
        "a",
        "later Ctrl press must not turn earlier ordinary text into Select All"
    );
    assert!(
        app.world()
            .resource::<ButtonInput<Key>>()
            .pressed(Key::Control)
    );
}

#[derive(Resource, Default)]
struct Events(Vec<(Entity, bool, bool, bool)>, Vec<bool>);

#[test]
fn repeats_focus_reset_and_deferred_observers_preserve_original_batch_target_and_final_keys() {
    use bevy::input::keyboard::KeyboardFocusLost;
    let mut app = App::new();
    app.add_plugins((InputPlugin, InputDispatchPlugin))
        .init_resource::<InputFocus>()
        .init_resource::<Events>()
        .add_observer(
            |event: On<FocusedInput<KeyboardInput>>,
             logical: Res<ButtonInput<Key>>,
             physical: Res<ButtonInput<KeyCode>>,
             mut events: ResMut<Events>,
             mut focus: ResMut<InputFocus>,
             mut commands: Commands| {
                if event.input.key_code != KeyCode::ArrowLeft {
                    return;
                }
                events.0.push((
                    event.focused_entity,
                    logical.pressed(Key::Control),
                    physical.pressed(KeyCode::ControlLeft),
                    event.input.repeat,
                ));
                // This changes only future batches in the original dispatcher.
                focus.clear();
                commands.queue(|world: &mut World| {
                    let control = world.resource::<ButtonInput<Key>>().pressed(Key::Control);
                    world.resource_mut::<Events>().1.push(control);
                });
            },
        );
    let window = app
        .world_mut()
        .spawn((Window::default(), PrimaryWindow))
        .id();
    let field = app.world_mut().spawn_empty().id();
    app.insert_resource(InputFocus::from_entity(field));
    message(
        &mut app,
        window,
        KeyCode::ControlLeft,
        Key::Control,
        true,
        None,
    );
    message(
        &mut app,
        window,
        KeyCode::ArrowLeft,
        Key::ArrowLeft,
        true,
        None,
    );
    app.world_mut().write_message(KeyboardInput {
        key_code: KeyCode::ArrowLeft,
        logical_key: Key::ArrowLeft,
        state: ButtonState::Pressed,
        text: None,
        repeat: true,
        window,
    });
    message(
        &mut app,
        window,
        KeyCode::ControlLeft,
        Key::Control,
        false,
        None,
    );
    app.world_mut().write_message(KeyboardFocusLost);
    app.world_mut().run_schedule(PreUpdate);
    let events = app.world().resource::<Events>();
    assert_eq!(
        events.0,
        [(field, true, true, false), (field, true, true, true)]
    );
    assert_eq!(
        events.1,
        [true, true],
        "deferred observers retain event-time keys too"
    );
    assert!(
        !app.world()
            .resource::<ButtonInput<KeyCode>>()
            .pressed(KeyCode::ArrowLeft),
        "focus-loss physical reset must survive dispatch restoration"
    );
    assert!(
        app.world()
            .resource::<ButtonInput<KeyCode>>()
            .just_released(KeyCode::ArrowLeft)
    );
    assert!(
        !app.world()
            .resource::<ButtonInput<Key>>()
            .pressed(Key::Control)
    );

    // External input reset must not leave a private held modifier in dispatch.
    app.world_mut()
        .resource_mut::<ButtonInput<Key>>()
        .reset_all();
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .reset_all();
    app.world_mut().entity_mut(field).despawn();
    app.insert_resource(InputFocus::from_entity(field));
    message(
        &mut app,
        window,
        KeyCode::ArrowLeft,
        Key::ArrowLeft,
        true,
        None,
    );
    app.world_mut().run_schedule(PreUpdate);
    assert_eq!(
        app.world().resource::<Events>().0.last(),
        Some(&(window, false, false, false))
    );
    assert_eq!(app.world().resource::<InputFocus>().get(), None);
}
