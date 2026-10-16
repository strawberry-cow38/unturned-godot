//! Door clearance for occupants who are not this host's own player.
//!
//! The authority answers CommandExitVehicle through `VehicleServices.ResolveExit`,
//! which only accepts a host-physics clearance result at most 10 ticks old (the
//! source asks `Vehicle.ResolveDoorExit` against the host's own physics). The
//! local driver's F press sends that result itself (`read_controls`); a joined
//! client's exit arrives as a command with nobody probing, so every door read as
//! blocked and a remote rider could never get out. A host therefore keeps a
//! fresh probe for each seat a remote player occupies.

use super::{exit, VehicleBody};
use crate::{bridge::CoreBridge, content::ContentCache, player::GameSnapshot, ui::UiModel, world::WorldConfig};
use bevy::prelude::*;
use bevy_rapier3d::prelude::*;
use serde_json::{json, Value};

/// Re-probe every few authority ticks: `ResolveExit` refuses results older than 10.
pub(super) const EVERY_TICKS: u32 = 4;

/// Seats held by players other than `local`, as (seat, playerId), from a
/// snapshot vehicle row (`driverPlayerId` is seat 0, `passengers[i]` seat i+1).
pub(super) fn remote_seats(row: &Value, local: u64) -> Vec<usize> {
    let driver = row["driverPlayerId"].as_u64().unwrap_or(0);
    let passengers = row["passengers"].as_array().cloned().unwrap_or_default();
    std::iter::once(driver)
        .chain(passengers.iter().map(|p| p.as_u64().unwrap_or(0)))
        .enumerate()
        .filter(|&(_, id)| id != 0 && id != local)
        .map(|(seat, _)| seat)
        .collect()
}

#[allow(clippy::too_many_arguments)]
pub(crate) fn publish(
    bridge: Res<CoreBridge>,
    model: Res<UiModel>,
    snapshot: Option<Res<GameSnapshot>>,
    context: ReadRapierContext,
    parents: Query<&ChildOf>,
    vehicles: Query<(Entity, &VehicleBody, &Transform)>,
    cache: Res<ContentCache>,
    config: Res<WorldConfig>,
    mut step: Local<u32>,
) {
    if !model.connected || !model.network.owns_world {
        return;
    }
    *step += 1;
    if *step % EVERY_TICKS != 0 {
        return;
    }
    let (Some(snapshot), Ok(physics)) = (snapshot, context.single()) else {
        return;
    };
    let local = snapshot.0["playerId"].as_u64().unwrap_or(0);
    let Some(rows) = snapshot.0["vehicles"].as_array() else {
        return;
    };
    for row in rows {
        let seats = remote_seats(row, local);
        if seats.is_empty() {
            continue;
        }
        let net_id = row["netId"].as_u64().unwrap_or(0) as u32;
        let Some((entity, body, pose)) = vehicles.iter().find(|(_, b, _)| b.net_id == net_id) else {
            continue;
        };
        let Some(d) = super::data::definition(body.type_id) else {
            continue;
        };
        for seat in seats.into_iter().filter(|&s| s < d.seats.len()) {
            let doors = exit::doors(d, seat, pose, &cache, &config.content_root);
            // A seated remote's hull is ColliderDisabled, so nothing of theirs
            // needs excluding beyond the vehicle's own bodies.
            let (clear, position) = exit::probe(
                &physics,
                &parents,
                entity,
                Entity::PLACEHOLDER,
                pose,
                Vec3::from_array(d.seats[seat]),
                &doors,
            );
            let _ = bridge.request(
                "worldVehicleExit",
                json!({"netId":net_id,"seat":seat,"clear":clear,"position":position.to_array()}),
            );
        }
    }
}

#[cfg(test)]
mod tests {
    use super::remote_seats;
    use serde_json::json;

    #[test]
    fn remote_seats_skip_the_local_player_and_empty_seats() {
        let row = json!({"driverPlayerId": 1, "passengers": [0, 2, 3]});
        assert_eq!(remote_seats(&row, 1), vec![2, 3]);
        assert_eq!(remote_seats(&row, 0), vec![0, 2, 3]);
        assert!(remote_seats(&json!({"driverPlayerId": 0}), 0).is_empty());
    }
}
