//! Source NPC puppets: shared human rig, authored placement, look focus and console spawn.
use super::{
    ApplyInteractions, CollectInteractions, InteractionFocus, ServiceManifest, WorldServicesReady,
};
use crate::{
    bridge::CoreBridge,
    character::{
        Appearance, CharacterLayers, GearItem, Wardrobe,
        nameplate::{Nameplate, NameplateEnabled},
    },
    player::{PlayerBody, PlayerCamera, PlayerInput, WorldSessionEpoch},
    rig::{RigAnimation, RigLibrary, spawn_rig},
    settings::{GameAction, Settings},
    ui::{UiIntent, UiModel, UiState},
    world::{WorldConfig, WorldGeometry},
};
use bevy::{
    camera::{primitives::MeshAabb, visibility::RenderLayers},
    mesh::skinning::SkinnedMeshInverseBindposes,
    prelude::*,
    transform::TransformSystems,
};
use bevy_rapier3d::prelude::*;
use serde_json::{Value, json};
use std::collections::{HashMap, HashSet};

mod collision;

pub(super) struct NpcPlugin;
impl Plugin for NpcPlugin {
    fn build(&self, app: &mut App) {
        collision::install(app);
        app.init_resource::<Registry>()
            .init_resource::<CurrentNpcSpeaker>()
            .init_resource::<LookedAt>()
            .add_systems(
                Update,
                console
                    .after(sync)
                    .after(crate::player::PlayerCameraUpdate)
                    .before(RigAnimation),
            )
            .add_systems(Update, sync.after(WorldServicesReady).before(RigAnimation))
            .add_systems(
                Update,
                focus
                    .in_set(CollectInteractions)
                    .after(crate::player::PlayerCameraUpdate),
            )
            .add_systems(
                Update,
                activate
                    .after(CollectInteractions)
                    .before(ApplyInteractions),
            )
            .add_systems(
                PostUpdate,
                measure_headwear.after(TransformSystems::Propagate),
            );
    }
}

/// Presentation context only. Dialogue content/state always comes from accepted npcView.
#[derive(Resource, Default, Clone, Debug)]
pub(crate) struct CurrentNpcSpeaker {
    pub key: String,
    pub name: String,
    pub dialogue: i32,
    pub shop: String,
}
impl CurrentNpcSpeaker {
    fn from_character(c: &Value) -> Self {
        Self {
            key: c["key"].as_str().unwrap_or("").into(),
            name: c["name"].as_str().unwrap_or("Someone").into(),
            dialogue: c["dialogue"].as_i64().unwrap_or(0) as i32,
            shop: c["shop"].as_str().unwrap_or("").into(),
        }
    }
}
#[derive(Component)]
struct Npc {
    speaker: CurrentNpcSpeaker,
    plate: Entity,
    measured: bool,
}
struct Instance {
    placement: Value,
    entity: Entity,
}
#[derive(Resource, Default)]
struct Registry {
    epoch: u64,
    map: String,
    instances: HashMap<String, Instance>,
    diagnostic: Vec<Value>,
    catalog: Option<Value>,
}
#[derive(Resource, Default)]
struct LookedAt(Option<Entity>);

fn appearance(c: &Value) -> Appearance {
    let id = |slot: &str| {
        c[slot]
            .as_i64()
            .filter(|id| *id > 0)
            .map_or(-1, |id| id as i32)
    };
    let skin = c["skin"]
        .as_str()
        .map(str::trim)
        .filter(|s| s.len() == 7 && s.starts_with('#'))
        .and_then(|s| Srgba::hex(s).ok())
        .map(Color::Srgba)
        .unwrap_or(Color::srgb(0.82, 0.66, 0.52));
    Appearance {
        skin,
        shirt: id("shirt"),
        pants: id("pants"),
        gear: ["hat", "vest", "mask", "glasses", "backpack"]
            .into_iter()
            .map(id)
            .filter(|id| *id > 0)
            .collect(),
        face: Some(c["face"].as_i64().unwrap_or(0).clamp(0, 31) as u8),
        ..default()
    }
}

#[allow(clippy::too_many_arguments)]
fn sync(
    mut commands: Commands,
    mut registry: ResMut<Registry>,
    epoch: Res<WorldSessionEpoch>,
    services: Res<ServiceManifest>,
    model: Res<UiModel>,
    bridge: Res<CoreBridge>,
    config: Res<WorldConfig>,
    mut speaker: ResMut<CurrentNpcSpeaker>,
    mut rigs: ResMut<RigLibrary>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut binds: ResMut<Assets<SkinnedMeshInverseBindposes>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
) {
    if !model.connected || registry.epoch != epoch.0 || registry.map != services.map {
        for (_, instance) in registry.instances.drain() {
            commands.entity(instance.entity).try_despawn();
        }
        registry.diagnostic.clear();
        registry.catalog = None;
        registry.epoch = epoch.0;
        registry.map = services.map.clone();
        *speaker = default();
    }
    if !model.connected {
        return;
    }
    if registry.catalog.is_none() {
        registry.catalog = bridge
            .request("npcCatalog", json!({}))
            .ok()
            .map(|r| r["result"].clone());
    }
    let placements: Vec<_> = services
        .npcs
        .iter()
        .enumerate()
        .map(|(i, p)| (format!("map:{i}"), p.clone()))
        .chain(
            registry
                .diagnostic
                .iter()
                .enumerate()
                .map(|(i, p)| (format!("console:{i}"), p.clone())),
        )
        .collect();
    let mut live = HashSet::new();
    for (key, placement) in placements {
        live.insert(key.clone());
        if registry
            .instances
            .get(&key)
            .is_some_and(|i| i.placement == placement)
        {
            continue;
        }
        if let Some(old) = registry.instances.remove(&key) {
            commands.entity(old.entity).try_despawn();
        }
        let c = &placement["character"];
        let Some(position) = crate::player::vector(&placement["position"]) else {
            continue;
        };
        if !c.is_object() || c["key"].as_str().is_none() {
            continue;
        }
        let transform = Transform::from_translation(position).with_rotation(Quat::from_rotation_y(
            (placement["yaw"].as_f64().unwrap_or(0.) as f32).to_radians(),
        ));
        let material = materials.add(StandardMaterial::default());
        let entity = match spawn_rig(
            &mut commands,
            &mut rigs,
            &mut meshes,
            &mut binds,
            &config.content_root.join("rig.json"),
            material,
            transform,
            false,
            "Idle_Stand",
        ) {
            Ok(entity) => entity,
            Err(error) => {
                warn!("NPC {}: {error}", c["key"]);
                continue;
            }
        };
        let plate = commands
            .spawn((
                Name::new("NPC look nameplate"),
                Nameplate {
                    name: c["displayName"].as_str().unwrap_or("Someone").into(),
                    picture: None,
                },
                NameplateEnabled(false),
                Transform::from_xyz(0., Nameplate::HEIGHT, 0.),
                Visibility::Hidden,
                ChildOf(entity),
            ))
            .id();
        commands.entity(entity).insert((
            Name::new(format!("Npc_{}", c["key"].as_str().unwrap_or(""))),
            Npc {
                speaker: CurrentNpcSpeaker::from_character(c),
                plate,
                measured: false,
            },
            appearance(c),
            CharacterLayers(RenderLayers::layer(0)),
            RigidBody::Fixed,
            Collider::compound(vec![(
                Vec3::Y * 0.9,
                Quat::IDENTITY,
                Collider::capsule_y(0.9 - 0.35, 0.35),
            )]),
            CollisionGroups::new(Group::GROUP_1, Group::ALL),
        ));
        registry
            .instances
            .insert(key, Instance { placement, entity });
    }
    registry.instances.retain(|key, instance| {
        if live.contains(key) {
            true
        } else {
            commands.entity(instance.entity).try_despawn();
            false
        }
    });
}

fn focus(
    ui: Res<UiState>,
    occupancy: Option<Res<crate::vehicle::Occupancy>>,
    camera: Query<&Transform, With<PlayerCamera>>,
    player: Query<Entity, With<PlayerBody>>,
    context: ReadRapierContext,
    npcs: Query<&Npc>,
    mut plates: Query<&mut NameplateEnabled>,
    mut looked: ResMut<LookedAt>,
    mut interactions: ResMut<InteractionFocus>,
) {
    looked.0 = None;
    for npc in &npcs {
        if let Ok(mut gate) = plates.get_mut(npc.plate) {
            gate.0 = false;
        }
    }
    if ui.captures_input() || occupancy.is_some_and(|o| o.seat.is_some()) {
        return;
    }
    let (Ok(camera), Ok(player), Ok(context)) =
        (camera.single(), player.single(), context.single())
    else {
        return;
    };
    // Same current source LookTrace origin and 2.6m reach; first ordinary solid wins.
    let Some((entity, distance)) = context.cast_ray(
        camera.translation,
        *camera.forward(),
        2.6,
        true,
        QueryFilter::default()
            .exclude_collider(player)
            .exclude_sensors()
            .groups(CollisionGroups::new(
                Group::ALL,
                Group::GROUP_1 | Group::GROUP_4,
            )),
    ) else {
        return;
    };
    let Ok(npc) = npcs.get(entity) else {
        return;
    };
    looked.0 = Some(entity);
    if let Ok(mut gate) = plates.get_mut(npc.plate) {
        gate.0 = true;
    }
    interactions.offer(
        distance,
        format!("Talk to {}", npc.speaker.name),
        "npcTalk",
        json!({"dialogue":npc.speaker.dialogue}),
        false,
    );
}

fn has_dialogue(registry: &Registry, id: i32) -> bool {
    registry
        .catalog
        .as_ref()
        .and_then(|c| c["dialogues"].as_array())
        .is_some_and(|rows| rows.iter().any(|d| d["id"].as_i64() == Some(id as i64)))
}
fn activate(
    ui: Res<UiState>,
    settings: Res<Settings>,
    keys: Res<ButtonInput<KeyCode>>,
    mouse: Res<ButtonInput<MouseButton>>,
    looked: Res<LookedAt>,
    npcs: Query<&Npc>,
    mut interactions: ResMut<InteractionFocus>,
    registry: Res<Registry>,
    mut speaker: ResMut<CurrentNpcSpeaker>,
    mut model: ResMut<UiModel>,
) {
    if ui.captures_input() || !settings.just_pressed(GameAction::Interact, &keys, &mouse) {
        return;
    }
    let Some(npc) = looked.0.and_then(|e| npcs.get(e).ok()) else {
        return;
    };
    // F belongs to this NPC only if it won the look AND no higher F branch
    // (RequestEnterNearestPuppet) has taken the key.
    if !interactions.selected_args("npcTalk", &json!({"dialogue":npc.speaker.dialogue})) {
        return;
    }
    if has_dialogue(&registry, npc.speaker.dialogue) {
        *speaker = npc.speaker.clone();
    } else {
        model.notice(format!("{} has nothing to say.", npc.speaker.name));
        interactions.candidate = None;
    }
}

fn measure_headwear(
    mut npcs: Query<(Entity, &GlobalTransform, &mut Npc)>,
    gear: Query<(&GearItem, &Mesh3d, &GlobalTransform, &ChildOf)>,
    parents: Query<&ChildOf>,
    wardrobe: Res<Wardrobe>,
    meshes: Res<Assets<Mesh>>,
    mut plates: Query<&mut Transform, With<Nameplate>>,
) {
    for (root, pose, mut npc) in &mut npcs {
        if npc.measured {
            continue;
        }
        let mut top: f32 = 0.;
        for (item, mesh, transform, parent) in &gear {
            if !wardrobe
                .entries
                .get(&item.0)
                .is_some_and(|g| matches!(g.slot.as_str(), "hat" | "mask" | "glasses"))
            {
                continue;
            }
            let mut ancestor = parent.parent();
            while ancestor != root {
                let Ok(parent) = parents.get(ancestor) else {
                    break;
                };
                ancestor = parent.parent();
            }
            if ancestor != root {
                continue;
            }
            let Some(bounds) = meshes.get(&mesh.0).and_then(MeshAabb::get_aabb) else {
                continue;
            };
            let min = Vec3::from(bounds.min());
            let max = Vec3::from(bounds.max());
            for x in [min.x, max.x] {
                for y in [min.y, max.y] {
                    for z in [min.z, max.z] {
                        top = top.max(
                            transform.transform_point(Vec3::new(x, y, z)).y - pose.translation().y,
                        );
                    }
                }
            }
        }
        if let Ok(mut plate) = plates.get_mut(npc.plate) {
            plate.translation.y = Nameplate::above_gear(top);
            npc.measured = true;
        }
    }
}

pub(crate) fn is_console_command(text: &str) -> bool {
    text.split_whitespace()
        .next()
        .is_some_and(|word| word.eq_ignore_ascii_case("npc"))
}
fn console(
    mut intents: MessageReader<UiIntent>,
    mut registry: ResMut<Registry>,
    bridge: Res<CoreBridge>,
    player: Query<&Transform, With<PlayerBody>>,
    input: Res<PlayerInput>,
    geometry: Res<WorldGeometry>,
    mut model: ResMut<UiModel>,
    mut speaker: ResMut<CurrentNpcSpeaker>,
) {
    for intent in intents.read() {
        let UiIntent::Action { name, args } = intent else {
            continue;
        };
        let text = args["text"].as_str().unwrap_or("");
        if name != "console" || !is_console_command(text) {
            continue;
        }
        if !model.connected {
            model.notice("Join a world before spawning NPCs");
            continue;
        }
        if registry.catalog.is_none() {
            match bridge.request("npcCatalog", json!({})) {
                Ok(reply) => registry.catalog = Some(reply["result"].clone()),
                Err(e) => {
                    model.notice(e.to_string());
                    continue;
                }
            }
        }
        let characters = registry
            .catalog
            .as_ref()
            .and_then(|c| c["characters"].as_array())
            .cloned()
            .unwrap_or_default();
        let mut words: Vec<_> = text.split_whitespace().skip(1).collect();
        if words.is_empty() {
            let mut keys: Vec<_> = characters
                .iter()
                .filter_map(|c| c["key"].as_str())
                .collect();
            keys.sort();
            model.notice(format!("{} characters: {}", keys.len(), keys.join(", ")));
            continue;
        }
        let talk = words.len() > 1 && words.last().is_some_and(|s| s.eq_ignore_ascii_case("talk"));
        if talk {
            words.pop();
        }
        let mut distance = 2.;
        if words.len() > 1 {
            if let Some(d) = words
                .last()
                .and_then(|s| s.parse::<f32>().ok())
                .filter(|d| d.is_finite())
            {
                distance = d.clamp(1., 20.);
                words.pop();
            }
        }
        let want = words.join(" ").to_lowercase();
        let character = characters
            .iter()
            .find(|c| {
                c["key"]
                    .as_str()
                    .is_some_and(|k| k.eq_ignore_ascii_case(&want))
            })
            .or_else(|| {
                characters.iter().find(|c| {
                    c["key"]
                        .as_str()
                        .is_some_and(|k| k.to_lowercase().contains(&want))
                })
            });
        let Some(character) = character else {
            model.notice(format!("no character '{want}' -- try `npc` for the list"));
            continue;
        };
        let Ok(player) = player.single() else {
            continue;
        };
        // DevConsole uses Player.LookBasis, independent of free-look/lean camera.
        let forward = Quat::from_rotation_y(input.world_yaw().to_radians()) * Vec3::NEG_Z;
        let mut position = player.translation + forward * distance;
        if let Some(y) = geometry.terrain_height(position.x, position.z) {
            position.y = y;
        }
        let context = CurrentNpcSpeaker::from_character(character);
        registry
            .diagnostic
            .push(json!({"key":context.key,"position":position.to_array(),
            "yaw":forward.x.atan2(forward.z).to_degrees(),"character":character}));
        model.notice(format!(
            "{} ({}) -- dialogue {}{}",
            context.name,
            context.key,
            context.dialogue,
            if has_dialogue(&registry, context.dialogue) {
                ""
            } else {
                " (NOT in the catalog)"
            }
        ));
        if talk {
            if has_dialogue(&registry, context.dialogue) {
                *speaker = context.clone();
                if let Err(error) = bridge.request(
                    "action",
                    json!({"name":"npcTalk","args":{"dialogue":context.dialogue}}),
                ) {
                    model.notice(error.to_string());
                }
            } else {
                model.notice(format!("{} has nothing to say.", context.name));
            }
        }
    }
}

pub(crate) fn shift_origin(world: &mut World, shift: Vec3) {
    if let Some(mut registry) = world.get_resource_mut::<Registry>() {
        let shift_placement = |p: &mut Value| {
            if let Some(position) = crate::player::vector(&p["position"]) {
                p["position"] = json!((position - shift).to_array());
            }
        };
        for instance in registry.instances.values_mut() {
            shift_placement(&mut instance.placement);
        }
        for placement in &mut registry.diagnostic {
            shift_placement(placement);
        }
    }
}

#[cfg(test)]
mod tests;
