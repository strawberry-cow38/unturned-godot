//! `VendingMachine` on `Vendor_0` (red, cola 80) and `Vendor_1` (blue, soda
//! 465). The machine's state, shake and refusal flash are local presentation,
//! as in the source; the purchase goes through the listen host's server
//! (`MpLoopback.NetVendPay`/`NetVendDrop`, bridge `vendPay`/`vendDrop`). A
//! joined client has no seam and the machine says "Out of order". The server
//! owns the drink and the tray (`MapServices.VendMachine`); the request names
//! only the machine. A paid can waits in `DeviceState::vend_drops`, which
//! outlives the machine's entity across a region re-stream.
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
    pub(crate) index: usize,
    body_min: Vec3,
    body_max: Vec3,
    /// The shudder currently added to the pose: removed, never overwritten
    /// with a stored rest pose, so a procedural origin rebase mid-shake keeps.
    pub(crate) offset: Vec3,
    pub(crate) shake: f32,
    pub(crate) cooldown: f32,
    pub(crate) denied: f32,
    pub(crate) state: State,
}
impl Vendor {
    #[cfg(test)]
    pub(crate) fn for_test(drink: u16, body_min: Vec3, body_max: Vec3) -> Self {
        Self {
            drink,
            index: 0,
            body_min,
            body_max,
            offset: Vec3::ZERO,
            shake: 0.,
            cooldown: 0.,
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
    state: Res<DeviceState>,
    config: Res<crate::world::WorldConfig>,
    mut cache: ResMut<crate::content::ContentCache>,
    mut meshes: ResMut<Assets<Mesh>>,
    objects: Query<(Entity, &WorldObject), (Added<WorldObject>, With<PlacementEuler>)>,
) {
    for (entity, object) in &objects {
        if !is_vendor_prop(&object.asset_name) {
            continue;
        }
        let path = config.content_root.join(format!("objects/{}.obj", object.asset_name));
        let Ok((_, data)) = cache.load_mesh(&path, &mut meshes) else {
            continue;
        };
        // Mid-vend across a re-stream: still shaking, still owed its can.
        let shake = state.vend_drops.get(&object.source_index).copied().unwrap_or(0.);
        commands.entity(entity).insert((
            Device(Target::Vendor(object.source_index)),
            DeviceTint(Color::WHITE),
            Vendor {
                drink: if object.asset_name == "Vendor_1" { SODA } else { COLA },
                index: object.source_index,
                body_min: data.min,
                body_max: data.max,
                offset: Vec3::ZERO,
                shake,
                cooldown: if shake > 0. { shake + 0.55 } else { 0. },
                denied: 0.,
                state: if shake > 0. { State::Dispensing } else { State::Ready },
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
    mut state: ResMut<DeviceState>,
    mut vendors: Query<(&Device, &mut Vendor, &Transform)>,
    mut commands: Commands,
    config: Res<crate::world::WorldConfig>,
    assets: Res<AssetServer>,
    audio: Option<Res<Assets<AudioSource>>>,
) {
    for (device, mut vendor, pose) in &mut vendors {
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
                .request("action", json!({"name":"vendPay","args":{"sourceIndex":vendor.index}}))
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
        state.vend_drops.insert(vendor.index, SHAKE_SECONDS);
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
                Transform::from_translation(pose.transform_point(vendor.center())),
            ));
        }
    }
}

/// `HubTick`: timers, the decaying shudder on the pose, and the can on the
/// tick the shake reaches zero. The owed cans are timed off the machine's
/// entity, so a re-stream mid-shake still drops the paid can.
pub(crate) fn shake(
    time: Res<Time>,
    ui: Option<Res<UiState>>,
    bridge: Option<Res<CoreBridge>>,
    mut model: ResMut<UiModel>,
    mut state: ResMut<DeviceState>,
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
        let off = if vendor.shake > 0. {
            Vec3::new(ph.sin() * amp, 0., (ph * 0.7).cos() * amp * 0.5)
        } else {
            Vec3::ZERO
        };
        pose.translation += off - vendor.offset;
        vendor.offset = off;
    }
    if dt <= 0. {
        return;
    }
    let mut due = vec![];
    state.vend_drops.retain(|index, left| {
        *left -= dt;
        if *left <= 0. {
            due.push(*index);
        }
        *left > 0.
    });
    for index in due {
        if let Some(bridge) = &bridge
            && let Err(error) = bridge.request("action", json!({"name":"vendDrop","args":{"sourceIndex":index}}))
        {
            model.notice(error.to_string());
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
