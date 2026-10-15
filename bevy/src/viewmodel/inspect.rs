//! Original per-weapon F gesture. Missing clips are an intentional no-op.
use crate::rig::RigPlayback;
use bevy::prelude::*;

pub(super) fn reset_after_session(
    epoch: Option<Res<crate::player::WorldSessionEpoch>>,
    mut previous: Local<u64>,
    mut state: ResMut<super::ViewmodelState>,
    mut status: ResMut<super::ViewmodelStatus>,
    mut rigs: Query<&mut RigPlayback>,
) {
    let Some(epoch) = epoch else { return };
    if epoch.0 == *previous {
        return;
    }
    *previous = epoch.0;
    if state.inspect_time.take().is_some() {
        if let Some(mut rig) = state.arms.and_then(|entity| rigs.get_mut(entity).ok()) {
            ready_hold(&mut rig, &state.hold_clip);
        }
    }
    status.inspecting = false;
    status.cancel_inspect = false;
}

pub(super) fn laser_ray(rail: Mat4) -> (Vec3, Vec3) {
    // Extracted weapon barrels run along model +Y, not the camera's -Z.
    (
        rail.transform_point3(Vec3::ZERO),
        rail.transform_vector3(Vec3::Y).normalize_or_zero(),
    )
}

pub(super) fn clip(rig: &RigPlayback, asset: &str, gun: bool, melee: bool) -> Option<String> {
    if !gun && !melee {
        return None;
    }
    let own = asset
        .chars()
        .next()
        .map(|first| first.to_uppercase().to_string() + &asset[first.len_utf8()..] + "_Inspect")?;
    let exists = |name: &str| {
        rig.data
            .anims
            .get(name)
            .is_some_and(|clip| clip.length > 0.)
    };
    if exists(&own) {
        Some(own)
    } else if gun && asset == "sks" && exists("Zubeknakov_Inspect") {
        Some("Zubeknakov_Inspect".into())
    } else {
        None
    }
}

pub(super) fn ready_hold(rig: &mut RigPlayback, hold: &str) {
    if rig.play(hold, false) {
        rig.time = rig.data.anims[hold].length;
        rig.speed = 1.;
    }
}

#[cfg(test)]
mod tests;
