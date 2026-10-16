//! Original Vehicle.Build lamp meshes, side splits and aircraft fittings.
//! This builds presentation metadata only; authority owns electrical/damage state.
use crate::{
    content::{ContentCache, ObjData},
    vehicle::data::VehicleDefinition,
};
use bevy::prelude::*;
use std::{path::Path, sync::Arc};
mod math;
#[cfg(test)]
mod tests;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(in crate::vehicle) enum Kind {
    Head,
    Tail,
    SirenLeft,
    SirenRight,
    NavPort,
    NavStarboard,
    Beacon,
}
pub(in crate::vehicle) struct Emitter {
    pub position: Vec3,
    pub color: Color,
    /// Original energy while lit; the presentation system initializes it dark.
    pub energy: f32,
    pub range: f32,
}
pub(in crate::vehicle) struct Lens {
    pub kind: Kind,
    pub entity: Entity,
    pub material: Handle<StandardMaterial>,
    pub center: Vec3,
    pub color: Color,
    pub emitter: Option<Emitter>,
}
pub(in crate::vehicle) struct Lamp {
    pub label: &'static str,
    pub entity: Entity,
    pub center: Vec3,
    /// Original mesh-local GetAabb.Size/2, used as a world-aligned shard box.
    pub half_extents: Vec3,
    pub material: Option<Handle<StandardMaterial>>,
}
#[derive(Component)]
#[require(super::breaks::BreakColors)]
pub(in crate::vehicle) struct BuiltLighting {
    pub lenses: Vec<Lens>,
    pub lamps: Vec<Lamp>,
    pub head_spots: Vec<Vec3>,
    pub tail_spots: Vec<Vec3>,
    pub head_fill: Option<Vec3>,
    pub head_tint: Color,
    pub round: bool,
    /// Exact source _headlightMat/_taillightMat aliases; parts choose the last
    /// side, baked zones the first, and unpaired/box paths overwrite the alias.
    pub head_primary: Option<Handle<StandardMaterial>>,
    pub tail_primary: Option<Handle<StandardMaterial>>,
}
impl Default for BuiltLighting {
    fn default() -> Self {
        Self {
            lenses: Vec::new(),
            lamps: Vec::new(),
            head_spots: Vec::new(),
            tail_spots: Vec::new(),
            head_fill: None,
            head_tint: Color::srgb(0.97, 0.96, 0.83),
            round: false,
            head_primary: None,
            tail_primary: None,
        }
    }
}
pub(in crate::vehicle) fn consumes(name: &str) -> bool {
    name.contains("headlight")
        || name.contains("taillight")
        || name.contains("siren0")
        || name.contains("siren1")
}
struct Builder<'a, 'w, 's> {
    commands: &'a mut Commands<'w, 's>,
    parent: Entity,
    root: &'a Path,
    cache: &'a mut ContentCache,
    meshes: &'a mut Assets<Mesh>,
    materials: &'a mut Assets<StandardMaterial>,
    out: BuiltLighting,
}
impl Builder<'_, '_, '_> {
    fn load(&mut self, name: &str) -> Option<Arc<ObjData>> {
        self.cache
            .load_mesh(&self.root.join(name), self.meshes)
            .ok()
            .map(|(_, d)| d)
    }
    fn cached(&mut self, name: &str, tag: &str, data: &ObjData) -> Handle<Mesh> {
        let key = self
            .root
            .join(name)
            .with_extension(format!("{tag}-lighting-native"));
        if let Some(h) = self.cache.meshes.get(&key) {
            return h.clone();
        }
        let mesh = self.meshes.add(data.mesh());
        self.cache.meshes.insert(key.clone(), mesh.clone());
        self.cache.geometry.insert(key, Arc::new(data.clone()));
        mesh
    }
    fn lens(
        &mut self,
        name: &str,
        mesh: Handle<Mesh>,
        data: &ObjData,
        kind: Kind,
        color: Color,
        roughness: f32,
        pose: Transform,
        label: Option<&'static str>,
        emitter: Option<Emitter>,
    ) {
        let material = self.materials.add(StandardMaterial {
            base_color: color,
            perceptual_roughness: roughness,
            cull_mode: None,
            ..default()
        });
        let entity = self
            .commands
            .spawn((
                Name::new(name.to_owned()),
                Mesh3d(mesh),
                MeshMaterial3d(material.clone()),
                pose,
                Visibility::Inherited,
                ChildOf(self.parent),
            ))
            .id();
        let center = pose.transform_point((data.min + data.max) * 0.5);
        if let Some(label) = label {
            self.out.lamps.push(Lamp {
                label,
                entity,
                center,
                half_extents: (data.max - data.min) * 0.5,
                material: Some(material.clone()),
            });
        }
        self.out.lenses.push(Lens {
            kind,
            entity,
            material,
            center,
            color,
            emitter,
        });
    }
    fn pair(
        &mut self,
        name: &str,
        data: &ObjData,
        kind: Kind,
        color: Color,
        baked: bool,
        auto: bool,
    ) {
        let (left, right) = math::halves(data);
        let pair = if baked {
            !left.positions.is_empty() || !right.positions.is_empty()
        } else {
            !left.positions.is_empty() && !right.positions.is_empty()
        };
        if pair {
            let start = self.out.lenses.len();
            for (half, side) in [(&left, "l"), (&right, "r")] {
                if half.positions.is_empty() {
                    continue;
                }
                let head = kind == Kind::Head;
                let label = match (head, side) {
                    (true, "l") => "headlight_l",
                    (true, _) => "headlight_r",
                    (false, "l") => "taillight_l",
                    _ => "taillight_r",
                };
                if auto {
                    let c = (half.min + half.max) * 0.5;
                    let pos = Vec3::new(
                        c.x,
                        c.y,
                        if head {
                            half.min.z - 0.06
                        } else {
                            half.max.z + 0.06
                        },
                    );
                    if head {
                        self.out.head_spots.push(pos);
                    } else {
                        self.out.tail_spots.push(pos);
                    }
                }
                let mesh = self.cached(name, label, half);
                self.lens(
                    &format!("Lamp_{label}"),
                    mesh,
                    half,
                    kind,
                    color,
                    if baked { 0.5 } else { 0.9 },
                    Transform::IDENTITY,
                    Some(label),
                    None,
                );
            }
            let chosen = if baked {
                self.out.lenses.get(start)
            } else {
                self.out.lenses.last()
            };
            if let Some(chosen) = chosen {
                let alias = if kind == Kind::Head {
                    &mut self.out.head_primary
                } else {
                    &mut self.out.tail_primary
                };
                if alias.is_none() {
                    *alias = Some(chosen.material.clone());
                }
            }
        } else if !data.positions.is_empty() {
            let mesh = self.cached(name, "whole", data);
            self.lens(
                name,
                mesh,
                data,
                kind,
                color,
                if baked { 0.5 } else { 0.9 },
                Transform::IDENTITY,
                None,
                None,
            );
            let alias = if kind == Kind::Head {
                &mut self.out.head_primary
            } else {
                &mut self.out.tail_primary
            };
            *alias = Some(self.out.lenses.last().unwrap().material.clone());
        }
    }
    fn nav(&mut self, name: &str, data: &ObjData) {
        let center = (data.min + data.max) * 0.5;
        let cx = if center.x.abs() < 0.00001 {
            -1.
        } else {
            center.x
        };
        let mesh = self.cached(name, "nav", data);
        for mirrored in [false, true] {
            let port = (cx < 0.) != mirrored;
            let (kind, color, name) = if port {
                (Kind::NavPort, Color::srgb(0.95, 0.05, 0.05), "NavLightPort")
            } else {
                (
                    Kind::NavStarboard,
                    Color::srgb(0.05, 0.95, 0.15),
                    "NavLightStarboard",
                )
            };
            let position = Vec3::new(if mirrored { -cx } else { cx }, center.y, center.z);
            self.lens(
                name,
                mesh.clone(),
                data,
                kind,
                color,
                0.4,
                Transform::from_scale(Vec3::new(if mirrored { -1. } else { 1. }, 1., 1.)),
                None,
                Some(Emitter {
                    position,
                    color,
                    energy: 1.4,
                    range: 2.2,
                }),
            );
        }
    }
}

pub(in crate::vehicle) struct BodySplit {
    pub mesh: Handle<Mesh>,
    pub data: Arc<ObjData>,
    pub legs: Option<Handle<Mesh>>,
}

/// Returns the optional main-body replacement and all owned lamp presentation.
/// The caller renders unconsumed parts and inserts BuiltLighting on the body.
pub(in crate::vehicle) fn build(
    commands: &mut Commands,
    parent: Entity,
    d: &VehicleDefinition,
    root: &Path,
    cache: &mut ContentCache,
    meshes: &mut Assets<Mesh>,
    materials: &mut Assets<StandardMaterial>,
) -> (Option<BodySplit>, BuiltLighting) {
    let s = &d.spec;
    let mut b = Builder {
        commands,
        parent,
        root,
        cache,
        meshes,
        materials,
        out: BuiltLighting::default(),
    };
    let mut body_override = None;
    // Preserve source branch priority: helicopters/planes never take road-body splits;
    // landing-leg branch precedes baked headlights. Tail zones belong to trailers.
    if !s.heli && !s.plane {
        let legs = s.landing_leg_zone_min != s.landing_leg_zone_max;
        let head = !legs && s.headlight_zone_min != s.headlight_zone_max;
        let tail = legs && s.taillight_zone_min != s.taillight_zone_max;
        if (legs || head)
            && let Some(name) = &s.body
            && let Some(data) = b.load(name)
        {
            // Original ParseObjSplit2 assigns legs first, then lenses, with
            // every corner inside its zone. The two source categories cannot
            // duplicate a triangle even if an authored box overlaps another.
            let (rest, leg_mesh) = if legs {
                let (rest, selected) = data.split_zones(&[(
                    Vec3::from_array(s.landing_leg_zone_min),
                    Vec3::from_array(s.landing_leg_zone_max),
                )]);
                let mesh = (!selected.positions.is_empty())
                    .then(|| b.cached(name, "landing-legs", &selected));
                (rest, mesh)
            } else {
                ((*data).clone(), None)
            };
            let rest = if head || tail {
                let zones = if head {
                    math::mirrored_zones(s.headlight_zone_min, s.headlight_zone_max)
                } else {
                    math::mirrored_zones(s.taillight_zone_min, s.taillight_zone_max)
                };
                let (rest, lenses) = rest.split_zones(&zones);
                b.pair(
                    name,
                    &lenses,
                    if head { Kind::Head } else { Kind::Tail },
                    if head {
                        Color::srgb(0.94, 0.89, 0.73)
                    } else {
                        Color::srgb(0.42, 0.06, 0.06)
                    },
                    true,
                    false,
                );
                rest
            } else {
                rest
            };
            body_override = Some(BodySplit {
                mesh: b.cached(name, "body-without-lamps-and-legs", &rest),
                data: Arc::new(rest),
                legs: leg_mesh,
            });
        }
    }
    if let Some(parts) = &s.parts {
        for (name, c) in parts {
            if !consumes(name) {
                continue;
            }
            let Some(data) = b.load(name) else {
                continue;
            };
            if s.heli && name.contains("taillights") {
                b.nav(name, &data);
                continue;
            }
            let color = Color::srgba(c[0], c[1], c[2], c[3]);
            if !s.heli && (name.contains("headlight") || name.contains("taillight")) {
                let head = name.contains("headlight");
                if head && let Some((round, color)) = math::classify(&data) {
                    b.out.round = round;
                    b.out.head_tint = color;
                }
                b.pair(
                    name,
                    &data,
                    if head { Kind::Head } else { Kind::Tail },
                    color,
                    false,
                    if head {
                        s.spot_pos.is_none()
                    } else {
                        s.tail_pos.is_none()
                    },
                );
                continue;
            }
            let (kind, label, emitter) = if name.contains("siren0") {
                (
                    Kind::SirenLeft,
                    Some("lightbar_l"),
                    Some(Color::srgb(1., 0.05, 0.05)),
                )
            } else if name.contains("siren1") {
                (
                    Kind::SirenRight,
                    Some("lightbar_r"),
                    Some(Color::srgb(0.2, 0.3, 1.)),
                )
            } else {
                (
                    if name.contains("headlight") {
                        Kind::Head
                    } else {
                        Kind::Tail
                    },
                    None,
                    None,
                )
            };
            let mesh = b.cached(name, "whole", &data);
            b.lens(
                name,
                mesh,
                &data,
                kind,
                color,
                0.9,
                Transform::IDENTITY,
                label,
                emitter.map(|color| Emitter {
                    position: (data.min + data.max) * 0.5,
                    color,
                    energy: 5.,
                    range: 12.,
                }),
            );
            if kind == Kind::Head {
                b.out.head_primary = Some(b.out.lenses.last().unwrap().material.clone());
                if let Some((round, color)) = math::classify(&data) {
                    b.out.round = round;
                    b.out.head_tint = color;
                }
            } else if kind == Kind::Tail {
                b.out.tail_primary = Some(b.out.lenses.last().unwrap().material.clone());
            }
        }
    }
    let siren = |label| {
        b.out
            .lamps
            .iter()
            .find(|l| l.label == label)
            .map(|l| l.center)
    };
    if let (Some(left), Some(right)) = (siren("lightbar_l"), siren("lightbar_r")) {
        let center = (left + right) * 0.5;
        let mesh = b.meshes.add(Cuboid::new(0.36, 0.16, 0.24));
        let entity = b
            .commands
            .spawn((
                Name::new("Lamp_lightbar_c"),
                Mesh3d(mesh),
                Transform::from_translation(center),
                Visibility::Hidden,
                ChildOf(parent),
            ))
            .id();
        b.out.lamps.push(Lamp {
            label: "lightbar_c",
            entity,
            center,
            half_extents: Vec3::new(0.18, 0.08, 0.12),
            material: None,
        });
    }
    if let Some(positions) = &s.taillight_mesh {
        // The source's added rear boxes share one material and are not shootable lamps.
        let color = Color::srgb(0.42, 0.06, 0.06);
        let material = b.materials.add(StandardMaterial {
            base_color: color,
            perceptual_roughness: 0.5,
            cull_mode: None,
            ..default()
        });
        b.out.tail_primary = Some(material.clone());
        let mesh = b.meshes.add(Cuboid::new(0.34, 0.28, 0.14));
        for pos in positions {
            let center = Vec3::from_array(*pos);
            let entity = b
                .commands
                .spawn((
                    Name::new("TaillightBox"),
                    Mesh3d(mesh.clone()),
                    MeshMaterial3d(material.clone()),
                    Transform::from_translation(center),
                    Visibility::Inherited,
                    ChildOf(parent),
                ))
                .id();
            b.out.lenses.push(Lens {
                kind: Kind::Tail,
                entity,
                material: material.clone(),
                center,
                color,
                emitter: None,
            });
        }
    }
    if s.heli {
        let source = s.beacon_lens_from.as_deref().or_else(|| {
            s.parts
                .as_ref()?
                .iter()
                .find(|(name, _)| name.contains("taillights"))
                .map(|(name, _)| name.as_str())
        });
        let lens = source.and_then(|name| b.load(name).and_then(|d| math::beacon(&d)));
        let center = Vec3::new(
            0.,
            s.box_center[1] - s.box_size[1] * 0.5 - 0.08,
            s.box_center[2],
        );
        let color = Color::srgb(1., 0.06, 0.06);
        let material = b.materials.add(StandardMaterial {
            base_color: color,
            perceptual_roughness: 0.4,
            cull_mode: None,
            ..default()
        });
        let mesh = if let Some(lens) = lens {
            b.cached(source.unwrap(), "belly-beacon", &lens)
        } else {
            b.meshes.add(Sphere::new(0.1).mesh().uv(8, 4))
        };
        let entity = b
            .commands
            .spawn((
                Name::new("BeaconBelly"),
                Mesh3d(mesh),
                MeshMaterial3d(material.clone()),
                Transform::from_translation(center),
                Visibility::Inherited,
                ChildOf(parent),
            ))
            .id();
        b.out.lenses.push(Lens {
            kind: Kind::Beacon,
            entity,
            material,
            center,
            color,
            emitter: Some(Emitter {
                position: center,
                color: Color::srgb(1., 0.1, 0.1),
                energy: 3.2,
                range: 6.,
            }),
        });
    }
    if let Some(positions) = &s.spot_pos {
        b.out.head_spots = positions.iter().copied().map(Vec3::from_array).collect();
    }
    if let Some(positions) = &s.tail_pos {
        b.out.tail_spots = positions.iter().copied().map(Vec3::from_array).collect();
    }
    if (s.spot_pos.is_some() || !b.out.head_spots.is_empty()) && s.omni_pos != [0.; 3] {
        b.out.head_fill = Some(Vec3::from_array(s.omni_pos) + Vec3::Y * 0.5);
    }
    (body_override, b.out)
}
