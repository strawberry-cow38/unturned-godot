//! The listen host renders the original authoritative AnimalField/AnimalAgent.
//! Brains live in the managed host. Native bodies only adopt their accepted pose.
use crate::{
    bridge::CoreBridge,
    content::ContentCache,
    entities::{ApplyInteractions, CoreEvent, ServiceManifest, WorldServicesReady},
    player::{GameSnapshot, WorldSessionEpoch, simulation_paused},
    rig::{RigAnimation, RigLibrary, RigPlayback, spawn_rig},
    settings::Settings,
    ui::{UiModel, UiState},
    world::{WorldConfig, WorldState},
};
use bevy::{mesh::skinning::SkinnedMeshInverseBindposes, prelude::*};
use bevy_rapier3d::prelude::*;
use serde::Deserialize;
use serde_json::json;
use std::collections::{HashMap, HashSet};

pub struct WildlifePlugin;
impl Plugin for WildlifePlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<Wildlife>().add_systems(
            Update,
            (register, sync, panic_audio)
                .chain()
                .after(WorldServicesReady)
                .after(ApplyInteractions)
                .before(RigAnimation),
        );
    }
}
#[derive(Resource, Default)]
struct Wildlife {
    entities: HashMap<u32, Entity>,
    epoch: u64,
    registered: bool,
    failed: bool,
    audio: HashMap<String, PanicBank>,
    last_event: u64,
}
#[derive(Component)]
pub struct AnimalBody {
    pub net_id: u32,
    pub species: u8,
    rig: Entity,
    capsule: Entity,
    dead: bool,
}
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct AnimalState {
    net_id: u32,
    species: u8,
    position: [f32; 3],
    yaw: f32,
    #[serde(default)]
    host_position: Option<[f32; 3]>,
    #[serde(default)]
    host_yaw: Option<f32>,
    clip: String,
    dead: bool,
    body_height: f32,
    radius: f32,
    rig: String,
    texture: String,
    rig_yaw_fix: f32,
}
impl AnimalState {
    fn pose(&self) -> Transform {
        Transform::from_translation(Vec3::from_array(
            self.host_position.unwrap_or(self.position),
        ))
        .with_rotation(Quat::from_rotation_y(
            self.host_yaw.unwrap_or(self.yaw).to_radians(),
        ))
    }
    fn valid(&self) -> bool {
        self.net_id != 0
            && self.position.iter().all(|v| v.is_finite())
            && self
                .host_position
                .is_none_or(|p| p.iter().all(|v| v.is_finite()))
            && self.yaw.is_finite()
            && self.host_yaw.is_none_or(f32::is_finite)
            && self.rig_yaw_fix.is_finite()
            && self.body_height.is_finite()
            && self.body_height > 0.
            && self.radius.is_finite()
            && self.radius > 0.
    }
}
fn register(
    bridge: Res<CoreBridge>,
    config: Res<WorldConfig>,
    world: Res<WorldState>,
    services: Res<ServiceManifest>,
    model: Res<UiModel>,
    epoch: Res<WorldSessionEpoch>,
    mut wildlife: ResMut<Wildlife>,
    mut commands: Commands,
) {
    reset_epoch(&mut wildlife, epoch.0, &mut commands);
    if wildlife.registered
        || wildlife.failed
        || !world.ready
        || !model.connected
        || services.map != config.map_name
    {
        return;
    }
    // InfiniteTerrain has no AnimalField in the original. The finite generated
    // island's fauna points are a separate, still-unported source path.
    if config.procedural_path.is_some() {
        wildlife.registered = true;
        return;
    }
    let Some(map) = &config.map_root else {
        return;
    };
    match bridge.request("worldWildlife", json!({"mapRoot":map})) {
        Ok(reply) => {
            wildlife.registered = true;
            info!("Original wildlife field registered: {}", reply["result"]);
        }
        Err(error) => {
            wildlife.failed = true;
            error!("Cannot register original wildlife: {error}");
        }
    }
}
fn reset_epoch(wildlife: &mut Wildlife, epoch: u64, commands: &mut Commands) {
    if wildlife.epoch == epoch {
        return;
    }
    for (_, entity) in wildlife.entities.drain() {
        commands.entity(entity).despawn();
    }
    wildlife.epoch = epoch;
    wildlife.registered = false;
    wildlife.failed = false;
    wildlife.last_event = 0;
}
#[allow(clippy::too_many_arguments)]
fn sync(
    mut commands: Commands,
    snapshot: Res<GameSnapshot>,
    epoch: Res<WorldSessionEpoch>,
    config: Res<WorldConfig>,
    ui: Res<UiState>,
    mut wildlife: ResMut<Wildlife>,
    assets: Res<AssetServer>,
    mut cache: ResMut<ContentCache>,
    mut library: ResMut<RigLibrary>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut bindposes: ResMut<Assets<SkinnedMeshInverseBindposes>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
    mut bodies: Query<(&mut AnimalBody, &mut Transform)>,
    mut rigs: Query<&mut RigPlayback>,
) {
    reset_epoch(&mut wildlife, epoch.0, &mut commands);
    let Some(rows) = snapshot.0["animals"].as_array() else {
        return;
    };
    let mut live = HashSet::new();
    for value in rows {
        let row = match serde_json::from_value::<AnimalState>(value.clone()) {
            Ok(row) if row.valid() => row,
            Ok(_) => {
                warn!("Ignored invalid animal transform");
                continue;
            }
            Err(error) => {
                warn!("Cannot read animal replica: {error}");
                continue;
            }
        };
        live.insert(row.net_id);
        if let Some(entity) = wildlife.entities.get(&row.net_id).copied() {
            if let Ok((mut body, mut transform)) = bodies.get_mut(entity) {
                *transform = row.pose();
                if row.dead && !body.dead {
                    commands.entity(body.capsule).insert(ColliderDisabled);
                    body.dead = true;
                }
                if let Ok(mut rig) = rigs.get_mut(body.rig) {
                    if !row.dead {
                        rig.play(&row.clip, true);
                    }
                    // Shipped animal rigs have no ragdoll entries. Original
                    // RagdollStart therefore stops animation without bodies.
                    rig.paused = row.dead || simulation_paused(&ui);
                }
            }
            continue;
        }
        let material = cache.material(Some(&row.texture), false, &assets, &mut materials);
        let rig = match spawn_rig(
            &mut commands,
            &mut library,
            &mut meshes,
            &mut bindposes,
            &config.content_root.join(&row.rig),
            material,
            Transform::from_rotation(Quat::from_rotation_y(row.rig_yaw_fix.to_radians())),
            false,
            &row.clip,
        ) {
            Ok(rig) => rig,
            Err(error) => {
                warn!("Cannot build animal {}: {error}", row.net_id);
                continue;
            }
        };
        let body = commands
            .spawn((
                Name::new(format!(
                    "Animal {} #{}",
                    row.rig.trim_end_matches("_rig.json"),
                    row.net_id
                )),
                row.pose(),
                Visibility::default(),
                RigidBody::KinematicPositionBased,
            ))
            .id();
        // Capsule height includes both hemispheres in Godot; Rapier receives
        // only the half-length of its central segment.
        let height = row.body_height.max(0.6);
        let capsule = commands
            .spawn((
                source_capsule(height, row.radius),
                Transform::from_xyz(0., height * 0.5, 0.),
                CollisionGroups::new(Group::GROUP_5, Group::GROUP_2),
                ChildOf(body),
            ))
            .id();
        if row.dead {
            commands.entity(capsule).insert(ColliderDisabled);
        }
        commands.entity(rig).insert(ChildOf(body));
        commands.entity(body).insert(AnimalBody {
            net_id: row.net_id,
            species: row.species,
            rig,
            capsule,
            dead: row.dead,
        });
        // Deferred insertion completes before RigAnimation; a corpse loaded
        // after death must not spend a frame playing its saved locomotion.
        let paused = row.dead || simulation_paused(&ui);
        commands.queue(move |world: &mut World| {
            if let Some(skin) = world.get::<crate::rig::RigSkin>(rig).map(|skin| skin.0) {
                world
                    .entity_mut(skin)
                    .insert(bevy::camera::visibility::VisibilityRange {
                        start_margin: 0.0..0.0,
                        end_margin: 95.0..95.0,
                        use_aabb: false,
                    });
            }
            if let Some(mut playback) = world.get_mut::<RigPlayback>(rig) {
                playback.paused = paused;
            }
        });
        wildlife.entities.insert(row.net_id, body);
    }
    wildlife.entities.retain(|id, entity| {
        if live.contains(id) {
            true
        } else {
            commands.entity(*entity).despawn();
            false
        }
    });
}
fn source_capsule(height: f32, radius: f32) -> Collider {
    Collider::capsule_y((height * 0.5 - radius).max(0.), radius)
}
#[derive(Default)]
struct PanicBank {
    clips: Vec<String>,
    last: usize,
}
impl PanicBank {
    fn load(root: &std::path::Path, rig: &str) -> Self {
        let prefix = format!("{rig}_panic_");
        let mut clips = std::fs::read_dir(root.join("audio/animals"))
            .into_iter()
            .flatten()
            .filter_map(Result::ok)
            .filter_map(|entry| {
                let name = entry.file_name().to_string_lossy().into_owned();
                (name.starts_with(&prefix) && (name.ends_with(".wav") || name.ends_with(".ogg")))
                    .then(|| format!("audio/animals/{name}"))
            })
            .collect::<Vec<_>>();
        clips.sort_unstable();
        Self { clips, last: 0 }
    }
    fn pick(&mut self, seed: u64) -> Option<&str> {
        if self.clips.is_empty() {
            return None;
        }
        if self.clips.len() > 1 {
            // Same no-consecutive-repeat bank selection as GameAudio.Pick.
            let mut index = seed as usize % (self.clips.len() - 1);
            if index >= self.last {
                index += 1;
            }
            self.last = index;
        }
        Some(&self.clips[self.last])
    }
}
fn panic_audio(
    mut events: MessageReader<CoreEvent>,
    mut wildlife: ResMut<Wildlife>,
    config: Res<WorldConfig>,
    snapshot: Res<GameSnapshot>,
    settings: Res<Settings>,
    assets: Res<AssetServer>,
    mut commands: Commands,
) {
    for event in events.read() {
        if event.sequence <= wildlife.last_event {
            continue;
        }
        wildlife.last_event = event.sequence;
        if event.kind != "animalPanic" {
            continue;
        }
        let Some(position) = crate::player::vector(&event.data["position"]) else {
            continue;
        };
        let net_id = event.data["netId"].as_u64().unwrap_or(0);
        let rig = event.data["rig"].as_str().or_else(|| {
            snapshot.0["animals"]
                .as_array()?
                .iter()
                .find(|row| row["netId"].as_u64() == Some(net_id))?["rig"]
                .as_str()?
                .strip_suffix("_rig.json")
        });
        let Some(rig) = rig else {
            continue;
        };
        let bank = wildlife
            .audio
            .entry(rig.into())
            .or_insert_with(|| PanicBank::load(&config.content_root, rig));
        let Some(path) = bank.pick(event.sequence.wrapping_mul(6364136223846793005)) else {
            continue;
        };
        let voice = crate::vehicle::spawn_ranged_audio(
            &mut commands,
            &assets,
            None,
            path,
            false,
            10_f32.powf(-2. / 20.),
            1.,
            8.,
            90.,
            settings.master_volume,
        );
        commands.entity(voice).insert((
            Name::new("Animal panic"),
            Transform::from_translation(position),
        ));
    }
}

#[cfg(test)]
mod tests;
