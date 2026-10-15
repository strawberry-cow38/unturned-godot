//! PlayerController.SeatedEyeLocal and DriverPeekOffset. The source reads the
//! previously completed pose before UpdateBody, then uses the current hull.
use crate::{
    avatar::{AvatarState, LocalAvatar},
    player::WorldSessionEpoch,
    rig::RigPlayback,
    vehicle::{OccupiedSeat, VehicleBody, data, turrets::Mounts},
};
use bevy::{ecs::system::SystemParam, prelude::*};

#[derive(Default)]
struct State {
    epoch: u64,
    vehicle: Option<u32>,
    peek: f32,
}
impl State {
    fn step(
        &mut self,
        epoch: u64,
        seat: Option<OccupiedSeat>,
        first_person: bool,
        yaw: f32,
        dt: f32,
        paused: bool,
    ) -> f32 {
        let vehicle = seat.map(|seat| seat.net_id);
        if self.epoch != epoch || self.vehicle != vehicle {
            // EnterVehicle resets the peek; TrySwitchSeat deliberately does not.
            *self = Self {
                epoch,
                vehicle,
                peek: 0.,
            };
        }
        if !paused {
            let target = if first_person && seat.is_some_and(|seat| seat.driver) {
                let beyond = (yaw.abs() - 40.).max(0.) * yaw.signum();
                -beyond / if yaw > 0. { 240. } else { 360. }
            } else {
                0.
            };
            self.peek += (target - self.peek) * (4. * dt).min(1.);
        }
        if self.peek.abs() < 0.001 {
            0.
        } else {
            self.peek
        }
    }
}

#[derive(SystemParam)]
pub(super) struct SeatedEyes<'w, 's> {
    avatar: Query<'w, 's, (&'static RigPlayback, &'static GlobalTransform), With<LocalAvatar>>,
    globals: Query<'w, 's, &'static GlobalTransform>,
    avatar_state: Option<Res<'w, AvatarState>>,
    epoch: Option<Res<'w, WorldSessionEpoch>>,
    state: Local<'s, State>,
}
impl SeatedEyes<'_, '_> {
    fn skull(&self) -> Option<Vec3> {
        let epoch = self.epoch.as_ref().map_or(0, |epoch| epoch.0);
        if self
            .avatar_state
            .as_ref()
            .is_some_and(|avatar| avatar.epoch != epoch)
        {
            return None;
        }
        let (rig, root) = self.avatar.single().ok()?;
        let head = self.globals.get(*rig.bones.get("Skull")?).ok()?;
        // Both globals come from the same completed propagation. Taking their
        // difference removes the previous hull motion and any world rebase.
        let local = root.affine().inverse().transform_point3(head.translation());
        local.is_finite().then_some(local)
    }
    pub(super) fn eye(
        &mut self,
        seat: Option<OccupiedSeat>,
        vehicle: Option<(&VehicleBody, &Transform, Option<&Mounts>)>,
        first_person: bool,
        yaw: f32,
        dt: f32,
        paused: bool,
    ) -> Option<Transform> {
        let epoch = self.epoch.as_ref().map_or(0, |epoch| epoch.0);
        let peek = self.state.step(epoch, seat, first_person, yaw, dt, paused);
        let seat = seat?;
        let (body, hull, mounts) = vehicle?;
        let definition = data::definition(body.type_id)?;
        if mounts
            .and_then(|mounts| mounts.seat_eye(definition, seat.seat as usize))
            .is_some()
        {
            // Original optics replace the entire skull/peek eye, even though
            // the driver's private peek state still advances behind the mask.
            return Some(seat.eye_pose);
        }
        let inverse = hull.compute_affine().inverse();
        let body_local = inverse.transform_point3(seat.body_pose.translation);
        let fallback = inverse.transform_point3(seat.eye_pose.translation);
        let local = skull_eye(body_local, fallback, self.skull()) + Vec3::X * peek;
        Some(seat.eye_pose.with_translation(hull.transform_point(local)))
    }
}

fn skull_eye(body: Vec3, fallback: Vec3, skull: Option<Vec3>) -> Vec3 {
    match skull {
        None => fallback,
        Some(head) if head.length_squared() < 0.01 => fallback + Vec3::Y * 0.15,
        // These are rig/vehicle-axis offsets, not a point rotated by Skull's
        // look pitch. Source GetBoneGlobalPose(...).Origin reads translation.
        Some(head) => body + head + Vec3::new(0., 0.16, -0.10) + Vec3::Y * 0.15,
    }
}

#[cfg(test)]
mod tests;
