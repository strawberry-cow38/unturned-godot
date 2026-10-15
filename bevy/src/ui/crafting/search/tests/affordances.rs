use super::*;
use bevy::{
    camera::NormalizedRenderTarget,
    math::Affine2,
    picking::{
        InteractionPlugin, PickingPlugin,
        pointer::{Location, PointerAction},
    },
    ui::{ComputedUiRenderTargetInfo, UiGlobalTransform},
};

fn setup(value: &str) -> (App, Entity, Entity, Entity) {
    let (mut app, e, w) = editor();
    app.add_plugins((
        MinimalPlugins,
        PickingPlugin,
        InteractionPlugin,
        bevy::ui_widgets::ButtonPlugin,
    ))
    .init_resource::<UiScale>()
    .insert_resource(UiState {
        screen: Screen::Crafting,
        ..default()
    });
    app.world_mut().entity_mut(w).insert(PrimaryWindow);
    app.world_mut().entity_mut(e).insert((
        Node::default(),
        ComputedNode {
            size: Vec2::new(400., 40.),
            ..default()
        },
        UiGlobalTransform::from(Affine2::from_translation(Vec2::new(300., 40.))),
        ComputedUiRenderTargetInfo::default(),
    ));
    let surface = app
        .world_mut()
        .spawn((pointer::Surface(e), ChildOf(e)))
        .id();
    pointer::install(&mut app);
    popup::install(&mut app);
    app.world_mut()
        .get_mut::<EditableText>(e)
        .unwrap()
        .viewport
        .size = Vec2::new(400., 40.);
    app.world_mut()
        .get_mut::<EditableText>(e)
        .unwrap()
        .queue_edit(TextEdit::Insert(value.into()));
    edits(&mut app);
    (app, e, w, surface)
}
fn select(app: &mut App, e: Entity, a: usize, b: usize) {
    app.world_mut()
        .run_system_once(
            move |mut fields: Query<&mut EditableText>,
                  mut fonts: ResMut<FontCx>,
                  mut layout: ResMut<LayoutCx>| {
                fields
                    .get_mut(e)
                    .unwrap()
                    .editor
                    .driver(&mut fonts, &mut layout.0)
                    .select_byte_range(a, b);
            },
        )
        .unwrap();
}
fn selected(app: &App, e: Entity) -> String {
    app.world()
        .get::<EditableText>(e)
        .unwrap()
        .editor
        .selected_text()
        .unwrap_or("")
        .into()
}
fn position(app: &mut App, e: Entity, index: usize) -> Vec2 {
    app.world_mut()
        .run_system_once(
            move |fields: Query<&EditableText>,
                  mut fonts: ResMut<FontCx>,
                  mut layout: ResMut<LayoutCx>| {
                let mut editor = fields.get(e).unwrap().editor.clone();
                editor.driver(&mut fonts, &mut layout.0).move_to_byte(index);
                let cursor = editor.cursor_geometry(1.).unwrap();
                Vec2::new(100. + cursor.x0 as f32, 20. + cursor.y0 as f32 + 5.)
            },
        )
        .unwrap()
}
fn mouse(app: &mut App, surface: Entity, at: Vec2, actions: &[PointerAction]) {
    crate::ui::button_input_tests::mouse_frame(
        app,
        &[surface],
        &[surface],
        Location {
            target: NormalizedRenderTarget::None {
                width: 1280,
                height: 720,
            },
            position: at,
        },
        actions,
    );
    edits(app);
}
fn press(app: &mut App, s: Entity, at: Vec2, button: PointerButton) {
    app.world_mut()
        .resource_mut::<Time<Real>>()
        .advance_by(std::time::Duration::from_millis(700));
    mouse(app, s, at, &[PointerAction::Press(button)]);
}
fn release(app: &mut App, s: Entity, at: Vec2, button: PointerButton) {
    mouse(app, s, at, &[PointerAction::Release(button)]);
}

#[test]
fn actual_picker_right_click_retains_selection_menu_keyboard_undo_and_field_identity() {
    let (mut app, e, w, s) = setup("Birch Maple");
    select(&mut app, e, 0, 5);
    let at = position(&mut app, e, 9);
    press(&mut app, s, at, PointerButton::Secondary);
    release(&mut app, s, at, PointerButton::Secondary);
    app.world_mut().run_system_once(popup::render).unwrap();
    let menu = app.world().resource::<InputFocus>().get().unwrap();
    assert_ne!(menu, e);
    assert_eq!(selected(&app, e), "Birch");
    assert!(!app.world().entity(e).contains::<TextInput>());
    assert!(app.world().resource::<popup::Popup>().typing());
    let (height, panel) = {
        let world = app.world_mut();
        let mut rows = world.query::<(&popup::Row, &Node, &ChildOf)>();
        let (_, node, parent) = rows.iter(world).next().unwrap();
        (node.height, parent.parent())
    };
    assert_eq!(
        height,
        px(27.),
        "original23px font height plus4px row separation"
    );
    let panel = app.world().get::<Node>(panel).unwrap();
    assert_eq!(
        (panel.width, panel.height),
        (px(209.), px(205.)),
        "actual original PopupMenu seven-row metrics"
    );
    key(&mut app, menu, w, Key::Character("cl".into()), Some("cl"));
    key(&mut app, menu, w, Key::Enter, None);
    app.world_mut().run_system_once(popup::render).unwrap();
    edits(&mut app);
    assert_eq!(value(&app, e), "");
    assert!(app.world().entity(e).contains::<TextInput>());
    assert_eq!(app.world().resource::<InputFocus>().get(), Some(e));
    undo(&mut app, e, w, false);
    assert_eq!(value(&app, e), "Birch Maple");
    // Menu key works without a pointer and first Escape closes only its popup.
    key(&mut app, e, w, Key::ContextMenu, None);
    app.world_mut().run_system_once(popup::render).unwrap();
    let menu = app.world().resource::<InputFocus>().get().unwrap();
    key(&mut app, menu, w, Key::Escape, None);
    app.world_mut().run_system_once(popup::render).unwrap();
    assert_eq!(app.world().resource::<InputFocus>().get(), Some(e));
    assert_eq!(app.world().resource::<UiState>().screen, Screen::Crafting);
}

#[test]
fn actual_picker_selected_text_move_copy_cancel_and_plain_click_use_source_boundaries() {
    let (mut app, e, w, s) = setup("Birch Maple");
    select(&mut app, e, 0, 5);
    let start = position(&mut app, e, 2);
    let end = position(&mut app, e, 11);
    press(&mut app, s, start, PointerButton::Primary);
    mouse(
        &mut app,
        s,
        end,
        &[PointerAction::Move { delta: end - start }],
    );
    release(&mut app, s, end, PointerButton::Primary);
    assert_eq!(value(&app, e), " MapleBirch");
    assert_eq!(selected(&app, e), "Birch");
    undo(&mut app, e, w, false);
    assert_eq!(value(&app, e), "Birch Maple");
    select(&mut app, e, 0, 5);
    app.world_mut()
        .resource_mut::<ButtonInput<Key>>()
        .press(Key::Control);
    press(&mut app, s, start, PointerButton::Primary);
    mouse(
        &mut app,
        s,
        end,
        &[PointerAction::Move { delta: end - start }],
    );
    release(&mut app, s, end, PointerButton::Primary);
    app.world_mut()
        .resource_mut::<ButtonInput<Key>>()
        .reset_all();
    assert_eq!(value(&app, e), "Birch MapleBirch");
    assert_eq!(selected(&app, e), "Birch");
    undo(&mut app, e, w, false);
    select(&mut app, e, 0, 5);
    press(&mut app, s, start, PointerButton::Primary);
    mouse(&mut app, s, end, &[PointerAction::Cancel]);
    release(&mut app, s, end, PointerButton::Primary);
    assert_eq!(value(&app, e), "Birch Maple");
    assert_eq!(selected(&app, e), "Birch");
    press(&mut app, s, start, PointerButton::Primary);
    release(&mut app, s, start, PointerButton::Primary);
    assert_eq!(selected(&app, e), "");
    assert_eq!(value(&app, e), "Birch Maple");
}

#[test]
#[ignore = "actual X11 primary selection: run under owned xvfb-run, unset WAYLAND_DISPLAY"]
fn os_primary_is_separate_and_middle_paste_inserts_at_click_without_replacing_old_selection() {
    let (mut app, e, _, s) = setup("Birch Maple");
    app.world_mut()
        .resource_mut::<Clipboard>()
        .set_text("normal clipboard")
        .unwrap();
    app.world_mut()
        .resource_mut::<primary::Primary>()
        .write("A\n\t🦀");
    select(&mut app, e, 0, 5);
    let at = position(&mut app, e, 6);
    press(&mut app, s, at, PointerButton::Middle);
    release(&mut app, s, at, PointerButton::Middle);
    for _ in 0..100 {
        edits(&mut app);
        if value(&app, e) != "Birch Maple" {
            break;
        }
        std::thread::sleep(std::time::Duration::from_millis(5));
    }
    assert_eq!(value(&app, e), "Birch A🦀Maple");
    let mut read = app.world_mut().resource_mut::<Clipboard>().fetch_text();
    assert_eq!(read.poll_result().unwrap().unwrap(), "normal clipboard");
    // Source publishes mouse selection on release, independently of normal copy.
    select(&mut app, e, 0, 5);
    let at = position(&mut app, e, 2);
    press(&mut app, s, at, PointerButton::Primary);
    release(&mut app, s, at, PointerButton::Primary);
    let read = app.world_mut().resource_mut::<primary::Primary>().read();
    let mut result = None;
    for _ in 0..100 {
        result = read.poll();
        if result.is_some() {
            break;
        }
        std::thread::sleep(std::time::Duration::from_millis(5));
    }
    assert_eq!(result, Some(Some("Birch".into())));
}

#[cfg(target_os = "linux")]
mod visual;

#[test]
fn ordered_os_ctrl_drop_keeps_event_modifiers_and_focus_loss_cancels_without_deleting() {
    use crate::ui::input_order::FrameInput;
    use bevy::{
        input::{ButtonState, mouse::MouseButtonInput},
        window::{CursorMoved, WindowEvent},
    };
    let (mut app, e, w, s) = setup("Birch Maple");
    select(&mut app, e, 0, 5);
    let start = position(&mut app, e, 2);
    let end = position(&mut app, e, 11);
    let key = |down| {
        WindowEvent::KeyboardInput(KeyboardInput {
            window: w,
            key_code: KeyCode::ControlLeft,
            logical_key: Key::Control,
            state: if down {
                ButtonState::Pressed
            } else {
                ButtonState::Released
            },
            repeat: false,
            text: None,
        })
    };
    let cursor = |position| {
        WindowEvent::CursorMoved(CursorMoved {
            window: w,
            position,
            delta: None,
        })
    };
    let mouse = |down| {
        WindowEvent::MouseButtonInput(MouseButtonInput {
            window: w,
            button: MouseButton::Left,
            state: if down {
                ButtonState::Pressed
            } else {
                ButtonState::Released
            },
        })
    };
    let mut frame = FrameInput::default();
    for event in [
        key(true),
        cursor(start),
        mouse(true),
        cursor(end),
        mouse(false),
        key(false),
    ] {
        frame.push(event);
    }
    assert!(!frame.current(w).unwrap().logical.pressed(Key::Control));
    app.insert_resource(frame);
    app.world_mut()
        .run_system_once(pointer::ordered::replay)
        .unwrap();
    edits(&mut app);
    assert_eq!(
        value(&app, e),
        "Birch MapleBirch",
        "Ctrl belongs to the release event even though final held keys are empty"
    );
    // The matching picking batch must not dispatch the same OS mouse twice.
    super::affordances::mouse(
        &mut app,
        s,
        end,
        &[
            PointerAction::Press(PointerButton::Primary),
            PointerAction::Release(PointerButton::Primary),
        ],
    );
    assert_eq!(selected(&app, e), "Birch");
    undo(&mut app, e, w, false);
    assert_eq!(value(&app, e), "Birch Maple");
    select(&mut app, e, 0, 5);
    app.world_mut()
        .resource_mut::<Time<Real>>()
        .advance_by(std::time::Duration::from_millis(700));
    let mut frame = app.world_mut().resource_mut::<FrameInput>();
    frame.begin();
    for event in [
        cursor(start),
        mouse(true),
        cursor(end),
        WindowEvent::KeyboardFocusLost(bevy::input::keyboard::KeyboardFocusLost),
    ] {
        frame.push(event);
    }
    drop(frame);
    app.world_mut()
        .run_system_once(pointer::ordered::replay)
        .unwrap();
    edits(&mut app);
    assert_eq!(value(&app, e), "Birch Maple");
    assert_eq!(selected(&app, e), "Birch");
    // Opening a popup during a selected-text press transfers the release to
    // the popup. After dismissal a plain mouse move must not resume that drag.
    app.world_mut()
        .resource_mut::<Time<Real>>()
        .advance_by(std::time::Duration::from_millis(700));
    let mut frame = app.world_mut().resource_mut::<FrameInput>();
    frame.begin();
    for event in [cursor(start), mouse(true), cursor(end)] {
        frame.push(event);
    }
    drop(frame);
    app.world_mut()
        .run_system_once(pointer::ordered::replay)
        .unwrap();
    edits(&mut app);
    assert!(app.world().resource::<popup::Popup>().drag.is_some());
    super::key(&mut app, e, w, Key::ContextMenu, None);
    app.world_mut().run_system_once(popup::render).unwrap();
    let menu = app.world().resource::<InputFocus>().get().unwrap();
    let mut frame = app.world_mut().resource_mut::<FrameInput>();
    frame.begin();
    frame.push(mouse(false));
    drop(frame);
    app.world_mut()
        .run_system_once(pointer::ordered::replay)
        .unwrap();
    edits(&mut app);
    assert!(app.world().resource::<popup::Popup>().drag.is_none());
    super::key(&mut app, menu, w, Key::Escape, None);
    app.world_mut().run_system_once(popup::render).unwrap();
    let mut frame = app.world_mut().resource_mut::<FrameInput>();
    frame.begin();
    frame.push(cursor(end + Vec2::X * 20.));
    drop(frame);
    app.world_mut()
        .run_system_once(pointer::ordered::replay)
        .unwrap();
    edits(&mut app);
    assert!(
        app.world().resource::<popup::Popup>().drag.is_none(),
        "popup release cannot leave a live text-drag capture"
    );
    assert_eq!(value(&app, e), "Birch Maple");
}

#[test]
fn original_double_word_and_triple_selection_keep_source_caret_endpoints() {
    let (mut app, e, _, s) = setup("Birch Maple Oak");
    let at = position(&mut app, e, 2);
    for _ in 0..2 {
        app.world_mut()
            .resource_mut::<Time<Real>>()
            .advance_by(std::time::Duration::from_millis(100));
        mouse(
            &mut app,
            s,
            at,
            &[
                PointerAction::Press(PointerButton::Primary),
                PointerAction::Release(PointerButton::Primary),
            ],
        );
    }
    assert_eq!(selected(&app, e), "Birch");
    app.world_mut()
        .resource_mut::<Time<Real>>()
        .advance_by(std::time::Duration::from_millis(100));
    mouse(
        &mut app,
        s,
        at,
        &[
            PointerAction::Press(PointerButton::Primary),
            PointerAction::Release(PointerButton::Primary),
        ],
    );
    assert_eq!(selected(&app, e), "Birch Maple Oak");
    assert_eq!(
        app.world()
            .get::<EditableText>(e)
            .unwrap()
            .editor
            .raw_selection()
            .focus()
            .index(),
        0,
        "source triple-click caret is at selection.begin"
    );
}
