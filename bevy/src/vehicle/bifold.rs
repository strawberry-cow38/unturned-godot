//! Original bus bi-fold geometry and accepted phase presentation.
use crate::{
    player::GameSnapshot,
    vehicle::{VehicleBody, data::VehicleSpec},
};
use bevy::prelude::*;
use bevy_rapier3d::prelude::*;
mod audio;
mod build;
mod geometry;
pub(super) mod input;
pub(super) use build::{build, pane};

#[derive(Component)]
pub(super) struct Built {
    a: Entity,
    b: Option<Entity>,
    offset_a: Vec3,
    offset_b: Vec3,
    degrees: f32,
    pub phase: f32,
    pub open: bool,
}
impl Built {
    pub fn pane(&self, label: &str, spec: &VehicleSpec) -> Option<(Entity, Transform)> {
        if spec.door_glass_a.as_deref() == Some(label) {
            Some((self.a, Transform::from_translation(self.offset_a)))
        } else if spec.door_glass_b.as_deref() == Some(label) {
            self.b
                .map(|b| (b, Transform::from_translation(self.offset_b)))
        } else {
            None
        }
    }
}
/// The source DoorHit uses a full leaf box, not the visual hollow panel.
#[derive(Component)]
pub(super) struct Leaf {
    pub center: Vec3,
    pub half: Vec3,
    pub contact_mesh: Handle<Mesh>,
}
#[derive(Component)]
pub(super) struct DoorPane;
pub(super) fn groups(intact: bool) -> CollisionGroups {
    CollisionGroups::new(
        if intact { Group::GROUP_4 } else { Group::NONE },
        Group::GROUP_4,
    )
}
pub(super) struct BiFoldPlugin;
impl Plugin for BiFoldPlugin {
    fn build(&self, app: &mut App) {
        app.add_systems(Update, adopt.after(super::VehicleUpdate))
            .add_systems(
                Update,
                input::input
                    .after(crate::ui::UiUpdate)
                    .before(crate::player::PlayerUpdate),
            )
            .add_systems(
                Update,
                audio::events
                    .after(crate::entities::CoreEventsReady)
                    .after(super::VehicleUpdate),
            );
    }
}
fn adopt(
    snapshot: Res<GameSnapshot>,
    mut bodies: Query<(&VehicleBody, &mut Built)>,
    mut hinges: Query<&mut Transform>,
) {
    let Some(rows) = snapshot.0["vehicles"].as_array() else {
        return;
    };
    for (body, mut built) in &mut bodies {
        let Some(row) = rows
            .iter()
            .find(|r| r["netId"].as_u64() == Some(body.net_id as u64))
        else {
            continue;
        };
        let Some(phase) = row["doorFold"].as_f64().filter(|v| v.is_finite()) else {
            continue;
        };
        built.phase = (phase as f32).clamp(0., 1.);
        built.open = row["doorOpen"].as_bool().unwrap_or(false);
        let p = built.phase;
        let eased = if p < 0.5 {
            2. * p * p
        } else {
            1. - (-2. * p + 2.).powi(2) * 0.5
        };
        if let Ok(mut pose) = hinges.get_mut(built.a) {
            pose.rotation = Quat::from_rotation_y((-built.degrees * eased).to_radians());
        }
        if let Some(b) = built.b
            && let Ok(mut pose) = hinges.get_mut(b)
        {
            pose.rotation = Quat::from_rotation_y((2. * built.degrees * eased).to_radians());
        }
    }
}

#[cfg(test)]
mod tests;
