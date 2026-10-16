//! BarricadePlacer's engine-free rules: which surfaces each mount family takes,
//! the yaw it resolves, the standoff it hugs, the reason a ghost is red and the
//! transform the ghost (and so the placed body) takes. Units are source units.
use super::super::{Definition, geometry::Bounds, geometry::stand_rotation};
use bevy::prelude::*;

/// Source `BarricadeMount`.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum Mount {
    Floor,
    Wall,
    Sticky,
    Ceiling,
    Window,
    Container,
}
impl Mount {
    pub(crate) fn of(def: &Definition) -> Self {
        match def.mount.as_str() {
            "Wall" => Self::Wall,
            "Sticky" => Self::Sticky,
            "Ceiling" => Self::Ceiling,
            "Window" => Self::Window,
            "Container" => Self::Container,
            _ => Self::Floor,
        }
    }
}

/// `BarricadePlacer.SurfaceOk`: the per-EBuild normal gates.
pub(crate) fn surface_ok(mount: Mount, n: Vec3) -> bool {
    match mount {
        Mount::Floor => n.y >= 0.01,
        Mount::Wall => n.y.abs() < 0.1,
        Mount::Ceiling => n.y <= -0.01,
        _ => true,
    }
}
/// `BarricadePlacer.SurfaceReason`: what to do, not what is wrong.
pub(crate) fn surface_reason(mount: Mount) -> &'static str {
    match mount {
        Mount::Wall => "Needs a wall",
        Mount::Window => "Needs a window opening",
        Mount::Sticky => "Needs a surface",
        Mount::Container => "Needs a storage container",
        _ => "Needs flat ground",
    }
}
pub(crate) const TOO_FAR: &str = "Too far away";
pub(crate) const NO_ROOM: &str = "Not enough room";
pub(crate) const CANNOT_BUILD: &str = "Can't build on that";

/// `BarricadePlacer.YawFacing`: StandBasis(0) faces +Z, so atan2(n.x, n.z).
pub(crate) fn yaw_facing(n: Vec3) -> f32 {
    n.x.atan2(n.z).to_degrees()
}
/// `BarricadePlacer.Standoff`, shared by the ghost and `Barricade.PlaceOnSurface`.
pub(crate) fn standoff(mount: Mount, def: &Definition) -> f32 {
    match mount {
        Mount::Ceiling => 0.005,
        Mount::Container => def.size.y * 0.5 + 0.01,
        _ => def.offset.min(0.1),
    }
}
/// `BarricadePlacer.AlignUpTo`: the shortest arc taking world up to `n`.
pub(crate) fn align_up_to(n: Vec3) -> Quat {
    let n = n.normalize();
    let d = Vec3::Y.dot(n);
    if d > 0.99999 {
        Quat::IDENTITY
    } else if d < -0.99999 {
        Quat::from_rotation_x(std::f32::consts::PI)
    } else {
        Quat::from_axis_angle(Vec3::Y.cross(n).normalize(), d.clamp(-1., 1.).acos())
    }
}

/// What the physics probe saw along the aim ray: the surface, whether the
/// clearance sphere was free and whether the hit is attachable.
#[derive(Clone, Copy, Debug)]
pub(crate) struct Probe {
    pub point: Vec3,
    pub normal: Vec3,
    pub clear: bool,
    pub attach: bool,
}
/// One frame of `BarricadePlacer.Aim`.
#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) struct Aim {
    pub valid: bool,
    pub reason: Option<&'static str>,
    pub point: Vec3,
    pub normal: Vec3,
    pub yaw: f32,
}
impl Default for Aim {
    fn default() -> Self {
        Self {
            valid: false,
            reason: None,
            point: Vec3::ZERO,
            normal: Vec3::Y,
            yaw: 0.,
        }
    }
}

/// `BarricadePlacer.Aim` for the raycast families. `aim_yaw` is the camera yaw
/// plus the R-accumulated `YawOffset`, as the source composes it.
pub(crate) fn resolve(
    def: &Definition,
    mount: Mount,
    from: Vec3,
    dir: Vec3,
    aim_yaw: f32,
    yaw_offset: f32,
    probe: Option<Probe>,
    sea_level: f32,
) -> Aim {
    if mount == Mount::Window {
        // AimWindow with no opening under the reticle: parked along the ray.
        return Aim {
            valid: false,
            reason: Some(surface_reason(mount)),
            point: from + dir * def.range,
            normal: Vec3::Y,
            yaw: 0.,
        };
    }
    let Some(probe) = probe else {
        return Aim {
            valid: false,
            reason: Some(if mount == Mount::Container {
                surface_reason(mount)
            } else {
                TOO_FAR
            }),
            point: from + dir * def.range,
            normal: Vec3::Y,
            yaw: aim_yaw,
        };
    };
    let n = probe.normal.normalize_or(Vec3::Y);
    let yaw = match mount {
        Mount::Wall => yaw_facing(n),
        Mount::Sticky => yaw_offset,
        _ => aim_yaw,
    };
    let surface = surface_ok(mount, n);
    let mut aim = Aim {
        valid: surface && probe.clear && probe.attach,
        // Most specific first: an obstructed legal surface says "no room".
        reason: if !surface {
            Some(surface_reason(mount))
        } else if !probe.clear {
            Some(NO_ROOM)
        } else if !probe.attach {
            Some(CANNOT_BUILD)
        } else {
            None
        },
        point: probe.point,
        normal: n,
        yaw,
    };
    // Submersible device (fluid inlet): only on seabed within its depth band.
    if aim.valid && def.water_depth_min >= 0. {
        let depth = sea_level - probe.point.y;
        aim.valid = depth >= def.water_depth_min && depth <= def.water_depth_max;
        if !aim.valid {
            aim.reason = Some(if depth < def.water_depth_min {
                "Needs deeper water"
            } else {
                "Water too deep"
            });
        }
    }
    if aim.valid {
        aim.reason = None;
    }
    aim
}

/// `BarricadePlacer.GhostTransform` without its mesh fixup: the node the ghost
/// mesh (which carries `MeshBasis`) hangs from. `bounds` is BuildMesh's local
/// AABB, already rotated by the mesh fixup.
pub(crate) fn ghost_transform(def: &Definition, bounds: Bounds, mount: Mount, aim: &Aim) -> Transform {
    let yaw = Quat::from_rotation_y(aim.yaw.to_radians());
    let ground = if def.upright {
        yaw
    } else {
        yaw * stand_rotation()
    };
    if mount == Mount::Window {
        // StandBasis(Yaw) * Scale(1) at the parked point: no opening was found.
        return Transform::from_translation(aim.point).with_rotation(Quat::from_rotation_y(aim.yaw.to_radians()) * stand_rotation());
    }
    let rotation = if mount == Mount::Container && aim.normal.y > 0.5 {
        let f = Vec3::new(aim.yaw.to_radians().sin(), 0., aim.yaw.to_radians().cos());
        Quat::from_axis_angle(f.cross(Vec3::Y).normalize(), std::f32::consts::FRAC_PI_2) * ground
    } else if mount == Mount::Sticky {
        align_up_to(aim.normal) * ground
    } else {
        ground
    };
    let origin = if mount == Mount::Floor {
        aim.point
            + Vec3::Y
                * if def.upright {
                    -bounds.min.y
                } else {
                    -bounds.rotated(stand_rotation()).min.y
                }
    } else {
        aim.point + aim.normal * standoff(mount, def)
    };
    Transform::from_translation(origin).with_rotation(rotation)
}
