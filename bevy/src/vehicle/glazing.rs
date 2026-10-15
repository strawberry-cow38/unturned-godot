//! Original ordered vehicle panes. Authority owns broken state; native geometry
//! supplies the same contact boxes and renders only accepted break/repair facts.
use super::{
    VehicleBody,
    data::{VehicleDefinition, VehicleSpec},
};
use crate::{content::ContentCache, player::GameSnapshot};
use bevy::prelude::*;
use bevy_rapier3d::prelude::*;
use std::path::Path;

pub(super) mod collision;
mod effects;
mod rain;
mod upload;

/// Canonical wire bits. Missing files do not renumber the remaining panes.
pub(super) const LABELS: [&str; 18] = [
    "windshield",
    "rear",
    "l_front",
    "r_front",
    "l_rear",
    "r_rear",
    "l_mid1",
    "r_mid1",
    "l_mid2",
    "r_mid2",
    "l_mid3",
    "r_mid3",
    "l_mid4",
    "r_mid4",
    "roof",
    "roof_front",
    "roof_rear",
    "canopy",
];

#[derive(Component)]
pub(super) struct Pane {
    pub index: u8,
    pub center: Vec3,
    pub size: Vec3,
    pub debug_color: Option<Color>,
}
impl Pane {
    pub fn has_triangles(&self) -> bool {
        self.index != 17
    }
    pub fn label(&self) -> &'static str {
        LABELS[self.index as usize]
    }
}

#[derive(Component, Default)]
pub(super) struct Glazing {
    pub panes: Vec<Entity>,
    pub broken: u32,
    /// Local pane transforms also invalidate stationary vehicle BVH uploads.
    pub signature: Vec<f32>,
    uploaded: Option<(u64, u32, Vec<f32>)>,
}

pub(super) struct GlazingPlugin;
impl Plugin for GlazingPlugin {
    fn build(&self, app: &mut App) {
        app.add_plugins(rain::RainGlassPlugin)
            .init_resource::<collision::Shapes>()
            .add_systems(
                Update,
                (collision::install, adopt)
                    .chain()
                    .after(super::VehicleUpdate),
            )
            .add_systems(
                Update,
                effects::receive
                    .after(crate::entities::CoreEventsReady)
                    .after(adopt),
            )
            .add_systems(
                PostUpdate,
                upload::publish
                    .after(TransformSystems::Propagate)
                    .before(super::geometry::upload),
            );
    }
}

fn files(name: &str, root: &Path) -> Vec<(u8, String)> {
    let stem = name.strip_suffix(".txt").unwrap_or(name);
    let mut panes: Vec<_> = LABELS[..17]
        .iter()
        .enumerate()
        .map(|(i, label)| (i as u8, format!("{stem}_{label}.txt")))
        .filter(|(_, file)| root.join(file).is_file())
        .collect();
    if panes.is_empty() && root.join(name).is_file() {
        panes.push((17, name.to_owned()));
    }
    panes
}

const DEBUG_COLORS: [[f32; 3]; 17] = [
    [1., 0.2, 1.],
    [0.1, 1., 1.],
    [0.25, 0.45, 1.],
    [1., 0.15, 0.15],
    [1., 0.95, 0.1],
    [0.15, 1., 0.25],
    [1., 0.55, 0.],
    [0.6, 0.2, 1.],
    [1., 1., 1.],
    [0.1, 0.35, 0.2],
    [0.5, 0.9, 0.1],
    [0.9, 0.4, 0.6],
    [0.2, 0.2, 0.6],
    [0.7, 0.7, 0.2],
    [0., 0.8, 0.6],
    [0.85, 0.3, 0.],
    [0.45, 0.65, 1.],
];

#[allow(clippy::too_many_arguments)]
pub(super) fn build(
    commands: &mut Commands,
    entity: Entity,
    d: &VehicleDefinition,
    root: &Path,
    cache: &mut ContentCache,
    meshes: &mut Assets<Mesh>,
    materials: &mut Assets<StandardMaterial>,
    bifold: Option<&super::bifold::Built>,
) {
    let Some(name) = &d.spec.glass_mesh else {
        commands.entity(entity).insert(Glazing::default());
        return;
    };
    let color = d.spec.glass_tint.unwrap_or([0.78, 0.62, 0.30, 0.40]);
    let material = materials.add(StandardMaterial {
        base_color: Color::srgba(color[0], color[1], color[2], color[3]),
        alpha_mode: AlphaMode::Blend,
        perceptual_roughness: 0.1,
        metallic: 0.35,
        cull_mode: None,
        ..default()
    });
    let debug = std::env::var("UG_GLASSDEBUG").as_deref() == Ok("1");
    let mut glazing = Glazing::default();
    for (index, file) in files(name, root) {
        let Ok((mesh, data)) = cache.load_mesh(&root.join(&file), meshes) else {
            continue;
        };
        // Source debug colors follow materialized order, not canonical wire bits.
        let debug_color = (debug && index != 17).then(|| {
            let c = DEBUG_COLORS[glazing.panes.len() % DEBUG_COLORS.len()];
            Color::srgb(c[0], c[1], c[2])
        });
        let mat = debug_color.map_or_else(
            || material.clone(),
            |color| {
                materials.add(StandardMaterial {
                    base_color: color,
                    unlit: true,
                    cull_mode: None,
                    ..default()
                })
            },
        );
        let attached = bifold.and_then(|door| door.pane(LABELS[index as usize], &d.spec));
        let child = commands
            .spawn((
                Name::new(format!("Glass_{}", LABELS[index as usize])),
                Mesh3d(mesh),
                MeshMaterial3d(mat),
                attached.map_or(Transform::IDENTITY, |(_, pose)| pose),
                Visibility::Inherited,
                ChildOf(attached.map_or(entity, |(parent, _)| parent)),
                bevy::light::NotShadowCaster,
                Pane {
                    index,
                    center: (data.min + data.max) * 0.5,
                    size: data.max - data.min,
                    debug_color,
                },
            ))
            .id();
        if attached.is_some() {
            super::bifold::pane(commands, child, &data);
        }
        if debug_color.is_none() {
            commands.entity(child).insert(rain::RainPane {
                body: entity,
                axis: rain::dominant_axis(&data.positions),
            });
        }
        glazing.panes.push(child);
    }
    commands
        .entity(entity)
        .insert((glazing, rain::Cover::default()));
}

pub(super) fn adopt(
    snapshot: Res<GameSnapshot>,
    mut bodies: Query<(&mut VehicleBody, &mut Glazing)>,
    mut panes: Query<(
        &Pane,
        &mut Visibility,
        Option<&mut CollisionGroups>,
        Has<super::bifold::DoorPane>,
    )>,
) {
    let Some(rows) = snapshot.0["vehicles"].as_array() else {
        return;
    };
    for (mut body, mut glazing) in &mut bodies {
        let Some(row) = rows
            .iter()
            .find(|r| r["netId"].as_u64() == Some(body.net_id as u64))
        else {
            continue;
        };
        // Older snapshots cannot manufacture a repair; only an explicit accepted mask adopts.
        let Some(broken) = row["brokenGlassMask"].as_u64() else {
            continue;
        };
        glazing.broken = broken as u32;
        body.glass_mask = row["glassMask"].as_u64().unwrap_or(0) as u32;
        body.broken_glass_mask = glazing.broken;
        for entity in &glazing.panes {
            let Ok((pane, mut visibility, groups, door)) = panes.get_mut(*entity) else {
                continue;
            };
            let intact = glazing.broken & (1 << pane.index) == 0;
            *visibility = if intact {
                Visibility::Inherited
            } else {
                Visibility::Hidden
            };
            if let Some(mut groups) = groups {
                *groups = if door {
                    super::bifold::groups(intact)
                } else {
                    collision::player_groups(intact)
                };
            }
        }
    }
}

/// Vehicle.GlassPaneNormal: thinnest local axis, transformed and flipped away
/// from the hull origin. This intentionally differs from the rain shader's face axis.
fn outward(pane: &Pane, pose: &GlobalTransform, hull: Vec3) -> Vec3 {
    let s = pane.size;
    let axis = if s.x <= s.y && s.x <= s.z {
        Vec3::X
    } else if s.y <= s.z {
        Vec3::Y
    } else {
        Vec3::Z
    };
    let normal = pose.affine().transform_vector3(axis).normalize_or_zero();
    let away = pose.transform_point(pane.center) - hull;
    if normal.dot(away) < 0. {
        -normal
    } else {
        normal
    }
}

fn mesh_hitbox() -> bool {
    std::env::var("UG_MESHHITBOX").as_deref() != Ok("0")
}
fn body_hitbox(spec: &VehicleSpec) -> bool {
    mesh_hitbox()
        && spec.body.is_some()
        && spec.hull_bands.is_none()
        && spec.hull_boxes.is_none()
        && std::env::var("UG_BOXHULL").as_deref() != Ok("1")
}

#[cfg(test)]
mod tests;
