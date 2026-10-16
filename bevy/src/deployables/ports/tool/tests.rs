use super::*;
mod manage;
mod review;
use bevy::input::keyboard::{Key, KeyboardInput};

fn key(app: &mut App, window: Entity, code: KeyCode, down: bool) {
    app.world_mut()
        .resource_mut::<FrameInput>()
        .push(WindowEvent::KeyboardInput(KeyboardInput {
            key_code: code,
            logical_key: Key::Character("x".into()),
            state: if down {
                ButtonState::Pressed
            } else {
                ButtonState::Released
            },
            text: None,
            repeat: false,
            window,
        }));
}
fn step(app: &mut App, n: usize) {
    super::super::tests::step(app, n);
}

#[test]
fn source_route_node_budget_undo_and_rebound_physical_inputs_use_ordered_edges() {
    let (mut app, window, _, start) = super::super::tests::fixture();
    app.world_mut().resource_mut::<Settings>().bindings.insert(
        GameAction::Fire,
        crate::settings::Binding::Key("KeyJ".into()),
    );
    app.world_mut().resource_mut::<Settings>().bindings.insert(
        GameAction::Aim,
        crate::settings::Binding::Key("KeyK".into()),
    );
    super::super::tests::aim(&mut app, start);
    key(&mut app, window, KeyCode::KeyJ, true);
    key(&mut app, window, KeyCode::KeyJ, false);
    step(&mut app, 1);
    assert_eq!(app.world().resource::<Gesture>().start, Some(start));
    let mut camera = app
        .world_mut()
        .query_filtered::<&mut Transform, With<PlayerCamera>>();
    let mut transform = camera.single_mut(app.world_mut()).unwrap();
    transform.rotation = Quat::IDENTITY;
    transform.translation += Vec3::Y * 2.;
    drop(transform);
    step(&mut app, 2);
    assert!(app.world().resource::<Frame>().hovered.is_none());
    for _ in 0..21 {
        key(&mut app, window, KeyCode::KeyJ, true);
        key(&mut app, window, KeyCode::KeyJ, false);
    }
    step(&mut app, 1);
    let gesture = app.world().resource::<Gesture>();
    assert_eq!(
        gesture.nodes.len(),
        20,
        "source refuses the21st routing node"
    );
    assert!(!gesture.valid);
    assert!(
        gesture.hud.ends_with("   -- LIMIT"),
        "20 nodes previews red even though a legal end may complete"
    );
    for _ in 0..20 {
        key(&mut app, window, KeyCode::KeyK, true);
        key(&mut app, window, KeyCode::KeyK, false);
    }
    step(&mut app, 1);
    assert_eq!(app.world().resource::<Gesture>().nodes.len(), 0);
    assert_eq!(
        app.world().resource::<Gesture>().start,
        Some(start),
        "undo of last node does not also cancel"
    );
    key(&mut app, window, KeyCode::KeyK, true);
    key(&mut app, window, KeyCode::KeyK, false);
    step(&mut app, 1);
    assert!(
        app.world().resource::<Gesture>().start.is_none(),
        "empty-route RMB cancels without managing a completed edge"
    );
    assert!(
        app.world().resource::<Preview>().segments.is_empty(),
        "cancel retires every preview segment"
    );
}
