//! Original StoreShelf display + ShelfItemBody grab. A replicated OPEN-tier
//! container (ContainerSchema `display`) shows its server display digest as
//! the items' dropped models on its tiers (StorageReplicaView -> ApplyDisplay ->
//! SyncDisplay -> PlaceItem). Each model is a look target; F asks the server
//! for that one cell (CommandTakeFromStorage 45) and the model leaves only
//! when the accepted digest no longer carries it. Focus floats the item's
//! name tag; the source focus outline (OutlineOverlay) is not ported yet.
use super::*;
use crate::{
    character::nameplate::raster::Raster,
    info_billboard::{Face, PanelMaterial},
    player::{PlayerBody, WorldSessionEpoch},
};
use bevy::{
    camera::visibility::{NoFrustumCulling, VisibilityRange},
    image::ImageSampler,
    light::{NotShadowCaster, NotShadowReceiver},
    pbr::ExtendedMaterial,
    text::TextPipeline,
};
use bevy_rapier3d::prelude::{QueryFilter, ReadRapierContext};
use std::sync::Arc;

pub(crate) mod layout;
use layout::{Facts, Pose};

/// PlayerController.LookReach / LookSphereR.
pub(crate) const LOOK_REACH: f32 = 2.6;
pub(crate) const LOOK_SPHERE: f32 = 0.16;
/// StorageCrate.RenderDist: shelf models cull past it too.
const RENDER_DIST: f32 = 80.;
/// The name tag: Label3D PixelSize 0.006, FontSize 64, OutlineSize 10.
const TAG_PIXEL: f32 = 0.006;
const TAG_FONT: f32 = 64.;
/// Godot's FreeType stroke radius is outline_size * 16 / 64 pixels.
const TAG_OUTLINE: f32 = 10. / 4.;
pub(crate) const TAKE: &str = "takeFromStorage";

/// ContainerSchema.Kind as the catalogue sends it (the whole schema row;
/// the display only reads mesh/display/width).
#[derive(Clone, Debug, Deserialize)]
#[allow(dead_code)]
#[serde(rename_all = "camelCase")]
pub struct ContainerKind {
    pub kind_id: u16,
    pub mesh: String,
    pub display: bool,
    pub label: String,
    pub width: u8,
    pub height: u8,
}

pub(super) fn install(app: &mut App) {
    app.init_resource::<Shelves>()
        .init_resource::<FocusedShelfItem>()
        .configure_sets(Update, items::material::AlphaDepthUpdate.after(sync))
        .add_systems(
            Update,
            sync.after(super::sync_world_items).before(CollectInteractions),
        )
        .add_systems(
            Update,
            offer
                .in_set(CollectInteractions)
                .after(crate::player::PlayerCameraUpdate),
        )
        .add_systems(
            Update,
            tag.after(ApplyInteractions).run_if(resource_exists::<TextPipeline>),
        );
}

/// One shelf's display root (the StoreShelf node: container position, yawed).
#[derive(Component)]
pub(crate) struct ShelfRoot;
/// ShelfItemBody: a placed model, linked back to its shelf and grid cell.
#[derive(Component, Clone, Debug)]
pub(crate) struct ShelfItem {
    pub net_id: u32,
    pub x: u8,
    pub y: u8,
    #[allow(dead_code)]
    pub item_id: u16,
    pub name: String,
    /// The tag's Modulate: the rarity colour lerped 35% toward white.
    pub tint: Color,
    /// The look hitbox: the mesh AABB scaled 1.1, in model space.
    pub center: Vec3,
    pub half: Vec3,
    /// The tag anchor in shelf space.
    pub tag: Vec3,
}
struct Entry {
    root: Entity,
    pose: (Vec3, f32),
    cells: HashMap<u16, Entity>,
}
#[derive(Resource, Default)]
pub(crate) struct Shelves {
    entries: HashMap<u32, Entry>,
    poses: Option<Arc<HashMap<u16, Pose>>>,
    stood: HashMap<String, Option<(Vec3, Vec3)>>,
    epoch: u64,
}

/// One digest cell as SyncDisplay sees it: the item's grid anchor, its
/// rotation-adjusted width, and the item id.
#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) struct Cell {
    pub x: u8,
    pub y: u8,
    pub eff_w: u8,
    pub item_id: u16,
}
impl Cell {
    pub fn key(&self) -> u16 {
        (u16::from(self.x) << 8) | u16::from(self.y)
    }
}
/// ApplyDisplay: digest Cell = y*Width+x; an item the asset table cannot
/// make (Assets.makeLoot null) is skipped. SyncDisplay's footprint swaps
/// size_x/size_y on an odd rotation.
pub(crate) fn cells(display: &Value, width: u8, catalog: &ItemCatalog) -> Vec<Cell> {
    if width == 0 {
        return Vec::new();
    }
    display
        .as_array()
        .into_iter()
        .flatten()
        .filter_map(|d| {
            let cell = u8::try_from(d["cell"].as_u64()?).ok()?;
            let item_id = u16::try_from(d["itemId"].as_u64()?).ok()?;
            let rot = d["rot"].as_u64().unwrap_or(0);
            let item = catalog.items.get(&item_id)?;
            let w = if rot % 2 == 1 { item.height } else { item.width };
            Some(Cell { x: cell % width, y: cell / width, eff_w: w.max(1), item_id })
        })
        .collect()
}

/// StorageReplicaView + StoreShelf.ApplyDisplay/SyncDisplay: diff-driven.
/// A cell keeps its model while its key stays in the digest (the source's
/// STABLE slot), even if the item in it changed; gone keys despawn.
#[allow(clippy::too_many_arguments)]
pub(super) fn sync(
    mut commands: Commands,
    snapshot: Res<GameSnapshot>,
    model: Res<UiModel>,
    config: Res<WorldConfig>,
    epoch: Res<WorldSessionEpoch>,
    catalog: Res<ItemCatalog>,
    mut shelves: ResMut<Shelves>,
    mut models: ResMut<items::Models>,
    mut cache: ResMut<ContentCache>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
    mut images: ResMut<Assets<Image>>,
) {
    if shelves.epoch != epoch.0 || !model.connected {
        for (_, entry) in shelves.entries.drain() {
            commands.entity(entry.root).try_despawn();
        }
        shelves.epoch = epoch.0;
        if !model.connected {
            return;
        }
    }
    if catalog.containers.is_empty() {
        return;
    }
    let poses = shelves
        .poses
        .get_or_insert_with(|| {
            Arc::new(layout::poses(
                &std::fs::read_to_string(config.content_root.join("items/item_poses.json")).unwrap_or_default(),
            ))
        })
        .clone();
    let mut seen = HashSet::new();
    for row in snapshot.0["containers"].as_array().into_iter().flatten() {
        let Some(id) = row["netId"].as_u64().and_then(|n| u32::try_from(n).ok()) else {
            continue;
        };
        // ContainerSchema.Get: an unknown kind falls back to kind 0.
        let kind_id = row["kindId"].as_u64().unwrap_or(0) as usize;
        let Some(kind) = catalog.containers.get(kind_id).or(catalog.containers.first()) else {
            continue;
        };
        if !kind.display {
            continue; // a solid prop keeps an empty display
        }
        let (Some(position), Some(yaw)) = (
            crate::player::vector(&row["position"]).filter(|p| p.is_finite()),
            row["yaw"].as_f64().map(|y| y as f32).filter(|y| y.is_finite()),
        ) else {
            continue;
        };
        let Some(stood) = *shelves.stood.entry(kind.mesh.clone()).or_insert_with(|| {
            crate::content::ObjData::load(&config.content_root.join(format!("objects/{}.obj", kind.mesh)))
                .ok()
                .map(|d| layout::stood_box(d.min, d.max))
        }) else {
            continue; // PlaceItem: no shelf mesh, nothing placed
        };
        seen.insert(id);
        let transform = Transform::from_translation(position).with_rotation(Quat::from_rotation_y(yaw.to_radians()));
        let entry = shelves.entries.entry(id).or_insert_with(|| Entry {
            root: commands
                .spawn((
                    Name::new(format!("Store shelf display #{id}")),
                    ShelfRoot,
                    transform,
                    Visibility::default(),
                ))
                .id(),
            pose: (position, yaw),
            cells: HashMap::new(),
        });
        if entry.pose != (position, yaw) {
            entry.pose = (position, yaw);
            commands.entity(entry.root).insert(transform);
        }
        let current = cells(&row["display"], kind.width, &catalog);
        let keys: HashSet<u16> = current.iter().map(Cell::key).collect();
        entry.cells.retain(|key, model| {
            let keep = keys.contains(key);
            if !keep {
                commands.entity(*model).try_despawn();
            }
            keep
        });
        for cell in current {
            if entry.cells.contains_key(&cell.key()) {
                continue;
            }
            let visual = catalog.items.get(&cell.item_id);
            let art = models.art(
                &config.content_root,
                cell.item_id,
                0,
                visual.map_or(0, |i| i.stack_size),
                visual.map_or("", |i| i.rarity.as_str()),
                &mut cache,
                &mut meshes,
                &mut materials,
                &mut images,
            );
            let mesh = models.bounds(cell.item_id);
            let facts = Facts {
                item_type: visual.map(|i| i.item_type.as_str()),
                name: visual.map(|i| i.name.as_str()),
            };
            let oriented = layout::orient(cell.item_id, mesh.1 - mesh.0, poses.get(&cell.item_id).copied(), facts);
            let Some(placed) = layout::place(
                layout::profile(&kind.mesh, true),
                stood,
                cell.x,
                cell.y,
                cell.eff_w,
                mesh,
                oriented,
            ) else {
                continue; // a grid cell beyond the shelf's slots is not shown
            };
            let rarity = visual.map_or(Color::WHITE, |i| items::rarity_color(&i.rarity));
            let model = commands
                .spawn((
                    Name::new(visual.map_or_else(|| format!("Shelf item #{}", cell.item_id), |i| i.name.clone())),
                    ShelfItem {
                        net_id: id,
                        x: cell.x,
                        y: cell.y,
                        item_id: cell.item_id,
                        name: visual.map_or_else(|| "?".into(), |i| i.name.clone()),
                        tint: tag_tint(rarity),
                        center: (mesh.0 + mesh.1) * 0.5,
                        half: (mesh.1 - mesh.0) * 0.55,
                        tag: placed.tag,
                    },
                    Mesh3d(art.mesh),
                    MeshMaterial3d(art.material),
                    placed.transform,
                    VisibilityRange { start_margin: 0.0..0.0, end_margin: RENDER_DIST..RENDER_DIST, use_aabb: false },
                    ChildOf(entry.root),
                ))
                .id();
            if art.translucent {
                commands.entity(model).insert(items::material::AlphaDepth);
            }
            entry.cells.insert(cell.key(), model);
        }
    }
    // The server retired a fixture: its display leaves too.
    shelves.entries.retain(|id, entry| {
        let keep = seen.contains(id);
        if !keep {
            commands.entity(entry.root).try_despawn();
        }
        keep
    });
}
/// Label3D Modulate: `rar.Lerp(Colors.White, 0.35f)` on the source colour values.
pub(crate) fn tag_tint(rarity: Color) -> Color {
    let c = rarity.to_srgba();
    let l = |v: f32| v + (1. - v) * 0.35;
    Color::srgba(l(c.red), l(c.green), l(c.blue), c.alpha + (1. - c.alpha) * 0.35)
}

/// The world-space model matrix of a shelf item.
pub(crate) fn item_matrix(root: &Transform, item: &Transform) -> Mat4 {
    root.to_matrix() * item.to_matrix()
}
/// The ray parameter where `from + dir*t` enters the item's look box, within `limit`.
pub(crate) fn ray_hit(item: &ShelfItem, world: Mat4, from: Vec3, dir: Vec3, limit: f32) -> Option<f32> {
    let inverse = world.inverse();
    let (o, d) = (inverse.transform_point3(from) - item.center, inverse.transform_vector3(dir));
    let (mut near, mut far) = (0f32, limit);
    for a in 0..3 {
        if d[a].abs() < 1e-9 {
            if o[a].abs() > item.half[a] {
                return None;
            }
            continue;
        }
        let (t0, t1) = ((-item.half[a] - o[a]) / d[a], (item.half[a] - o[a]) / d[a]);
        near = near.max(t0.min(t1));
        far = far.min(t0.max(t1));
        if near > far {
            return None;
        }
    }
    Some(near)
}
/// Does the look sphere (world centre, world radius) overlap the item's box?
/// Shelf models are uniformly scaled, so the radius scales with them.
pub(crate) fn sphere_hits(item: &ShelfItem, world: Mat4, center: Vec3, radius: f32) -> bool {
    let scale = world.x_axis.truncate().length().max(1e-6);
    let local = world.inverse().transform_point3(center) - item.center;
    let nearest = local.clamp(-item.half, item.half);
    local.distance_squared(nearest) <= (radius / scale).powi(2)
}

/// The shelf item the look settled on this frame, for the tag.
#[derive(Resource, Default)]
pub(crate) struct FocusedShelfItem(pub Option<(Value, Vec3, String, Color)>);

/// UpdateLookFocus for shelf items: the eye ray (2.6 m, stopped by the
/// world) claims an item box it hits; otherwise the 0.16 m sphere at the
/// ray's end refines to the item nearest it (by its origin). Offered as an
/// ordinary candidate with no HUD prompt, so it beats the shelf behind it
/// (ResolveFocus: Shelf -> ShelfItem) and crop harvest never overrides it.
#[allow(clippy::too_many_arguments)]
pub(super) fn offer(
    ui: Res<UiState>,
    snapshot: Res<GameSnapshot>,
    occupancy: Option<Res<crate::vehicle::Occupancy>>,
    camera: Query<&Transform, With<PlayerCamera>>,
    player: Query<Entity, With<PlayerBody>>,
    context: ReadRapierContext,
    roots: Query<&Transform, With<ShelfRoot>>,
    items: Query<(&ShelfItem, &Transform, &ChildOf)>,
    mut focus: ResMut<InteractionFocus>,
    mut focused: ResMut<FocusedShelfItem>,
) {
    focused.0 = None;
    if ui.captures_input()
        || occupancy.is_some_and(|o| o.seat.is_some())
        || snapshot.0["player"]["alive"] == false
        || snapshot.0["player"]["vehicleNetId"].as_u64().unwrap_or(0) != 0
    {
        return;
    }
    let Ok(camera) = camera.single() else {
        return;
    };
    let (origin, direction) = (camera.translation, *camera.forward());
    let reach = context
        .single()
        .ok()
        .and_then(|context| {
            let mut filter = QueryFilter::default().exclude_sensors();
            if let Ok(player) = player.single() {
                filter = filter.exclude_collider(player);
            }
            context.cast_ray(origin, direction, LOOK_REACH, true, filter)
        })
        .map_or(LOOK_REACH, |hit| hit.1);
    let looked: Vec<_> = items
        .iter()
        .filter_map(|(item, transform, parent)| {
            let root = roots.get(parent.parent()).ok()?;
            Some((item, item_matrix(root, transform), root.transform_point(item.tag)))
        })
        .collect();
    let worlds: Vec<_> = looked.iter().map(|(item, world, _)| (*item, *world)).collect();
    if let Some((index, distance)) = pick(&worlds, origin, direction, reach) {
        let (item, _, tag) = looked[index];
        let args = json!({"netId":item.net_id,"x":item.x,"y":item.y});
        offer_silent(&mut focus, distance, args.clone());
        focused.0 = Some((args, tag, item.name.clone(), item.tint));
    }
}
/// The ray claim (nearest hit) or else the sphere refinement, with the
/// distance offered: the hit, or the sphere's near face so a refined item
/// beats the shelf the ray stopped on.
pub(crate) fn pick(items: &[(&ShelfItem, Mat4)], origin: Vec3, direction: Vec3, reach: f32) -> Option<(usize, f32)> {
    let ray = items
        .iter()
        .enumerate()
        .filter_map(|(i, (item, world))| ray_hit(item, *world, origin, direction, reach).map(|t| (i, t)))
        .min_by(|a, b| a.1.total_cmp(&b.1));
    if ray.is_some() {
        return ray;
    }
    let end = origin + direction * reach;
    let from_end = |world: &Mat4| world.transform_point3(Vec3::ZERO).distance_squared(end);
    items
        .iter()
        .enumerate()
        .filter(|(_, (item, world))| sphere_hits(item, *world, end, LOOK_SPHERE))
        .min_by(|a, b| from_end(&a.1.1).total_cmp(&from_end(&b.1.1)))
        .map(|(i, _)| (i, (reach - LOOK_SPHERE).max(0.)))
}
/// An ordinary (not open_storage) candidate without a HUD prompt: the source
/// shows only the floating tag for a looked-at shelf item.
fn offer_silent(focus: &mut InteractionFocus, distance: f32, args: Value) {
    focus.offer(distance, "", TAKE, args.clone(), false);
    if let Some(candidate) = &mut focus.candidate
        && candidate.action == TAKE
        && candidate.args == args
    {
        candidate.show_binding = false;
    }
}

#[derive(Default)]
struct Surface {
    entity: Option<Entity>,
    image: Option<Handle<Image>>,
    material: Option<Handle<PanelMaterial>>,
    content: Option<(String, [f32; 4])>,
}
/// ShelfItemBody.SetFocused: the focused item's name tag, a depth-ignoring
/// camera-facing Label3D. Shown only while the look winner is still that item.
#[allow(clippy::too_many_arguments)]
fn tag(
    mut commands: Commands,
    focus: Res<InteractionFocus>,
    focused: Res<FocusedShelfItem>,
    mut surface: Local<Surface>,
    mut raster: Raster,
    mut meshes: ResMut<Assets<Mesh>>,
    materials: Option<ResMut<Assets<PanelMaterial>>>,
    exists: Query<()>,
) {
    if surface.entity.is_some_and(|e| !exists.contains(e)) {
        *surface = Surface::default();
    }
    let shown = focused.0.as_ref().filter(|(args, ..)| {
        focus.candidate.as_ref().is_some_and(|c| c.action == TAKE && c.args == *args)
    });
    let (Some((_, at, name, tint)), Some(mut materials)) = (shown, materials) else {
        if let Some(entity) = surface.entity {
            commands.entity(entity).insert(Visibility::Hidden);
        }
        return;
    };
    let key = (name.clone(), tint.to_srgba().to_f32_array());
    if surface.content.as_ref() != Some(&key) {
        let Some(mut bitmap) = raster.render_fitted(name, TAG_FONT, f32::MAX, TAG_OUTLINE, 1.) else {
            return;
        };
        bitmap.image.sampler = ImageSampler::linear();
        let size = Vec2::new(bitmap.image.width() as f32, bitmap.image.height() as f32) * TAG_PIXEL;
        let image = raster.images.add(bitmap.image);
        let material = materials.add(ExtendedMaterial {
            base: StandardMaterial {
                base_color: *tint,
                base_color_texture: Some(image.clone()),
                alpha_mode: AlphaMode::Blend,
                unlit: true,
                double_sided: true,
                cull_mode: None,
                ..default()
            },
            extension: Face {},
        });
        let mesh = meshes.add(Rectangle::from_size(size));
        if let Some(entity) = surface.entity {
            commands.entity(entity).insert((Mesh3d(mesh), MeshMaterial3d(material.clone())));
        } else {
            surface.entity = Some(
                commands
                    .spawn((
                        Name::new("Original shelf item name tag"),
                        Mesh3d(mesh),
                        MeshMaterial3d(material.clone()),
                        Transform::from_translation(*at),
                        NotShadowCaster,
                        NotShadowReceiver,
                        NoFrustumCulling,
                    ))
                    .id(),
            );
        }
        surface.image = Some(image);
        surface.material = Some(material);
        surface.content = Some(key);
    }
    if let Some(entity) = surface.entity {
        commands.entity(entity).insert((Transform::from_translation(*at), Visibility::Inherited));
    }
}

#[cfg(test)]
mod tests;
