use super::*;
use bevy::ecs::system::RunSystemOnce;
use std::time::Duration;

pub(super) fn setup() -> App {
    let mut app = App::new();
    app.init_resource::<Settings>()
        .init_resource::<ButtonInput<KeyCode>>()
        .init_resource::<ButtonInput<MouseButton>>()
        .init_resource::<UiState>()
        .init_resource::<GameSnapshot>()
        .init_resource::<WorldSessionEpoch>()
        .init_resource::<Time<Real>>()
        .init_resource::<Menu>()
        .add_message::<UiIntent>();
    app.world_mut().resource_mut::<UiState>().screen = Screen::Playing;
    app.world_mut().resource_mut::<GameSnapshot>().0 = json!({"connected":true,"player":{"alive":true,"vehicleNetId":7,"vehicleSeat":0},
        "vehicles":[{"netId":7,"typeId":data::named("police").unwrap().type_id,"flags":0,"lightbarPattern":0}]});
    app.world_mut().spawn((
        Window {
            resolution: bevy::window::WindowResolution::new(1280, 1080),
            ..default()
        },
        PrimaryWindow,
    ));
    app
}
pub(super) fn step(app: &mut App, ms: u64) {
    app.world_mut()
        .resource_mut::<Time<Real>>()
        .advance_by(Duration::from_millis(ms));
    app.world_mut().run_system_once(input).unwrap();
}
pub(super) fn clear(app: &mut App) {
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .clear();
    app.world_mut()
        .resource_mut::<ButtonInput<MouseButton>>()
        .clear();
}
pub(super) fn events(app: &mut App) -> Vec<UiIntent> {
    app.world_mut()
        .resource_mut::<Messages<UiIntent>>()
        .drain()
        .collect()
}
pub(super) fn open(app: &mut App) {
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .press(KeyCode::ControlLeft);
    step(app, 0);
    clear(app);
    step(app, 220);
    assert_eq!(app.world().resource::<UiState>().screen, Screen::Lightbar);
}
#[test]
fn source_ctrl_tap_and_hold_boundary_emit_distinct_intents_without_optimistic_state() {
    let mut app = setup();
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .press(KeyCode::ControlLeft);
    step(&mut app, 0);
    clear(&mut app);
    step(&mut app, 219);
    assert_eq!(app.world().resource::<UiState>().screen, Screen::Playing);
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .release(KeyCode::ControlLeft);
    step(&mut app, 0);
    let e = events(&mut app);
    assert!(
        matches!(&e[..],[UiIntent::Action{name,args}]if name=="vehicleLighting"&&args["action"]=="ToggleSiren")
    );
    assert_eq!(
        app.world().resource::<GameSnapshot>().0["vehicles"][0]["flags"],
        0
    );
    clear(&mut app);
    open(&mut app);
    assert!(events(&mut app).is_empty());
}
#[test]
fn radial_release_click_cancel_hub_and_disabled_off_match_original() {
    let mut app = setup();
    open(&mut app);
    app.world_mut()
        .resource_mut::<Menu>()
        .radial
        .as_mut()
        .unwrap()
        .highlight = Some(1);
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .release(KeyCode::ControlLeft);
    step(&mut app, 1);
    assert!(
        matches!(&events(&mut app)[..],[UiIntent::Action{args,..}]if args["action"]=="SetPattern"&&args["value"]==1)
    );
    clear(&mut app);
    open(&mut app);
    app.world_mut()
        .resource_mut::<Menu>()
        .radial
        .as_mut()
        .unwrap()
        .highlight = None;
    app.world_mut()
        .resource_mut::<ButtonInput<MouseButton>>()
        .press(MouseButton::Left);
    step(&mut app, 1);
    assert!(events(&mut app).is_empty());
    assert!(
        !app.world()
            .resource::<ButtonInput<MouseButton>>()
            .just_pressed(MouseButton::Left)
    );
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .release(KeyCode::ControlLeft);
    clear(&mut app);
    open(&mut app);
    app.world_mut()
        .resource_mut::<Menu>()
        .radial
        .as_mut()
        .unwrap()
        .highlight = Some(3);
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .release(KeyCode::ControlLeft);
    step(&mut app, 1);
    assert!(events(&mut app).is_empty());
    clear(&mut app);
    open(&mut app);
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .press(KeyCode::Escape);
    step(&mut app, 1);
    assert!(events(&mut app).is_empty());
    assert!(
        !app.world()
            .resource::<ButtonInput<KeyCode>>()
            .just_pressed(KeyCode::Escape)
    );
    assert_eq!(app.world().resource::<UiState>().screen, Screen::Playing);
}
#[test]
fn authority_loss_session_change_and_non_driver_do_not_open_or_submit() {
    let mut app = setup();
    app.world_mut().resource_mut::<GameSnapshot>().0["player"]["vehicleSeat"] = json!(1);
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .press(KeyCode::ControlLeft);
    step(&mut app, 0);
    step(&mut app, 500);
    assert_eq!(app.world().resource::<UiState>().screen, Screen::Playing);
    assert!(events(&mut app).is_empty());
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .release(KeyCode::ControlLeft);
    clear(&mut app);
    app.world_mut().resource_mut::<GameSnapshot>().0["player"]["vehicleSeat"] = json!(0);
    open(&mut app);
    app.world_mut().resource_mut::<GameSnapshot>().0["player"]["vehicleSeat"] = json!(1);
    step(&mut app, 1);
    assert_eq!(app.world().resource::<UiState>().screen, Screen::Playing);
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .release(KeyCode::ControlLeft);
    clear(&mut app);
    app.world_mut().resource_mut::<GameSnapshot>().0["player"]["vehicleSeat"] = json!(0);
    open(&mut app);
    app.world_mut().resource_mut::<WorldSessionEpoch>().0 += 1;
    step(&mut app, 1);
    assert_eq!(app.world().resource::<UiState>().screen, Screen::Playing);
    assert!(events(&mut app).is_empty());
}
#[test]
fn exact_directional_wedges_and_scaled_cancel_radius_stay_aligned() {
    let mut r = radial::Selection::new(7, &json!({"flags":8,"lightbarPattern":2}));
    let vp = Vec2::new(1280., 1080.);
    for (i, d) in [Vec2::NEG_Y, Vec2::X, Vec2::Y, Vec2::NEG_X]
        .into_iter()
        .enumerate()
    {
        r.point(vp * 0.5 + d * 200., vp);
        assert_eq!(r.highlight, Some(i));
    }
    r.point(vp * 0.5, vp);
    assert_eq!(r.confirm(), None);
    r.point(vp * 0.5 - Vec2::X * 200., vp);
    assert_eq!(r.confirm(), Some(-1));
}

#[test]
fn source_hover_pop_ignores_disabled_off_and_settles_exactly() {
    let mut r = radial::Selection::new(7, &json!({"flags":0}));
    r.highlight = Some(3);
    r.advance(0.12);
    assert_eq!(r.open, 1.);
    assert_eq!(r.pop, Vec4::ZERO);
    r.highlight = Some(1);
    for _ in 0..20 {
        r.advance(0.02);
    }
    assert_eq!(r.pop, Vec4::Y);
    r.highlight = None;
    for _ in 0..20 {
        r.advance(0.02);
    }
    assert_eq!(r.pop, Vec4::ZERO);
}

#[test]
fn cancellation_wins_when_slow_frames_coalesce_confirm_and_cancel_edges() {
    for keyboard in [true, false] {
        let mut app = setup();
        open(&mut app);
        app.world_mut()
            .resource_mut::<Menu>()
            .radial
            .as_mut()
            .unwrap()
            .highlight = Some(1);
        if keyboard {
            let mut keys = app.world_mut().resource_mut::<ButtonInput<KeyCode>>();
            keys.press(KeyCode::Escape);
            keys.release(KeyCode::ControlLeft);
        } else {
            let mut mouse = app.world_mut().resource_mut::<ButtonInput<MouseButton>>();
            mouse.press(MouseButton::Right);
            mouse.press(MouseButton::Left);
        }
        step(&mut app, 1);
        assert!(events(&mut app).is_empty());
        assert_eq!(app.world().resource::<UiState>().screen, Screen::Playing);
        assert!(app.world().resource::<Menu>().radial.is_none());
    }
}
