//! Original shoulder camera geometry and eye-origin aim convergence.
use bevy::prelude::*;
use bevy_rapier3d::prelude::*;
mod seated;
#[derive(Resource)]
pub struct Perspective {
    pub first_person: bool,
    pub head: Vec2,
    pub orbit: Vec2,
    pub left: bool,
    pub shoulder: f32,
    pub pivot_height: f32,
    pub drive: Vec2,
    pub drive_zoom: f32,
    pub fly: Vec2,
    pub(crate) vehicle: Option<u32>,
}
impl Default for Perspective {
    fn default() -> Self {
        Self {
            first_person: true,
            head: Vec2::ZERO,
            orbit: Vec2::ZERO,
            left: false,
            shoulder: 1.,
            pivot_height: 1.6,
            drive: Vec2::new(0., 15.),
            drive_zoom: 1.,
            fly: Vec2::ZERO,
            vehicle: None,
        }
    }
}
pub fn pivot(eye: f32, height: f32) -> f32 {
    eye.clamp(0.395, (height - 0.395).max(0.395))
}
pub fn offset(shoulder: f32) -> Vec3 {
    Vec3::new(shoulder, 0.25, 1.5).normalize()
}
/// Source stepped/bisected radius .39 query, so a thin wall cannot be skipped.
pub fn sweep(
    context: &RapierContext,
    origin: Vec3,
    direction: Vec3,
    length: f32,
    owner: Entity,
) -> f32 {
    let filter = QueryFilter::default()
        .exclude_collider(owner)
        .groups(CollisionGroups::new(
            Group::ALL,
            Group::GROUP_1 | Group::GROUP_4,
        ));
    let shape = Collider::ball(0.39);
    let blocked = |distance: f32| {
        let mut hit = false;
        context.intersect_shape(
            origin + direction * distance,
            Quat::IDENTITY,
            &shape,
            filter,
            |_, _| {
                hit = true;
                false
            },
        );
        hit
    };
    let steps = (length / 0.39).ceil().max(2.) as usize;
    let mut clear = 0.;
    for i in 1..=steps {
        let mut hit = length * i as f32 / steps as f32;
        if blocked(hit) {
            for _ in 0..5 {
                let mid = (clear + hit) * 0.5;
                if blocked(mid) {
                    hit = mid;
                } else {
                    clear = mid;
                }
            }
            return clear;
        }
        clear = hit;
    }
    length
}
#[allow(clippy::too_many_arguments)]
pub fn follow(
    view: &mut Perspective,
    context: Option<&RapierContext>,
    owner: Entity,
    feet: Vec3,
    eye: f32,
    height: f32,
    yaw: f32,
    pitch: f32,
    lean: f32,
    dt: f32,
) -> Transform {
    if view.first_person {
        let pivot = crate::player::lean::pivot(yaw, lean);
        return Transform::from_translation(feet + pivot * Vec3::Y * eye).with_rotation(
            pivot
                * Quat::from_euler(
                    EulerRot::YXZ,
                    view.head.x.to_radians(),
                    (pitch + view.head.y).clamp(-89., 89.).to_radians(),
                    0.,
                ),
        );
    }
    view.shoulder =
        view.shoulder + (if view.left { -1. } else { 1. } - view.shoulder) * (8. * dt).min(1.);
    view.pivot_height += (pivot(eye, height) - view.pivot_height) * (4. * dt).min(1.);
    let rotation = crate::player::lean::pivot(yaw, lean)
        * Quat::from_euler(
            EulerRot::YXZ,
            (-view.shoulder * 5. + view.orbit.x).to_radians(),
            (pitch + view.orbit.y).clamp(-89., 89.).to_radians(),
            0.,
        );
    let origin = feet + Vec3::Y * view.pivot_height;
    let dir = rotation * offset(view.shoulder);
    let distance = context.map_or(2., |c| sweep(c, origin, dir, 2., owner));
    Transform::from_translation(origin + dir * distance).with_rotation(rotation)
}
pub fn converge(eyes: Vec3, forward: Vec3, target: Vec3, fallback: Vec3) -> Vec3 {
    let delta = target - eyes;
    if delta.dot(forward) <= 0. || delta.length_squared() < 1e-6 {
        fallback
    } else {
        delta.normalize()
    }
}
pub fn aim(
    context: &RapierContext,
    owner: Entity,
    eyes: Vec3,
    camera: &Transform,
    fallback: Vec3,
) -> Vec3 {
    let forward = camera.rotation * Vec3::NEG_Z;
    let start = camera.translation + forward * (eyes - camera.translation).dot(forward).max(0.);
    let filter = QueryFilter::default()
        .exclude_collider(owner)
        .exclude_sensors();
    let length = context
        .cast_ray(start, forward, 512., true, filter)
        .map_or(512., |(_, d)| d);
    converge(eyes, forward, start + forward * length, fallback)
}

/// Source WorldMeshAabb includes the actual visible vehicle parts, so chase
/// distance follows the fleet mesh instead of a guessed generic car size.
#[derive(bevy::ecs::system::SystemParam)]
pub struct VehicleCameras<'w, 's> {
    vehicles: Query<
        'w,
        's,
        (
            &'static crate::vehicle::VehicleBody,
            &'static Transform,
            Option<&'static crate::vehicle::turrets::Mounts>,
        ),
        (
            Without<crate::player::PlayerCamera>,
            Without<crate::player::PlayerBody>,
        ),
    >,
    meshes: Query<
        'w,
        's,
        (
            Entity,
            &'static bevy::camera::primitives::Aabb,
            &'static GlobalTransform,
        ),
    >,
    parents: Query<'w, 's, &'static ChildOf>,
    seated: seated::SeatedEyes<'w, 's>,
}
impl VehicleCameras<'_, '_> {
    /// Camera-only source head anchor. The accepted capsule/body seat stays fixed.
    pub fn seated_eye(
        &mut self,
        seat: Option<crate::vehicle::OccupiedSeat>,
        view: &Perspective,
        yaw: f32,
        dt: f32,
        paused: bool,
    ) -> Option<Transform> {
        let vehicle = seat.and_then(|seat| self.vehicles.get(seat.vehicle).ok());
        self.seated
            .eye(seat, vehicle, view.first_person, yaw, dt, paused)
    }

    fn size(&self, vehicle: Entity) -> f32 {
        let mut min = Vec3::splat(f32::INFINITY);
        let mut max = Vec3::splat(f32::NEG_INFINITY);
        for (entity, aabb, global) in &self.meshes {
            let mut ancestor = entity;
            let mut found = false;
            for _ in 0..24 {
                if ancestor == vehicle {
                    found = true;
                    break;
                }
                let Ok(parent) = self.parents.get(ancestor) else {
                    break;
                };
                ancestor = parent.parent();
            }
            if !found {
                continue;
            }
            for x in [-1., 1.] {
                for y in [-1., 1.] {
                    for z in [-1., 1.] {
                        let p = global.transform_point(
                            Vec3::from(aabb.center)
                                + Vec3::from(aabb.half_extents) * Vec3::new(x, y, z),
                        );
                        min = min.min(p);
                        max = max.max(p);
                    }
                }
            }
        }
        if min.is_finite() {
            (max - min).length()
        } else {
            0.
        }
    }
    pub fn follow(
        &self,
        seat: crate::vehicle::OccupiedSeat,
        view: &Perspective,
        context: Option<&RapierContext>,
    ) -> Option<Transform> {
        let (body, pose, _) = self.vehicles.get(seat.vehicle).ok()?;
        let spec = &crate::vehicle::data::definition(body.type_id)?.spec;
        let size = self.size(seat.vehicle);
        let (target, wanted, up, rotation) = vehicle_pose(*pose, size, spec.plane, spec.heli, view);
        let mut eye = wanted;
        if let Some(context) = context {
            let dir = wanted - target;
            let len = dir.length();
            if len > 0.001 {
                let filter = QueryFilter::default()
                    .groups(CollisionGroups::new(Group::ALL, Group::GROUP_1))
                    .exclude_sensors();
                if let Some((_, distance)) = context.cast_ray(target, dir / len, len, false, filter)
                {
                    eye = target + dir / len * (distance - 0.4_f32.min(len * 0.5));
                }
            }
        }
        Some(if let Some(rotation) = rotation {
            Transform::from_translation(eye).with_rotation(rotation)
        } else {
            Transform::from_translation(eye).looking_at(target, up)
        })
    }
}
fn vehicle_pose(
    pose: Transform,
    size: f32,
    plane: bool,
    heli: bool,
    view: &Perspective,
) -> (Vec3, Vec3, Vec3, Option<Quat>) {
    if plane || heli {
        let distance = if plane {
            (size * 0.62).clamp(6.5, 20.)
        } else {
            (size * 0.95).clamp(5.5, 34.)
        } * view.drive_zoom;
        if plane {
            let forward = pose.rotation * Vec3::NEG_Z;
            let air_up = pose.rotation * Vec3::Y;
            let mut up = (Vec3::Y - forward * forward.dot(Vec3::Y))
                .try_normalize()
                .unwrap_or(air_up);
            let vertical = forward.y.abs();
            if vertical > 0.94 {
                up = up
                    .lerp(air_up, ((vertical - 0.94) / (0.999 - 0.94)).clamp(0., 1.))
                    .try_normalize()
                    .unwrap_or(air_up);
            }
            let z = -forward;
            let x = up.cross(z).normalize();
            let basis = Quat::from_mat3(&Mat3::from_cols(x, z.cross(x), z));
            let look = basis
                * Quat::from_euler(
                    EulerRot::YXZ,
                    view.fly.x.to_radians(),
                    view.fly.y.to_radians(),
                    0.,
                );
            let target = pose.translation + up * 0.4;
            let eye = pose.translation
                + look * Vec3::Z * (distance * 0.9)
                + look * Vec3::Y * (distance * 0.22 + size * 0.04);
            (target, eye, up, None)
        } else {
            let look = pose.rotation
                * Quat::from_euler(
                    EulerRot::YXZ,
                    view.fly.x.to_radians(),
                    view.fly.y.to_radians(),
                    0.,
                );
            let eye = pose.translation
                + look * Vec3::Z * (distance * 0.86)
                + look * Vec3::Y * (distance * 0.16 + size * 0.06);
            (
                pose.translation + pose.rotation * Vec3::Y * 0.5,
                eye,
                pose.rotation * Vec3::Y,
                Some(look),
            )
        }
    } else {
        let forward = (pose.rotation * Vec3::NEG_Z)
            .with_y(0.)
            .try_normalize()
            .unwrap_or(Vec3::NEG_Z);
        let distance = (size * 0.85).clamp(4.5, 34.) * view.drive_zoom;
        let pitch = view.drive.y.to_radians();
        let dir = Quat::from_rotation_y(view.drive.x.to_radians()) * (-forward);
        let target = pose.translation + Vec3::Y * (size * 0.15);
        let eye = pose.translation
            + dir * (distance * pitch.cos())
            + Vec3::Y * (distance * pitch.sin() + size * 0.22);
        (target, eye, Vec3::Y, None)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn source_aircraft_chase_ignores_plane_roll_but_keeps_helicopter_roll_and_pitch() {
        let view = Perspective {
            first_person: false,
            ..default()
        };
        let pitch = Quat::from_rotation_x(-45_f32.to_radians());
        let a = vehicle_pose(Transform::from_rotation(pitch), 20., true, false, &view);
        let b = vehicle_pose(
            Transform::from_rotation(pitch * Quat::from_rotation_z(80_f32.to_radians())),
            20.,
            true,
            false,
            &view,
        );
        assert!(
            (a.1 - b.1).length() < 1e-4,
            "plane tail camera follows true pitch, without roll"
        );
        let roll = Quat::from_euler(EulerRot::YXZ, 0.3, -0.2, 0.7);
        let heli = vehicle_pose(Transform::from_rotation(roll), 12., false, true, &view);
        assert_eq!(
            heli.3,
            Some(roll),
            "helicopter camera is the airframe basis"
        );
        let car = vehicle_pose(Transform::IDENTITY, 6., false, false, &view);
        assert!(
            (car.1
                - Vec3::new(
                    0.,
                    5.1 * 15_f32.to_radians().sin() + 1.32,
                    5.1 * 15_f32.to_radians().cos()
                ))
            .length()
                < 1e-5
        );
    }
}
