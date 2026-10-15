//! Original HUD.AddVital geometry and palette; values come from client replicas.
use super::*;

const ICON: f32 = 24.;
const ROW: f32 = 36.;
const TOP: f32 = 6.;
// The current source reserves seven rows. Body temperature is the seventh;
// until its actual simulation is exposed, leave that row empty rather than
// invent a comfortable reading or move the six established rows.
const PANEL_HEIGHT: f32 = TOP + 7. * ROW + 18. - 24.;
const ROWS: [(&str, Color); 6] = [
    (
        "hud_health.png",
        Color::srgb(0.7490196, 0.12156863, 0.12156863),
    ),
    ("hud_food.png", Color::srgb(57. / 85., 0.5019608, 5. / 51.)),
    (
        "hud_water.png",
        Color::srgb(10. / 51., 0.59607846, 40. / 51.),
    ),
    (
        "hud_stamina.png",
        Color::srgb(44. / 51., 0.7058824, 0.07450981),
    ),
    ("hud_virus.png", Color::srgb(0.24, 0.71, 0.29)),
    ("hud_oxygen.png", Color::srgb(0.95, 0.97, 1.)),
];
#[derive(Component)]
struct Panel;
#[derive(Component)]
struct Fill {
    index: usize,
    shown: f32,
}
#[derive(Component)]
pub(super) struct MenuPanel;
#[derive(Component)]
struct Icon(usize);

pub(super) struct VitalsPlugin;
impl Plugin for VitalsPlugin {
    fn build(&self, app: &mut App) {
        app.add_systems(Startup, spawn)
            .add_systems(Update, update.in_set(UiUpdate))
            .add_systems(
                PostUpdate,
                avoid_menu_overlap.before(bevy::ui::UiSystems::Layout),
            );
    }
}
fn fractions(model: &UiModel) -> [f32; 6] {
    // Current original unified player explicitly adopts MaxHealth100.
    [
        model.health / 100.,
        model.food,
        model.water,
        model.stamina,
        1. - model.infection,
        model.oxygen,
    ]
    .map(|v| v.clamp(0., 1.))
}
fn spawn(mut commands: Commands, assets: Res<AssetServer>) {
    commands
        .spawn((
            Name::new("Original player vitals"),
            Panel,
            Node {
                position_type: PositionType::Absolute,
                left: px(24),
                bottom: px(24),
                width: percent(23),
                height: px(PANEL_HEIGHT),
                ..default()
            },
            // Source vitals CanvasLayer12 sits above inventory CanvasLayer11.
            // Native menu root is20; ignore picking so the bars never intercept it.
            GlobalZIndex(21),
            Pickable::IGNORE,
        ))
        .with_children(|panel| {
            for (i, (icon, color)) in ROWS.into_iter().enumerate() {
                let y = TOP + i as f32 * ROW;
                panel.spawn((
                    Name::new(icon),
                    Icon(i),
                    ImageNode::new(assets.load(icon)),
                    Node {
                        position_type: PositionType::Absolute,
                        left: px(6),
                        top: px(y),
                        width: px(ICON),
                        height: px(ICON),
                        ..default()
                    },
                    Pickable::IGNORE,
                ));
                panel
                    .spawn((
                        Name::new(format!("Vital trough {i}")),
                        Node {
                            position_type: PositionType::Absolute,
                            left: px(36),
                            right: px(10),
                            top: px(y + 5.),
                            height: px(12),
                            ..default()
                        },
                        BackgroundColor(color.with_alpha(0.5)),
                        Pickable::IGNORE,
                    ))
                    .with_children(|bar| {
                        bar.spawn((
                            Name::new(format!("Vital fill {i}")),
                            Fill {
                                index: i,
                                shown: -1.,
                            },
                            Node {
                                width: percent(100),
                                height: percent(100),
                                ..default()
                            },
                            BackgroundColor(color),
                            Pickable::IGNORE,
                        ));
                    });
            }
        });
}
fn update(
    model: Res<UiModel>,
    state: Res<UiState>,
    time: Res<Time>,
    mut panels: Query<&mut Visibility, With<Panel>>,
    mut fills: Query<(&mut Fill, &mut Node)>,
) {
    let visible = model.connected
        && state.screen != Screen::MainMenu
        && !(state.screen == Screen::Settings && state.settings_return == Screen::MainMenu);
    for mut panel in &mut panels {
        *panel = if visible {
            Visibility::Inherited
        } else {
            Visibility::Hidden
        };
    }
    let values = fractions(&model);
    for (mut fill, mut node) in &mut fills {
        fill.shown = smooth(fill.shown, values[fill.index], time.delta_secs());
        node.width = percent(fill.shown * 100.);
    }
}

// HUD.VitalTweenRate/VitalSnapEps. First observation is immediate; later
// stepped network samples ease exponentially, independently of frame rate.
fn smooth(shown: f32, target: f32, dt: f32) -> f32 {
    if shown < 0. || (target - shown).abs() < 0.002 {
        target
    } else {
        shown + (target - shown) * (1. - (-6. * dt).exp())
    }
}
// HUD.ContentBottom: reserve the band only for columns intersecting the
// actual vitals rectangle, including its 12px clearance.
pub(super) fn content_bottom(viewport: Vec2, x0: f32, x1: f32, bottom: f32) -> f32 {
    if x1 <= 24. || x0 >= 24. + 0.23 * viewport.x {
        bottom
    } else {
        bottom.min(viewport.y - PANEL_HEIGHT - 24. - 12.)
    }
}
fn avoid_menu_overlap(
    model: Res<UiModel>,
    windows: Query<&Window, With<bevy::window::PrimaryWindow>>,
    mut panels: Query<(&ComputedNode, &mut Node), With<MenuPanel>>,
) {
    let Ok(window) = windows.single() else { return };
    let viewport = Vec2::new(window.width(), window.height());
    for (computed, mut node) in &mut panels {
        let size = computed.size() * computed.inverse_scale_factor();
        if size.x <= 0. || size.y <= 0. {
            continue;
        }
        let left = (viewport.x - size.x) * 0.5;
        let desired_bottom = (viewport.y + size.y) * 0.5;
        let bottom = if model.connected {
            content_bottom(viewport, left, left + size.x, desired_bottom)
        } else {
            desired_bottom
        };
        node.position_type = PositionType::Absolute;
        node.left = px(left);
        node.top = px((bottom - size.y).max(12.));
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use bevy::asset::AssetPlugin;
    #[test]
    fn real_replica_values_drive_six_source_rows_without_oxygen_infection_overlap() {
        let mut model = UiModel::default();
        model.adopt_snapshot(&json!({"connected":true,"player":{"hp":70,"food":0.8,"water":0.6,"stamina":0.4,"infection":0.25,"oxygen":0.2}}));
        let mut app = App::new();
        app.add_plugins((MinimalPlugins, AssetPlugin::default()))
            .init_asset::<Image>()
            .insert_resource(model)
            .insert_resource(UiState {
                screen: Screen::Playing,
                ..default()
            })
            .add_plugins(VitalsPlugin);
        app.update();
        let mut fills: Vec<_> = app
            .world_mut()
            .query::<(&Fill, &Node)>()
            .iter(app.world())
            .map(|(f, n)| (f.index, n.width))
            .collect();
        fills.sort_by_key(|f| f.0);
        assert_eq!(fills.len(), 6);
        for ((index, width), expected) in fills.iter().zip([70., 80., 60., 40., 75., 20.]) {
            let Val::Percent(actual) = width else {
                panic!("row {index} did not receive a fractional fill")
            };
            assert!((actual - expected).abs() < 0.0001, "row {index}");
        }
        let mut icons: Vec<_> = app
            .world_mut()
            .query::<(&Icon, &Node)>()
            .iter(app.world())
            .map(|(i, n)| (i.0, n.top, n.height))
            .collect();
        icons.sort_by_key(|i| i.0);
        assert_eq!(icons[4], (4, px(150), px(24)));
        assert_eq!(icons[5], (5, px(186), px(24)));
        for (icon, _) in ROWS {
            let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
                .join("../game/content")
                .join(icon);
            assert_eq!(&std::fs::read(path).unwrap()[..8], b"\x89PNG\r\n\x1a\n");
        }
        app.world_mut().resource_mut::<UiState>().screen = Screen::Inventory;
        app.update();
        assert_eq!(
            *app.world_mut()
                .query_filtered::<&Visibility, With<Panel>>()
                .single(app.world())
                .unwrap(),
            Visibility::Inherited
        );
        app.world_mut().resource_mut::<UiState>().screen = Screen::MainMenu;
        app.update();
        assert_eq!(
            *app.world_mut()
                .query_filtered::<&Visibility, With<Panel>>()
                .single(app.world())
                .unwrap(),
            Visibility::Hidden
        );
    }
    #[test]
    fn fills_seed_then_ease_at_original_rate_and_snap_to_exact_endpoint() {
        assert_eq!(smooth(-1., 0.2, 0.02), 0.2);
        let once = smooth(1., 0.2, 0.1);
        let twice = smooth(smooth(1., 0.2, 0.05), 0.2, 0.05);
        assert!((once - (0.2 + 0.8 * (-0.6_f32).exp())).abs() < 1e-6);
        assert!((once - twice).abs() < 1e-6);
        assert_eq!(smooth(0.001, 0., 0.), 0.);
    }
    #[test]
    fn actual_menu_placement_uses_source_column_clearance_and_preserves_other_columns() {
        let vp = Vec2::new(1280., 720.);
        assert_eq!(content_bottom(vp, 400., 1100., 680.), 680.);
        assert_eq!(content_bottom(vp, 0., 24., 680.), 680.);
        assert_eq!(content_bottom(vp, 210., 1070., 505.), 432.);
        let mut app = App::new();
        let mut model = UiModel::default();
        model.connected = true;
        app.insert_resource(model)
            .add_systems(Update, avoid_menu_overlap);
        app.world_mut().spawn((
            Window {
                resolution: (1280, 720).into(),
                ..default()
            },
            bevy::window::PrimaryWindow,
        ));
        let panel = app
            .world_mut()
            .spawn((
                MenuPanel,
                Node::default(),
                ComputedNode {
                    size: Vec2::new(860., 290.),
                    ..default()
                },
            ))
            .id();
        app.update();
        let node = app.world().get::<Node>(panel).unwrap();
        assert_eq!(node.left, px(210.));
        assert_eq!(node.top, px(142.));
        app.world_mut().resource_mut::<UiModel>().connected = false;
        app.update();
        assert_eq!(app.world().get::<Node>(panel).unwrap().top, px(215.));
    }
    #[test]
    fn infection_depletes_healthy_bar_oxygen_stays_white_and_values_clamp() {
        let mut model = UiModel::default();
        model.health = 120.;
        model.infection = 1.;
        model.oxygen = -0.2;
        assert_eq!(fractions(&model)[0], 1.);
        assert_eq!(fractions(&model)[4], 0.);
        assert_eq!(fractions(&model)[5], 0.);
        assert_eq!(ROWS[5].1.to_srgba(), Srgba::new(0.95, 0.97, 1., 1.));
        assert_eq!(PANEL_HEIGHT, 252.);
    }
}
