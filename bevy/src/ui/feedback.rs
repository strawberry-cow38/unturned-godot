//! Player feedback the source shows for accepted facts and held tools:
//! HUD.Alert's centre-screen line (a refused vehicle exit), ClientWorldSession's
//! toast (a legal pickup the server bag had no room for) and the held fluid
//! container line (PlayerController.UpdateFluidContainerHud). None of these
//! changes local state. XpAwarded(10) has no source consumer and shows nothing.
use super::*;
use crate::character::nameplate::raster::{OriginalFont, Raster};
use crate::player::GameSnapshot;
use bevy::{image::ImageSampler, text::TextPipeline};

/// VehicleExit.ExitRefusedText, shown by HUD.Alert(…, 2f).
pub const EXIT_REFUSED: &str = "Your door is blocked";
/// ClientWorldSession ItemPickupDenied toast.
pub const NO_ROOM: &str = "No room in inventory";
/// PlayerController.FluidToast's hold.
pub const FLUID_TOAST_SECONDS: f32 = 2.2;
const ALERT_COLOR: Color = Color::srgb(0.98, 0.42, 0.35);
const TOAST_COLOR: Color = Color::srgb(1., 0.85, 0.4);

#[derive(Clone, Debug, Default, PartialEq)]
pub struct Timed {
    pub text: String,
    pub left: f32,
    /// Bumped per raise so a repeated identical alert restarts its hold.
    pub serial: u64,
}
impl Timed {
    fn raise(&mut self, text: &str, seconds: f32) {
        self.text = text.into();
        self.left = seconds;
        self.serial = self.serial.wrapping_add(1);
    }
    fn step(&mut self, dt: f32) {
        if self.left > 0. {
            self.left -= dt;
            if self.left <= 0. {
                self.text.clear();
            }
        }
    }
}
#[derive(Clone, Debug, Default, PartialEq)]
pub struct Feedback {
    pub alert: Timed,
    pub toast: Timed,
    pub fluid: Timed,
}
impl Feedback {
    pub fn adopt(&mut self, event: &Value) {
        match event["kind"].as_str() {
            Some("vehicleExitRefused") => self.alert.raise(EXIT_REFUSED, 2.),
            Some("pickupDenied") => self.toast.raise(NO_ROOM, 2.5),
            _ => {}
        }
    }
    /// FluidToast: an action's line briefly overrides the held-container label.
    pub fn fluid_toast(&mut self, text: &str) {
        self.fluid.raise(text, FLUID_TOAST_SECONDS);
    }
    /// HUD._Process: the alert holds, then fades over its last second and clears;
    /// the session toast and fluid toast simply clear when their timers run out.
    pub fn step(&mut self, dt: f32) {
        self.alert.step(dt);
        self.toast.step(dt);
        self.fluid.step(dt);
    }
    pub fn alert_alpha(&self) -> f32 {
        if self.alert.text.is_empty() {
            0.
        } else {
            self.alert.left.clamp(0., 1.)
        }
    }
}

/// UpdateFluidContainerHud: the toast while it lasts, otherwise the held
/// container's FluidItem.Label plus the source hint for what RMB/LMB would do.
/// Placed fluid-tank focus and the water mains switch have no accepted native
/// source yet, so the tank hints and "the water's off" never appear here.
fn holds_fluid(snapshot: &Value, catalog: &crate::entities::ItemCatalog) -> bool {
    crate::use_items::Cell::held(snapshot)
        .and_then(|cell| catalog.items.get(&cell.id))
        .is_some_and(|item| item.hand_kind == "fluid")
}

pub(crate) fn fluid_line(snapshot: &Value, catalog: &crate::entities::ItemCatalog, feedback: &Feedback) -> Option<String> {
    let cell = crate::use_items::Cell::held(snapshot)?;
    if !holds_fluid(snapshot, catalog) {
        return None;
    }
    if feedback.fluid.left > 0. && !feedback.fluid.text.is_empty() {
        return Some(feedback.fluid.text.clone());
    }
    let label = snapshot["inventory"]["pages"]
        .as_array()?
        .iter()
        .find(|p| p["index"].as_u64() == Some(cell.page as u64))?["items"]
        .as_array()?
        .iter()
        .find(|i| i["x"].as_u64() == Some(cell.x as u64) && i["y"].as_u64() == Some(cell.y as u64))?["fluid"]["label"]
        .as_str()?
        .to_owned();
    Some(match snapshot["player"]["aimedTap"].as_str() {
        Some(tap) => format!("{label}     [RMB] fill at the {}", tap.to_lowercase()),
        None => format!("{label}     [LMB] drink"),
    })
}

#[derive(Component)]
struct AlertBand;
#[derive(Component)]
struct FluidBand;
/// An outlined label baked with the original font; re-baked only when its text changes.
#[derive(Component)]
struct Outlined {
    text: String,
}
#[derive(Component)]
struct ToastLabel;

pub(crate) struct FeedbackPlugin;
impl Plugin for FeedbackPlugin {
    fn build(&self, app: &mut App) {
        if !app.world().contains_resource::<Assets<Font>>() {
            app.init_asset::<Font>();
        }
        app.init_resource::<OriginalFont>()
            .add_systems(Startup, spawn)
            .add_systems(Update, step.in_set(UiUpdate))
            .add_systems(
                Update,
                draw.after(step)
                    .in_set(UiUpdate)
                    .run_if(resource_exists::<TextPipeline>),
            );
    }
}

fn spawn(mut commands: Commands, font: Res<OriginalFont>) {
    // HUD._alert: anchors 0..1 x 0.5..0.5, offsets -120..-84, centred text.
    commands.spawn((
        Name::new("Original HUD alert"),
        AlertBand,
        Node {
            position_type: PositionType::Absolute,
            left: px(0),
            right: px(0),
            top: percent(50),
            margin: UiRect::top(px(-120)),
            height: px(36),
            ..default()
        },
        Pickable::IGNORE,
    ));
    // _fluidContainerLabel: CenterTop, offsets -360..360 x 185, 24px, outline6 @0.9.
    commands.spawn((
        Name::new("Original held fluid line"),
        FluidBand,
        Node {
            position_type: PositionType::Absolute,
            left: percent(50),
            margin: UiRect::left(px(-360)),
            width: px(720),
            top: px(185),
            height: px(34),
            ..default()
        },
        GlobalZIndex(40),
        Pickable::IGNORE,
    ));
    // ClientWorldSession._toast: Position (24,86), 20px, modulate (1,.85,.4).
    commands.spawn((
        Name::new("Original session toast"),
        ToastLabel,
        Text::new(""),
        TextFont {
            font: font.0.clone().into(),
            font_size: FontSize::Px(20.),
            ..default()
        },
        TextColor(TOAST_COLOR),
        Node {
            position_type: PositionType::Absolute,
            left: px(24),
            top: px(86),
            ..default()
        },
        Pickable::IGNORE,
    ));
}

fn step(
    time: Res<Time>,
    state: Res<UiState>,
    mut model: ResMut<UiModel>,
    held: (Option<Res<GameSnapshot>>, Option<Res<crate::entities::ItemCatalog>>),
    mut toast: Query<(&mut Text, &mut Visibility), With<ToastLabel>>,
) {
    model.feedback.step(time.delta_secs());
    // UpdateFluidContainerHud zeroes the toast whenever no container is held.
    if let (Some(snapshot), Some(catalog)) = held
        && !holds_fluid(&snapshot.0, &catalog)
    {
        model.feedback.fluid = default();
    }
    for (mut text, mut visibility) in &mut toast {
        if text.0 != model.feedback.toast.text {
            text.0 = model.feedback.toast.text.clone();
        }
        *visibility = if state.screen == Screen::MainMenu || model.feedback.toast.text.is_empty() {
            Visibility::Hidden
        } else {
            Visibility::Inherited
        };
    }
}

#[allow(clippy::too_many_arguments)]
fn draw(
    mut commands: Commands,
    model: Res<UiModel>,
    state: Res<UiState>,
    snapshot: Res<GameSnapshot>,
    catalog: Res<crate::entities::ItemCatalog>,
    mut raster: Raster,
    alert: Query<(Entity, Option<&Children>), (With<AlertBand>, Without<FluidBand>)>,
    fluid: Query<(Entity, Option<&Children>), (With<FluidBand>, Without<AlertBand>)>,
    mut labels: Query<(&mut Outlined, &mut Node, &mut ImageNode, &mut Visibility)>,
) {
    let playing = model.connected && state.screen != Screen::MainMenu;
    let alpha = model.feedback.alert_alpha();
    let alert_text = (alpha > 0.).then(|| model.feedback.alert.text.clone());
    for (band, children) in &alert {
        // 26px, black outline of size6; Godot Label is top-aligned.
        bake(&mut commands, &mut raster, &mut labels, band, children, alert_text.as_deref(), 26., 3., 1., ALERT_COLOR.with_alpha(alpha), false);
    }
    let line = playing
        .then(|| fluid_line(&snapshot.0, &catalog, &model.feedback))
        .flatten();
    for (band, children) in &fluid {
        bake(&mut commands, &mut raster, &mut labels, band, children, line.as_deref(), 24., 3., 0.9, Color::WHITE, false);
    }
}

#[allow(clippy::too_many_arguments)]
fn bake(
    commands: &mut Commands,
    raster: &mut Raster,
    labels: &mut Query<(&mut Outlined, &mut Node, &mut ImageNode, &mut Visibility)>,
    band: Entity,
    children: Option<&Children>,
    text: Option<&str>,
    size: f32,
    outline: f32,
    outline_alpha: f32,
    color: Color,
    centre_vertically: bool,
) {
    let existing = children
        .into_iter()
        .flatten()
        .copied()
        .find(|child| labels.contains(*child));
    let Some(text) = text.filter(|t| !t.is_empty()) else {
        if let Some(child) = existing
            && let Ok((_, _, _, mut visibility)) = labels.get_mut(child)
        {
            *visibility = Visibility::Hidden;
        }
        return;
    };
    if let Some(child) = existing
        && let Ok((label, _, mut image, mut visibility)) = labels.get_mut(child)
        && label.text == text
    {
        image.color = color;
        *visibility = Visibility::Inherited;
        return;
    }
    let Some(mut bitmap) = raster.render_fitted(text, size, f32::MAX, outline, outline_alpha) else {
        return;
    };
    bitmap.image.sampler = ImageSampler::linear();
    let image_size = Vec2::new(bitmap.image.width() as f32, bitmap.image.height() as f32);
    // Centre the layout box horizontally (Godot HorizontalAlignment.Center).
    let node = Node {
        position_type: PositionType::Absolute,
        left: percent(50),
        top: if centre_vertically { percent(50) } else { px(0) },
        margin: UiRect {
            left: px(-bitmap.layout.x * 0.5 + bitmap.origin.x),
            top: px(bitmap.origin.y - if centre_vertically { bitmap.layout.y * 0.5 } else { 0. }),
            ..default()
        },
        width: px(image_size.x),
        height: px(image_size.y),
        ..default()
    };
    let image = raster.images.add(bitmap.image);
    if let Some(child) = existing
        && let Ok((mut label, mut old, mut handle, mut visibility)) = labels.get_mut(child)
    {
        label.text = text.into();
        *old = node;
        handle.image = image;
        handle.color = color;
        *visibility = Visibility::Inherited;
    } else {
        commands.entity(band).with_child((
            Name::new(format!("Outlined label: {text}")),
            Outlined { text: text.into() },
            node,
            ImageNode::new(image).with_color(color),
            Visibility::Inherited,
            Pickable::IGNORE,
        ));
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;
    #[test]
    fn accepted_refusals_raise_source_text_and_timers_then_clear() {
        let mut feedback = Feedback::default();
        feedback.adopt(&json!({"kind":"vehicleExitRefused","data":{"netId":3,"seat":0}}));
        feedback.adopt(&json!({"kind":"pickupDenied","data":{"netId":9}}));
        feedback.adopt(&json!({"kind":"xpAwarded","data":{"amount":5}}));
        assert_eq!(feedback.alert.text, "Your door is blocked");
        assert_eq!(feedback.alert.left, 2.);
        assert_eq!(feedback.toast.text, "No room in inventory");
        assert_eq!(feedback.toast.left, 2.5);
        // Holds at full opacity, then fades over the final second.
        feedback.step(0.5);
        assert_eq!(feedback.alert_alpha(), 1.);
        feedback.step(1.0);
        assert!((feedback.alert_alpha() - 0.5).abs() < 1e-5);
        feedback.step(0.6);
        assert_eq!(feedback.alert_alpha(), 0.);
        assert!(feedback.alert.text.is_empty());
        assert_eq!(feedback.toast.text, "No room in inventory", "the toast holds 2.5s");
        feedback.step(0.5);
        assert!(feedback.toast.text.is_empty());
    }
    #[test]
    fn model_adopts_each_event_once_by_sequence() {
        let mut model = UiModel::default();
        let snapshot = json!({"events":[{"seq":4,"kind":"pickupDenied","data":{"netId":2}}]});
        model.adopt_snapshot(&snapshot);
        model.feedback.step(2.6);
        assert!(model.feedback.toast.text.is_empty());
        // The same retained event in a later snapshot does not re-raise it.
        model.adopt_snapshot(&snapshot);
        assert!(model.feedback.toast.text.is_empty());
        model.adopt_snapshot(&json!({"events":[{"seq":5,"kind":"pickupDenied","data":{"netId":2}}]}));
        assert_eq!(model.feedback.toast.text, NO_ROOM);
    }
    #[test]
    fn held_fluid_line_shows_label_tap_hint_and_toast_override() {
        let mut catalog = crate::entities::ItemCatalog::default();
        let bottle: crate::entities::ItemVisual = serde_json::from_value(json!({
            "id":14,"name":"Bottled Water","type":"water","isConsumable":true,"isFluidContainer":true,"handKind":"fluid"})).unwrap();
        catalog.items.insert(14, bottle);
        let mut snapshot = json!({"weapon":{"held":{"id":14,"page":2,"x":1,"y":0}},
            "inventory":{"pages":[{"index":2,"items":[{"id":14,"x":1,"y":0,"fluid":{"label":"Bottled Water -- 1.0 L Clean Water"}}]}]},
            "player":{"aimedTap":null}});
        let mut feedback = Feedback::default();
        assert_eq!(fluid_line(&snapshot, &catalog, &feedback).as_deref(),
            Some("Bottled Water -- 1.0 L Clean Water     [LMB] drink"));
        snapshot["player"]["aimedTap"] = json!("Bathtub");
        assert_eq!(fluid_line(&snapshot, &catalog, &feedback).as_deref(),
            Some("Bottled Water -- 1.0 L Clean Water     [RMB] fill at the bathtub"));
        feedback.fluid_toast("drank 1.0 L  (+40% water)");
        assert_eq!(fluid_line(&snapshot, &catalog, &feedback).as_deref(), Some("drank 1.0 L  (+40% water)"));
        feedback.step(FLUID_TOAST_SECONDS + 0.01);
        assert!(fluid_line(&snapshot, &catalog, &feedback).unwrap().ends_with("[RMB] fill at the bathtub"));
        snapshot["weapon"]["held"] = Value::Null;
        assert_eq!(fluid_line(&snapshot, &catalog, &feedback), None, "nothing in hand, no line");
    }
}
