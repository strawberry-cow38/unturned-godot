//! Current ZombieChunkField's HOT bodies: one native 50 Hz physical solve.
//! The managed field owns promotion, sensing, wardrobe, damage and retirement.
use crate::{
    bridge::CoreBridge,
    character::{Appearance, ragdoll::StartRagdoll},
    entities::{ApplyInteractions, ServiceManifest, WorldServicesReady},
    player::{GameSnapshot, PublishPlayer, WorldSessionEpoch, simulation_paused},
    rig::{RigAnimation, RigFixedAnimation, RigFixedUpdate, RigLibrary, RigPlayback, spawn_rig},
    ui::{UiModel, UiState},
    world::{WorldConfig, WorldState},
};
use bevy::{
    camera::visibility::VisibilityRange, mesh::skinning::SkinnedMeshInverseBindposes, prelude::*,
};
use bevy_rapier3d::prelude::*;
use serde::{Deserialize, Serialize};
use serde_json::json;
use std::collections::{HashMap, HashSet};

pub struct ZombiePlugin;
#[derive(SystemSet, Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct ZombiePublish;
impl Plugin for ZombiePlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<Zombies>()
            .add_systems(
                Update,
                (register, sync)
                    .chain()
                    .after(WorldServicesReady)
                    .after(ApplyInteractions)
                    .before(RigAnimation),
            )
            .add_systems(FixedUpdate, drive.before(PhysicsSet::SyncBackend))
            .add_systems(
                FixedUpdate,
                (resolved, publish)
                    .chain()
                    .in_set(ZombiePublish)
                    .after(PhysicsSet::Writeback)
                    .before(PublishPlayer)
                    .before(RigFixedUpdate),
            );
    }
}
#[derive(Resource, Default)]
struct Zombies {
    entities: HashMap<u32, Entity>,
    epoch: u64,
    registered: bool,
    failed: bool,
}
#[derive(Clone, Copy, Debug, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", default)]
struct Motion {
    window_position: [f32; 3],
    window_time: f32,
    escape_time: f32,
    position_initialized: bool,
}
impl Motion {
    fn drive(&mut self, desired: Vec2, normals: Vec3, dt: f32) -> Vec2 {
        if self.escape_time <= 0. || desired.length_squared() <= 0.04 {
            return desired;
        }
        self.escape_time -= dt;
        let n = normals.xz();
        if n.length_squared() <= 0.01 {
            return desired;
        }
        let n = n.normalize();
        let mut tangent = Vec2::new(-n.y, n.x);
        if tangent.dot(desired) < 0. {
            tangent = -tangent;
        }
        (tangent * 0.9 + desired.normalize() * 0.1).normalize() * desired.length()
    }
    fn record(&mut self, position: Vec3, desired: Vec2, dt: f32) {
        if !self.position_initialized {
            self.window_position = position.to_array();
            self.position_initialized = true;
        }
        self.window_time += dt;
        if self.window_time >= 1. {
            let progress = position
                .xz()
                .distance(Vec3::from_array(self.window_position).xz());
            if self.escape_time <= 0. && desired.length_squared() > 0.2 && progress < 0.5 {
                self.escape_time = 1.;
            }
            self.window_position = position.to_array();
            self.window_time = 0.;
        }
    }
}
#[derive(Component)]
pub struct ZombieBody {
    pub net_id: u32,
    rig: Entity,
    dead: bool,
    variant: u8,
    velocity: Vec3,
    desired: Vec2,
    yaw: f32,
    motion: Motion,
    stepping: bool,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Replica {
    net_id: u32,
    position: [f32; 3],
    yaw: f32,
    has_body: bool,
    host_position: Option<[f32; 3]>,
    host_yaw: Option<f32>,
    velocity: Option<[f32; 3]>,
    desired_velocity: Option<[f32; 2]>,
    dead: bool,
    variant: Option<u8>,
    shirt_id: Option<i32>,
    pants_id: Option<i32>,
    hat_id: Option<i32>,
    gear_id: Option<i32>,
    motion: Option<Motion>,
    death_impulse: Option<[f32; 3]>,
}
impl Replica {
    fn position(&self) -> Vec3 {
        Vec3::from_array(self.host_position.unwrap_or(self.position))
    }
    fn yaw(&self) -> f32 {
        self.host_yaw.unwrap_or(self.yaw).to_radians()
    }
    fn valid(&self) -> bool {
        self.has_body && self.net_id != 0 && self.position().is_finite() && self.yaw().is_finite()
    }
}
pub fn shift_origin(world: &mut World, shift: Vec3) {
    for mut body in world.query::<&mut ZombieBody>().iter_mut(world) {
        body.motion.window_position =
            (Vec3::from_array(body.motion.window_position) - shift).to_array();
    }
}
fn reset(zombies: &mut Zombies, epoch: u64, commands: &mut Commands) {
    if zombies.epoch == epoch {
        return;
    }
    for (_, entity) in zombies.entities.drain() {
        commands.entity(entity).try_despawn();
    }
    zombies.epoch = epoch;
    zombies.registered = false;
    zombies.failed = false;
}
fn register(
    mut commands: Commands,
    mut zombies: ResMut<Zombies>,
    epoch: Res<WorldSessionEpoch>,
    world: Res<WorldState>,
    config: Res<WorldConfig>,
    model: Res<UiModel>,
    services: Res<ServiceManifest>,
    bridge: Res<CoreBridge>,
) {
    reset(&mut zombies, epoch.0, &mut commands);
    if !model.network.owns_world {return;}
    if zombies.registered
        || zombies.failed
        || !world.ready
        || !model.connected
        || services.map != config.map_name
    {
        return;
    }
    // InfiniteTerrain's RegionStreamer does not currently instantiate this field.
    // Finite generated-map points belong to the separate ProcIslandSpawn path.
    if config.procedural_path.is_some() {
        zombies.registered = true;
        return;
    }
    let Some(root) = &config.map_root else {
        return;
    };
    match bridge.request("worldZombies", json!({"mapRoot":root})) {
        Ok(reply) => {
            zombies.registered = true;
            info!("Original zombie field registered: {}", reply["result"]);
        }
        Err(error) => {
            zombies.failed = true;
            error!("Cannot register source zombie field: {error}");
        }
    }
}
fn capsule() -> Collider {
    Collider::capsule(Vec3::Y * 0.4, Vec3::Y * 1.4, 0.4)
}
fn controller() -> KinematicCharacterController {
    KinematicCharacterController {
        offset: CharacterLength::Absolute(0.001),
        slide: true,
        autostep: None,
        max_slope_climb_angle: 55_f32.to_radians(),
        min_slope_slide_angle: 55_f32.to_radians(),
        snap_to_ground: Some(CharacterLength::Absolute(0.5)),
        filter_groups: Some(CollisionGroups::new(Group::GROUP_6, Group::GROUP_1)),
        apply_impulse_to_dynamic_bodies: false,
        ..default()
    }
}
#[allow(clippy::too_many_arguments)]
fn sync(
    mut commands: Commands,
    snapshot: Res<GameSnapshot>,
    epoch: Res<WorldSessionEpoch>,
    config: Res<WorldConfig>,
    ui: Res<UiState>,
    mut zombies: ResMut<Zombies>,
    mut library: ResMut<RigLibrary>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut bindposes: ResMut<Assets<SkinnedMeshInverseBindposes>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
    mut bodies: Query<&mut ZombieBody>,
    mut rigs: Query<&mut RigPlayback>,
) {
    reset(&mut zombies, epoch.0, &mut commands);
    let Some(rows) = snapshot.0["zombies"].as_array() else {
        return;
    };
    let mut live = HashSet::new();
    for value in rows {
        let row = match serde_json::from_value::<Replica>(value.clone()) {
            Ok(row) if row.valid() => row,
            Ok(_) => continue,
            Err(error) => {
                warn!("Cannot read source zombie body: {error}");
                continue;
            }
        };
        live.insert(row.net_id);
        if let Some(entity) = zombies.entities.get(&row.net_id).copied() {
            if let Ok(mut body) = bodies.get_mut(entity) {
                if row.dead && !body.dead {
                    body.dead = true;
                    commands
                        .entity(entity)
                        .insert(ColliderDisabled)
                        .remove::<KinematicCharacterController>();
                    commands
                        .entity(body.rig)
                        .insert(StartRagdoll(Vec3::from_array(
                            row.death_impulse.unwrap_or([0.; 3]),
                        )));
                }
                if let Ok(mut rig) = rigs.get_mut(body.rig) {
                    rig.paused = row.dead || simulation_paused(&ui);
                }
            }
            continue;
        }
        let variant = row.variant.unwrap_or(0) % 4;
        let rig = match spawn_rig(
            &mut commands,
            &mut library,
            &mut meshes,
            &mut bindposes,
            &config.content_root.join("rig.json"),
            materials.add(StandardMaterial::default()),
            Transform::IDENTITY,
            false,
            &format!("Move_{variant}"),
        ) {
            Ok(rig) => rig,
            Err(error) => {
                warn!("Cannot build original zombie {}: {error}", row.net_id);
                continue;
            }
        };
        let body = commands
            .spawn((
                Name::new(format!("Source zombie #{}", row.net_id)),
                Transform::from_translation(row.position())
                    .with_rotation(Quat::from_rotation_y(row.yaw())),
                Visibility::Inherited,
                RigidBody::KinematicPositionBased,
                capsule(),
                controller(),
                CollisionGroups::new(Group::GROUP_6, Group::GROUP_1),
                ZombieBody {
                    net_id: row.net_id,
                    rig,
                    dead: row.dead,
                    variant,
                    velocity: Vec3::from_array(row.velocity.unwrap_or([0.; 3])),
                    desired: Vec2::from_array(row.desired_velocity.unwrap_or([0.; 2])),
                    yaw: row.yaw(),
                    motion: row.motion.unwrap_or_default(),
                    stepping: false,
                },
            ))
            .id();
        commands.entity(rig).insert((
            ChildOf(body),
            RigFixedAnimation,
            Appearance {
                skin: Color::srgb(0.588, 0.620, 0.502),
                shirt: row.shirt_id.unwrap_or(-1),
                pants: row.pants_id.unwrap_or(-1),
                gear: [row.hat_id, row.gear_id]
                    .into_iter()
                    .flatten()
                    .filter(|id| *id >= 0)
                    .collect(),
                face: Some(19),
                ..default()
            },
        ));
        if row.dead {
            commands
                .entity(body)
                .insert(ColliderDisabled)
                .remove::<KinematicCharacterController>();
            commands.entity(rig).insert(StartRagdoll(Vec3::from_array(
                row.death_impulse.unwrap_or([0.; 3]),
            )));
        }
        let paused = row.dead || simulation_paused(&ui);
        commands.queue(move |world: &mut World| {
            if let Some(skin) = world.get::<crate::rig::RigSkin>(rig).map(|s| s.0) {
                world.entity_mut(skin).insert(VisibilityRange {
                    start_margin: 0.0..0.0,
                    end_margin: 95.0..95.0,
                    use_aabb: false,
                });
            }
            if let Some(mut playback) = world.get_mut::<RigPlayback>(rig) {
                playback.paused = paused;
            }
        });
        zombies.entities.insert(row.net_id, body);
    }
    zombies.entities.retain(|id, entity| {
        if live.contains(id) {
            true
        } else {
            commands.entity(*entity).try_despawn();
            false
        }
    });
}
fn drive(
    time: Res<Time<Fixed>>,
    snapshot: Res<GameSnapshot>,
    ui: Res<UiState>,
    mut bodies: Query<(
        &mut ZombieBody,
        &mut Transform,
        &mut KinematicCharacterController,
        Option<&KinematicCharacterControllerOutput>,
    )>,
) {
    let dt = time.delta_secs();
    let rows = snapshot.0["zombies"].as_array();
    for (mut body, mut transform, mut controller, output) in &mut bodies {
        controller.translation = None;
        body.stepping = false;
        if body.dead || simulation_paused(&ui) {
            continue;
        }
        let Some(row) = rows.and_then(|r| {
            r.iter()
                .find(|r| r["netId"].as_u64() == Some(body.net_id as u64))
        }) else {
            continue;
        };
        if row["dead"].as_bool() == Some(true) {
            continue;
        }
        let desired = row["desiredVelocity"]
            .as_array()
            .and_then(|v| {
                Some(Vec2::new(
                    v.first()?.as_f64()? as f32,
                    v.get(1)?.as_f64()? as f32,
                ))
            })
            .filter(|v| v.is_finite())
            .unwrap_or(Vec2::ZERO);
        body.desired = desired;
        let normals = output.map_or(Vec3::ZERO, |o| {
            o.collisions
                .iter()
                .map(|c| c.hit.details.map_or(Vec3::ZERO, |d| d.normal1))
                .sum()
        });
        let drive = body.motion.drive(desired, normals, dt);
        body.velocity.x = drive.x;
        body.velocity.z = drive.y;
        body.velocity.y = if output.is_some_and(|o| o.grounded) {
            0.
        } else {
            body.velocity.y - 22. * dt
        };
        controller.translation = Some(body.velocity * dt);
        body.stepping = true;
        if drive.length_squared() > 0.04 {
            let want = (-drive.x).atan2(-drive.y);
            let delta = (want - body.yaw + std::f32::consts::PI).rem_euclid(std::f32::consts::TAU)
                - std::f32::consts::PI;
            body.yaw += delta * (1. - (-10. * dt).exp());
            transform.rotation = Quat::from_rotation_y(body.yaw);
        }
    }
}
fn resolved(
    time: Res<Time<Fixed>>,
    ui: Res<UiState>,
    mut bodies: Query<(
        &mut ZombieBody,
        &Transform,
        &KinematicCharacterControllerOutput,
    )>,
    mut rigs: Query<&mut RigPlayback>,
) {
    if simulation_paused(&ui) {
        return;
    }
    let dt = time.delta_secs();
    for (mut body, pose, output) in &mut bodies {
        if body.dead || !body.stepping {
            continue;
        }
        body.velocity = output.effective_translation / dt;
        if output.grounded {
            body.velocity.y = 0.;
        }
        let desired = body.desired;
        body.motion.record(pose.translation, desired, dt);
        if let Ok(mut rig) = rigs.get_mut(body.rig) {
            let speed = body.velocity.xz().length();
            rig.play(
                &format!(
                    "{}_{}",
                    if speed < 0.2 { "Idle" } else { "Move" },
                    body.variant
                ),
                true,
            );
            rig.speed = if speed < 0.2 {
                1.
            } else {
                (speed / 6.87).clamp(0.1, 3.)
            };
        }
    }
}
fn publish(
    ui: Res<UiState>,
    zombies: Res<Zombies>,
    bodies: Query<(&ZombieBody, &Transform)>,
    bridge: Res<CoreBridge>,
) {
    if simulation_paused(&ui) || !zombies.registered || zombies.failed {
        return;
    }
    let rows:Vec<_>=bodies.iter().filter(|(b,_)|!b.dead&&b.stepping).map(|(b,t)|json!({"netId":b.net_id,"position":t.translation.to_array(),"velocity":b.velocity.to_array(),"yaw":b.yaw.to_degrees(),"motion":b.motion})).collect();
    if rows.is_empty() {
        return;
    }
    if let Err(error) = bridge.request("worldZombieState", json!({"bodies":rows})) {
        warn!("Cannot publish zombie physical result: {error}");
    }
}
#[cfg(test)]
mod tests;
