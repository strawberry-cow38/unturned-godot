//! `VendingMachine` on `Vendor_0` (red, cola 80) and `Vendor_1` (blue, soda
//! 465). The machine's state, shake and refusal flash are local presentation,
//! as in the source; the purchase goes through the listen host's server
//! (`MpLoopback.NetVendPay`/`NetVendDrop`, bridge `vendPay`/`vendDrop`). A
//! joined client has no seam and the machine says "Out of order".
use super::*;
use crate::{bridge::CoreBridge, info_billboard::{Content, FocusedInfo, View}, ui::UiModel};

/// `VendingMachine.ColaId` / `SodaId` / `Price`.
pub(crate) const COLA: u16 = 80;
pub(crate) const SODA: u16 = 465;
pub(crate) const PRICE: u32 = 1;
/// `Currency.StackId`: the loonie; a stack's amount is dollars.
const DOLLARS: u16 = 1056;
const SHAKE_SECONDS: f32 = 0.45;
/// `DenySeconds`: a refused press flashes the rim.
const DENY_SECONDS: f32 = 0.6;

pub(crate) const READY: Color = Color::srgb(0.35, 0.92, 0.42);
pub(crate) const NO_FUNDS: Color = Color::srgb(0.95, 0.25, 0.2);
pub(crate) const NO_POWER: Color = Color::srgb(0.5, 0.5, 0.52);
pub(crate) const DISPENSING: Color = Color::srgb(1., 0.74, 0.16);

pub(crate) fn is_vendor_prop(name: &str) -> bool {
    name == "Vendor_0" || name == "Vendor_1"
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum State {
    Ready,
    NoFunds,
    NoPower,
    Dispensing,
    OutOfOrder,
}
/// `VendingMachine.StateFor`: mid-vend first, then power (a dollar would not
/// fix it), then the seam, then the wallet.
pub(crate) fn state_for(cooldown: f32, mains: bool, can_vend: bool, dollars: u32) -> State {
    if cooldown > 0. {
        State::Dispensing
    } else if !mains {
        State::NoPower
    } else if !can_vend {
        State::OutOfOrder
    } else if dollars < PRICE {
        State::NoFunds
    } else {
        State::Ready
    }
}
pub(crate) fn color_of(state: State) -> Color {
    match state {
        State::Ready => READY,
        State::NoFunds => NO_FUNDS,
        State::NoPower | State::OutOfOrder => NO_POWER,
        State::Dispensing => DISPENSING,
    }
}

#[derive(Component, Debug)]
pub(crate) struct Vendor {
    pub(crate) drink: u16,
    body_min: Vec3,
    body_max: Vec3,
    pub(crate) rest: Transform,
    pub(crate) shake: f32,
    pub(crate) cooldown: f32,
    pub(crate) pending: u16,
    pub(crate) denied: f32,
    pub(crate) state: State,
}
impl Vendor {
    #[cfg(test)]
    pub(crate) fn for_test(drink: u16, body_min: Vec3, body_max: Vec3, rest: Transform) -> Self {
        Self {
            drink,
            body_min,
            body_max,
            rest,
            shake: 0.,
            cooldown: 0.,
            pending: 0,
            denied: 0.,
            state: State::Ready,
        }
    }
    fn drink_name(&self) -> &'static str {
        if self.drink == SODA { "soda" } else { "cola" }
    }
    fn machine_name(&self) -> &'static str {
        if self.drink == SODA { "Soda Machine" } else { "Cola Machine" }
    }
    /// The livery lifted to a readable text colour.
    fn machine_color(&self) -> Color {
        if self.drink == SODA {
            Color::srgb(0.45, 0.65, 1.)
        } else {
            Color::srgb(1., 0.42, 0.38)
        }
    }
    /// `PromptFor`: the line under the machine's name.
    pub(crate) fn prompt(&self, key: &str) -> String {
        match self.state {
            State::Ready => format!("[{key}] ${PRICE} {}", self.drink_name()),
            State::NoFunds => format!("Insufficient funds -- ${PRICE} {}", self.drink_name()),
            State::NoPower => "No power".into(),
            State::OutOfOrder => "Out of order".into(),
            State::Dispensing => "Dispensing...".into(),
        }
    }
    /// `OutlineColorFor`: the refusal flash blinks red/white over the state.
    pub(crate) fn outline(&self) -> Color {
        if self.denied > 0. {
            if self.denied.rem_euclid(0.2) < 0.1 { NO_FUNDS } else { Color::WHITE }
        } else {
            color_of(self.state)
        }
    }
    fn center(&self) -> Vec3 {
        (self.body_min + self.body_max) * 0.5
    }
    /// `PromptAnchor`: just above the roof (mesh +Z), centred, then 0.3 m up.
    pub(crate) fn prompt_anchor(&self, pose: &Transform) -> Vec3 {
        let c = self.center();
        pose.transform_point(Vec3::new(c.x, c.y, self.body_max.z)) + Vec3::Y * 0.3
    }
    /// `DispensePoint`: the tray, centred across the +Y front, clear of it by
    /// 0.35 m, 0.30 m up the mesh's Z.
    pub(crate) fn dispense_point(&self) -> Vec3 {
        let size = self.body_max - self.body_min;
        self.rest.transform_point(Vec3::new(
            self.body_min.x + size.x * 0.5,
            self.body_min.y + size.y + 0.35,
            self.body_min.z + 0.30,
        ))
    }
}

/// `UpdateLookFocus`/`HasFeed`/`StateFor` inputs.
fn dollars(model: &UiModel) -> u32 {
    model
        .pages
        .iter()
        .filter(|p| p.index < 7)
        .flat_map(|p| p.items.iter())
        .filter(|i| i.id == DOLLARS)
        .map(|i| u32::from(i.amount))
        .sum()
}

pub(crate) fn attach(
    mut commands: Commands,
    config: Res<crate::world::WorldConfig>,
    mut cache: ResMut<crate::content::ContentCache>,
    mut meshes: ResMut<Assets<Mesh>>,
    objects: Query<(Entity, &WorldObject, &Transform), (Added<WorldObject>, With<PlacementEuler>)>,
) {
    for (entity, object, xf) in &objects {
        if !is_vendor_prop(&object.asset_name) {
            continue;
        }
        let path = config.content_root.join(format!("objects/{}.obj", object.asset_name));
        let Ok((_, data)) = cache.load_mesh(&path, &mut meshes) else {
            continue;
        };
        commands.entity(entity).insert((
            Device(Target::Vendor(object.source_index)),
            DeviceTint(Color::WHITE),
            Vendor {
                drink: if object.asset_name == "Vendor_1" { SODA } else { COLA },
                body_min: data.min,
                body_max: data.max,
                rest: *xf,
                shake: 0.,
                cooldown: 0.,
                pending: 0,
                denied: 0.,
                state: State::Ready,
            },
        ));
    }
}

/// The machine's state and rim colour each frame: the state moves under the
/// player (the dollar spent, the can dropping, the power cut).
pub(crate) fn tint(
    mains: Res<Mains>,
    model: Res<UiModel>,
    mut vendors: Query<(&mut Vendor, &mut DeviceTint)>,
) {
    let funds = dollars(&model);
    for (mut vendor, mut tint) in &mut vendors {
        vendor.state = state_for(vendor.cooldown, mains.live, model.network.owns_world, funds);
        let color = vendor.outline();
        if tint.0 != color {
            tint.0 = color;
        }
    }
}

/// F on a focused machine: a refusal flashes the rim (not while dispensing);
/// otherwise the host takes the dollar, then the machine shakes and drops.
#[allow(clippy::too_many_arguments)]
pub(crate) fn press(
    ui: Res<UiState>,
    settings: Res<Settings>,
    keys: Res<ButtonInput<KeyCode>>,
    mouse: Res<ButtonInput<MouseButton>>,
    focus: Res<InteractionFocus>,
    bridge: Option<Res<CoreBridge>>,
    mut model: ResMut<UiModel>,
    mut vendors: Query<(&Device, &mut Vendor)>,
    mut commands: Commands,
    config: Res<crate::world::WorldConfig>,
    assets: Res<AssetServer>,
    audio: Option<Res<Assets<AudioSource>>>,
) {
    for (device, mut vendor) in &mut vendors {
        if !pressed_target(&ui, &settings, &keys, &mouse, &focus, device.0) {
            continue;
        }
        if vendor.state != State::Ready {
            if vendor.state != State::Dispensing {
                vendor.denied = DENY_SECONDS;
            }
            info!("[vending] refused: {:?}", vendor.state);
            continue;
        }
        let paid = bridge.as_ref().is_some_and(|bridge| {
            bridge
                .request("action", json!({"name":"vendPay","args":{}}))
                .map(|r| r["result"]["queued"] == true)
                .unwrap_or_else(|error| {
                    model.notice(error.to_string());
                    false
                })
        });
        if !paid {
            vendor.denied = DENY_SECONDS;
            continue;
        }
        vendor.pending = vendor.drink;
        vendor.shake = SHAKE_SECONDS;
        vendor.cooldown = SHAKE_SECONDS + 0.55;
        vendor.state = State::Dispensing;
        // The clunk: inv_heavy_01.wav, 0.9 linear, unit 3, max 20 m.
        let path = "sounds/inv_heavy_01.wav";
        if audio.is_some() && config.content_root.join(path).is_file() {
            commands.spawn((
                Name::new("Vending clunk"),
                AudioPlayer::new(assets.load(path)),
                PlaybackSettings::DESPAWN
                    .with_spatial(true)
                    .with_spatial_scale(bevy::audio::SpatialScale::new(0.5 / 20.))
                    .with_volume(bevy::audio::Volume::Linear(0.9)),
                Transform::from_translation(vendor.rest.transform_point(vendor.center())),
            ));
        }
    }
}

/// `HubTick`: timers, the decaying shudder off the rest pose, and the can on
/// the tick the shake reaches zero.
pub(crate) fn shake(
    time: Res<Time>,
    ui: Option<Res<UiState>>,
    bridge: Option<Res<CoreBridge>>,
    mut model: ResMut<UiModel>,
    mut vendors: Query<(&mut Vendor, &mut Transform)>,
) {
    let dt = if ui.is_some_and(|ui| crate::player::simulation_paused(&ui)) {
        0.
    } else {
        time.delta_secs()
    };
    for (mut vendor, mut pose) in &mut vendors {
        let vendor = &mut *vendor;
        vendor.cooldown = (vendor.cooldown - dt).max(0.);
        vendor.denied = (vendor.denied - dt).max(0.);
        if vendor.shake <= 0. {
            continue;
        }
        vendor.shake = (vendor.shake - dt).max(0.);
        let amp = 0.018 * (vendor.shake / SHAKE_SECONDS);
        let ph = vendor.shake * 46.;
        let off = Vec3::new(ph.sin() * amp, 0., (ph * 0.7).cos() * amp * 0.5);
        pose.translation = vendor.rest.translation + off;
        if vendor.shake <= 0. {
            pose.translation = vendor.rest.translation;
            let drink = std::mem::take(&mut vendor.pending);
            if drink != 0
                && let Some(bridge) = &bridge
                && let Err(error) = bridge.request(
                    "action",
                    json!({"name":"vendDrop","args":{"drinkId":drink,"position":vendor.dispense_point().to_array()}}),
                )
            {
                model.notice(error.to_string());
            }
        }
    }
}

/// `VendPromptSet`: the machine's own look-at billboard while focused.
pub(crate) fn prompt(
    focus: Res<InteractionFocus>,
    settings: Res<Settings>,
    mut info: Option<ResMut<FocusedInfo>>,
    vendors: Query<(&Device, &Vendor, &Transform)>,
) {
    let Some(info) = info.as_mut() else { return };
    let key = settings.bindings[&GameAction::Interact].label();
    for (device, vendor, pose) in &vendors {
        let (action, args) = device.0.action();
        if !focus.selected_args(action, &args) {
            continue;
        }
        info.0 = Some(View {
            position: vendor.prompt_anchor(pose),
            content: Content {
                name: vendor.machine_name().into(),
                name_color: vendor.machine_color(),
                prompt: vendor.prompt(&key),
                prompt_color: if vendor.denied > 0. { NO_FUNDS } else { color_of(vendor.state) },
                bars: [None; 5],
            },
        });
    }
}
