//! Authored ObjectDoor and PropSeat presentation. Commands and occupancy remain
//! in the original server; native leaves adopt its replicated targets.
use super::{WorldConfig, WorldGeometry, WorldObject};
use crate::{
    bridge::CoreBridge,
    content::{ContentCache, ObjData},
    entities::{CollectInteractions, InteractionFocus, ServiceManifest, WorldServicesReady},
    player::{GameSnapshot, PlayerBody, PlayerCamera, PlayerCameraUpdate},
    ui::UiState,
};
use bevy::prelude::*;
use bevy_rapier3d::prelude::*;
use serde::Deserialize;
use serde_json::{Value, json};
use std::{
    collections::{HashMap, HashSet},
    sync::Arc,
};

#[derive(Resource, Default)]
pub struct PropOccupancy {
    pub seat: Option<OccupiedPropSeat>,
}
#[derive(Clone, Copy, Debug)]
pub struct OccupiedPropSeat {
    pub net_id: u32,
    pub body_pose: Transform,
    pub eye_pose: Transform,
    pub anchor: Vec3,
    pub exit_position: Vec3,
}
#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct DoorDefinition {
    net_id: u32,
    source_index: usize,
    name: String,
    open: bool,
    leaves: Vec<LeafDefinition>,
    #[serde(default)]
    container: bool,
    #[serde(default)]
    lid_rises_when_lit: bool,
}
#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct LeafDefinition {
    mesh: String,
    pivot: [f32; 3],
    axis: [f32; 3],
    angle_degrees: f32,
    duration_seconds: f32,
    sound: Option<String>,
    open_curve: Option<Vec<[f32; 2]>>,
    close_curve: Option<Vec<[f32; 2]>>,
}
#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct SeatDefinition {
    net_id: u32,
    source_index: usize,
    name: String,
    position: [f32; 3],
    face: [f32; 3],
    ground_y: f32,
}
#[derive(Component)]
struct DoorLeaf {
    parent: Entity,
    net_id: u32,
    source_index: usize,
    definition: LeafDefinition,
    parent_pose: Transform,
    swing: f32,
    target: bool,
    reverse: bool,
    solid: bool,
    dirty: bool,
    center: Vec3,
    half: Vec3,
    collision: Arc<ObjData>,
    container: bool,
    lid_rises_when_lit: bool,
}
#[derive(Resource, Default)]
struct Registry {
    map: String,
    parents: HashMap<usize, Entity>,
    doors: HashMap<usize, DoorDefinition>,
    seats: HashMap<u32, SeatDefinition>,
    exported: HashSet<(bool, u32)>,
    geometry_revision: u64,
    session_epoch: u64,
    spawn_epoch: u64,
}
pub struct InteractablePlugin;
impl Plugin for InteractablePlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<PropOccupancy>()
            .init_resource::<Registry>()
            .add_systems(
                Update,
                (spawn, adopt, export_geometry, adopt_occupancy)
                    .chain()
                    .after(WorldServicesReady)
                    .before(PlayerCameraUpdate),
            )
            .add_systems(FixedUpdate, animate.before(PhysicsSet::SyncBackend))
            .add_systems(
                Update,
                collect_focus
                    .in_set(CollectInteractions)
                    .after(PlayerCameraUpdate),
            );
    }
}
fn easing(swing: f32, opening: bool, curve: Option<&[[f32; 2]]>) -> f32 {
    let Some(curve) = curve.filter(|c| !c.is_empty()) else {
        return swing * swing * (3. - 2. * swing);
    };
    let at = if opening { swing } else { 1. - swing };
    if at <= curve[0][0] {
        return curve[0][1];
    }
    for pair in curve.windows(2) {
        if at <= pair[1][0] {
            let span = pair[1][0] - pair[0][0];
            let t = if span > 1e-6 {
                (at - pair[0][0]) / span
            } else {
                0.
            };
            return pair[0][1] + (pair[1][1] - pair[0][1]) * t;
        }
    }
    curve.last().unwrap()[1]
}
fn leaf_pose(def: &LeafDefinition, swing: f32, opening: bool, reverse: bool) -> Transform {
    let curve = if opening {
        def.open_curve.as_deref()
    } else {
        def.close_curve.as_deref()
    };
    let fraction = easing(swing, opening, curve);
    let axis = Vec3::from_array(def.axis)
        .try_normalize()
        .unwrap_or(Vec3::Z);
    let rotation = Quat::from_axis_angle(
        axis,
        def.angle_degrees.to_radians() * fraction * if reverse { -1. } else { 1. },
    );
    let pivot = Vec3::from_array(def.pivot);
    Transform {
        translation: pivot - rotation * pivot,
        rotation,
        ..default()
    }
}
fn seat_pose(seat: &SeatDefinition) -> OccupiedPropSeat {
    let anchor = Vec3::from_array(seat.position);
    let forward = Vec3::new(seat.face[0], 0., seat.face[2])
        .try_normalize()
        .unwrap_or(Vec3::NEG_Z);
    let body_pose =
        Transform::from_translation(anchor - Vec3::Y * 0.422).looking_to(forward, Vec3::Y);
    OccupiedPropSeat {
        net_id: seat.net_id,
        body_pose,
        eye_pose: Transform {
            translation: body_pose.translation + Vec3::Y * 1.6,
            ..body_pose
        },
        anchor,
        exit_position: Vec3::new(
            anchor.x + forward.x * 1.1,
            seat.ground_y + 0.1,
            anchor.z + forward.z * 1.1,
        ),
    }
}
#[allow(clippy::too_many_arguments)]
fn spawn(
    mut commands: Commands,
    services: Res<ServiceManifest>,
    config: Res<WorldConfig>,
    mut registry: ResMut<Registry>,
    parents: Query<(
        Entity,
        &WorldObject,
        &Transform,
        &MeshMaterial3d<StandardMaterial>,
    )>,
    mut cache: ResMut<ContentCache>,
    mut meshes: ResMut<Assets<Mesh>>,
    snapshot: Res<GameSnapshot>,
    mut materials: ResMut<Assets<StandardMaterial>>,
    session: Res<crate::player::WorldSessionEpoch>,
    old_leaves: Query<Entity, With<DoorLeaf>>,
) {
    if registry.map != services.map || registry.spawn_epoch != session.0 {
        for entity in &old_leaves {
            commands.entity(entity).despawn();
        }
        registry.spawn_epoch = session.0;
        registry.map = services.map.clone();
        registry.parents.clear();
        registry.doors.clear();
        registry.seats.clear();
        registry.exported.clear();
        for (container, row) in services
            .object_doors
            .iter()
            .map(|r| (false, r))
            .chain(services.container_doors.iter().map(|r| (true, r)))
        {
            match serde_json::from_value::<DoorDefinition>(row.clone()) {
                Ok(mut d) => {
                    d.container = container;
                    registry.doors.insert(d.source_index, d);
                }
                Err(e) => warn!("Door manifest: {e}"),
            }
        }
        for row in &services.seats {
            match serde_json::from_value::<SeatDefinition>(row.clone()) {
                Ok(s) => {
                    registry.seats.insert(s.net_id, s);
                }
                Err(e) => warn!("Seat manifest: {e}"),
            }
        }
    }
    for (entity, object, parent_pose, material) in &parents {
        if registry.parents.get(&object.source_index) == Some(&entity) {
            continue;
        }
        let Some(definition) = registry
            .doors
            .values()
            .find(|d| d.source_index == object.source_index)
            .cloned()
        else {
            continue;
        };
        // The prop owns the fixed body; each animated leaf is a child collider.
        // Rapier 0.37 child-rigid-body writeback corrupts translations beneath a
        // rotated parent. Child colliders avoid that path and keep render and
        // collision transforms identical (covered by the real Rapier regression).
        commands.entity(entity).insert(RigidBody::Fixed);
        for leaf in &definition.leaves {
            let path = config.content_root.join(&leaf.mesh);
            let Ok((mesh, data)) = cache.load_mesh(&path, &mut meshes) else {
                warn!("Door leaf missing: {}", path.display());
                continue;
            };
            let center = (data.min + data.max) * 0.5;
            let half = ((data.max - data.min).abs() * 0.5).max(Vec3::splat(0.075));
            let collision = Arc::new(super::ladder_box_geometry(&ObjData {
                min: center - half,
                max: center + half,
                ..default()
            }));
            let (open, reverse) = door_state(
                &snapshot.0,
                definition.net_id,
                definition.container,
                definition.lid_rises_when_lit,
            )
            .unwrap_or((definition.open, false));
            let swing = if open { 1. } else { 0. };
            let leaf_entity = commands
                .spawn((
                    Name::new(format!("{} leaf {}", definition.name, leaf.mesh)),
                    ChildOf(entity),
                    Mesh3d(mesh),
                    material.clone(),
                    leaf_pose(leaf, swing, open, reverse),
                    Collider::compound(vec![(
                        center,
                        Quat::IDENTITY,
                        Collider::cuboid(half.x, half.y, half.z),
                    )]),
                    ColliderDisabled,
                    DoorLeaf {
                        parent: entity,
                        net_id: definition.net_id,
                        source_index: object.source_index,
                        definition: leaf.clone(),
                        parent_pose: *parent_pose,
                        swing,
                        target: open,
                        reverse,
                        solid: false,
                        dirty: true,
                        center,
                        half,
                        collision,
                        container: definition.container,
                        lid_rises_when_lit: definition.lid_rises_when_lit,
                    },
                ))
                .id();
            // StoreShelf.TrySpawnDoors restores the measured pane omitted by the
            // retail glass material extractor. Picnic cool boxes are not cabinets.
            if definition.name.starts_with("Cooler") && !definition.name.starts_with("Cooler_Beach")
            {
                commands.spawn((
                    Name::new("Display cooler glass"),
                    ChildOf(leaf_entity),
                    Mesh3d(meshes.add(Cuboid::new(1.25, 2.25, 0.03))),
                    MeshMaterial3d(materials.add(StandardMaterial {
                        base_color: Color::srgba(0.55, 0.70, 0.78, 0.26),
                        alpha_mode: AlphaMode::Blend,
                        perceptual_roughness: 0.06,
                        metallic: 0.,
                        cull_mode: None,
                        ..default()
                    })),
                    Transform::from_xyz(0., 0.6, 1.25)
                        .with_rotation(Quat::from_rotation_arc(Vec3::Z, Vec3::Y)),
                ));
            }
        }
        registry.parents.insert(object.source_index, entity);
    }
}
fn door_state(
    snapshot: &Value,
    id: u32,
    container: bool,
    lid_rises_when_lit: bool,
) -> Option<(bool, bool)> {
    let states = if container {
        &snapshot["containers"]
    } else {
        &snapshot["interactables"]["objectDoors"]
    };
    let state = states
        .as_array()?
        .iter()
        .find(|s| s["netId"].as_u64() == Some(id as u64))?;
    Some((
        state[if container { "doorsOpen" } else { "open" }].as_bool()?
            || (container && lid_rises_when_lit && state["cookerOn"].as_bool().unwrap_or(false)),
        state["reverse"].as_bool().unwrap_or(false),
    ))
}
fn dynamic_key(id: (bool, u32)) -> String {
    format!(
        "{}:{}",
        if id.0 { "containerDoor" } else { "objectDoor" },
        id.1
    )
}
fn adopt(
    snapshot: Res<GameSnapshot>,
    mut leaves: Query<&mut DoorLeaf>,
    mut commands: Commands,
    assets: Res<AssetServer>,
    config: Res<WorldConfig>,
    settings: Res<crate::settings::Settings>,
) {
    let mut sounds = HashSet::new();
    for mut leaf in &mut leaves {
        if let Some((open, reverse)) = door_state(
            &snapshot.0,
            leaf.net_id,
            leaf.container,
            leaf.lid_rises_when_lit,
        ) {
            if open != leaf.target {
                info!(
                    "Authored leaf {}:{} target={open}",
                    if leaf.container {
                        "container"
                    } else {
                        "object"
                    },
                    leaf.net_id
                );
                if sounds.insert((leaf.container, leaf.net_id)) {
                    if let Some(sound) = &leaf.definition.sound {
                        let path = format!("sounds/{sound}.wav");
                        if config.content_root.join(&path).is_file() {
                            commands.spawn((
                                AudioPlayer::new(assets.load(path)),
                                PlaybackSettings::DESPAWN
                                    .with_spatial(true)
                                    .with_spatial_scale(bevy::audio::SpatialScale::new(1. / 6.))
                                    .with_volume(bevy::audio::Volume::Linear(
                                        0.125 * settings.master_volume,
                                    )),
                                Transform::from_translation(leaf.parent_pose.translation),
                            ));
                        }
                    }
                }
            }
            leaf.dirty |= leaf.target != open || leaf.reverse != reverse;
            leaf.target = open;
            leaf.reverse = reverse;
        }
    }
}
fn animate(
    mut commands: Commands,
    time: Res<Time>,
    context: ReadRapierContext,
    players: Query<Entity, With<PlayerBody>>,
    parents: Query<(&Visibility, Option<&ColliderDisabled>)>,
    mut leaves: Query<(Entity, &mut DoorLeaf, &mut Transform)>,
) {
    let context = context.single().ok();
    for (entity, mut leaf, mut pose) in &mut leaves {
        let target = if leaf.target { 1. } else { 0. };
        let step = time.delta_secs() / leaf.definition.duration_seconds.max(0.001);
        let old = leaf.swing;
        leaf.swing = if leaf.swing < target {
            (leaf.swing + step).min(target)
        } else {
            (leaf.swing - step).max(target)
        };
        *pose = leaf_pose(&leaf.definition, leaf.swing, leaf.target, leaf.reverse);
        let at_rest = leaf.swing <= 0.001 || leaf.swing >= 0.999;
        let mut clear = true;
        if at_rest {
            if let Some(context) = &context {
                let world = leaf.parent_pose.to_matrix() * pose.to_matrix();
                clear = leaf_clear(context, world, leaf.center, leaf.half, |hit| {
                    players.contains(hit)
                });
            }
        }
        let parent_alive = parents
            .get(leaf.parent)
            .is_ok_and(|(visibility, disabled)| {
                *visibility != Visibility::Hidden && disabled.is_none()
            });
        let solid = at_rest && clear && parent_alive;
        if solid != leaf.solid {
            if solid {
                commands.entity(entity).remove::<ColliderDisabled>();
            } else {
                commands.entity(entity).insert(ColliderDisabled);
            }
            leaf.solid = solid;
            leaf.dirty = true;
        }
        leaf.dirty |= old != leaf.swing;
    }
}
fn leaf_clear(
    context: &RapierContext<'_>,
    world: Mat4,
    center: Vec3,
    half: Vec3,
    blocks: impl Fn(Entity) -> bool,
) -> bool {
    let (scale, rotation, _) = world.to_scale_rotation_translation();
    let size = half * scale.abs();
    let shape = Collider::cuboid(size.x, size.y, size.z);
    let mut clear = true;
    context.intersect_shape(
        world.transform_point3(center),
        rotation,
        &shape,
        QueryFilter::default().predicate(&|hit, _| blocks(hit)),
        |_, _| {
            clear = false;
            false
        },
    );
    clear
}
fn export_geometry(
    bridge: Res<CoreBridge>,
    geometry: Res<WorldGeometry>,
    services: Res<ServiceManifest>,
    mut registry: ResMut<Registry>,
    mut leaves: Query<(&mut DoorLeaf, &Transform)>,
    session: Res<crate::player::WorldSessionEpoch>,
) {
    let force =
        registry.geometry_revision != geometry.revision || registry.session_epoch != session.0;
    let mut groups: HashMap<(bool, u32), (bool, Vec<Value>)> = HashMap::new();
    for (leaf, pose) in &leaves {
        let group = groups.entry((leaf.container, leaf.net_id)).or_default();
        group.0 |= leaf.dirty || force;
        if !leaf.solid {
            continue;
        }
        let world = leaf.parent_pose.to_matrix() * pose.to_matrix();
        let vertices: Vec<_> = leaf
            .collision
            .positions
            .iter()
            .map(|p| world.transform_point3(Vec3::from_array(*p)).to_array())
            .collect();
        let destructible = services
            .objects
            .get(&leaf.source_index)
            .and_then(|v| v["destructibleIndex"].as_i64())
            .unwrap_or(-1);
        group
            .1
            .push(json!({"vertices":vertices,"destructibleIndex":destructible}));
    }
    let retired: Vec<_> = registry
        .exported
        .iter()
        .filter(|id| !groups.contains_key(id))
        .copied()
        .collect();
    for id in retired {
        if bridge
            .request(
                "worldDynamicGeometry",
                json!({"key":dynamic_key(id),"meshes":[]}),
            )
            .is_ok()
        {
            registry.exported.remove(&id);
        }
    }
    for (id, (dirty, meshes)) in groups {
        if dirty {
            match bridge.request(
                "worldDynamicGeometry",
                json!({"key":dynamic_key(id),"meshes":meshes}),
            ) {
                Ok(_) => {
                    registry.exported.insert(id);
                    for (mut leaf, _) in &mut leaves {
                        if (leaf.container, leaf.net_id) == id {
                            leaf.dirty = false;
                        }
                    }
                }
                Err(e) => warn!("Door authority geometry: {e}"),
            }
        }
    }
    registry.geometry_revision = geometry.revision;
    registry.session_epoch = session.0;
}

#[cfg(test)]
mod tests;
fn adopt_occupancy(
    snapshot: Res<GameSnapshot>,
    registry: Res<Registry>,
    mut occupancy: ResMut<PropOccupancy>,
) {
    occupancy.seat = None;
    let player = snapshot.0["playerId"].as_u64().unwrap_or(0);
    if player == 0 {
        return;
    }
    for state in snapshot.0["interactables"]["seats"]
        .as_array()
        .into_iter()
        .flatten()
    {
        if state["occupant"].as_u64() != Some(player) {
            continue;
        }
        if let Some(seat) = state["netId"]
            .as_u64()
            .and_then(|id| registry.seats.get(&(id as u32)))
        {
            occupancy.seat = Some(seat_pose(seat));
            break;
        }
    }
}
#[allow(clippy::too_many_arguments)]
fn collect_focus(
    ui: Res<UiState>,
    snapshot: Res<GameSnapshot>,
    occupancy: Res<PropOccupancy>,
    vehicle: Res<crate::vehicle::Occupancy>,
    registry: Res<Registry>,
    mut focus: ResMut<InteractionFocus>,
    camera: Query<&Transform, With<PlayerCamera>>,
    player: Query<Entity, With<PlayerBody>>,
    leaves: Query<&DoorLeaf>,
    objects: Query<&WorldObject>,
    context: ReadRapierContext,
) {
    if ui.captures_input() || vehicle.seat.is_some() {
        return;
    }
    if occupancy.seat.is_some() {
        focus.offer(0., "Stand up", "sitSeat", json!({"netId":0}), false);
        return;
    }
    let (Ok(camera), Ok(player), Ok(context)) =
        (camera.single(), player.single(), context.single())
    else {
        return;
    };
    let Some((hit, distance)) = context.cast_ray(
        camera.translation,
        *camera.forward(),
        4.,
        true,
        QueryFilter::default()
            .exclude_collider(player)
            .exclude_sensors(),
    ) else {
        return;
    };
    let source = leaves
        .get(hit)
        .map(|l| l.source_index)
        .ok()
        .or_else(|| objects.get(hit).ok().map(|o| o.source_index));
    let Some(source) = source else {
        return;
    };
    let door = leaves
        .get(hit)
        .ok()
        .map(|l| (l.net_id, l.target, l.container))
        .or_else(|| {
            registry
                .doors
                .values()
                .find(|d| d.source_index == source && !d.name.starts_with("Container_"))
                .map(|d| {
                    let open = door_state(&snapshot.0, d.net_id, d.container, d.lid_rises_when_lit)
                        .map_or(d.open, |v| v.0);
                    (d.net_id, open, d.container)
                })
        });
    if let Some((id, open, container)) = door {
        focus.offer(
            distance,
            if container {
                "Open container"
            } else if open {
                "Close door"
            } else {
                "Open door"
            },
            if container {
                "openStorage"
            } else {
                "toggleObjectDoor"
            },
            json!({"netId":id}),
            container,
        );
        return;
    }
    let point = camera.translation + *camera.forward() * distance;
    let occupied: HashSet<_> = snapshot.0["interactables"]["seats"]
        .as_array()
        .into_iter()
        .flatten()
        .filter(|s| s["occupant"].as_u64().unwrap_or(0) > 0)
        .filter_map(|s| s["netId"].as_u64())
        .collect();
    if let Some(seat) = registry
        .seats
        .values()
        .filter(|s| s.source_index == source && !occupied.contains(&(s.net_id as u64)))
        .min_by(|a, b| {
            Vec3::from_array(a.position)
                .distance_squared(point)
                .total_cmp(&Vec3::from_array(b.position).distance_squared(point))
        })
    {
        focus.offer(
            distance,
            format!("Sit on {}", seat.name.replace('_', " ")),
            "sitSeat",
            json!({"netId":seat.net_id}),
            false,
        );
    }
}
