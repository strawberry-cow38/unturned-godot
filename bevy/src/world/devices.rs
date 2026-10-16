//! Interactive world devices placed by `WorldBuilder` from the retail object
//! list: indoor lamps (`LampLight`, `Deployable` world scenery) and readable
//! notes (`NoteBody`/`NoteReader`). Every one of these is client-local in the
//! source (`mode != WorldMode.Dedicated`, no command, no replication), so the
//! port keeps their state local too; the only shared input is the accepted
//! mains (`PowerNet.MainsLive`), read from replicated GridSource fixtures.
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

pub(crate) mod lamps;
pub(crate) mod notes;
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
        app.init_resource::<DeviceState>()
            .init_resource::<Mains>()
            .init_resource::<notes::Texts>()
            .init_resource::<notes::Reader>()
            .add_systems(
                Update,
                (reset_epoch, mains, lamps::attach, notes::attach, lamps::grid, lamps::hum)
                    .chain()
                    .after(crate::entities::WorldServicesReady)
                    .before(CollectInteractions),
            )
            .add_systems(
                Update,
                collect_focus
                    .in_set(CollectInteractions)
                    .after(PlayerCameraUpdate),
            )
            .add_systems(
                Update,
                (scenery_info, lamps::press, notes::press, notes::draw)
                    .chain()
                    .after(CollectInteractions)
                    .before(crate::entities::ApplyInteractions),
            );
    }
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
}
impl Target {
    pub(crate) fn action(&self) -> (&'static str, Value) {
        match self {
            Self::Lamp(i) => ("toggleLamp", json!({"sourceIndex":i})),
            Self::SceneryLamp(i) => ("sceneryDeployable", json!({"sourceIndex":i})),
            Self::Note(i) => ("readNote", json!({"sourceIndex":i})),
        }
    }
}
/// The device component the ray resolves through, on the prop entity.
#[derive(Component, Clone, Copy, Debug)]
pub(crate) struct Device(pub Target);

#[allow(clippy::too_many_arguments)]
fn collect_focus(
    ui: Res<UiState>,
    snapshot: Res<GameSnapshot>,
    occupancy: Option<Res<super::interactables::PropOccupancy>>,
    vehicle: Option<Res<crate::vehicle::Occupancy>>,
    mut focus: ResMut<InteractionFocus>,
    camera: Query<&Transform, With<PlayerCamera>>,
    player: Query<Entity, With<PlayerBody>>,
    devices: Query<&Device>,
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
                Group::GROUP_1 | Group::GROUP_4,
            ))
            .predicate(&exclude_owner),
    ) else {
        return;
    };
    let Some((owner, device)) = std::iter::once(hit)
        .chain(parents.iter_ancestors(hit))
        .find_map(|e| devices.get(e).ok().map(|d| (e, *d)))
    else {
        return;
    };
    let (action, args) = device.0.action();
    match device.0 {
        // LampLight/NoteBody.SetLookFocused: the whole prop, white, no prompt.
        Target::Lamp(_) | Target::Note(_) => {
            focus.offer_status(distance, "", action, args.clone());
            focus.outline(action, &args, owner, Color::WHITE);
        }
        Target::SceneryLamp(_) => {
            // Deployable focus: Deployable.OutlineColor and the look-at billboard.
            focus.offer_status(distance, "", action, args.clone());
            focus.outline(action, &args, owner, crate::deployables::OUTLINE_COLOR);
        }
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
        if focus.selected_args(action, &args) {
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
