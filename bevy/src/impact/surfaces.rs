//! Original PropSurfaces and Terrain.SurfForMaterial lookup, shared by impact/footstep consumers.
use crate::world::{
    RoadSurface, WorldConfig, WorldGeometry, WorldObject, WorldResource, WorldTerrain,
};
use bevy::{ecs::system::SystemParam, prelude::*};
use std::collections::HashMap;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash)]
pub enum Surface {
    #[default]
    Concrete,
    Grass,
    Dirt,
    Metal,
    Wood,
    Sand,
    Water,
    Gravel,
    Snow,
    Ice,
    Rock,
    Rails,
}
/// Explicit source material for generated geometry without a retail prefab.
#[derive(Component, Clone, Copy)]
pub struct SurfaceOverride(pub Surface);
impl Surface {
    pub fn debris(self) -> &'static str {
        match self {
            Self::Metal => "metal",
            Self::Wood => "wood",
            Self::Grass => "foliage",
            Self::Water => "water",
            Self::Sand | Self::Dirt | Self::Gravel | Self::Snow => "gravel",
            _ => "concrete",
        }
    }
    pub fn bullet_bank(self) -> &'static str {
        match self {
            Self::Metal => "metallight",
            Self::Wood => "woodlight",
            Self::Grass => "grass",
            Self::Dirt => "dirt",
            Self::Sand | Self::Gravel => "gravel",
            Self::Water => "water",
            Self::Snow => "snow",
            Self::Ice => "ice",
            Self::Rock => "rock",
            Self::Rails => "concrete",
            Self::Concrete => "concrete",
        }
    }
    pub fn hard(self) -> bool {
        matches!(self, Self::Concrete | Self::Metal | Self::Wood)
    }
    pub fn physic(name: &str) -> Self {
        let n = name.trim().to_ascii_lowercase();
        for (prefix, s) in [
            ("metal", Self::Metal),
            ("wood", Self::Wood),
            ("gravel", Self::Gravel),
            ("foliage", Self::Grass),
            ("snow", Self::Snow),
            ("ice", Self::Ice),
            ("water", Self::Water),
            ("cloth", Self::Dirt),
        ] {
            if n.starts_with(prefix) {
                return s;
            }
        }
        Self::Concrete
    }
    pub fn terrain(name: &str) -> Self {
        let n = name.trim().to_ascii_lowercase();
        for (prefix, s) in [
            ("snow", Self::Snow),
            ("ice", Self::Ice),
            ("gravel", Self::Gravel),
            ("road", Self::Concrete),
            ("concrete", Self::Concrete),
            ("asphalt", Self::Concrete),
            ("sand", Self::Sand),
            ("stone", Self::Rock),
            ("rock", Self::Rock),
            ("cliff", Self::Rock),
            ("dirt", Self::Dirt),
            ("mud", Self::Dirt),
        ] {
            if n.starts_with(prefix) {
                return s;
            }
        }
        Self::Grass
    }
}
#[derive(Clone)]
struct Minority {
    surface: Surface,
    center: Vec3,
    size: Vec3,
}
#[derive(Resource, Default)]
pub struct SurfaceTable {
    map: String,
    props: HashMap<String, Surface>,
    boxes: HashMap<String, Vec<Minority>>,
    layers: Vec<Surface>,
}
impl SurfaceTable {
    pub fn load(config: &WorldConfig) -> Self {
        let mut out = Self {
            map: config.map_name.clone(),
            ..default()
        };
        if let Ok(text) = std::fs::read_to_string(config.content_root.join("objects/surfaces.tsv"))
        {
            for line in text.lines() {
                let c: Vec<_> = line.split('\t').collect();
                if c.len() >= 2 && !c[0].is_empty() {
                    out.props
                        .insert(c[0].trim().to_ascii_lowercase(), Surface::physic(c[1]));
                }
            }
        }
        if let Ok(text) =
            std::fs::read_to_string(config.content_root.join("objects/surfaces_boxes.tsv"))
        {
            for line in text.lines() {
                let c: Vec<_> = line.split('\t').collect();
                if c.len() < 8 {
                    continue;
                }
                let values: Option<Vec<f32>> = c[2..8].iter().map(|s| s.parse().ok()).collect();
                if let Some(v) = values {
                    out.boxes
                        .entry(c[0].trim().to_ascii_lowercase())
                        .or_default()
                        .push(Minority {
                            surface: Surface::physic(c[1]),
                            size: Vec3::new(v[0], v[1], v[2]),
                            center: Vec3::new(v[3], v[4], v[5]),
                        });
                }
            }
        }
        let folder = match config.map_name.as_str() {
            "Washington" => "terrain_washington",
            "Yukon" => "terrain_yukon",
            _ => "terrain",
        };
        let names = std::fs::read_to_string(config.content_root.join(folder).join("layers.txt"))
            .unwrap_or_else(|_| "Dirt\nWheat\nGrass\nGravel\nRoad\nSand\nSnow\nStone".into());
        out.layers = names
            .lines()
            .map(str::trim)
            .filter(|s| !s.is_empty())
            .map(Surface::terrain)
            .collect();
        out
    }
    pub fn prop(&self, name: &str, local: Vec3, wood_fallback: bool) -> Surface {
        if name.starts_with("New_Rail_") {
            return Surface::Rails;
        }
        let mut name = name.to_ascii_lowercase();
        if let Some(boxes) = self.boxes.get(&name) {
            for b in boxes {
                if (local - b.center)
                    .abs()
                    .cmple(b.size * 0.5 + Vec3::splat(0.01))
                    .all()
                {
                    return b.surface;
                }
            }
        }
        loop {
            if let Some(s) = self.props.get(&name) {
                return *s;
            }
            let Some(i) = name.rfind('_') else {
                break;
            };
            if i == 0 {
                break;
            }
            name.truncate(i);
        }
        if wood_fallback {
            Surface::Wood
        } else {
            Surface::Concrete
        }
    }
}
pub fn reload(config: Res<WorldConfig>, mut table: ResMut<SurfaceTable>) {
    if table.map != config.map_name {
        *table = SurfaceTable::load(&config);
    }
}
#[derive(SystemParam)]
pub struct Surfaces<'w, 's> {
    table: Res<'w, SurfaceTable>,
    geometry: Res<'w, WorldGeometry>,
    nodes: Query<
        'w,
        's,
        (
            Option<&'static SurfaceOverride>,
            Option<&'static WorldObject>,
            Option<&'static WorldResource>,
            Option<&'static RoadSurface>,
            Option<&'static WorldTerrain>,
            Option<&'static crate::vehicle::VehicleBody>,
            Option<&'static GlobalTransform>,
            Option<&'static ChildOf>,
        ),
    >,
}
impl Surfaces<'_, '_> {
    /// Return the source surface and optional moving-body parent for its decal.
    pub fn at(
        &self,
        mut entity: Entity,
        point: Vec3,
    ) -> (Surface, Option<(Entity, GlobalTransform)>) {
        for _ in 0..16 {
            let Ok((explicit, object, resource, road, terrain, vehicle, pose, parent)) =
                self.nodes.get(entity)
            else {
                break;
            };
            if let Some(explicit) = explicit {
                return (explicit.0, None);
            }
            if vehicle.is_some() {
                return (Surface::Metal, pose.map(|p| (entity, *p)));
            }
            if let Some(road) = road {
                return (
                    if road.concrete {
                        Surface::Concrete
                    } else {
                        Surface::Gravel
                    },
                    None,
                );
            }
            if terrain.is_some() {
                let layer = crate::world::layers::sample(&self.geometry.terrain, point.xz());
                return (
                    self.table
                        .layers
                        .get(layer as usize)
                        .copied()
                        .unwrap_or(Surface::Grass),
                    None,
                );
            }
            if let Some(resource) = resource {
                let lower = resource.name.to_ascii_lowercase();
                return (
                    if lower.contains("metal") || lower.contains("ore") {
                        Surface::Metal
                    } else {
                        Surface::Wood
                    },
                    None,
                );
            }
            if let Some(object) = object {
                let local = pose
                    .copied()
                    .map_or(point, |p| p.affine().inverse().transform_point3(point));
                return (self.table.prop(&object.asset_name, local, false), None);
            }
            let Some(parent) = parent else {
                break;
            };
            entity = parent.parent();
        }
        (Surface::Concrete, None)
    }
}

#[derive(SystemParam)]
pub struct CurrentPoses<'w, 's> {
    nodes: Query<'w, 's, (&'static Transform, Option<&'static ChildOf>)>,
}
impl CurrentPoses<'_, '_> {
    /// Physics transforms are authoritative before PostUpdate render propagation.
    pub fn current_pose(&self, mut entity: Entity) -> Option<GlobalTransform> {
        let mut chain = Vec::new();
        for _ in 0..64 {
            let (pose, parent) = self.nodes.get(entity).ok()?;
            chain.push(*pose);
            if let Some(parent) = parent {
                entity = parent.parent();
            } else {
                return Some(
                    chain
                        .into_iter()
                        .rev()
                        .fold(GlobalTransform::IDENTITY, |p, t| p.mul_transform(t)),
                );
            }
        }
        None
    }
}
