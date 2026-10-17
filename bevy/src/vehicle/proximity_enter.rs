//! `PlayerController.NearestPuppet` / `RequestEnterNearestPuppet` (C6): a
//! joined MP client pressing F with no higher-ranked target asks the server
//! for a seat in the nearest replicated vehicle within 4 m of the player
//! origin, seat 255 (`EnterVehicleCommand.AnySeat`, driver first). It is a
//! REQUEST only: `ServerVehicles.CanEnter` validates reach, occupancy and
//! life, and the seat arrives through the accepted `VehicleEntered` fact.
//!
//! The source has no prompt and no outline for it (`NearestPuppet` touches
//! no focus state), so it offers nothing visible: a lower look target keeps
//! its own label and rim while F goes to the vehicle. Its rank in the F
//! chain lives in `InteractionFocus::offer_proximity`.
//!
//! Only a joined client has puppets (`NetEnterVehicle` is set only by
//! `ClientWorldSession`); singleplayer and the listen host never offer it.
use super::VehicleBody;
use crate::{
    entities::{CollectInteractions, InteractionFocus},
    player::{GameSnapshot, PlayerBody},
    ui::UiState,
};
use bevy::prelude::*;
use serde_json::json;

/// `NearestPuppet`: `bestD = 4.0f * 4.0f`, the SP NearestVehicle prompt range.
pub(crate) const REACH: f32 = 4.;
/// `EnterVehicleCommand.AnySeat`. The source's door-zone seat applies only to
/// a `_focusVehicle`, which a joined client's puppets never are.
pub(crate) const ANY_SEAT: u8 = 255;

/// Screen/input only: the `--dedicated` app has no local player to press F.
pub struct ProximityEnterPlugin;
impl Plugin for ProximityEnterPlugin {
    fn build(&self, app: &mut App) {
        app.add_systems(Update, offer.in_set(CollectInteractions));
    }
}

pub(crate) fn offer(
    ui: Res<UiState>,
    snapshot: Res<GameSnapshot>,
    occupancy: Option<Res<super::Occupancy>>,
    player: Query<&Transform, With<PlayerBody>>,
    bodies: Query<(&VehicleBody, &Transform)>,
    mut focus: ResMut<InteractionFocus>,
) {
    if crate::network::owns_world(&snapshot.0)
        || ui.captures_input()
        || occupancy.is_some_and(|o| o.seat.is_some())
        || snapshot.0["player"]["alive"] != true
        || snapshot.0["player"]["vehicleNetId"].as_u64().unwrap_or(0) != 0
    {
        return;
    }
    let Ok(player) = player.single() else {
        return;
    };
    if let Some(id) = nearest(
        player.translation,
        bodies.iter().map(|(b, t)| (b.net_id, t.translation)),
    ) {
        focus.offer_proximity("enterVehicle", json!({"netId":id,"seat":ANY_SEAT}));
    }
}

/// The first strictly-nearest vehicle origin with `d² < 4²`.
pub(crate) fn nearest(from: Vec3, vehicles: impl Iterator<Item = (u32, Vec3)>) -> Option<u32> {
    let mut best = None;
    let mut best_d = REACH * REACH;
    for (id, at) in vehicles {
        let d = from.distance_squared(at);
        if id != 0 && d < best_d {
            best_d = d;
            best = Some(id);
        }
    }
    best
}

#[cfg(test)]
mod tests;
#[cfg(test)]
mod udp;
