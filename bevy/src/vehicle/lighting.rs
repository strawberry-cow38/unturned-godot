//! Source vehicle electrical presentation. Accepted state owns all switches,
//! power and broken lamps; native clocks animate only flashes and sound.
pub(super) mod geometry;
mod breaks;
mod presentation;
mod state;

use bevy::prelude::*;
use serde::Deserialize;

#[derive(Clone, Debug, Deserialize)]
pub(crate) struct Beam {
    pub range: f32,
    pub energy: f32,
    pub angle: f32,
    pub attenuation: f32,
}
#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct Definition {
    pub lamp_labels: Vec<String>,
    pub pattern_names: Vec<String>,
    pub siren_pitch: Vec<f32>,
    pub low: Beam,
    pub high: Beam,
}
pub(super) struct LightingPlugin;
impl Plugin for LightingPlugin {
    fn build(&self, app: &mut App) {
        app.add_systems(
            Update,
            (presentation::build, presentation::upload)
                .chain()
                .after(super::VehicleUpdate),
        )
        .add_systems(FixedUpdate, presentation::advance.after(super::step_bodies))
        .add_systems(Update, presentation::update.after(super::VehicleUpdate))
        .add_systems(Update, breaks::receive
            .after(crate::entities::CoreEventsReady)
            .after(super::VehicleUpdate)
            .before(presentation::update))
        .add_systems(
            Update,
            presentation::events.after(crate::entities::ApplyInteractions),
        );
    }
}
