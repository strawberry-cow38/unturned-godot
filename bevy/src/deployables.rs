//! Source DeployableReplicaView: accepted objects own their only rendered body.
//! Specialized fluid/storage/fixture bodies are implemented separately, never
//! approximated by the ordinary Deployable.Spawn mesh.
mod engine;
mod fixture;
mod geometry;
mod interaction;
mod lamps;
mod pickup;
mod placement;
mod ports;
pub(crate) use ports::{ToolHud, ToolReadouts};
#[cfg(test)]
mod power_tests;
mod presentation;
mod switch;
/// Wire.SetPoints / TowRope segment basis, shared with the vehicle rope.
pub(crate) use wires::segment::pose as segment_pose;
#[cfg(test)]
mod tests;
mod turbine;
mod wires;

use crate::{
    bridge::CoreBridge,
    content::ContentCache,
    entities::{ItemCatalog, WorldServicesReady},
    player::{GameSnapshot, WorldSessionEpoch},
    ui::UiModel,
    world::{WorldConfig, WorldGeometry},
};
use bevy::prelude::*;
use bevy_rapier3d::prelude::*;
use serde::Deserialize;
use serde_json::{Value, json};
use std::collections::{HashMap, HashSet};

fn no_depth() -> f32 {
    -1.
}
#[derive(Clone, Copy, Debug, Default, Deserialize)]
pub(crate) struct SourceVector {
    x: f32,
    y: f32,
    z: f32,
}
impl From<SourceVector> for Vec3 {
    fn from(v: SourceVector) -> Self {
        Self::new(v.x, v.y, v.z)
    }
}

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct Definition {
    id: u16,
    name: String,
    model: Option<String>,
    size: SourceVector,
    offset: f32,
    /// ItemBarricadeAsset Radius / Range: the placement clearance sphere and aim reach.
    #[serde(default)]
    radius: f32,
    #[serde(default)]
    range: f32,
    /// Submersible placement band (-1 = no water requirement).
    #[serde(default = "no_depth")]
    water_depth_min: f32,
    #[serde(default = "no_depth")]
    water_depth_max: f32,
    /// `.dat` PlacementAudioClip stem under `sounds/`.
    #[serde(default)]
    place_sound: Option<String>,
    /// A LocalOnly placeable door (DoorDeploy); never a server body.
    #[serde(default)]
    door_prop: Option<String>,
    upright: bool,
    mount: String,
    fixture: String,
    #[serde(default)]
    fluid: Value,
    is_storage: bool,
    is_wind_turbine: bool,
    proc_box: bool,
    mesh_euler: SourceVector,
    #[serde(default)]
    health: f32,
    #[serde(default)]
    fuel: f32,
    #[serde(default)]
    is_battery: bool,
    #[serde(default)]
    is_switch: bool,
    #[serde(default)]
    is_item_configurable: bool,
    #[serde(default)]
    ports: Vec<wires::Port>,
    /// Source `DeployLight`s a powered consumer lights (Spotlight, Cagelight).
    #[serde(default)]
    lights: Vec<lamps::Light>,
    #[serde(default)]
    energy_max: f32,
}
impl Definition {
    /// The `Deployable.Spawn` branch of `DeployableReplicaView`: the turbine is
    /// a Deployable body with its procedural `BuildTurbine` art.
    fn ordinary(&self) -> bool {
        self.fixture == "None" && self.fluid.is_null() && !self.is_storage
    }
}

#[derive(Component)]
pub(crate) struct Deployed {
    pub(crate) net_id: u32,
    pub(crate) def_id: u16,
}

/// Read-only capture evidence for production focus/hold integration checks.
#[derive(Resource, Default, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct InteractionDiagnostics {
    gate: String,
    camera: Option<[f32; 3]>,
    direction: Option<[f32; 3]>,
    hit: Option<String>,
    ray_distance: Option<f32>,
    offered: Option<u32>,
    selected: Option<u32>,
    progress: f32,
}

#[derive(Clone, Copy, PartialEq)]
struct Pose {
    point: Vec3,
    yaw: f32,
    mount_up: bool,
}
impl Pose {
    fn read(row: &Value) -> Option<(u32, u16, Self)> {
        let id = u32::try_from(row["netId"].as_u64()?)
            .ok()
            .filter(|id| *id != 0)?;
        let def = u16::try_from(row["defId"].as_u64()?).ok()?;
        let point = crate::player::vector(&row["position"])?;
        let yaw = row["yaw"].as_f64()? as f32;
        yaw.is_finite().then_some((
            id,
            def,
            Self {
                point,
                yaw,
                mount_up: row["mountUp"].as_bool().unwrap_or(false),
            },
        ))
    }
}
struct Instance {
    entity: Entity,
    def_id: u16,
    pose: Pose,
}
#[derive(Resource, Default)]
struct Replicas {
    instances: HashMap<u32, Instance>,
    art: HashMap<u16, presentation::Art>,
    epoch: u64,
    published: HashMap<u32, (Pose, u16, u64)>,
    error: Option<String>,
}
pub(crate) struct DeployablePlugin;
impl Plugin for DeployablePlugin {
    fn build(&self, app: &mut App) {
        interaction::install(app);
        wires::install(app);
        ports::install(app);
        engine::install(app);
        placement::install(app);
        app.init_resource::<Replicas>()
            .add_systems(
                Update,
                sync.after(WorldServicesReady)
                    .before(crate::entities::CollectInteractions),
            )
            .add_systems(
                Update,
                (
                    switch::update,
                    turbine::spin,
                    lamps::update.after(wires::refresh),
                )
                    .after(sync),
            );
    }
}

fn sync(
    mut commands: Commands,
    snapshot: Res<GameSnapshot>,
    catalog: Res<ItemCatalog>,
    config: Res<WorldConfig>,
    epoch: Res<WorldSessionEpoch>,
    geometry: Res<WorldGeometry>,
    bridge: Res<CoreBridge>,
    mut model: ResMut<UiModel>,
    mut replicas: ResMut<Replicas>,
    mut cache: ResMut<ContentCache>,
    assets: Res<AssetServer>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
) {
    if replicas.epoch != epoch.0 {
        for (_, instance) in replicas.instances.drain() {
            commands.entity(instance.entity).despawn();
        }
        replicas.published.clear();
        replicas.epoch = epoch.0;
    }
    let mut live = HashSet::new();
    // Fixture endpoints retire with `live` but never upload authority geometry.
    let mut endpoints = HashSet::new();
    if model.connected {
        for row in snapshot.0["deployables"].as_array().into_iter().flatten() {
            let Some((id, def_id, pose)) = Pose::read(row) else {
                continue;
            };
            let Some(def) = catalog
                .deployables
                .iter()
                .find(|d| d.id == def_id && (d.ordinary() || fixture::endpoint(d)))
            else {
                continue;
            };
            if fixture::endpoint(def) {
                live.insert(id);
                endpoints.insert(id);
                let transform = fixture::transform(pose);
                // The look box undoes the root basis, so a moved fixture is rebuilt whole.
                match replicas.instances.get(&id) {
                    Some(instance) if instance.def_id == def_id && instance.pose == pose => {}
                    _ => {
                        if let Some(old) = replicas.instances.remove(&id) {
                            commands.entity(old.entity).despawn();
                        }
                        let entity = fixture::spawn(&mut commands, def, id, transform);
                        replicas.instances.insert(
                            id,
                            Instance {
                                entity,
                                def_id,
                                pose,
                            },
                        );
                    }
                }
                continue;
            }
            if !replicas.art.contains_key(&def_id) {
                match presentation::load(
                    def,
                    &config.content_root,
                    &mut cache,
                    &assets,
                    &mut meshes,
                    &mut materials,
                ) {
                    Ok(art) => {
                        replicas.art.insert(def_id, art);
                    }
                    Err(error) => {
                        let message = format!("Cannot materialize {}: {error}", def.name);
                        if replicas.error.as_ref() != Some(&message) {
                            warn!("{message}");
                        }
                        replicas.error = Some(message);
                        continue;
                    }
                }
            }
            live.insert(id);
            let art = &replicas.art[&def_id];
            let transform = geometry::seat(def, art.bounds, pose);
            if let Some(instance) = replicas.instances.get(&id) {
                if instance.def_id == def_id {
                    if instance.pose != pose {
                        commands.entity(instance.entity).insert(transform);
                        replicas.instances.get_mut(&id).unwrap().pose = pose;
                    }
                    continue;
                }
                commands.entity(instance.entity).despawn();
            }
            let entity = presentation::spawn(
                &mut commands,
                def,
                art,
                id,
                transform,
                row["toggledOn"] == true,
            );
            replicas.instances.insert(
                id,
                Instance {
                    entity,
                    def_id,
                    pose,
                },
            );
        }
    }
    let gone: Vec<_> = replicas
        .instances
        .keys()
        .filter(|id| !live.contains(*id))
        .copied()
        .collect();
    for id in gone {
        commands
            .entity(replicas.instances.remove(&id).unwrap().entity)
            .despawn();
    }
    if !model.connected || !crate::network::owns_world(&snapshot.0) {
        replicas.published.clear();
        return;
    }
    let result = (|| -> anyhow::Result<()> {
        let gone: Vec<_> = replicas
            .published
            .keys()
            .filter(|id| !live.contains(*id))
            .copied()
            .collect();
        for id in gone {
            bridge.request(
                "worldDynamicGeometry",
                json!({"key":format!("deployable:{id}"),"meshes":[]}),
            )?;
            replicas.published.remove(&id);
        }
        for id in live.difference(&endpoints) {
            let id = *id;
            let instance = &replicas.instances[&id];
            let stamp = (instance.pose, instance.def_id, geometry.revision);
            if replicas.published.get(&id) == Some(&stamp) {
                continue;
            }
            let def = catalog
                .deployables
                .iter()
                .find(|d| d.id == instance.def_id)
                .unwrap();
            let bounds = replicas.art[&instance.def_id].bounds;
            let transform = geometry::seat(def, bounds, instance.pose);
            bridge.request(
                "worldDynamicGeometry",
                json!({"key":format!("deployable:{id}"),
                "meshes":[geometry::authority(bounds, transform, id)]}),
            )?;
            replicas.published.insert(id, stamp);
        }
        Ok(())
    })();
    if let Err(error) = result {
        let message = format!("Deployable collision: {error}");
        if replicas.error.as_ref() != Some(&message) {
            warn!("{message}");
            model.notice(message.clone());
        }
        replicas.error = Some(message);
    } else {
        replicas.error = None;
    }
}
