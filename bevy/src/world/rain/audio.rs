//! RainAudio / RainMaterialAudio / ShelterProbe. Cosmetic playback only: these
//! sources never enter the authority's zombie-hearing path.
mod filter;
use super::{RainState, RainUpdate};
use crate::{
    player::{PlayerCamera, WorldSessionEpoch},
    settings::Settings,
    ui::UiState,
    vehicle::{Occupancy, VehicleBody},
    world::{WorldConfig, WorldFoliage, WorldObject, WorldResource},
};
use bevy::{
    audio::{AddAudioSource, AudioSinkPlayback, SpatialScale, Volume},
    prelude::*,
};
use bevy_rapier3d::prelude::*;
use filter::{FilteredClip, Pcm};
use std::sync::{
    Arc,
    atomic::{AtomicU32, Ordering},
};

const PATHS: [&str; 7] = [
    "rain_light.wav",
    "rain_heavy.wav",
    "audio/ambience/defaultrainambience.ogg",
    "rain_car.wav",
    "rain_foliage.wav",
    "rain_metal_roof.wav",
    "rain_tarp.wav",
];
#[derive(Component)]
struct RainVoice;
#[derive(Resource)]
struct RainAudio {
    sources: Vec<Handle<AudioSource>>,
    pcm: Vec<Option<Pcm>>,
    voices: [Option<Entity>; 7],
    levels: [f32; 7],
    cutoff: Arc<AtomicU32>,
    shelter: f32,
    cut_shelter: f32,
    target: f32,
    poll: f32,
    random: u64,
    epoch: u64,
}
impl Default for RainAudio {
    fn default() -> Self {
        Self {
            sources: vec![],
            pcm: vec![None; 7],
            voices: [None; 7],
            levels: [-80.; 7],
            cutoff: Arc::new(AtomicU32::new(20500_f32.to_bits())),
            shelter: 1.,
            cut_shelter: 1.,
            target: 1.,
            poll: 0.,
            random: 745921,
            epoch: u64::MAX,
        }
    }
}
#[derive(Clone, Copy)]
struct Target {
    position: Vec3,
    radius: f32,
}
#[derive(Resource)]
pub(super) struct MaterialTargets {
    targets: [Option<Target>; 4],
    pub canopy: Vec4,
    canopy_shelter: f32,
    poll: f32,
    epoch: u64,
}
impl Default for MaterialTargets {
    fn default() -> Self {
        Self {
            targets: [None; 4],
            canopy: Vec4::ZERO,
            canopy_shelter: 1.,
            poll: 0.,
            epoch: u64::MAX,
        }
    }
}
pub struct RainAudioPlugin;
impl Plugin for RainAudioPlugin {
    fn build(&self, app: &mut App) {
        app.add_audio_source::<FilteredClip>()
            .init_resource::<RainAudio>()
            .init_resource::<MaterialTargets>()
            .add_systems(Startup, setup)
            .add_systems(Update, (material_targets, update).chain().after(RainUpdate));
    }
}
fn setup(mut audio: ResMut<RainAudio>, assets: Res<AssetServer>, config: Res<WorldConfig>) {
    audio.sources = PATHS
        .iter()
        .map(|path| {
            if config.content_root.join(path).is_file() {
                assets.load((*path).to_owned())
            } else {
                Handle::default()
            }
        })
        .collect();
}
fn material_targets(
    mut targets: ResMut<MaterialTargets>,
    epoch: Res<WorldSessionEpoch>,
    rain: Res<RainState>,
    time: Res<Time>,
    ui: Res<UiState>,
    cameras: Query<&Transform, With<PlayerCamera>>,
    vehicles: Query<&GlobalTransform, With<VehicleBody>>,
    trees: Query<
        (&GlobalTransform, Option<&WorldResource>, &Name),
        (With<WorldFoliage>, With<Collider>),
    >,
    props: Query<(&GlobalTransform, &WorldObject)>,
) {
    if crate::player::simulation_paused(&ui) {
        return;
    }
    let Ok(camera) = cameras.single() else { return };
    let at = camera.translation;
    if targets.epoch != epoch.0 {
        *targets = MaterialTargets {
            epoch: epoch.0,
            ..default()
        };
    }
    if rain.intensity < 0.02 {
        *targets = MaterialTargets {
            epoch: epoch.0,
            ..default()
        };
        return;
    }
    targets.poll -= time.delta_secs();
    if targets.poll > 0. {
        return;
    }
    targets.poll = 0.25;
    targets.targets = [None; 4];
    targets.canopy = Vec4::ZERO;
    targets.canopy_shelter = 1.;
    let mut distances = [16. * 16., 28. * 28., 16. * 16., 16. * 16.];
    for transform in &vehicles {
        let position = transform.translation();
        let d = position.distance_squared(at);
        if d < distances[0] {
            distances[0] = d;
            targets.targets[0] = Some(Target {
                position,
                radius: 16.,
            });
        }
    }
    for (transform, resource, name) in &trees {
        let name = resource.map_or(name.as_str(), |r| r.name.as_str());
        if !["Pine", "Birch", "Maple", "Redwood"]
            .iter()
            .any(|part| name.contains(part))
        {
            continue;
        }
        let position = transform.translation();
        let d = position.distance_squared(at);
        if d < distances[1] {
            distances[1] = d;
            let pine = name.contains("Pine");
            let radius = if pine { 28. } else { 16. };
            targets.targets[1] = Some(Target { position, radius });
            let canopy = if pine { 6. } else { 4. };
            targets.canopy = Vec4::new(position.x, position.z, canopy, 1.);
            targets.canopy_shelter = (position.xz().distance(at.xz()) / canopy).clamp(0., 1.);
        }
    }
    for (transform, object) in &props {
        let Some(index) = material_kind(&object.asset_name) else {
            continue;
        };
        let position = transform.translation();
        let d = position.distance_squared(at);
        if d < distances[index] {
            distances[index] = d;
            targets.targets[index] = Some(Target {
                position,
                radius: 16.,
            });
        }
    }
}
fn material_kind(name: &str) -> Option<usize> {
    if name.starts_with("Tent_") {
        Some(3)
    } else if ["Container_", "Shed_", "Hangar_", "Silo_Grain_"]
        .iter()
        .any(|p| name.starts_with(p))
        || name == "Garage_Metal"
    {
        Some(2)
    } else {
        None
    }
}
fn levels(intensity: f32, shelter: f32) -> [f32; 3] {
    let r = intensity.clamp(0., 1.);
    let trim = -3. * (1. - shelter.clamp(0., 1.));
    let light = -28. + 22. * (r / 0.5).clamp(0., 1.) + trim;
    let heavy = ((r - 0.5) / 0.5).clamp(0., 1.);
    [
        if r > 0.02 { light } else { -80. },
        if heavy > 0.02 {
            -26. + 17. * heavy + trim
        } else {
            -80.
        },
        if r > 0.02 { light - 4. } else { -80. },
    ]
}
fn update(
    mut commands: Commands,
    mut audio: ResMut<RainAudio>,
    targets: Res<MaterialTargets>,
    rain: Res<RainState>,
    time: Res<Time>,
    ui: Res<UiState>,
    settings: Res<Settings>,
    epoch: Res<WorldSessionEpoch>,
    cameras: Query<&Transform, With<PlayerCamera>>,
    occupancy: Res<Occupancy>,
    vehicles: Query<&VehicleBody>,
    shelter: crate::sound::shelter::Shelter,
    sources: Res<Assets<AudioSource>>,
    mut clips: ResMut<Assets<FilteredClip>>,
    mut voices: Query<
        (
            &mut PlaybackSettings,
            Option<&mut AudioSink>,
            Option<&mut SpatialAudioSink>,
            &mut Transform,
        ),
        (With<RainVoice>, Without<PlayerCamera>),
    >,
) {
    let Ok(camera) = cameras.single() else { return };
    let at = camera.translation;
    let paused = crate::player::simulation_paused(&ui);
    let dt = if paused { 0. } else { time.delta_secs() };
    if audio.epoch != epoch.0 {
        for entity in audio.voices.iter().flatten() {
            commands.entity(*entity).despawn();
        }
        audio.voices = [None; 7];
        audio.levels = [-80.; 7];
        audio.epoch = epoch.0;
        audio.poll = 0.;
        audio.target = 1.;
        audio.shelter = 1.;
        audio.cut_shelter = 1.;
    }
    audio.poll -= dt;
    if !paused && audio.poll <= 0. {
        audio.poll = 0.15;
        let cabin = occupancy
            .seat
            .as_ref()
            .filter(|seat| seat.eye_pose.translation.distance(at) < 0.6)
            .and_then(|seat| vehicles.get(seat.vehicle).ok())
            .and_then(VehicleBody::cabin_openness)
            // The source probes the roof again after every cabin pane is gone.
            .filter(|openness| *openness < 1.);
        audio.target = cabin.unwrap_or_else(|| if shelter.covered(at) { 0. } else { 1. });
    }
    audio.shelter += (audio.target - audio.shelter).clamp(-dt / 0.35, dt / 0.35);
    let shelter = audio.shelter.min(0.7 + 0.3 * targets.canopy_shelter);
    audio.cut_shelter += (shelter - audio.cut_shelter).clamp(-dt / 0.07, dt / 0.07);
    let cutoff = (6000_f32.ln() + (20500_f32.ln() - 6000_f32.ln()) * audio.cut_shelter).exp();
    audio.cutoff.store(cutoff.to_bits(), Ordering::Relaxed);
    let bed = levels(rain.intensity, shelter);
    for index in 0..7 {
        let target = if index < 3 {
            None
        } else {
            targets.targets[index - 3]
        };
        let goal = if index < 3 {
            bed[index]
        } else if rain.intensity >= 0.02
            && target.is_some_and(|t| t.position.distance(at) < t.radius)
        {
            -14. + 12. * rain.intensity.clamp(0., 1.)
        } else {
            -80.
        };
        audio.levels[index] = if index < 3 {
            audio.levels[index] + (goal - audio.levels[index]).clamp(-180. * dt, 180. * dt)
        } else {
            goal
        };
        let volume =
            Volume::Linear(10_f32.powf(audio.levels[index] / 20.) * settings.master_volume);
        if audio.levels[index] <= -70. {
            if let Some(entity) = audio.voices[index].take() {
                commands.entity(entity).despawn();
            }
            continue;
        }
        if let Some(entity) = audio.voices[index] {
            if let Ok((mut playback, sink, spatial, mut transform)) = voices.get_mut(entity) {
                playback.volume = volume;
                playback.paused = paused;
                if let Some(target) = target {
                    transform.translation = target.position;
                    playback.spatial_scale = Some(SpatialScale::new(1. / (target.radius * 0.6)));
                }
                if let Some(mut sink) = sink {
                    sink.set_volume(volume);
                    if paused { sink.pause() } else { sink.play() }
                }
                if let Some(mut sink) = spatial {
                    sink.set_volume(volume);
                    if paused { sink.pause() } else { sink.play() }
                }
                continue;
            }
            audio.voices[index] = None;
        }
        if paused {
            continue;
        }
        if audio.pcm[index].is_none() {
            let Some(source) = audio.sources.get(index).and_then(|h| sources.get(h)) else {
                continue;
            };
            let pcm = Pcm::decode(source);
            if pcm.samples.is_empty() {
                continue;
            }
            audio.pcm[index] = Some(pcm);
        }
        let pcm = audio.pcm[index].as_ref().unwrap().clone();
        let frame_count = pcm.samples.len() / usize::from(pcm.channels.get());
        // RainAudio only randomizes WAV starts; its retail Ogg starts at zero.
        let start = if index == 2 {
            0
        } else {
            (crate::particles::random(&mut audio.random) * frame_count as f32) as usize
        };
        let clip = clips.add(FilteredClip {
            pcm,
            cutoff: audio.cutoff.clone(),
            start_frame: start,
        });
        let mut playback = PlaybackSettings::ONCE.with_volume(volume);
        let mut transform = Transform::IDENTITY;
        if let Some(target) = target {
            playback = playback
                .with_spatial(true)
                .with_spatial_scale(SpatialScale::new(1. / (target.radius * 0.6)));
            transform.translation = target.position;
        }
        let entity = commands
            .spawn((
                RainVoice,
                AudioPlayer::<FilteredClip>(clip),
                playback,
                transform,
            ))
            .id();
        audio.voices[index] = Some(entity);
    }
}
pub(super) fn shift_origin(world: &mut World, shift: Vec3) {
    if let Some(mut targets) = world.get_resource_mut::<MaterialTargets>() {
        for target in targets.targets.iter_mut().flatten() {
            target.position -= shift;
        }
        targets.canopy.x -= shift.x;
        targets.canopy.y -= shift.z;
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn real_thin_roof_muffles_loops_and_removal_pause_and_stop_reach_playback() {
        let mut app = App::new();
        app.add_plugins((
            MinimalPlugins,
            TransformPlugin,
            AssetPlugin::default(),
            RapierPhysicsPlugin::<NoUserData>::default().in_fixed_schedule(),
        ))
        .init_asset::<AudioSource>()
        .init_asset::<FilteredClip>()
        .init_resource::<RainAudio>()
        .init_resource::<MaterialTargets>()
        .init_resource::<WorldSessionEpoch>()
        .init_resource::<Settings>()
        .init_resource::<Occupancy>()
        .insert_resource(RainState {
            intensity: 1.,
            ..default()
        })
        .insert_resource(UiState {
            screen: crate::ui::Screen::Playing,
            ..default()
        })
        .insert_resource(Time::<Fixed>::from_hz(50.))
        .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
            std::time::Duration::from_millis(20),
        ))
        .add_systems(Update, (material_targets, update).chain());
        let pcm = Pcm {
            samples: Arc::from([0.3; 128]),
            channels: bevy::audio::ChannelCount::new(2).unwrap(),
            rate: bevy::audio::SampleRate::new(44100).unwrap(),
        };
        app.world_mut().resource_mut::<RainAudio>().pcm = vec![Some(pcm); 7];
        app.world_mut()
            .spawn((PlayerCamera, Transform::from_xyz(0., 1.6, 0.)));
        let ceiling = app
            .world_mut()
            .spawn((
                RigidBody::Fixed,
                Collider::cuboid(5., 0.1, 5.),
                Transform::from_xyz(0., 4., 0.),
            ))
            .id();
        app.world_mut().spawn((
            WorldObject {
                guid: "metal".into(),
                asset_name: "Shed_0".into(),
                source_index: 0,
            },
            Transform::from_xyz(4., 0., 0.),
        ));
        for _ in 0..40 {
            app.update();
        }
        let audio = app.world().resource::<RainAudio>();
        assert!(
            audio.shelter < 0.01,
            "thin ceilings must shelter the audio up-ray"
        );
        assert!((f32::from_bits(audio.cutoff.load(Ordering::Relaxed)) - 6000.).abs() < 2.);
        assert_eq!(audio.levels[..3], [-9., -12., -13.]);
        assert!(
            audio.voices[5].is_some(),
            "map metal roof emits separately from global rain"
        );
        let retail = audio.voices[2].unwrap();
        let handle = &app
            .world()
            .get::<AudioPlayer<FilteredClip>>(retail)
            .unwrap()
            .0;
        assert_eq!(
            app.world()
                .resource::<Assets<FilteredClip>>()
                .get(handle)
                .unwrap()
                .start_frame,
            0,
            "The original retail Ogg bed is not randomized like its WAV layers"
        );
        let light = audio.voices[0].unwrap();
        assert_eq!(
            app.world().get::<PlaybackSettings>(light).unwrap().volume,
            Volume::Linear(10_f32.powf(-9. / 20.))
        );
        app.world_mut().entity_mut(ceiling).insert(ColliderDisabled);
        for _ in 0..40 {
            app.update();
        }
        assert!(app.world().resource::<RainAudio>().shelter > 0.99);
        app.world_mut().resource_mut::<UiState>().screen = crate::ui::Screen::Paused;
        app.update();
        assert!(app.world().get::<PlaybackSettings>(light).unwrap().paused);
        app.world_mut().resource_mut::<UiState>().screen = crate::ui::Screen::Playing;
        app.world_mut().resource_mut::<RainState>().intensity = 0.;
        for _ in 0..30 {
            app.update();
        }
        assert!(
            app.world()
                .resource::<RainAudio>()
                .voices
                .iter()
                .all(Option::is_none)
        );
        assert_eq!(
            app.world_mut()
                .query_filtered::<Entity, With<RainVoice>>()
                .iter(app.world())
                .count(),
            0
        );
    }
    #[test]
    fn source_bed_layer_thresholds_and_material_registry() {
        assert_eq!(levels(0., 1.), [-80.; 3]);
        assert_eq!(levels(0.5, 1.), [-6., -80., -10.]);
        assert_eq!(levels(1., 0.), [-9., -12., -13.]);
        assert_eq!(material_kind("Shed_0"), Some(2));
        assert_eq!(material_kind("Garage_Metal"), Some(2));
        assert_eq!(material_kind("Tent_1"), Some(3));
        assert_eq!(material_kind("Wall_Metal"), None);
    }
    #[test]
    fn original_audio_files_decode_nonempty_and_loop_beyond_full_duration() {
        let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../game/content");
        for path in PATHS {
            let source = AudioSource {
                bytes: std::fs::read(root.join(path)).unwrap().into(),
            };
            let pcm = Pcm::decode(&source);
            assert!(!pcm.samples.is_empty(), "{path}");
            assert!(
                pcm.samples.iter().any(|v| v.abs() > 0.001),
                "{path} decoded silence"
            );
            let length = pcm.samples.len();
            let clip = FilteredClip {
                pcm,
                cutoff: Arc::new(AtomicU32::new(20500_f32.to_bits())),
                start_frame: 0,
            };
            assert!(
                clip.decoder().nth(length + 4).is_some(),
                "{path} must loop the whole clip"
            );
        }
    }
}
