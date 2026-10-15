use super::*;
use bevy::audio::{AudioSinkPlayback, Decodable, Source, SpatialScale, Volume};

#[derive(Component)]
pub(super) struct Voice {
    db: f32,
    unit: f32,
    range: f32,
    elapsed: f32,
    duration: Option<f32>,
}
#[allow(clippy::too_many_arguments)]
pub(super) fn spawn(
    commands: &mut Commands,
    assets: &AssetServer,
    path: &str,
    at: Vec3,
    parent: Option<Entity>,
    db: f32,
    unit: f32,
    range: f32,
    paused: bool,
) {
    let mut entity = commands.spawn((
        Name::new(format!("Mounted audio {path}")),
        Transient,
        Voice {
            db,
            unit,
            range,
            elapsed: 0.,
            duration: None,
        },
        AudioPlayer::new(assets.load(path.to_owned())),
        PlaybackSettings {
            paused,
            ..PlaybackSettings::DESPAWN
        }
        .with_spatial(true)
        .with_spatial_scale(SpatialScale::new(0.5 / (range + 1.)))
        .with_volume(Volume::SILENT),
        Transform::from_translation(at),
    ));
    if let Some(parent) = parent {
        entity.insert(ChildOf(parent));
    }
}
#[allow(clippy::too_many_arguments)]
pub(super) fn update(
    mut commands: Commands,
    time: Res<Time>,
    ui: Res<UiState>,
    settings: Res<Settings>,
    sources: Res<Assets<AudioSource>>,
    camera: Query<&GlobalTransform, With<PlayerCamera>>,
    mut voices: Query<(
        Entity,
        &mut Voice,
        &GlobalTransform,
        &AudioPlayer,
        &mut PlaybackSettings,
        Option<&mut SpatialAudioSink>,
    )>,
) {
    let paused = simulation_paused(&ui);
    for (entity, mut voice, pose, audio, mut playback, sink) in &mut voices {
        if voice.duration.is_none() {
            voice.duration = sources
                .get(&audio.0)
                .and_then(|s| s.decoder().total_duration())
                .map(|d| d.as_secs_f32() + 0.1);
        }
        if !paused {
            voice.elapsed += time.delta_secs();
        }
        if voice.elapsed > voice.duration.unwrap_or(5.) {
            commands.entity(entity).try_despawn();
            continue;
        }
        let distance = camera.single().map_or(voice.range, |p| {
            p.translation().distance(pose.translation())
        });
        playback.volume = Volume::Linear(
            crate::sound::distance_gain(voice.db, voice.unit, voice.range, distance)
                * settings.master_volume,
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
