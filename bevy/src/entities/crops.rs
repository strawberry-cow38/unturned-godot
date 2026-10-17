//! Original CropNode / CropReplicaView / RequestHarvestNearestCrop. A replicated
//! crop is its Model_0 dirt base (the crops.tsv `_Color`) plus the Foliage_0
//! (young) or Foliage_1 (grown) billboard, stood up -90° about X. The stage is
//! the authority's tick-derived `grown`; F with no other target harvests the
//! nearest grown crop within 3 m through the existing HarvestCrop intent.
use super::*;
use crate::player::{PlayerBody, WorldSessionEpoch};
use crate::ui::UiIntent;
use bevy_rapier3d::prelude::{CollisionGroups, Group, QueryFilter, ReadRapierContext};
use std::sync::Arc;

/// CropManager.NearestGrown / RequestHarvestNearestCrop reach (player origin
/// to crop origin); the server's own CropReach (6 m) is wider.
pub(crate) const HARVEST_REACH: f32 = 3.;
/// CropNode.FoliRot: the billboards are authored lying down.
const FOLIAGE_STAND_DEGREES: f32 = -90.;
const DEFAULT_DIRT: [f32; 3] = [0.4, 0.32, 0.2];
/// PlayerController.LookReach.
const LOOK_REACH: f32 = 2.6;

pub(super) fn install(app: &mut App) {
    install_harvest(app);
    app.add_systems(Update, console.after(crate::player::PlayerCameraUpdate));
}
pub(crate) fn install_harvest(app: &mut App) {
    app.init_resource::<Crops>()
        .add_systems(
            Update,
            offer_harvest
                .after(CollectInteractions)
                .after(crate::vehicle::access::VehicleAccess)
                .before(ApplyInteractions),
        );
}

#[derive(Clone, Debug, PartialEq)]
pub(crate) struct CropDef {
    pub name: String,
    pub dirt: [f32; 3],
}

/// CropRegistry.Load over content/crops.tsv: name, seed id, dirt rgb, parts.
pub(crate) fn registry(source: &str) -> HashMap<u16, CropDef> {
    let mut seeds = HashMap::new();
    for line in source.lines() {
        let c: Vec<&str> = line.split('\t').collect();
        if c.len() < 3 {
            continue;
        }
        let rgb: Vec<f32> = c[2].split(',').filter_map(|v| v.trim().parse().ok()).collect();
        let dirt = if c[2].split(',').count() == 3 && rgb.len() == 3 {
            [rgb[0], rgb[1], rgb[2]]
        } else {
            DEFAULT_DIRT
        };
        if let Ok(seed) = c[1].parse::<u16>()
            && seed != 0
        {
            seeds.insert(seed, CropDef { name: c[0].to_owned(), dirt });
        }
    }
    seeds
}

#[derive(Component, Debug)]
pub(crate) struct CropReplica {
    pub net_id: u32,
    pub grown: bool,
    young: Option<Entity>,
    mature: Option<Entity>,
}
#[cfg(test)]
impl CropReplica {
    pub(crate) fn for_test(net_id: u32, grown: bool) -> Self {
        Self { net_id, grown, young: None, mature: None }
    }
}

type Part = Option<(Handle<Mesh>, Handle<StandardMaterial>)>;
#[derive(Default)]
struct Art {
    dirt: Part,
    young: Part,
    grown: Part,
}

#[derive(Resource, Default)]
pub(crate) struct Crops {
    entities: HashMap<u32, Entity>,
    defs: Option<Arc<HashMap<u16, CropDef>>>,
    art: HashMap<String, Art>,
    epoch: u64,
}

fn art(
    def: &CropDef,
    root: &std::path::Path,
    cache: &mut ContentCache,
    meshes: &mut Assets<Mesh>,
    materials: &mut Assets<StandardMaterial>,
    assets: &AssetServer,
) -> Art {
    let mesh = |cache: &mut ContentCache, meshes: &mut Assets<Mesh>, part: &str| {
        cache
            .load_mesh(&root.join(format!("crop_{}_{part}.txt", def.name)), meshes)
            .ok()
            .filter(|(_, data)| !data.positions.is_empty())
            .map(|(handle, _)| handle)
    };
    // Model_0: dirt `_Color`, roughness 1, CullMode.Disabled.
    let dirt = mesh(cache, meshes, "Model_0").map(|m| {
        let [r, g, b] = def.dirt;
        (
            m,
            materials.add(StandardMaterial {
                base_color: Color::srgb(r, g, b),
                perceptual_roughness: 1.,
                double_sided: true,
                cull_mode: None,
                ..default()
            }),
        )
    });
    // MakeFoliage: AlphaScissor 0.4, double-sided, nearest + mipmaps.
    let mut foliage = |stage: &str| {
        let m = mesh(cache, meshes, stage)?;
        let texture = format!("crop_{}_{stage}.png", def.name);
        let base_color_texture = root
            .join(&texture)
            .exists()
            .then(|| cache.nearest_mipmapped(&texture, assets));
        Some((
            m,
            materials.add(StandardMaterial {
                base_color_texture,
                alpha_mode: AlphaMode::Mask(0.4),
                perceptual_roughness: 1.,
                double_sided: true,
                cull_mode: None,
                ..default()
            }),
        ))
    };
    Art { young: foliage("Foliage_0"), grown: foliage("Foliage_1"), dirt }
}

/// CropReplicaView: diff-driven, the replica rows are the truth.
#[allow(clippy::too_many_arguments)]
pub(super) fn sync(
    mut commands: Commands,
    snapshot: Res<GameSnapshot>,
    model: Res<UiModel>,
    config: Res<WorldConfig>,
    epoch: Res<WorldSessionEpoch>,
    mut crops: ResMut<Crops>,
    mut cache: ResMut<ContentCache>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
    assets: Res<AssetServer>,
    mut replicas: Query<&mut CropReplica>,
) {
    if crops.epoch != epoch.0 || !model.connected {
        for (_, entity) in crops.entities.drain() {
            commands.entity(entity).despawn();
        }
        crops.epoch = epoch.0;
        if !model.connected {
            return;
        }
    }
    let defs = crops
        .defs
        .get_or_insert_with(|| {
            Arc::new(registry(&std::fs::read_to_string(config.content_root.join("crops.tsv")).unwrap_or_default()))
        })
        .clone();
    let mut seen = HashSet::new();
    for row in snapshot.0["crops"].as_array().into_iter().flatten() {
        let (Some(id), Some(seed)) = (row["netId"].as_u64(), row["seedId"].as_u64()) else {
            continue;
        };
        // Unknown seed: nothing to grow into, never materialized.
        let Some(def) = defs.get(&(seed as u16)) else {
            continue;
        };
        let Some(position) = serde_json::from_value::<[f32; 3]>(row["position"].clone())
            .ok()
            .map(Vec3::from_array)
            .filter(|p| p.is_finite())
        else {
            continue;
        };
        let (id, grown) = (id as u32, row["grown"] == true);
        seen.insert(id);
        if let Some(mut replica) = crops.entities.get(&id).and_then(|e| replicas.get_mut(*e).ok()) {
            if replica.grown != grown {
                replica.grown = grown;
                for (part, shown) in [(replica.young, !grown), (replica.mature, grown)] {
                    if let Some(part) = part {
                        commands.entity(part).insert(visible(shown));
                    }
                }
            }
            continue;
        }
        if !crops.art.contains_key(&def.name) {
            let built = art(def, &config.content_root, &mut cache, &mut meshes, &mut materials, &assets);
            crops.art.insert(def.name.clone(), built);
        }
        let art = &crops.art[&def.name];
        let root = commands
            .spawn((
                Name::new(format!("Crop {} #{id}", def.name)),
                Transform::from_translation(position),
                Visibility::default(),
            ))
            .id();
        let mut part = |art: &Part, rotation: Quat, shown: bool| {
            art.clone().map(|(mesh, material)| {
                commands
                    .spawn((
                        Mesh3d(mesh),
                        MeshMaterial3d(material),
                        Transform::from_rotation(rotation),
                        visible(shown),
                        ChildOf(root),
                    ))
                    .id()
            })
        };
        let stand = Quat::from_rotation_x(FOLIAGE_STAND_DEGREES.to_radians());
        part(&art.dirt, Quat::IDENTITY, true);
        let young = part(&art.young, stand, !grown);
        let mature = part(&art.grown, stand, grown);
        commands.entity(root).insert(CropReplica { net_id: id, grown, young, mature });
        crops.entities.insert(id, root);
    }
    // The server retired it (harvest/despawn): the visual leaves too.
    crops.entities.retain(|id, entity| {
        let keep = seen.contains(id);
        if !keep {
            commands.entity(*entity).despawn();
        }
        keep
    });
}
fn visible(shown: bool) -> Visibility {
    if shown { Visibility::Inherited } else { Visibility::Hidden }
}

/// RequestHarvestNearestCrop: below every look target, above the shelf/crate
/// branches, and without a prompt of its own.
#[allow(clippy::too_many_arguments)]
pub(super) fn offer_harvest(
    ui: Res<UiState>,
    snapshot: Res<GameSnapshot>,
    occupancy: Option<Res<crate::vehicle::Occupancy>>,
    player: Query<&Transform, With<PlayerBody>>,
    replicas: Query<(&CropReplica, &Transform)>,
    mut focus: ResMut<InteractionFocus>,
) {
    if ui.captures_input()
        || occupancy.is_some_and(|o| o.seat.is_some())
        || snapshot.0["player"]["alive"] != true
        || snapshot.0["player"]["vehicleNetId"].as_u64().unwrap_or(0) != 0
    {
        return;
    }
    let Ok(player) = player.single() else {
        return;
    };
    if let Some(id) = nearest_grown(player.translation, replicas.iter().map(|(c, t)| (c.net_id, c.grown, t.translation))) {
        focus.fall_through("harvestCrop", json!({"netId":id}));
    }
}
pub(crate) fn nearest_grown(from: Vec3, crops: impl Iterator<Item = (u32, bool, Vec3)>) -> Option<u32> {
    let mut best = None;
    let mut best_d = HARVEST_REACH * HARVEST_REACH;
    for (id, grown, at) in crops {
        let d = from.distance_squared(at);
        if grown && d < best_d {
            best_d = d;
            best = Some(id);
        }
    }
    best
}

pub(crate) fn is_console_command(text: &str) -> bool {
    text.split_whitespace().next().is_some_and(|verb| verb.eq_ignore_ascii_case("plant"))
}

/// PlayerController.LookPoint: the camera ray to 2.6 m against world,
/// vehicles and props (source mask bits 0/5/6), else the ray end.
pub(crate) fn look_point(camera: &Transform, cast: impl FnOnce(Vec3, Vec3, f32) -> Option<f32>) -> Vec3 {
    let direction = *camera.forward();
    camera.translation + direction * cast(camera.translation, direction, LOOK_REACH).unwrap_or(LOOK_REACH)
}

/// DevConsole `plant`: the source resolves `at` on the planting client, so the
/// native look point travels with the console text (host and joined alike).
fn console(
    mut intents: MessageReader<UiIntent>,
    bridge: Res<CoreBridge>,
    camera: Query<&Transform, With<PlayerCamera>>,
    player: Query<Entity, With<PlayerBody>>,
    context: ReadRapierContext,
    mut model: ResMut<UiModel>,
) {
    for intent in intents.read() {
        let UiIntent::Action { name, args } = intent else {
            continue;
        };
        if name != "console" || !is_console_command(args["text"].as_str().unwrap_or("")) {
            continue;
        }
        let mut args = args.clone();
        if let Ok(camera) = camera.single() {
            let context = context.single().ok();
            let player = player.single().ok();
            let at = look_point(camera, |from, dir, reach| {
                let mut filter = QueryFilter::default()
                    .exclude_sensors()
                    .groups(CollisionGroups::new(Group::ALL, Group::GROUP_1 | Group::GROUP_4));
                if let Some(player) = player {
                    filter = filter.exclude_collider(player).exclude_rigid_body(player);
                }
                context?.cast_ray(from, dir, reach, true, filter).map(|(_, d)| d)
            });
            if at.is_finite() {
                args["at"] = json!(at.to_array());
            }
        }
        if let Err(e) = bridge.request("action", json!({"name":"console","args":args})) {
            model.notice(e.to_string());
        }
    }
}

#[cfg(test)]
mod tests;
