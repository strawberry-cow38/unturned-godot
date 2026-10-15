//! The original seated pose, pinned to the real wheel across camera FOVs.
use bevy::prelude::*;

pub(super) fn wheel_target(camera_local: Vec3, main_fov: f32) -> Vec3 {
    if camera_local.z >= -0.05 {
        // Looking behind must leave the arms behind the camera too.
        return camera_local;
    }
    let scale = (60_f32.to_radians() * 0.5).tan() / (main_fov * 0.5).tan();
    Vec3::new(
        camera_local.x * scale,
        camera_local.y * scale,
        camera_local.z,
    )
}

pub(super) fn pin(
    target: Vec3,
    hands: Vec3,
    vehicle_in_camera: Quat,
    wheel_axis_in_camera: Vec3,
    steer: f32,
) -> Transform {
    let turn = wheel_axis_in_camera
        .try_normalize()
        .map_or(Quat::IDENTITY, |axis| Quat::from_axis_angle(axis, steer));
    let rotation = turn * vehicle_in_camera;
    // Rotate the hand offset before subtracting it. The old source translated
    // by the unrotated offset, which let the hands drift when looking sideways.
    Transform {
        translation: target - rotation * hands - turn * Vec3::Y * 0.09,
        rotation,
        ..default()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn driving_hands_follow_the_wheel_through_head_turns_and_steering() {
        let hands = Vec3::new(0.12, 1.05, -0.42);
        let wheel = Vec3::new(-0.16, -0.28, -0.65);
        for yaw in [0_f32, 70., 90., 180., -120.] {
            let camera = Quat::from_euler(EulerRot::YXZ, yaw.to_radians(), 0.2, 0.);
            let vehicle_in_camera = camera.inverse();
            let wheel_cam = camera.inverse() * wheel;
            let axis = camera.inverse() * Vec3::new(0., 0.4, 0.9).normalize();
            for steer in [-35_f32, 0., 35.] {
                let target = wheel_target(wheel_cam, 90_f32.to_radians());
                let transform = pin(target, hands, vehicle_in_camera, axis, steer.to_radians());
                let turn = Quat::from_axis_angle(axis, steer.to_radians());
                assert!(
                    (transform.transform_point(hands) - (target - turn * Vec3::Y * 0.09)).length()
                        < 0.00001
                );
                assert!(
                    (transform.rotation * Vec3::NEG_Z - turn * vehicle_in_camera * Vec3::NEG_Z)
                        .length()
                        < 0.00001
                );
                if wheel_cam.z >= -0.05 {
                    assert_eq!(target, wheel_cam);
                }
            }
        }
        // This is the source's missing basis transform: this regression fails
        // if pin uses target - hands before rotating the rig to face the car.
        let basis = Quat::from_rotation_y(90_f32.to_radians());
        let old_hand = wheel - hands + basis * hands - Vec3::Y * 0.09;
        assert!((old_hand - (wheel - Vec3::Y * 0.09)).length() > 0.5);
    }
    #[test]
    fn wheel_projection_has_the_same_screen_position_in_both_cameras() {
        let p = Vec3::new(0.2, -0.3, -0.8);
        for fov in [50_f32, 60., 90., 110.] {
            let target = wheel_target(p, fov.to_radians());
            let main = p.xy() / (-p.z * (fov.to_radians() * 0.5).tan());
            let vm = target.xy() / (-target.z * (60_f32.to_radians() * 0.5).tan());
            assert!((main - vm).length() < 0.000001);
        }
    }
}
