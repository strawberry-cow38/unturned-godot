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
pub(crate) mod crops;
mod items;
pub(crate) mod forage;
mod map_bindings;
pub(crate) mod npcs;
pub(crate) mod shelves;

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
    /// ItemAsset.fuelCapacity: above zero, a source fuel container (gas can, jerrycan).
    #[serde(default)]
    pub fuel_capacity: f32,
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
    /// Source vehicle paint/tire/carjack use timeline (Catalog.HandTool).
    #[serde(default)]
    pub hand_tool: Value,
    /// ItemAsset.size_x/size_y: the unrotated grid footprint.
    #[serde(default)]
    pub width: u8,
    #[serde(default)]
    pub height: u8,
}
#[derive(Resource, Default)]
pub struct ItemCatalog {
    pub items: HashMap<u16, ItemVisual>,
    pub(crate) deployables: Vec<crate::deployables::Definition>,
    pub gestures: Vec<crate::avatar::gestures::Definition>,
    pub throw_motion: Value,
    pub bullet_visuals: Option<crate::bullets::Visuals>,
    pub mounted_weapons: HashMap<String, f32>,
    pub(crate) npc_crew: Option<crate::vehicle::turrets::crew::Definition>,
    pub(crate) vehicle_lighting: Option<crate::vehicle::lighting::Definition>,
    pub(crate) vehicle_maintenance: Option<crate::vehicle::maintenance::Definition>,
    /// ContainerSchema.All, indexed by KindId.
    pub(crate) containers: Vec<shelves::ContainerKind>,
}
#[derive(Resource, Default)]
pub struct ServiceManifest {
    pub map: String,
    /// Local adoption generation; also changes when a late replica rebinds a door.
    pub revision: u64,
    pub holiday: Option<String>,
    pub npcs: Vec<Value>,
    pub objects: HashMap<usize, Value>,
    pub resources: HashMap<(String, usize), Value>,
    pub object_doors: Vec<Value>,
    pub container_doors: Vec<Value>,
    pub seats: Vec<Value>,
}
impl ServiceManifest {
    fn clear(&mut self) {
        let revision = self.revision.wrapping_add(1);
        *self = Self {
            revision,
            ..default()
        };
    }
    fn adopt(&mut self, map: &str, holiday: &str, result: &Value) {
        self.clear();
        self.map = map.into();
        self.holiday = Some(holiday.into());
        self.object_doors = result["objectDoors"]
            .as_array()
            .cloned()
            .unwrap_or_default();
        self.container_doors = result["containerDoors"]
            .as_array()
            .cloned()
            .unwrap_or_default();
        self.seats = result["seats"].as_array().cloned().unwrap_or_default();
        self.npcs = result["npcs"].as_array().cloned().unwrap_or_default();
        for object in result["objects"].as_array().into_iter().flatten() {
            if let Some(index) = object["sourceIndex"].as_u64() {
                self.objects.insert(index as usize, object.clone());
            }
        }
        for resource in result["resources"].as_array().into_iter().flatten() {
            if let (Some(name), Some(index)) =
                (resource["name"].as_str(), resource["instance"].as_u64())
            {
                self.resources
                    .insert((name.to_owned(), index as usize), resource.clone());
            }
        }
    }
}
#[derive(SystemSet, Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct WorldServicesReady;
#[derive(SystemSet, Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub(crate) struct CoreEventsReady;
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
    manual: bool,
    show_binding: bool,
    outline: Option<(Entity, Color)>,
}
impl InteractionFocus {
    /// Reserve the closest prompt for a source interaction with its own
    /// tap/release/hold lifecycle. The generic press dispatcher must not fire it.
    pub fn offer_manual(
        &mut self,
        distance: f32,
        label: impl Into<String>,
        action: &str,
        args: Value,
    ) {
        self.offer(distance, label, action, args.clone(), false);
        if let Some(candidate) = &mut self.candidate {
            if candidate.action == action && candidate.args == args {
                candidate.manual = true;
            }
        }
    }
    /// A passive source status occupies focus without advertising or dispatching a key.
    pub fn offer_status(
        &mut self,
        distance: f32,
        label: impl Into<String>,
        action: &str,
        args: Value,
    ) {
        self.offer_manual(distance, label, action, args.clone());
        if let Some(candidate) = &mut self.candidate {
            if candidate.action == action && candidate.args == args {
                candidate.show_binding = false;
            }
        }
    }
    pub fn selected(&self, action: &str, id: u32) -> bool {
        self.candidate.as_ref().is_some_and(|candidate| {
            candidate.action == action && candidate.args["netId"].as_u64() == Some(u64::from(id))
        })
    }
    /// A dispatched candidate with no prompt (the source shows none for it).
    pub fn offer_hidden(&mut self, distance: f32, action: &str, args: Value, open_storage: bool) {
        self.offer(distance, "", action, args.clone(), open_storage);
        if let Some(candidate) = &mut self.candidate {
            if candidate.action == action && candidate.args == args {
                candidate.show_binding = false;
            }
        }
    }
    /// Whether the winning candidate carries the storage-screen flag that the
    /// crop harvest fall-through yields to.
    #[cfg(test)]
    pub(crate) fn opens_storage(&self) -> bool {
        self.candidate.as_ref().is_some_and(|c| c.open_storage)
    }
    pub fn has_candidate(&self) -> bool {
        self.candidate.is_some()
    }
    /// The winning candidate's replicated identity (source ResolveFocus has one winner).
    pub fn selected_net_id(&self) -> Option<u64> {
        self.candidate.as_ref().and_then(|c| c.args["netId"].as_u64())
    }
    /// OutlineOverlay.ShowOutline: the candidate offered as (action, args)
    /// claims its silhouette and the rim colour, if it is still the winner.
    pub(crate) fn outline(&mut self, action: &str, args: &Value, target: Entity, color: Color) {
        if let Some(candidate) = &mut self.candidate
            && candidate.action == action
            && candidate.args == *args
        {
            candidate.outline = Some((target, color));
        }
    }
    pub(crate) fn outlined(&self) -> Option<(Entity, Color)> {
        self.candidate.as_ref().and_then(|c| c.outline)
    }
    /// A source F branch tried after every look target except the shelf and
    /// crate ones (`PlayerController` runs `RequestHarvestNearestCrop` before
    /// `_focusShelf`/`OpenNearestCrate`). It has no prompt of its own: a storage
    /// candidate keeps its label, but F dispatches this action instead.
    pub(crate) fn fall_through(&mut self, action: &str, args: Value) {
        match &mut self.candidate {
            None => {
                self.candidate = Some(InteractionCandidate {
                    distance: f32::INFINITY,
                    label: String::new(),
                    action: action.into(),
                    args,
                    open_storage: false,
                    manual: false,
                    show_binding: false,
                    outline: None,
                })
            }
            Some(candidate) if candidate.open_storage => {
                candidate.action = action.into();
                candidate.args = args;
                candidate.open_storage = false;
            }
            Some(_) => {}
        }
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
            manual: false,
            show_binding: true,
            outline: None,
        });
    }
}
#[derive(Resource, Default)]
pub(crate) struct Replicas {
    entities: HashMap<u32, Entity>,
    last_event: u64,
    map_name: String,
    geometry_map: String,
    geometry_revision: u64,
    session_epoch: u64,
    setup_failed: bool,
}
impl Replicas {
    /// The host has accepted this map's services, geometry and loot stream.
    pub(crate) fn geometry_registered(&self, map: &str) -> bool {
        !map.is_empty() && self.geometry_map == map
    }
    /// The last world-authority registration attempt was refused.
    pub(crate) fn setup_failed(&self) -> bool {
        self.setup_failed
    }
    #[cfg(test)]
    pub(crate) fn mark_geometry_for_test(&mut self, map: &str) {
        self.geometry_map = map.into();
    }
    #[cfg(test)]
    pub(crate) fn fail_setup_for_test(&mut self) {
        self.setup_failed = true;
    }
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
        crops::install(app);
        shelves::install(app);
        app.add_plugins(npcs::NpcPlugin)
            .init_resource::<ItemCatalog>()
            .init_resource::<ServiceManifest>()
            .init_resource::<map_bindings::Bindings>()
            .init_resource::<InteractionFocus>()
            .init_resource::<Replicas>()
            .init_resource::<items::Models>()

            .add_plugins(items::material::AlphaDepthPlugin)
            .configure_sets(
                Update,
                items::material::AlphaDepthUpdate.after(sync_world_items),
            )
            .add_message::<CoreEvent>()
            .add_systems(
                Update,
                (
                    load_catalog,
                    map_bindings::adopt,
                    register_world_geometry.in_set(WorldServicesReady),
                    sync_static_life,
                    forage::prepare,
                    sync_world_items,
                    crops::sync,
                    forward_events.in_set(CoreEventsReady),
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
        replicas.geometry_map.clear();
        replicas.geometry_revision = 0;
        replicas.session_epoch = epoch.0;
        services.clear();
        replicas.map_name = config.map_name.clone();
    }
    if !model.connected || !catalog.items.is_empty() {
        return;
    }
    match bridge.request("catalog", json!({})).and_then(|response| {
        catalog.deployables = serde_json::from_value(response["result"]["deployables"].clone())?;
        catalog.gestures =
            serde_json::from_value(response["result"]["gestures"].clone()).unwrap_or_default();
        catalog.throw_motion = response["result"]["throwMotion"].clone();
        catalog.bullet_visuals =
            serde_json::from_value(response["result"]["bulletVisuals"].clone()).ok();
        catalog.mounted_weapons =
            serde_json::from_value(response["result"]["mountedWeapons"].clone())
                .unwrap_or_default();
        catalog.npc_crew = serde_json::from_value(response["result"]["npcCrew"].clone()).ok();
        catalog.vehicle_maintenance =
            serde_json::from_value(response["result"]["vehicleMaintenance"].clone()).ok();
        catalog.vehicle_lighting =
            serde_json::from_value(response["result"]["vehicleLighting"].clone()).ok();
        catalog.containers =
            serde_json::from_value(response["result"]["containers"].clone()).unwrap_or_default();
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
    options: Res<crate::LaunchOptions>,
    mut config: ResMut<WorldConfig>,
    world: Res<crate::world::WorldState>,
    geometry: Res<WorldGeometry>,
    mut replicas: ResMut<Replicas>,
    mut model: ResMut<UiModel>,
    mut services: ResMut<ServiceManifest>,
) {
    if !world.ready
        || !model.connected
        || !model.network.owns_world
        || geometry
            .holiday
            .as_deref()
            .is_some_and(|h| h != config.holiday)
        || (replicas.geometry_map == config.map_name
            && replicas.geometry_revision == geometry.revision)
    {
        return;
    }
    let result = (|| -> anyhow::Result<()> {
        if services.map != config.map_name
            || services.holiday.as_deref() != Some(config.holiday.as_str())
        {
            services.clear();
            if let Some(directory) = &config.finite_path {
                let map_root = options.0.unturned.as_ref().map(|root| root.join("Maps/PEI"));
                let reply = bridge.request("finiteServices", json!({"directory":directory,
                    "mapName":config.map_name,"contentRoot":config.content_root,"mapRoot":map_root}))?;
                services.adopt(&config.map_name, &config.holiday, &reply["result"]);
                info!("Original editor services: {} containers, {} resources",
                    reply["result"]["containerCount"], reply["result"]["resourceCount"]);
            } else if config.procedural_path.is_none() {
                if let Some(root) = &config.map_root {
                    let reply = bridge.request("mapServices", json!({"mapRoot":root,"mapName":config.map_name,"contentRoot":config.content_root}))?;
                    let result = &reply["result"];
                    services.adopt(&config.map_name, &config.holiday, result);
                    info!(
                        "Original map services: {} containers, {} fixtures, {} resources",
                        result["containerCount"], result["fixtureCount"], result["resourceCount"]
                    );
                }
            }
            services.map = config.map_name.clone();
            services.holiday = Some(config.holiday.clone());
        }
        bridge.request(
            "worldGeometry",
            json!({"replace":true,"finalize":false,"meshes":[],"terrainLayers":crate::world::layers::authority_grids(&geometry.terrain)}),
        )?;
        if geometry.finite.is_some() {
            for data in &geometry.exact_terrain {
                bridge.request("worldGeometry",json!({"replace":false,"finalize":false,"meshes":[{"vertices":data.positions,"terrain":true}]}))?;
            }
        } else {
            for patch in &geometry.terrain {
                for data in patch.mesh_by_layer(geometry.loaded_center, geometry.loaded_radius) {
                    if data.positions.is_empty() {
                        continue;
                    }
                    bridge.request("worldGeometry",json!({"replace":false,"finalize":false,"meshes":[{"vertices":data.positions,"terrain":true}]}))?;
                }
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
        if let Some(population) = &config.finite_population {
            let map_root = options.0.unturned.as_ref()
                .map(|root| root.join("Maps/PEI"))
                .ok_or_else(|| anyhow::anyhow!("Fresh finite populations require original PEI spawn tables"))?;
            let result = bridge.request("finitePopulation", json!({
                "mapName":config.map_name,"mapRoot":map_root,"population":population
            }))?;
            info!("Original generated populations: {}",result["result"]);
            config.finite_population = None;
        }
        Ok(())
    })();
    match result {
        Ok(()) => {
            replicas.geometry_map = config.map_name.clone();
            replicas.geometry_revision = geometry.revision;
            replicas.setup_failed = false;
        }
        Err(error) => {
            replicas.setup_failed = true;
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
            Option<&forage::Part>,
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
    for (entity, resource, mut visibility, disabled, part) in &mut resources {
        let Some(row) = services
            .resources
            .get(&(resource.name.clone(), resource.instance_index))
        else {
            continue;
        };
        let Some(index) = row["index"].as_u64() else { continue; };
        let keep_leaves = row["rewardItemId"].as_u64().unwrap_or(0) > 0
            && part.is_some_and(|part| !part.last);
        set_alive(
            &mut commands,
            entity,
            &mut visibility,
            disabled.is_some(),
            keep_leaves || !taken.contains(&index),
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
            commands
                .entity(entity)
                .remove::<items::material::AlphaDepth>();
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
    items: Query<(Entity, &Transform, &WorldItemReplica, &items::Focus)>,
    mut focus: ResMut<InteractionFocus>,
    context: bevy_rapier3d::prelude::ReadRapierContext,
    player: Query<Entity, With<crate::player::PlayerBody>>,
    objects: Query<&crate::world::WorldObject>,
    resources: Query<&crate::world::WorldResource>,
    services: Res<ServiceManifest>,
    occupancy: Option<Res<crate::vehicle::Occupancy>>,
    forage: forage::Plants,
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
    forage::offer(&forage, &services, &catalog, origin, direction, wall_distance, &mut focus);
    let nearest = items
        .iter()
        .filter_map(|(entity, transform, item, shape)| {
            shape
                .ray(transform, origin, direction, wall_distance)
                .map(|distance| (distance, entity, item))
        })
        .min_by(|a, b| a.0.total_cmp(&b.0));
    if let Some((distance, entity, item)) = nearest {
        let visual = catalog.items.get(&item.item_id);
        let name = visual.map_or("Item", |item| item.name.as_str());
        let args = json!({"netId":item.net_id});
        focus.offer(distance, format!("Pick up {name}"), "pickupItem", args.clone(), false);
        // WorldItem.SetFocused: the rim takes the item's rarity colour.
        let rarity = items::rarity_color(visual.map_or("", |v| v.rarity.as_str()));
        focus.outline("pickupItem", &args, entity, rarity);
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
                let args = json!({"netId":id});
                focus.offer(
                    wall_distance,
                    format!("Open {}", object.asset_name.replace('_', " ")),
                    "openStorage",
                    args.clone(),
                    true,
                );
                // StoreShelf.SetShelfFocused: a container is always a plain white rim.
                focus.outline("openStorage", &args, entity, Color::WHITE);
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
    model.interaction = if candidate.show_binding {
        format!(
            "[{}] {}",
            settings.bindings[&GameAction::Interact].label(),
            candidate.label
        )
    } else {
        candidate.label.clone()
    };
    // PlayerController: Interact while ALT-looking interacts with nothing.
    if candidate.manual
        || !settings.just_pressed(GameAction::Interact, &keys, &mouse)
        || settings.pressed(GameAction::FreeLook, &keys, &mouse)
    {
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

#[cfg(test)]
mod manual_interaction_tests;
#[cfg(test)]
mod finite_tests;
#[cfg(test)]
mod outline_visual;
