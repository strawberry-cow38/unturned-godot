//! Original measured Timber clip begins early enough to finish at landing.
use super::*;
use crate::{
    player::{PlayerCamera, WorldSessionEpoch, simulation_paused},
    settings::Settings,
    ui::{UiModel, UiState},
};
use bevy::audio::{AudioSinkPlayback, SpatialScale, Volume};
#[derive(Component)]
pub(super) struct Voice {
    pub(super) epoch: u64,
    pub(super) elapsed: f32,
    pub(super) duration: f32,
    db: f32,
}
pub(super) fn play(
    world: &mut World,
    clip: Handle<AudioSource>,
    duration: f32,
    at: Vec3,
    epoch: u64,
) {
    let (_, speed) = super::fall::predict();
    let db = -6. + 10. * (speed / 1.094).min(1.);
    let pitch = 0.94
        + 0.12
            * crate::particles::random(
                &mut (epoch
                    .wrapping_add(at.x.to_bits() as u64)
                    .wrapping_add(at.z.to_bits() as u64)),
            );
    world.spawn((
        Name::new("Source timber fall audio"),
        AudioPlayer::new(clip),
        PlaybackSettings::DESPAWN
            .with_spatial(true)
            .with_spatial_scale(SpatialScale::new(0.5 / 91.))
            .with_volume(Volume::SILENT)
            .with_speed(pitch),
        Transform::from_translation(at),
        Voice {
            epoch,
            elapsed: 0.,
            duration: duration / pitch + 0.1,
            db,
        },
    ));
}
pub(super) fn update(
    mut commands: Commands,
    time: Res<Time>,
    epoch: Res<WorldSessionEpoch>,
    ui: Res<UiState>,
    model: Res<UiModel>,
    settings: Res<Settings>,
    listener: Query<&Transform, With<PlayerCamera>>,
    mut voices: Query<(
        Entity,
        &mut Voice,
        &Transform,
        &mut PlaybackSettings,
        Option<&mut SpatialAudioSink>,
    )>,
) {
    let paused = simulation_paused(&ui);
    for (entity, mut voice, pose, mut playback, sink) in &mut voices {
        if !model.connected || voice.epoch != epoch.0 {
            commands.entity(entity).try_despawn();
            continue;
        }
        if !paused {
            voice.elapsed += time.delta_secs();
        }
        if voice.elapsed >= voice.duration {
            commands.entity(entity).try_despawn();
            continue;
        }
        let distance = listener
            .single()
            .ok()
            .map_or(90., |p| p.translation.distance(pose.translation));
        playback.volume = Volume::Linear(
            crate::sound::distance_gain(voice.db, 8., 90., distance) * settings.master_volume,
        );
        playback.paused = paused;
        if let Some(mut sink) = sink {
            sink.set_volume(playback.volume);
            if paused {
                sink.pause();
            } else {
                sink.play();
            }
        }
    }
}
