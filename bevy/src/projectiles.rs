//! Original ProjectileReplicaView: follow authority, retire on despawn. Local
//! accepted throws retain their exact model; remote wire data gives a family.
use crate::{content::ContentCache, player::GameSnapshot, world::WorldConfig};
use bevy::prelude::*;
use std::collections::{HashMap, HashSet};
pub struct ProjectilePlugin;
impl Plugin for ProjectilePlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<Replicas>()
            .add_systems(FixedUpdate, sync.after(crate::player::PublishPlayer));
    }
}
#[derive(Resource, Default)]
struct Replicas {
    nodes: HashMap<u32, Entity>,
    items: HashMap<u32, u16>,
    epoch: u64,
}
fn family_item(kind: u64) -> u16 {
    match kind {
        1 => 267,
        2 => 259,
        _ => 254,
    }
}
fn sync(
    mut commands: Commands,
    mut replicas: ResMut<Replicas>,
    snapshot: Res<GameSnapshot>,
    epoch: Res<crate::player::WorldSessionEpoch>,
    config: Res<WorldConfig>,
    assets: Res<AssetServer>,
    mut cache: ResMut<ContentCache>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
    mut transforms: Query<&mut Transform>,
) {
    if replicas.epoch != epoch.0 {
        for (_, node) in replicas.nodes.drain() {
            commands.entity(node).despawn();
        }
        replicas.items.clear();
        replicas.epoch = epoch.0;
    }
    for event in snapshot.0["events"]
        .as_array()
        .into_iter()
        .flatten()
        .filter(|e| e["kind"] == "throwAccepted")
    {
        if let (Some(net), Some(item)) = (
            event["data"]["netId"].as_u64(),
            event["data"]["itemId"].as_u64(),
        ) {
            replicas.items.insert(net as u32, item as u16);
        }
    }
    let mut seen = HashSet::new();
    for projectile in snapshot.0["projectiles"].as_array().into_iter().flatten() {
        let Some(id) = projectile["netId"].as_u64().map(|n| n as u32) else {
            continue;
        };
        let Some(position) = crate::player::vector(&projectile["position"]) else {
            continue;
        };
        seen.insert(id);
        if let Some(node) = replicas.nodes.get(&id) {
            if let Ok(mut transform) = transforms.get_mut(*node) {
                transform.translation = transform.translation.lerp(position, 0.4);
            }
            continue;
        }
        let item = replicas
            .items
            .get(&id)
            .copied()
            .unwrap_or_else(|| family_item(projectile["kind"].as_u64().unwrap_or(0)));
        let path = format!("items/{item}.txt");
        let Ok((mesh, _)) = cache.load_mesh(&config.content_root.join(&path), &mut meshes) else {
            warn!("Missing original projectile model {path}");
            continue;
        };
        let material = cache.material(
            Some(&format!("items/{item}.png")),
            false,
            &assets,
            &mut materials,
        );
        let node = commands
            .spawn((
                Name::new(format!("Projectile {id}, item {item}")),
                Mesh3d(mesh),
                MeshMaterial3d(material),
                Transform::from_translation(position),
            ))
            .id();
        replicas.nodes.insert(id, node);
    }
    replicas.nodes.retain(|id, node| {
        if seen.contains(id) {
            true
        } else {
            commands.entity(*node).despawn();
            false
        }
    });
    replicas.items.retain(|id, _| seen.contains(id));
}
