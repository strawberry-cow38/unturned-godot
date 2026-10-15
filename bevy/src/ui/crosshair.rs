//! HUD.Crosshair3PControl and PlayerController.CrosshairSpread01. The first-person
//! on-foot view has no generic crosshair; its held sights mark the aim.
use super::{Screen, UiModel, UiState};
use crate::{
    avatar::Perspective,
    entities::ItemCatalog,
    player::{PlayerBody, WorldSessionEpoch},
    recoil::Recoil,
    vehicle::Occupancy,
    viewmodel::ViewmodelStatus,
};
use bevy::prelude::*;

pub(super) struct CrosshairPlugin;
impl Plugin for CrosshairPlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<Spread>()
            .add_systems(Startup, spawn)
            // Source computes openness after movement/recoil and viewmodel aim.
            // All three native states are final by PostUpdate, before UI layout.
            .add_systems(PostUpdate, update.before(bevy::ui::UiSystems::Layout));
    }
}
#[derive(Component)]
struct Crosshair;
#[derive(Component)]
struct Arm(usize);
#[derive(Resource)]
struct Spread {
    value: f32,
    epoch: u64,
}
impl Default for Spread {
    fn default() -> Self {
        Self {
            value: 1.,
            epoch: u64::MAX,
        }
    }
}
const HALO: f32 = 1.5;
fn rectangles(spread: f32) -> [(Vec2, Vec2); 5] {
    let gap = 2.5 + 9.5 * spread;
    [
        (Vec2::new(-1., -gap - 9.), Vec2::new(2., 9.)),
        (Vec2::new(-1., gap), Vec2::new(2., 9.)),
        (Vec2::new(-gap - 9., -1.), Vec2::new(9., 2.)),
        (Vec2::new(gap, -1.), Vec2::new(9., 2.)),
        (Vec2::splat(-1.5), Vec2::splat(3.)),
    ]
}
fn halo_node(position: Vec2, size: Vec2) -> Node {
    Node {
        position_type: PositionType::Absolute,
        left: px(position.x - HALO),
        top: px(position.y - HALO),
        width: px(size.x + 2. * HALO),
        height: px(size.y + 2. * HALO),
        ..default()
    }
}
fn spawn(mut commands: Commands) {
    commands
        .spawn((
            Name::new("Original third-person crosshair"),
            Crosshair,
            Node {
                position_type: PositionType::Absolute,
                left: percent(50),
                top: percent(50),
                width: px(0),
                height: px(0),
                ..default()
            },
            Visibility::Hidden,
            GlobalZIndex(21),
            Pickable::IGNORE,
        ))
        .with_children(|parent| {
            for (index, (position, size)) in rectangles(1.).into_iter().enumerate() {
                parent
                    .spawn((
                        Arm(index),
                        halo_node(position, size),
                        BackgroundColor(Color::srgba(0., 0., 0., 0.85)),
                        Pickable::IGNORE,
                    ))
                    .with_children(|outline| {
                        outline.spawn((
                            Node {
                                position_type: PositionType::Absolute,
                                left: px(HALO),
                                top: px(HALO),
                                width: px(size.x),
                                height: px(size.y),
                                ..default()
                            },
                            BackgroundColor(Color::WHITE),
                            Pickable::IGNORE,
                        ));
                    });
            }
        });
}
fn tween(
    current: f32,
    aiming: bool,
    spread_aim: f32,
    speed_squared: f32,
    pending_pitch: f32,
    dt: f32,
) -> f32 {
    let mut target = if aiming {
        spread_aim.clamp(0.05, 1.)
    } else {
        1.
    };
    if speed_squared > 0.25 {
        target += 0.3;
    }
    target += (pending_pitch.abs() * 0.4).min(1.);
    current + (target - current) * (12. * dt).min(1.)
}
#[allow(clippy::too_many_arguments)]
fn update(
    time: Res<Time>,
    model: Res<UiModel>,
    ui: Res<UiState>,
    view: Res<Perspective>,
    occupancy: Res<Occupancy>,
    status: Res<ViewmodelStatus>,
    catalog: Res<ItemCatalog>,
    recoil: Res<Recoil>,
    epoch: Res<WorldSessionEpoch>,
    body: Query<&PlayerBody>,
    mut spread: ResMut<Spread>,
    mut root: Query<&mut Visibility, With<Crosshair>>,
    mut arms: Query<(&Arm, &mut Node)>,
) {
    if spread.epoch != epoch.0 {
        spread.value = 1.;
        spread.epoch = epoch.0;
    }
    let alive = model.connected && model.alive && ui.screen != Screen::MainMenu;
    // Source ThirdPersonActive excludes both driven and passenger vehicle views.
    // CannonAiming / GunsightActive are not ported yet; do not infer them from
    // the existence of a turret mesh or show an unsupported first-person mark.
    let visible = alive && !view.first_person && occupancy.seat.is_none();
    for mut visibility in &mut root {
        *visibility = if visible {
            Visibility::Inherited
        } else {
            Visibility::Hidden
        };
    }
    if alive && occupancy.seat.is_none() {
        let aim = catalog
            .items
            .get(&status.held)
            .and_then(|item| item.gun["spreadAim"].as_f64())
            .unwrap_or(0.35) as f32;
        let speed_squared = body
            .single()
            .map_or(0., |body| body.velocity().length_squared());
        spread.value = tween(
            spread.value,
            status.aiming,
            aim,
            speed_squared,
            recoil.pending_pitch(),
            time.delta_secs(),
        );
    }
    let marks = rectangles(spread.value);
    for (arm, mut node) in &mut arms {
        let (position, size) = marks[arm.0];
        *node = halo_node(position, size);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::Duration;

    #[test]
    fn marks_have_exact_source_center_dimensions_and_spread_bands() {
        let marks = rectangles(1.);
        assert_eq!(marks[0], (Vec2::new(-1., -21.), Vec2::new(2., 9.)));
        assert_eq!(marks[1], (Vec2::new(-1., 12.), Vec2::new(2., 9.)));
        assert_eq!(marks[2], (Vec2::new(-21., -1.), Vec2::new(9., 2.)));
        assert_eq!(marks[3], (Vec2::new(12., -1.), Vec2::new(9., 2.)));
        assert_eq!(marks[4].0 + marks[4].1 * 0.5, Vec2::ZERO);
        assert_eq!(halo_node(marks[4].0, marks[4].1).left, px(-3.));
        assert!((rectangles(0.05)[3].0.x - 2.975).abs() < 1e-6);
        // Inclusive speed threshold, vertical pending recoil only, original
        // ADS clamp and 12/s lerp. Large frame gaps clamp rather than overshoot.
        assert_eq!(tween(1., false, 0.05, 0.25, 0., 1.), 1.);
        assert!((tween(1., false, 0.05, 0.2501, 0., 1.) - 1.3).abs() < 1e-6);
        assert!((tween(1., true, 0., 0., 0., 0.02) - 0.772).abs() < 1e-6);
        assert!((tween(1., true, 0.05, 0., -20., 1.) - 1.05).abs() < 1e-6);
    }

    #[test]
    fn actual_ui_system_hides_first_person_dead_and_vehicle_views_then_restores_on_foot() {
        let mut app = App::new();
        app.init_resource::<Time>()
            .init_resource::<UiModel>()
            .init_resource::<UiState>()
            .init_resource::<Perspective>()
            .init_resource::<Occupancy>()
            .init_resource::<ViewmodelStatus>()
            .init_resource::<ItemCatalog>()
            .init_resource::<Recoil>()
            .init_resource::<WorldSessionEpoch>()
            .add_plugins(CrosshairPlugin);
        app.world_mut().resource_mut::<UiModel>().connected = true;
        app.world_mut().resource_mut::<UiState>().screen = Screen::Playing;
        app.update();
        let root = app
            .world_mut()
            .query_filtered::<Entity, With<Crosshair>>()
            .single(app.world())
            .unwrap();
        let visibility = |app: &App| *app.world().get::<Visibility>(root).unwrap();
        assert_eq!(visibility(&app), Visibility::Hidden);
        let node = app.world().get::<Node>(root).unwrap();
        assert_eq!(
            (node.left, node.top, node.width, node.height),
            (percent(50), percent(50), px(0), px(0))
        );
        assert_eq!(
            app.world_mut()
                .query_filtered::<Entity, With<Arm>>()
                .iter(app.world())
                .count(),
            5
        );
        assert_eq!(
            app.world_mut().query::<&Text>().iter(app.world()).count(),
            0
        );
        app.world_mut().resource_mut::<Perspective>().first_person = false;
        app.update();
        assert_eq!(visibility(&app), Visibility::Inherited);
        let vehicle = app.world_mut().spawn_empty().id();
        app.world_mut().resource_mut::<Occupancy>().seat = Some(crate::vehicle::OccupiedSeat {
            vehicle,
            net_id: 1,
            seat: 1,
            body_pose: Transform::IDENTITY,
            eye_pose: Transform::IDENTITY,
            velocity: Vec3::ZERO,
            driver: false,
            aircraft: false,
        });
        app.update();
        assert_eq!(visibility(&app), Visibility::Hidden);
        app.world_mut().resource_mut::<Occupancy>().seat = None;
        app.world_mut().resource_mut::<UiModel>().alive = false;
        app.update();
        assert_eq!(visibility(&app), Visibility::Hidden);
        app.world_mut().resource_mut::<UiModel>().alive = true;
        app.world_mut()
            .resource_mut::<Time>()
            .advance_by(Duration::from_secs_f32(0.02));
        app.world_mut().resource_mut::<ViewmodelStatus>().aiming = true;
        app.update();
        assert_eq!(visibility(&app), Visibility::Inherited);
        assert!((app.world().resource::<Spread>().value - 0.844).abs() < 1e-6);
        app.world_mut().resource_mut::<UiState>().screen = Screen::MainMenu;
        app.update();
        assert_eq!(visibility(&app), Visibility::Hidden);
    }
}
