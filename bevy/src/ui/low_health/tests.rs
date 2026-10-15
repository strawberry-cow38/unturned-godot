use super::*;
use std::time::Duration;
#[test]
fn original_fraction_thresholds_and_exponential_recovery_have_no_alive_gate() {
    let mut state = State::default();
    for fraction in [1., 0.5, 2.] {
        state.step(fraction, 1.);
        assert_eq!(state.strength, 0.);
    }
    state.step(0.325, 0.1);
    let expected = 0.5 * (1. - (-0.6_f32).exp());
    assert!((state.strength - expected).abs() < 1e-6);
    state.step(0., 0.2);
    let dead = expected + (1. - expected) * (1. - (-1.2_f32).exp());
    assert!((state.strength - dead).abs() < 1e-6);
    state.step(1., 0.1);
    assert!((state.strength - dead * (-0.6_f32).exp()).abs() < 1e-6);
    let before = state.strength;
    state.step(0., 0.);
    assert_eq!(state.strength, before);
}
#[test]
fn accepted_owner_health_targets_only_hud_and_survives_death_but_not_session_reset() {
    let mut app = App::new();
    let mut model = UiModel::default();
    model.connected = true;
    model.health = 15.;
    app.init_resource::<Time>()
        .init_resource::<State>()
        .init_resource::<WorldSessionEpoch>()
        .init_resource::<HurtFeedback>()
        .insert_resource(model)
        .insert_resource(UiState {
            screen: super::super::Screen::Playing,
            ..default()
        })
        .add_systems(Update, update);
    let hud = app.world_mut().spawn(IsDefaultUiCamera).id();
    let other = app.world_mut().spawn(Camera2d).id();
    app.world_mut()
        .resource_mut::<Time>()
        .advance_by(Duration::from_millis(100));
    app.world_mut().resource_mut::<HurtFeedback>().pain_alpha = 0.4;
    app.update();
    assert!(app.world().get::<Feedback>(other).is_none());
    let first = app.world().get::<Feedback>(hud).unwrap().parameters;
    assert!(first.x > 0.);
    assert_eq!(first.y, 0.4);
    app.world_mut().resource_mut::<UiModel>().alive = false;
    app.update();
    assert!(app.world().get::<Feedback>(hud).unwrap().parameters.x > first.x);
    let dead = app.world().resource::<State>().strength;
    app.world_mut().resource_mut::<UiModel>().alive = true;
    app.world_mut().resource_mut::<UiModel>().health = 100.;
    app.update();
    let recovery = app.world().resource::<State>().strength;
    assert!(recovery > 0. && recovery < dead);
    app.world_mut().resource_mut::<UiState>().screen = super::super::Screen::Paused;
    app.update();
    assert_eq!(app.world().resource::<State>().strength, recovery);
    app.world_mut().resource_mut::<UiState>().networked = true;
    app.update();
    assert!(app.world().resource::<State>().strength < recovery);
    app.world_mut().resource_mut::<WorldSessionEpoch>().0 += 1;
    app.update();
    assert_eq!(app.world().resource::<State>().strength, 0.);
    app.world_mut().resource_mut::<UiModel>().connected = false;
    app.update();
    assert_eq!(
        app.world().get::<Feedback>(hud).unwrap().parameters,
        Vec4::ZERO
    );
}
