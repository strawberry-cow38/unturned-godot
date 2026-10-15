//! Original NpcHeli brains supply four stick axes. The existing native vehicle
//! body and Flight::step remain the sole movement/force integrator.
use super::{VehicleBody, VehicleUpdate, data, dynamics::Controls};
use crate::{
    bridge::CoreBridge,
    player::{GameSnapshot, WorldSessionEpoch, simulation_paused},
    ui::{UiModel, UiState},
    world::{WorldConfig, WorldGeometry, WorldState},
};
use bevy::prelude::*;
use serde::Deserialize;
use serde_json::json;
use std::{
    collections::{HashMap, HashSet, hash_map::DefaultHasher},
    hash::{Hash, Hasher},
};

mod terrain;
#[cfg(test)]
mod tests;

pub(super) struct NpcFlightPlugin;
impl Plugin for NpcFlightPlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<Autopilots>()
            .init_resource::<TerrainRegistration>()
            .init_resource::<terrain::Support>()
            .add_systems(
                Update,
                (configure, terrain::support)
                    .chain()
                    .after(crate::entities::WorldServicesReady)
                    .after(VehicleUpdate),
            )
            .add_systems(FixedUpdate, drive.before(super::step_bodies));
    }
}

#[derive(Clone, Copy, Deserialize)]
struct Axes {
    collective: f32,
    yaw: f32,
    pitch: f32,
    roll: f32,
}
impl Axes {
    fn valid(self) -> bool {
        [self.collective, self.yaw, self.pitch, self.roll]
            .iter()
            .all(|v| v.is_finite() && (-1. ..=1.).contains(v))
    }
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Row {
    net_id: u32,
    active: bool,
    rotor_running: bool,
    controls: Axes,
}

#[derive(Resource, Default)]
pub(super) struct Autopilots {
    epoch: u64,
    managed: HashSet<u32>,
    active: HashSet<u32>,
    initialized: HashMap<Entity, u32>,
}
impl Autopilots {
    pub(super) fn managed(&self, id: u32) -> bool {
        self.managed.contains(&id)
    }
    pub(super) fn active(&self, id: u32) -> bool {
        self.active.contains(&id)
    }
}

fn drive(
    snapshot: Res<GameSnapshot>,
    epoch: Res<WorldSessionEpoch>,
    ui: Res<UiState>,
    mut pilots: ResMut<Autopilots>,
    mut bodies: Query<(Entity, &mut VehicleBody)>,
) {
    if pilots.epoch != epoch.0
        || snapshot.0["connected"] != true
        || !crate::network::owns_world(&snapshot.0)
    {
        *pilots = Autopilots {
            epoch: epoch.0,
            ..default()
        };
    }
    if snapshot.0["connected"] != true
        || !crate::network::owns_world(&snapshot.0)
        || simulation_paused(&ui)
    {
        return;
    }
    let rows: Vec<Row> = serde_json::from_value(snapshot.0["npcHelis"].clone()).unwrap_or_default();
    let previous = std::mem::take(&mut pilots.managed);
    pilots.active.clear();
    pilots
        .initialized
        .retain(|entity, _| bodies.get(*entity).is_ok());
    for (entity, mut body) in &mut bodies {
        let Some(row) = rows.iter().find(|row| row.net_id == body.net_id) else {
            if previous.contains(&body.net_id) && body.driver_player_id == 0 {
                body.controls = Controls {
                    engine: body.flags & 1 != 0,
                    ..default()
                };
            }
            continue;
        };
        if !data::definition(body.type_id).is_some_and(|d| d.spec.heli) {
            continue;
        }
        pilots.managed.insert(body.net_id);
        // SpawnRotorRunning/DebugInstantStart are initialization, never a per-tick
        // refill of damaged rotors or a restart of a stopped engine.
        if pilots.initialized.insert(entity, body.net_id) != Some(body.net_id) && row.rotor_running
        {
            body.flight.spawn_rotor_running();
        }
        if row.active && row.controls.valid() && body.driver_player_id == 0 && body.flags & 32 == 0
        {
            pilots.active.insert(body.net_id);
            body.controls = Controls {
                throttle: row.controls.collective,
                yaw: row.controls.yaw,
                pitch: row.controls.pitch,
                roll: row.controls.roll,
                engine: body.flags & 1 != 0,
                ..default()
            };
        } else if body.driver_player_id == 0 {
            body.controls = Controls {
                engine: body.flags & 1 != 0,
                ..default()
            };
        }
    }
}

#[derive(Resource, Default)]
struct TerrainRegistration {
    epoch: u64,
    map: String,
    revision: Option<u64>,
    signature: Option<u64>,
}
fn terrain_signature(geometry: &WorldGeometry) -> u64 {
    let mut hash = DefaultHasher::new();
    geometry.terrain.len().hash(&mut hash);
    for p in &geometry.terrain {
        p.origin.x.to_bits().hash(&mut hash);
        p.origin.y.to_bits().hash(&mut hash);
        p.cells.hash(&mut hash);
        p.spacing.to_bits().hash(&mut hash);
        for height in &p.heights {
            height.to_bits().hash(&mut hash);
        }
    }
    hash.finish()
}
fn configure(
    bridge: Res<CoreBridge>,
    config: Res<WorldConfig>,
    world: Res<WorldState>,
    geometry: Res<WorldGeometry>,
    model: Res<UiModel>,
    epoch: Res<WorldSessionEpoch>,
    mut registration: ResMut<TerrainRegistration>,
) {
    if !model.connected || !model.network.owns_world || !world.ready {
        return;
    }
    if registration.epoch != epoch.0 || registration.map != config.map_name {
        *registration = TerrainRegistration {
            epoch: epoch.0,
            map: config.map_name.clone(),
            ..default()
        };
    }
    if registration.revision == Some(geometry.revision) {
        return;
    }
    let signature = terrain_signature(&geometry);
    if registration.signature == Some(signature) {
        registration.revision = Some(geometry.revision);
        return;
    }
    let result = (|| -> anyhow::Result<()> {
        let mut min = Vec2::splat(f32::INFINITY);
        let mut max = Vec2::splat(f32::NEG_INFINITY);
        for p in &geometry.terrain {
            min = min.min(p.origin);
            max = max.max(p.origin + Vec2::splat(p.cells as f32 * p.spacing));
        }
        let bounds = min.is_finite().then(|| [min.x, max.x, min.y, max.y]);
        // One complete source height grid per request bounds JSON memory even on
        // large maps. No simulation tick runs between these synchronous uploads.
        bridge.request(
            "worldNpcHelis",
            json!({"mapBounds":bounds,
            "nodeFile":config.nodes_file(),"terrainPatchesReplace":true,"terrainPatches":[]}),
        )?;
        for p in &geometry.terrain {
            bridge.request("worldNpcHelis", json!({"terrainPatches":[{
                "origin":p.origin.to_array(),"cells":p.cells,"spacing":p.spacing,"heights":p.heights
            }]}))?;
        }
        Ok(())
    })();
    match result {
        Ok(()) => {
            registration.signature = Some(signature);
            registration.revision = Some(geometry.revision);
        }
        Err(error) => warn!("Original NPC helicopter terrain: {error}"),
    }
}
