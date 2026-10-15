//! Accepted resource facts drive presentation only. ResourceServices retains
//! health, rewards and regrowth; no native damage or spawn publication occurs.
use super::{fall::Fall, reaction::Reaction, *};
use crate::{
    content::ContentCache,
    entities::{CollectInteractions, CoreEvent, ServiceManifest},
    impact::surfaces::{Surface, SurfaceOverride},
    player::{GameSnapshot, WorldSessionEpoch, simulation_paused, vector},
    ui::{UiModel, UiState},
    world::{WorldConfig, WorldGeometry, WorldResource, rain::roof::RoofMap, water::Reflective},
};
use bevy::{
    audio::{Decodable, Source},
    ecs::message::MessageCursor,
};
use bevy_rapier3d::prelude::*;
use std::collections::HashSet;

pub struct ResourcePresentationPlugin;
impl Plugin for ResourcePresentationPlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<Registry>()
            .add_systems(Update, present.after(CollectInteractions).after(convert))
            .add_systems(Update, super::timber::update.after(present));
    }
}
#[derive(Clone)]
struct Tree {
    name: String,
    instance: usize,
    transform: Transform,
    canopies: Vec<Entity>,
}
struct Remains {
    identity: (String, usize),
    stump: Entity,
    body: Entity,
    debris: Option<Entity>,
}
struct PendingFall {
    identity: (String, usize),
    direction: Vec3,
    lifetime: f32,
    age: f32,
}
#[derive(Resource)]
struct Registry {
    epoch: u64,
    geometry: u64,
    manifest: u64,
    trees: HashMap<u64, Tree>,
    remains: HashMap<u64, Remains>,
    pending: HashMap<u64, PendingFall>,
    events: MessageCursor<CoreEvent>,
    sequence: u64,
    timber: Option<(Handle<AudioSource>, f32)>,
}
impl Default for Registry {
    fn default() -> Self {
        Self {
            epoch: u64::MAX,
            geometry: u64::MAX,
            manifest: u64::MAX,
            trees: default(),
            remains: default(),
            pending: default(),
            events: default(),
            sequence: 0,
            timber: None,
        }
    }
}
#[derive(Component)]
struct Hit(f32);

fn remove(world: &mut World, remains: Remains) {
    let _ = world.despawn(remains.stump);
    let _ = world.despawn(remains.body);
    if let Some(debris) = remains.debris {
        let _ = world.despawn(debris);
    }
}
fn present(world: &mut World) {
    world.resource_scope(|world, mut registry: Mut<Registry>| {
        let epoch = world.resource::<WorldSessionEpoch>().0;
        let connected = world.resource::<UiModel>().connected;
        if registry.epoch != epoch || !connected {
            for (_, remains) in registry.remains.drain() { remove(world, remains); }
            let debris = world.query_filtered::<Entity, With<Fall>>().iter(world).collect::<Vec<_>>();
            for entity in debris { let _ = world.despawn(entity); }
            registry.trees.clear(); registry.geometry = u64::MAX; registry.manifest = u64::MAX;
            registry.pending.clear();
            registry.epoch = epoch; registry.sequence = 0;
            let entities = world.query_filtered::<Entity, With<Hit>>().iter(world).collect::<Vec<_>>();
            for entity in entities { world.entity_mut(entity).remove::<(Hit, Reaction)>(); }
        }
        let events = registry.events.read(world.resource::<Messages<CoreEvent>>()).cloned().collect::<Vec<_>>();
        if !connected { return; }
        let geometry = world.resource::<WorldGeometry>().revision;
        let manifest = world.resource::<ServiceManifest>().revision;
        if registry.geometry != geometry || registry.manifest != manifest {
            let mapping = world.resource::<ServiceManifest>().resources.iter()
                .filter_map(|(key, value)| value["index"].as_u64().map(|i| (key.clone(),i)))
                .collect::<HashMap<_,_>>();
            registry.trees.clear();
            for (entity, resource, transform, surface) in world.query_filtered::<
                (Entity, &WorldResource, &Transform, Option<&FoliageSurface>), With<Mesh3d>>().iter(world) {
                if !crate::world::is_tree(&resource.name) { continue; }
                let Some(&index) = mapping.get(&(resource.name.clone(),resource.instance_index)) else { continue; };
                let tree = registry.trees.entry(index).or_insert_with(|| Tree {
                    name: resource.name.clone(), instance: resource.instance_index, transform: *transform, canopies: vec![] });
                if surface.is_some() { tree.canopies.push(entity); }
            }
            registry.geometry = geometry; registry.manifest = manifest;
        }
        let dead = world.resource::<GameSnapshot>().0["resources"]["dead"].as_array()
            .into_iter().flatten().filter_map(|v| v.as_u64()).collect::<HashSet<_>>();
        let remove_indices = registry.remains.iter().filter(|(i,r)| !dead.contains(i)
            || registry.trees.get(i).is_none_or(|t| r.identity != (t.name.clone(),t.instance)))
            .map(|(&i,_)|i).collect::<Vec<_>>();
        for index in remove_indices {
            if let Some(mut remains) = registry.remains.remove(&index) {
                // Source Regrow removes stump/collider, while the already
                // scheduled 11-second debris cleanup retains its own lifetime.
                if !dead.contains(&index) && registry.trees.get(&index).is_some_and(|t| remains.identity == (t.name.clone(),t.instance)) { remains.debris = None; }
                remove(world, remains);
            }
        }
        let config = world.resource::<WorldConfig>().clone();
        for index in &dead {
            if registry.remains.contains_key(index) { continue; }
            let Some(tree) = registry.trees.get(index).cloned() else { continue; };
            let remains = stump(world, &config, *index, &tree);
            registry.remains.insert(*index, remains);
        }
        let paused = simulation_paused(world.resource::<UiState>());
        let dt = if paused { 0. } else { world.resource::<Time>().delta_secs() };
        for pending in registry.pending.values_mut() { pending.age += dt; }
        let mut expired = vec![];
        for (entity, mut hit, mut reaction) in world.query::<(Entity, &mut Hit, &mut Reaction)>().iter_mut(world) {
            hit.0 *= (-5.5*dt).exp();
            reaction.shake = hit.0;
            if hit.0 < 0.01 { expired.push(entity); }
        }
        for entity in expired { world.entity_mut(entity).remove::<(Hit, Reaction)>(); }
        // Process after decay so every accepted hit writes a full 1 immediately.
        for event in events {
            if event.kind != "resourceHit" && event.kind != "resourceFelled" { continue; }
            if event.sequence <= registry.sequence { continue; }
            registry.sequence = event.sequence;
            if event.data["kind"].as_str() != Some("tree") { continue; }
            let Some(index) = event.data["index"].as_u64() else { continue; };
            let Some(tree) = registry.trees.get(&index).cloned() else { continue; };
            // Remote facts carry elapsed source time known at receipt. A late
            // event must not restart either the shake or the debris lifetime.
            let age = event.data["ageSeconds"].as_f64().filter(|v| v.is_finite())
                .unwrap_or(0.).max(0.) as f32;
            if event.kind == "resourceHit" {
                if dead.contains(&index) { continue; }
                let shake = (-5.5 * age).exp();
                if shake < 0.01 { continue; }
                for canopy in tree.canopies {
                    if let Ok(mut entity) = world.get_entity_mut(canopy) {
                        entity.insert((Hit(shake), Reaction { shake, ..default() }));
                    }
                }
            } else {
                // A remote dead bit alone cannot supply a direction: it gets a
                // persistent stump, never an invented/random felling animation.
                if registry.remains.get(&index).is_some_and(|r| r.debris.is_some()) { continue; }
                let Some(direction) = vector(&event.data["direction"]) else { continue; };
                let lifetime = event.data["debrisSeconds"].as_f64().unwrap_or(11.) as f32;
                registry.pending.insert(index, PendingFall {
                    identity: (tree.name.clone(),tree.instance), direction,lifetime,age });
            }
        }
        // Reliable accepted events may precede the 25Hz resource snapshot.
        // Retain their exact direction rather than dropping the event or
        // inferring a direction later from a dead/alive transition.
        for (index,pending) in std::mem::take(&mut registry.pending) {
            let Some(tree) = registry.trees.get(&index).cloned() else { continue; };
            if pending.identity != (tree.name.clone(),tree.instance) || pending.age >= pending.lifetime { continue; }
            if !dead.contains(&index) { registry.pending.insert(index,pending); continue; }
            let entity = debris(world,&config,index,&tree,pending.direction,pending.lifetime);
            if pending.age > 0. {
                let mut fall = world.get_mut::<Fall>(entity).unwrap();
                let mut remaining = pending.age;
                while remaining > 0. { let step=remaining.min(1./60.); fall.step(step); remaining-=step; }
            }
            if let Some(remains) = registry.remains.get_mut(&index) { remains.debris=Some(entity); }
            if registry.timber.is_none() { registry.timber=load_timber(world,&config); }
        }
        if dt > 0. {
            let mut outputs = vec![];
            let mut retired = vec![];
            let sound = registry.timber.clone();
            for (entity, mut fall, mut transform) in world.query::<(Entity, &mut Fall, &mut Transform)>().iter_mut(world) {
                let (pose, leaf, trunk) = fall.step(dt);
                *transform = pose;
                let play = sound.as_ref().is_some_and(|(_, length)|
                    !fall.sound_played && fall.age >= (super::fall::predict().0-*length).max(0.));
                if play { fall.sound_played = true; }
                let sound_age = sound.as_ref().map_or(0., |(_, length)|
                    (fall.age-(super::fall::predict().0-*length).max(0.)).max(0.));
                outputs.push((fall.leaf, leaf, fall.trunk, trunk, play.then_some((fall.base.translation,sound_age))));
                if fall.age >= fall.lifetime { retired.push(entity); }
            }
            for (leaf, a, trunk, b, at) in outputs {
                if let Some(e) = leaf { if let Ok(mut e) = world.get_entity_mut(e) { e.insert(a); } }
                if let Some(e) = trunk { if let Ok(mut e) = world.get_entity_mut(e) { e.insert(b); } }
                if let (Some((at,age)), Some((clip,length))) = (at,sound.as_ref()) {
                    super::timber::play(world, clip.clone(), *length, at, epoch, age);
                }
            }
            for entity in retired { let _ = world.despawn(entity); }
        }
    });
}
fn directory(config: &WorldConfig) -> &'static str {
    match config.map_name.as_str() {
        "Washington" => "resources_washington",
        "Yukon" => "resources_yukon",
        _ => "resources",
    }
}
fn parts(
    world: &mut World,
    config: &WorldConfig,
    parent: Entity,
    name: &str,
    suffix: &str,
) -> (Vec<Entity>, Vec3, Vec3) {
    let directory = directory(config);
    let assets = world.resource::<AssetServer>().clone();
    let mut entities = vec![];
    let mut min = Vec3::splat(f32::INFINITY);
    let mut max = Vec3::splat(f32::NEG_INFINITY);
    for i in 0..12 {
        let stem = format!("{directory}/{name}_{suffix}_{i}");
        let path = config.content_root.join(format!("{stem}.obj"));
        if !path.is_file() {
            break;
        }
        let loaded = world.resource_scope(|world, mut cache: Mut<ContentCache>| {
            cache.load_mesh(&path, &mut world.resource_mut::<Assets<Mesh>>())
        });
        let Ok((mesh, data)) = loaded else {
            break;
        };
        min = min.min(data.min);
        max = max.max(data.max);
        let texture = format!("{stem}_tex.png");
        let material = world.resource_scope(|world, mut cache: Mut<ContentCache>| {
            let alpha =
                suffix == "debris" || cache.has_cutout_alpha(&config.content_root.join(&texture));
            cache.material(
                Some(&texture),
                alpha,
                &assets,
                &mut world.resource_mut::<Assets<StandardMaterial>>(),
            )
        });
        let mut base = world
            .resource::<Assets<StandardMaterial>>()
            .get(&material)
            .unwrap()
            .clone();
        base.cull_mode = None;
        base.double_sided = true;
        base.perceptual_roughness = 0.9;
        if !matches!(base.alpha_mode, AlphaMode::Opaque) {
            base.alpha_mode = AlphaMode::Mask(if suffix == "debris" && i < 2 { 0.4 } else { 0.5 });
        }
        let material = world.resource_mut::<Assets<StandardMaterial>>().add(base);
        let mut entity = world.spawn((
            Name::new(stem),
            Mesh3d(mesh),
            MeshMaterial3d(material),
            Transform::IDENTITY,
            ChildOf(parent),
            Reflective,
        ));
        if suffix == "debris" && i < 2 {
            entity.insert((
                FoliageSurface::canopy(),
                Reaction {
                    sway: if i == 0 { 0.05 } else { 0. },
                    ..default()
                },
            ));
        }
        entities.push(entity.id());
    }
    (entities, min, max)
}
fn stump(world: &mut World, config: &WorldConfig, index: u64, tree: &Tree) -> Remains {
    let stump = world
        .spawn((
            Name::new(format!("{} stump {index}", tree.name)),
            tree.transform,
            Visibility::default(),
        ))
        .id();
    let (_, _, max) = parts(world, config, stump, &tree.name, "stump");
    let top = if max.y.is_finite() && max.y > 0.01 {
        max.y
    } else {
        1.6
    };
    let (half, offset) = stump_shape(&tree.name, tree.transform.scale, top);
    let body = world
        .spawn((
            Name::new(format!("{} stump collider {index}", tree.name)),
            RigidBody::Fixed,
            Collider::cuboid(half.x, half.y, half.z),
            CollisionGroups::new(Group::GROUP_1, Group::ALL),
            SurfaceOverride(Surface::Wood),
            Transform {
                translation: tree.transform.translation + tree.transform.rotation * offset,
                rotation: tree.transform.rotation,
                scale: Vec3::ONE,
            },
        ))
        .id();
    if let Some(mut roof) = world.get_resource_mut::<RoofMap>() {
        roof.invalidate(tree.transform.translation, half.x + 1.);
    }
    Remains {
        identity: (tree.name.clone(), tree.instance),
        stump,
        body,
        debris: None,
    }
}
fn stump_shape(name: &str, scale: Vec3, top: f32) -> (Vec3, Vec3) {
    let r = crate::world::tree_radius(name) * scale.x.abs().max(scale.z.abs());
    (
        Vec3::new(r, (top + 1.5) * scale.y.abs() * 0.5, r),
        Vec3::Y * ((top - 1.5) * scale.y.abs() * 0.5),
    )
}
fn debris(
    world: &mut World,
    config: &WorldConfig,
    index: u64,
    tree: &Tree,
    direction: Vec3,
    lifetime: f32,
) -> Entity {
    let entity = world
        .spawn((
            Name::new(format!("{} falling debris {index}", tree.name)),
            tree.transform,
            Visibility::default(),
        ))
        .id();
    let (parts, min, max) = parts(world, config, entity, &tree.name, "debris");
    let length = if min.is_finite() && max.is_finite() {
        ((max.y - min.y) * tree.transform.scale.y.abs().max(0.01)).max(1.)
    } else {
        8.
    };
    let mut fall = Fall::new(
        tree.transform,
        direction,
        length,
        crate::world::tree_radius(&tree.name),
        lifetime,
    );
    fall.leaf = parts.first().copied();
    fall.trunk = parts.get(1).copied();
    world.entity_mut(entity).insert(fall);
    entity
}
fn load_timber(world: &mut World, config: &WorldConfig) -> Option<(Handle<AudioSource>, f32)> {
    let mut bank = crate::sound::Bank::load(&config.content_root, "explosions", "birch_0");
    let path = bank.pick(0)?;
    let audio = AudioSource {
        bytes: std::fs::read(config.content_root.join(path)).ok()?.into(),
    };
    let length = audio.decoder().total_duration()?.as_secs_f32();
    Some((
        world.resource_mut::<Assets<AudioSource>>().add(audio),
        length,
    ))
}
pub(super) fn shift_origin(world: &mut World, shift: Vec3) {
    if let Some(mut registry) = world.get_resource_mut::<Registry>() {
        for tree in registry.trees.values_mut() {
            tree.transform.translation -= shift;
        }
    }
    for mut fall in world.query::<&mut Fall>().iter_mut(world) {
        fall.base.translation -= shift;
    }
}

#[cfg(test)]
mod tests;
#[cfg(test)]
mod visual;
