use super::{rules::*, *};
use crate::{player::PlayerCamera, settings::Settings};
use bevy::audio::{AudioSinkPlayback, Decodable, Source, SpatialScale, Volume};

#[derive(Resource)]
pub(super) struct Random(u64);
impl Default for Random {
    fn default() -> Self {
        Self(829173)
    }
}
#[derive(Component)]
pub(super) struct Voice {
    event: Footfall,
    elapsed: f32,
    duration: Option<f32>,
}
// Godot 4.6 AudioStreamPlayer3D::_get_attenuation_db/_update_panning:
// inverse distance, default +3 dB cap, then linear fade to MaxDistance.
// https://github.com/godotengine/godot/blob/4.6-stable/scene/3d/audio_stream_player_3d.cpp
pub(super) fn gain(db: f32, unit: f32, range: f32, distance: f32) -> f32 {
    (10_f32.powf(db / 20.) / (distance / unit + 0.00001)).min(10_f32.powf(3. / 20.))
        * (1. - distance / range).max(0.)
}
pub(super) fn play(
    mut commands: Commands,
    mut events: MessageReader<Footfall>,
    mut banks: ResMut<Banks>,
    mut random: ResMut<Random>,
    epoch: Res<WorldSessionEpoch>,
    model: Res<UiModel>,
    settings: Res<Settings>,
    assets: Res<AssetServer>,
    listener: Query<&Transform, With<PlayerCamera>>,
) {
    for event in events.read() {
        if event.epoch != epoch.0 || !model.connected {
            continue;
        }
        let pitch = 1. + (crate::particles::random(&mut random.0) * 2. - 1.) * event.pitch_jitter;
        let seed = random.0;
        let clip = match event.sound {
            Sound::Step(surface, run) => banks.step(surface, run, seed),
            Sound::Landing(surface) => banks.pick("landing", land_surface(surface), seed),
            Sound::Swim(bank) => banks.pick("swim", bank, seed),
        };
        let Some(clip) = clip else {
            continue;
        };
        let distance = listener
            .single()
            .ok()
            .map_or(event.range, |p| p.translation.distance(event.at));
        commands.spawn((
            Name::new(format!("Foot audio {clip}")),
            AudioPlayer::new(assets.load(clip)),
            PlaybackSettings::DESPAWN
                .with_spatial(true)
                // Keep Rodio's ears within its unity-distance zone throughout
                // our audible range: panning remains, inverse-square does not
                // multiply the source's manually applied inverse-distance gain.
                .with_spatial_scale(SpatialScale::new(0.5 / (event.range + 1.)))
                .with_volume(Volume::Linear(
                    gain(event.db, event.unit, event.range, distance) * settings.master_volume,
                ))
                .with_speed(pitch),
            Transform::from_translation(event.at),
            Voice {
                event: *event,
                elapsed: 0.,
                duration: None,
            },
        ));
    }
}
pub(super) fn update(
    mut commands: Commands,
    time: Res<Time>,
    epoch: Res<WorldSessionEpoch>,
    ui: Res<UiState>,
    model: Res<UiModel>,
    settings: Res<Settings>,
    sources: Res<Assets<AudioSource>>,
    listener: Query<&Transform, With<PlayerCamera>>,
    mut voices: Query<(
        Entity,
        &mut Voice,
        &Transform,
        &AudioPlayer,
        &mut PlaybackSettings,
        Option<&mut SpatialAudioSink>,
    )>,
) {
    let paused = simulation_paused(&ui);
    for (entity, mut voice, pose, audio, mut playback, sink) in &mut voices {
        if voice.event.epoch != epoch.0 || !model.connected {
            commands.entity(entity).try_despawn();
            continue;
        }
        if voice.duration.is_none() {
            voice.duration = sources
                .get(&audio.0)
                .and_then(|s| s.decoder().total_duration())
                .map(|duration| duration.as_secs_f32() / playback.speed + 0.1);
        }
        if !paused {
            voice.elapsed += time.delta_secs();
        }
        // Also retire in environments with no audio device; queued sources must
        // not accumulate forever when Bevy's playback systems are disabled.
        if voice.elapsed > voice.duration.unwrap_or(5.) {
            commands.entity(entity).try_despawn();
            continue;
        }
        let event = voice.event;
        let distance = listener
            .single()
            .ok()
            .map_or(event.range, |p| p.translation.distance(pose.translation));
        let volume = Volume::Linear(
            gain(event.db, event.unit, event.range, distance) * settings.master_volume,
        );
        playback.volume = volume;
        playback.paused = paused;
        if let Some(mut sink) = sink {
            sink.set_volume(volume);
            if paused {
                sink.pause();
            } else {
                sink.play();
            }
        }
    }
}
