//! Source TorsoPoseModifier: character-axis look pitch split over spine/skull.
use crate::{avatar::LocalAvatar, rig::RigPlayback};
use bevy::prelude::*;
#[derive(Component)]
pub(super) struct TorsoPose {
    pub pitch: f32,
    pub lean: f32,
    pub spine_scale: f32,
}
impl Default for TorsoPose {
    fn default() -> Self {
        Self {
            pitch: 0.,
            lean: 0.,
            spine_scale: 1.,
        }
    }
}
pub(super) fn apply(
    rigs: Query<(&RigPlayback, &TorsoPose), With<LocalAvatar>>,
    mut transforms: Query<&mut Transform>,
) {
    for (rig, pose) in &rigs {
        if rig.paused {
            continue;
        }
        let mut bases = Vec::<Quat>::with_capacity(rig.data.bones.len());
        for bone in &rig.data.bones {
            let parent = if bone.parent < 0 {
                Quat::IDENTITY
            } else {
                bases[bone.parent as usize]
            };
            let Some(entity) = rig.bones.get(&bone.name) else {
                bases.push(parent);
                continue;
            };
            let Ok(mut t) = transforms.get_mut(*entity) else {
                bases.push(parent);
                continue;
            };
            if bone.name == "Spine" {
                let lean =
                    Quat::from_axis_angle(parent.inverse() * Vec3::Z, pose.lean.to_radians());
                let pitch = Quat::from_axis_angle(
                    parent.inverse() * Vec3::X,
                    (pose.pitch * 0.5 * pose.spine_scale).to_radians(),
                );
                t.rotation = pitch * lean * t.rotation;
            } else if bone.name == "Skull" {
                t.rotation = Quat::from_axis_angle(
                    parent.inverse() * Vec3::X,
                    (pose.pitch * 0.5).to_radians(),
                ) * t.rotation;
            }
            bases.push(parent * t.rotation);
        }
    }
}
