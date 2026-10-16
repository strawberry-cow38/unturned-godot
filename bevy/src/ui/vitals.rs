//! Original HUD.AddVital geometry and palette; values come from client replicas.
//! The seventh row is HUD.AddBipolarVital (owner body temperature, with the
//! Thermometer readout); the status row is HUD.AddStatus above the panel.
use super::*;
use crate::character::nameplate::raster::{OriginalFont, Raster};
use bevy::{image::ImageSampler, text::TextPipeline};

const ICON: f32 = 24.;
const ROW: f32 = 36.;
const TOP: f32 = 6.;
const BAR_X: f32 = 36.;
const BAR_H: f32 = 12.;
/// HUD.VitalsLeft / VitalRows / VitalsTopGap.
const VITALS_LEFT: f32 = 24.;
const VITAL_ROWS: f32 = 7.;
const VITALS_TOP_GAP: f32 = TOP + VITAL_ROWS * ROW + 18.;
const PANEL_HEIGHT: f32 = VITALS_TOP_GAP - 24.;
/// HUD.StatusSz / StatusStride / StatusGap: a row of 40px boxes above the panel.
const STATUS_SIZE: f32 = 40.;
const STATUS_STRIDE: f32 = 46.;
const STATUS_GAP: f32 = 8.;
/// HUD.CCold / CHot, the neutral shared trough and the centre mark.
const COLD: Color = Color::srgb(0.42, 0.80, 0.98);
const HOT: Color = Color::srgb(0.98, 0.38, 0.10);
const TROUGH: Color = Color::srgba(0.75, 0.78, 0.82, 0.35);
const MID: Color = Color::srgba(0.95, 0.97, 1., 0.55);
const TEMPERATURE_ROW: usize = 6;
/// HUD.AddStatus rows in source order. Dehydration uses the water meter's own
/// symbol and the deadzone the infection one, exactly as HUD.cs explains.
const STATUS: [(&str, Status); 5] = [
    ("hud_bleeding.png", Status::Bleeding),
    ("hud_broken.png", Status::Broken),
    ("hud_starved.png", Status::Starved),
    ("hud_water.png", Status::Dehydrated),
    ("hud_virus.png", Status::Deadzone),
];
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Status {
    Bleeding,
    Broken,
    Starved,
    Dehydrated,
    Deadzone,
}
impl Status {
    fn on(self, model: &UiModel) -> bool {
        match self {
            Status::Bleeding => model.bleeding,
            Status::Broken => model.broken,
            // Two statuses, each on its own condition (HUD.cs 2026-09-16 note).
            Status::Starved => model.food <= 0.,
            Status::Dehydrated => model.water <= 0.,
            Status::Deadzone => model.in_deadzone,
        }
    }
}
/// The owner's PlayerTemperatureSim reading as the bridge's client shell reports it.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct Temperature {
    /// PlayerTemperatureSim.Comfort: 0 across the comfort band, -1/+1 where cold/heat turn lethal.
    pub comfort: f32,
    pub body_c: f32,
    pub has_thermometer: bool,
    pub band: String,
}
impl Temperature {
    pub fn read(value: &Value) -> Self {
        Self {
            comfort: value["comfort"].as_f64().unwrap_or(0.) as f32,
            body_c: value["bodyC"].as_f64().unwrap_or(19.) as f32,
            has_thermometer: value["hasThermometer"].as_bool().unwrap_or(false),
            band: value["band"].as_str().unwrap_or("Comfortable").into(),
        }
    }
    /// HUD's thermometer text: Units.TemperatureReadout in the source default
    /// MeasurementSystem.Both, with Mathf.RoundToInt's ties-to-even rounding.
    pub fn readout(&self) -> Option<String> {
        self.has_thermometer.then(|| {
            let c = self.body_c.round_ties_even() as i32;
            let f = (self.body_c * 9. / 5. + 32.).round_ties_even() as i32;
            format!("{c} °C / {f} °F")
        })
    }
}
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
/// The bipolar temperature fill; `shown` seeds from 2 because -1 is a real value.
#[derive(Component)]
struct Bipolar {
    shown: f32,
}
#[derive(Component)]
struct StatusRow;
#[derive(Component)]
struct StatusBox(Status);
/// The trough-spanning box the thermometer readout centres in.
#[derive(Component)]
struct ReadoutBox;
#[derive(Component)]
struct Readout {
    text: String,
}

pub(crate) struct VitalsPlugin;
impl Plugin for VitalsPlugin {
    fn build(&self, app: &mut App) {
        if !app.world().contains_resource::<Assets<Font>>() {
            app.init_asset::<Font>();
        }
        app.init_resource::<OriginalFont>()
            .add_systems(Startup, spawn)
            .add_systems(Update, update.in_set(UiUpdate))
            .add_systems(
                Update,
                readout
                    .after(update)
                    .in_set(UiUpdate)
                    .run_if(resource_exists::<TextPipeline>),
            )
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
            super::backdrop::AboveDashboard,
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
            let y = TOP + TEMPERATURE_ROW as f32 * ROW;
            panel.spawn((
                Name::new("hud_temperature.png"),
                Icon(TEMPERATURE_ROW),
                ImageNode::new(assets.load("hud_temperature.png")),
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
            // The trough is neutral: it is shared by both ends.
            panel
                .spawn((
                    Name::new("Temperature trough"),
                    Node {
                        position_type: PositionType::Absolute,
                        left: px(BAR_X),
                        right: px(10),
                        top: px(y + 5.),
                        height: px(BAR_H),
                        ..default()
                    },
                    BackgroundColor(TROUGH),
                    Pickable::IGNORE,
                ))
                .with_children(|bar| {
                    // HUD adds the centre mark before the fill, so the fill draws over it.
                    bar.spawn((
                        Name::new("Temperature centre mark"),
                        Node {
                            position_type: PositionType::Absolute,
                            left: percent(50),
                            margin: UiRect::left(px(-1)),
                            width: px(2),
                            height: percent(100),
                            ..default()
                        },
                        BackgroundColor(MID),
                        Pickable::IGNORE,
                    ));
                    bar.spawn((
                        Name::new("Temperature fill"),
                        Bipolar { shown: 2. },
                        Node {
                            position_type: PositionType::Absolute,
                            left: percent(50),
                            width: percent(0),
                            height: percent(100),
                            ..default()
                        },
                        BackgroundColor(COLD),
                        Pickable::IGNORE,
                    ));
                });
            panel.spawn((
                Name::new("Thermometer readout box"),
                ReadoutBox,
                Node {
                    position_type: PositionType::Absolute,
                    left: px(BAR_X),
                    right: px(10),
                    top: px(y - 3.),
                    height: px(5. + BAR_H + 6.),
                    ..default()
                },
                Pickable::IGNORE,
            ));
        });
    // HUD.AddStatus: ordinary HUD layer10, so menus above it cover the row.
    commands
        .spawn((
            Name::new("Original status icons"),
            StatusRow,
            Node {
                position_type: PositionType::Absolute,
                left: px(0),
                bottom: px(0),
                width: percent(100),
                height: percent(100),
                ..default()
            },
            Pickable::IGNORE,
        ))
        .with_children(|row| {
            for (i, (icon, status)) in STATUS.into_iter().enumerate() {
                row.spawn((
                    Name::new(format!("Status {icon}")),
                    StatusBox(status),
                    Node {
                        position_type: PositionType::Absolute,
                        left: px(VITALS_LEFT + i as f32 * STATUS_STRIDE),
                        bottom: px(VITALS_TOP_GAP + STATUS_GAP),
                        width: px(STATUS_SIZE),
                        height: px(STATUS_SIZE),
                        ..default()
                    },
                    BackgroundColor(Color::srgba(0., 0., 0., 0.5)),
                    Visibility::Hidden,
                    Pickable::IGNORE,
                ))
                .with_children(|b| {
                    b.spawn((
                        ImageNode::new(assets.load(icon)),
                        Node {
                            width: percent(100),
                            height: percent(100),
                            ..default()
                        },
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
    mut panels: Query<&mut Visibility, (With<Panel>, Without<StatusRow>, Without<StatusBox>)>,
    mut fills: Query<(&mut Fill, &mut Node), Without<Bipolar>>,
    mut bipolar: Query<(&mut Bipolar, &mut Node, &mut BackgroundColor), Without<Fill>>,
    mut rows: Query<&mut Visibility, (With<StatusRow>, Without<Panel>, Without<StatusBox>)>,
    mut boxes: Query<(&StatusBox, &mut Visibility), (Without<Panel>, Without<StatusRow>)>,
) {
    let visible = model.connected
        && state.screen != Screen::MainMenu
        && !(state.screen == Screen::Settings && state.settings_return == Screen::MainMenu);
    let shown = if visible {
        Visibility::Inherited
    } else {
        Visibility::Hidden
    };
    for mut panel in &mut panels {
        *panel = shown;
    }
    for mut row in &mut rows {
        *row = shown;
    }
    for (status, mut visibility) in &mut boxes {
        *visibility = if status.0.on(&model) {
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
    for (mut fill, mut node, mut color) in &mut bipolar {
        fill.shown = smooth_signed(fill.shown, model.temperature.comfort, time.delta_secs());
        let v = fill.shown;
        // Half-width per side: a full -1 or +1 reaches one edge from the centre.
        node.left = percent(50. + v.min(0.) * 50.);
        node.width = percent(v.abs() * 50.);
        color.0 = if v < 0. { COLD } else { HOT };
    }
}

/// HUD.AddBipolarVital's thermometer label: 15px original font, black 0.95
/// outline of size5, centred over the trough, hidden when there is nothing to say.
fn readout(
    mut commands: Commands,
    model: Res<UiModel>,
    mut raster: Raster,
    boxes: Query<(Entity, Option<&Children>), With<ReadoutBox>>,
    mut labels: Query<(&mut Readout, &mut Node, &mut ImageNode, &mut Visibility)>,
) {
    let text = model.temperature.readout();
    for (parent, children) in &boxes {
        let existing = children
            .into_iter()
            .flatten()
            .copied()
            .find(|child| labels.contains(*child));
        let Some(text) = text.as_ref() else {
            if let Some(child) = existing
                && let Ok((_, _, _, mut visibility)) = labels.get_mut(child)
            {
                *visibility = Visibility::Hidden;
            }
            continue;
        };
        if let Some(child) = existing
            && let Ok((label, _, _, mut visibility)) = labels.get_mut(child)
            && label.text == *text
        {
            *visibility = Visibility::Inherited;
            continue;
        }
        let Some(mut bitmap) = raster.render_fitted(text, 15., f32::MAX, 2.5, 0.95) else {
            continue;
        };
        bitmap.image.sampler = ImageSampler::linear();
        let size = Vec2::new(bitmap.image.width() as f32, bitmap.image.height() as f32);
        // Centre the label's layout box (Godot Label alignment), not its ink.
        let node = Node {
            position_type: PositionType::Absolute,
            left: percent(50),
            top: percent(50),
            margin: UiRect {
                left: px(-bitmap.layout.x * 0.5 + bitmap.origin.x),
                top: px(-bitmap.layout.y * 0.5 + bitmap.origin.y),
                ..default()
            },
            width: px(size.x),
            height: px(size.y),
            ..default()
        };
        let image = raster.images.add(bitmap.image);
        if let Some(child) = existing
            && let Ok((mut label, mut old, mut handle, mut visibility)) = labels.get_mut(child)
        {
            label.text = text.clone();
            *old = node;
            handle.image = image;
            *visibility = Visibility::Inherited;
        } else {
            commands.entity(parent).with_child((
                Name::new("Thermometer readout"),
                Readout { text: text.clone() },
                node,
                ImageNode::new(image),
                Visibility::Inherited,
                Pickable::IGNORE,
            ));
        }
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
// HUD bipolar tween: seeded by any |x|>1 (the signed bar's "not yet shown").
fn smooth_signed(shown: f32, target: f32, dt: f32) -> f32 {
    let target = target.clamp(-1., 1.);
    if shown > 1. || (target - shown).abs() < 0.002 {
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
        assert_eq!(fills.len(), 6, "the bipolar temperature fill is separate");
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
        assert_eq!(icons.len(), 7, "temperature is the seventh source row");
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
    fn hud(model: UiModel) -> App {
        let mut app = App::new();
        app.add_plugins((MinimalPlugins, AssetPlugin::default()))
            .init_asset::<Image>()
            .insert_resource(model)
            .insert_resource(UiState {
                screen: Screen::Playing,
                ..default()
            })
            .add_plugins(VitalsPlugin);
        app
    }
    fn bipolar(app: &mut App) -> (Val, Val, Color) {
        let (node, color) = app
            .world_mut()
            .query_filtered::<(&Node, &BackgroundColor), With<Bipolar>>()
            .single(app.world())
            .unwrap();
        (node.left, node.width, color.0)
    }
    #[test]
    fn temperature_row_fills_from_the_centre_in_source_cold_and_hot() {
        let mut model = UiModel::default();
        model.adopt_snapshot(&json!({"connected":true,"player":{"temperature":{"comfort":-0.5,"bodyC":2.5,"band":"Cold"}}}));
        let mut app = hud(model);
        app.update();
        // First observation is immediate; -0.5 spans the left quarter only.
        assert_eq!(bipolar(&mut app), (percent(25), percent(25), COLD));
        let mut icons: Vec<_> = app
            .world_mut()
            .query::<(&Icon, &Node)>()
            .iter(app.world())
            .map(|(i, n)| (i.0, n.top))
            .collect();
        icons.sort_by_key(|i| i.0);
        assert_eq!(icons.len(), 7);
        assert_eq!(icons[6], (6, px(222)));
        app.world_mut().resource_mut::<UiModel>().temperature.comfort = 1.;
        app.update();
        let (left, width, color) = bipolar(&mut app);
        assert!(matches!(left, Val::Percent(l) if l > 25. && l <= 50.));
        assert!(matches!(width, Val::Percent(w) if w < 50.), "eases rather than jumps");
        let _ = color;
        app.insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
            std::time::Duration::from_millis(50),
        ));
        for _ in 0..60 {
            app.update();
        }
        assert_eq!(bipolar(&mut app), (percent(50), percent(50), HOT));
        assert_eq!(smooth_signed(2., -1., 0.), -1., "-1 is a real reading, not the seed");
        assert_eq!(smooth_signed(0.3, -4., 0.), smooth_signed(0.3, -1., 0.));
    }
    #[test]
    fn thermometer_readout_uses_source_both_units_and_round_to_int() {
        let mut t = Temperature {
            body_c: 21.5,
            ..default()
        };
        assert_eq!(t.readout(), None, "no thermometer, no number");
        t.has_thermometer = true;
        assert_eq!(t.readout().as_deref(), Some("22 °C / 71 °F"));
        t.body_c = 22.5;
        assert_eq!(t.readout().as_deref(), Some("22 °C / 72 °F"), "ties to even");
        t.body_c = -5.5;
        assert_eq!(t.readout().as_deref(), Some("-6 °C / 22 °F"));
        assert_eq!(Temperature::read(&json!({})).band, "Comfortable");
    }
    #[test]
    fn status_icons_follow_each_source_condition_above_the_panel() {
        let mut app = hud(UiModel::default());
        app.world_mut().resource_mut::<UiModel>().connected = true;
        app.update();
        let shown = |app: &mut App| {
            let mut rows: Vec<_> = app
                .world_mut()
                .query::<(&StatusBox, &Node, &Visibility)>()
                .iter(app.world())
                .map(|(s, n, v)| (s.0, n.left, n.bottom, n.width, *v))
                .collect();
            rows.sort_by_key(|r| match r.1 {
                Val::Px(x) => x as i32,
                _ => i32::MAX,
            });
            rows
        };
        let rows = shown(&mut app);
        assert_eq!(rows.len(), 5);
        for (i, (status, left, bottom, width, visibility)) in rows.iter().enumerate() {
            assert_eq!(*status, STATUS[i].1);
            assert_eq!(*left, px(24. + i as f32 * 46.));
            assert_eq!(*bottom, px(284));
            assert_eq!(*width, px(40));
            // Healthy defaults: food/water 0 would be starving, so set them full.
            let _ = visibility;
        }
        {
            let mut model = app.world_mut().resource_mut::<UiModel>();
            model.food = 1.;
            model.water = 1.;
        }
        app.update();
        assert!(shown(&mut app).iter().all(|r| r.4 == Visibility::Hidden));
        {
            let mut model = app.world_mut().resource_mut::<UiModel>();
            model.bleeding = true;
            model.water = 0.;
            model.in_deadzone = true;
        }
        app.update();
        let on: Vec<_> = shown(&mut app)
            .into_iter()
            .filter(|r| r.4 == Visibility::Inherited)
            .map(|r| r.0)
            .collect();
        assert_eq!(on, vec![Status::Bleeding, Status::Dehydrated, Status::Deadzone]);
        for (icon, _) in STATUS {
            let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
                .join("../game/content")
                .join(icon);
            assert_eq!(&std::fs::read(path).unwrap()[..8], b"\x89PNG\r\n\x1a\n");
        }
        app.world_mut().resource_mut::<UiState>().screen = Screen::MainMenu;
        app.update();
        assert_eq!(
            *app.world_mut()
                .query_filtered::<&Visibility, With<StatusRow>>()
                .single(app.world())
                .unwrap(),
            Visibility::Hidden
        );
    }
}
