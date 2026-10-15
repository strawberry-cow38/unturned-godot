//! Source SoundBus alerts are independent of audio volume and listener distance.
use super::{VehicleBody, data};
use crate::{
    bridge::CoreBridge,
    player::{GameSnapshot, simulation_paused},
    ui::UiState,
};
use bevy::prelude::*;
use bevy_rapier3d::prelude::Velocity;
use serde_json::json;
use std::collections::HashMap;

#[derive(Default)]
struct Source {
    timer: f32,
}
impl Source {
    fn advance(&mut self, dt: f32, running: bool, speed_fraction: f32) -> Vec<f32> {
        let mut radii = Vec::new();
        self.timer -= dt;
        if running && self.timer <= 0. {
            self.timer = 0.4;
            let radius = 48. * speed_fraction.clamp(0., 1.);
            if radius > 2. {
                radii.push(radius);
            }
        }
        radii
    }
}
#[derive(Resource, Default)]
pub(super) struct Hearing(HashMap<Entity, Source>);

pub(super) fn publish(
    bridge: Res<CoreBridge>,
    snapshot: Res<GameSnapshot>,
    ui: Res<UiState>,
    time: Res<Time<Fixed>>,
    mut hearing: ResMut<Hearing>,
    bodies: Query<(Entity, &VehicleBody, &Transform, &Velocity)>,
) {
    if simulation_paused(&ui)
        || snapshot.0["connected"] != true
        || !crate::network::owns_world(&snapshot.0)
    {
        return;
    }
    hearing.0.retain(|e, _| bodies.get(*e).is_ok());
    for (entity, body, pose, velocity) in &bodies {
        let Some(definition) = data::definition(body.type_id) else {
            continue;
        };
        let maximum = definition.spec.speed_max();
        // Source ForwardSpeedPct uses signed forward speed: backing up is quiet.
        let fraction = if maximum > 0. {
            velocity.linear.dot(pose.rotation * Vec3::NEG_Z) / maximum
        } else {
            0.
        };
        let source = hearing.0.entry(entity).or_default();
        for radius in source.advance(time.delta_secs(), body.flags & 1 != 0, fraction) {
            if let Err(error) = bridge.request(
                "worldZombieNoise",
                json!({
                    "position":pose.translation.to_array(), "radius":radius
                }),
            ) {
                warn!("Vehicle hearing publication: {error}");
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn only_running_forward_noise_is_published_horns_are_authority_events() {
        let mut source = Source::default();
        assert!(source.advance(0.02, true, -0.5).is_empty());
        // The reverse sample still resets the original timer; accelerating
        // forward cannot generate a fresh alert until that period has elapsed.
        for _ in 0..19 {
            assert!(source.advance(0.02, true, 0.5).is_empty());
        }
        assert_eq!(source.advance(0.02, true, 0.5), vec![24.]);
        assert!(source.advance(0.02, true, 0.5).is_empty());
        assert!(source.advance(0.02, false, 1.).is_empty());
        assert!(source.advance(1., false, 1.).is_empty());
        assert_eq!(source.advance(0.02, true, 2.), vec![48.]);
    }
}
