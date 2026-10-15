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
mod items;

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
    #[serde(default)]
    pub attachment_mesh: Option<String>,
    #[serde(default)]
    pub attachment_texture: Option<String>,
    #[serde(default)]
    pub gun: Value,
    #[serde(default)]
    pub melee: Value,
    #[serde(default)]
    pub is_consumable: bool,
    #[serde(default)]
    pub is_fluid_container: bool,
    #[serde(default)]
    pub is_deployable: bool,
    #[serde(default)]
    pub hand_kind: String,
    #[serde(default)]
    pub has_condition: bool,
    #[serde(default)]
    pub stack_size: u16,
    #[serde(default)]
    pub rarity: String,
    #[serde(default)]
    pub consume_animation: Value,
    #[serde(default)]
    pub throwable: Value,
    #[serde(default)]
    pub tool: Value,
    #[serde(default)]
    pub gravity_multiplier: f32,
}
#[derive(Resource, Default)]
pub struct ItemCatalog {
    pub items: HashMap<u16, ItemVisual>,
    pub gestures: Vec<crate::avatar::gestures::Definition>,
    pub throw_motion: Value,
    pub bullet_visuals: Option<crate::bullets::Visuals>,
}
#[derive(Resource, Default)]
pub struct ServiceManifest {
    pub map: String,
    pub objects: HashMap<usize, Value>,
    pub resources: HashMap<(String, usize), Value>,
    pub object_doors: Vec<Value>,
    pub container_doors: Vec<Value>,
    pub seats: Vec<Value>,
}
#[derive(SystemSet, Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct WorldServicesReady;
#[derive(SystemSet, Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct CollectInteractions;
#[derive(SystemSet, Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct ApplyInteractions;
#[derive(Resource, Default)]
pub struct InteractionFocus {
    candidate: Option<InteractionCandidate>,
}
struct InteractionCandidate {
    distance: f32,
    label: String,
    action: String,
    args: Value,
    open_storage: bool,
}
impl InteractionFocus {
    pub fn has_candidate(&self) -> bool {
        self.candidate.is_some()
    }

    pub fn offer(
        &mut self,
        distance: f32,
        label: impl Into<String>,
        action: impl Into<String>,
        args: Value,
        open_storage: bool,
    ) {
        if !distance.is_finite()
            || distance < 0.
            || self
                .candidate
                .as_ref()
                .is_some_and(|c| c.distance <= distance)
        {
            return;
        }
        self.candidate = Some(InteractionCandidate {
            distance,
            label: label.into(),
            action: action.into(),
            args,
            open_storage,
        });
    }
}
#[derive(Resource, Default)]
struct Replicas {
    entities: HashMap<u32, Entity>,
    last_event: u64,
    map_name: String,
    geometry_map: String,
    geometry_revision: u64,
    session_epoch: u64,
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
            .init_resource::<ServiceManifest>()
            .init_resource::<InteractionFocus>()
            .init_resource::<Replicas>()
            .init_resource::<items::Models>()
            .add_plugins(items::material::AlphaDepthPlugin)
            .configure_sets(Update, items::material::AlphaDepthUpdate.after(sync_world_items))
            .add_message::<CoreEvent>()
            .add_systems(
                Update,
                (
                    load_catalog,
                    register_world_geometry.in_set(WorldServicesReady),
                    sync_static_life,
                    sync_world_items,
                    forward_events,
                )
                    .chain()
                    .after(crate::player::PlayerUpdate)
                    .before(CollectInteractions),
            )
            .add_systems(
                Update,
                reset_focus
                    .after(crate::player::PlayerUpdate)
                    .before(CollectInteractions),
            )
            .add_systems(
                Update,
                interact_items
                    .in_set(CollectInteractions)
                    .after(crate::player::PlayerCameraUpdate),
            )
            .add_systems(
                Update,
                apply_interaction
                    .in_set(ApplyInteractions)
                    .after(CollectInteractions),
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
    epoch: Res<crate::player::WorldSessionEpoch>,
    mut services: ResMut<ServiceManifest>,
) {
    if replicas.map_name != config.map_name || replicas.session_epoch != epoch.0 {
        for (_, entity) in replicas.entities.drain() {
            commands.entity(entity).despawn();
        }
        replicas.last_event = 0;
        if replicas.map_name != config.map_name {
            replicas.geometry_map.clear();
        }
        replicas.geometry_revision = 0;
        replicas.session_epoch = epoch.0;
        services.map.clear();
        replicas.map_name = config.map_name.clone();
    }
    if !model.connected || !catalog.items.is_empty() {
        return;
    }
    match bridge.request("catalog", json!({})).and_then(|response| {
        catalog.gestures =
            serde_json::from_value(response["result"]["gestures"].clone()).unwrap_or_default();
        catalog.throw_motion = response["result"]["throwMotion"].clone();
        catalog.bullet_visuals =
            serde_json::from_value(response["result"]["bulletVisuals"].clone()).ok();
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
    mut services: ResMut<ServiceManifest>,
) {
    if !world.ready
        || !model.connected
        || (replicas.geometry_map == config.map_name
            && replicas.geometry_revision == geometry.revision)
    {
        return;
    }
    let result = (|| -> anyhow::Result<()> {
        if services.map != config.map_name {
            services.objects.clear();
            services.resources.clear();
            services.object_doors.clear();
            services.container_doors.clear();
            services.seats.clear();
            if config.procedural_path.is_none() {
                if let Some(root) = &config.map_root {
                    let reply = bridge.request("mapServices", json!({"mapRoot":root,"mapName":config.map_name,"contentRoot":config.content_root}))?;
                    let result = &reply["result"];
                    services.object_doors = result["objectDoors"]
                        .as_array()
                        .cloned()
                        .unwrap_or_default();
                    services.container_doors = result["containerDoors"]
                        .as_array()
                        .cloned()
                        .unwrap_or_default();
                    services.seats = result["seats"].as_array().cloned().unwrap_or_default();
                    for object in result["objects"].as_array().into_iter().flatten() {
                        if let Some(index) = object["sourceIndex"].as_u64() {
                            services.objects.insert(index as usize, object.clone());
                        }
                    }
                    for resource in result["resources"].as_array().into_iter().flatten() {
                        if let (Some(name), Some(index)) =
                            (resource["name"].as_str(), resource["instance"].as_u64())
                        {
                            services
                                .resources
                                .insert((name.to_owned(), index as usize), resource.clone());
                        }
                    }
                    info!(
                        "Original map services: {} containers, {} fixtures, {} resources",
                        result["containerCount"], result["fixtureCount"], result["resourceCount"]
                    );
                }
            }
            services.map = config.map_name.clone();
        }
        bridge.request(
            "worldGeometry",
            json!({"replace":true,"finalize":false,"meshes":[],"terrainLayers":crate::world::layers::authority_grids(&geometry.terrain)}),
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
                    let destructible = object
                        .source_index
                        .and_then(|index| services.objects.get(&index))
                        .and_then(|row| row["destructibleIndex"].as_i64())
                        .unwrap_or(-1);
                    let resource = object
                        .resource_key
                        .as_ref()
                        .and_then(|key| services.resources.get(key))
                        .and_then(|row| row["index"].as_i64())
                        .unwrap_or(-1);
                    json!({"vertices":vertices,"destructibleIndex":destructible,"resourceIndex":resource})
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
        if replicas.geometry_map != config.map_name {
            bridge.request("worldServices", json!({"hasWater":world.sea_level.is_some(),"seaLevel":world.sea_level.unwrap_or(0.)}))?;
        }
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

fn sync_static_life(
    mut commands: Commands,
    snapshot: Res<GameSnapshot>,
    services: Res<ServiceManifest>,
    mut objects: Query<
        (
            Entity,
            &crate::world::WorldObject,
            &mut Visibility,
            Option<&bevy_rapier3d::prelude::ColliderDisabled>,
        ),
        Without<crate::world::WorldResource>,
    >,
    mut resources: Query<
        (
            Entity,
            &crate::world::WorldResource,
            &mut Visibility,
            Option<&bevy_rapier3d::prelude::ColliderDisabled>,
        ),
        Without<crate::world::WorldObject>,
    >,
) {
    let dead = |key: &str| {
        snapshot.0[key]["dead"]
            .as_array()
            .into_iter()
            .flatten()
            .filter_map(Value::as_u64)
            .collect::<HashSet<_>>()
    };
    let broken = dead("destructibles");
    let taken = dead("resources");
    for (entity, object, mut visibility, disabled) in &mut objects {
        let Some(index) = services
            .objects
            .get(&object.source_index)
            .and_then(|row| row["destructibleIndex"].as_u64())
        else {
            continue;
        };
        set_alive(
            &mut commands,
            entity,
            &mut visibility,
            disabled.is_some(),
            !broken.contains(&index),
        );
    }
    for (entity, resource, mut visibility, disabled) in &mut resources {
        let Some(index) = services
            .resources
            .get(&(resource.name.clone(), resource.instance_index))
            .and_then(|row| row["index"].as_u64())
        else {
            continue;
        };
        set_alive(
            &mut commands,
            entity,
            &mut visibility,
            disabled.is_some(),
            !taken.contains(&index),
        );
    }
}
fn set_alive(
    commands: &mut Commands,
    entity: Entity,
    visibility: &mut Visibility,
    disabled: bool,
    alive: bool,
) {
    *visibility = if alive {
        Visibility::Inherited
    } else {
        Visibility::Hidden
    };
    if alive && disabled {
        commands
            .entity(entity)
            .remove::<bevy_rapier3d::prelude::ColliderDisabled>();
    }
    if !alive && !disabled {
        commands
            .entity(entity)
            .insert(bevy_rapier3d::prelude::ColliderDisabled);
    }
}

fn sync_world_items(
    mut commands: Commands,
    snapshot: Res<GameSnapshot>,
    catalog: Res<ItemCatalog>,
    config: Res<WorldConfig>,
    mut replicas: ResMut<Replicas>,
    mut models: ResMut<items::Models>,
    mut cache: ResMut<ContentCache>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
    mut images: ResMut<Assets<Image>>,
    mut transforms: Query<(&mut Transform, &items::Displayed), With<WorldItemReplica>>,
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
        let amount = item["amount"]
            .as_u64()
            .and_then(|n| u16::try_from(n).ok())
            .unwrap_or(0);
        live.insert(id);
        let existing = replicas.entities.get(&id).copied();
        let rotation = items::rotation(item, id);
        let aligned_arrow = items::aligned_arrow(item, item_id);
        if let Some(entity) = existing {
            if let Ok((mut transform, displayed)) = transforms.get_mut(entity) {
                transform.translation = position;
                transform.rotation = rotation;
                if displayed.item_id == item_id
                    && displayed.amount == amount
                    && displayed.aligned_arrow == aligned_arrow
                {
                    continue;
                }
            }
        }
        let visual = catalog.items.get(&item_id);
        let art = models.art(
            &config.content_root,
            item_id,
            amount,
            visual.map_or(0, |i| i.stack_size),
            visual.map_or("", |i| i.rarity.as_str()),
            &mut cache,
            &mut meshes,
            &mut materials,
            &mut images,
        );
        let art = if aligned_arrow {
            models.align_arrow(item_id, art, &mut meshes)
        } else {
            art
        };
        let entity = existing.unwrap_or_else(|| commands.spawn_empty().id());
        if art.translucent {
            commands.entity(entity).insert(items::material::AlphaDepth);
        } else {
            commands.entity(entity).remove::<items::material::AlphaDepth>();
        }
        commands
            .entity(entity)
            .insert((
                Name::new(visual.map_or_else(|| format!("Item #{item_id}"), |i| i.name.clone())),
                WorldItemReplica {
                    net_id: id,
                    item_id,
                },
                items::Displayed {
                    item_id,
                    amount,
                    aligned_arrow,
                },
                art.focus,
                Mesh3d(art.mesh),
                MeshMaterial3d(art.material),
                Transform::from_translation(position).with_rotation(rotation),
            ))
            .remove::<bevy::camera::primitives::Aabb>();
        replicas.entities.insert(id, entity);
    }
    replicas.entities.retain(|id, entity| {
        if live.contains(id) {
            true
        } else {
            commands.entity(*entity).try_despawn();
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
    ui: Res<UiState>,
    catalog: Res<ItemCatalog>,
    camera: Query<&Transform, With<PlayerCamera>>,
    items: Query<(&Transform, &WorldItemReplica, &items::Focus)>,
    mut focus: ResMut<InteractionFocus>,
    context: bevy_rapier3d::prelude::ReadRapierContext,
    player: Query<Entity, With<crate::player::PlayerBody>>,
    objects: Query<&crate::world::WorldObject>,
    resources: Query<&crate::world::WorldResource>,
    services: Res<ServiceManifest>,
    occupancy: Option<Res<crate::vehicle::Occupancy>>,
) {
    if ui.captures_input() || occupancy.is_some_and(|o| o.seat.is_some()) {
        return;
    }
    let Ok(camera) = camera.single() else {
        return;
    };
    let origin = camera.translation;
    let direction = *camera.forward();
    let (Ok(context), Ok(player)) = (context.single(), player.single()) else {
        return;
    };
    let obstacle = context.cast_ray(
        origin,
        direction,
        6.,
        true,
        bevy_rapier3d::prelude::QueryFilter::default()
            .exclude_collider(player)
            .exclude_sensors(),
    );
    let wall_distance = obstacle.map_or(6.0, |hit| hit.1);
    let nearest = items
        .iter()
        .filter_map(|(transform, item, shape)| {
            shape
                .ray(transform, origin, direction, wall_distance)
                .map(|distance| (distance, item))
        })
        .min_by(|a, b| a.0.total_cmp(&b.0));
    if let Some((distance, item)) = nearest {
        let name = catalog
            .items
            .get(&item.item_id)
            .map_or("Item", |item| item.name.as_str());
        focus.offer(
            distance,
            format!("Pick up {name}"),
            "pickupItem",
            json!({"netId":item.net_id}),
            false,
        );
    } else {
        let Some((entity, _)) = obstacle else {
            return;
        };
        if let Ok(object) = objects.get(entity) {
            if let Some(id) = services
                .objects
                .get(&object.source_index)
                .and_then(|row| row["containerNetId"].as_u64())
            {
                focus.offer(
                    wall_distance,
                    format!("Open {}", object.asset_name.replace('_', " ")),
                    "openStorage",
                    json!({"netId":id}),
                    true,
                );
            }
        }
        if let Ok(resource) = resources.get(entity) {
            if let Some(row) = services
                .resources
                .get(&(resource.name.clone(), resource.instance_index))
            {
                if row["rewardItemId"].as_u64().unwrap_or(0) > 0 {
                    focus.offer(
                        wall_distance,
                        format!("Harvest {}", resource.name.replace('_', " ")),
                        "forageResource",
                        json!({"index":row["index"]}),
                        false,
                    );
                }
            }
        }
    }
}

fn reset_focus(mut focus: ResMut<InteractionFocus>) {
    focus.candidate = None;
}
fn apply_interaction(
    bridge: Res<CoreBridge>,
    focus: Res<InteractionFocus>,
    settings: Res<Settings>,
    keys: Res<ButtonInput<KeyCode>>,
    mouse: Res<ButtonInput<MouseButton>>,
    mut ui: ResMut<UiState>,
    mut model: ResMut<UiModel>,
) {
    model.interaction.clear();
    if ui.captures_input() {
        return;
    }
    let Some(candidate) = &focus.candidate else {
        return;
    };
    model.interaction = format!(
        "[{}] {}",
        settings.bindings[&GameAction::Interact].label(),
        candidate.label
    );
    if !settings.just_pressed(GameAction::Interact, &keys, &mouse) {
        return;
    }
    match bridge.request(
        "action",
        json!({"name":candidate.action,"args":candidate.args}),
    ) {
        Ok(_) if candidate.open_storage => {
            ui.page = model
                .pages
                .iter()
                .filter(|p| p.index < 7 && p.width > 0 && p.height > 0)
                .count();
            ui.screen = crate::ui::Screen::Inventory;
            ui.selected_item = None;
        }
        Err(error) => model.notice(error.to_string()),
        _ => {}
    }
}
