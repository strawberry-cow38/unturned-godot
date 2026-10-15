//! Deployable.BuildMesh AABB and Barricade.PlaceOnSurface seating, in source units.
use super::*;

#[derive(Clone, Copy, Debug)]
pub(super) struct Bounds {
    pub min: Vec3,
    pub max: Vec3,
}
impl Bounds {
    pub fn corners(self) -> [Vec3; 8] {
        std::array::from_fn(|i| {
            self.min
                + (self.max - self.min)
                    * Vec3::new((i & 1) as f32, ((i >> 1) & 1) as f32, ((i >> 2) & 1) as f32)
        })
    }
    pub fn rotated(self, rotation: Quat) -> Self {
        self.corners().into_iter().map(|p| rotation * p).fold(
            Self {
                min: Vec3::splat(f32::INFINITY),
                max: Vec3::splat(f32::NEG_INFINITY),
            },
            |b, p| Self {
                min: b.min.min(p),
                max: b.max.max(p),
            },
        )
    }
    pub fn collider(self) -> Collider {
        let size = self.max - self.min;
        Collider::compound(vec![(
            (self.min + self.max) * 0.5,
            Quat::IDENTITY,
            Collider::cuboid(size.x * 0.5, size.y * 0.5, size.z * 0.5),
        )])
    }
}
pub(super) fn env_vector(name: &str, fallback: Vec3) -> Vec3 {
    std::env::var(name)
        .ok()
        .and_then(|text| {
            let v = text
                .split(',')
                .map(|s| s.trim().parse::<f32>().ok())
                .collect::<Option<Vec<_>>>()?;
            (v.len() == 3)
                .then(|| Vec3::new(v[0], v[1], v[2]))
                .filter(|v| v.is_finite())
        })
        .unwrap_or(fallback)
}
pub(super) fn euler(degrees: Vec3) -> Quat {
    Quat::from_euler(
        EulerRot::YXZ,
        degrees.y.to_radians(),
        degrees.x.to_radians(),
        degrees.z.to_radians(),
    )
}
pub(super) fn mesh_rotation(def: &Definition) -> Quat {
    let degrees = Vec3::from(def.mesh_euler);
    euler(if def.id == 1450 {
        env_vector("UG_BATROT", degrees)
    } else {
        degrees
    })
}
pub(super) fn stand_rotation() -> Quat {
    let degrees = std::env::var("UG_DEPLOYROT")
        .ok()
        .and_then(|v| v.parse::<f32>().ok())
        .filter(|v| v.is_finite())
        .unwrap_or(90.);
    Quat::from_rotation_x(degrees.to_radians())
}
pub(super) fn seat(def: &Definition, bounds: Bounds, pose: Pose) -> Transform {
    let yaw = pose.yaw.to_radians();
    let ground = Quat::from_rotation_y(yaw)
        * if def.upright {
            Quat::IDENTITY
        } else {
            stand_rotation()
        };
    let normal = match def.mount.as_str() {
        "Ceiling" => -Vec3::Y,
        "Container" if pose.mount_up => Vec3::Y,
        "Wall" | "Container" => Vec3::new(yaw.sin(), 0., yaw.cos()),
        _ => Vec3::Y,
    };
    let surface = matches!(def.mount.as_str(), "Ceiling" | "Wall" | "Container");
    let lift = if def.upright {
        -bounds.min.y
    } else {
        -bounds.rotated(stand_rotation()).min.y
    };
    let translation = pose.point
        + if surface {
            normal
                * match def.mount.as_str() {
                    "Ceiling" => 0.005,
                    "Container" => def.size.y * 0.5 + 0.01,
                    _ => def.offset.min(0.1),
                }
        } else {
            Vec3::Y * lift
        };
    let rotation = if def.mount == "Container" && pose.mount_up {
        let forward = Vec3::new(yaw.sin(), 0., yaw.cos());
        Quat::from_axis_angle(
            forward.cross(Vec3::Y).normalize(),
            std::f32::consts::FRAC_PI_2,
        ) * ground
    } else {
        ground
    };
    Transform::from_translation(translation).with_rotation(rotation)
}
pub(super) fn authority(bounds: Bounds, pose: Transform, id: u32) -> Value {
    let collider = bounds.collider();
    let compound = collider.as_compound().unwrap();
    let (local, shape) = &compound.raw.shapes()[0];
    let (points, triangles) = shape.as_cuboid().unwrap().to_trimesh();
    let vertices: Vec<_> = points
        .into_iter()
        .map(|p| pose.transform_point(local.transform_point(p)).to_array())
        .collect();
    let indices: Vec<_> = triangles.into_iter().flatten().collect();
    json!({"vertices":vertices,"indices":indices,"deployableNetId":id,"sourceWorld":true,"combat":true})
}
