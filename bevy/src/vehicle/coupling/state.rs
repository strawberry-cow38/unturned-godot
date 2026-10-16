//! Accepted relationship identity. A trailer inherits physics ownership without
//! acquiring a driver seat or an independent client state stream.
use super::super::{Replica, VehicleBody};
use serde_json::Value;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub(in crate::vehicle) struct Accepted {
    pub cab: u32,
    pub trailer: u32,
    pub driver: u32,
}
impl Accepted {
    pub(in crate::vehicle) fn from_rows(id: u32, rows: &[Replica]) -> Self {
        let trailer = rows
            .iter()
            .find(|r| r.net_id == id)
            .map_or(0, |r| r.coupled_trailer_net_id);
        let cab = rows
            .iter()
            .find(|r| r.coupled_trailer_net_id == id && id != 0);
        Self {
            cab: cab.map_or(0, |r| r.net_id),
            trailer,
            driver: cab.map_or(0, |r| r.driver_player_id),
        }
    }
}
pub(in crate::vehicle) fn driver(body: &VehicleBody) -> u32 {
    if body.coupling.cab != 0 {
        body.coupling.driver
    } else {
        body.driver_player_id
    }
}
pub(in crate::vehicle) fn simulates(snapshot: &Value, body: &VehicleBody) -> bool {
    let owner = driver(body);
    (owner != 0 && snapshot["playerId"].as_u64() == Some(owner.into()))
        || (owner == 0 && crate::network::owns_world(snapshot))
}
