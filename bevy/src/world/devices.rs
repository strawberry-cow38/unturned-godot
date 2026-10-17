//! Interactive world devices placed by `WorldBuilder` from the retail object
//! list: indoor lamps (`LampLight`, `Deployable` world scenery) and readable
//! notes (`NoteBody`/`NoteReader`). Every one of these is client-local in the
//! source (`mode != WorldMode.Dedicated`, no command, no replication), so the
//! port keeps their state local too; the only shared input is the accepted
//! mains (`PowerNet.MainsLive`), read from replicated GridSource fixtures.
//!
//! On a `--dedicated` host none of them run (`has_local_player`), as the
//! source never builds them there. The car lift is the exception: its
//! platform is a collider in EVERY mode (`WorldBuilder` "EVERY MODE, because
//! the ramp is a moving COLLIDER"), so `carlift::attach` runs on the server too.
//! See `bevy/docs/WORLD_DEVICES_PORT.md`.
use super::{WorldObject, origin::WorldOrigin};
use crate::{
    entities::{CollectInteractions, InteractionFocus, ItemCatalog},
    player::{GameSnapshot, PlayerBody, PlayerCamera, PlayerCameraUpdate, WorldSessionEpoch},
    settings::{GameAction, Settings},
    ui::{Screen, UiState},
};
use bevy::prelude::*;
use bevy_rapier3d::prelude::*;
use serde_json::{Value, json};
use std::collections::HashMap;

pub(crate) mod carlift;
pub(crate) mod lamps;
pub(crate) mod monitor;
pub(crate) mod notes;
pub(crate) mod radio;
pub(crate) mod traffic;
pub(crate) mod vending;
#[cfg(test)]
mod tests;

/// `PlayerController.LookReach`: the eye ray reaches 2.6 m.
pub(crate) const LOOK_REACH: f32 = 2.6;

/// The retail placement's authored Euler angles (`WorldBuilder` reads them to
/// decide whether a lamp can stand as a deployable). Absent on finite maps.
#[derive(Component, Clone, Copy, Debug)]
pub(crate) struct PlacementEuler(pub Vec3);

/// Local, per-client device state keyed by the stable placement identity, so
/// a region re-stream keeps a toggled lamp toggled (source nodes never stream).
#[derive(Resource, Default)]
pub(crate) struct DeviceState {
    epoch: u64,
    /// `LampLight._on`: the manual switch, ON by default.
    pub(crate) lamp_off: std::collections::HashSet<usize>,
    /// `RadioDevice._on`: OFF by default.
    pub(crate) radio_on: std::collections::HashSet<usize>,
    /// `HeartMonitor._on`: ON by default.
    pub(crate) monitor_off: std::collections::HashSet<usize>,
    /// `HeartMonitor._alive`: rolled once per world build (`GD.Randf()`), so a
    /// re-stream must not re-roll it.
    pub(crate) monitor_alive: HashMap<usize, bool>,
    /// `TrafficLight._batteryDeadAt` of each head running on its battery, by
    /// (placement, head): a re-stream mid-outage keeps the deadline.
    pub(crate) signal_battery: HashMap<(usize, usize), f64>,
    /// Paid vends whose can has not dropped yet, by placement: seconds left
    /// on the shake (`_pendingDrink`). Outlives the machine's entity.
    pub(crate) vend_drops: HashMap<usize, f32>,
    /// The traffic clock's fallback when the snapshot carries no day.
    pub(crate) signal_clock: traffic::Clock,
}

/// `DayNightCycle.MainsLive`, throttled to 4 Hz as the source scan is.
#[derive(Resource)]
pub(crate) struct Mains {
    pub(crate) live: bool,
    timer: f32,
}
impl Default for Mains {
    fn default() -> Self {
        Self { live: true, timer: 0. }
    }
}

pub(crate) struct DevicePlugin;
impl Plugin for DevicePlugin {
    fn build(&self, app: &mut App) {
        register_ecg(app);
        systems(app);
    }
}

/// The device resources and schedule, apart from the render-side material.
pub(crate) fn systems(app: &mut App) {
    app.init_resource::<monitor::Clock>()
        .init_resource::<traffic::SideRoads>()
        .init_resource::<traffic::Palette>()
        .init_resource::<DeviceState>()
        .init_resource::<Mains>()
        .init_resource::<notes::Texts>()
        .init_resource::<notes::Reader>()
        .add_systems(
            Update,
            (
                reset_epoch,
                mains,
                (
                    lamps::attach,
                    notes::attach,
                    vending::attach,
                    radio::attach,
                    monitor::attach,
                    traffic::attach,
                ),
                (
                    lamps::grid,
                    lamps::hum,
                    (vending::shake, vending::tint).chain(),
                    radio::hiss,
                    monitor::tick,
                    traffic::tick,
                ),
            )
                .chain()
                .after(crate::entities::WorldServicesReady)
                .before(CollectInteractions)
                .run_if(crate::dedicated::has_local_player),
        )
        // The platform collider exists in every mode, dedicated included.
        .add_systems(
            Update,
            carlift::attach
                .after(crate::entities::WorldServicesReady)
                .before(CollectInteractions),
        )
        .add_systems(
            Update,
            collect_focus
                .in_set(CollectInteractions)
                .after(PlayerCameraUpdate)
                .run_if(crate::dedicated::has_local_player),
        )
        .add_systems(
            Update,
            (
                scenery_info,
                lamps::press,
                notes::press,
                notes::draw,
                vending::press,
                vending::prompt,
                radio::press,
                monitor::press,
            )
                .chain()
                .after(CollectInteractions)
                .before(crate::entities::ApplyInteractions)
                .run_if(crate::dedicated::has_local_player),
        )
        .add_systems(
            Update,
            radio::board
                .after(radio::press)
                .run_if(resource_exists::<bevy::text::TextPipeline>)
                .run_if(crate::dedicated::has_local_player),
        );
}

/// The patient monitor's screen material (`ecg.gdshader`).
pub(crate) fn register_ecg(app: &mut App) {
    bevy::asset::embedded_asset!(app, "devices/ecg.wesl");
    app.add_plugins(MaterialPlugin::<monitor::EcgMaterial>::default());
}

fn reset_epoch(epoch: Res<WorldSessionEpoch>, mut state: ResMut<DeviceState>) {
    if state.epoch != epoch.0 {
        *state = DeviceState {
            epoch: epoch.0,
            ..default()
        };
    }
}

/// `PowerNet.MainsLive` on the replicated path: believe the breakers when the
/// world has any GridSource, else the default-on global flag.
pub(crate) fn mains_live(snapshot: &Value, catalog: &ItemCatalog) -> bool {
    crate::deployables::mains_live(snapshot, catalog)
}

fn mains(
    time: Res<Time>,
    snapshot: Res<GameSnapshot>,
    catalog: Res<ItemCatalog>,
    mut mains: ResMut<Mains>,
) {
    // DayNightCycle.MainsLive: the real delta, a 0.25 s throttle.
    mains.timer -= time.delta_secs();
    if mains.timer > 0. {
        return;
    }
    mains.timer = 0.25;
    let live = mains_live(&snapshot.0, &catalog);
    if mains.live != live {
        mains.live = live;
    }
}

/// What a source look-ray hit resolves to (`UpdateLookFocus` meta chain).
#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) enum Target {
    /// A toggle `LampLight` (`LampLight.LookMeta`).
    Lamp(usize),
    /// An upright desk/standing lamp: world-scenery `Deployable` focus.
    SceneryLamp(usize),
    /// A `NoteBody`.
    Note(usize),
    /// A `VendingMachine` (`Vendor_0`/`Vendor_1`).
    Vendor(usize),
    /// A `RadioDevice` (`Radio_0`/`Radio_1`).
    Radio(usize),
    /// A `HeartMonitor` (`Science_3`).
    Monitor(usize),
    /// A `CarLift` platform (`Car_Lift_0`).
    CarLift(usize),
}
impl Target {
    pub(crate) fn action(&self) -> (&'static str, Value) {
        match self {
            Self::Lamp(i) => ("toggleLamp", json!({"sourceIndex":i})),
            Self::SceneryLamp(i) => ("sceneryDeployable", json!({"sourceIndex":i})),
            Self::Note(i) => ("readNote", json!({"sourceIndex":i})),
            Self::Vendor(i) => ("vend", json!({"sourceIndex":i})),
            Self::Radio(i) => ("toggleRadio", json!({"sourceIndex":i})),
            Self::Monitor(i) => ("toggleMonitor", json!({"sourceIndex":i})),
            Self::CarLift(i) => ("carLift", json!({"sourceIndex":i})),
        }
    }
}
/// The device component the ray resolves through, on the prop entity.
#[derive(Component, Clone, Copy, Debug)]
pub(crate) struct Device(pub Target);
/// The rim colour a device claims (`SetLookFocused`); white when absent.
#[derive(Component, Clone, Copy, Debug)]
pub(crate) struct DeviceTint(pub Color);

#[allow(clippy::too_many_arguments)]
fn collect_focus(
    ui: Res<UiState>,
    snapshot: Res<GameSnapshot>,
    occupancy: Option<Res<super::interactables::PropOccupancy>>,
    vehicle: Option<Res<crate::vehicle::Occupancy>>,
    mut focus: ResMut<InteractionFocus>,
    camera: Query<&Transform, With<PlayerCamera>>,
    player: Query<Entity, With<PlayerBody>>,
    devices: Query<(&Device, Option<&DeviceTint>)>,
    parents: Query<&ChildOf>,
    context: ReadRapierContext,
) {
    if ui.captures_input()
        || vehicle.is_some_and(|v| v.seat.is_some())
        || occupancy.is_some_and(|o| o.seat.is_some())
        || snapshot.0["player"]["alive"] == false
    {
        return;
    }
    let (Ok(camera), Ok(context)) = (camera.single(), context.single()) else {
        return;
    };
    let own = player.single().ok();
    let exclude_owner = |entity, _: &bevy_rapier3d::rapier::geometry::Collider| {
        !own.is_some_and(|p| entity == p || parents.iter_ancestors(entity).any(|a| a == p))
    };
    let Some((hit, distance)) = context.cast_ray(
        camera.translation,
        *camera.forward(),
        LOOK_REACH,
        true,
        QueryFilter::default()
            .exclude_sensors()
            .groups(CollisionGroups::new(
                Group::GROUP_2,
                Group::GROUP_1 | Group::GROUP_4 | crate::world::buildings::SOURCE_PROP,
            ))
            .predicate(&exclude_owner),
    ) else {
        return;
    };
    let Some((owner, device, tint)) = std::iter::once(hit)
        .chain(parents.iter_ancestors(hit))
        .find_map(|e| devices.get(e).ok().map(|(d, t)| (e, *d, t.map_or(Color::WHITE, |t| t.0))))
    else {
        return;
    };
    // Every source device here focuses silently (the scenery lamp and the
    // vending machine say what they do on their look-at billboard) and claims
    // the whole prop's rim: white (LampLight, NoteBody, RadioDevice,
    // HeartMonitor), Deployable.OutlineColor, or the vending state colour.
    let (action, args) = device.0.action();
    focus.offer_status(distance, "", action, args.clone());
    // `_focusCarLift` is a bare assignment in the source: no outline.
    if !matches!(device.0, Target::CarLift(_)) {
        focus.outline(action, &args, owner, tint);
    }
}

/// The look-at billboard of the focused world-scenery lamp, once focus has a
/// single winner (`Deployable._Process` shows it only while look-focused).
pub(crate) fn scenery_info(
    focus: Res<InteractionFocus>,
    mut info: Option<ResMut<crate::info_billboard::FocusedInfo>>,
    scenery: Query<(&Device, &lamps::SceneryLamp, &GlobalTransform)>,
) {
    let Some(info) = info.as_mut() else { return };
    for (device, lamp, pose) in &scenery {
        let (action, args) = device.0.action();
        if focus.looked_args(action, &args) {
            info.0 = Some(lamp.billboard(pose.translation()));
        }
    }
}

/// The winner, if F was just pressed on one of these targets this frame.
pub(crate) fn pressed_target(
    ui: &UiState,
    settings: &Settings,
    keys: &ButtonInput<KeyCode>,
    mouse: &ButtonInput<MouseButton>,
    focus: &InteractionFocus,
    target: Target,
) -> bool {
    let (action, args) = target.action();
    ui.screen == Screen::Playing
        && !ui.captures_input()
        && settings.just_pressed(GameAction::Interact, keys, mouse)
        // PlayerController: Interact while ALT-looking interacts with nothing.
        && !settings.pressed(GameAction::FreeLook, keys, mouse)
        && focus.selected_args(action, &args)
}

/// Absolute source position (InitJitter hashes the world position).
pub(crate) fn absolute(origin: Option<&WorldOrigin>, local: Vec3) -> Vec3 {
    origin.map_or(local, |o| o.to_absolute(local).as_vec3())
}

/// Shared per-prop split meshes, by (prop name, kind).
#[derive(Default)]
pub(crate) struct MeshCache(pub HashMap<(String, u8), Option<lamps::Built>>);
