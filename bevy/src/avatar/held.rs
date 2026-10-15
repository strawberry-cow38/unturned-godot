//! The body shares already-resolved held meshes and original clips. This is a
//! second view of accepted equipment, never a second inventory or weapon clock.
use super::{AvatarMuzzle, FirstPersonArms, LocalAvatar, Perspective};
use crate::{
    entities::ItemCatalog,
    player::GameSnapshot,
    rig::{RigPlayback, apply_additive, sample_track},
    viewmodel::{HeldMesh, ViewmodelStatus},
};
use bevy::{camera::visibility::RenderLayers, prelude::*};
use std::collections::{HashMap, HashSet};
#[derive(Resource, Default)]
pub(super) struct Mirror {
    pub source: Option<Entity>,
    pub copies: HashMap<Entity, Entity>,
    visuals: Option<HashMap<String, crate::viewmodel::GunVisual>>,
}
#[derive(Component)]
pub(super) struct MuzzlePoint {
    owner: Entity,
    point: Vec3,
}
#[derive(SystemSet, Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct AvatarMuzzleUpdate;
pub(super) fn muzzle(mut commands: Commands, points: Query<(&MuzzlePoint, &GlobalTransform)>) {
    for (point, global) in &points {
        commands.entity(point.owner).insert(AvatarMuzzle(
            global.transform_point(point.point),
            global.rotation() * Vec3::Y,
        ));
    }
}
const LOWER: [&str; 7] = [
    "Skeleton",
    "Left_Hip",
    "Left_Leg",
    "Left_Foot",
    "Right_Hip",
    "Right_Leg",
    "Right_Foot",
];
pub(crate) fn clip(rig: &RigPlayback, asset: &str, suffix: &str, fallback: &str) -> String {
    crate::viewmodel::own_clip(rig, asset, suffix, fallback)
}
fn overlay(rig: &RigPlayback, asset: &str, gun: bool, held: u16) -> Option<(String, f32)> {
    let source = &rig.clip;
    if held != 0 && source.ends_with("_Inspect") {
        return Some((source.clone(), rig.time));
    }
    if held == 0 {
        return (source.starts_with("Punch_")
            && rig
                .data
                .anims
                .get(source)
                .is_some_and(|c| rig.time < c.length))
        .then(|| (source.clone(), rig.time));
    }
    let equip = clip(
        rig,
        asset,
        "_Equip",
        if gun { "Gun_Equip" } else { "Melee_Equip" },
    );
    if gun
        && (source.ends_with("_Equip")
            || source.ends_with("_Reload")
            || source.ends_with("_Hammer"))
    {
        return Some((source.clone(), rig.time));
    }
    if !gun
        && (source.ends_with("_Weak") || source.ends_with("_Strong"))
        && rig
            .data
            .anims
            .get(source)
            .is_some_and(|c| rig.time < c.length)
    {
        return Some((source.clone(), rig.time));
    }
    rig.data.anims.get(&equip).map(|c| (equip, c.length))
}
pub(super) fn animate(
    snapshot: Res<GameSnapshot>,
    catalog: Res<ItemCatalog>,
    occupancy: Res<crate::vehicle::Occupancy>,
    status: Res<ViewmodelStatus>,
    sources: Query<&RigPlayback, (With<FirstPersonArms>, Without<LocalAvatar>)>,
    bodies: Query<&RigPlayback, With<LocalAvatar>>,
    mut transforms: Query<&mut Transform>,
) {
    let Ok(source) = sources.single() else {
        return;
    };
    let Ok(body) = bodies.single() else {
        return;
    };
    if body.paused || occupancy.seat.is_some_and(|s| s.driver) {
        return;
    }
    let held = snapshot.0["player"]["heldItemId"].as_u64().unwrap_or(0) as u16;
    let item = catalog.items.get(&held);
    let gun = item.is_some_and(|i| i.item_type == "gun");
    if held != 0 && !gun && item.is_none_or(|i| i.melee.is_null()) {
        return;
    }
    let asset = item.and_then(|i| i.asset.as_deref()).unwrap_or("");
    let Some((name, time)) = overlay(source, asset, gun, held) else {
        return;
    };
    let Some(clip) = body.data.anims.get(&name) else {
        return;
    };
    let time = time.clamp(0., clip.length);
    let preserve = matches!(
        snapshot.0["player"]["stance"].as_str(),
        Some("crouch" | "prone")
    );
    apply_upper_body(body, clip, time, preserve, &mut transforms);
    let aim_name = clip_name(source, asset);
    let aim = gun.then(|| body.data.anims.get(&aim_name)).flatten();
    for bone in &body.data.bones {
        let Some(entity) = body.bones.get(&bone.name) else {
            continue;
        };
        let Ok(mut pose) = transforms.get_mut(*entity) else {
            continue;
        };
        if let Some((clip, track)) = aim.and_then(|c| c.tracks.get(&bone.name).map(|t| (c, t))) {
            *pose = apply_additive(track, clip.length, status.aim_alpha, *pose);
        }
    }
}
/// RiggedCharacter's shared gun/gesture upper-body layer. Locomotion legs are
/// never sampled from the action; crouch/prone preserve spine/skull and counter
/// the shoulders by the authored spine delta.
pub(crate) fn apply_upper_body(
    body: &RigPlayback,
    clip: &crate::rig::Clip,
    time: f32,
    preserve: bool,
    transforms: &mut Query<&mut Transform>,
) {
    let spine = body
        .bones
        .get("Spine")
        .and_then(|entity| transforms.get(*entity).ok())
        .copied();
    let counter = spine
        .map(|pose| {
            pose.rotation.inverse()
                * clip
                    .tracks
                    .get("Spine")
                    .map_or(pose, |track| sample_track(track, time, pose))
                    .rotation
        })
        .unwrap_or(Quat::IDENTITY);
    for bone in &body.data.bones {
        let Some(entity) = body.bones.get(&bone.name) else {
            continue;
        };
        let Ok(mut pose) = transforms.get_mut(*entity) else {
            continue;
        };
        if !LOWER.contains(&bone.name.as_str())
            && !(preserve && matches!(bone.name.as_str(), "Spine" | "Skull"))
        {
            if let Some(track) = clip.tracks.get(&bone.name) {
                *pose = sample_track(track, time, *pose);
            }
            if preserve && matches!(bone.name.as_str(), "Left_Shoulder" | "Right_Shoulder") {
                pose.rotation = counter * pose.rotation;
            }
        }
    }
}

fn clip_name(rig: &RigPlayback, asset: &str) -> String {
    clip(rig, asset, "_Aim", "Gun_Aim")
}
#[derive(Clone)]
struct Visual {
    source: Entity,
    parent: Entity,
    pose: Transform,
    mesh: Option<Mesh3d>,
    material: Option<MeshMaterial3d<StandardMaterial>>,
    visible: Visibility,
}
#[allow(clippy::too_many_arguments)]
pub(super) fn mirror(
    mut commands: Commands,
    mut mirror: ResMut<Mirror>,
    view: Res<Perspective>,
    snapshot: Res<GameSnapshot>,
    catalog: Res<ItemCatalog>,
    config: Res<crate::world::WorldConfig>,
    occupancy: Res<crate::vehicle::Occupancy>,
    body: Query<(Entity, &RigPlayback), With<LocalAvatar>>,
    arms: Query<&RigPlayback, With<FirstPersonArms>>,
    held: Query<(Entity, &ChildOf), With<HeldMesh>>,
    visuals: Query<(
        &Transform,
        Option<&Mesh3d>,
        Option<&MeshMaterial3d<StandardMaterial>>,
        Option<&Children>,
        Option<&Visibility>,
        Option<&Name>,
    )>,
) {
    let Ok((body_entity, body)) = body.single() else {
        return;
    };
    let alive = snapshot.0["player"]["alive"].as_bool().unwrap_or(true);
    let id = snapshot.0["player"]["heldItemId"].as_u64().unwrap_or(0) as u16;
    let item = catalog.items.get(&id);
    let allowed = alive
        && occupancy.seat.is_none_or(|s| !s.driver)
        && item.is_some_and(|i| i.item_type == "gun" || !i.melee.is_null());
    let source = allowed.then(|| held.single().ok()).flatten();
    let source_id = source.map(|v| v.0);
    if mirror.source != source_id {
        for (_, copy) in mirror.copies.drain() {
            commands.entity(copy).try_despawn();
        }
        mirror.source = source_id;
    }
    let (Some((root, hook)), Ok(arms)) = (source, arms.single()) else {
        commands.entity(body_entity).remove::<AvatarMuzzle>();
        return;
    };
    let Some(hook_name) = arms
        .bones
        .iter()
        .find_map(|(name, id)| (*id == hook.parent()).then_some(name))
    else {
        return;
    };
    let Some(body_hook) = body.bones.get(hook_name).copied() else {
        return;
    };
    let mut stack = vec![(root, body_hook)];
    let mut live = HashSet::new();
    let layer = RenderLayers::layer(if view.first_person {
        super::OWN_BODY_LAYER
    } else {
        0
    });
    while let Some((entity, parent)) = stack.pop() {
        let Ok((pose, mesh, material, children, visibility, name)) = visuals.get(entity) else {
            continue;
        };
        // Reticles billboard toward the FP eye; world scopes render their body.
        if name.is_some_and(|n| {
            matches!(
                n.as_str(),
                "Optic reticle" | "Original muzzle flash" | "Muzzle flash light"
            )
        }) {
            continue;
        }
        live.insert(entity);
        let copy = *mirror
            .copies
            .entry(entity)
            .or_insert_with(|| commands.spawn_empty().id());
        let pose = if entity == root && item.is_some_and(|i| i.item_type == "gun") {
            Transform::from_rotation(Quat::from_rotation_z(std::f32::consts::FRAC_PI_2))
        } else {
            *pose
        };
        let visual = Visual {
            source: entity,
            parent,
            pose,
            mesh: mesh.cloned(),
            material: material.cloned(),
            visible: visibility.copied().unwrap_or_default(),
        };
        let mut command = commands.entity(copy);
        command.insert((
            Name::new(format!("Body held part {:?}", visual.source)),
            visual.pose,
            visual.visible,
            layer.clone(),
            ChildOf(visual.parent),
        ));
        if entity == root {
            if mirror.visuals.is_none() {
                mirror.visuals = Some(
                    crate::viewmodel::load_gun_visuals(&config.content_root).unwrap_or_default(),
                );
            }
            if let Some(visual) = item
                .and_then(|i| i.asset.as_ref())
                .and_then(|asset| mirror.visuals.as_ref()?.get(asset))
            {
                command.insert(MuzzlePoint {
                    owner: body_entity,
                    point: visual.muzzle,
                });
            }
        }
        if let (Some(mesh), Some(material)) = (visual.mesh, visual.material) {
            command.insert((mesh, material));
        }
        if let Some(children) = children {
            for child in children.iter() {
                stack.push((child, copy));
            }
        }
    }
    mirror.copies.retain(|source, copy| {
        if live.contains(source) {
            true
        } else {
            commands.entity(*copy).try_despawn();
            false
        }
    });
}
