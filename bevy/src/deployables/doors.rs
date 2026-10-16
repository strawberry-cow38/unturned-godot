//! `DoorDeploy`: a placeable door is LocalOnly in the source. `TickDeploy`
//! spawns it on the placer's machine on every path, while the host only spends
//! the item (DeployableNetSchema keeps no entity for a DoorProp def). It is
//! never replicated, saved or seen by another player. The native client keeps
//! exactly that: this module is presentation and local collision for the
//! placer, the ObjectDoor swing (`ApplySwing`, `TrySolidify`) and its F toggle.
use super::*;
use crate::{
    entities::{ApplyInteractions, CollectInteractions, InteractionFocus},
    player::{PlayerBody, PlayerCamera},
    settings::{GameAction, Settings},
    ui::{UiState, input_order::FrameInput},
};
use bevy::{
    input::ButtonState,
    window::{PrimaryWindow, WindowEvent},
};

#[cfg(test)]
mod tests;
#[cfg(test)]
mod visual;

const ACTION: &str = "placedDoor";
/// The interactables door look ray (source `_focusObjectDoor`).
const REACH: f32 = 4.;
/// `ObjectDoor.CooldownSec`.
const COOLDOWN: f64 = 0.35;
/// `WoodenLeaves`: the container leaves' 0.4667 s, not retail's 0.6333 s.
const DURATION: f32 = 0.4667;
/// Retail door AudioSource m_Volume (linear), UnitSize 6.
const VOLUME: f32 = 0.125;

/// One `WorldBuilder.DoorCatalogEntry` from `WoodenLeaves`.
#[derive(Clone, Debug, PartialEq)]
pub(super) struct Leaf {
    pub mesh: String,
    pub pivot: Vec3,
    pub axis: Vec3,
    pub angle: f32,
    pub duration: f32,
}
/// `DoorDeploy.WoodenLeaves`: the form's hinge row from `wooden_door_anims.txt`,
/// the angle clamped to 90 degrees with its sign kept, the pivot rebased into
/// the leaf mesh's own frame by its AABB centre. Two hinges (Doubledoor) are
/// refused, as the source refuses them.
pub(super) fn wooden_leaf(root: &std::path::Path, prop: &str, center: Vec3) -> Option<Leaf> {
    let form = prop.split('_').next().unwrap_or(prop);
    let text = std::fs::read_to_string(root.join("objects/wooden_door_anims.txt")).ok()?;
    let rows: Vec<Vec<f32>> = text
        .lines()
        .filter_map(|line| {
            let f: Vec<&str> = line.split_whitespace().collect();
            (f.len() >= 10 && f[0] == form)
                .then(|| f[2..10].iter().filter_map(|v| v.parse().ok()).collect())
        })
        .filter(|v: &Vec<f32>| v.len() == 8)
        .collect();
    if rows.len() != 1 {
        if rows.len() > 1 {
            warn!("{prop}: {} hinges but one mesh -- the panel split is not implemented, skipping", rows.len());
        }
        return None;
    }
    let r = &rows[0];
    Some(Leaf {
        mesh: format!("objects/{prop}.obj"),
        pivot: Vec3::new(r[0], r[1], r[2]) + center,
        axis: Vec3::new(r[3], r[4], r[5]),
        angle: r[6].signum() * r[6].abs().min(90.),
        duration: DURATION,
    })
}
/// The `Basis(Right, 270)` stand-up these rips need (height on +Z).
pub(super) fn stand() -> Quat {
    Quat::from_rotation_x(270_f32.to_radians())
}
/// `SpawnProp`'s seat: the host at the point, yawed, raised so the closed
/// leaf's lowest AABB corner sits on the point.
pub(super) fn host(point: Vec3, yaw: f32, bounds: geometry::Bounds) -> Transform {
    let rotation = Quat::from_rotation_y(yaw.to_radians()) * stand();
    let lowest = bounds
        .corners()
        .into_iter()
        .map(|c| (rotation * c).y)
        .fold(f32::INFINITY, f32::min);
    Transform::from_translation(point - Vec3::Y * lowest)
        .with_rotation(Quat::from_rotation_y(yaw.to_radians()))
}
/// `ApplySwing`: the pivot's swing and the leaf collider tracking it.
pub(super) fn swing(leaf: &Leaf, t: f32, center: Vec3) -> (Transform, Transform) {
    let fraction = crate::world::interactables::easing(t, true, None);
    let rotation = Quat::from_axis_angle(
        leaf.axis.try_normalize().unwrap_or(Vec3::Z),
        leaf.angle.to_radians() * fraction,
    );
    (
        Transform::from_translation(leaf.pivot).with_rotation(rotation),
        Transform::from_translation(rotation * (center - leaf.pivot) + leaf.pivot).with_rotation(rotation),
    )
}

#[derive(Component)]
pub(crate) struct PlacedDoor {
    pub id: u32,
    leaf: Leaf,
    center: Vec3,
    half: Vec3,
    pivot: Entity,
    collider: Entity,
    pub swing: f32,
    pub open: bool,
    pub solid: bool,
    last_toggle: Option<f64>,
}
#[derive(Resource, Default)]
pub(crate) struct LocalDoors {
    epoch: u64,
    next: u32,
    /// (prop, frozen point, frozen yaw) from completed place gestures.
    pub queue: Vec<(String, Vec3, f32)>,
    hovered: Option<u32>,
}

pub(super) fn install(app: &mut App) {
    app.init_resource::<LocalDoors>()
        .add_systems(Update, spawn.after(super::placement::act))
        .add_systems(
            Update,
            collect.in_set(CollectInteractions).after(crate::player::PlayerCameraUpdate),
        )
        .add_systems(Update, toggle.after(CollectInteractions).before(ApplyInteractions))
        .add_systems(FixedUpdate, animate.before(PhysicsSet::SyncBackend));
}

#[allow(clippy::too_many_arguments)]
fn spawn(
    mut commands: Commands,
    epoch: Res<WorldSessionEpoch>,
    config: Res<WorldConfig>,
    mut doors: ResMut<LocalDoors>,
    placed: Query<Entity, With<PlacedDoor>>,
    parents: Query<&ChildOf>,
    mut cache: ResMut<ContentCache>,
    assets: Res<AssetServer>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
) {
    if doors.epoch != epoch.0 {
        // Spawned under the scene: a new world session leaves none behind.
        for leaf in &placed {
            if let Some(host) = parents.iter_ancestors(leaf).last() {
                commands.entity(host).despawn();
            }
        }
        doors.epoch = epoch.0;
        doors.queue.clear();
        doors.hovered = None;
    }
    for (prop, point, yaw) in std::mem::take(&mut doors.queue) {
        let path = config.content_root.join(format!("objects/{prop}.obj"));
        let Ok((mesh, data)) = cache.load_mesh(&path, &mut meshes) else {
            // SpawnProp returns null: a missing leaf is said, never invisible.
            warn!("[door] {prop}: leaf mesh failed to load");
            continue;
        };
        let center = (data.min + data.max) * 0.5;
        let Some(leaf) = wooden_leaf(&config.content_root, &prop, center) else {
            continue;
        };
        let bounds = geometry::Bounds {
            min: data.min,
            max: data.max,
        };
        // MatFor: the palette texture, roughness 1, both faces.
        let texture = format!("objects/{prop}_tex.png");
        let material = if config.content_root.join(&texture).is_file() {
            let shared = cache.material(Some(&texture), false, &assets, &mut materials);
            let mut m = materials.get(&shared).unwrap().clone();
            m.perceptual_roughness = 1.;
            m.double_sided = true;
            m.cull_mode = None;
            materials.add(m)
        } else {
            materials.add(StandardMaterial {
                base_color: Color::srgb(0.62, 0.62, 0.64),
                perceptual_roughness: 1.,
                double_sided: true,
                cull_mode: None,
                ..default()
            })
        };
        doors.next += 1;
        let id = doors.next;
        let host = commands
            .spawn((
                Name::new(format!("{prop} (local)")),
                host(point, yaw, bounds),
                Visibility::Inherited,
            ))
            .id();
        let door = commands
            .spawn((
                Transform::from_rotation(stand()),
                Visibility::Inherited,
                RigidBody::Fixed,
                ChildOf(host),
            ))
            .id();
        let (pivot_pose, collider_pose) = swing(&leaf, 0., center);
        let pivot = commands
            .spawn((pivot_pose, Visibility::Inherited, ChildOf(door)))
            .id();
        commands.spawn((
            Mesh3d(mesh),
            MeshMaterial3d(material),
            Transform::from_translation(-leaf.pivot),
            ChildOf(pivot),
        ));
        // The leaf's own box: source bit6, solid to the player and look rays.
        let size = (data.max - data.min).abs().max(Vec3::splat(0.15));
        let half = size * 0.5;
        let collider = commands
            .spawn((
                collider_pose,
                Collider::cuboid(half.x, half.y, half.z),
                CollisionGroups::new(Group::GROUP_4 | crate::world::buildings::SOURCE_PROP, Group::GROUP_2),
                ColliderDisabled,
                ChildOf(door),
            ))
            .id();
        commands.entity(collider).insert(PlacedDoor {
            id,
            leaf,
            center,
            half,
            pivot,
            collider,
            swing: 0.,
            open: false,
            solid: false,
            last_toggle: None,
        });
    }
}

fn collect(
    ui: Option<Res<UiState>>,
    snapshot: Res<GameSnapshot>,
    camera: Query<&Transform, With<PlayerCamera>>,
    player: Query<Entity, With<PlayerBody>>,
    placed: Query<&PlacedDoor>,
    context: ReadRapierContext,
    mut focus: Option<ResMut<InteractionFocus>>,
    mut doors: ResMut<LocalDoors>,
) {
    doors.hovered = None;
    let Some(focus) = focus.as_mut() else {
        return;
    };
    if ui.is_none_or(|u| u.captures_input())
        || snapshot.0["player"]["alive"] != true
        || snapshot.0["player"]["vehicleNetId"].as_u64().unwrap_or(0) != 0
    {
        return;
    }
    let (Ok(camera), Ok(context)) = (camera.single(), context.single()) else {
        return;
    };
    let own = player.single().ok();
    let mut filter = QueryFilter::default().exclude_sensors();
    if let Some(own) = own {
        filter = filter.exclude_collider(own);
    }
    let Some((hit, distance)) = context.cast_ray(camera.translation, *camera.forward(), REACH, true, filter)
    else {
        return;
    };
    let Ok(door) = placed.get(hit) else {
        return;
    };
    doors.hovered = Some(door.id);
    focus.offer_manual(
        distance,
        if door.open { "Close door" } else { "Open door" },
        ACTION,
        json!({"netId":door.id}),
    );
}

#[allow(clippy::too_many_arguments)]
fn toggle(
    mut commands: Commands,
    focus: Option<Res<InteractionFocus>>,
    settings: Option<Res<Settings>>,
    events: Option<Res<FrameInput>>,
    windows: Query<Entity, With<PrimaryWindow>>,
    doors: Res<LocalDoors>,
    time: Res<Time>,
    config: Res<WorldConfig>,
    assets: Res<AssetServer>,
    sources: Option<Res<Assets<AudioSource>>>,
    mut placed: Query<(&mut PlacedDoor, &GlobalTransform)>,
) {
    let (Some(focus), Some(settings), Some(events), Some(id)) = (focus, settings, events, doors.hovered)
    else {
        return;
    };
    if !focus.selected(ACTION, id) {
        return;
    }
    let window = windows.single().ok();
    let pressed = events.events.iter().any(|e| {
        (e.window.is_none() || e.window == window)
            && settings.bindings[&GameAction::Interact].matches_window_event(&e.event)
            && match &e.event {
                WindowEvent::KeyboardInput(k) => !k.repeat && k.state == ButtonState::Pressed,
                WindowEvent::MouseButtonInput(m) => m.state == ButtonState::Pressed,
                _ => false,
            }
    });
    if !pressed {
        return;
    }
    let now = time.elapsed_secs_f64();
    for (mut door, pose) in &mut placed {
        if door.id != id || door.last_toggle.is_some_and(|t| now - t < COOLDOWN) {
            continue;
        }
        door.last_toggle = Some(now);
        door.open = !door.open;
        // Retail plays the same clip on open and close.
        if sources.is_some() && config.content_root.join("sounds/DoorHandle.wav").is_file() {
            commands.spawn((
                Name::new("Source door handle"),
                AudioPlayer::new(assets.load("sounds/DoorHandle.wav")),
                PlaybackSettings::DESPAWN
                    .with_spatial(true)
                    .with_spatial_scale(bevy::audio::SpatialScale::new(1. / 6.))
                    .with_volume(bevy::audio::Volume::Linear(VOLUME * settings.master_volume)),
                Transform::from_translation(pose.translation()),
            ));
        }
    }
}

/// `_PhysicsProcess`/`ApplySwing`/`TrySolidify`: never solid mid-swing, and
/// solid at rest only once no player stands in the leaf's volume.
fn animate(
    mut commands: Commands,
    time: Res<Time>,
    context: ReadRapierContext,
    players: Query<Entity, With<PlayerBody>>,
    globals: Query<&GlobalTransform>,
    parents: Query<&ChildOf>,
    mut placed: Query<(Entity, &mut PlacedDoor)>,
    mut poses: Query<&mut Transform>,
) {
    let context = context.single().ok();
    for (entity, mut door) in &mut placed {
        let target = if door.open { 1. } else { 0. };
        let step = time.delta_secs() / door.leaf.duration.max(0.05);
        door.swing = if door.swing < target {
            (door.swing + step).min(target)
        } else {
            (door.swing - step).max(target)
        };
        let (pivot, collider) = swing(&door.leaf, door.swing, door.center);
        if let Ok(mut p) = poses.get_mut(door.pivot) {
            *p = pivot;
        }
        if let Ok(mut c) = poses.get_mut(door.collider) {
            *c = collider;
        }
        let at_rest = door.swing <= 0.001 || door.swing >= 0.999;
        let mut solid = at_rest;
        if at_rest
            && let (Some(context), Ok(parent)) = (&context, parents.get(entity))
            && let Ok(frame) = globals.get(parent.parent())
        {
            let world = frame.affine() * collider.compute_affine();
            let (_, rotation, translation) = world.to_scale_rotation_translation();
            let shape = Collider::cuboid(door.half.x, door.half.y, door.half.z);
            context.intersect_shape(
                translation,
                rotation,
                &shape,
                QueryFilter::default().predicate(&|hit, _| players.contains(hit)),
                |_, _| {
                    solid = false;
                    false
                },
            );
        }
        if solid != door.solid {
            if solid {
                commands.entity(entity).remove::<ColliderDisabled>();
            } else {
                commands.entity(entity).insert(ColliderDisabled);
            }
            door.solid = solid;
        }
    }
}
