//! Completed source camera for the host station query. Store absolute
//! coordinates so a world rebase between camera and UI updates is harmless.
use super::WorldSessionEpoch;
use crate::world::origin::WorldOrigin;
use bevy::{math::DVec3, prelude::*};

#[derive(Resource, Default)]
pub(crate) struct CraftingEye {
    absolute: Option<DVec3>,
    epoch: u64,
}
impl CraftingEye {
    pub(super) fn set(&mut self, local: Vec3, origin: &WorldOrigin, epoch: &WorldSessionEpoch) {
        self.absolute = local.is_finite().then(|| origin.to_absolute(local));
        self.epoch = epoch.0;
    }
    pub(crate) fn request(&self, origin: &WorldOrigin, epoch: &WorldSessionEpoch) -> Option<[f32; 3]> {
        (self.epoch == epoch.0)
            .then_some(self.absolute)
            .flatten()
            .map(|absolute| origin.local(absolute).to_array())
    }
}

/// Bridge actions whose host admission rechecks this completed camera. The
/// bridge refuses these without an eye, so every sender must be listed here.
pub(crate) fn needs_eye(action: &str) -> bool {
    matches!(
        action,
        "craft" | "nearbyInventory" | "nearbyOperation" | "nearbyMagazineTransfer"
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn eye_survives_rebase_but_never_crosses_a_session() {
        let mut eye = CraftingEye::default();
        let epoch = WorldSessionEpoch(4);
        let mut origin = WorldOrigin::default();
        assert!(eye.request(&origin, &epoch).is_none());
        eye.set(Vec3::new(1025., 1.75, -7.), &origin, &epoch);
        origin.offset.x = 1024.;
        assert_eq!(eye.request(&origin, &epoch), Some([1., 1.75, -7.]));
        assert!(eye.request(&origin, &WorldSessionEpoch(5)).is_none());
        eye.set(Vec3::NAN, &origin, &epoch);
        assert!(eye.request(&origin, &epoch).is_none());
    }
}
