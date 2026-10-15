use super::*;
use serde_json::json;
use std::time::Duration;

mod authority;
mod visual;

#[test]
fn original_flash_gate_replacement_and_independent_mark_lifetime() {
    let mut state = HurtFeedback::default();
    state.hit(1, 5., Some(Vec3::ZERO), Some(Vec3::X));
    assert_eq!(state.pain_alpha, 0.);
    assert_eq!(
        state.marks.len(),
        1,
        "small directional hits still make a marker"
    );
    state.hit(2, 80., Some(Vec3::ZERO), Some(Vec3::NEG_Z));
    assert_eq!(state.pain_alpha, 0.75);
    assert!(state.marks[1].critical);
    state.hit(3, 8., Some(Vec3::ZERO), None);
    assert!(
        (state.pain_alpha - 0.15).abs() < 1e-6,
        "flash is replaced, not maxed"
    );
    state.advance(0.2);
    assert_eq!(state.pain_alpha, 0.);
    assert_eq!(
        state.marks.len(),
        2,
        "unattributed damage has no invented bearing"
    );
    state.advance(1.3);
    assert_eq!(state.marks[0].alpha(), 1.);
    state.advance(0.75);
    assert!((state.marks[0].alpha() - 0.5).abs() < 1e-6);
    state.advance(0.75);
    assert!(state.marks.is_empty());
    state.hit(4, 10., Some(Vec3::ZERO), Some(Vec3::Y * 100.));
    assert!(
        state.marks.is_empty(),
        "overhead sources have no horizontal direction"
    );
    assert!(state.pain_alpha > 0.);
}

#[test]
fn original_bearing_reprojects_world_direction_as_camera_turns_and_ignores_pitch() {
    let camera = Transform::IDENTITY;
    for (direction, expected) in [
        (Vec3::NEG_Z, 0.),
        (Vec3::X, 90.),
        (Vec3::Z, 180.),
        (Vec3::NEG_X, -90.),
    ] {
        assert!((bearing(direction, &camera).unwrap().to_degrees() - expected).abs() < 1e-4);
    }
    let pitched = Transform::from_rotation(Quat::from_rotation_x(0.8));
    assert!(bearing(Vec3::NEG_Z, &pitched).unwrap().abs() < 1e-6);
    let turned = Transform::from_rotation(Quat::from_rotation_y(-std::f32::consts::FRAC_PI_2));
    assert!((bearing(Vec3::NEG_Z, &turned).unwrap().to_degrees() + 90.).abs() < 1e-4);
    let up = Transform::from_rotation(Quat::from_rotation_x(std::f32::consts::FRAC_PI_2));
    assert!(
        bearing(Vec3::X, &up).is_none(),
        "no arbitrary bearing at a vertical camera"
    );
}

fn app() -> App {
    let mut app = App::new();
    let mut model = UiModel::default();
    model.connected = true;
    app.add_plugins(MinimalPlugins)
        .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
            Duration::from_millis(20),
        ))
        .init_resource::<WorldSessionEpoch>()
        .init_resource::<HurtFeedback>()
        .insert_resource(model)
        .insert_resource(UiState {
            screen: super::super::Screen::Playing,
            ..default()
        })
        .add_message::<CoreEvent>()
        .add_systems(Update, receive);
    app.world_mut().spawn((
        PlayerBody::for_motion_test(Vec3::ZERO, true, "stand"),
        Transform::IDENTITY,
    ));
    app.update();
    app.update();
    app
}
fn hit(app: &mut App, sequence: u64) {
    app.world_mut().write_message(CoreEvent {
        sequence,
        kind: "hurt".into(),
        data: json!({"damage":30.,"hasSource":true,"sourcePos":{"x":2.,"y":5.,"z":0.}}),
    });
}

#[test]
fn accepted_event_replay_epoch_death_and_snapshot_boundaries_do_not_create_local_damage() {
    let mut app = app();
    // Replicated health alone is not a hit: loading a low-HP save has no attacker.
    app.world_mut().resource_mut::<UiModel>().health = 30.;
    app.update();
    assert!(app.world().resource::<HurtFeedback>().marks.is_empty());
    hit(&mut app, 10);
    app.update();
    let state = app.world().resource::<HurtFeedback>();
    assert_eq!(state.marks.len(), 1);
    assert_eq!(state.marks[0].direction, Vec3::X);
    let first_alpha = state.pain_alpha;
    hit(&mut app, 10);
    app.update();
    let state = app.world().resource::<HurtFeedback>();
    assert_eq!(state.marks.len(), 1);
    assert!(
        state.pain_alpha < first_alpha,
        "duplicate event cannot refresh flash"
    );
    assert_eq!(app.world().resource::<UiModel>().health, 30.);

    app.world_mut().resource_mut::<UiModel>().alive = false;
    hit(&mut app, 11);
    app.update();
    assert_eq!(
        app.world().resource::<HurtFeedback>().marks.len(),
        2,
        "lethal hurt remains under death overlay"
    );
    app.world_mut().resource_mut::<UiModel>().alive = true;
    app.update();
    assert_eq!(app.world().resource::<HurtFeedback>().pain_alpha, 0.);
    app.world_mut().resource_mut::<WorldSessionEpoch>().0 += 1;
    hit(&mut app, 12);
    app.update();
    assert!(app.world().resource::<HurtFeedback>().marks.is_empty());
    hit(&mut app, 13);
    app.update();
    assert_eq!(app.world().resource::<HurtFeedback>().marks.len(), 1);
    app.world_mut().resource_mut::<UiModel>().connected = false;
    app.update();
    assert!(app.world().resource::<HurtFeedback>().marks.is_empty());
}

#[test]
fn local_pause_freezes_feedback_but_network_pause_still_receives_and_ages_hits() {
    let mut app = app();
    hit(&mut app, 1);
    app.update();
    let before = app.world().resource::<HurtFeedback>().pain_alpha;
    app.world_mut().resource_mut::<UiState>().screen = super::super::Screen::Paused;
    app.update();
    assert_eq!(app.world().resource::<HurtFeedback>().pain_alpha, before);
    app.world_mut().resource_mut::<UiState>().networked = true;
    hit(&mut app, 2);
    app.update();
    assert_eq!(app.world().resource::<HurtFeedback>().marks.len(), 2);
    let state = app.world().resource::<HurtFeedback>();
    assert!(state.marks[0].age > state.marks[1].age);
}
