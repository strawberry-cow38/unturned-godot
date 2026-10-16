//! `PlayPlaceSound`: the def's PlacementAudioClip as a positional one-shot at
//! the frozen point (AudioStreamPlayer3D UnitSize 6, no max distance). It is
//! the placer's own cue when the request leaves, never an accepted fact.
use super::*;
use bevy::audio::{SpatialScale, Volume};

#[derive(Resource, Default)]
pub(crate) struct Queue(pub(crate) Vec<(String, Vec3)>);

pub(super) fn update(
    mut commands: Commands,
    mut queue: ResMut<Queue>,
    config: Res<WorldConfig>,
    assets: Res<AssetServer>,
    settings: Option<Res<Settings>>,
    cameras: Query<&Transform, With<PlayerCamera>>,
    sources: Option<Res<Assets<AudioSource>>>,
) {
    if sources.is_none() {
        queue.0.clear();
        return;
    }
    for (stem, at) in queue.0.drain(..) {
        let path = format!("sounds/{stem}.wav");
        // LoadWavOneShot: a missing clip is silent (woodplacement is not ripped).
        if !config.content_root.join(&path).is_file() {
            continue;
        }
        let distance = cameras
            .single()
            .map_or(0., |c| c.translation.distance(at));
        let master = settings.as_ref().map_or(1., |s| s.master_volume);
        commands.spawn((
            Name::new("Source placement sound"),
            AudioPlayer::new(assets.load(path)),
            PlaybackSettings::DESPAWN
                .with_spatial(true)
                .with_spatial_scale(SpatialScale::new(0.5 / 13.))
                .with_volume(Volume::Linear(
                    crate::sound::distance_gain(0., 6., f32::INFINITY, distance) * master,
                )),
            Transform::from_translation(at),
        ));
    }
}
