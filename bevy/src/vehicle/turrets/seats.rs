//! Vehicle.SeatBodyLocal/SeatBodyBasis/SeatEyeOverride. The gunner's position
//! belongs to the yaw frame; the optic view direction remains hull-relative.
use super::*;

impl Mounts {
    fn gunner_frame(&self, definition: &VehicleDefinition) -> Option<Transform> {
        if !self.hatches.as_ref()?.has_top() {
            return None;
        }
        let cannon = definition.spec.turrets.as_ref()?.first()?;
        let yaw = self.angles.first()?.x;
        Some(
            Transform::from_translation(Vec3::from_array(cannon.pivot))
                .with_rotation(Quat::from_rotation_y(yaw.to_radians())),
        )
    }

    pub(in crate::vehicle) fn seat_body(
        &self,
        definition: &VehicleDefinition,
        seat: usize,
    ) -> Option<Transform> {
        if seat != 1 {
            return None;
        }
        let frame = self.gunner_frame(definition)?;
        let local = Vec3::from_array(tank::DATA.gunner_seat)
            + Vec3::Y * (tank::DATA.gunner_rise * self.gunner_rise);
        Some(frame.with_translation(frame.transform_point(local)))
    }

    pub(crate) fn seat_eye(&self, definition: &VehicleDefinition, seat: usize) -> Option<Vec3> {
        match seat {
            0 if self.hatches.as_ref()?.has_driver() => {
                Some(Vec3::from_array(tank::DATA.periscope_eye))
            }
            1 => {
                let local = if self.head_out {
                    tank::DATA.cupola_eye
                } else {
                    tank::DATA.sight_eye
                };
                Some(
                    self.gunner_frame(definition)?
                        .transform_point(Vec3::from_array(local)),
                )
            }
            _ => None,
        }
    }
}
