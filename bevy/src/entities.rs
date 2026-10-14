//! Client replicas: visuals follow authoritative snapshots, and interactions send
//! the existing client commands. The local renderer never owns item inventories.
use crate::{
    bridge::CoreBridge,
    content::ContentCache,
    player::{GameSnapshot, PlayerCamera},
    settings::{GameAction, Settings},
    ui::{UiModel, UiState},
    world::{WorldConfig, WorldGeometry},
};
use bevy::prelude::*;
use serde::Deserialize;
use serde_json::{Value, json};
use std::collections::{HashMap, HashSet};

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ItemVisual {
    pub id: u16,
    pub name: String,
    #[serde(rename = "type")]
    pub item_type: String,
    pub asset: Option<String>,
    pub mesh: Option<String>,
    pub texture: Option<String>,
    pub icon: Option<String>,
}
#[derive(Resource, Default)]
pub struct ItemCatalog {
    pub items: HashMap<u16, ItemVisual>,
}
#[derive(Resource, Default)]
struct Replicas {
    entities: HashMap<u32, Entity>,
    last_event: u64,
    map_name: String,
    geometry_map: String,
    geometry_revision: u64,
}
#[derive(Component)]
pub struct WorldItemReplica {
    pub net_id: u32,
    pub item_id: u16,
}
#[derive(Message, Clone)]
pub struct CoreEvent {
    pub sequence: u64,
    pub kind: String,
    pub data: Value,
}

pub struct EntityPlugin;
impl Plugin for EntityPlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<ItemCatalog>()
            .init_resource::<Replicas>()
            .add_message::<CoreEvent>()
            .add_systems(
                Update,
                (
                    load_catalog,
                    register_world_geometry,
                    sync_world_items,
                    forward_events,
                    interact_items,
                )
                    .chain()
                    .after(crate::player::PlayerUpdate),
            );
    }
}

fn load_catalog(
    bridge: Res<CoreBridge>,
    config: Res<WorldConfig>,
    model: Res<UiModel>,
    mut catalog: ResMut<ItemCatalog>,
    mut replicas: ResMut<Replicas>,
    mut commands: Commands,
) {
    if replicas.map_name != config.map_name {
        for (_, entity) in replicas.entities.drain() {
            commands.entity(entity).despawn();
        }
        replicas.last_event = 0;
        replicas.geometry_map.clear();
        replicas.geometry_revision = 0;
        replicas.map_name = config.map_name.clone();
    }
    if !model.connected || !catalog.items.is_empty() {
        return;
    }
    match bridge.request("catalog", json!({})).and_then(|response| {
        Ok(serde_json::from_value::<Vec<ItemVisual>>(
            response["result"]["items"].clone(),
        )?)
    }) {
        Ok(items) => {
            catalog.items = items.into_iter().map(|item| (item.id, item)).collect();
            info!("Loaded {} item visual definitions", catalog.items.len());
        }
        Err(error) => error!("Cannot read item catalogue: {error}"),
    }
}

fn register_world_geometry(
    bridge: Res<CoreBridge>,
    config: Res<WorldConfig>,
    world: Res<crate::world::WorldState>,
    geometry: Res<WorldGeometry>,
    mut replicas: ResMut<Replicas>,
    mut model: ResMut<UiModel>,
) {
    if !world.ready
        || !model.connected
        || (replicas.geometry_map == config.map_name
            && replicas.geometry_revision == geometry.revision)
    {
        return;
    }
    let result = (|| -> anyhow::Result<()> {
        bridge.request(
            "worldGeometry",
            json!({"replace":true,"finalize":false,"meshes":[]}),
        )?;
        for patch in &geometry.terrain {
            for data in patch.mesh_by_layer(geometry.loaded_center, geometry.loaded_radius) {
                if data.positions.is_empty() {
                    continue;
                }
                bridge.request("worldGeometry",json!({"replace":false,"finalize":false,"meshes":[{"vertices":data.positions,"terrain":true}]}))?;
            }
        }
        for chunk in geometry.objects.chunks(32) {
            let meshes: Vec<Value> = chunk
                .iter()
                .map(|object| {
                    let vertices: Vec<[f32; 3]> = object
                        .geometry
                        .positions
                        .iter()
                        .map(|p| {
                            object
                                .transform
                                .transform_point3(Vec3::from_array(*p))
                                .to_array()
                        })
                        .collect();
                    json!({"vertices":vertices})
                })
                .collect();
            bridge.request(
                "worldGeometry",
                json!({"replace":false,"finalize":false,"meshes":meshes}),
            )?;
        }
        let result = bridge.request("worldGeometry", json!({"replace":false,"finalize":true}))?;
        info!(
            "Registered authoritative world geometry: {}",
            result["result"]
        );
        if config.procedural_path.is_none() && replicas.geometry_map != config.map_name {
            if let Some(path) = &config.map_root {
                let loot = bridge.request("worldLoot", json!({"mapRoot":path}))?;
                info!("Registered original loot stream: {}", loot["result"]);
            }
        }
        Ok(())
    })();
    match result {
        Ok(()) => {
            replicas.geometry_map = config.map_name.clone();
            replicas.geometry_revision = geometry.revision;
        }
        Err(error) => {
            error!("World authority setup failed: {error}");
            model.notice(error.to_string());
            model.connected = false;
        }
    }
}

fn sync_world_items(
    mut commands: Commands,
    snapshot: Res<GameSnapshot>,
    catalog: Res<ItemCatalog>,
    config: Res<WorldConfig>,
    mut replicas: ResMut<Replicas>,
    mut cache: ResMut<ContentCache>,
    assets: Res<AssetServer>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
    mut transforms: Query<&mut Transform, With<WorldItemReplica>>,
) {
    let Some(items) = snapshot.0["worldItems"].as_array() else {
        return;
    };
    let mut live = HashSet::new();
    for item in items {
        let Some(id) = item["netId"].as_u64().and_then(|id| u32::try_from(id).ok()) else {
            continue;
        };
        let Some(item_id) = item["itemId"]
            .as_u64()
            .and_then(|id| u16::try_from(id).ok())
        else {
            continue;
        };
        let Ok(position) = serde_json::from_value::<[f32; 3]>(item["position"].clone()) else {
            continue;
        };
        let position = Vec3::from_array(position);
        if !position.is_finite() {
            continue;
        }
        live.insert(id);
        if let Some(&entity) = replicas.entities.get(&id) {
            if let Ok(mut transform) = transforms.get_mut(entity) {
                transform.translation = position;
            }
            continue;
        }
        let Some(visual) = catalog.items.get(&item_id) else {
            continue;
        };
        let Some(path) = visual.mesh.as_ref() else {
            continue;
        };
        let (mesh, _) = match cache.load_mesh(&config.content_root.join(path), &mut meshes) {
            Ok(mesh) => mesh,
            Err(error) => {
                warn!("Item {}: {error}", item_id);
                continue;
            }
        };
        let material = cache.material(visual.texture.as_deref(), false, &assets, &mut materials);
        let entity = commands
            .spawn((
                Name::new(visual.name.clone()),
                WorldItemReplica {
                    net_id: id,
                    item_id,
                },
                Mesh3d(mesh),
                MeshMaterial3d(material),
                Transform::from_translation(position).with_rotation(Quat::from_euler(
                    EulerRot::YXZ,
                    ((id.wrapping_mul(137)) % 360) as f32 * std::f32::consts::PI / 180.0,
                    std::f32::consts::FRAC_PI_2,
                    0.0,
                )),
            ))
            .id();
        replicas.entities.insert(id, entity);
    }
    replicas.entities.retain(|id, entity| {
        if live.contains(id) {
            true
        } else {
            commands.entity(*entity).despawn();
            false
        }
    });
}

fn forward_events(
    snapshot: Res<GameSnapshot>,
    bridge: Res<CoreBridge>,
    mut replicas: ResMut<Replicas>,
    mut events: MessageWriter<CoreEvent>,
) {
    if let Some(source) = snapshot.0["events"].as_array() {
        for event in source {
            let sequence = event["seq"].as_u64().unwrap_or(0);
            if sequence <= replicas.last_event {
                continue;
            }
            events.write(CoreEvent {
                sequence,
                kind: event["kind"].as_str().unwrap_or("").into(),
                data: event["data"].clone(),
            });
            replicas.last_event = sequence;
        }
        bridge.ack_events(replicas.last_event);
    }
}

fn interact_items(
    bridge: Res<CoreBridge>,
    ui: Res<UiState>,
    settings: Res<Settings>,
    keys: Res<ButtonInput<KeyCode>>,
    mouse: Res<ButtonInput<MouseButton>>,
    catalog: Res<ItemCatalog>,
    camera: Query<&Transform, With<PlayerCamera>>,
    items: Query<(&Transform, &WorldItemReplica)>,
    geometry: Res<WorldGeometry>,
    mut model: ResMut<UiModel>,
) {
    if ui.captures_input() {
        model.interaction.clear();
        return;
    }
    let Ok(camera) = camera.single() else {
        return;
    };
    let origin = camera.translation;
    let direction = *camera.forward();
    let wall_distance = geometry
        .raycast(origin, direction, 6.0)
        .map_or(6.0, |hit| hit.0);
    let nearest = items
        .iter()
        .filter_map(|(transform, item)| {
            let delta = transform.translation - origin;
            let along = delta.dot(direction);
            let off_ray = (delta - direction * along).length();
            (along >= 0.0 && along < wall_distance && off_ray < 0.6).then_some((along, item))
        })
        .min_by(|a, b| a.0.total_cmp(&b.0));
    if let Some((_, item)) = nearest {
        let name = catalog
            .items
            .get(&item.item_id)
            .map_or("Item", |item| item.name.as_str());
        model.interaction = format!(
            "[{}] Pick up {name}",
            settings
                .bindings
                .get(&GameAction::Interact)
                .map(|b| b.label())
                .unwrap_or_default()
        );
        if settings.just_pressed(GameAction::Interact, &keys, &mouse) {
            if let Err(error) = bridge.request(
                "action",
                json!({"name":"pickupItem","args":{"netId":item.net_id}}),
            ) {
                model.notice(error.to_string());
            }
        }
    } else {
        model.interaction.clear();
    }
}
