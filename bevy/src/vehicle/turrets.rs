//! Original Vehicle.Build mounts and BuildTankExtras. Pose is presentation state;
//! firing, ammunition and seat ownership belong to the managed authority.
use super::{VehicleBody, data::VehicleDefinition, dynamics::approach, render::spawn_mesh};
use crate::content::{ContentCache, ObjData};
use bevy::prelude::*;
use bevy_rapier3d::prelude::*;
use serde::Deserialize;
use std::{path::Path, sync::LazyLock};

mod tank;
mod seats;
pub(crate) mod input;
pub(crate) mod fx;
pub(crate) use input::Operator;
#[cfg(test)]
mod tests;

#[derive(Component)]
pub(crate) struct Mounts {
    pub angles: Vec<Vec2>, // accepted yaw/pitch degrees, one row per original mount
    pub crew_alive: Vec<bool>,
    pub head_out: bool,
    pub gunner_rise: f32,
    /// Present on current authority replicas; absent only for source fixtures
    /// or older adapters, where the original three-per-second easing is used.
    pub accepted_gunner_rise: Option<f32>,
    frames: Vec<Frame>,
    hatches: Option<tank::Hatches>,
}
struct Frame {
    yaw: Entity,
    pitch: Entity,
    gun: Option<Entity>,
}
impl Mounts {
    pub(super) fn signature(&self) -> Vec<f32> {
        let mut values: Vec<_> = self.angles.iter().flat_map(|v| [v.x, v.y]).collect();
        values.extend(self.crew_alive.iter().map(|v| if *v { 1. } else { 0. }));
        if let Some(h) = &self.hatches {
            values.extend([h.top_phase, h.driver_phase]);
        }
        values
    }
}
fn rotation(degrees: [f32; 3]) -> Quat {
    Quat::from_euler(
        EulerRot::YXZ,
        degrees[1].to_radians(),
        degrees[0].to_radians(),
        degrees[2].to_radians(),
    )
}
#[allow(clippy::too_many_arguments)]
pub(super) fn build(
    commands: &mut Commands,
    entity: Entity,
    d: &VehicleDefinition,
    root: &Path,
    cache: &mut ContentCache,
    meshes: &mut Assets<Mesh>,
    materials: &mut Assets<StandardMaterial>,
    body_material: Handle<StandardMaterial>,
) {
    let Some(defs) = &d.spec.turrets else { return };
    let mut mounts = Mounts {
        angles: vec![Vec2::ZERO; defs.len()],
        crew_alive: defs.iter().map(|t| t.gunner_at == [0.; 3]).collect(),
        head_out: false,
        gunner_rise: 0.,
        accepted_gunner_rise: None,
        frames: Vec::new(),
        hatches: None,
    };
    for (i, def) in defs.iter().enumerate() {
        let parent = usize::try_from(def.parent_mount)
            .ok()
            .filter(|p| *p < i)
            .map_or(entity, |p| mounts.frames[p].yaw);
        let yaw = commands
            .spawn((
                Name::new(format!("TurretYaw{}:{i}", def.seat)),
                Transform::from_translation(Vec3::from_array(def.pivot)),
                Visibility::Inherited,
                ChildOf(parent),
            ))
            .id();
        let pitch = commands
            .spawn((
                Name::new(format!("TurretPitch{}:{i}", def.seat)),
                Transform::from_translation(Vec3::from_array(def.pitch_offset)),
                Visibility::Inherited,
                ChildOf(yaw),
            ))
            .id();
        let material = materials.add(StandardMaterial {
            base_color: Color::srgba(def.colour[0], def.colour[1], def.colour[2], def.colour[3]),
            perceptual_roughness: 1.,
            ..default()
        });
        if let Some(mesh) = &def.yaw_mesh {
            spawn_mesh(
                commands,
                yaw,
                mesh,
                root,
                cache,
                meshes,
                material.clone(),
                Transform::from_rotation(rotation(def.yaw_mesh_rotation_deg)),
            );
        }
        let gun = def.pitch_mesh.as_ref().and_then(|mesh| {
            spawn_mesh(
                commands,
                pitch,
                mesh,
                root,
                cache,
                meshes,
                material,
                Transform::from_rotation(rotation(def.mesh_rotation_deg)),
            )
        });
        if let Some(gun) = gun {
            commands.entity(gun).insert(if def.gunner_at == [0.; 3] {
                Visibility::Inherited
            } else {
                Visibility::Hidden
            });
        }
        mounts.frames.push(Frame { yaw, pitch, gun });
    }
    if d.spec.turret_meshes.is_some() {
        mounts.hatches = tank::build(
            commands,
            entity,
            d,
            root,
            cache,
            meshes,
            materials,
            body_material,
            &mounts.frames,
        );
    }
    commands.entity(entity).insert(mounts);
}
pub(super) fn animate(
    time: Res<Time>,
    ui: Option<Res<crate::ui::UiState>>,
    mut bodies: Query<(&VehicleBody, &mut Mounts)>,
    mut transforms: Query<&mut Transform>,
    mut visibility: Query<&mut Visibility>,
) {
    let dt = if ui
        .as_ref()
        .is_some_and(|ui| crate::player::simulation_paused(ui))
    {
        0.
    } else {
        time.delta_secs()
    };
    for (body, mut mounts) in &mut bodies {
        for (i, frame) in mounts.frames.iter().enumerate() {
            if let Some(angles) = mounts.angles.get(i).filter(|v| v.is_finite()) {
                if let Ok(mut pose) = transforms.get_mut(frame.yaw) {
                    pose.rotation = Quat::from_rotation_y(angles.x.to_radians());
                }
                if let Ok(mut pose) = transforms.get_mut(frame.pitch) {
                    pose.rotation = Quat::from_rotation_x(angles.y.to_radians());
                }
            }
            if let Some(gun) = frame.gun {
                if let Ok(mut visible) = visibility.get_mut(gun) {
                    *visible = if mounts.crew_alive.get(i) == Some(&true) {
                        Visibility::Inherited
                    } else {
                        Visibility::Hidden
                    };
                }
            }
        }
        let occupied = body.passengers.first().is_some_and(|id| *id != 0);
        if !occupied {
            mounts.head_out = false;
        }
        let head_out = mounts.head_out;
        mounts.gunner_rise = mounts.accepted_gunner_rise.unwrap_or_else(|| {
            approach(mounts.gunner_rise, if occupied && head_out { 1. } else { 0. }, 3. * dt)
        });
        if let Some(h) = &mut mounts.hatches {
            h.animate(
                body.driver_player_id != 0,
                occupied && !head_out,
                dt,
                &mut transforms,
            );
        }
    }
}

/// Source mesh partition kept in the shared immutable content cache.
fn split(
    cache: &mut ContentCache,
    meshes: &mut Assets<Mesh>,
    root: &Path,
    file: &str,
    tag: &str,
    zones: &[(Vec3, Vec3)],
    inside: bool,
) -> Option<(Handle<Mesh>, std::sync::Arc<ObjData>)> {
    let key = root.join(format!("{file}#{tag}"));
    if let (Some(mesh), Some(data)) = (cache.meshes.get(&key), cache.geometry.get(&key)) {
        return Some((mesh.clone(), data.clone()));
    }
    let (_, data) = cache.load_mesh(&root.join(file), meshes).ok()?;
    let (a, b) = data.split_zones(zones);
    let data = std::sync::Arc::new(if inside { b } else { a });
    if data.positions.is_empty() {
        warn!("Source vehicle mesh partition is empty: {file}#{tag}");
        return None;
    }
    let mesh = meshes.add(data.mesh());
    cache.meshes.insert(key.clone(), mesh.clone());
    cache.geometry.insert(key, data.clone());
    Some((mesh, data))
}
fn piece(
    commands: &mut Commands,
    parent: Entity,
    name: &str,
    mesh: Handle<Mesh>,
    material: Handle<StandardMaterial>,
    pose: Transform,
) -> Entity {
    commands
        .spawn((
            Name::new(name.to_owned()),
            Mesh3d(mesh),
            MeshMaterial3d(material),
            pose,
            Visibility::Inherited,
            ChildOf(parent),
        ))
        .id()
}
