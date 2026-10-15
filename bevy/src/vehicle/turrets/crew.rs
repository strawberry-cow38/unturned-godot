//! Vehicle.EquipDoorGunners and TargetDummy's original character presentation.
//! Accepted crewAlive is the only life gate. These meshes never create a hitbox
//! or damage authority; the managed host owns the original analytic crew box.
use super::{Mounts, VehicleBody};
use crate::{
    character::{Appearance, CharacterLayers, CharacterUpdate, ClothesMaterial},
    content::ContentCache,
    entities::ItemCatalog,
    player::{GameSnapshot, WorldSessionEpoch},
    rig::{RigAnimation, RigLibrary, RigPlayback, spawn_rig},
    vehicle::{VehicleUpdate, data},
    viewmodel::{GunVisual, load_gun_visuals},
    world::WorldConfig,
};
use bevy::{
    camera::visibility::RenderLayers, mesh::skinning::SkinnedMeshInverseBindposes, prelude::*,
};
use serde::Deserialize;
use std::collections::{HashMap, HashSet};

#[derive(Clone, Debug, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub(crate) struct Definition {
    rig: String,
    face: u8,
    tint: [f32; 3],
    gun_rotation_degrees: [f32; 3],
    #[serde(default)]
    clip: Option<String>,
}
#[derive(Component)]
struct Crew {
    gun: String,
    attached: bool,
}
#[derive(Component)]
struct HeldGun;
#[derive(Resource, Default)]
struct Registry {
    epoch: u64,
    definition: Option<Definition>,
    roots: HashMap<(Entity, usize), Entity>,
}
#[derive(Resource, Default)]
struct GunLibrary(Option<HashMap<String, GunVisual>>);

pub(crate) struct CrewPlugin;
impl Plugin for CrewPlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<Registry>()
            .init_resource::<GunLibrary>()
            .init_resource::<crate::vehicle::damage::WreckMaterial>()
            .add_systems(
                Update,
                (sync, attach)
                    .chain()
                    .after(VehicleUpdate)
                    .before(RigAnimation),
            )
            .add_systems(Update, char_wreck.after(CharacterUpdate));
    }
}

fn char_wreck(
    mut commands: Commands,
    registry: Res<Registry>,
    bodies: Query<&VehicleBody>,
    hierarchy: Query<&Children>,
    parts: Query<
        (
            Option<&MeshMaterial3d<StandardMaterial>>,
            Option<&MeshMaterial3d<ClothesMaterial>>,
        ),
        With<Mesh3d>,
    >,
    mut wreck: ResMut<crate::vehicle::damage::WreckMaterial>,
    mut materials: ResMut<Assets<StandardMaterial>>,
) {
    for ((vehicle, _), crew) in &registry.roots {
        if !bodies.get(*vehicle).is_ok_and(|body| body.flags & 32 != 0) {
            continue;
        }
        // Vehicle.CharWreck walks every mesh, including still-living gunners.
        // Run after dressing so late catalog/rig materialization on an already
        // exploded replica receives that same override. No life state changes.
        let charred = wreck.handle(&mut materials);
        for part in hierarchy.iter_descendants(*crew) {
            let Ok((standard, clothing)) = parts.get(part) else {
                continue;
            };
            if clothing.is_some() || standard.is_none_or(|m| m.0 != charred) {
                commands
                    .entity(part)
                    .remove::<MeshMaterial3d<ClothesMaterial>>()
                    .insert(MeshMaterial3d(charred.clone()));
            }
        }
    }
}

#[allow(clippy::too_many_arguments)]
fn sync(
    mut commands: Commands,
    config: Res<WorldConfig>,
    catalog: Res<ItemCatalog>,
    snapshot: Res<GameSnapshot>,
    epoch: Res<WorldSessionEpoch>,
    mut registry: ResMut<Registry>,
    mut rigs: ResMut<RigLibrary>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut binds: ResMut<Assets<SkinnedMeshInverseBindposes>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
    bodies: Query<(Entity, &VehicleBody, &Mounts)>,
    existing: Query<(), With<Crew>>,
) {
    if registry.epoch != epoch.0
        || registry.definition != catalog.npc_crew
        || snapshot.0["connected"] != true
    {
        for (_, entity) in registry.roots.drain() {
            commands.entity(entity).try_despawn();
        }
        registry.epoch = epoch.0;
        registry.definition = catalog.npc_crew.clone();
    }
    if snapshot.0["connected"] != true {
        return;
    }
    let Some(definition) = &catalog.npc_crew else {
        return;
    };
    let mut wanted = HashSet::new();
    for (vehicle, body, mounts) in &bodies {
        let Some(defs) = data::definition(body.type_id).and_then(|d| d.spec.turrets.as_ref())
        else {
            continue;
        };
        for (index, mount) in defs.iter().enumerate() {
            if mount.gunner_at == [0.; 3] || mounts.crew_alive.get(index) != Some(&true) {
                continue;
            }
            let key = (vehicle, index);
            wanted.insert(key);
            if registry
                .roots
                .get(&key)
                .is_some_and(|e| existing.contains(*e))
            {
                continue;
            }
            // Source TargetDummy has no root yaw, seated clip or aiming layer.
            // RiggedCharacter.AttachGun's old comment mentions a clip, but its
            // current implementation only attaches art. Preserve the bind pose.
            let pose = Transform::from_translation(Vec3::from_array(mount.gunner_at));
            let root = match spawn_rig(
                &mut commands,
                &mut rigs,
                &mut meshes,
                &mut binds,
                &config.content_root.join(&definition.rig),
                materials.add(StandardMaterial::default()),
                pose,
                false,
                definition.clip.as_deref().unwrap_or(""),
            ) {
                Ok(root) => {
                    commands.entity(root).insert((
                        Appearance {
                            skin: Color::srgb(
                                definition.tint[0],
                                definition.tint[1],
                                definition.tint[2],
                            ),
                            face: Some(definition.face),
                            ..default()
                        },
                        CharacterLayers(RenderLayers::layer(0)),
                    ));
                    root
                }
                Err(error) => {
                    warn!(
                        "Original door crew rig unavailable; using TargetDummy fallback: {error}"
                    );
                    fallback(&mut commands, &mut meshes, &mut materials, pose)
                }
            };
            commands.entity(root).insert((
                Name::new(format!(
                    "Original vehicle {} gunner {}",
                    body.net_id, mount.seat
                )),
                Crew {
                    gun: mount.gun_id.clone(),
                    attached: false,
                },
                ChildOf(vehicle),
            ));
            registry.roots.insert(key, root);
        }
    }
    registry.roots.retain(|key, entity| {
        if wanted.contains(key) {
            true
        } else {
            commands.entity(*entity).try_despawn();
            false
        }
    });
}

#[allow(clippy::too_many_arguments)]
fn attach(
    mut commands: Commands,
    config: Res<WorldConfig>,
    catalog: Res<ItemCatalog>,
    assets: Res<AssetServer>,
    mut library: ResMut<GunLibrary>,
    mut cache: ResMut<ContentCache>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
    mut crew: Query<(&mut Crew, &mut RigPlayback)>,
) {
    let Some(definition) = &catalog.npc_crew else {
        return;
    };
    if library.0.is_none() {
        library.0 = load_gun_visuals(&config.content_root).ok();
    }
    let Some(visuals) = &library.0 else { return };
    for (mut crew, mut rig) in &mut crew {
        // No playing AnimationPlayer exists in TargetDummy. Prevent even a
        // synthetic clock from advancing while retaining the original bone rest.
        rig.paused = definition.clip.is_none();
        if crew.attached {
            continue;
        }
        let Some(hook) = rig
            .bones
            .get("Right_Hook")
            .or_else(|| rig.bones.get("Right_Hand"))
        else {
            continue;
        };
        let Some(visual) = visuals.get(&crew.gun) else {
            continue;
        };
        let Ok((mesh, _)) = cache.load_mesh(&config.content_root.join(&visual.mesh), &mut meshes)
        else {
            continue;
        };
        let texture = config
            .content_root
            .join(&visual.texture)
            .is_file()
            .then_some(visual.texture.as_str());
        let base = cache.material(texture, false, &assets, &mut materials);
        let mut material = materials.get(&base).cloned().unwrap_or_default();
        material.cull_mode = None;
        commands.spawn((
            Name::new(format!("Original crew held {}", crew.gun)),
            HeldGun,
            Mesh3d(mesh),
            MeshMaterial3d(materials.add(material)),
            Transform::from_rotation(super::rotation(definition.gun_rotation_degrees)),
            RenderLayers::layer(0),
            ChildOf(*hook),
        ));
        // The source does not install magazine/sight attachments or call the
        // character muzzle flash. Accepted mount FX stay at the actual mount.
        crew.attached = true;
    }
}

fn fallback(
    commands: &mut Commands,
    meshes: &mut Assets<Mesh>,
    materials: &mut Assets<StandardMaterial>,
    pose: Transform,
) -> Entity {
    let root = commands
        .spawn((
            Name::new("Original TargetDummy block fallback"),
            pose,
            Visibility::Inherited,
        ))
        .id();
    // Exact Humanoid.Build layout and TargetDummy fallback colours; only visuals.
    let skin = materials.add(StandardMaterial {
        base_color: Color::srgb(0.86, 0.70, 0.55),
        ..default()
    });
    let shirt = materials.add(StandardMaterial {
        base_color: Color::srgb(0.25, 0.45, 0.75),
        ..default()
    });
    let pants = materials.add(StandardMaterial {
        base_color: Color::srgb(0.20, 0.22, 0.28),
        ..default()
    });
    for (center, size, material) in [
        ([0., 1.62, 0.], [0.32, 0.34, 0.32], skin),
        ([0., 1.12, 0.], [0.50, 0.66, 0.28], shirt.clone()),
        ([-0.36, 1.12, 0.], [0.16, 0.62, 0.22], shirt.clone()),
        ([0.36, 1.12, 0.], [0.16, 0.62, 0.22], shirt),
        ([-0.13, 0.42, 0.], [0.20, 0.84, 0.24], pants.clone()),
        ([0.13, 0.42, 0.], [0.20, 0.84, 0.24], pants),
    ] {
        commands.spawn((
            Mesh3d(meshes.add(Cuboid::from_size(Vec3::from_array(size)))),
            MeshMaterial3d(material),
            Transform::from_translation(Vec3::from_array(center)),
            ChildOf(root),
        ));
    }
    root
}

#[cfg(test)]
mod tests;
