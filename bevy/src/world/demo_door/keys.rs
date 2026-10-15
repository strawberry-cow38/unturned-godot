//! PlayerController.RequestSetDoorLocked's local keyring cue. This is cosmetic:
//! a queued intent plays it; accepted server state alone changes the lock.
use super::*;
use bevy::audio::{AudioSinkPlayback, Decodable, Source, SpatialScale, Volume};
#[derive(Resource, Default)]
pub(super) struct Bank {
    root: std::path::PathBuf,
    clips: crate::sound::Bank,
    seed: u64,
}
#[derive(Component)]
pub(super) struct Voice {
    epoch: u64,
    elapsed: f32,
    duration: Option<f32>,
}
pub(super) fn play(
    commands: &mut Commands,
    bank: &mut Bank,
    config: &WorldConfig,
    assets: &AssetServer,
    at: Vec3,
    epoch: u64,
) {
    if bank.root != config.content_root {
        bank.clips = crate::sound::Bank::load(
            &config.content_root,
            "foley",
            "foley_keys_belt_metal_jingle",
        );
        bank.root = config.content_root.clone();
    }
    let pitch = 0.96 + 0.08 * crate::particles::random(&mut bank.seed);
    let Some(clip) = bank.clips.pick(bank.seed) else {
        return;
    };
    commands.spawn((
        Name::new("Source door keyring"),
        AudioPlayer::new(assets.load(clip.to_owned())),
        PlaybackSettings::DESPAWN
            .with_spatial(true)
            .with_spatial_scale(SpatialScale::new(0.5 / 13.))
            .with_volume(Volume::SILENT)
            .with_speed(pitch),
        Transform::from_translation(at),
        Voice {
            epoch,
            elapsed: 0.,
            duration: None,
        },
    ));
}
#[allow(clippy::too_many_arguments)]
pub(super) fn update(
    mut commands: Commands,
    time: Res<Time>,
    epoch: Res<WorldSessionEpoch>,
    ui: Res<UiState>,
    model: Res<UiModel>,
    settings: Res<Settings>,
    sources: Res<Assets<AudioSource>>,
    camera: Query<&Transform, With<PlayerCamera>>,
    mut voices: Query<(
        Entity,
        &mut Voice,
        &Transform,
        &AudioPlayer,
        &mut PlaybackSettings,
        Option<&mut SpatialAudioSink>,
    )>,
) {
    let paused = crate::player::simulation_paused(&ui);
    for (entity, mut voice, pose, audio, mut playback, sink) in &mut voices {
        if voice.epoch != epoch.0 || !model.connected {
            commands.entity(entity).try_despawn();
            continue;
        }
        if voice.duration.is_none() {
            voice.duration = sources
                .get(&audio.0)
                .and_then(|s| s.decoder().total_duration())
                .map(|d| d.as_secs_f32() / playback.speed + 0.1);
        }
        if !paused {
            voice.elapsed += time.delta_secs();
        }
        if voice.elapsed > voice.duration.unwrap_or(5.) {
            commands.entity(entity).try_despawn();
            continue;
        }
        let distance = camera
            .single()
            .map_or(12., |p| p.translation.distance(pose.translation));
        playback.volume = Volume::Linear(
            crate::sound::distance_gain(-7., 3., 12., distance) * settings.master_volume,
        );
        playback.paused = paused;
        if let Some(mut sink) = sink {
            sink.set_volume(playback.volume);
            if paused { sink.pause() } else { sink.play() }
        }
    }
}
