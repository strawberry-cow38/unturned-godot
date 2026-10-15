//! Mounted input chooses the original seat/slot but only sends intent. Rendered
//! angles, belt counts and head-out state are accepted replicas on every peer.
use super::*;
use crate::{
    bridge::CoreBridge,
    player::{GameSnapshot, PlayerBody, PlayerCamera, PlayerInput},
    settings::{GameAction, Settings},
    ui::{UiModel, UiState},
};
use serde_json::{Value, json};

#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Row {
    vehicle_net_id: u32,
    mounts: Vec<State>,
    head_out: bool,
    #[serde(default)]
    gunner_rise: Option<f32>,
}
#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct State {
    index: usize,
    yaw: f32,
    pitch: f32,
    ammo: u16,
    cooldown_seconds: f32,
    crew_alive: bool,
}
fn rows(snapshot: &Value) -> Vec<Row> {
    serde_json::from_value::<Vec<Row>>(snapshot["vehicleMounts"].clone())
        .unwrap_or_default()
        .into_iter()
        .filter(|r| {
            r.gunner_rise
                .is_none_or(|v| v.is_finite() && (0. ..=1.).contains(&v))
                && r.mounts.iter().enumerate().all(|(i, m)| {
                    i == m.index
                        && m.yaw.is_finite()
                        && m.pitch.is_finite()
                        && m.cooldown_seconds.is_finite()
                        && m.cooldown_seconds >= 0.
                })
        })
        .collect()
}
pub(crate) fn adopt(snapshot: Res<GameSnapshot>, mut bodies: Query<(&VehicleBody, &mut Mounts)>) {
    let rows = rows(&snapshot.0);
    for (body, mut mounts) in &mut bodies {
        let Some(row) = rows
            .iter()
            .find(|r| r.vehicle_net_id == body.net_id && r.mounts.len() == mounts.angles.len())
        else {
            continue;
        };
        for (i, state) in row.mounts.iter().enumerate() {
            mounts.angles[i] = Vec2::new(state.yaw, state.pitch);
            mounts.crew_alive[i] = state.crew_alive;
        }
        mounts.head_out = row.head_out;
        mounts.accepted_gunner_rise = row.gunner_rise;
    }
}

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub(crate) struct Seat {
    pub net_id: u32,
    pub seat: u8,
    pub type_id: u8,
}
#[derive(Clone)]
pub(crate) struct Operated {
    pub index: usize,
    pub definition: &'static super::super::data::TurretDef,
    /// None means the mount replica has not arrived; do not invent a full belt.
    pub ammo: Option<u16>,
    pub cooldown: f32,
}
#[derive(Resource, Default)]
pub(crate) struct Operator {
    pub seat: Option<Seat>,
    pub slot: usize,
    available: Vec<Operated>,
    pub head_out: bool,
    head_request: Option<bool>,
    epoch: u64,
}
impl Operator {
    pub fn active(&self) -> Option<&Operated> {
        self.available.get(self.slot)
    }
    pub fn consumes_slot(&self, slot: usize) -> bool {
        slot < self.available.len()
    }
    pub fn select(&mut self, slot: usize) {
        if self.consumes_slot(slot) {
            self.slot = slot;
        }
    }
    pub fn toggle_head_out(&mut self) {
        if self.seat.is_some_and(|s| {
            s.seat == 1
                && super::super::data::definition(s.type_id)
                    .is_some_and(|d| d.spec.turret_meshes.is_some())
        }) {
            self.head_request = Some(!self.head_request.unwrap_or(self.head_out));
        }
    }
    pub fn trigger_live(&self, aim: bool) -> bool {
        self.active().is_some_and(|mount| {
            let t = mount.definition;
            !t.crosshair_aim
                || !t.hold_to_aim
                || self.seat.is_some_and(|s| s.seat != 0 || t.driver_fallback)
                || aim
        })
    }
    pub fn crosshair_aiming(&self, aim: bool) -> bool {
        self.active().is_some_and(|m| m.definition.crosshair_aim) && self.trigger_live(aim)
    }
    pub fn look_unlimited(&self) -> bool {
        self.available.first().is_some_and(|m| {
            self.seat
                .is_some_and(|s| s.seat != 0 || !m.definition.crosshair_aim)
        })
    }
    pub fn refresh(&mut self, snapshot: &Value, epoch: u64) {
        let player = snapshot["playerId"].as_u64().unwrap_or(0) as u32;
        let live = snapshot["connected"].as_bool() == Some(true)
            && snapshot["player"]["alive"].as_bool() != Some(false)
            && player != 0;
        let vehicles: Vec<super::super::Replica> =
            serde_json::from_value(snapshot["vehicles"].clone()).unwrap_or_default();
        let seated = vehicles.iter().filter(|_| live).find_map(|v| {
            let seat = if v.driver_player_id == player {
                Some(0)
            } else {
                v.passengers
                    .as_ref()
                    .and_then(|p| p.iter().position(|p| *p == player))
                    .map(|i| i as u8 + 1)
            }?;
            Some((
                Seat {
                    net_id: v.net_id,
                    seat,
                    type_id: v.type_id,
                },
                v,
            ))
        });
        let next = seated.map(|s| s.0);
        if self.epoch != epoch
            || self.seat.map(|s| (s.net_id, s.type_id)) != next.map(|s| (s.net_id, s.type_id))
        {
            *self = Self {
                epoch,
                seat: next,
                ..default()
            };
        } else if self.seat != next {
            // TrySwitchSeat preserves the selected weapon; EnterVehicle resets
            // it. A pending gunner hatch request must not follow another seat.
            self.seat = next;
            self.head_request = None;
        }
        self.available.clear();
        let Some((seat, vehicle)) = seated else {
            return;
        };
        let Some(defs) =
            super::super::data::definition(seat.type_id).and_then(|d| d.spec.turrets.as_ref())
        else {
            return;
        };
        let accepted = rows(snapshot)
            .into_iter()
            .find(|r| r.vehicle_net_id == seat.net_id && r.mounts.len() == defs.len());
        self.head_out = accepted.as_ref().is_some_and(|r| r.head_out);
        if self.head_request == Some(self.head_out) {
            self.head_request = None;
        }
        let owns = defs.iter().any(|d| d.seat == seat.seat);
        for (index, def) in defs.iter().enumerate() {
            let free = if def.seat == 0 {
                vehicle.driver_player_id == 0
            } else {
                vehicle
                    .passengers
                    .as_ref()
                    .and_then(|p| p.get(def.seat as usize - 1))
                    .is_none_or(|p| *p == 0)
            };
            if (owns && def.seat == seat.seat)
                || (!owns && seat.seat == 0 && def.driver_fallback && free)
            {
                let state = accepted.as_ref().and_then(|r| r.mounts.get(index));
                self.available.push(Operated {
                    index,
                    definition: def,
                    ammo: state.map(|m| m.ammo),
                    cooldown: state.map_or(0., |m| m.cooldown_seconds),
                });
            }
        }
    }
}

/// Source CrosshairPoint: active camera,600m, ignore the rider and every child
/// collision body of the ridden vehicle, including its articulated barrel.
fn crosshair(
    context: &RapierContext,
    camera: &Transform,
    vehicle: Entity,
    player: Entity,
    parents: &Query<&ChildOf>,
    oceans: &Query<(&crate::world::OceanBox, &Transform)>,
) -> Vec3 {
    let skip = |entity, _: &_| {
        entity != player
            && entity != vehicle
            && !parents
                .iter_ancestors(entity)
                .take(24)
                .any(|e| e == vehicle)
    };
    let forward = *camera.forward();
    let mut length = context
        .cast_ray(
            camera.translation,
            forward,
            600.,
            true,
            QueryFilter::default()
                .exclude_sensors()
                .predicate(&skip)
                .groups(CollisionGroups::new(
                    Group::ALL,
                    Group::GROUP_1
                        | Group::GROUP_2
                        | Group::GROUP_4
                        | Group::GROUP_5
                        | Group::GROUP_6
                        | Group::GROUP_15,
                )),
        )
        .map_or(600., |(_, distance)| distance);
    // The original ocean participates in the aim ray, but is deliberately not
    // a solid native player collider. Query its same authored box directly.
    for (water, pose) in oceans {
        let half = water.size * 0.5;
        if let Some(distance) = Collider::cuboid(half.x, half.y, half.z).cast_ray(
            pose.translation,
            pose.rotation,
            camera.translation,
            forward,
            length,
            true,
        ) {
            length = distance;
        }
    }
    camera.translation + forward * length
}
#[allow(clippy::too_many_arguments)]
pub(crate) fn send(
    operator: Res<Operator>,
    ui: Res<UiState>,
    model: Res<UiModel>,
    settings: Res<Settings>,
    keys: Res<ButtonInput<KeyCode>>,
    mouse: Res<ButtonInput<MouseButton>>,
    input: Res<PlayerInput>,
    occupancy: Res<super::super::Occupancy>,
    bridge: Res<CoreBridge>,
    context: ReadRapierContext,
    camera: Query<&Transform, With<PlayerCamera>>,
    players: Query<Entity, With<PlayerBody>>,
    parents: Query<&ChildOf>,
    oceans: Query<(&crate::world::OceanBox, &Transform)>,
) {
    // The integrated authority stops with SP pause. Do not accumulate input
    // commands for it to replay when unpaused; MP menus still send release.
    if crate::player::simulation_paused(&ui) {
        return;
    }
    let (Some(seat), Some(mount), Some(occupied)) =
        (operator.seat, operator.active(), occupancy.seat)
    else {
        return;
    };
    if occupied.net_id != seat.net_id || occupied.seat != seat.seat || mount.ammo.is_none() {
        return;
    }
    let active = model.connected && model.alive && !ui.captures_input();
    let live = active && operator.trigger_live(settings.pressed(GameAction::Aim, &keys, &mouse));
    let Ok(camera) = camera.single() else { return };
    let point = if let (Ok(context), Ok(player)) = (context.single(), players.single()) {
        crosshair(
            &context,
            camera,
            occupied.vehicle,
            player,
            &parents,
            &oceans,
        )
    } else {
        camera.translation + *camera.forward() * 600.
    };
    let args = json!({"netId":seat.net_id,"slot":operator.slot,
        "aimMode":if !live {"none"} else if mount.definition.crosshair_aim {"point"} else {"look"},
        "yaw":(input.yaw()+180.).rem_euclid(360.)-180.,"pitch":input.pitch(),"target":point.to_array(),
        "trigger":live && settings.pressed(GameAction::Fire,&keys,&mouse),
        "headOut":operator.head_request.unwrap_or(operator.head_out)});
    if let Err(error) = bridge.request("action", json!({"name":"mountInput","args":args})) {
        warn!("Mounted input: {error}");
    }
}

#[cfg(test)]
mod tests;
