//! GlassPane.RainGlassMat + Vehicle.RainCoverTick. One source pass, two frames:
//! mesh-local beads/released trails and world-height rivulets.
use super::{Pane, VehicleBody};
use crate::{
    sound::shelter::Shelter,
    world::rain::{RainState, RainUpdate},
};
use bevy::{asset::embedded_asset, pbr::ScreenSpaceTransmission, prelude::*};

mod material;
use material::{GlassMaterial, GlassUniform};

#[derive(Component)]
pub(super) struct RainPane {
    pub body: Entity,
    pub axis: Vec3,
}
#[derive(Component, Default)]
pub(super) struct Cover {
    remaining: f32,
    covered: bool,
}

pub(super) struct RainGlassPlugin;
impl Plugin for RainGlassPlugin {
    fn build(&self, app: &mut App) {
        embedded_asset!(app, "rain/glass.wesl");
        app.add_plugins(MaterialPlugin::<GlassMaterial>::default())
            .add_systems(
                Update,
                (
                    cameras,
                    poll.after(RainUpdate).after(crate::vehicle::VehicleUpdate),
                ),
            )
            .add_systems(PostUpdate, materials.after(TransformSystems::Propagate));
    }
}
fn cameras(
    mut commands: Commands,
    cameras: Query<Entity, (With<Camera3d>, Without<ScreenSpaceTransmission>)>,
) {
    for entity in &cameras {
        // A single pre-transparent scene copy, as in Godot hint_screen_texture.
        // More steps would feed earlier glass into later glass and change the source.
        commands
            .entity(entity)
            .insert(ScreenSpaceTransmission::default());
    }
}
fn top_local_y(definition: &crate::vehicle::data::VehicleDefinition) -> f32 {
    let s = &definition.spec;
    (s.box_center[1] + s.box_size[1] * 0.5).max(
        definition
            .roof
            .map_or(0., |(center, size)| center[1] + size[1] * 0.5),
    )
}
fn poll(
    time: Res<Time>,
    rain: Res<RainState>,
    ui: Option<Res<crate::ui::UiState>>,
    shelter: Shelter,
    mut bodies: Query<(&VehicleBody, &GlobalTransform, &mut Cover)>,
) {
    if ui.is_some_and(|ui| crate::player::simulation_paused(&ui)) {
        return;
    }
    for (body, pose, mut cover) in &mut bodies {
        cover.remaining -= time.delta_secs();
        if cover.remaining > 0. {
            continue;
        }
        cover.remaining = 0.5;
        let Some(definition) = crate::vehicle::data::definition(body.type_id) else {
            continue;
        };
        cover.covered = rain.intensity > 0.01
            && shelter.covered(pose.transform_point(Vec3::Y * (top_local_y(definition) + 0.25)));
    }
}
#[allow(clippy::type_complexity)]
fn materials(
    mut commands: Commands,
    time: Res<Time>,
    rain: Res<RainState>,
    bodies: Query<(&VehicleBody, &Cover)>,
    panes: Query<(
        Entity,
        &RainPane,
        &Pane,
        &GlobalTransform,
        Option<&MeshMaterial3d<StandardMaterial>>,
        Option<&MeshMaterial3d<GlassMaterial>>,
    )>,
    mut source: ResMut<Assets<StandardMaterial>>,
    mut materials: ResMut<Assets<GlassMaterial>>,
    mut wreck: ResMut<crate::vehicle::damage::WreckMaterial>,
) {
    for (entity, pane, _, pose, base, current) in &panes {
        let Ok((body, cover)) = bodies.get(pane.body) else {
            continue;
        };
        if body.flags & 32 != 0 {
            // Original CharWreck walks ALL meshes, replacing ShaderMaterial too.
            let black = wreck.handle(&mut source);
            if current.is_some() || base.is_none_or(|base| base.0 != black) {
                commands
                    .entity(entity)
                    .remove::<MeshMaterial3d<GlassMaterial>>()
                    .insert(MeshMaterial3d(black));
            }
            continue;
        }
        let world = pose.to_matrix();
        // Godot4.6 shader TIME is scaled engine time, unaffected by tree pause,
        // and rolls over at3600s. Shelter's CPU poll above still pauses.
        let weather = Vec4::new(
            rain.wetness,
            rain.intensity,
            if cover.covered { 1. } else { 0. },
            time.elapsed_secs() % 3600.,
        );
        if let Some(current) = current {
            if let Some(mut material) = materials.get_mut(&current.0) {
                material.uniform.world_from_local = world;
                material.uniform.local_from_world = world.inverse();
                material.uniform.weather = weather;
            }
        } else if let Some(base) = base.and_then(|base| source.get(&base.0)) {
            let material = materials.add(GlassMaterial {
                uniform: GlassUniform::new(base, pane.axis, world, weather),
            });
            commands
                .entity(entity)
                .remove::<MeshMaterial3d<StandardMaterial>>()
                .insert(MeshMaterial3d(material));
        }
    }
}

/// Exact GlassPane.DominantFaceAxis: fold opposite normals, first matching
/// 0.985 bucket, triangle-area vote, first maximum. The AABB's thin axis is not
/// equivalent for the raked/curved original windscreens.
pub(super) fn dominant_axis(positions: &[[f32; 3]]) -> Vec3 {
    let mut buckets: Vec<(Vec3, f32)> = Vec::new();
    for triangle in positions.chunks_exact(3) {
        let a = Vec3::from(triangle[0]);
        let cross = (Vec3::from(triangle[1]) - a).cross(Vec3::from(triangle[2]) - a);
        let length = cross.length();
        if length < 1e-7 {
            continue;
        }
        let mut axis = cross / length;
        let absolute = axis.abs();
        let major = if absolute.x >= absolute.y && absolute.x >= absolute.z {
            0
        } else if absolute.y >= absolute.z {
            1
        } else {
            2
        };
        if axis[major] < 0. {
            axis = -axis;
        }
        if let Some((_, area)) = buckets
            .iter_mut()
            .find(|(existing, _)| existing.dot(axis) > 0.985)
        {
            *area += length * 0.5;
        } else {
            buckets.push((axis, length * 0.5));
        }
    }
    let mut best = (Vec3::ZERO, 0.);
    for bucket in buckets {
        if bucket.1 > best.1 {
            best = bucket;
        }
    }
    best.0
}

#[cfg(test)]
mod tests;
