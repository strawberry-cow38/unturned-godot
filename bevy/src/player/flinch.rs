//! PlayerLook's local angular kick. The ordinary camera is rebuilt first; this
//! layer applies one spring value, never feeds a rendered kick back into aim.
use super::{PlayerBody, PlayerCamera, PlayerCameraUpdate, PlayerInput, WorldSessionEpoch};
use crate::{
    avatar::{Perspective, camera},
    entities::{CoreEvent, ItemCatalog},
    ui::{UiModel, UiState},
};
use bevy::prelude::*;
use bevy_rapier3d::prelude::*;

#[derive(Message, Clone, Copy, Debug)]
pub(crate) struct ExplosionFlinch {
    pub point: Vec3,
    pub radius: f32,
    /// Source magnitude is in degrees (cannon 4, ordinary blast 30).
    pub magnitude: f32,
}
#[derive(SystemSet, Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub(crate) struct FlinchUpdate;
pub(super) struct FlinchPlugin;
impl Plugin for FlinchPlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<Flinch>()
            .add_message::<ExplosionFlinch>()
            .add_systems(
                Update,
                update
                    .in_set(FlinchUpdate)
                    .after(PlayerCameraUpdate)
                    .after(crate::entities::CoreEventsReady)
                    .before(crate::entities::CollectInteractions),
            );
    }
}
#[derive(Resource)]
pub(crate) struct Flinch {
    rotation: Quat,
    epoch: u64,
    last_event: u64,
    was_alive: bool,
}
impl Default for Flinch {
    fn default() -> Self {
        Self {
            rotation: Quat::IDENTITY,
            epoch: 0,
            last_event: 0,
            was_alive: true,
        }
    }
}
impl Flinch {
    /// Current presentation kick in degrees; useful for rendered event evidence.
    pub(crate) fn angle_degrees(&self) -> f32 {
        self.rotation.angle_between(Quat::IDENTITY).to_degrees()
    }
    fn kick(&mut self, camera: Quat, direction: Vec3, degrees: f32) {
        if !degrees.is_finite() || !camera.is_finite() || !direction.is_finite() {
            return;
        }
        let Some(world_axis) = Vec3::Y.cross(direction).try_normalize() else {
            return;
        };
        let Some(local_axis) = (camera.inverse() * world_axis).try_normalize() else {
            return;
        };
        self.rotation =
            (self.rotation * Quat::from_axis_angle(local_axis, degrees.to_radians())).normalize();
    }
    /// PlayerController.FlinchFromExplosion: quadratic radial falloff, strict
    /// open radius, and the camera-local axis perpendicular to the blast.
    fn explosion(&mut self, camera: &Transform, blast: ExplosionFlinch) {
        let relative = camera.translation - blast.point;
        let distance = relative.length();
        if !distance.is_finite()
            || !blast.radius.is_finite()
            || distance <= 0.
            || distance >= blast.radius
        {
            return;
        }
        let degrees = blast.magnitude * (1. - (distance / blast.radius).powi(2));
        if degrees.abs() > 0.01 {
            self.kick(camera.rotation, relative / distance, degrees);
        }
    }
    fn damage(&mut self, camera: Quat, feet: Vec3, source: Vec3, damage: f32) {
        let relative = (feet - source).with_y(0.);
        if relative.length_squared() > 0.0001 && damage.is_finite() && damage > 0. {
            self.kick(camera, relative.normalize(), damage.min(25.) * 0.5);
        }
    }
    fn decay(&mut self, dt: f32) {
        if !self.rotation.is_finite() || self.rotation.length_squared() < 1e-6 {
            self.rotation = Quat::IDENTITY;
        }
        if dt.is_finite() && dt >= 0. {
            // Virtual time caps normal delta at .25s. Clamp exceptional custom
            // time sources rather than overshooting past a recovered camera.
            self.rotation = self
                .rotation
                .normalize()
                .slerp(Quat::IDENTITY, (4. * dt).min(1.));
        }
    }
}
fn event_blast(event: &CoreEvent, catalog: &ItemCatalog) -> Option<ExplosionFlinch> {
    if let Some((point, radius)) = crate::projectiles::effects::accepted_blast(event, catalog) {
        return Some(ExplosionFlinch {
            point,
            radius: (radius * 2.).max(12.),
            magnitude: 30.,
        });
    }
    let (radius, magnitude) = match event.kind.as_str() {
        "vehicleExploded" => (32., 45.),
        "deployableExploded" => (18., 28.),
        _ => return None,
    };
    Some(ExplosionFlinch {
        point: super::vector(&event.data["position"])?,
        radius,
        magnitude,
    })
}
fn rotated(base: Quat, parent: Quat, spring: Quat) -> Quat {
    (parent * spring * parent.inverse() * base).normalize()
}
#[allow(clippy::too_many_arguments)]
fn update(
    mut state: ResMut<Flinch>,
    epoch: Res<WorldSessionEpoch>,
    ui: Res<UiState>,
    model: Res<UiModel>,
    time: Res<Time>,
    catalog: Res<ItemCatalog>,
    recoil: Option<Res<crate::recoil::Recoil>>,
    mut events: MessageReader<CoreEvent>,
    mut explosions: MessageReader<ExplosionFlinch>,
    input: Res<PlayerInput>,
    view: Res<Perspective>,
    scene: (
        Option<Res<crate::vehicle::Occupancy>>,
        Option<Res<crate::world::interactables::PropOccupancy>>,
        Option<Res<super::lean::LeanState>>,
        Option<Res<crate::world::WorldConfig>>,
    ),
    body: Query<(Entity, &Transform), (With<PlayerBody>, Without<PlayerCamera>)>,
    context: ReadRapierContext,
    mut camera: Query<&mut Transform, (With<PlayerCamera>, Without<PlayerBody>)>,
) {
    let changed = state.epoch != epoch.0;
    if changed || !model.connected || !model.alive || !state.was_alive {
        state.rotation = Quat::IDENTITY;
        if changed {
            state.last_event = 0;
        }
    }
    state.epoch = epoch.0;
    state.was_alive = model.alive;
    let Ok((owner, body)) = body.single() else {
        events.clear();
        explosions.clear();
        return;
    };
    let Ok(mut pose) = camera.single_mut() else {
        events.clear();
        explosions.clear();
        return;
    };
    let paused = super::simulation_paused(&ui);
    let accept = model.connected && model.alive && !changed && !paused;
    for event in events.read() {
        if event.sequence <= state.last_event {
            continue;
        }
        state.last_event = event.sequence;
        if !accept {
            continue;
        }
        if let Some(blast) = event_blast(event, &catalog) {
            state.explosion(&pose, blast);
        }
        if event.kind == "hurt" && event.data["hasSource"].as_bool() == Some(true) {
            if let (Some(source), Some(damage)) = (
                super::vector(&event.data["sourcePos"]),
                event.data["damage"].as_f64(),
            ) {
                state.damage(pose.rotation, body.translation, source, damage as f32);
            }
        }
    }
    for blast in explosions.read() {
        if accept {
            state.explosion(&pose, *blast);
        }
    }
    if accept && !view.first_person {
        let pitch = recoil.as_ref().map_or(0., |r| r.shot_pitch());
        if pitch.is_finite() {
            state.rotation =
                (state.rotation * Quat::from_rotation_x((pitch * 0.35).to_radians())).normalize();
        }
    }
    if !paused {
        state.decay(time.delta_secs());
    }
    if !model.connected
        || !model.alive
        || ui.menu_camera()
        || scene.3.as_ref().is_some_and(|c| c.fixed_camera.is_some())
    {
        return;
    }
    let seat = scene.0.as_ref().and_then(|o| o.seat);
    let prop = scene.1.as_ref().and_then(|o| o.seat);
    let parent = if view.first_person {
        seat.map(|s| s.eye_pose.rotation)
            .or_else(|| prop.map(|s| s.eye_pose.rotation))
            .unwrap_or_else(|| {
                super::lean::pivot(input.yaw(), scene.2.as_ref().map_or(0., |l| l.angle))
            })
    } else if seat.is_some() {
        // Original seated camera overwrote the cannon spring entirely. Retain
        // its requested visible kick in the orbit camera's own frame here.
        pose.rotation
    } else {
        super::lean::pivot(
            input.world_yaw(),
            if prop.is_some() {
                0.
            } else {
                scene.2.as_ref().map_or(0., |l| l.angle)
            },
        )
    };
    pose.rotation = rotated(pose.rotation, parent, state.rotation);
    if !view.first_person && seat.is_none() {
        let origin = prop.map_or(body.translation, |s| s.body_pose.translation)
            + Vec3::Y * view.pivot_height;
        let direction = pose.rotation * camera::offset(view.shoulder);
        let length = context
            .single()
            .ok()
            .as_ref()
            .map_or(2., |c| camera::sweep(c, origin, direction, 2., owner));
        pose.translation = origin + direction * length;
    }
}
#[cfg(test)]
mod tests;
