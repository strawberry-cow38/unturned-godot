use super::*;
use std::time::Duration;

fn app() -> App {
    let mut app = App::new();
    let mut model = UiModel::default();
    model.connected = true;
    app.init_resource::<Time>()
        .insert_resource(model)
        .insert_resource(UiState {
            screen: Screen::Inventory,
            ..default()
        })
        .add_plugins(SwoopPlugin);
    app
}
fn step(app: &mut App, seconds: f64) {
    app.world_mut()
        .resource_mut::<Time>()
        .advance_by(Duration::from_secs_f64(seconds));
    app.update();
}
fn root(app: &mut App, screen: Screen) -> (Entity, Entity) {
    let root = app
        .world_mut()
        .spawn((Node::default(), Swoop(screen), Backdrop::dashboard(1.)))
        .id();
    let child = app
        .world_mut()
        .spawn((
            ChildOf(root),
            Node::default(),
            BackgroundColor(Color::srgba(0.3, 0.4, 0.5, 0.8)),
            BorderColor::all(Color::srgba(1., 0., 0., 0.6)),
            TextColor(Color::srgba(1., 1., 1., 0.4)),
            ImageNode {
                color: Color::srgba(0., 1., 1., 0.2),
                ..default()
            },
            Button,
            UiButton::Open(Screen::Crafting),
        ))
        .id();
    (root, child)
}
fn close(app: &mut App, entity: Entity, next: Screen) {
    app.world_mut().resource_mut::<UiState>().screen = next;
    app.world_mut().entity_mut(entity).insert(Retiring);
}
fn approx(a: f32, b: f32) {
    assert!((a - b).abs() < 0.00001, "{a} != {b}");
}

#[test]
fn original_85ms_easing_and_reversal_keep_the_existing_clock() {
    let mut clock = Clock::default();
    clock.step(true, SECONDS * 0.5);
    approx(clock.t, 0.5);
    approx(clock.alpha(), 0.875);
    clock.step(false, 0.);
    approx(clock.alpha(), 0.25);
    clock.step(false, SECONDS * 0.25);
    approx(clock.t, 0.25);
    approx(clock.alpha(), 0.0625);
    clock.step(true, 0.);
    approx(clock.t, 0.25);
    approx(clock.alpha(), 1. - 0.75f32.powi(3));
    clock.step(true, SECONDS);
    assert_eq!(clock.direction, 0);
    assert_eq!(clock.alpha(), 1.);
    clock.step(false, SECONDS);
    assert_eq!(clock.direction, 0);
    assert_eq!(clock.alpha(), 0.);
}

#[test]
fn child_colors_use_source_alpha_once_and_follow_live_hover_changes() {
    let mut app = app();
    let (root, child) = root(&mut app, Screen::Inventory);
    step(&mut app, 0.0425);
    approx(
        app.world().get::<BackgroundColor>(child).unwrap().0.alpha(),
        0.8 * 0.875,
    );
    approx(
        app.world().get::<BorderColor>(child).unwrap().top.alpha(),
        0.6 * 0.875,
    );
    approx(
        app.world().get::<TextColor>(child).unwrap().0.alpha(),
        0.4 * 0.875,
    );
    approx(
        app.world().get::<ImageNode>(child).unwrap().color.alpha(),
        0.2 * 0.875,
    );
    assert_eq!(
        app.world().get::<UiTransform>(root).unwrap().translation.y,
        px(6.75)
    );
    step(&mut app, 0.);
    approx(
        app.world().get::<BackgroundColor>(child).unwrap().0.alpha(),
        0.8 * 0.875,
    );
    app.world_mut().get_mut::<BackgroundColor>(child).unwrap().0 = Color::srgba(1., 0.2, 0.1, 0.3);
    step(&mut app, 0.);
    approx(
        app.world().get::<BackgroundColor>(child).unwrap().0.alpha(),
        0.3 * 0.875,
    );
    step(&mut app, 0.0425);
    approx(
        app.world().get::<BackgroundColor>(child).unwrap().0.alpha(),
        0.3,
    );
    assert!(app.world().get::<Paint>(child).is_none());
    assert_eq!(
        app.world().get::<UiTransform>(root).unwrap().translation.y,
        px(0.)
    );
}

#[test]
fn tab_change_hides_old_frost_disables_actions_and_retires_pixels_on_time() {
    let mut app = app();
    let (inventory, button) = root(&mut app, Screen::Inventory);
    step(&mut app, 0.085);
    close(&mut app, inventory, Screen::Crafting);
    let (craft, _) = root(&mut app, Screen::Crafting);
    step(&mut app, 0.02125);
    assert_eq!(app.world().get::<Backdrop>(inventory).unwrap().fade, 0.);
    approx(app.world().get::<Backdrop>(craft).unwrap().fade, 0.578125);
    approx(
        app.world()
            .get::<BackgroundColor>(button)
            .unwrap()
            .0
            .alpha(),
        0.8 * 0.5625,
    );
    assert!(app.world().get::<Button>(button).is_none());
    assert!(app.world().get::<UiButton>(button).is_none());
    let picking = app.world().get::<Pickable>(button).unwrap();
    assert!(!picking.is_hoverable && !picking.should_block_lower);
    step(&mut app, 0.06375);
    assert!(app.world().get_entity(inventory).is_err());
    assert!(app.world().get_entity(button).is_err());
    assert!(app.world().get_entity(craft).is_ok());
}

#[test]
fn map_close_ducks_below_hud_and_reopen_reverses_without_a_second_root() {
    let mut app = app();
    app.world_mut().resource_mut::<UiState>().screen = Screen::Information;
    let (map, _) = root(&mut app, Screen::Information);
    step(&mut app, 0.085);
    close(&mut app, map, Screen::Playing);
    step(&mut app, 0.0425);
    assert!(
        app.world().resource::<UiState>().captures_input(),
        "source map cursor remains free until Closed"
    );
    assert!(app.world().get::<Backdrop>(map).is_none());
    assert_eq!(app.world().get::<GlobalZIndex>(map).unwrap().0, 9);
    assert_eq!(
        app.world().get::<UiTransform>(map).unwrap().translation.y,
        px(40.5)
    );
    app.world_mut().resource_mut::<UiState>().screen = Screen::Information;
    let (reopened, _) = root(&mut app, Screen::Information);
    step(&mut app, 0.);
    assert!(app.world().get_entity(map).is_err());
    assert!(
        !app.world().resource::<UiState>().map_closing,
        "reopen cancels pending recapture"
    );
    approx(app.world().get::<Backdrop>(reopened).unwrap().fade, 0.875);
    app.world_mut().resource_mut::<UiModel>().connected = false;
    step(&mut app, 0.);
    assert!(app.world().get_entity(reopened).is_err());
    assert!(app.world().resource::<Clocks>().0.iter().all(|c| c.t == 0.));
}

#[test]
fn map_tab_switch_does_not_hold_the_cursor_after_the_new_tab_closes() {
    let mut app = app();
    app.world_mut().resource_mut::<UiState>().screen = Screen::Information;
    let (map, _) = root(&mut app, Screen::Information);
    step(&mut app, 0.085);
    close(&mut app, map, Screen::Crafting);
    let (craft, _) = root(&mut app, Screen::Crafting);
    step(&mut app, 0.01);
    assert!(!app.world().resource::<UiState>().map_closing);
    close(&mut app, craft, Screen::Playing);
    step(&mut app, 0.01);
    assert!(
        !app.world().resource::<UiState>().captures_input(),
        "MapUI.Close(false) cannot recapture or delay another screen's cursor"
    );
    assert!(app.world().get_entity(map).is_ok());
    // A direct map close retains capture until the exact end of the animation.
    app.world_mut().resource_mut::<UiState>().screen = Screen::Information;
    let (map, _) = root(&mut app, Screen::Information);
    step(&mut app, 0.085);
    close(&mut app, map, Screen::Playing);
    step(&mut app, 0.084);
    assert!(app.world().resource::<UiState>().captures_input());
    step(&mut app, 0.0011);
    assert!(!app.world().resource::<UiState>().captures_input());
    assert!(app.world().get_entity(map).is_err());
}

#[test]
fn real_inventory_snapshot_rebuild_preserves_progress_and_close_keeps_no_live_buttons() {
    let mut app = app();
    app.add_plugins((
        bevy::app::TaskPoolPlugin::default(),
        bevy::asset::AssetPlugin::default(),
    ))
    .init_resource::<crate::settings::Settings>()
    .init_resource::<crate::entities::ItemCatalog>()
    .init_resource::<crate::profile::LocalProfile>()
    .add_systems(Update, super::super::rebuild_overlay);
    let overlay = |app: &mut App| {
        app.world_mut()
            .query_filtered::<Entity, With<super::super::OverlayRoot>>()
            .single(app.world())
            .unwrap()
    };
    step(&mut app, 0.02125);
    let first = overlay(&mut app);
    approx(app.world().get::<Backdrop>(first).unwrap().fade, 0.578125);
    app.world_mut()
        .resource_mut::<UiModel>()
        .pages
        .push(super::super::InventoryPage {
            index: 2,
            width: 5,
            height: 5,
            ..default()
        });
    step(&mut app, 0.02125);
    let replacement = overlay(&mut app);
    assert_ne!(first, replacement);
    assert!(app.world().get_entity(first).is_err());
    approx(
        app.world().get::<Backdrop>(replacement).unwrap().fade,
        0.875,
    );
    app.world_mut().resource_mut::<UiState>().screen = Screen::Playing;
    step(&mut app, 0.);
    assert!(
        app.world()
            .get::<super::super::OverlayRoot>(replacement)
            .is_none()
    );
    assert!(app.world().get::<Retiring>(replacement).is_some());
    assert_eq!(
        app.world_mut()
            .query_filtered::<Entity, With<UiButton>>()
            .iter(app.world())
            .count(),
        0
    );
    step(&mut app, 0.0425);
    assert!(app.world().get_entity(replacement).is_err());
}
