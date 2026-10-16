//! Wire.SetPoints: local +Y cylinder rotation before local length scaling.
use bevy::prelude::*;

pub(crate) fn pose(a: Vec3, b: Vec3) -> Option<Transform> {
    let delta = b - a;
    let length = delta.length();
    if !a.is_finite() || !b.is_finite() || !length.is_finite() || length < 1e-4 {
        return None;
    }
    let direction = delta / length;
    let dot = Vec3::Y.dot(direction);
    let rotation = if dot > 0.9999 {
        Quat::IDENTITY
    } else if dot < -0.9999 {
        Quat::from_rotation_x(std::f32::consts::PI)
    } else {
        Quat::from_axis_angle(
            Vec3::Y.cross(direction).normalize(),
            dot.clamp(-1., 1.).acos(),
        )
    };
    Some(Transform {
        translation: (a + b) * 0.5,
        rotation,
        scale: Vec3::new(1., length, 1.),
    })
}
