//! Accepted source fifth-wheel relationship, physical point joint and landing supports.
pub(super) mod geometry;
pub(super) mod input;
pub(super) mod physics;
pub(super) mod recovery;
pub(super) mod state;
pub(super) mod support;

use bevy::prelude::*;
pub(crate) use geometry::GHOST_TRAILER;
pub(super) struct CouplingPlugin;
impl Plugin for CouplingPlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<physics::Exceptions>()
            .init_resource::<recovery::Recoveries>()
            .add_systems(
                Update,
                (
                    recovery::receive,
                    recovery::apply,
                    physics::sync,
                    super::adopt_occupancy,
                )
                    .chain()
                    .after(super::VehicleUpdate)
                    .after(super::glazing::collision::install)
                    .after(crate::entities::CoreEventsReady)
                    .before(crate::player::PlayerCameraUpdate),
            );
    }
}
#[cfg(test)]
mod tests;
