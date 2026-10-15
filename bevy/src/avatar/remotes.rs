//! Original RemotePlayers body replication. Only accepted client replicas create
//! puppets; no local-owner duplicate and no presentation-to-authority writes.
use crate::{
    character::{Appearance, CharacterLayers},
    entities::ItemCatalog,
    player::{GameSnapshot, PlayerUpdate, StanceHeights, WorldSessionEpoch},
    rig::{RigAnimation, RigLibrary, RigPlayback, spawn_rig},
    world::WorldConfig,
};
use bevy::{
    camera::visibility::RenderLayers, mesh::skinning::SkinnedMeshInverseBindposes, prelude::*,
};
use bevy_rapier3d::prelude::*;
use serde_json::Value;
use std::collections::{HashMap, HashSet};

mod actions;
mod held;
mod lights;

pub(super) struct RemotePlayersPlugin;
#[derive(SystemSet, Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub(crate) struct RemotePlayersUpdate;
impl Plugin for RemotePlayersPlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<Roster>()
            .init_resource::<crate::content::ContentCache>()
            .init_resource::<held::Library>()
            .init_resource::<actions::Events>()
            .init_resource::<crate::viewmodel::lights::EmissionMasks>()
            .add_message::<crate::entities::CoreEvent>()
            .add_systems(
                Update,
                (sync, update, held::sync, actions::receive)
                    .chain()
                    .in_set(RemotePlayersUpdate)
                    .after(PlayerUpdate)
                    .before(RigAnimation),
            )
            .add_systems(
                Update,
                actions::animate
                    .after(RigAnimation)
                    .before(crate::character::CharacterUpdate),
            )
            .add_systems(
                Update,
                lights::update.after(crate::character::CharacterUpdate),
            );
    }
}
#[derive(Component)]
pub(crate) struct RemotePlayer {
    pub id: u16,
    pub alive: bool,
    speed: f32,
    hull: Entity,
    height: f32,
    seated: bool,
    pub(crate) foot_motion: FootMotion,
}
#[derive(Clone, Copy, Default)]
pub(crate) struct FootMotion {
    pub speed: f32,
    pub vertical_speed: f32,
    pub stance: &'static str,
    pub seated: bool,
    pub snap: bool,
}
#[cfg(test)]
impl RemotePlayer {
    pub(crate) fn for_footstep_test(foot_motion: FootMotion) -> Self {
        Self {
            id: 2,
            alive: true,
            speed: foot_motion.speed,
            hull: Entity::PLACEHOLDER,
            height: 2.,
            seated: foot_motion.seated,
            foot_motion,
        }
    }
}
#[derive(Resource, Default)]
pub(crate) struct Roster {
    epoch: u64,
    roots: HashMap<u16, Entity>,
}
#[derive(Component)]
struct RemoteHull;

fn replicas(state: &Value) -> impl Iterator<Item = (u16, &Value)> {
    let owner = state["playerId"].as_u64().unwrap_or(0);
    state["players"]
        .as_array()
        .into_iter()
        .flatten()
        .filter_map(move |row| {
            let id = row["playerId"].as_u64()?;
            if id == 0 || id == owner || id > u16::MAX as u64 {
                return None;
            }
            crate::player::vector(&row["position"])?;
            Some((id as u16, row))
        })
}

#[allow(clippy::too_many_arguments)]
fn sync(
    mut commands: Commands,
    snapshot: Res<GameSnapshot>,
    epoch: Res<WorldSessionEpoch>,
    mut roster: ResMut<Roster>,
    existing: Query<(), With<RemotePlayer>>,
    config: Res<WorldConfig>,
    catalog: Res<ItemCatalog>,
    mut rigs: ResMut<RigLibrary>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut binds: ResMut<Assets<SkinnedMeshInverseBindposes>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
) {
    let connected = snapshot.0["connected"].as_bool().unwrap_or(false);
    if roster.epoch != epoch.0 || !connected {
        for (_, root) in roster.roots.drain() {
            commands.entity(root).try_despawn();
        }
        roster.epoch = epoch.0;
    }
    if !connected {
        return;
    }
    let live: HashSet<_> = replicas(&snapshot.0).map(|(id, _)| id).collect();
    roster.roots.retain(|id, root| {
        if !live.contains(id) {
            commands.entity(*root).try_despawn();
            return false;
        }
        existing.contains(*root)
    });
    for (id, row) in replicas(&snapshot.0) {
        if roster.roots.contains_key(&id) {
            continue;
        }
        let position = crate::player::vector(&row["position"]).unwrap();
        let body = match spawn_rig(
            &mut commands,
            &mut rigs,
            &mut meshes,
            &mut binds,
            &config.content_root.join("rig.json"),
            materials.add(StandardMaterial::default()),
            Transform::from_translation(position),
            false,
            "Idle_Stand",
        ) {
            Ok(body) => body,
            Err(error) => {
                error!("Remote player {id}: {error}");
                continue;
            }
        };
        let height = StanceHeights::default().for_stance("stand");
        let hull = commands
            .spawn((
                RemoteHull,
                Collider::capsule_y(height * 0.5 - 0.35, 0.35),
                // Rapier filtering is symmetric: the hull contacts only the local
                // character, so a vehicle does not stop against a pedestrian.
                CollisionGroups::new(Group::GROUP_15, Group::GROUP_2),
                Transform::from_xyz(0., height * 0.5, 0.),
                ChildOf(body),
            ))
            .id();
        commands.entity(body).insert((
            Name::new(format!("Remote player {id}")),
            RemotePlayer {
                id,
                alive: true,
                speed: 0.,
                hull,
                height,
                seated: false,
                foot_motion: default(),
            },
            RigidBody::KinematicPositionBased,
            held::Held::default(),
            actions::Actions::default(),
            appearance(row, &catalog),
            CharacterLayers(RenderLayers::layer(0)),
        ));
        roster.roots.insert(id, body);
    }
}

fn appearance(row: &Value, catalog: &ItemCatalog) -> Appearance {
    super::outfit_parts(
        &row["worn"],
        &row["wornQuality"],
        row["profile"]["face"].as_u64().unwrap_or(0).min(31) as u8,
        false,
        catalog,
    )
}
fn stance(row: &Value) -> &'static str {
    match row["stance"].as_u64().unwrap_or(0) {
        1 => "sprint",
        2 => "crouch",
        3 => "prone",
        4 => "sitting",
        _ => "stand",
    }
}

fn update(
    mut commands: Commands,
    snapshot: Res<GameSnapshot>,
    time: Res<Time>,
    catalog: Res<ItemCatalog>,
    mut puppets: Query<
        (
            &mut Transform,
            &mut RigPlayback,
            &mut Appearance,
            &mut RemotePlayer,
        ),
        Without<RemoteHull>,
    >,
    mut hulls: Query<&mut Transform, (With<RemoteHull>, Without<RemotePlayer>)>,
) {
    let rows: HashMap<_, _> = replicas(&snapshot.0).collect();
    let mut seated = HashSet::new();
    for car in snapshot.0["vehicles"].as_array().into_iter().flatten() {
        if let Some(id) = car["driverPlayerId"].as_u64() {
            seated.insert(id as u16);
        }
        for id in car["passengers"]
            .as_array()
            .into_iter()
            .flatten()
            .filter_map(Value::as_u64)
        {
            seated.insert(id as u16);
        }
    }
    let dt = time.delta_secs();
    let glide = 1. - (-14. * dt).exp();
    let speed_blend = 1. - (-8. * dt).exp();
    for (mut transform, mut rig, mut old, mut player) in &mut puppets {
        let Some(row) = rows.get(&player.id) else {
            continue;
        };
        let target = crate::player::vector(&row["position"]).unwrap();
        let previous = transform.translation;
        let snap = previous.distance(target) > 5.;
        transform.translation = if snap {
            target
        } else {
            previous.lerp(target, glide)
        };
        transform.rotation = Quat::from_rotation_y(
            row["yaw"].as_f64().unwrap_or(0.) as f32 * std::f32::consts::PI / 180.,
        );
        if !snap && dt > 0. {
            let speed = (transform.translation - previous).xz().length() / dt;
            player.speed += (speed - player.speed) * speed_blend;
        }
        player.alive = row["alive"].as_bool().unwrap_or(true);
        let stance = stance(row);
        let is_seated = stance == "sitting" || seated.contains(&player.id);
        player.foot_motion = FootMotion {
            speed: player.speed,
            vertical_speed: if dt > 0. {
                (transform.translation.y - previous.y) / dt
            } else {
                0.
            },
            stance,
            seated: is_seated,
            snap,
        };
        let height = StanceHeights::default().for_stance(stance);
        if (height - player.height).abs() > 0.001 {
            commands
                .entity(player.hull)
                .insert(Collider::capsule_y(height * 0.5 - 0.35, 0.35));
            if let Ok(mut pose) = hulls.get_mut(player.hull) {
                pose.translation.y = height * 0.5;
            }
            player.height = height;
        }
        if is_seated != player.seated {
            if is_seated {
                commands.entity(player.hull).insert(ColliderDisabled);
            } else {
                commands.entity(player.hull).remove::<ColliderDisabled>();
            }
            player.seated = is_seated;
        }
        let clip = if stance == "sitting" && rig.data.anims.contains_key("Idle_Sit") {
            "Idle_Sit"
        } else {
            super::locomotion(player.speed, stance)
        };
        rig.play(clip, true);
        let outfit = appearance(row, &catalog);
        if *old != outfit {
            *old = outfit;
        }
    }
}

#[cfg(test)]
mod tests;
